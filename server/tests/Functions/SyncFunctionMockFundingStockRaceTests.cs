using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-18 on the mock payment path (dev, <c>TELEBIRR_MOCK=true</c>), for the buyer: a payment whose
/// funding finds the listing's stock already gone is funded, then ended with
/// <c>listing_out_of_stock</c> and its escrow put up for the full refund. The buyer's answer carries
/// the ended row at its version, so the app shows the end at once. An order within stock funds as before.
/// </summary>
[Collection(TelebirrMockEnvironmentCollection.Name)]
public sealed class SyncFunctionMockFundingStockRaceTests : IDisposable
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string OrderId = "order-001";
    private const string ListingId = "listing-001";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string? _telebirrMockBefore = Environment.GetEnvironmentVariable("TELEBIRR_MOCK");
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly List<string> _auditActions = new();
    private Dictionary<string, object> _stored;

    public SyncFunctionMockFundingStockRaceTests()
    {
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _stored = Order("created", 1);
        _orders.GetByIdAsync(OrderId, true).Returns(_ => new Dictionary<string, object>(_stored));
        _orders.GetByIdAsync(OrderId).Returns(_ => new Dictionary<string, object>(_stored));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>()).Returns(call =>
        {
            var next = new Dictionary<string, object>(_stored);
            foreach (var (key, value) in call.ArgAt<Dictionary<string, object>>(1))
            {
                next[key] = value is JsonElement json ? json.ToString() : value;
            }

            next["version"] = (call.ArgAt<int>(2) + 1).ToString();
            _stored = next;
            return AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>(next));
        });
        _orders.TryAdvanceStatusUnlessAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                if (_stored["status"].ToString() != call.ArgAt<string>(1))
                {
                    return null;
                }

                var next = new Dictionary<string, object>(_stored) { ["status"] = call.ArgAt<string>(2) };
                foreach (var (key, value) in call.ArgAt<Dictionary<string, object>?>(3) ?? new Dictionary<string, object>())
                {
                    next[key] = value;
                }

                next["version"] = (int.Parse(_stored["version"].ToString()!) + 1).ToString();
                _stored = next;
                return new Dictionary<string, object>(next);
            });
        _users.GetUserByIdAsync(BuyerId).Returns(Party(BuyerId, "buyer"));
        _users.GetUserByIdAsync(FarmerId).Returns(Party(FarmerId, "farmer"));
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>>
        {
            new()
            {
                { "escrowId", $"escrow-order-{OrderId}" }, { "createdAt", "2026-11-06T08:00:00.0000000Z" },
                { "orderId", OrderId }, { "status", "escrowed" }
            }
        });
        _txAudit.AppendAsync(Arg.Do<Dictionary<string, object>>(e => _auditActions.Add((string)e["action"])))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("TELEBIRR_MOCK", _telebirrMockBefore);

    [Fact]
    public async Task Pay_StockAlreadyGone_FundsThenEndsTheOrder_TheBuyerSeesTheEnd()
    {
        _crops.TryDecrementStockAsync(ListingId, OrderId, 0.5).Returns(StockDecrementResult.InsufficientStock);

        var result = await PayAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4); // created 1 -> submitted 2 -> funded 3 -> ended 4
        var data = result.GetProperty("serverData");
        data.GetProperty("status").GetString().Should().Be(OrderFundingService.OutOfStockStatus);
        data.GetProperty("cancelReason").GetString().Should().Be("listing_out_of_stock");
        data.GetProperty("faultParty").GetString().Should().Be("none");

        await _escrows.Received(1).UpdateStatusAsync($"escrow-order-{OrderId}", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null && (string)f["refundReason"] == "listing_out_of_stock"));
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        // The trail reads in order: paid, verified, then ended.
        _auditActions.Should().ContainInOrder("payment_verified", "order_cancelled");
    }

    [Fact]
    public async Task Pay_WithinStock_FundsAsBefore()
    {
        _crops.TryDecrementStockAsync(ListingId, OrderId, 0.5).Returns(StockDecrementResult.Decremented);

        var result = await PayAsync();

        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("funded");
        await _orders.DidNotReceive().TryAdvanceStatusUnlessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(),
            Arg.Any<string>(), Arg.Any<string>());
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        _auditActions.Should().Contain("payment_verified").And.NotContain("order_cancelled");
    }

    private static Dictionary<string, object> Order(string status, int version) => new()
    {
        { "orderId", OrderId }, { "listingId", ListingId },
        { "buyerId", BuyerId }, { "farmerId", FarmerId },
        { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
        { "cropType", "teff" }, { "quantity", 50.0 }, { "pricePerUnit", 45.5 },
        { "cropCost", 2275.0 }, { "commissionRate", 0.05 }, { "commissionAmount", 113.75 },
        { "transportCost", 0.0 }, { "orderTotal", 2388.75 },
        { "status", status }, { "version", version.ToString() },
        { "createdAt", DateTime.UtcNow.AddHours(-2).ToString("o") }
    };

    private static UserDto Party(string id, string role) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000000",
        Role = role,
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    private async Task<JsonElement> PayAsync()
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-18",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-pay",
                        IdempotencyKey = $"{BuyerId}:order:{OrderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = OrderId,
                        Action = "update",
                        Data = new Dictionary<string, object>
                        {
                            { "status", "payment_submitted" }, { "paymentMethod", "telebirr" }, { "version", 1 }
                        }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", BuyerId },
                    { "custom:role", "buyer" },
                    { "auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
                }
            }
        };

        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null);
        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _users, orderRepo: _orders, transactionAuditRepo: _txAudit, orderEscrowService: escrowService)
        {
            CommitmentsEnabled = false
        };
        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}

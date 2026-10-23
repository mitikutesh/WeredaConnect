using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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

/// <summary>Tests that set <c>TELEBIRR_MOCK</c>, a process-wide variable, run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelebirrMockEnvironmentCollection
{
    public const string Name = "TelebirrMockEnvironment";
}

/// <summary>
/// FUP-21: two updates of one order queued offline arrive in one /sync batch, each with the
/// version the client had before it. The second must be checked against the row the first one
/// wrote. The fake order table serves the row from before the batch to an eventually consistent
/// read (a replica that has not caught up) and the stored row to a strongly consistent one, so
/// any decision taken on an eventual read shows up as a wrong answer. Buyer, farmer and trucker
/// chains.
/// </summary>
[Collection(TelebirrMockEnvironmentCollection.Name)]
public sealed class SyncFunctionConsistentReadTests : IDisposable
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string ListingId = "listing-001";
    private const string TripId = "trip-001";
    private const string DeliveryId = "del-001";
    private const string TripsTable = "test-trips-table";
    private const string DeliveriesTable = "test-deliveries-table";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string? _telebirrMockBefore = Environment.GetEnvironmentVariable("TELEBIRR_MOCK");
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();

    /// <summary>The order as the table holds it after every write so far.</summary>
    private Dictionary<string, object> _stored = new();

    /// <summary>The order as it was before the batch: what a lagging replica still serves.</summary>
    private Dictionary<string, object> _beforeBatch = new();

    private readonly List<string> _writtenStatuses = new();

    public SyncFunctionConsistentReadTests()
    {
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        _orders.GetByIdAsync(OrderId).Returns(_ => new Dictionary<string, object>(_beforeBatch));
        _orders.GetByIdAsync(OrderId, true).Returns(_ => new Dictionary<string, object>(_stored));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(call => ApplyUpdate(call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<int>(2)));

        _users.GetUserByIdAsync(BuyerId).Returns(Party(BuyerId, "buyer"));
        _users.GetUserByIdAsync(FarmerId).Returns(Party(FarmerId, "farmer"));
    }

    public void Dispose() => Environment.SetEnvironmentVariable("TELEBIRR_MOCK", _telebirrMockBefore);

    // ===== Buyer =====

    [Fact]
    public async Task PayThenCancel_LivePayments_TheCancelSeesThePayment_AndIsRefused()
    {
        Store(Order("created", 1));

        var results = await SendBatchAsync(BuyerId, "buyer",
            Update("op-pay", Pay(version: 1)),
            Update("op-cancel", StatusChange("cancelled", version: 2)));

        results[0].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("status").GetString().Should().Be("error");
        results[1].GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: Invalid status transition from 'payment_submitted' to 'cancelled'.");
        _writtenStatuses.Should().Equal("payment_submitted");
        _stored["status"].ToString().Should().Be("payment_submitted");
        await OrderWasOnlyReadConsistentlyAsync();
    }

    [Fact]
    public async Task PayThenCancel_MockPayments_TheConflictEchoesTheFundedRow_NotTheOneFromBeforeTheBatch()
    {
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        Store(Order("created", 1));

        var results = await SendBatchAsync(BuyerId, "buyer",
            Update("op-pay", Pay(version: 1)),
            Update("op-cancel", StatusChange("cancelled", version: 2)));

        // The payment is auto-funded (version 3), so the cancel's version 2 is stale. The client
        // adopts the conflict row and retries from its version: it must be the funded one.
        results[0].GetProperty("status").GetString().Should().Be("success");
        results[0].GetProperty("serverVersion").GetInt32().Should().Be(3);
        results[1].GetProperty("status").GetString().Should().Be("conflict");
        results[1].GetProperty("serverVersion").GetInt32().Should().Be(3);
        results[1].GetProperty("serverData").GetProperty("status").GetString().Should().Be("funded");
        _writtenStatuses.Should().Equal("payment_submitted", "funded");
        await OrderWasOnlyReadConsistentlyAsync();
    }

    [Fact]
    public async Task CancelThenPay_ThePaymentSeesTheCancel_AndIsRefused()
    {
        Store(Order("created", 1));

        var results = await SendBatchAsync(BuyerId, "buyer",
            Update("op-cancel", StatusChange("cancelled", version: 1)),
            Update("op-pay", Pay(version: 2)));

        results[0].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("status").GetString().Should().Be("error");
        results[1].GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: Invalid status transition from 'cancelled' to 'payment_submitted'.");
        _writtenStatuses.Should().Equal("cancelled");
        await OrderWasOnlyReadConsistentlyAsync();
    }

    // ===== Farmer =====

    [Fact]
    public async Task AcceptThenReject_TheRejectSeesTheAccept_AndMovesNoMoneyOrStock()
    {
        Store(Order("funded", 3));

        var results = await SendBatchAsync(FarmerId, "farmer",
            Update("op-accept", StatusChange("accepted", version: 3)),
            Update("op-reject", StatusChange("rejected", version: 4)));

        results[0].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("status").GetString().Should().Be("error");
        results[1].GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: Invalid status transition from 'accepted' to 'rejected'.");
        _writtenStatuses.Should().Equal("accepted");

        // Read as funded, the reject would also have handed the stock back to the listing.
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await OrderWasOnlyReadConsistentlyAsync();
    }

    // ===== Trucker =====

    [Fact]
    public async Task PickupThenInTransit_TheSecondStepSeesTheFirst_AndIsApplied()
    {
        // A valid chain an eventual read would refuse (accepted -> in_transit is not a transition).
        Store(Order("accepted", 4, trucker: true));
        DeliveryIs("inTransit");

        var results = await SendBatchAsync(TruckerId, "trucker",
            Update("op-pickup", StatusChange("pickup_confirmed", version: 4)),
            Update("op-transit", StatusChange("in_transit", version: 5)));

        results[0].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("status").GetString().Should().Be("success");
        results[1].GetProperty("serverVersion").GetInt32().Should().Be(6);
        _writtenStatuses.Should().Equal("pickup_confirmed", "in_transit");
        await OrderWasOnlyReadConsistentlyAsync();
    }

    // ===== Helpers =====

    private async Task OrderWasOnlyReadConsistentlyAsync()
    {
        await _orders.Received().GetByIdAsync(OrderId, true);
        await _orders.DidNotReceive().GetByIdAsync(OrderId);
        await _orders.DidNotReceive().GetByIdAsync(OrderId, false);
    }

    /// <summary>The order table's version check, applied to <see cref="_stored"/>.</summary>
    private AuthResult<Dictionary<string, object>> ApplyUpdate(Dictionary<string, object> fields, int expectedVersion)
    {
        var current = int.Parse(_stored["version"].ToString()!);
        if (current != expectedVersion)
        {
            return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT",
                $"Version conflict: server has version {current}, client expected {expectedVersion}.");
        }

        // Client values arrive as JsonElement; the table hands back plain values, as here.
        var next = new Dictionary<string, object>(_stored);
        foreach (var (key, value) in fields)
        {
            next[key] = value is JsonElement json ? json.ToString() : value;
        }

        next["version"] = (expectedVersion + 1).ToString();
        _stored = next;
        if (fields.TryGetValue("status", out var status))
        {
            _writtenStatuses.Add(status.ToString()!);
        }

        return AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>(next));
    }

    private void Store(Dictionary<string, object> order)
    {
        _stored = order;
        _beforeBatch = new Dictionary<string, object>(order);
    }

    private void DeliveryIs(string status) =>
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "deliveryId", new AttributeValue { S = DeliveryId } },
                        { "orderId", new AttributeValue { S = OrderId } },
                        { "truckerId", new AttributeValue { S = TruckerId } },
                        { "status", new AttributeValue { S = status } }
                    }
                }
            });

    /// <summary>A self-pickup order (no trip to read on accept), or one with a trucker and an active delivery.</summary>
    private static Dictionary<string, object> Order(string status, int version, bool trucker = false)
    {
        var order = new Dictionary<string, object>
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
        if (trucker)
        {
            order["tripId"] = TripId;
            order["truckerId"] = TruckerId;
            order["activeDeliveryId"] = DeliveryId;
        }

        return order;
    }

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

    private static Dictionary<string, object> Pay(int version) => new()
    {
        { "status", "payment_submitted" }, { "paymentMethod", "telebirr" }, { "version", version }
    };

    private static Dictionary<string, object> StatusChange(string status, int version) => new()
    {
        { "status", status }, { "version", version }
    };

    private static SyncOperationRequest Update(string operationId, Dictionary<string, object> data) => new()
    {
        OperationId = operationId,
        IdempotencyKey = $"{operationId}:order:{OrderId}:update:12345",
        EntityType = "order",
        EntityId = OrderId,
        Action = "update",
        Data = data
    };

    /// <summary>One /sync request carrying every operation, signed in moments ago (payment step-up).</summary>
    private async Task<JsonElement[]> SendBatchAsync(string userId, string role, params SyncOperationRequest[] operations)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-21",
                ClientId = "device-001",
                Operations = operations.ToList()
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId },
                    { "custom:role", role },
                    { "auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
                }
            }
        };

        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            userRepo: _users, orderRepo: _orders);
        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")
            .EnumerateArray().Select(r => r.Clone()).ToArray();
    }
}

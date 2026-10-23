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

/// <summary>
/// FUP-09: a client <c>delivered</c> order update is honoured only when the order's active
/// delivery is already <c>delivered</c> (SIM-11). The gate reads that delivery strongly
/// consistent, so replication lag can neither refuse a catch-up that follows the delivery's
/// confirm nor pass one on a <c>delivered</c> the delivery has since left. The fake deliveries
/// table answers differently for the two read modes, so only the consistent read is right.
/// </summary>
public class SyncFunctionDeliveredGateTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string TripId = "trip-001";
    private const string DeliveryId = "del-001";
    private const string TripsTable = "test-trips-table";
    private const string DeliveriesTable = "test-deliveries-table";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    private Dictionary<string, object>? _written;

    public SyncFunctionDeliveredGateTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        // Both order reads see the same in_transit row, so these tests only exercise the
        // delivery read (FUP-21 makes the order read consistent separately).
        var order = Order();
        _orders.GetByIdAsync(OrderId).Returns(order);
        _orders.GetByIdAsync(Arg.Is(OrderId), Arg.Any<bool>()).Returns(order);
        _orders.UpdateAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>(order)
            {
                ["status"] = "delivered",
                ["version"] = (call.ArgAt<int>(2) + 1).ToString()
            }));

        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        _function = new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            userRepo: _users, orderRepo: _orders);
    }

    public static IEnumerable<object[]> Participants() => new[]
    {
        new object[] { BuyerId },
        new object[] { FarmerId },
        new object[] { TruckerId }
    };

    public static IEnumerable<object[]> ParticipantsAndUnconfirmedStatuses()
    {
        foreach (var caller in new[] { BuyerId, FarmerId, TruckerId })
        {
            foreach (var status in new[] { "pickedUp", "inTransit", "disputed" })
            {
                yield return new object[] { caller, status };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Participants))]
    public async Task Delivered_ConsistentReadSeesTheConfirm_IsHonoured_ThoughALaggingReadWouldNot(string caller)
    {
        // Right after the buyer's confirm a lagging read may still say inTransit.
        SetupDeliveryReads(eventualStatus: "inTransit", consistentStatus: "delivered");

        var result = await UpdateOrderAsync(caller);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotBeNull();
        _written!["status"].ToString().Should().Be("delivered");
        await AssertDeliveryReadOnlyConsistently();
    }

    [Theory]
    [MemberData(nameof(ParticipantsAndUnconfirmedStatuses))]
    public async Task Delivered_ConsistentReadSeesNoConfirm_IsRefused_ThoughALaggingReadSaysDelivered(
        string caller, string consistentStatus)
    {
        // The reverse: a lagging read says delivered, the delivery itself does not.
        SetupDeliveryReads(eventualStatus: "delivered", consistentStatus: consistentStatus);

        var result = await UpdateOrderAsync(caller);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(
            $"ORDER_VALIDATION: The delivery has not been confirmed yet (delivery status: {consistentStatus}); "
            + "confirm the delivery instead of marking the order delivered.");
        await NothingWritten();
        await AssertDeliveryReadOnlyConsistently();
    }

    [Fact]
    public async Task Delivered_ConsistentReadFindsNoDelivery_IsRefused()
    {
        // Fail closed: a lagging read with a delivered row does not stand in for a missing one.
        SetupDeliveryReads(eventualStatus: "delivered", consistentStatus: null);

        var result = await UpdateOrderAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith(
            "ORDER_VALIDATION: The delivery has not been confirmed yet (delivery status: unknown)");
        await NothingWritten();
        await AssertDeliveryReadOnlyConsistently();
    }

    // ===== helpers =====

    /// <summary>The deliveries table answers per read mode; null means no row.</summary>
    private void SetupDeliveryReads(string? eventualStatus, string? consistentStatus)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.ConsistentRead != true))
            .Returns(_ => DeliveryResponse(eventualStatus));
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.ConsistentRead == true))
            .Returns(_ => DeliveryResponse(consistentStatus));
    }

    private async Task AssertDeliveryReadOnlyConsistently()
    {
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(r =>
            r.TableName == DeliveriesTable
            && r.ConsistentRead == true
            && r.KeyConditionExpression == "deliveryId = :id"
            && r.ExpressionAttributeValues[":id"].S == DeliveryId));
        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r =>
            r.TableName == DeliveriesTable && r.ConsistentRead != true));
    }

    private async Task NothingWritten()
    {
        _written.Should().BeNull();
        await _orders.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    private async Task<JsonElement> UpdateOrderAsync(string userId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-09",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-fup-09",
                        IdempotencyKey = $"{userId}:order:{OrderId}:update:12345",
                        EntityType = "order",
                        EntityId = OrderId,
                        Action = "update",
                        Data = new Dictionary<string, object> { { "status", "delivered" }, { "version", 5 } }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await _function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static QueryResponse DeliveryResponse(string? status) => new()
    {
        Items = status == null
            ? new List<Dictionary<string, AttributeValue>>()
            : new()
            {
                new()
                {
                    { "deliveryId", new AttributeValue { S = DeliveryId } },
                    { "tripId", new AttributeValue { S = TripId } },
                    { "orderId", new AttributeValue { S = OrderId } },
                    { "farmerId", new AttributeValue { S = FarmerId } },
                    { "truckerId", new AttributeValue { S = TruckerId } },
                    { "buyerId", new AttributeValue { S = BuyerId } },
                    { "status", new AttributeValue { S = status } },
                    { "version", new AttributeValue { N = "3" } }
                }
            }
    };

    /// <summary>An in_transit trucker order whose active delivery is del-001.</summary>
    private static Dictionary<string, object> Order() => new()
    {
        { "orderId", OrderId }, { "listingId", "listing-001" },
        { "buyerId", BuyerId }, { "farmerId", FarmerId },
        { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
        { "cropType", "teff" }, { "quantity", 50.0 }, { "pricePerUnit", 45.5 },
        { "tripId", TripId }, { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" },
        { "transportCost", 60.0 }, { "cropCost", 2275.0 }, { "commissionRate", 0.05 },
        { "commissionAmount", 113.75 }, { "orderTotal", 2448.75 },
        { "activeDeliveryId", DeliveryId },
        { "status", "in_transit" }, { "version", "5" }
    };
}

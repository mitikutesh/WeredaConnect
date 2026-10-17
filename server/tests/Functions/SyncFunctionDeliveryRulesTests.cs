using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-08: who may cancel a delivery in which status (an update to <c>cancelled</c> and the
/// legacy delete action follow one rule), the trucker create bound to the order's trip and
/// listing, and the takeover of an order's delivery slot from a holder that can no longer
/// become a handoff.
/// </summary>
public class SyncFunctionDeliveryRulesTests
{
    private const string TruckerId = "00000000-0000-0000-0000-000000000001";
    private const string FarmerId = "00000000-0000-0000-0000-000000000002";
    private const string BuyerId = "00000000-0000-0000-0000-000000000003";
    private const string ListingId = "00000000-0000-0000-0000-000000000004";
    private const string TripId = "00000000-0000-0000-0000-000000000005";
    private const string OrderId = "00000000-0000-0000-0000-000000000006";
    private const string OutsiderId = "00000000-0000-0000-0000-000000000009";
    private const string DeliveryId = "00000000-0000-0000-0000-000000000010";
    private const string DeadDeliveryId = "00000000-0000-0000-0000-000000000011";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string TripsTable = "test-trips-table";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    public SyncFunctionDeliveryRulesTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(row => _auditRows.Add(row))).Returns(Task.CompletedTask);
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _function = new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            userRepo: _users, orderRepo: _orders, transactionAuditRepo: _audit);
    }

    // ===== Cancel: who, when =====

    public static IEnumerable<object[]> PartyCancels()
    {
        foreach (var action in new[] { "update", "delete" })
        {
            foreach (var caller in new[] { TruckerId, FarmerId, BuyerId })
            {
                yield return new object[] { action, caller };
            }
        }
    }

    public static IEnumerable<object[]> PartyCancelsAfterPickup()
    {
        foreach (var action in new[] { "update", "delete" })
        {
            foreach (var caller in new[] { TruckerId, FarmerId, BuyerId })
            {
                foreach (var status in new[] { "pickedUp", "inTransit", "delivered", "disputed" })
                {
                    yield return new object[] { action, caller, status };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PartyCancels))]
    public async Task Cancel_PendingByAnyParty_CancelsAuditsAndFreesTheSlot(string action, string caller)
    {
        SetupDelivery(Delivery("pending", version: 3));

        var result = await CancelAsync(action, caller, version: 3);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        NoPhones(result.GetProperty("serverData"));
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.TableName == DeliveriesTable
            && r.Key["tripId"].S == TripId
            && r.UpdateExpression == "SET #status = :cancelled, #updated = :updated, #v = :newVersion"
            && r.ConditionExpression == "#v = :expectedVersion AND #status = :pending"
            && r.ExpressionAttributeValues[":expectedVersion"].N == "3"
            && r.ExpressionAttributeValues[":newVersion"].N == "4"
            && !r.ExpressionAttributeNames.Values.Contains("deletedAt")));
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);

        var audit = _auditRows.Should().ContainSingle().Subject;
        audit["action"].Should().Be("delivery_cancelled");
        audit["transactionId"].Should().Be(OrderId);
        audit["entityId"].Should().Be(DeliveryId);
        audit["fromState"].Should().Be("pending");
        audit["toState"].Should().Be("cancelled");
        audit["actor"].Should().Be(caller);
        audit["metadata"].Should().Be($"via={action}");
    }

    [Theory]
    [MemberData(nameof(PartyCancelsAfterPickup))]
    public async Task Cancel_AfterPickupByAnyParty_IsRefused(string action, string caller, string status)
    {
        SetupDelivery(Delivery(status));

        var result = await CancelAsync(action, caller);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith(
            $"DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled only before pickup (status: {status}).");
        await NothingChanged();
    }

    [Theory]
    [InlineData("update", "pending")]
    [InlineData("update", "pickedUp")]
    [InlineData("update", "cancelled")]
    [InlineData("delete", "pending")]
    [InlineData("delete", "pickedUp")]
    [InlineData("delete", "cancelled")]
    public async Task Cancel_ByOutsider_IsRefusedWithoutData(string action, string status)
    {
        SetupDelivery(Delivery(status));

        var result = await CancelAsync(action, OutsiderId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("DELIVERY_UNAUTHORIZED: Only participants can cancel a delivery.");
        (!result.TryGetProperty("serverData", out var data) || data.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("a non-party must not see the delivery");
        await NothingChanged();
    }

    [Theory]
    [MemberData(nameof(PartyCancels))]
    public async Task Cancel_AlreadyCancelled_AnswersConflictWithTheRow(string action, string caller)
    {
        SetupDelivery(Delivery("cancelled", version: 5));

        var result = await CancelAsync(action, caller, version: 4);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("cancelled");
        NoPhones(result.GetProperty("serverData"));
        await NothingChanged();
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Cancel_LegacySoftDeletedRow_AnswersConflict(string action)
    {
        var row = Delivery("cancelled");
        row["deletedAt"] = new AttributeValue { S = "2026-10-01T10:00:00Z" };
        SetupDelivery(row);

        var result = await CancelAsync(action, BuyerId);

        result.GetProperty("status").GetString().Should().Be("conflict");
        await NothingChanged();
    }

    [Theory]
    [InlineData("update", BuyerId)]
    [InlineData("update", FarmerId)]
    [InlineData("delete", BuyerId)]
    [InlineData("delete", FarmerId)]
    public async Task Cancel_SelfPickupPendingByBuyerOrFarmer_Succeeds(string action, string caller)
    {
        SetupDelivery(SelfPickup("pending"));

        var result = await CancelAsync(action, caller);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Cancel_SelfPickupDelivered_IsRefused(string action)
    {
        SetupDelivery(SelfPickup("delivered"));

        var result = await CancelAsync(action, BuyerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_CANCEL_NOT_ALLOWED");
        await NothingChanged();
    }

    [Fact]
    public async Task Cancel_SelfPickupByATrucker_IsRefused()
    {
        // A self-pickup has no trucker, so any trucker is an outsider to it.
        SetupDelivery(SelfPickup("pending"));

        var result = await CancelAsync("delete", TruckerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_UNAUTHORIZED");
        await NothingChanged();
    }

    [Fact]
    public async Task Cancel_VersionConflict_AnswersConflictAndChangesNothingElse()
    {
        SetupDelivery(Delivery("pending", version: 2));
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("version moved"));

        var result = await CancelAsync("update", BuyerId, version: 1);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(2);
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        _auditRows.Should().BeEmpty();
    }

    // The write's condition failed because something landed after the read: the answer comes
    // from a strongly consistent re-read, never from the stale row.
    [Theory]
    [InlineData("pickedUp", "error", "DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled only before pickup (status: pickedUp).")]
    [InlineData("cancelled", "conflict", "DELIVERY_ALREADY_CANCELLED: The delivery is already cancelled.")]
    [InlineData("pending", "conflict", "Version conflict: server has version 3, client expected 1.")]
    public async Task Cancel_ConditionFails_IsJudgedOnAFreshRead(string freshStatus, string status, string message)
    {
        SetupDelivery(Delivery("pending", version: 1));
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable
                && r.ConsistentRead
                && r.ExpressionAttributeValues.ContainsKey(":id")
                && r.ExpressionAttributeValues[":id"].S == DeliveryId))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { Delivery(freshStatus, version: 3) }
            });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var result = await CancelAsync("update", FarmerId, version: 1);

        result.GetProperty("status").GetString().Should().Be(status);
        result.GetProperty("message").GetString().Should().StartWith(message);
        if (status == "conflict")
        {
            result.GetProperty("serverVersion").GetInt32().Should().Be(3);
            result.GetProperty("serverData").GetProperty("status").GetString().Should().Be(freshStatus);
            NoPhones(result.GetProperty("serverData"));
        }

        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_SlotReleaseFails_StillSucceedsAndLogs()
    {
        SetupDelivery(Delivery("pending"));
        _orders.ReleaseDeliverySlotAsync(OrderId, DeliveryId).ThrowsAsync(new InvalidOperationException("throttled"));

        var result = await CancelAsync("delete", TruckerId);

        result.GetProperty("status").GetString().Should().Be("success");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(
            $"DELIVERY_SLOT_RELEASE_FAILED orderId={OrderId} deliveryId={DeliveryId}");
    }

    [Fact]
    public async Task Cancel_UpdateWithOtherFields_IsRefused()
    {
        SetupDelivery(Delivery("pending"));

        var result = await SendAsync(Op("update", new Dictionary<string, object>
        {
            { "status", "cancelled" }, { "version", 1 }, { "disputeReason", "changed my mind" }
        }), BuyerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: A cancel may only send status and version, not: disputeReason");
        await NothingChanged();
    }

    [Fact]
    public async Task Cancel_UpdateWithEchoFields_Succeeds()
    {
        // The echo keys every client may send are dropped before the cancel rule looks.
        SetupDelivery(Delivery("pending"));

        var result = await SendAsync(Op("update", new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "cancelled" },
            { "updatedAt", "2026-10-09T08:00:00Z" }, { "version", 1 }
        }), FarmerId);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Update_OtherChangeOnACancelledDelivery_IsStillAlreadyCancelled()
    {
        SetupDelivery(Delivery("cancelled"));

        var result = await SendAsync(Op("update", new Dictionary<string, object>
        {
            { "status", "pickedUp" }, { "version", 1 }
        }), TruckerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_ALREADY_CANCELLED");
        await NothingChanged();
    }

    // ===== Create: the trucker delivery is bound to the order's trip and listing =====

    [Fact]
    public async Task Create_MatchingTripAndListing_Writes()
    {
        SetupCreate();

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(
            OrderId, DeliveriesTable,
            Arg.Is<Dictionary<string, AttributeValue>>(item =>
                item["deliveryId"].S == DeliveryId && item["tripId"].S == TripId && item["status"].S == "pending"
                && item["buyerPhone"].S == "+251900100000"),
            Arg.Is<DeliverySlotHolder?>(h => h == null));
        // ESC-48: the phones are stored for the admin view, never echoed to the creator.
        NoPhones(result.GetProperty("serverData"));
    }

    [Fact]
    public async Task Create_TripOfTheOrderIsCancelled_StillWrites()
    {
        // The trip status rule belongs to ESC-17 (bookings); refusing here would strand the order.
        SetupCreate(tripStatus: "cancelled");

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Create_OtherTrip_IsRefused()
    {
        SetupCreate();

        var result = await SendAsync(CreateOp(tripId: "00000000-0000-0000-0000-000000000099"), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_PARTICIPANT_MISMATCH: tripId does not match the order.");
        await NothingWritten();
    }

    [Fact]
    public async Task Create_OrderWithoutTrip_IsRefused()
    {
        SetupCreate(orderTripId: null);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_PARTICIPANT_MISMATCH: tripId does not match the order.");
        await NothingWritten();
    }

    [Fact]
    public async Task Create_OtherListing_IsRefused()
    {
        SetupCreate();

        var result = await SendAsync(CreateOp(listingId: "00000000-0000-0000-0000-000000000098"), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_PARTICIPANT_MISMATCH: listingId does not match the order.");
        await NothingWritten();
    }

    [Fact]
    public async Task Create_TripNotFound_IsRefused()
    {
        SetupCreate(tripExists: false);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be("DELIVERY_VALIDATION: Referenced trip not found.");
        await NothingWritten();
    }

    [Fact]
    public async Task Create_TripOfAnotherTrucker_IsRefused()
    {
        SetupCreate(tripTruckerId: OutsiderId);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_PARTICIPANT_MISMATCH: The trip belongs to another trucker.");
        await NothingWritten();
    }

    [Fact]
    public async Task Create_RowAlreadyWrittenByTheTransaction_AnswersConflictToTheParty()
    {
        // Two creates of one deliveryId in flight: the loser's transaction finds the row.
        SetupCreate();
        _orders.TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.DeliveryExists);
        SetupHolderRow(DeliveryId, Delivery("pending"));

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverData").GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
        NoPhones(result.GetProperty("serverData"));
    }

    [Fact]
    public async Task Create_OrderGoneBeforeTheTransaction_IsRefused()
    {
        SetupCreate();
        _orders.TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.OrderNotFound);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be("DELIVERY_VALIDATION: Referenced order not found.");
        _auditRows.Should().BeEmpty();
    }

    // ===== Create: the slot is taken over only from a dead holder =====

    [Fact]
    public async Task Create_SlotHeldByACancelledDelivery_TakesItOverFromThatClaim()
    {
        SetupCreate();
        SetupSlotHolder(DeadDeliveryId, "2026-10-09T08:00:00.0000000Z");
        SetupHolderRow(DeadDeliveryId, Delivery("cancelled"));
        SetupSlotWrites(replace: DeliverySlotWriteResult.Written);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
            Arg.Any<Dictionary<string, AttributeValue>>(),
            Arg.Is<DeliverySlotHolder?>(h => h != null
                && h.DeliveryId == DeadDeliveryId && h.ClaimedAt == "2026-10-09T08:00:00.0000000Z"));
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(
            $"DELIVERY_SLOT_RECLAIMED orderId={OrderId} from={DeadDeliveryId} to={DeliveryId}");
    }

    [Fact]
    public async Task Create_SlotHeldByAClaimWithoutRow_TakesItOver()
    {
        // A claim left by a create from before ESC-08 (claim and row were separate writes).
        SetupCreate();
        SetupSlotHolder(DeadDeliveryId, claimedAt: null);
        SetupSlotWrites(replace: DeliverySlotWriteResult.Written);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
            Arg.Any<Dictionary<string, AttributeValue>>(),
            Arg.Is<DeliverySlotHolder?>(h => h != null && h.DeliveryId == DeadDeliveryId && h.ClaimedAt == null));
    }

    [Fact]
    public async Task Create_SlotHeldByATimedClaimWithoutRow_IsRefused()
    {
        // Since ESC-08 a timed claim is always written with its row, so a missing row is never
        // read as a dead holder (only an untimed claim from before ESC-08 is).
        SetupCreate();
        SetupSlotHolder(DeadDeliveryId, "2026-10-09T08:00:00.0000000Z");
        SetupSlotWrites(replace: DeliverySlotWriteResult.Written);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: The order already has an active delivery.");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>());
    }

    [Fact]
    public async Task Create_OrderCancelledAfterItWasRead_IsRefusedByTheSlotWrite()
    {
        // ESC-06: the slot claim requires the order to still take a delivery, so a create racing a
        // buyer's transport-failure cancel never lands on the cancelled order.
        SetupCreate();
        _orders.TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.OrderNotActive);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: The order no longer takes a delivery (it must be accepted, pickup_confirmed or in_transit).");
        _auditRows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("pickedUp")]
    [InlineData("delivered")]
    public async Task Create_SlotHeldByALiveDelivery_IsRefusedWithoutTakeover(string holderStatus)
    {
        SetupCreate();
        SetupSlotHolder(DeadDeliveryId, "2026-10-09T08:00:00.0000000Z");
        SetupHolderRow(DeadDeliveryId, Delivery(holderStatus));
        SetupSlotWrites(replace: DeliverySlotWriteResult.Written);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: The order already has an active delivery.");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>());
    }

    [Fact]
    public async Task Create_TakeoverLosesTheRace_IsRefusedWithoutRetrying()
    {
        // The holder changed between the read and the takeover: refuse, never loop.
        SetupCreate();
        SetupSlotHolder(DeadDeliveryId, "2026-10-09T08:00:00.0000000Z");
        SetupHolderRow(DeadDeliveryId, Delivery("cancelled"));
        SetupSlotWrites(replace: DeliverySlotWriteResult.SlotTaken);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: The order already has an active delivery.");
        await _orders.Received(2).TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>());
    }

    [Fact]
    public async Task Create_SlotFreedSinceTheFailedClaim_IsRefusedWithoutTakeover()
    {
        // No holder on the consistent re-read: nothing to take over; the client retries.
        SetupCreate();
        _orders.GetByIdAsync(OrderId, true).Returns(Order());
        SetupSlotWrites(replace: DeliverySlotWriteResult.Written);

        var result = await SendAsync(CreateOp(), TruckerId);

        result.GetProperty("message").GetString().Should().Be(
            "DELIVERY_VALIDATION: The order already has an active delivery.");
        await _orders.Received(1).TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>());
    }

    // ===== Helpers =====

    private static Dictionary<string, AttributeValue> Delivery(string status, int version = 1) => new()
    {
        { "deliveryId", new AttributeValue { S = DeliveryId } },
        { "tripId", new AttributeValue { S = TripId } },
        { "orderId", new AttributeValue { S = OrderId } },
        { "listingId", new AttributeValue { S = ListingId } },
        { "farmerId", new AttributeValue { S = FarmerId } },
        { "farmerPhone", new AttributeValue { S = "+251911111111" } },
        { "truckerId", new AttributeValue { S = TruckerId } },
        { "truckerPhone", new AttributeValue { S = "+251922222222" } },
        { "buyerId", new AttributeValue { S = BuyerId } },
        { "buyerPhone", new AttributeValue { S = "+251933333333" } },
        { "status", new AttributeValue { S = status } },
        { "createdAt", new AttributeValue { S = "2026-10-09T08:00:00Z" } },
        { "version", new AttributeValue { N = version.ToString() } }
    };

    private static Dictionary<string, AttributeValue> SelfPickup(string status) => new()
    {
        { "deliveryId", new AttributeValue { S = DeliveryId } },
        { "tripId", new AttributeValue { S = DeliveryId } },
        { "orderId", new AttributeValue { S = OrderId } },
        { "listingId", new AttributeValue { S = ListingId } },
        { "farmerId", new AttributeValue { S = FarmerId } },
        { "buyerId", new AttributeValue { S = BuyerId } },
        { "deliveryType", new AttributeValue { S = "self_pickup" } },
        { "status", new AttributeValue { S = status } },
        { "createdAt", new AttributeValue { S = "2026-10-09T08:00:00Z" } },
        { "version", new AttributeValue { N = "1" } }
    };

    private void SetupDelivery(Dictionary<string, AttributeValue> row)
    {
        SetupHolderRow(DeliveryId, row);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>()).Returns(_ =>
        {
            var written = new Dictionary<string, AttributeValue>(row)
            {
                ["status"] = new AttributeValue { S = "cancelled" }
            };
            return new UpdateItemResponse { Attributes = written };
        });
    }

    /// <summary>The row <c>GetDeliveryItemAsync</c> reads for <paramref name="deliveryId"/>.</summary>
    private void SetupHolderRow(string deliveryId, Dictionary<string, AttributeValue> row)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable
                && r.ExpressionAttributeValues.ContainsKey(":id")
                && r.ExpressionAttributeValues[":id"].S == deliveryId))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { row } });
    }

    private static Dictionary<string, object> Order(string? tripId = TripId, string? claimedBy = null, string? claimedAt = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "truckerId", TruckerId }, { "listingId", ListingId },
            { "buyerName", "Mekonnen Haile" }, { "farmerName", "Abebe Kebede" }, { "truckerName", "Chaltu Tolosa" },
            { "status", "accepted" }, { "version", "1" }
        };
        if (tripId != null)
        {
            order["tripId"] = tripId;
        }

        if (claimedBy != null)
        {
            order["activeDeliveryId"] = claimedBy;
        }

        if (claimedAt != null)
        {
            order["activeDeliveryClaimedAt"] = claimedAt;
        }

        return order;
    }

    private void SetupCreate(
        string? orderTripId = TripId, bool tripExists = true, string tripTruckerId = TruckerId, string tripStatus = "open")
    {
        _orders.GetByIdAsync(OrderId).Returns(Order(orderTripId));
        // Any listing the farmer owns: the order binding, not the listing check, judges the id.
        _crops.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "cropId", ListingId }, { "farmerId", FarmerId }, { "status", "available" }
        });
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, "buyer", "Mekonnen Haile"));
        _users.GetUserByIdAsync(FarmerId).Returns(Profile(FarmerId, "farmer", "Abebe Kebede"));
        _users.GetUserByIdAsync(TruckerId).Returns(Profile(TruckerId, "trucker", "Chaltu Tolosa"));
        if (tripExists)
        {
            _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
                .Returns(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new()
                        {
                            { "tripId", new AttributeValue { S = TripId } },
                            { "truckerId", new AttributeValue { S = tripTruckerId } },
                            { "status", new AttributeValue { S = tripStatus } }
                        }
                    }
                });
        }

        _orders.TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.Written);
    }

    private void SetupSlotHolder(string holderId, string? claimedAt) =>
        _orders.GetByIdAsync(OrderId, true).Returns(Order(claimedBy: holderId, claimedAt: claimedAt));

    /// <summary>The plain claim finds the slot taken; a takeover answers <paramref name="replace"/>.</summary>
    private void SetupSlotWrites(DeliverySlotWriteResult replace)
    {
        _orders.TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Is<DeliverySlotHolder?>(h => h == null))
            .Returns(DeliverySlotWriteResult.SlotTaken);
        _orders.TryCreateDeliveryWithSlotAsync(OrderId, DeliveriesTable,
                Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Is<DeliverySlotHolder?>(h => h != null))
            .Returns(replace);
    }

    private static UserDto Profile(string userId, string role, string name) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900100000",
        DisplayName = name,
        Role = role,
        Woreda = "Jimma",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    private static SyncOperationRequest Op(string action, Dictionary<string, object>? data) => new()
    {
        OperationId = $"op-{action}",
        IdempotencyKey = $"user:delivery:{DeliveryId}:{action}:12345",
        EntityType = "delivery",
        EntityId = DeliveryId,
        Action = action,
        Data = data
    };

    private static SyncOperationRequest CreateOp(string tripId = TripId, string listingId = ListingId) =>
        Op("create", new Dictionary<string, object>
        {
            { "tripId", tripId }, { "orderId", OrderId }, { "listingId", listingId },
            { "farmerId", FarmerId }, { "farmerName", "Abebe" },
            { "truckerId", TruckerId }, { "truckerName", "Chaltu" },
            { "buyerId", BuyerId }, { "buyerName", "Mekonnen" },
            { "qrToken", "qr-token-abc123" }
        });

    /// <summary>A cancel as the shipped clients send it: update (web, mobile since ESC-08) or delete (older mobile).</summary>
    private Task<JsonElement> CancelAsync(string action, string caller, int version = 1) =>
        SendAsync(Op(action, action == "update"
            ? new Dictionary<string, object> { { "status", "cancelled" }, { "version", version } }
            : new Dictionary<string, object>
            {
                { "deliveryId", DeliveryId }, { "status", "cancelled" },
                { "updatedAt", "2026-10-09T08:00:00Z" }, { "version", version }
            }), caller);

    private async Task<JsonElement> SendAsync(SyncOperationRequest operation, string userId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-08",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
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

    /// <summary>ESC-48: a delivery row in /sync data carries no party's phone.</summary>
    private static void NoPhones(JsonElement serverData)
    {
        serverData.ValueKind.Should().Be(JsonValueKind.Object);
        foreach (var field in new[] { "farmerPhone", "truckerPhone", "buyerPhone" })
        {
            serverData.TryGetProperty(field, out _).Should().BeFalse(field);
        }
    }

    private async Task NothingChanged()
    {
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        _auditRows.Should().BeEmpty();
    }

    private Task<DeliverySlotWriteResult> NothingWritten() =>
        _orders.DidNotReceive().TryCreateDeliveryWithSlotAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>());
}

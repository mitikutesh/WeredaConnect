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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-06: the farmer's accept stamps <c>pickupDeadlineAt</c>, and the buyer may cancel an
/// accepted order nobody picked up by then, for a full refund (order-lifecycle.md 1.15.0): the
/// pending delivery first, then the order, then the slot, the stock and the refund schedule.
/// Trucker orders and self-pickup orders, for all three roles.
/// </summary>
public class SyncFunctionTransportCancelTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string TripId = "trip-001";
    private const string DeliveryId = "del-001";
    private const string ListingId = "listing-001";
    private const string EscrowId = "escrow-order-order-001";
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
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();

    private Dictionary<string, object> _order;
    private Dictionary<string, AttributeValue>? _trip;
    private Func<string?> _deliveryStatus = () => "pending";
    private Dictionary<string, object>? _escrow;
    private Dictionary<string, object>? _written;
    private List<string> _legacyDeliveryIds = new();

    public SyncFunctionTransportCancelTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _txAudit.AppendAsync(Arg.Do<Dictionary<string, object>>(row => _auditRows.Add(row))).Returns(Task.CompletedTask);

        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1));
        _trip = Trip(DateTime.UtcNow.AddDays(-4));
        _escrow = Escrow();

        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId, true).Returns(_ => _order);
        _orders.UpdateAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>(_order)
            {
                ["status"] = "cancelled",
                ["version"] = (call.ArgAt<int>(2) + 1).ToString()
            }));

        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(_ => new QueryResponse
            {
                Items = _trip == null ? new List<Dictionary<string, AttributeValue>>() : new() { _trip }
            });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(_ =>
            {
                var status = _deliveryStatus();
                return new QueryResponse
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
                                { "truckerId", new AttributeValue { S = TruckerId } },
                                { "buyerId", new AttributeValue { S = BuyerId } },
                                { "status", new AttributeValue { S = status } }
                            }
                        }
                };
            });

        // An order whose delivery predates the slot claim is looked up by its listing; none by default.
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.IndexName == "ListingIndex"))
            .Returns(_ => new QueryResponse
            {
                Items = _legacyDeliveryIds
                    .Select(id => new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = id } },
                        { "orderId", new AttributeValue { S = OrderId } },
                        { "listingId", new AttributeValue { S = ListingId } }
                    })
                    .ToList()
            });

        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(true);

        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>())
            .Returns(_ => _escrow == null ? new List<Dictionary<string, object>>() : new() { _escrow });
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => _escrow);
        _escrows.GetByIdAsync(EscrowId).Returns(_ => _escrow);
        _crops.TryRestoreStockAsync(ListingId, OrderId, Arg.Any<double>()).Returns(true);
    }

    private SyncFunction MakeFunction(bool withEscrowService = true)
    {
        var escrowService = withEscrowService
            ? new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                userRepo: _users, orderRepo: _orders, deliveryRepo: _deliveries)
            : null;
        return new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            userRepo: _users, orderRepo: _orders, escrowRepo: _escrows, transactionAuditRepo: _txAudit,
            orderEscrowService: escrowService, deliveryRepo: _deliveries);
    }

    // ===== The accept stamps the pickup deadline =====

    [Fact]
    public async Task Accept_TripOrder_StampsTheDeadline48HoursAfterTheDeparture()
    {
        var departure = DateTime.UtcNow.AddDays(3);
        _trip = Trip(departure);
        _order = Order(status: "funded", version: 2);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].ToString().Should().Be("accepted");
        DeadlineWritten().Should().BeCloseTo(departure.AddHours(48), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Accept_TripAlreadyDeparted_CountsFromTheAccept()
    {
        _trip = Trip(DateTime.UtcNow.AddDays(-2));
        _order = Order(status: "funded", version: 2);

        await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        DeadlineWritten().Should().BeCloseTo(DateTime.UtcNow.AddHours(48), TimeSpan.FromSeconds(5));
    }

    public static IEnumerable<object[]> SevenDayAccepts() => new[]
    {
        new object[] { "self_pickup" },
        new object[] { "missing_trip" },
        new object[] { "deleted_trip" }
    };

    [Theory]
    [MemberData(nameof(SevenDayAccepts))]
    public async Task Accept_SelfPickupOrGoneTrip_StampsSevenDaysAfterTheAccept(string shape)
    {
        _order = Order(status: "funded", version: 2, selfPickup: shape == "self_pickup");
        _trip = shape switch
        {
            "missing_trip" => null,
            "deleted_trip" => Trip(DateTime.UtcNow.AddDays(3), deletedAt: "2026-10-09T08:00:00Z"),
            _ => _trip
        };

        await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        DeadlineWritten().Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Accept_TripCannotBeRead_IsATransientRefusal_AndWritesNothing()
    {
        _order = Order(status: "funded", version: 2);
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_").And.Contain("try again");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ===== Buyer cancel after a transport failure =====

    [Fact]
    public async Task Cancel_TruckOrderPastItsDeadline_CancelsTheDeliveryFirst_ThenTheOrder_ThenSlotStockAndRefund()
    {
        var result = await CancelAsync(BuyerId, note: "  The truck never came.  ");

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        Received.InOrder(() =>
        {
            _deliveries.TryCancelPendingAsync(DeliveryId);
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
            _orders.ReleaseDeliverySlotAsync(OrderId, DeliveryId);
        });
        _written!["status"].Should().Be("cancelled");
        _written["cancelReason"].Should().Be("transport_failed_buyer_cancelled");
        _written["cancelNote"].Should().Be("The truck never came.");
        _written.Should().ContainKey("cancelledAt");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "order_transport_failed"));

        _auditRows.Select(r => (string)r["action"]).Should().Equal("delivery_cancelled", "order_cancelled");
        _auditRows.Should().AllSatisfy(r => r["actor"].Should().Be(BuyerId));
    }

    [Fact]
    public async Task Cancel_SelfPickupOrderWithNoDelivery_CancelsAndSchedulesTheRefund()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddMinutes(-1), selfPickup: true,
            activeDeliveryId: null);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        _written!["cancelReason"].Should().Be("transport_failed_buyer_cancelled");
        _written.Should().NotContainKey("cancelNote");
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => f.ContainsKey("refundReason")));
    }

    [Fact]
    public async Task Cancel_SelfPickupOrderWithItsPendingDelivery_CancelsBoth()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddMinutes(-1), selfPickup: true);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);
    }

    // ===== ESC-48: a cancel after contact had opened is a fraud signal for admin review =====

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();

    private SyncFunction MakeFunctionWithFraud()
        => new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            fraudService: _fraud, userRepo: _users, orderRepo: _orders, escrowRepo: _escrows,
            transactionAuditRepo: _txAudit,
            orderEscrowService: new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null,
                telebirrGateway: null, userRepo: _users, orderRepo: _orders, deliveryRepo: _deliveries),
            deliveryRepo: _deliveries);

    [Fact]
    public async Task Cancel_AfterContactOpened_RecordsAFraudSignalOnTheBuyer()
    {
        var result = await CancelAsync(BuyerId, function: MakeFunctionWithFraud());

        result.GetProperty("status").GetString().Should().Be("success");
        await _fraud.Received(1).RecordFraudSignalAsync(
            BuyerId, "CONTACT_THEN_TRANSPORT_CANCEL", OrderId, "order",
            Arg.Is<string?>(d => d != null && d.Contains(FarmerId) && !d.Contains("+251")));
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public async Task Cancel_OfAnOrderWhoseContactWasClosed_RecordsNoSignal(string flag)
    {
        _order[flag] = "2026-10-10T10:00:00Z";

        var result = await CancelAsync(BuyerId, function: MakeFunctionWithFraud());

        result.GetProperty("status").GetString().Should().Be("success");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Cancel_Refused_RecordsNoSignal()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(5));

        var result = await CancelAsync(BuyerId, function: MakeFunctionWithFraud());

        result.GetProperty("status").GetString().Should().Be("error");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Cancel_SignalWriteFails_TheCancelStillSucceeds()
    {
        _fraud.RecordFraudSignalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var result = await CancelAsync(BuyerId, function: MakeFunctionWithFraud());

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be("cancelled");
    }

    [Fact]
    public async Task Cancel_BeforeTheDeadline_IsRefused_AndNothingIsWritten()
    {
        var deadline = DateTime.UtcNow.AddHours(5);
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: deadline);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("ORDER_CANCEL_NOT_ALLOWED: The crops can still be collected until");
        await NothingWritten();
    }

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task Cancel_DeliveryAlreadyPickedUp_IsRefused(string deliveryStatus)
    {
        _deliveryStatus = () => deliveryStatus;

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_PickupLandsBetweenTheReadAndTheDeliveryCancel_IsRefused_AndTheOrderStays()
    {
        var reads = new Queue<string>(new[] { "pending", "pickedUp" });
        _deliveryStatus = () => reads.Count > 0 ? reads.Dequeue() : "pickedUp";
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(false);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Cancel_StaleClientVersion_AnswersConflict_BeforeAnyWrite()
    {
        var result = await CancelAsync(BuyerId, version: 3);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        await NothingWritten();
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("locked")]
    [InlineData("manual_payout")]
    [InlineData("delivery_confirmed")]
    [InlineData("refunded")]
    public async Task Cancel_EscrowOwnedBySomethingElse_IsRefused(string shape)
    {
        _escrow = shape switch
        {
            "disputed" => Escrow(status: "disputed"),
            "refunded" => Escrow(status: "refunded"),
            "locked" => Escrow(extra: ("settlementLock", "refund")),
            "manual_payout" => Escrow(extra: ("manualPayout", "open")),
            _ => Escrow(extra: ("deliveryConfirmedAt", "2026-10-09T08:00:00Z"))
        };

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_FundedOrderWithoutAnEscrowRecord_CreatesItFirst_ThenSchedulesTheRefund()
    {
        // ORDER_ESCROW_CREATE_FAILED at funding (live since ESC-44): the held money has no record.
        var created = Escrow();
        var records = new Queue<Dictionary<string, object>?>(new[] { null, created });
        _escrow = null;
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => records.Count > 0 ? records.Dequeue() : created);
        _escrows.GetByIdAsync(EscrowId).Returns(null, created);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _escrows.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["escrowId"] == EscrowId && (string)e["status"] == "escrowed"));
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "order_transport_failed"));
    }

    [Fact]
    public async Task Cancel_EscrowRecordCannotBeCreated_IsTransient_AndNothingIsWritten()
    {
        _escrow = null;
        _order["orderTotal"] = 0.0; // the self-heal has no amount to record
        _order["quantity"] = 0.0;

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_WithoutTheEscrowService_IsTransient_AndNothingIsWritten()
    {
        var result = await CancelAsync(BuyerId, function: MakeFunction(withEscrowService: false));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_DeliveryCannotBeRead_IsTransient_AndNothingIsWritten()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("The order's delivery could not be read. Please try again later.");
        await NothingWritten();
    }

    [Theory]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public async Task Cancel_ByTheFarmerOrTheTrucker_IsUnauthorized(string caller)
    {
        var result = await CancelAsync(caller);

        result.GetProperty("message").GetString().Should().StartWith("ORDER_UNAUTHORIZED");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_LegacyOrderWithoutTheStamp_UsesTheLiveTripAndTheAcceptTime()
    {
        _order = Order(status: "accepted", version: 4, respondedAt: DateTime.UtcNow.AddDays(-6));
        _trip = Trip(DateTime.UtcNow.AddDays(-3));

        (await CancelAsync(BuyerId)).GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Cancel_LegacyOrderWhoseTripStillHasTime_IsRefused()
    {
        _order = Order(status: "accepted", version: 4, respondedAt: DateTime.UtcNow.AddDays(-6));
        _trip = Trip(DateTime.UtcNow.AddDays(-1));

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().StartWith("ORDER_CANCEL_NOT_ALLOWED");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_OrderCasConflictsAfterTheDeliveryCancel_RereadsAndRetries()
    {
        var calls = 0;
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(call =>
            {
                calls++;
                if (calls == 1)
                {
                    // A system write moved the version after the delivery was cancelled.
                    _order = new Dictionary<string, object>(_order) { ["version"] = "5" };
                    return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict.");
                }

                return AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>(_order)
                {
                    ["status"] = "cancelled",
                    ["version"] = "6"
                });
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(6);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5);
    }

    [Fact]
    public async Task Cancel_OrderMovedOnAfterTheDeliveryCancel_AnswersConflictWithTheServerRow()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(_ =>
            {
                _order = new Dictionary<string, object>(_order) { ["status"] = "disputed", ["version"] = "5" };
                return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict.");
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("conflict");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Cancel_OrderCasKeepsConflicting_IsTransient()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("The order could not be cancelled right now. Please try again later.");
        await _orders.Received(3).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Cancel_PendingDeliveryCreatedInTheGap_IsCancelledToo()
    {
        var slotReads = 0;
        _orders.GetByIdAsync(OrderId, true).Returns(_ => ++slotReads <= 1
            ? _order
            : new Dictionary<string, object>(_order) { ["status"] = "cancelled", ["activeDeliveryId"] = "del-late" });
        _deliveries.TryCancelPendingAsync("del-late").Returns(true);

        (await CancelAsync(BuyerId)).GetProperty("status").GetString().Should().Be("success");

        await _deliveries.Received(1).TryCancelPendingAsync("del-late");
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, "del-late");
        Logs.Should().NotContain("ORDER_TRANSPORT_CANCEL_LATE_DELIVERY");
    }

    [Fact]
    public async Task Cancel_DeliveryPickedUpInTheGap_IsAlarmed_WithDefangedIds()
    {
        const string hostileId = "del_ORDER_TRANSPORT_CANCEL_LATE_DELIVERY";
        var slotReads = 0;
        _orders.GetByIdAsync(OrderId, true).Returns(_ => ++slotReads <= 1
            ? _order
            : new Dictionary<string, object>(_order) { ["status"] = "cancelled", ["activeDeliveryId"] = hostileId });
        _deliveries.TryCancelPendingAsync(hostileId).Returns(false);
        var deliveryReads = 0;
        _deliveryStatus = () => ++deliveryReads <= 1 ? "pending" : "pickedUp";

        (await CancelAsync(BuyerId)).GetProperty("status").GetString().Should().Be("success");

        var lines = Logs.Split('\n').Where(l => l.Contains("ORDER_TRANSPORT_CANCEL_LATE_DELIVERY")).ToList();
        lines.Should().ContainSingle();
        lines[0].Should().Contain("deliveryId=del-ORDER-TRANSPORT-CANCEL-LATE-DELIVERY").And.Contain("status=pickedUp");
        System.Text.RegularExpressions.Regex.Matches(lines[0], "ORDER_TRANSPORT_CANCEL_LATE_DELIVERY").Should().HaveCount(1);
    }

    // ===== Review fixes =====

    [Fact]
    public async Task Cancel_DeliveryThatTookTheSlotDuringTheRetryWasPickedUp_IsRefused()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(_ =>
            {
                // A new delivery claimed the slot and was picked up between the delivery cancel and the order write.
                _order = new Dictionary<string, object>(_order) { ["version"] = "5", ["activeDeliveryId"] = "del-new" };
                _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.IndexName == null
                        && r.ExpressionAttributeValues.ContainsKey(":id") && r.ExpressionAttributeValues[":id"].S == "del-new"))
                    .Returns(new QueryResponse
                    {
                        Items = new() { new() { { "deliveryId", new AttributeValue { S = "del-new" } }, { "status", new AttributeValue { S = "pickedUp" } } } }
                    });
                return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict.");
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Cancel_PendingDeliveryThatTookTheSlotDuringTheRetry_IsCancelledWithTheOrder()
    {
        var calls = 0;
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(_ =>
            {
                if (++calls == 1)
                {
                    _order = new Dictionary<string, object>(_order) { ["version"] = "5", ["activeDeliveryId"] = "del-new" };
                    return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict.");
                }

                _order = new Dictionary<string, object>(_order) { ["status"] = "cancelled", ["version"] = "6" };
                return AuthResult<Dictionary<string, object>>.Success(_order);
            });
        _deliveries.TryCancelPendingAsync("del-new").Returns(true);

        (await CancelAsync(BuyerId)).GetProperty("status").GetString().Should().Be("success");

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5);
        await _deliveries.Received(1).TryCancelPendingAsync("del-new");
    }

    [Theory]
    [InlineData("transport_failed_buyer_cancelled")]
    [InlineData("  Escrow_Refunded ")]
    [InlineData("buyer_withdrew")]
    public async Task FundedCancel_ClientTextSpellingAServerCode_IsKeptAsANote(string text)
    {
        _order = Order(status: "funded", version: 4);

        var result = await CancelAsync(BuyerId, note: text);

        result.GetProperty("status").GetString().Should().Be("success");
        _written!.Should().NotContainKey("cancelReason");
        _written!["cancelNote"].Should().Be(text.Trim());
    }

    [Fact]
    public async Task FundedCancel_FreeText_StaysTheCancelReason()
    {
        _order = Order(status: "funded", version: 4);

        await CancelAsync(BuyerId, note: "changed my mind");

        _written!["cancelReason"].ToString().Should().Be("changed my mind");
        _written.Should().NotContainKey("cancelNote");
    }

    [Fact]
    public async Task Cancel_OrderWithoutASlotClaim_LooksUpItsDeliveriesByListing_AndRefusesAPickedUpOne()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1), activeDeliveryId: null);
        _legacyDeliveryIds = new List<string> { DeliveryId };
        _deliveryStatus = () => "inTransit";

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_OrderWithoutASlotClaim_CancelsItsPendingLegacyDelivery()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1), activeDeliveryId: null);
        _legacyDeliveryIds = new List<string> { DeliveryId };

        (await CancelAsync(BuyerId)).GetProperty("status").GetString().Should().Be("success");

        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _dynamoDb.Received().QueryAsync(Arg.Is<QueryRequest>(r =>
            r.IndexName == "ListingIndex" && r.ExpressionAttributeValues[":listingId"].S == ListingId
            && r.ExpressionAttributeValues[":orderId"].S == OrderId && r.FilterExpression == "orderId = :orderId"));
    }

    [Fact]
    public async Task Cancel_OrderWithoutASlotClaim_FindsItsDeliveryOnALaterPage()
    {
        // The listing's first page holds only other orders' deliveries (filtered out); the order's own
        // delivery, picked up, is on page 2 and must still refuse the cancel.
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1), activeDeliveryId: null);
        _deliveryStatus = () => "pickedUp";
        var pages = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.IndexName == "ListingIndex"))
            .Returns(call =>
            {
                var request = call.Arg<QueryRequest>();
                pages.Add(request);
                return request.ExclusiveStartKey == null || request.ExclusiveStartKey.Count == 0
                    ? new QueryResponse
                    {
                        Items = new List<Dictionary<string, AttributeValue>>(),
                        LastEvaluatedKey = new Dictionary<string, AttributeValue> { { "deliveryId", new AttributeValue { S = "page-1-end" } } }
                    }
                    : new QueryResponse
                    {
                        Items = new() { new() { { "deliveryId", new AttributeValue { S = DeliveryId } }, { "orderId", new AttributeValue { S = OrderId } } } },
                        LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                    };
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        pages.Should().HaveCount(2);
        pages[1].ExclusiveStartKey["deliveryId"].S.Should().Be("page-1-end");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_OrderWithoutASlotClaim_LookupFails_IsTransient_AndNothingIsWritten()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1), activeDeliveryId: null);
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.IndexName == "ListingIndex"))
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("The order's delivery could not be read. Please try again later.");
        await NothingWritten();
    }

    [Fact]
    public async Task Cancel_OrderWithoutASlotClaim_ListingTooLargeToRead_IsTransient()
    {
        _order = Order(status: "accepted", version: 4, pickupDeadlineAt: DateTime.UtcNow.AddHours(-1), activeDeliveryId: null);
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.IndexName == "ListingIndex"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { { "deliveryId", new AttributeValue { S = "more" } } }
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_");
        await NothingWritten();
    }

    [Theory]
    [InlineData(TruckerId, "pickedUp", "transport_failed_buyer_cancelled", true)]
    [InlineData(TruckerId, "inTransit", "transport_failed_buyer_cancelled", true)]
    [InlineData(BuyerId, "delivered", "transport_failed_buyer_cancelled", true)]
    [InlineData(TruckerId, "pickedUp", "changed my mind", false)]
    [InlineData(TruckerId, "pending", "transport_failed_buyer_cancelled", false)]
    // Only the party who could have made the step pages the alarm.
    [InlineData("stranger-999", "pickedUp", "transport_failed_buyer_cancelled", false)]
    [InlineData(BuyerId, "pickedUp", "transport_failed_buyer_cancelled", false)]
    [InlineData(TruckerId, "delivered", "transport_failed_buyer_cancelled", false)]
    [InlineData(FarmerId, "delivered", "transport_failed_buyer_cancelled", false)]
    public async Task PickupOnADeliveryTheTransportCancelCancelled_IsAlarmed(
        string caller, string requested, string cancelReason, bool alarmed)
    {
        _deliveryStatus = () => "cancelled";
        _order = new Dictionary<string, object>(Order(status: "cancelled", version: 5)) { ["cancelReason"] = cancelReason };

        var result = await SendDeliveryUpdateAsync(caller, new Dictionary<string, object> { { "status", requested }, { "version", 2 } });

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_");
        var lines = Logs.Split('\n').Where(l => l.Contains("ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED")).ToList();
        if (alarmed)
        {
            lines.Should().ContainSingle();
            lines[0].Should().Contain("orderId=order-001").And.Contain($"status={requested}");
        }
        else
        {
            lines.Should().BeEmpty();
        }
    }

    // ===== helpers =====

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private DateTime DeadlineWritten()
    {
        _written.Should().NotBeNull();
        return DateTime.Parse((string)_written!["pickupDeadlineAt"], null,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    private async Task NothingWritten()
    {
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrows.DidNotReceive().CreateAsync(Arg.Any<Dictionary<string, object>>());
    }

    private Task<JsonElement> CancelAsync(string caller, int version = 4, string? note = null, SyncFunction? function = null)
    {
        var data = new Dictionary<string, object> { { "status", "cancelled" }, { "version", version } };
        if (note != null)
        {
            data["cancelReason"] = note;
        }

        return SendAsync(caller, data, function);
    }

    private async Task<JsonElement> SendDeliveryUpdateAsync(string userId, Dictionary<string, object> data)
        => await SendOperationAsync(userId, new SyncOperationRequest
        {
            OperationId = "op-esc-06-delivery",
            IdempotencyKey = $"{userId}:delivery:{DeliveryId}:update:12345",
            EntityType = "delivery",
            EntityId = DeliveryId,
            Action = "update",
            Data = data
        }, null);

    private Task<JsonElement> SendAsync(string userId, Dictionary<string, object> data, SyncFunction? function = null)
        => SendOperationAsync(userId, new SyncOperationRequest
        {
            OperationId = "op-esc-06",
            IdempotencyKey = $"{userId}:order:{OrderId}:update:12345",
            EntityType = "order",
            EntityId = OrderId,
            Action = "update",
            Data = data
        }, function);

    private async Task<JsonElement> SendOperationAsync(string userId, SyncOperationRequest operation, SyncFunction? function)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-06",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await (function ?? MakeFunction()).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static Dictionary<string, object> Order(
        string status, int version, DateTime? pickupDeadlineAt = null, bool selfPickup = false,
        string? activeDeliveryId = DeliveryId, DateTime? respondedAt = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "listingId", ListingId },
            { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 500.0 }, { "pricePerUnit", 45.5 },
            { "cropCost", 22750.0 }, { "commissionRate", 0.05 }, { "commissionAmount", 1137.5 },
            { "orderTotal", 24187.5 },
            { "status", status }, { "version", version.ToString() },
            { "createdAt", DateTime.UtcNow.AddDays(-10).ToString("o") },
            { "paymentVerifiedAt", DateTime.UtcNow.AddDays(-9).ToString("o") }
        };
        if (!selfPickup)
        {
            order["tripId"] = TripId;
            order["truckerId"] = TruckerId;
            order["truckerName"] = "Tadesse Worku";
            order["transportCost"] = 300.0;
        }

        if (pickupDeadlineAt != null)
        {
            order["pickupDeadlineAt"] = pickupDeadlineAt.Value.ToString("o");
        }

        if (activeDeliveryId != null && status == "accepted")
        {
            order["activeDeliveryId"] = activeDeliveryId;
        }

        if (respondedAt != null)
        {
            order["respondedAt"] = respondedAt.Value.ToString("o");
        }

        return order;
    }

    private static Dictionary<string, AttributeValue> Trip(DateTime departure, string? deletedAt = null)
    {
        var trip = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = TripId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "status", new AttributeValue { S = "open" } },
            { "departureDate", new AttributeValue { S = departure.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") } },
            { "createdAt", new AttributeValue { S = "2026-10-01T08:00:00.000Z" } }
        };
        if (deletedAt != null)
        {
            trip["deletedAt"] = new AttributeValue { S = deletedAt };
        }

        return trip;
    }

    private static Dictionary<string, object> Escrow(string status = "escrowed", (string Key, string Value)? extra = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", EscrowId }, { "orderId", OrderId }, { "source", "order" },
            { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "status", status }, { "amount", 24187.5m },
            { "createdAt", "2026-10-01T08:00:00.0000000Z" },
            { "expiresAt", "2026-10-08T08:00:00.0000000Z" }
        };
        if (extra != null)
        {
            escrow[extra.Value.Key] = extra.Value.Value;
        }

        return escrow;
    }
}

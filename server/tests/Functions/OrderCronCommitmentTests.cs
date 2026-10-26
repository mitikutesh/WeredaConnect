using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-15: the order cron's <c>commitment_deadlines</c> walk. Each walk applies its transition as
/// a version CAS and audits it; markers are stamped once; nothing is written with the flag off,
/// with a dependency missing, or for a row whose window is still open.
/// </summary>
public class OrderCronCommitmentTests
{
    private const string OrderId = "ord-1";
    private const string TripId = "trip-1";
    private const string ListingId = "listing-1";
    private const string DeliveryId = "del-1";
    private const string EscrowId = "escrow-order-ord-1";

    /// <summary>The order's 500 kg as quintals.</summary>
    private const double Quintals = 5.0;

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public OrderCronCommitmentTests()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _orders.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "orderId", ci.Arg<string>() } }));
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(true);
        _trips.GetByIdAsync(TripId).Returns(OpenTrip());
        _trips.TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _crops.TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _deliveries.TryCancelPendingAsync(Arg.Any<string>()).Returns(true);
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(ci => new List<Dictionary<string, object>> { EscrowedRow(ci.Arg<string>()) });
    }

    // ===== fixture =====

    private OrderCronFunction Sut(
        bool enabled = true, bool withEscrow = true, bool withCrops = true, bool withTrips = true, bool withDeliveries = true)
        => new(_orders, _txAudit, inboxRepo: null,
            escrowService: withEscrow
                ? new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                    orderRepo: _orders, deliveryRepo: _deliveries)
                : null,
            cropRepo: withCrops ? _crops : null,
            tripRepo: withTrips ? _trips : null,
            deliveryRepo: withDeliveries ? _deliveries : null)
        {
            CommitmentsEnabled = enabled
        };

    private static APIGatewayProxyRequest Request()
        => new() { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" };

    private Task<APIGatewayProxyResponse> RunAsync(OrderCronFunction? sut = null)
        => (sut ?? Sut()).FunctionHandler(Request(), _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private DateTime HoursFromNow(double hours) => _now.AddHours(hours);

    /// <summary>A paid trip order row; <paramref name="fields"/> override or (with null) remove attributes.</summary>
    private Dictionary<string, object> Order(string status, params (string Key, object? Value)[] fields)
    {
        var row = new Dictionary<string, object>
        {
            { "orderId", OrderId },
            { "status", status },
            { "version", "4" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { "listingId", ListingId },
            { "quantity", "500" },
            { "createdAt", Iso(HoursFromNow(-30)) },
            { "updatedAt", Iso(HoursFromNow(-26)) },
            { "paymentVerifiedAt", Iso(HoursFromNow(-26)) }
        };
        foreach (var (key, value) in fields)
        {
            if (value == null)
            {
                row.Remove(key);
            }
            else
            {
                row[key] = value;
            }
        }

        return row;
    }

    private Dictionary<string, object> FarmerPendingOrder(double deadlineInHours, bool withTrip = true)
        => Order("funded",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.FarmerDeadlineAtField, Iso(HoursFromNow(deadlineInHours))),
            ("tripId", withTrip ? TripId : null),
            (OrderCommitment.TruckerResponseField, withTrip ? OrderCommitment.Response.NotAsked : OrderCommitment.Response.None));

    private Dictionary<string, object> TruckerPendingOrder(double askedHoursAgo, double? deadlineInHours)
        => Order("funded",
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            (OrderCommitment.FarmerRespondedAtField, Iso(HoursFromNow(-askedHoursAgo))),
            ("tripId", TripId),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Pending),
            (OrderCommitment.TruckerAskedAtField, Iso(HoursFromNow(-askedHoursAgo))),
            (OrderCommitment.TruckerDeadlineAtField, deadlineInHours is { } d ? Iso(HoursFromNow(d)) : null));

    private Dictionary<string, object> AcceptedTripOrder(string orderId = OrderId, string? deliveryId = DeliveryId)
        => Order("accepted",
            ("orderId", orderId),
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            ("tripId", TripId),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Accepted),
            ("activeDeliveryId", deliveryId));

    private Dictionary<string, object> AwaitingTransportOrder(double? deadlineInHours)
        => Order(OrderCommitment.AwaitingTransportStatus,
            (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted),
            ("tripId", TripId),
            (OrderCommitment.TruckerResponseField, OrderCommitment.Response.Declined),
            (OrderCommitment.TransportFailureField, OrderCommitment.TransportFailure.TruckerDeclined),
            (OrderCommitment.BuyerChoiceDeadlineAtField, deadlineInHours is { } d ? Iso(HoursFromNow(d)) : null));

    private void Serve(string status, params Dictionary<string, object>[] rows)
        => _orders.QueryByStatusForCronAsync(status, Arg.Any<string?>()).Returns(rows.ToList());

    private static Dictionary<string, object> OpenTrip()
        => new() { { "tripId", TripId }, { "status", "open" }, { "departureDate", "2026-11-01" } };

    private static Dictionary<string, object> CancelledTrip()
        => new() { { "tripId", TripId }, { "status", "cancelled" } };

    private static Dictionary<string, object> DeletedTrip()
        => new() { { "tripId", TripId }, { "status", "open" }, { "deletedAt", "2026-10-25T00:00:00.0000000Z" } };

    private static Dictionary<string, object> EscrowedRow(string orderId)
        => new()
        {
            { "escrowId", $"escrow-order-{orderId}" },
            { "orderId", orderId },
            { "createdAt", "2026-10-25T00:00:00.0000000Z" },
            { "status", "escrowed" },
            { "buyerId", "buyer-1" },
            { "amount", "1000" }
        };

    private static Dictionary<string, object> Delivery(string status)
        => new() { { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", status } };

    private Task AssertRefundScheduled()
        => _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f =>
                f != null && f.ContainsKey("expiresAt") && (string)f["refundReason"] == "order_cancelled"));

    private Task AssertNoRefundScheduled()
        => _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());

    private Task AssertAudited(string action, string from, string to)
        => _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == OrderId && (string)e["action"] == action && (string)e["actor"] == "system"
            && (string)e["fromState"] == from && (string)e["toState"] == to));

    private Task AssertNoOrderWrite()
        => _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());

    private Task AssertNoStamp()
        => _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());

    // ===== (a) the farmer never answered =====

    [Fact]
    public async Task FarmerWindowExpired_CancelsTheOrder_RefundsRestoresStockAndFreesTheTruck()
    {
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1));

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"farmerExpired\":\"1\"", actual.Body);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "cancelled"
            && (string)f["cancelReason"] == "farmer_response_expired"
            && (string)f[OrderCommitment.FarmerResponseField] == OrderCommitment.Response.Expired
            && (string)f[OrderCommitment.CancelledByField] == OrderCommitment.Party.System
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Farmer
            && f.ContainsKey("cancelledAt")), 4);
        await AssertAudited("order_cancelled", "funded", "cancelled");
        await AssertRefundScheduled();
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, Quintals);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, Quintals);
    }

    [Fact]
    public async Task FarmerWindowExpired_SelfPickup_TouchesNoTrip()
    {
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1, withTrip: false));

        await RunAsync();

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "cancelled"), 4);
        await AssertRefundScheduled();
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task FarmerWindowExpired_TripWasFullAtFunding_ReleasesNoCapacity()
    {
        var order = FarmerPendingOrder(deadlineInHours: -1);
        order[OrderCommitment.TruckerResponseField] = OrderCommitment.Response.FailedCapacity;
        Serve("funded", order);

        await RunAsync();

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Theory]
    [InlineData(25)]
    [InlineData(23)]
    public async Task FarmerWindow_WithoutTheStamp_IsLeftToTheEscrowSweep(int verifiedHoursAgo)
    {
        // A funded row from before the stamps: no farmerDeadlineAt and no farmerResponse. The first
        // run after the flag turns on must not cancel and refund every old order at once; the 7-day
        // escrow sweep keeps handling such rows.
        Serve("funded", Order("funded",
            ("paymentVerifiedAt", Iso(HoursFromNow(-verifiedHoursAgo))),
            ("tripId", null)));

        var actual = await RunAsync();

        Assert.Contains("\"farmerExpired\":\"0\"", actual.Body);
        await AssertNoOrderWrite();
        await AssertNoRefundScheduled();
    }

    [Fact]
    public async Task FarmerWindowStillOpen_WritesNothing()
    {
        // 10 hours left: past the expiry check, outside the 6-hour reminder lead.
        var order = FarmerPendingOrder(deadlineInHours: 10);
        order["paymentVerifiedAt"] = Iso(HoursFromNow(-14));
        Serve("funded", order);

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        await AssertNoOrderWrite();
        await AssertNoStamp();
        await AssertNoRefundScheduled();
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ===== (b) the trucker never answered =====

    [Fact]
    public async Task TruckerWindowExpired_ParksTheOrderWithTheBuyerAndFreesTheTruck()
    {
        Serve("funded", TruckerPendingOrder(askedHoursAgo: 25, deadlineInHours: -1));

        var actual = await RunAsync();

        Assert.Contains("\"truckerExpired\":\"1\"", actual.Body);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == OrderCommitment.AwaitingTransportStatus
            && (string)f[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.Expired
            && (string)f[OrderCommitment.TransportFailureField] == OrderCommitment.TransportFailure.TruckerExpired
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Trucker
            && f.ContainsKey(OrderCommitment.BuyerChoiceDeadlineAtField)
            && f.ContainsKey(OrderCommitment.TruckerRespondedAtField)), 4);
        await AssertAudited("order_updated", "funded", OrderCommitment.AwaitingTransportStatus);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, Quintals);
        // The money stays held: the buyer decides.
        await AssertNoRefundScheduled();
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task TruckerWindow_WithoutTheStamp_FallsBackToTruckerAskedAtPlus24Hours()
    {
        Serve("funded", TruckerPendingOrder(askedHoursAgo: 25, deadlineInHours: null));

        var actual = await RunAsync();

        Assert.Contains("\"truckerExpired\":\"1\"", actual.Body);
    }

    // ===== (c) the trip is gone =====

    [Fact]
    public async Task TripCancelled_FarmerHasNotAnswered_OnlyNotesItOnTheTruckerResponse()
    {
        _trips.GetByIdAsync(TripId).Returns(CancelledTrip());
        Serve("funded", FarmerPendingOrder(deadlineInHours: 10));

        var actual = await RunAsync();

        Assert.Contains("\"tripCancelled\":\"1\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f =>
                f.Count == 1 && (string)f[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.Cancelled),
            Arg.Is<IReadOnlyDictionary<string, string>>(c => c.Count == 1 && c["status"] == "funded"));
        await AssertNoOrderWrite();
        await AssertAudited("order_updated", "funded", "funded");
    }

    [Fact]
    public async Task TripMissing_FarmerAccepted_FailsTheTransport()
    {
        _trips.GetByIdAsync(TripId).Returns((Dictionary<string, object>?)null);
        Serve("funded", TruckerPendingOrder(askedHoursAgo: 14, deadlineInHours: 10));

        var actual = await RunAsync();

        Assert.Contains("\"tripCancelled\":\"1\"", actual.Body);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == OrderCommitment.AwaitingTransportStatus
            && (string)f[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.Cancelled
            && (string)f[OrderCommitment.TransportFailureField] == OrderCommitment.TransportFailure.TripCancelled), 4);
        await AssertAudited("order_updated", "funded", OrderCommitment.AwaitingTransportStatus);
    }

    [Fact]
    public async Task TripDeleted_AcceptedOrders_AreParkedAndTheirPendingDeliveriesCancelled_OneTripRead()
    {
        _trips.GetByIdAsync(TripId).Returns(DeletedTrip());
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("pending"));
        _deliveries.GetByIdAsync("del-2").Returns(Delivery("pending"));
        Serve("accepted", AcceptedTripOrder(), AcceptedTripOrder("ord-2", "del-2"));

        var actual = await RunAsync();

        Assert.Contains("\"tripCancelled\":\"2\"", actual.Body);
        Assert.Contains("\"tripReads\":\"1\"", actual.Body);
        await _trips.Received(1).GetByIdAsync(TripId);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == OrderCommitment.AwaitingTransportStatus
            && (string)f[OrderCommitment.TransportFailureField] == OrderCommitment.TransportFailure.TripCancelled), 4);
        await _orders.Received(1).UpdateAsync("ord-2", Arg.Any<Dictionary<string, object>>(), 4);
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);
        await _deliveries.Received(1).TryCancelPendingAsync("del-2");
        await _orders.Received(1).ReleaseDeliverySlotAsync("ord-2", "del-2");
        await AssertAudited("order_updated", "accepted", OrderCommitment.AwaitingTransportStatus);
    }

    [Fact]
    public async Task TripCancelled_AcceptedOrderAlreadyPickedUp_IsLeftAlone()
    {
        _trips.GetByIdAsync(TripId).Returns(CancelledTrip());
        _deliveries.GetByIdAsync(DeliveryId).Returns(Delivery("pickedUp"));
        Serve("accepted", AcceptedTripOrder());

        var actual = await RunAsync();

        Assert.Contains("\"tripCancelled\":\"0\"", actual.Body);
        await AssertNoOrderWrite();
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        Assert.Contains("left for the parties", Logs);
    }

    [Fact]
    public async Task TripCancelled_AcceptedOrderWithoutADeliveryRepo_StillParksTheOrder()
    {
        _trips.GetByIdAsync(TripId).Returns(CancelledTrip());
        Serve("accepted", AcceptedTripOrder());

        var actual = await RunAsync(Sut(withDeliveries: false));

        Assert.Contains("\"tripCancelled\":\"1\"", actual.Body);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task TransportAlreadyFailed_TheTripIsNotEvenRead()
    {
        var order = FarmerPendingOrder(deadlineInHours: 10);
        order[OrderCommitment.TruckerResponseField] = OrderCommitment.Response.Cancelled;
        Serve("funded", order);

        var actual = await RunAsync();

        Assert.Contains("\"tripReads\":\"0\"", actual.Body);
        await _trips.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await AssertNoOrderWrite();
        await AssertNoStamp();
    }

    [Fact]
    public async Task UnreadableTrip_FailsNoTransport()
    {
        _trips.GetByIdAsync(TripId).ThrowsAsync(new AmazonDynamoDBException("throttled"));
        Serve("accepted", AcceptedTripOrder());

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"tripCancelled\":\"0\"", actual.Body);
        await AssertNoOrderWrite();
    }

    // ===== (d) the buyer never decided =====

    [Fact]
    public async Task BuyerChoiceExpired_CancelsAndRefundsWithNobodyAtFault()
    {
        Serve(OrderCommitment.AwaitingTransportStatus, AwaitingTransportOrder(deadlineInHours: -1));

        var actual = await RunAsync();

        Assert.Contains("\"buyerChoiceExpired\":\"1\"", actual.Body);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "cancelled"
            && (string)f["cancelReason"] == "transport_not_arranged"
            && (string)f[OrderCommitment.CancelledByField] == OrderCommitment.Party.System
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.None), 4);
        await AssertAudited("order_cancelled", OrderCommitment.AwaitingTransportStatus, "cancelled");
        await AssertRefundScheduled();
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, Quintals);
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task BuyerChoice_WithoutTheStamp_FallsBackToUpdatedAtPlus48Hours()
    {
        var order = AwaitingTransportOrder(deadlineInHours: null);
        order["updatedAt"] = Iso(HoursFromNow(-49));
        Serve(OrderCommitment.AwaitingTransportStatus, order);

        var actual = await RunAsync();

        Assert.Contains("\"buyerChoiceExpired\":\"1\"", actual.Body);
    }

    // ===== (e) reminders =====

    [Fact]
    public async Task FarmerReminder_StampedOnceInsideTheLead_NotAgainWithTheMarkerSet()
    {
        // 24-hour window, 5 hours left.
        var order = FarmerPendingOrder(deadlineInHours: 5);
        order["paymentVerifiedAt"] = Iso(HoursFromNow(-19));
        Serve("funded", order);

        var first = await RunAsync();

        Assert.Contains("\"farmerReminders\":\"1\"", first.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.Count == 1 && f.ContainsKey(OrderCommitment.FarmerReminderAtField)),
            Arg.Is<IReadOnlyDictionary<string, string>>(c =>
                c["status"] == "funded" && c[OrderCommitment.FarmerResponseField] == OrderCommitment.Response.Pending));
        await AssertNoOrderWrite();

        _orders.ClearReceivedCalls();
        order[OrderCommitment.FarmerReminderAtField] = Iso(_now);
        var second = await RunAsync();

        Assert.Contains("\"farmerReminders\":\"0\"", second.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task FarmerReminder_WindowShorterThan12Hours_IsNotSent()
    {
        // Funded 5 hours ago with a deadline 5 hours away: a 10-hour window near departure.
        var order = FarmerPendingOrder(deadlineInHours: 5);
        order["paymentVerifiedAt"] = Iso(HoursFromNow(-5));
        Serve("funded", order);

        var actual = await RunAsync();

        Assert.Contains("\"farmerReminders\":\"0\"", actual.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task FarmerReminder_LegacyRowWithoutAResponseAttribute_IsNotAttempted()
    {
        // Funded 19 hours ago, no stamps: the fallback deadline is 5 hours away, but the
        // conditional stamp on farmerResponse=pending could never match this row.
        Serve("funded", Order("funded", ("paymentVerifiedAt", Iso(HoursFromNow(-19))), ("tripId", null)));

        var actual = await RunAsync();

        Assert.Contains("\"farmerReminders\":\"0\"", actual.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task TruckerReminder_StampedInsideTheLead()
    {
        Serve("funded", TruckerPendingOrder(askedHoursAgo: 19, deadlineInHours: 5));

        var actual = await RunAsync();

        Assert.Contains("\"truckerReminders\":\"1\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.Count == 1 && f.ContainsKey(OrderCommitment.TruckerReminderAtField)),
            Arg.Is<IReadOnlyDictionary<string, string>>(c =>
                c["status"] == "funded" && c[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.Pending));
        await AssertNoOrderWrite();
    }

    [Fact]
    public async Task TruckerReminder_WindowShorterThan12Hours_IsNotSent()
    {
        Serve("funded", TruckerPendingOrder(askedHoursAgo: 4, deadlineInHours: 5));

        var actual = await RunAsync();

        Assert.Contains("\"truckerReminders\":\"0\"", actual.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task BuyerReminder_StampedOnceInsideTheLead()
    {
        var order = AwaitingTransportOrder(deadlineInHours: 5);
        Serve(OrderCommitment.AwaitingTransportStatus, order);

        var first = await RunAsync();

        Assert.Contains("\"buyerReminders\":\"1\"", first.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.Count == 1 && f.ContainsKey(OrderCommitment.BuyerChoiceReminderAtField)),
            Arg.Is<IReadOnlyDictionary<string, string>>(c =>
                c.Count == 1 && c["status"] == OrderCommitment.AwaitingTransportStatus));
        await AssertNoOrderWrite();

        _orders.ClearReceivedCalls();
        order[OrderCommitment.BuyerChoiceReminderAtField] = Iso(_now);
        var second = await RunAsync();

        Assert.Contains("\"buyerReminders\":\"0\"", second.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task BuyerReminder_OutsideTheLead_IsNotSent()
    {
        Serve(OrderCommitment.AwaitingTransportStatus, AwaitingTransportOrder(deadlineInHours: 30));

        var actual = await RunAsync();

        Assert.Contains("\"buyerReminders\":\"0\"", actual.Body);
        await AssertNoStamp();
    }

    // ===== (f) pickup overdue =====

    [Fact]
    public async Task PickupOverdue_StampedOnce()
    {
        var order = AcceptedTripOrder(deliveryId: null);
        order.Remove("tripId");
        order[OrderCommitment.TruckerResponseField] = OrderCommitment.Response.None;
        order[PickupDeadline.Field] = PickupDeadline.Format(HoursFromNow(-1));
        Serve("accepted", order);

        var first = await RunAsync();

        Assert.Contains("\"pickupOverdue\":\"1\"", first.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.Count == 1 && f.ContainsKey(OrderCommitment.PickupOverdueNotifiedAtField)),
            Arg.Is<IReadOnlyDictionary<string, string>>(c => c.Count == 1 && c["status"] == "accepted"));
        await AssertNoOrderWrite();

        _orders.ClearReceivedCalls();
        order[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(_now);
        var second = await RunAsync();

        Assert.Contains("\"pickupOverdue\":\"0\"", second.Body);
        await AssertNoStamp();
    }

    [Fact]
    public async Task PickupDeadlineAhead_NothingStamped()
    {
        var order = AcceptedTripOrder();
        order[PickupDeadline.Field] = PickupDeadline.Format(HoursFromNow(5));
        Serve("accepted", order);

        var actual = await RunAsync();

        Assert.Contains("\"pickupOverdue\":\"0\"", actual.Body);
        await AssertNoStamp();
    }

    // ===== failure modes =====

    [Fact]
    public async Task VersionConflict_IsLoggedNotThrown_AndNoMoneyMoves()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "version moved"));
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1));

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"farmerExpired\":\"0\"", actual.Body);
        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        Assert.Contains("changed since it was read", Logs);
        await AssertNoRefundScheduled();
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task StampConflict_IsCountedNotThrown()
    {
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(false);
        var order = FarmerPendingOrder(deadlineInHours: 5);
        order["paymentVerifiedAt"] = Iso(HoursFromNow(-19));
        Serve("funded", order);

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"farmerReminders\":\"0\"", actual.Body);
        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
    }

    [Fact]
    public async Task RefundSchedulingFails_LogsTheMoneyAlarm_AndTheRunGoesOn()
    {
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1));

        var actual = await RunAsync();

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"farmerExpired\":\"1\"", actual.Body);
        Assert.Contains("ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId=", Logs);
        Assert.Contains("reason=[AmazonDynamoDBException]", Logs);
        // Best effort: the stock still goes back and the truck is still freed.
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, Quintals);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, Quintals);
    }

    [Fact]
    public async Task FlagOff_WalksNothing()
    {
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1));

        var actual = await RunAsync(Sut(enabled: false));

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"outcome\":\"disabled\"", actual.Body);
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
        await AssertNoOrderWrite();
        await AssertNoStamp();
        Assert.DoesNotContain("ORDER_CRON_COMMITMENTS_UNCONFIGURED", Logs);
    }

    [Theory]
    [InlineData(false, true, true, "escrow")]
    [InlineData(true, false, true, "crops")]
    [InlineData(true, true, false, "trips")]
    public async Task MissingDependency_LogsTheTokenOnce_AndWalksNothing(bool escrow, bool crops, bool trips, string missing)
    {
        Serve("funded", FarmerPendingOrder(deadlineInHours: -1));

        var actual = await RunAsync(Sut(withEscrow: escrow, withCrops: crops, withTrips: trips));

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"outcome\":\"unconfigured\"", actual.Body);
        Assert.Contains($"ORDER_CRON_COMMITMENTS_UNCONFIGURED missing={missing}", Logs);
        Assert.Equal(1, CountOf(Logs, "ORDER_CRON_COMMITMENTS_UNCONFIGURED"));
        await _orders.DidNotReceive().QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>());
        await AssertNoOrderWrite();
        await AssertNoStamp();
    }

    [Fact]
    public async Task ExistingAutoCancelAction_StillRoutes()
    {
        var request = new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"auto_cancel_expired\"}" };

        var actual = await Sut().FunctionHandler(request, _context);

        Assert.Equal(200, actual.StatusCode);
        Assert.Contains("\"action\":\"auto_cancel_expired\"", actual.Body);
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

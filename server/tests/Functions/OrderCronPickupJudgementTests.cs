using Amazon.DynamoDBv2.Model;
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
/// ESC-34 (spec 4.8, delivery-verification-api.md 2.6.0): the order cron's pickup judgement at T7. Every
/// row of the spec's table through <c>commitment_deadlines</c>, the self-pickup column, a late offline
/// sync, the settle margin, the flag turned off with orders waiting, and the races (a pickup or another
/// write landing in between).
/// </summary>
public class OrderCronPickupJudgementTests
{
    private const string OrderId = "ord-pj";
    private const string TripId = "trip-pj";
    private const string ListingId = "listing-pj";
    private const string DeliveryId = "del-pj";
    private const string EscrowId = "escrow-order-ord-pj";
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

    public OrderCronPickupJudgementTests()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new List<Dictionary<string, object>>());
        _orders.TrySetDeadlineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OrderDeadline?>()).Returns(true);
        _orders.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object> { { "orderId", ci.Arg<string>() } }));
        _orders.TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(ci => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written,
                new Dictionary<string, object> { { "orderId", ci.ArgAt<string>(0) } }));
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(true);
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object> { { "tripId", TripId }, { "status", "matched" } });
        _trips.TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _crops.TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        _deliveries.TryCancelPendingAsync(Arg.Any<string>()).Returns(true);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("pending"));
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(ci => new List<Dictionary<string, object>> { EscrowedRow(ci.Arg<string>()) });
        _escrows.GetByIdAsync(Arg.Any<string>(), true)
            .Returns(ci => EscrowedRow(ci.ArgAt<string>(0)["escrow-order-".Length..]));
    }

    // ===== fixture =====

    private OrderCronFunction Sut(bool classification = true)
        => new(_orders, _txAudit, inboxRepo: null,
            escrowService: new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                orderRepo: _orders, deliveryRepo: _deliveries),
            cropRepo: _crops, tripRepo: _trips, deliveryRepo: _deliveries)
        {
            CommitmentsEnabled = true,
            PickupClassificationEnabled = classification
        };

    private Task<APIGatewayProxyResponse> RunAsync(bool classification = true)
        => Sut(classification).FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    /// <summary>The pickup deadline passed <paramref name="minutesAgo"/> minutes ago.</summary>
    private Dictionary<string, object> Order(
        double minutesAgo = 30, bool selfPickup = false, params (string Kind, double ReceivedMinutesBeforeDeadline, bool DistanceChecked)[] checkIns)
    {
        var deadline = _now.AddMinutes(-minutesAgo);
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "status", "accepted" }, { "version", "7" },
            { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" }, { "listingId", ListingId }, { "quantity", "500" },
            { "createdAt", Iso(_now.AddDays(-5)) }, { "paymentVerifiedAt", Iso(_now.AddDays(-4)) },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted },
            { OrderCommitment.FulfillmentField, selfPickup ? OrderCommitment.Fulfillment.SelfPickup : OrderCommitment.Fulfillment.Trucker },
            { OrderCommitment.TruckerResponseField, selfPickup ? OrderCommitment.Response.None : OrderCommitment.Response.Accepted },
            { PickupDeadline.Field, Iso(deadline) },
            { PickupJudgement.Field, PickupJudgement.Outcome.Pending },
            { "activeDeliveryId", DeliveryId }
        };
        if (!selfPickup)
        {
            order["tripId"] = TripId;
            order["truckerId"] = "trucker-1";
        }

        var map = new Dictionary<string, object>();
        foreach (var (kind, before, distanceChecked) in checkIns)
        {
            map[kind] = new Dictionary<string, object>
            {
                { PickupJudgement.EntryReceivedAt, Iso(deadline.AddMinutes(-before)) },
                { PickupJudgement.EntryDistanceChecked, distanceChecked },
                { PickupJudgement.EntryLate, before < 0 }
            };
        }

        if (map.Count > 0)
        {
            order[PickupJudgement.CheckInsField] = map;
        }

        return order;
    }

    private static (string, double, bool) Arrived(double minutesBefore = 600, bool distanceChecked = true)
        => (PickupJudgement.Kind.ArrivedPickup, minutesBefore, distanceChecked);

    private static (string, double, bool) Ready(double minutesBefore = 700)
        => (PickupJudgement.Kind.CropsReady, minutesBefore, false);

    private void Serve(Dictionary<string, object> order)
    {
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { order }, (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync(OrderId, true).Returns(order);
    }

    private static Dictionary<string, object> Delivery(string status)
        => new() { { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", status } };

    private static Dictionary<string, object> EscrowedRow(string orderId) => new()
    {
        { "escrowId", $"escrow-order-{orderId}" }, { "orderId", orderId }, { "createdAt", "2026-11-01T00:00:00.0000000Z" },
        { "status", "escrowed" }, { "buyerId", "buyer-1" }, { "amount", "1000" }
    };

    private static bool Judged(Dictionary<string, object> f, string outcome)
        => (string)f[PickupJudgement.Field] == outcome
           && f.ContainsKey(PickupJudgement.JudgedAtField)
           && f.ContainsKey(OrderCommitment.PickupOverdueNotifiedAtField);

    private Task AssertQueued(string reason, string status)
        => _orders.Received(1).TryEnterAdminQueueAsync(OrderId, reason, EscrowId, Arg.Any<string>(), status);

    private Task AssertNotQueued()
        => _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());

    private async Task AssertNoTransition()
    {
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _orders.DidNotReceiveWithAnyArgs().TryUpdateWithAsync(default!, default!, default, default!, default!);
    }

    // ===== row 1: a scan =====

    [Fact]
    public async Task APickedUpDelivery_IsTheNormalFlow_OnlyStamped()
    {
        Serve(Order(30, false, Ready()));
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("pickedUp"));

        var actual = await RunAsync();

        Assert.Contains("\"pickupJudged\":\"1\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => Judged(f, PickupJudgement.Outcome.Scanned)),
            Arg.Is<IReadOnlyDictionary<string, string>>(e =>
                e["status"] == "accepted" && e[PickupJudgement.Field] == PickupJudgement.Outcome.Pending));
        await AssertNoTransition();
        await AssertNotQueued();
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
    }

    // ===== row 2: no trucker check-in =====

    [Fact]
    public async Task NoTruckerCheckIn_IsATruckerNoShow_TheBuyerChoosesAgain()
    {
        Serve(Order(30, false, Ready()));

        var actual = await RunAsync();

        Assert.Contains("\"pickupJudged\":\"1\"", actual.Body);
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == OrderCommitment.AwaitingTransportStatus
            && (string)f[OrderCommitment.TransportFailureField] == PickupJudgement.TruckerNoShowFailure
            && (string)f[OrderCommitment.TruckerResponseField] == PickupJudgement.NoShowResponse
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Trucker
            && f.ContainsKey(OrderCommitment.BuyerChoiceDeadlineAtField)
            && Judged(f, PickupJudgement.Outcome.TruckerNoShow)), 7);
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, Quintals);
        await AssertNotQueued();
        // No money moves for a trucker no-show: the buyer decides in awaiting_transport.
        await _orders.DidNotReceiveWithAnyArgs().TryUpdateWithAsync(default!, default!, default, default!, default!);
        Assert.Contains($"{OrderCronFunction.PickupJudgedToken} orderId={OrderId} outcome=trucker_no_show", Logs);
    }

    // ===== row 3: the trucker came, the farmer did not =====

    [Fact]
    public async Task TruckerCheckedIn_FarmerDidNot_IsAFarmerNoShow_FullRefund()
    {
        Serve(Order(30, false, Arrived()));

        var actual = await RunAsync();

        Assert.Contains("\"pickupJudged\":\"1\"", actual.Body);
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "cancelled"
            && (string)f["cancelReason"] == PickupJudgement.FarmerNoShowReason
            && (string)f[OrderCommitment.FarmerResponseField] == PickupJudgement.NoShowResponse
            && (string)f[OrderCommitment.CancelledByField] == OrderCommitment.Party.System
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Farmer
            && Judged(f, PickupJudgement.Outcome.FarmerNoShow)), 7, "accepted",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items => items.Count == 1));
        _escrows.Received(1).BuildRefundArm(EscrowId, Arg.Any<string>(), "order_cancelled", Arg.Any<string>(), false);
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, Quintals);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, Quintals);
        await AssertNotQueued();
    }

    [Fact]
    public async Task AFarmerNoShow_ThatRestsOnAnUnmeasuredCheckIn_GoesToTheAdminQueueInstead()
    {
        Serve(Order(30, false, Arrived(distanceChecked: false)));

        await RunAsync();

        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => Judged(f, PickupJudgement.Outcome.UncheckedCheckIn)),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await AssertQueued(PickupJudgement.ReasonUnchecked, "accepted");
        await AssertNoTransition();
    }

    // ===== row 4: both came, nothing scanned =====

    [Fact]
    public async Task BothCheckedIn_NoScan_GoesToTheAdminQueue_MoneyHeld()
    {
        Serve(Order(30, false, Arrived(), Ready()));

        await RunAsync();

        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => Judged(f, PickupJudgement.Outcome.BothPresentNoScan)),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await AssertQueued(PickupJudgement.ReasonBothPresent, "accepted");
        await AssertNoTransition();
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        _escrows.DidNotReceiveWithAnyArgs().BuildRefundArm(default!, default!, default!, default!, default);
    }

    // ===== the self-pickup column =====

    [Fact]
    public async Task ASelfPickupBuyerWhoNeverCame_IsCancelled_FullRefund_NobodyBlamedButTheBuyer()
    {
        Serve(Order(30, true, Ready()));

        await RunAsync();

        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "cancelled"
            && (string)f["cancelReason"] == PickupJudgement.BuyerNoShowReason
            && (string)f[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Buyer
            && !f.ContainsKey(OrderCommitment.FarmerResponseField)
            && Judged(f, PickupJudgement.Outcome.BuyerNoShow)), 7, "accepted", Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task ASelfPickupBuyerWhoCame_AndAFarmerWhoDidNot_IsAFarmerNoShow()
    {
        Serve(Order(30, true, Arrived()));

        await RunAsync();

        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["cancelReason"] == PickupJudgement.FarmerNoShowReason), 7, "accepted", Arg.Any<IReadOnlyList<TransactWriteItem>>());
    }

    // ===== a late offline sync =====

    [Fact]
    public async Task ALateOfflineSync_DoesNotSaveTheTrucker_ButTheAdminSeesTheEvidence()
    {
        // The trucker's check-in reached the server 20 minutes after the deadline (an offline phone).
        Serve(Order(30, false, Arrived(minutesBefore: -20), Ready()));

        await RunAsync();

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            Judged(f, PickupJudgement.Outcome.TruckerNoShow)), 7);
        await AssertQueued(PickupJudgement.ReasonLateEvidence, OrderCommitment.AwaitingTransportStatus);
    }

    [Fact]
    public async Task AFarmersLateCropsReady_IsEvidenceNextToTheFarmerNoShow()
    {
        Serve(Order(30, false, Arrived(), Ready(minutesBefore: -5)));

        await RunAsync();

        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            Judged(f, PickupJudgement.Outcome.FarmerNoShow)), 7, "accepted", Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await AssertQueued(PickupJudgement.ReasonLateEvidence, "cancelled");
    }

    // ===== timing, flag and races =====

    [Fact]
    public async Task WithinTheSettleMargin_NothingIsJudgedYet()
    {
        Serve(Order(minutesAgo: 0.5));

        var actual = await RunAsync();

        Assert.Contains("\"pickupJudged\":\"0\"", actual.Body);
        await AssertNoTransition();
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    [Fact]
    public async Task WithTheFlagOff_AWaitingOrderGetsTheP1Marker_AndItsStampIsCleared()
    {
        Serve(Order(30, false, Arrived()));

        var actual = await RunAsync(classification: false);

        Assert.Contains("\"pickupOverdue\":\"1\"", actual.Body);
        Assert.Contains("\"pickupJudged\":\"0\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f =>
                f.ContainsKey(OrderCommitment.PickupOverdueNotifiedAtField)
                && f.ContainsKey(PickupJudgement.Field) && f[PickupJudgement.Field] == null),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        await AssertNoTransition();
        await AssertNotQueued();
    }

    [Fact]
    public async Task AnOrderWithoutTheStamp_KeepsTheP1Marker_WithTheFlagOn()
    {
        var order = Order(30);
        order.Remove(PickupJudgement.Field);
        Serve(order);

        var actual = await RunAsync();

        Assert.Contains("\"pickupOverdue\":\"1\"", actual.Body);
        Assert.Contains("\"pickupJudged\":\"0\"", actual.Body);
        await AssertNoTransition();
    }

    [Fact]
    public async Task AnUnreadableDelivery_Waits()
    {
        Serve(Order(30));
        _deliveries.GetByIdAsync(DeliveryId, true).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        await AssertNoTransition();
    }

    [Fact]
    public async Task APickupThatWinsTheDeliveryCancel_IsJudgedAgainLater()
    {
        Serve(Order(30));
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(false);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("pending"), Delivery("pickedUp"));

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        await AssertNoTransition();
    }

    [Fact]
    public async Task AWriteThatLandedFirst_IsAConflict_AndNothingElseHappens()
    {
        Serve(Order(30));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 7)
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict"));

        var actual = await RunAsync();

        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await AssertNotQueued();
    }

    [Fact]
    public async Task ATripCancelled_StillComesBeforeTheJudgement()
    {
        Serve(Order(30));
        _trips.GetByIdAsync(TripId).Returns(new Dictionary<string, object> { { "tripId", TripId }, { "status", "cancelled" } });

        await RunAsync();

        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f[OrderCommitment.TransportFailureField] == OrderCommitment.TransportFailure.TripCancelled), 7);
        Assert.DoesNotContain(OrderCronFunction.PickupJudgedToken, Logs);
    }
}

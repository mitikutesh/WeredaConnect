using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-18 (spec A2): two buyers fund the last stock of one listing. Exactly one gets it; the other
/// order ends at once (<see cref="OrderFundingService.OutOfStockStatus"/>, <c>listing_out_of_stock</c>,
/// <c>faultParty: none</c>), its whole escrow is put up for refund once through the same path as
/// every funded cancel, nothing is restored to the listing (nothing was taken) and no trip capacity
/// stays held for it. An order within stock funds exactly as before.
/// </summary>
public class OrderFundingStockRaceTests
{
    private const string ListingId = "listing-1";
    private const string TripId = "trip-1";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly List<(string OrderId, string From, string To, Dictionary<string, object>? Fields)> _advanced = new();
    private readonly object _advanceLock = new();

    public OrderFundingStockRaceTests()
    {
        _orders.TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(call =>
            {
                var (orderId, from, to, fields) = (call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2),
                    call.ArgAt<Dictionary<string, object>?>(3));
                lock (_advanceLock)
                {
                    _advanced.Add((orderId, from, to, fields));
                }

                var row = new Dictionary<string, object> { { "orderId", orderId }, { "status", to }, { "version", "5" } };
                foreach (var (key, value) in fields ?? new Dictionary<string, object>())
                {
                    row[key] = value;
                }

                return from == "funded" ? row : null;
            });
        _trips.TryReserveCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(new TripReserveResult(TripReserveOutcome.Reserved, 50));
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(call => new List<Dictionary<string, object>> { Escrow(call.ArgAt<string>(0)) });
    }

    private OrderFundingService Service()
    {
        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null);
        return new OrderFundingService(_orders, _crops, escrowService, _trips, commitmentsEnabled: true, txAuditRepo: _txAudit);
    }

    private static Dictionary<string, object> FundedOrder(string orderId, double kilograms, string? farmerResponse = "pending")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId }, { "listingId", ListingId }, { "buyerId", $"buyer-{orderId}" }, { "farmerId", "farmer-1" },
            { "tripId", TripId }, { "truckerId", "trucker-1" }, { "quantity", kilograms }, { "status", "funded" },
            { "orderTotal", "5000.00" }, { "cropCost", "4500.00" }, { "transportCost", "275.00" },
            { "paymentVerifiedAt", "2026-11-06T08:00:00.0000000Z" }, { "version", "4" }
        };
        if (farmerResponse != null)
        {
            order[OrderCommitment.FarmerResponseField] = farmerResponse;
            order[OrderCommitment.TruckerResponseField] = OrderCommitment.Response.NotAsked;
        }

        return order;
    }

    private static Dictionary<string, object> Escrow(string orderId) => new()
    {
        { "escrowId", $"escrow-order-{orderId}" }, { "createdAt", "2026-11-06T08:00:00.0000000Z" },
        { "orderId", orderId }, { "status", "escrowed" }, { "amount", 5000m }
    };

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    /// <summary>The listing as the conditional write sees it: one atomic check-and-take per call.</summary>
    private void ListingHolds(double quintals, int racers = 1)
    {
        var stock = (decimal)quintals;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var stockLock = new object();
        _crops.TryDecrementStockAsync(ListingId, Arg.Any<string>(), Arg.Any<double>())
            .Returns(async call =>
            {
                // Every racer has read the same stock before any write lands.
                if (Interlocked.Increment(ref arrived) >= racers)
                {
                    gate.TrySetResult();
                }

                await gate.Task;
                lock (stockLock)
                {
                    var need = (decimal)call.ArgAt<double>(2);
                    if (stock < need)
                    {
                        return StockDecrementResult.InsufficientStock;
                    }

                    stock -= need;
                    return StockDecrementResult.Decremented;
                }
            });
    }

    // ===== the race =====

    [Fact]
    public async Task TwoFundingsForMoreThanTheStock_OneWins_TheOtherEndsWithAFullRefund()
    {
        // 10 quintals; buyer A wants 8 (800 kg), buyer B wants 5 (500 kg). Both pay at once.
        ListingHolds(10, racers: 2);
        var service = Service();

        var results = await Task.WhenAll(
            service.EnsureFundedSideEffectsAsync(FundedOrder("order-a", 800), "telebirr", null, _context),
            service.EnsureFundedSideEffectsAsync(FundedOrder("order-b", 500), "telebirr", null, _context));

        results.Count(r => r.Stock == StockDecrementResult.Decremented).Should().Be(1);
        results.Count(r => r.Stock == StockDecrementResult.InsufficientStock).Should().Be(1);
        var winnerId = results[0].Stock == StockDecrementResult.Decremented ? "order-a" : "order-b";
        var loserId = winnerId == "order-a" ? "order-b" : "order-a";
        var winner = results[winnerId == "order-a" ? 0 : 1];
        var loser = results[winnerId == "order-a" ? 1 : 0];

        // The winner funds exactly as before: escrow, trip capacity, no status change.
        winner.EscrowCreated.Should().BeTrue();
        winner.Trip.Should().Be(TripReserveOutcome.Reserved);
        winner.OutOfStockOrder.Should().BeNull();
        await _trips.Received(1).TryReserveCapacityAsync(TripId, winnerId, Arg.Any<double>());

        // The loser ends: one status write, nobody's fault, the farmer is not penalized.
        loser.EscrowCreated.Should().BeTrue();
        loser.Trip.Should().BeNull();
        loser.OutOfStockOrder.Should().NotBeNull();
        loser.OutOfStockOrder!["status"].Should().Be(OrderFundingService.OutOfStockStatus);
        var end = _advanced.Should().ContainSingle().Subject;
        end.OrderId.Should().Be(loserId);
        end.From.Should().Be("funded");
        end.To.Should().Be(OrderFundingService.OutOfStockStatus);
        end.Fields!["cancelReason"].Should().Be("listing_out_of_stock");
        end.Fields[OrderCommitment.CancelledByField].Should().Be(OrderCommitment.Party.System);
        end.Fields[OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.None);
        end.Fields.Should().NotContainKey(OrderCommitment.FarmerResponseField);

        // Its money: the refund is armed once, on its own escrow, through the cancel path.
        await _escrows.Received(1).UpdateStatusAsync(
            $"escrow-order-{loserId}", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "listing_out_of_stock" && f.ContainsKey("expiresAt")));
        await _escrows.DidNotReceive().UpdateStatusAsync(
            $"escrow-order-{winnerId}", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());

        // No stock goes back (none was taken), and no capacity stays held for the loser.
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _trips.DidNotReceive().TryReserveCapacityAsync(TripId, loserId, Arg.Any<double>());
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, loserId, Arg.Any<double>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(TripId, winnerId, Arg.Any<double>());

        // Audited as a system cancel, and no alarm: a lost race is not a fault.
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == loserId && (string)e["action"] == "order_cancelled"));
        Logs.Should().NotContain("ORDER_STOCK_DECREMENT_FAILED").And.NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [Fact]
    public async Task OutOfStock_PenalizesNobody()
    {
        // Spec 4.11 "not penalized: races the system caused" and D16. Reputation reads only what the
        // row records: no fault party, a system cancel, and no farmer or trucker answer written (a
        // decline or an expiry would be counted against them; the farmer was never really asked).
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        var ended = result.OutOfStockOrder!;
        ended[OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.None);
        ended[OrderCommitment.CancelledByField].Should().Be(OrderCommitment.Party.System);
        ended["cancelReason"].Should().Be(OrderFundingService.OutOfStockReason);
        OrderCommitment.ServerCancelReasons.Should().Contain(OrderFundingService.OutOfStockReason);
        var fields = _advanced.Single().Fields!;
        fields.Should().NotContainKey(OrderCommitment.FarmerResponseField)
            .And.NotContainKey(OrderCommitment.TruckerResponseField)
            .And.NotContainKey(OrderCommitment.DeclineReasonField)
            .And.NotContainKey(OrderCommitment.TransportFailureField);
    }

    [Fact]
    public async Task TwoFundingsThatBothFit_BothWin()
    {
        ListingHolds(10, racers: 2);
        var service = Service();

        var results = await Task.WhenAll(
            service.EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context),
            service.EnsureFundedSideEffectsAsync(FundedOrder("order-b", 500), "telebirr", null, _context));

        results.Should().OnlyContain(r => r.Stock == StockDecrementResult.Decremented && r.OutOfStockOrder == null);
        _advanced.Should().BeEmpty();
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    // ===== the outcomes one at a time =====

    [Fact]
    public async Task ExactlyTheStockLeft_Wins()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.Decremented);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "system", null, _context);

        result.Stock.Should().Be(StockDecrementResult.Decremented);
        result.OutOfStockOrder.Should().BeNull();
        _advanced.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedFunding_AlreadyApplied_IsUnchanged()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.AlreadyApplied);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "system", null, _context);

        result.Stock.Should().Be(StockDecrementResult.AlreadyApplied);
        result.Trip.Should().Be(TripReserveOutcome.Reserved);
        result.OutOfStockOrder.Should().BeNull();
        _advanced.Should().BeEmpty();
    }

    [Fact]
    public async Task OutOfStock_WithTheFlagOff_EndsTheOrderTheSameWay()
    {
        // The stock fix is not part of the commitments rollout: no farmerResponse on the row.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null);
        var service = new OrderFundingService(_orders, _crops, escrowService, tripRepo: null, commitmentsEnabled: false);

        var result = await service.EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500, farmerResponse: null), "system", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        _advanced.Should().ContainSingle(a => a.OrderId == "order-a" && a.To == OrderFundingService.OutOfStockStatus);
        await _escrows.Received(1).UpdateStatusAsync("escrow-order-order-a", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "listing_out_of_stock"));
    }

    [Fact]
    public async Task OutOfStock_InAwaitingTransport_IsEndedFromThere()
    {
        // A repair: the first attempt's decrement failed and the trip was full; the order waits on
        // the buyer. It holds no stock, so it ends from awaiting_transport.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.TryAdvanceStatusAsync("order-a", OrderCommitment.AwaitingTransportStatus, OrderFundingService.OutOfStockStatus,
                Arg.Any<Dictionary<string, object>?>())
            .Returns(new Dictionary<string, object> { { "orderId", "order-a" }, { "status", OrderFundingService.OutOfStockStatus } });
        _orders.TryAdvanceStatusAsync("order-a", "funded", Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns((Dictionary<string, object>?)null);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        await _escrows.Received(1).UpdateStatusAsync("escrow-order-order-a", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task OutOfStock_EndedAlreadyByAConcurrentDelivery_RefundArmedAgainHarmlessly_NoAlarm()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.TryAdvanceStatusAsync("order-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns((Dictionary<string, object>?)null);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-a" }, { "status", OrderFundingService.OutOfStockStatus }, { "cancelReason", "listing_out_of_stock" }
        });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        Logs.Should().NotContain("ORDER_STOCK_DECREMENT_FAILED");
        // The run that ended it audited it; this one only re-arms the idempotent refund and release.
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _escrows.Received(1).UpdateStatusAsync("escrow-order-order-a", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "listing_out_of_stock"));
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, "order-a", 5.0);
    }

    [Fact]
    public async Task OutOfStock_OrderMovedOn_IsLeftAlone_AndAlarmed()
    {
        // The farmer accepted between the read and the end (flag off: funded -> accepted). The
        // order now needs stock the listing does not have: a person decides, no refund is armed.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.TryAdvanceStatusAsync("order-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns((Dictionary<string, object>?)null);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object> { { "orderId", "order-a" }, { "status", "accepted" } });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED").And.Contain("reason=insufficient_stock");
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OutOfStock_FarmerAlreadyAccepted_IsLeftAlone_AndAlarmed()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);

        var result = await Service().EnsureFundedSideEffectsAsync(
            FundedOrder("order-a", 500, farmerResponse: OrderCommitment.Response.Accepted), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        _advanced.Should().BeEmpty();
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED").And.Contain("status=farmer_accepted");
    }

    [Fact]
    public async Task OutOfStock_EndWriteThrows_IsAlarmed_AndNothingElseRuns()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.TryAdvanceStatusAsync("order-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new InvalidOperationException("dynamo down"));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED").And.Contain("step=end");
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task OutOfStock_TheFreshEscrowIsNotFoundAtFirst_ItIsLookedForOnceMore_AndArmed()
    {
        // The escrow was written a moment ago and the lookups are eventually consistent.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.GetByIdAsync("escrow-order-order-a").Returns((Dictionary<string, object>?)null);
        // The create finds it, the first arming misses it, the second finds it.
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(
            _ => new List<Dictionary<string, object>> { Escrow("order-a") },
            _ => new List<Dictionary<string, object>>(),
            _ => new List<Dictionary<string, object>> { Escrow("order-a") });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        await _escrows.Received(1).UpdateStatusAsync("escrow-order-order-a", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null && (string)f["refundReason"] == "listing_out_of_stock"));
        Logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [Fact]
    public async Task OutOfStock_TheEscrowCannotBeArmed_IsAlarmed()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(
            _ => new List<Dictionary<string, object>> { Escrow("order-a") },
            _ => new List<Dictionary<string, object>>());
        _escrows.GetByIdAsync("escrow-order-order-a").Returns((Dictionary<string, object>?)null);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        Logs.Should().Contain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED").And.Contain("reason=escrow_not_armable");
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task OutOfStock_RefundSchedulingThrows_LogsTheRefundAlarm_TheOrderStaysEnded()
    {
        // The 7 day sweep still refunds an ended order whose escrow expires; the alarm says why it waits.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new InvalidOperationException("dynamo down"));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        Logs.Should().Contain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, "order-a", 5.0);
    }
}

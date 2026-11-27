using Amazon.DynamoDBv2.Model;
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
        _orders.TryAdvanceStatusUnlessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(),
                OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted)
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
        // FUP-210: the end reads the order (strongly consistent), plans its money and writes the end
        // and the refund arm in one transaction, recorded with the plain ends above.
        _orders.GetByIdAsync(Arg.Any<string>(), true).Returns(call => new Dictionary<string, object>
        {
            { "orderId", call.ArgAt<string>(0) }, { "status", "funded" }, { "version", "4" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending }
        });
        _orders.TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(call =>
            {
                var (orderId, fields, from) = (call.ArgAt<string>(0), call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<string>(3));
                lock (_advanceLock)
                {
                    _advanced.Add((orderId, from, (string)fields["status"], fields));
                }

                var row = new Dictionary<string, object> { { "orderId", orderId }, { "version", "5" } };
                foreach (var (key, value) in fields)
                {
                    row[key] = value;
                }

                return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, row);
            });
        _trips.TryReserveCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(new TripReserveResult(TripReserveOutcome.Reserved, 50));
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(call => new List<Dictionary<string, object>> { Escrow(call.ArgAt<string>(0)) });
        _escrows.GetByIdAsync(Arg.Any<string>(), true)
            .Returns(call => Escrow(call.ArgAt<string>(0)["escrow-order-".Length..]));
    }

    private OrderFundingService Service()
    {
        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null, orderRepo: _orders);
        return new OrderFundingService(_orders, _crops, escrowService, _trips, commitmentsEnabled: true, txAuditRepo: _txAudit);
    }

    /// <summary>FUP-210: the refund armed in the end's own transaction, with its row, and never by a separate write.</summary>
    private async Task AssertArmedWithTheEnd(string orderId)
    {
        _escrows.Received(1).BuildRefundArm($"escrow-order-{orderId}", Arg.Any<string>(), OrderFundingService.OutOfStockReason,
            Arg.Any<string>(), false);
        await _escrowAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["escrowId"] == $"escrow-order-{orderId}" && (string)e["action"] == "refund_scheduled"
            && (string)e["actor"] == "system" && (string)e["metadata"] == OrderFundingService.OutOfStockReason));
        await _escrows.DidNotReceive().UpdateStatusAsync(
            $"escrow-order-{orderId}", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    /// <summary>Nothing armed: no separate arm, and no transaction carrying one landed.</summary>
    private async Task AssertNothingArmed()
    {
        await _escrows.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "refund_scheduled"));
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

        // Its money: the refund is armed once, on its own escrow, in the end's transaction (FUP-210).
        end.Fields["status"].Should().Be(OrderFundingService.OutOfStockStatus);
        await AssertArmedWithTheEnd(loserId);
        _escrows.DidNotReceive().BuildRefundArm($"escrow-order-{winnerId}", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
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
        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null, orderRepo: _orders);
        var service = new OrderFundingService(_orders, _crops, escrowService, tripRepo: null, commitmentsEnabled: false);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-a" }, { "status", "funded" }, { "version", "4" }
        });

        var result = await service.EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500, farmerResponse: null), "system", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        _advanced.Should().ContainSingle(a => a.OrderId == "order-a" && a.To == OrderFundingService.OutOfStockStatus);
        await AssertArmedWithTheEnd("order-a");
    }

    [Fact]
    public async Task OutOfStock_InAwaitingTransport_IsEndedFromThere()
    {
        // A repair: the first attempt's decrement failed and the trip was full; the order waits on
        // the buyer. It holds no stock, so it ends from awaiting_transport.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-a" }, { "status", OrderCommitment.AwaitingTransportStatus }, { "version", "6" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending }
        });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        var end = _advanced.Should().ContainSingle().Subject;
        end.From.Should().Be(OrderCommitment.AwaitingTransportStatus);
        await _orders.Received(1).TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), 6,
            OrderCommitment.AwaitingTransportStatus, Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await AssertArmedWithTheEnd("order-a");
    }

    [Fact]
    public async Task OutOfStock_EndedAlreadyByAConcurrentDelivery_RefundArmedAgainHarmlessly_NoAlarm()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.TryAdvanceStatusUnlessAsync("order-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns((Dictionary<string, object>?)null);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-a" }, { "status", OrderFundingService.OutOfStockStatus }, { "cancelReason", "listing_out_of_stock" }
        });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        Logs.Should().NotContain("ORDER_STOCK_DECREMENT_FAILED");
        // The run that ended it audited it; this one only re-arms the idempotent refund and release.
        await _orders.DidNotReceiveWithAnyArgs().TryUpdateWithAsync(default!, default!, default, default!, default!);
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
        _orders.TryAdvanceStatusUnlessAsync("order-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(),
                Arg.Any<string>(), Arg.Any<string>())
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
    public async Task OutOfStock_FarmerAcceptsInTheRepairWindow_TheWriteRefuses_AndItIsLeftForAPerson()
    {
        // The row read still said pending; the farmer's accept landed before the end. The end's
        // condition (the version read) refuses it, the row read again shows the accept, so the accept
        // is never undone and nothing is armed.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.GetByIdAsync("order-a", true).Returns(
            new Dictionary<string, object>
            {
                { "orderId", "order-a" }, { "status", "funded" }, { "version", "4" },
                { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending }
            },
            new Dictionary<string, object>
            {
                { "orderId", "order-a" }, { "status", "funded" }, { "version", "5" },
                { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted }
            });
        _orders.TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), 4, "funded", Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderChanged));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        await _orders.Received(1).TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED").And.Contain("status=farmer_accepted");
        await AssertNothingArmed();
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OutOfStock_EscrowCreateFailedAtFunding_IsCreatedOnceMore_BeforeTheEnd_ThenArmedWithIt()
    {
        // The order is cancelled next, so no redelivery or delivery link would create the escrow later.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.GetByIdAsync("escrow-order-order-a").Returns((Dictionary<string, object>?)null);
        // Create attempt 1 finds none and its write fails; attempt 2 finds none and writes; the
        // end's plan then finds it.
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(
            _ => new List<Dictionary<string, object>>(),
            _ => new List<Dictionary<string, object>>(),
            _ => new List<Dictionary<string, object>> { Escrow("order-a") });
        var creates = 0;
        _escrows.CreateAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(_ => ++creates == 1 ? throw new InvalidOperationException("dynamo down") : Task.CompletedTask);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.EscrowCreated.Should().BeFalse();
        result.OutOfStockOrder.Should().NotBeNull();
        creates.Should().Be(2);
        Received.InOrder(() =>
        {
            _escrows.CreateAsync(Arg.Any<Dictionary<string, object>>());
            _escrows.CreateAsync(Arg.Any<Dictionary<string, object>>());
            _orders.TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), 4, "funded", Arg.Any<IReadOnlyList<TransactWriteItem>>());
        });
        await AssertArmedWithTheEnd("order-a");
        Logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [Fact]
    public async Task OutOfStock_EscrowCannotBeCreated_EndsAsBefore_AndIsAlarmedAsNoEscrow()
    {
        // No record holds the money: the end alone (its status and farmerResponse condition), as before.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.GetByIdAsync("escrow-order-order-a").Returns((Dictionary<string, object>?)null);
        _escrows.GetByIdAsync("escrow-order-order-a", true).Returns((Dictionary<string, object>?)null);
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _escrows.CreateAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new InvalidOperationException("dynamo down"));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        await _escrows.Received(2).CreateAsync(Arg.Any<Dictionary<string, object>>());
        await _orders.Received(1).TryAdvanceStatusUnlessAsync("order-a", "funded", OrderFundingService.OutOfStockStatus,
            Arg.Any<Dictionary<string, object>?>(), OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted);
        await _orders.DidNotReceiveWithAnyArgs().TryUpdateWithAsync(default!, default!, default, default!, default!);
        Logs.Should().Contain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId=").And.Contain("reason=no_escrow");
        await AssertNothingArmed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutOfStock_EndWriteOrPlanThrows_IsAlarmed_AndNothingElseRuns(bool planThrows)
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        if (planThrows)
        {
            _escrows.GetByIdAsync("escrow-order-order-a", true).ThrowsAsync(new InvalidOperationException("dynamo down"));
        }
        else
        {
            _orders.TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<TransactWriteItem>>())
                .ThrowsAsync(new InvalidOperationException("dynamo down"));
        }

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED").And.Contain("step=end");
        Logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
        await AssertNothingArmed();
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OutOfStock_TheFreshEscrowIsMissingFromTheIndex_TheStrongReadStillArmsIt()
    {
        // The escrow was written a moment ago and the index read is eventually consistent; the end's
        // plan reads escrow-order-{orderId} strongly consistent, so it arms it the first time.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _escrows.GetByIdAsync("escrow-order-order-a").Returns((Dictionary<string, object>?)null);
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(
            _ => new List<Dictionary<string, object>> { Escrow("order-a") },
            _ => new List<Dictionary<string, object>>());

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        await AssertArmedWithTheEnd("order-a");
        Logs.Should().NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [Fact]
    public async Task OutOfStock_EscrowHeldForReview_EndsWhileTheHoldStands_ArmsNothing_AndIsAlarmedAsBefore()
    {
        // FRD-03: the held money is its admin's to decide. Before FUP-210 the end landed and the separate
        // arm found the escrow held; now the end is fenced on the hold, and the same alarm follows.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        var held = Escrow("order-a");
        held["status"] = "held";
        _escrows.GetByIdAsync("escrow-order-order-a", true).Returns(held);
        _escrows.GetByIdAsync("escrow-order-order-a").Returns(held);
        _escrows.QueryByOrderIdAsync("order-a", Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { held });

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        _advanced.Should().ContainSingle(a => a.OrderId == "order-a" && a.To == OrderFundingService.OutOfStockStatus);
        _escrows.Received(1).BuildStatusCheck("escrow-order-order-a", Arg.Any<string>(), "held");
        _escrows.DidNotReceiveWithAnyArgs().BuildRefundArm(default!, default!, default!, default!, default);
        await AssertNothingArmed();
        Logs.Should().Contain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED").And.Contain("reason=escrow_not_armable");
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, "order-a", 5.0);
    }

    [Theory]
    [InlineData("disputed", null, null)]
    [InlineData("escrowed", "settlementLock", "refund")]
    [InlineData("escrowed", "manualPayout", "open")]
    public async Task OutOfStock_MoneyFrozenOrMoving_TheOrderIsNotEnded_AndIsAlarmedForAPerson(string status, string? marker, string? value)
    {
        // Never an end without its refund armed: the order stays funded and the alarm asks a person.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        var escrow = Escrow("order-a");
        escrow["status"] = status;
        if (marker != null)
        {
            escrow[marker] = value!;
        }

        _escrows.GetByIdAsync("escrow-order-order-a", true).Returns(escrow);

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().BeNull();
        _advanced.Should().BeEmpty();
        Logs.Should().Contain("ORDER_STOCK_DECREMENT_FAILED orderId=order-a").And.Contain("status=escrow_busy");
        await AssertNothingArmed();
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OutOfStock_EscrowChangedUnderTheEnd_IsPlannedAgain_ThenEndsAndArms()
    {
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        var calls = 0;
        _orders.TryUpdateWithAsync("order-a", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(_ => ++calls == 1
                ? new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused)
                : new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, new Dictionary<string, object>
                {
                    { "orderId", "order-a" }, { "status", OrderFundingService.OutOfStockStatus }
                }));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        calls.Should().Be(2);
        await _escrowAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "refund_scheduled"));
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "order_cancelled"));
        Logs.Should().NotContain("ORDER_STOCK_DECREMENT_FAILED").And.NotContain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
    }

    [Fact]
    public async Task OutOfStock_EndedByAnotherRun_ItsReArmThrows_LogsTheRefundAlarm_TheOrderStaysEnded()
    {
        // The 7 day sweep still refunds an ended order whose escrow expires; the alarm says why it waits.
        _crops.TryDecrementStockAsync(ListingId, "order-a", 5.0).Returns(StockDecrementResult.InsufficientStock);
        _orders.GetByIdAsync("order-a", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-a" }, { "status", OrderFundingService.OutOfStockStatus }, { "cancelReason", "listing_out_of_stock" }
        });
        _escrows.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new InvalidOperationException("dynamo down"));

        var result = await Service().EnsureFundedSideEffectsAsync(FundedOrder("order-a", 500), "telebirr", null, _context);

        result.OutOfStockOrder.Should().NotBeNull();
        Logs.Should().Contain("ORDER_ESCROW_REFUND_SCHEDULE_FAILED");
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, "order-a", 5.0);
    }
}

using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-09: the order status follows the money (order-lifecycle.md 1.17.0, escrow-protocol.md 1.17.0).
/// The target table is total over the escrow states; the order only moves forward, one
/// status-conditioned hop at a time, and a verdict escrow is never moved to disbursing or settled.
/// </summary>
public class OrderMoneyMirrorTests
{
    private const string OrderId = "order-1";
    private const string DeliveryId = "delivery-1";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly OrderMoneyMirror _mirror;
    private readonly List<(string From, string To)> _hops = new();
    private string _status = "delivered";
    private int _version = 4;

    public OrderMoneyMirrorTests()
    {
        _mirror = new OrderMoneyMirror(_orders, _deliveries, _txAudit, new DisputeVerdictMirror(_orders, _deliveries, _txAudit));
        _orders.GetByIdAsync(OrderId, true).Returns(_ => Order());
        _orders.TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            if (call.ArgAt<string>(1) != _status)
            {
                return Task.FromResult<Dictionary<string, object>?>(null);
            }

            _hops.Add((_status, call.ArgAt<string>(2)));
            _status = call.ArgAt<string>(2);
            _version++;
            return Task.FromResult<Dictionary<string, object>?>(Order());
        });
    }

    private Dictionary<string, object> Order(params (string Key, object Value)[] extra)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "status", _status }, { "version", _version }, { "activeDeliveryId", DeliveryId }
        };
        foreach (var (key, value) in extra)
        {
            order[key] = value;
        }

        return order;
    }

    private static Dictionary<string, object> Escrow(string status, params (string Key, object Value)[] extra)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-order-1" }, { "status", status }, { "orderId", OrderId },
            { "deliveryId", DeliveryId }, { "createdAt", "2026-10-01T10:00:00.0000000Z" }
        };
        foreach (var (key, value) in extra)
        {
            escrow[key] = value;
        }

        return escrow;
    }

    private static readonly (string, object) Queued = (ManualPayoutFields.Marker, ManualPayoutFields.MarkerOpen);
    private static readonly (string, object) OpRelease = (ManualPayoutFields.Op, ManualPayoutFields.OpRelease);
    private static readonly (string, object) OpRefund = (ManualPayoutFields.Op, ManualPayoutFields.OpRefund);
    private static readonly (string, object) TruckerOwed = (TruckerPayoutFields.Pending, true);

    // ──────────────────────────────────────────────
    // The target table (V9: total over the escrow states)
    // ──────────────────────────────────────────────

    public static IEnumerable<object[]> TargetCases()
    {
        yield return new object[] { Escrow("completed"), OrderMoneyTarget.Settled };
        yield return new object[] { Escrow("completed", TruckerOwed), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("completed", Queued, OpRelease), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("released"), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("released", TruckerOwed), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("released", Queued, OpRelease), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("escrowed", Queued, OpRelease), OrderMoneyTarget.Disbursing };
        yield return new object[] { Escrow("escrowed", Queued, OpRefund), OrderMoneyTarget.None };
        yield return new object[] { Escrow("escrowed"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("disputed"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("initiated"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("cancelled"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("held"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("something_new"), OrderMoneyTarget.None };
        yield return new object[] { Escrow("refunded"), OrderMoneyTarget.Refunded };
        yield return new object[] { Escrow("refunded", ("disputeResolution", "buyer_wins")), OrderMoneyTarget.Verdict };
        yield return new object[] { Escrow("completed", ("disputeResolution", "farmer_wins")), OrderMoneyTarget.Verdict };
        yield return new object[] { Escrow("dispute_resolved", ("disputeResolution", "farmer_wins"), Queued, OpRelease), OrderMoneyTarget.Verdict };
        yield return new object[] { Escrow("dispute_resolved", ("disputeResolution", "buyer_wins")), OrderMoneyTarget.Verdict };
    }

    [Theory]
    [MemberData(nameof(TargetCases))]
    public void TargetFor_IsTotalOverTheEscrowStates(Dictionary<string, object> escrow, OrderMoneyTarget expected)
        => OrderMoneyMirror.TargetFor(escrow).Should().Be(expected);

    /// <summary>C6: the money hops are exactly the two system edges of the client transition map.</summary>
    [Fact]
    public void MoneyHops_AreEdgesOfTheOrderTransitionMap()
    {
        var map = SyncFunction.OrderTransitions;
        foreach (var (from, to) in OrderMoneyMirror.MoneyHops)
        {
            map.Should().ContainKey(from);
            map[from].Should().Contain(to);
        }
    }

    // ──────────────────────────────────────────────
    // Moving the order
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Completed_MovesADeliveredOrderThroughDisbursingToSettled_OneAuditRowPerHop()
    {
        var result = await _mirror.FollowAsync(Escrow("completed"), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        result.OrderStatus.Should().Be("settled");
        _hops.Should().Equal(("delivered", "disbursing"), ("disbursing", "settled"));
        await _txAudit.Received(2).AppendAsync(Arg.Is<Dictionary<string, object>>(r => r["action"].ToString() == "order_updated"));
    }

    [Fact]
    public async Task Released_WithATruckerLegOwed_StopsAtDisbursing()
    {
        var result = await _mirror.FollowAsync(Escrow("released", TruckerOwed), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _status.Should().Be("disbursing");
        _hops.Should().Equal(("delivered", "disbursing"));
    }

    [Fact]
    public async Task QueuedRelease_MovesTheOrderToDisbursing()
    {
        (await _mirror.FollowAsync(Escrow("escrowed", Queued, OpRelease), "release")).Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _status.Should().Be("disbursing");
    }

    [Fact]
    public async Task AnOrderAlreadyThere_IsNotWritten()
    {
        _status = "settled";

        (await _mirror.FollowAsync(Escrow("completed"), "sweep")).Outcome.Should().Be(OrderMoneyOutcome.AlreadyThere);
        _hops.Should().BeEmpty();
    }

    [Theory]
    [InlineData("in_transit")]
    [InlineData("pickup_confirmed")]
    [InlineData("accepted")]
    public async Task ALaggingOrderBehindAConfirmedDelivery_IsAdvancedToDeliveredFirst(string lagging)
    {
        _status = lagging;

        var result = await _mirror.FollowAsync(Escrow("completed", ("deliveryConfirmedAt", "2026-10-02T10:00:00Z")), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _hops.Should().Equal((lagging, "delivered"), ("delivered", "disbursing"), ("disbursing", "settled"));
    }

    [Fact]
    public async Task ALaggingOrder_WithNoDeliveredDelivery_CannotFollow()
    {
        _status = "in_transit";
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object> { { "status", "inTransit" } });

        var result = await _mirror.FollowAsync(Escrow("completed"), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Mismatch);
        result.Reason.Should().Be("cannot_follow");
        _hops.Should().BeEmpty();
    }

    [Theory]
    [InlineData("funded")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("disputed")]
    [InlineData("payment_failed")]
    public async Task AnOrderThatCannotFollowAPayout_IsAMismatch_AndNotWritten(string status)
    {
        _status = status;

        var result = await _mirror.FollowAsync(Escrow("completed"), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Mismatch);
        _hops.Should().BeEmpty();
    }

    [Fact]
    public async Task ASettledOrderOverMoneyStillOwingALeg_IsAhead()
    {
        _status = "settled";

        var result = await _mirror.FollowAsync(Escrow("released", TruckerOwed), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Mismatch);
        result.Reason.Should().Be("order_ahead");
    }

    [Theory]
    [InlineData("disbursing")]
    [InlineData("settled")]
    public async Task AnOrderPaidOutOverHeldMoney_IsAhead(string status)
    {
        _status = status;

        var result = await _mirror.FollowAsync(Escrow("escrowed"), "sweep", orderStatusHint: status);

        result.Outcome.Should().Be(OrderMoneyOutcome.Mismatch);
        result.Reason.Should().Be("order_ahead");
    }

    [Fact]
    public async Task HeldMoney_WithoutAHint_ReadsNothing()
    {
        (await _mirror.FollowAsync(Escrow("escrowed"), "release")).Outcome.Should().Be(OrderMoneyOutcome.NotApplicable);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task AConcurrentWriterThatGotThereFirst_EndsTheFollow_WithoutAnotherWrite()
    {
        // The first hop loses: another writer moved the order to settled in between.
        _orders.Configure().TryAdvanceStatusAsync(OrderId, "delivered", "disbursing").Returns(_ =>
        {
            _status = "settled";
            return Task.FromResult<Dictionary<string, object>?>(null);
        });

        var result = await _mirror.FollowAsync(Escrow("completed"), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.AlreadyThere);
        result.OrderStatus.Should().Be("settled");
    }

    [Fact]
    public async Task AWriteThatKeepsLosing_GivesUp_AsFailed()
    {
        _orders.Configure().TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>()).Returns((Dictionary<string, object>?)null);

        var result = await _mirror.FollowAsync(Escrow("completed"), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Failed);
        result.Reason.Should().Be("conflict");
    }

    [Fact]
    public async Task ARepositoryThatThrows_IsFailed_NeverThrown()
    {
        _orders.Configure().GetByIdAsync(OrderId, true).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _mirror.FollowAsync(Escrow("completed"), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Failed);
    }

    [Fact]
    public async Task AnEscrowWithoutAnOrder_UsesItsDeliverysOrder()
    {
        var escrow = Escrow("completed");
        escrow.Remove("orderId");
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object> { { "orderId", OrderId }, { "status", "delivered" } });

        (await _mirror.FollowAsync(escrow, "release")).Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _status.Should().Be("settled");
    }

    [Fact]
    public async Task AnEscrowWithNoOrderAtAll_IsNoOrder()
    {
        var escrow = Escrow("completed");
        escrow.Remove("orderId");
        escrow.Remove("deliveryId");

        (await _mirror.FollowAsync(escrow, "release")).Outcome.Should().Be(OrderMoneyOutcome.NoOrder);
    }

    // ──────────────────────────────────────────────
    // Refunds and overrides
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("cancelled", OrderMoneyOutcome.Consistent)]
    [InlineData("rejected", OrderMoneyOutcome.Consistent)]
    [InlineData("dispute_resolved", OrderMoneyOutcome.Consistent)]
    [InlineData("delivered", OrderMoneyOutcome.Mismatch)]
    [InlineData("accepted", OrderMoneyOutcome.Mismatch)]
    public async Task ARefund_IsOnlyCheckedAgainstAClosedOrder(string status, OrderMoneyOutcome expected)
    {
        _status = status;

        (await _mirror.FollowAsync(Escrow("refunded"), "sweep")).Outcome.Should().Be(expected);
        _hops.Should().BeEmpty();
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public async Task AFlaggedOverride_ExcusesARefund_NeverMovingTheOrder(string flag)
    {
        _status = "in_transit";
        _orders.Configure().GetByIdAsync(OrderId, true).Returns(_ => Order((flag, "2026-10-09T10:00:00Z")));

        (await _mirror.FollowAsync(Escrow("refunded"), "sweep")).Outcome.Should().Be(OrderMoneyOutcome.Consistent);
        _hops.Should().BeEmpty();
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public async Task AFlagLeftByAnOverrideRefundThatFailed_NeverKeepsAReleaseFromMovingTheOrder(string flag)
    {
        // The flag is written before the money moves; the refund then lost the lock to a release.
        _status = "in_transit";
        _orders.Configure().GetByIdAsync(OrderId, true).Returns(_ => Order((flag, "2026-10-09T10:00:00Z")));

        var result = await _mirror.FollowAsync(Escrow("completed", ("deliveryConfirmedAt", "2026-10-02T10:00:00Z")), "release");

        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _status.Should().Be("settled");
    }

    [Fact]
    public async Task ThreeHopsAndThreeLostRaces_StillReachSettled()
    {
        _status = "in_transit";
        var lost = 0;
        _orders.Configure().TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            // Every hop first loses once to a writer that changed nothing that matters.
            if (lost < 3 && _hops.Count == lost)
            {
                lost++;
                return Task.FromResult<Dictionary<string, object>?>(null);
            }

            _hops.Add((_status, call.ArgAt<string>(2)));
            _status = call.ArgAt<string>(2);
            return Task.FromResult<Dictionary<string, object>?>(Order());
        });

        var result = await _mirror.FollowAsync(Escrow("completed", ("deliveryConfirmedAt", "2026-10-02T10:00:00Z")), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        result.OrderStatus.Should().Be("settled");
        _hops.Should().HaveCount(3);
    }

    // ──────────────────────────────────────────────
    // Verdict escrows (V1)
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task ASettledFarmerWinsVerdict_RepairsTheOrderToDisputeResolved_NeverSettled(string status)
    {
        _status = status;
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>()).Returns(call =>
        {
            _status = call.ArgAt<Dictionary<string, object>>(1)["status"].ToString()!;
            _version++;
            return AuthResult<Dictionary<string, object>>.Success(Order());
        });
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object> { { "status", "disputed" }, { "orderId", OrderId } });
        _deliveries.TryRecordDisputeResolutionAsync(DeliveryId, Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var result = await _mirror.FollowAsync(
            Escrow("completed", ("disputeResolution", "farmer_wins"), ("disputeResolvedAt", "2026-10-05T10:00:00Z")), "sweep");

        result.Target.Should().Be(OrderMoneyTarget.Verdict);
        result.Outcome.Should().Be(OrderMoneyOutcome.Moved);
        _status.Should().Be("dispute_resolved");
        await _orders.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TheSweepsVerdictRepair_NeverCancelsOrRestocks_AnOrderBeforePickup()
    {
        // A buyer_wins refund already paid over an order still before pickup: only the refund settlement
        // may cancel it (and give its stock back); the repair reports it and writes nothing.
        _status = "accepted";
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "pending" }, { "orderId", OrderId }
        });
        var crops = Substitute.For<ICropRepository>();
        var mirror = new OrderMoneyMirror(_orders, _deliveries, _txAudit, new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops));

        var result = await mirror.FollowAsync(Escrow("refunded", ("disputeResolution", "buyer_wins")), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Mismatch);
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task AVerdictWhoseMoneyHasNotMoved_IsLeftToTheSettlement()
    {
        var result = await _mirror.FollowAsync(Escrow("dispute_resolved", ("disputeResolution", "farmer_wins")), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.NotApplicable);
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ALegacyVerdictWithoutADirection_IsLeftToAnAdmin()
        => (await _mirror.FollowAsync(Escrow("completed", ("disputeResolution", "partial_refund")), "sweep"))
            .Outcome.Should().Be(OrderMoneyOutcome.NotApplicable);

    [Fact]
    public async Task ARefundedBuyerWinsVerdict_OnACancelledOrder_IsConsistent()
    {
        _status = "cancelled";

        var result = await _mirror.FollowAsync(Escrow("refunded", ("disputeResolution", "buyer_wins")), "sweep");

        result.Outcome.Should().Be(OrderMoneyOutcome.Consistent);
        _hops.Should().BeEmpty();
    }

    [Fact]
    public async Task NoOrderRepository_IsNotApplicable()
    {
        var mirror = new OrderMoneyMirror(null, _deliveries, _txAudit, new DisputeVerdictMirror(null, _deliveries, _txAudit));

        (await mirror.FollowAsync(Escrow("completed"), "release")).Outcome.Should().Be(OrderMoneyOutcome.NotApplicable);
    }
}

/// <summary>ESC-09 (V4): the shared cron run budget.</summary>
public class CronRunBudgetTests
{
    private TimeSpan _elapsed = TimeSpan.Zero;

    [Fact]
    public void ASlice_StopsAtItsOwnLength_CountedFromWhereItStarted()
    {
        var budget = new CronRunBudget(() => _elapsed);
        _elapsed = TimeSpan.FromSeconds(100);
        var slice = budget.Slice(TimeSpan.FromSeconds(30));

        _elapsed = TimeSpan.FromSeconds(129);
        slice.MayStartRow(null).Should().BeTrue();
        _elapsed = TimeSpan.FromSeconds(130);
        slice.MayStartRow(null).Should().BeFalse();
        slice.StopReason.Should().Be("time");
    }

    [Fact]
    public void ASliceUntil_CountsFromTheRunStart_SoTheStepsBeforeTheSweepUseItUp()
    {
        // ESC-10's paged sweeps: the release sweep may start no row once the run is 90 s old, however
        // late it began.
        var budget = new CronRunBudget(() => _elapsed);
        _elapsed = TimeSpan.FromSeconds(80);
        var slice = budget.SliceUntil(EscrowCronFunction.ReleaseSweepUntil, EscrowCronFunction.SweepReserve);

        slice.Reserve.Should().Be(TimeSpan.FromSeconds(120));
        _elapsed = TimeSpan.FromSeconds(89);
        slice.MayStartRow(null).Should().BeTrue();
        _elapsed = TimeSpan.FromSeconds(90);
        slice.MayStartRow(null).Should().BeFalse();
        slice.StopReason.Should().Be("time");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(29, false)]
    [InlineData(31, true)]
    public void TheLambdaGuard_StopsBelowTheDefaultReserve_AndZeroTurnsItOff(int remainingSeconds, bool mayStart)
    {
        var slice = new CronRunBudget(() => _elapsed).Slice(TimeSpan.FromMinutes(5));
        var context = new Amazon.Lambda.TestUtilities.TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(remainingSeconds) };

        slice.MayStartRow(context).Should().Be(mayStart);
        if (!mayStart)
        {
            slice.StopReason.Should().Be("lambda");
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, false)]
    [InlineData(121, true)]
    public void EachSlice_KeepsItsOwnReserve(int remainingSeconds, bool mayStart)
    {
        // A sweep before the trucker retry keeps 120 s (its 90 s lock reserve plus 30 s); the run's
        // other slices keep theirs.
        var budget = new CronRunBudget(() => _elapsed);
        var early = budget.Slice(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(120));
        var last = budget.Slice(TimeSpan.FromMinutes(1));
        var context = new Amazon.Lambda.TestUtilities.TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(remainingSeconds) };

        early.Reserve.Should().Be(TimeSpan.FromSeconds(120));
        last.Reserve.Should().Be(CronRunBudget.DefaultReserve);
        early.MayStartRow(context).Should().Be(mayStart);
        last.MayStartRow(context).Should().BeTrue();
        CronRunBudget.IsLambdaTimeLow(context, TimeSpan.FromSeconds(120)).Should().Be(!mayStart);
    }
}

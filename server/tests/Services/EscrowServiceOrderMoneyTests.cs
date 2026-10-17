using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-09: the escrow service moves the order after the money (inline, never changing the
/// settlement's answer), the sweep's per-order reconcile, and Part B (an admin release or refund of
/// a plain escrowed order escrow checks the order first). Real conditions: OrderFollowsMoneyDynamoDbTests.
/// </summary>
public class EscrowServiceOrderMoneyTests
{
    private const string EscrowId = "escrow-order-ord-1";
    private const string OrderId = "ord-1";
    private const string DeliveryId = "del-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string AdminId = "admin-1";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly Dictionary<string, object> _orderExtra = new();
    private readonly List<string> _alarms = new();
    private readonly EscrowService _service;

    private readonly Dictionary<string, object> _escrow = new()
    {
        { "escrowId", EscrowId }, { "createdAt", CreatedAt }, { "status", "escrowed" }, { "source", "order" },
        { "orderId", OrderId }, { "deliveryId", DeliveryId }, { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" },
        { "amount", 1000m }, { "farmerPayout", 950m }, { "commission", 50m }, { "version", 1 },
        { "deliveryConfirmedAt", "2026-10-03T10:00:00.0000000Z" }
    };

    private static long _keys = 1_700_000_000_000;
    private string _orderStatus = "delivered";
    private int _orderVersion = 5;
    private string _deliveryStatus = "delivered";
    private readonly List<(string From, string To)> _hops = new();

    public EscrowServiceOrderMoneyTests()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(_ => new Dictionary<string, object>(_escrow));
        _escrowRepo.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(_ => new Dictionary<string, object>(_escrow));
        _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(call =>
            {
                _escrow["status"] = call.ArgAt<string>(2);
                foreach (var (key, value) in call.ArgAt<Dictionary<string, object>?>(4) ?? new())
                {
                    _escrow[key] = value;
                }

                return Task.CompletedTask;
            });
        _idempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _auditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "TB-REL-1" });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "TB-REF-1" });

        _orders.GetByIdAsync(OrderId, Arg.Any<bool>()).Returns(_ => Order());
        _orders.GetByIdAsync(OrderId).Returns(_ => Order());
        _orders.TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            if (call.ArgAt<string>(1) != _orderStatus)
            {
                return Task.FromResult<Dictionary<string, object>?>(null);
            }

            _hops.Add((_orderStatus, call.ArgAt<string>(2)));
            _orderStatus = call.ArgAt<string>(2);
            _orderVersion++;
            return Task.FromResult<Dictionary<string, object>?>(Order());
        });
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>()).Returns(call =>
        {
            if (call.ArgAt<Dictionary<string, object>>(1).TryGetValue("status", out var status))
            {
                _orderStatus = status.ToString()!;
            }

            _orderVersion++;
            return AuthResult<Dictionary<string, object>>.Success(Order());
        });
        _deliveries.GetByIdAsync(DeliveryId, Arg.Any<bool>()).Returns(_ => new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", _deliveryStatus }
        });
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(_ =>
        {
            _deliveryStatus = "cancelled";
            return true;
        });

        _crops.RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(StockRestoreResult.Restored);

        _service = new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, userRepo: null,
            orderRepo: _orders, deliveryRepo: _deliveries, transactionAuditRepo: _txAudit, cropRepo: _crops)
        {
            AlarmLog = _alarms.Add
        };
    }

    private Dictionary<string, object> Order()
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "status", _orderStatus }, { "version", _orderVersion }, { "activeDeliveryId", DeliveryId }
        };
        foreach (var (key, value) in _orderExtra)
        {
            order[key] = value;
        }

        return order;
    }

    private ReleaseEscrowRequest Release(string trigger = "delivery_confirmed") => new()
    {
        EscrowId = EscrowId,
        Trigger = trigger,
        DeliveryId = DeliveryId,
        IdempotencyKey = $"system:{EscrowId}:release:{Interlocked.Increment(ref _keys)}"
    };

    private RefundEscrowRequest Refund(bool overrideMismatch = false) => new()
    {
        EscrowId = EscrowId,
        Trigger = "admin_override",
        Reason = overrideMismatch ? "The goods were lost on the road." : null,
        IdempotencyKey = $"admin:{EscrowId}:refund:{Interlocked.Increment(ref _keys)}",
        OverrideOrderMismatch = overrideMismatch
    };

    // ──────────────────────────────────────────────
    // Inline follow after the money
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ASystemRelease_SettlesTheDeliveredOrder_AfterTheEscrowIsCompleted()
    {
        var result = await _service.ReleaseAsync(Release(), "system");

        result.IsSuccess.Should().BeTrue();
        _escrow["status"].Should().Be("completed");
        _hops.Should().Equal(("delivered", "disbursing"), ("disbursing", "settled"));
    }

    [Fact]
    public async Task AQueuedRelease_MovesTheOrderToDisbursing_Only()
    {
        _gateway.Configure().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported
        });
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
                _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
                return true;
            });

        var result = await _service.ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_034");
        _escrow["status"].Should().Be("escrowed");
        _hops.Should().Equal(("delivered", "disbursing"));
    }

    [Fact]
    public async Task AFollowThatFails_NeverChangesTheReleaseAnswer()
    {
        _orders.Configure().TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("throttled"));

        var result = await _service.ReleaseAsync(Release(), "system");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("completed");
        _orderStatus.Should().Be("delivered");
    }

    [Fact]
    public async Task AGatewayRefusal_LeavesTheOrderAlone()
    {
        _gateway.Configure().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_REFUSED" });

        var result = await _service.ReleaseAsync(Release(), "system");

        result.IsSuccess.Should().BeFalse();
        await _orders.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ──────────────────────────────────────────────
    // Part B: admin release
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("accepted", "pending")]
    [InlineData("funded", "pending")]
    [InlineData("in_transit", "inTransit")]
    [InlineData("cancelled", "delivered")]
    [InlineData("disputed", "delivered")]
    public async Task AnAdminRelease_OfAnOrderNotDelivered_IsRefused_NothingPaid(string orderStatus, string deliveryStatus)
    {
        _orderStatus = orderStatus;
        _deliveryStatus = deliveryStatus;

        var result = await _service.ReleaseAsync(Release("admin_override"), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AnAdminRelease_OfALaggingOrderBehindADeliveredDelivery_AdvancesItFirst_ThenSettles()
    {
        _orderStatus = "in_transit";

        var result = await _service.ReleaseAsync(Release("admin_override"), AdminId, "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        _hops.Should().Equal(("in_transit", "delivered"), ("delivered", "disbursing"), ("disbursing", "settled"));
    }

    [Fact]
    public async Task AnAdminRelease_WhoseOrderCannotBeRead_Is503_NothingPaid()
    {
        _orders.Configure().GetByIdAsync(OrderId, true).ThrowsAsync(new InvalidOperationException("down"));

        var result = await _service.ReleaseAsync(Release("admin_override"), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(503);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task TheSystemSweep_IsNeverGatedHere()
    {
        // ESC-10 owns the sweep's trade check; the service checks only admin callers.
        _orderStatus = "accepted";
        _deliveryStatus = "pending";

        var result = await _service.ReleaseAsync(Release(), "system");

        result.IsSuccess.Should().BeTrue();
    }

    // ──────────────────────────────────────────────
    // Part B: admin refund
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("funded", "pending")]
    [InlineData("accepted", "pending")]
    public async Task AnAdminRefund_BeforePickup_CancelsTheOrderFirst_SchedulesTheRefund_ThenRefunds(string status, string deliveryStatus)
    {
        _orderStatus = status;
        _deliveryStatus = deliveryStatus;

        var result = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        _orderStatus.Should().Be("cancelled");
        _escrow["status"].Should().Be("refunded");
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null && f["refundReason"].ToString() == "admin_refund"));
        await _orders.Received(1).UpdateAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f["status"].ToString() == "cancelled" && f["cancelReason"].ToString() == "escrow_refunded"),
            Arg.Any<int>());
    }

    [Theory]
    [InlineData("in_transit")]
    [InlineData("pickup_confirmed")]
    [InlineData("delivered")]
    public async Task AnAdminRefund_OfGoodsOnTheMove_IsRefusedWithoutAnOverride(string status)
    {
        _orderStatus = status;
        _deliveryStatus = "inTransit";

        var result = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Message.Should().Contain("overrideOrderMismatch");
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AnAdminRefund_OfGoodsOnTheMove_WithAnOverride_RefundsAndFlagsTheOrder_StatusLeft()
    {
        _orderStatus = "in_transit";
        _deliveryStatus = "inTransit";

        var result = await _service.RefundAsync(Refund(overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        _orderStatus.Should().Be("in_transit");
        await _orders.Received(1).UpdateAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.ContainsKey(OrderMoneyFields.RefundOverrideAt) && !f.ContainsKey("status")),
            Arg.Any<int>());
        await _auditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            a["action"].ToString() == "refund_order_mismatch_overridden"));
    }

    [Theory]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("payment_submitted")]
    public async Task AnAdminRefund_OfAnOrderPastOrOutsideTheTrade_IsRefused_EvenWithAnOverride(string status)
    {
        _orderStatus = status;

        var result = await _service.RefundAsync(Refund(overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AnAdminRefund_OfARejectedOrder_GoesAhead()
    {
        _orderStatus = "rejected";

        (await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ASystemRefund_IsNeverGatedHere()
    {
        // The auto-refund sweep cancels the order itself first (ESC-06); the service gates only admins.
        _orderStatus = "accepted";
        _deliveryStatus = "pending";
        var request = Refund() with { Trigger = "delivery_timeout", IdempotencyKey = $"system:{EscrowId}:refund:1" };

        (await _service.RefundAsync(request, "system")).IsSuccess.Should().BeTrue();
        _orderStatus.Should().Be("accepted");
    }

    // ──────────────────────────────────────────────
    // The sweep's per-order reconcile
    // ──────────────────────────────────────────────

    private Dictionary<string, object> IndexRow() => new() { { "orderId", OrderId }, { "status", _orderStatus }, { "activeDeliveryId", DeliveryId } };

    [Fact]
    public async Task Reconcile_ACompletedEscrowBehindADeliveredOrder_SettlesIt()
    {
        _escrow["status"] = "completed";

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Settled);
        _orderStatus.Should().Be("settled");
    }

    [Fact]
    public async Task Reconcile_AMismatch_AlarmsOnce_AndMarksTheOrder()
    {
        _escrow["status"] = "refunded";

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Mismatch);
        _auditRepo.Configure().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(false);
        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Mismatch);

        _alarms.Should().ContainSingle(a => a.StartsWith("ORDER_MONEY_MISMATCH ") && a.Contains("reason=cannot_follow"));
        await _auditRepo.Received().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(a =>
            a["action"].ToString() == OrderMoneyMirror.MismatchAuditAction
            && a["timestamp"].ToString() == "2026-10-01T10:00:00.0000007Z"));
        await _orders.Received(2).TryMarkMoneySweepSkipAsync(OrderId, "delivered", Arg.Any<string>());
    }

    [Fact]
    public async Task Reconcile_AnOrderAheadOfHeldMoney_UsesItsOwnAuditSlot()
    {
        _orderStatus = "disbursing";

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Mismatch);

        _alarms.Should().ContainSingle(a => a.Contains("reason=order_ahead"));
        await _auditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(a =>
            a["action"].ToString() == OrderMoneyMirror.AheadAuditAction
            && a["timestamp"].ToString() == "2026-10-01T10:00:00.0000008Z"));
    }

    [Fact]
    public async Task Reconcile_HeldMoney_IsWaiting_AndNeverMarked()
    {
        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Waiting);
        await _orders.DidNotReceive().TryMarkMoneySweepSkipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task Reconcile_AWriteThatFails_AlarmsOnlyOnceTheMoneyIsTwoHoursOld(int hoursAgo, bool alarms)
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        _escrow["status"] = "completed";
        _escrow["completedAt"] = now.AddHours(-hoursAgo).ToString("o");
        _orders.Configure().TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("x"));

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), now)).Should().Be(OrderMoneySweepOutcome.Failed);

        _alarms.Any(a => a.Contains("reason=write_failed")).Should().Be(alarms);
        await _orders.DidNotReceive().TryMarkMoneySweepSkipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Reconcile_AnOrderWithoutAnyEscrow_IsMarked()
    {
        _escrowRepo.Configure().GetByIdAsync(EscrowId, true).Returns((Dictionary<string, object>?)null);

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.NoEscrow);
        await _orders.Received(1).TryMarkMoneySweepSkipAsync(OrderId, "delivered", Arg.Any<string>());
    }

    [Fact]
    public async Task Reconcile_PrefersTheEscrowFurthestAlong_WhenTheOrderEscrowWasRefunded()
    {
        // A legacy order: its own escrow refunded, a delivery escrow released after.
        _escrow["status"] = "refunded";
        _escrowRepo.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>>
        {
            new() { { "escrowId", "escrow-legacy" }, { "status", "completed" } }
        });
        _escrowRepo.GetByIdAsync("escrow-legacy", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-legacy" }, { "status", "completed" }, { "orderId", OrderId }, { "createdAt", CreatedAt }
        });

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Settled);
    }

    // ──────────────────────────────────────────────
    // Review follow-ups
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AFollowThatThrowsOnAQueuedRelease_KeepsTheQueuedAnswer()
    {
        _gateway.Configure().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported
        });
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
                _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
                return true;
            });
        _orders.Configure().TryAdvanceStatusAsync(OrderId, Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("throttled"));

        var result = await _service.ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_034");
        _orderStatus.Should().Be("delivered");
    }

    [Fact]
    public async Task AStaleReleaseLockParked_MovesTheOrderToDisbursing()
    {
        _escrow[SettlementLockFields.Lock] = ManualPayoutFields.OpRelease;
        _escrow[SettlementLockFields.LockAt] = "2026-10-01T00:00:00.0000000Z";
        _escrow[SettlementLockFields.LockId] = "lock-1";
        _escrowRepo.TryQueueStaleSettlementAsync(EscrowId, CreatedAt, ManualPayoutFields.OpRelease, "escrowed",
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), "lock-1", Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
                _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
                return true;
            });

        var outcome = await _service.ParkStaleSettlementAsync(new Dictionary<string, object>(_escrow), DateTime.UtcNow);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        _hops.Should().Equal(("delivered", "disbursing"));
    }

    [Fact]
    public async Task AnAdminRefund_WhoseCancelDoesNotLand_Is503_NothingRefunded()
    {
        _orderStatus = "funded";
        _deliveryStatus = "pending";
        _orders.Configure().UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(500, "SYNC_ERROR", "down"));

        var result = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(503);
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        _escrow.Should().NotContainKey("refundReason");
    }

    [Theory]
    [InlineData("escrow_refunded")]
    [InlineData("transport_failed_buyer_cancelled")]
    public async Task AnAdminRefund_OfACancelledOrderWhoseStockIsNotBack_RetriesTheRestockFirst(string cancelReason)
    {
        _orderStatus = "cancelled";
        _orderExtra["cancelReason"] = cancelReason;
        _orderExtra["listingId"] = "crop-1";
        _orderExtra["quantity"] = "500";
        _crops.Configure().RestoreStockAsync("crop-1", OrderId, 5.0).Returns(StockRestoreResult.Failed);

        var refused = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true);

        refused.Error!.HttpStatus.Should().Be(503);
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());

        _crops.Configure().RestoreStockAsync("crop-1", OrderId, 5.0).Returns(StockRestoreResult.Restored);
        (await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AnAdminRefund_WhoseOrderWasDisputedMeanwhile_IsRefused_EvenWithAnOverride()
    {
        // Read as accepted, disputed by the time the cancel-before-refund reads it again.
        _orderStatus = "accepted";
        _deliveryStatus = "pending";
        var reads = 0;
        _orders.Configure().GetByIdAsync(OrderId, true).Returns(_ =>
        {
            if (++reads == 2)
            {
                _orderStatus = "disputed";
            }

            return Order();
        });

        var result = await _service.RefundAsync(Refund(overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Message.Should().NotContain("overrideOrderMismatch");
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task Reconcile_AnOrderBeforeDeliveryWithoutAnEscrow_IsNotMarked()
    {
        _orderStatus = "accepted";
        _escrowRepo.Configure().GetByIdAsync(EscrowId, true).Returns((Dictionary<string, object>?)null);

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.NoEscrow);
        await _orders.DidNotReceive().TryMarkMoneySweepSkipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Reconcile_AnOrderThatOnlyLooksAheadOfAStaleEscrowCopy_IsNotAlarmed()
    {
        // The sweep read the escrow released; a manual close completed it and settled the order
        // before the order was read.
        _orderStatus = "settled";
        _escrow["status"] = "released";
        _escrow[TruckerPayoutFields.Pending] = true;
        var reads = 0;
        _escrowRepo.Configure().GetByIdAsync(EscrowId, true).Returns(_ =>
        {
            if (++reads == 2)
            {
                _escrow["status"] = "completed";
                _escrow.Remove(TruckerPayoutFields.Pending);
            }

            return new Dictionary<string, object>(_escrow);
        });

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Waiting);
        _alarms.Should().BeEmpty();
        await _auditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
    }

    // ──────────────────────────────────────────────
    // rev-esc-09: each writer's own follow, and Part B's fail-closed edges
    // ──────────────────────────────────────────────

    private EscrowService ServiceWithUsers(IUserRepository users) => new(
        _escrowRepo, _auditRepo, _idempotencyRepo, _gateway, users, _orders, _deliveries,
        transactionAuditRepo: _txAudit, cropRepo: _crops)
    {
        AlarmLog = _alarms.Add
    };

    private static UserDto Trucker() => new()
    {
        UserId = "trucker-1",
        PhoneNumber = "+251911000002",
        DisplayName = "Trucker",
        Role = "trucker",
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        FaydaVerified = true,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    [Fact]
    public async Task AnOverrideRefund_WhoseAuditWriteFails_Is503_AndNeverReachesTheGateway()
    {
        _orderStatus = "in_transit";
        _deliveryStatus = "inTransit";
        _auditRepo.Configure().AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
                a["action"].ToString() == "refund_order_mismatch_overridden"))
            .ThrowsAsync(new InvalidOperationException("audit table down"));

        var result = await _service.RefundAsync(Refund(overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(503);
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _orders.DidNotReceive().UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task AnAdminRelease_OverAMissingOrder_Is422_NothingPaid()
    {
        _orders.Configure().GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);

        var result = await _service.ReleaseAsync(Release("admin_override"), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task AReleaseWhoseTruckerLegIsQueued_MovesTheOrderToDisbursing()
    {
        _escrow["truckerId"] = "trucker-1";
        _escrow["truckerPayout"] = 100m;
        _gateway.Configure().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
            call.Arg<ReleaseFundsRequest>().TransactionRef.EndsWith(":trucker", StringComparison.Ordinal)
                ? new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported }
                : new ReleaseFundsResponse { Success = true, TelebirrRef = "TB-REL-1" });
        _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
                _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
                return true;
            });

        var result = await _service.ReleaseAsync(Release(), "system");

        result.Error!.Code.Should().Be("ESCROW_034");
        _hops.Should().Equal(("delivered", "disbursing"));
    }

    [Fact]
    public async Task TheTruckerRetryThatPaysTheLastLeg_SettlesTheOrder()
    {
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("trucker-1").Returns(Trucker());
        _orderStatus = "disbursing";
        _escrow["status"] = "released";
        _escrow["truckerId"] = "trucker-1";
        _escrow["truckerPayout"] = 100m;
        _escrow[TruckerPayoutFields.Pending] = true;
        _escrow[TruckerPayoutFields.PendingSince] = "2026-10-05T10:00:00.0000000Z";
        _escrowRepo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt).Returns("lock-t");
        _escrowRepo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, "lock-t", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow["status"] = "completed";
                _escrow.Remove(TruckerPayoutFields.Pending);
                return true;
            });

        var result = await ServiceWithUsers(users).RetryTruckerLegAsync(new Dictionary<string, object>(_escrow), DateTime.UtcNow, true);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        _hops.Should().Equal(("disbursing", "settled"));
    }

    [Fact]
    public async Task TheLegacyTruckerPark_MovesTheOrderToDisbursing()
    {
        _escrow["status"] = "completed";
        _escrow["truckerId"] = "trucker-1";
        _escrow["truckerPayout"] = 100m;
        _escrow[TruckerPayoutFields.Pending] = true;
        _escrow["completedAt"] = "2026-10-03T10:00:00.0000000Z";
        _escrowRepo.TryParkLegacyTruckerLegAsync(EscrowId, CreatedAt, "completed",
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _escrow["status"] = "released";
                _escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
                _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
                return true;
            });

        (await _service.ParkLegacyTruckerLegAsync(new Dictionary<string, object>(_escrow))).Should().BeTrue();

        _hops.Should().Equal(("delivered", "disbursing"));
    }

    [Fact]
    public async Task AReplayOfARecordedLeg_RepairsAnOrderTheCloseCouldNotMove()
    {
        // The close completed the escrow but its follow did not land: the order is still delivered.
        _escrow["status"] = "completed";
        _escrow["completedAt"] = "2026-10-05T10:00:00.0000001Z";
        _escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRelease;
        _escrow[ManualPayoutFields.FromStatus] = "escrowed";
        _escrow[ManualPayoutFields.StatusAttr(ManualPayoutFields.LegFarmer)] = ManualPayoutFields.LegPaid;
        _escrow[ManualPayoutFields.RefAttr(ManualPayoutFields.LegFarmer)] = "TB-FARMER-0001";
        _escrow[ManualPayoutFields.PaidAtAttr(ManualPayoutFields.LegFarmer)] = "2026-10-05T10:00:00.0000000Z";
        _escrow[ManualPayoutFields.PaidByAttr(ManualPayoutFields.LegFarmer)] = AdminId;

        var result = await _service.CompleteManualPayoutAsync(EscrowId, ManualPayoutFields.LegFarmer, "TB-FARMER-0001", AdminId, true);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AlreadyRecorded.Should().BeTrue();
        _hops.Should().Equal(("delivered", "disbursing"), ("disbursing", "settled"));
    }

    [Fact]
    public async Task ACancelLostToATransportFailureCancel_StillRetriesTheRestockFirst()
    {
        // Read accepted (pickup deadline passed); the buyer's transport-failure cancel lands between the
        // read and Part B's cancel, and its own restock did not land. (rev-esc-09's probe.)
        _orderStatus = "accepted";
        _deliveryStatus = "pending";
        _orderExtra["listingId"] = "crop-1";
        _orderExtra["quantity"] = "500";
        _crops.Configure().RestoreStockAsync("crop-1", OrderId, 5.0).Returns(StockRestoreResult.Failed);
        _orders.Configure().UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>()).Returns(_ =>
        {
            _orderStatus = "cancelled";
            _orderExtra["cancelReason"] = "transport_failed_buyer_cancelled";
            return AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "version");
        });

        var refused = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true);

        refused.Error!.HttpStatus.Should().Be(503);
        await _crops.Received().RestoreStockAsync("crop-1", OrderId, 5.0);
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());

        // The admin's retry: the stock goes back, then the refund.
        _crops.Configure().RestoreStockAsync("crop-1", OrderId, 5.0).Returns(StockRestoreResult.Restored);
        (await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: true)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reconcile_AMismatchBeforeDelivery_AlarmsOnce_ButStaysInTheSweep()
    {
        // A payout behind an order whose delivery is not confirmed yet: a later confirmation can still fix it.
        _orderStatus = "in_transit";
        _deliveryStatus = "inTransit";
        _escrow["status"] = "completed";
        _escrow.Remove("deliveryConfirmedAt");

        (await _service.ReconcileOrderMoneyAsync(IndexRow(), DateTime.UtcNow)).Should().Be(OrderMoneySweepOutcome.Mismatch);

        _alarms.Should().ContainSingle(a => a.Contains("reason=cannot_follow"));
        await _orders.DidNotReceive().TryMarkMoneySweepSkipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}

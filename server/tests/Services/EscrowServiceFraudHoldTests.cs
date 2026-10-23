using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-03 (fraud-detection-architecture.md section 18): the escrow <c>held</c> status in the escrow service.
/// The hold write and its audit rows, the admin-group exits with a reason, the order side of a refund by
/// <c>heldFor</c> (18.3), the settlement lock and the active set. The sweeps that write the hold are in
/// <c>EscrowCronFraudHoldTests</c>; the conditions DynamoDB evaluates are in <c>FraudHoldDynamoDbTests</c>.
/// </summary>
public class EscrowServiceFraudHoldTests
{
    private const string OrderId = "ord-1";
    private const string EscrowId = "escrow-order-ord-1";
    private const string DeliveryId = "del-1";
    private const string CreatedAt = "2026-10-10T10:00:00.0000000Z";
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string AdminId = "admin-1";
    private const string Reason = "Seller confirmed as fraudulent by support.";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly List<string> _alarms = new();
    private readonly List<string> _orderWrites = new();
    private readonly EscrowService _service;

    public EscrowServiceFraudHoldTests()
    {
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "TB-REL-1" });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "TB-REF-1" });
        _idempotency.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _users.GetUserByIdAsync(BuyerId).Returns(User(BuyerId, "buyer"));
        _users.GetUserByIdAsync(FarmerId).Returns(User(FarmerId, "farmer"));
        _deliveries.TryMarkDisputedAsync(DeliveryId, Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _deliveries.TryRecordDisputeResolutionAsync(DeliveryId, "buyer_wins", Arg.Any<string>()).Returns(true);
        _crops.RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(StockRestoreResult.Restored);

        _service = new EscrowService(_escrows, _audit, _idempotency, _gateway, _users, _orders, _deliveries,
            transactionAuditRepo: _txAudit, cropRepo: _crops)
        {
            AlarmLog = _alarms.Add
        };
    }

    private static UserDto User(string userId, string role, bool suspended = false) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900000001",
        Role = role,
        Woreda = "w",
        IsPhoneVerified = true,
        FaydaVerified = true,
        Suspended = suspended,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    /// <summary>An escrow held by a sweep: an order escrow by default (source order, the order's deterministic id).</summary>
    private static Dictionary<string, object> Held(
        string heldFor, string from = "escrowed", bool orderEscrow = true, bool withOrder = true, string? resolution = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", orderEscrow ? EscrowId : "escrow-initiate-1" },
            { "createdAt", CreatedAt },
            { "status", "held" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "farmerPayout", 7_800m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-1" },
            { "version", 3 },
            { "deliveryId", DeliveryId },
            { "holdReason", "account_suspended" },
            { "heldAt", "2026-10-20T10:00:00.0000000Z" },
            { "heldBy", "system" },
            { "heldFromStatus", from },
            { "heldFor", heldFor }
        };
        if (orderEscrow)
        {
            escrow["source"] = "order";
        }

        if (withOrder)
        {
            escrow["orderId"] = OrderId;
        }

        if (resolution != null)
        {
            escrow["disputeResolution"] = resolution;
            escrow["disputeResolvedAt"] = "2026-10-19T10:00:00.0000000Z";
        }

        return escrow;
    }

    private void EscrowIs(Dictionary<string, object> escrow)
    {
        _escrows.GetByIdAsync((string)escrow["escrowId"]).Returns(escrow);
        _escrows.GetByIdAsync((string)escrow["escrowId"], Arg.Any<bool>()).Returns(escrow);
    }

    private static Dictionary<string, object> Order(string status, int version, string? activeDeliveryId = DeliveryId)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "createdAt", CreatedAt }, { "status", status }, { "version", version },
            { "listingId", "listing-1" }, { "quantity", "500" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }
        };
        if (activeDeliveryId != null)
        {
            order["activeDeliveryId"] = activeDeliveryId;
        }

        return order;
    }

    private void OrderIs(string status, int version = 4, string? activeDeliveryId = DeliveryId)
    {
        _orders.GetByIdAsync(OrderId, true).Returns(Order(status, version, activeDeliveryId));
        _orders.GetByIdAsync(OrderId).Returns(Order(status, version, activeDeliveryId));
    }

    private void DeliveryIs(string status, string? resolution = null)
    {
        var delivery = new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", status }, { "orderId", OrderId } };
        if (resolution != null)
        {
            delivery["disputeResolution"] = resolution;
        }

        _deliveries.GetByIdAsync(DeliveryId, Arg.Any<bool>()).Returns(delivery);
        _deliveries.GetByIdAsync(DeliveryId).Returns(delivery);
    }

    /// <summary>Every order write succeeds and returns the order at the new status and version.</summary>
    private void OrderWritesSucceed()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(call =>
            {
                var status = ((Dictionary<string, object>)call[1])["status"].ToString()!;
                _orderWrites.Add(status);
                return AuthResult<Dictionary<string, object>>.Success(Order(status, (int)call[2] + 1));
            });
    }

    private static ReleaseEscrowRequest Release(string? reason = Reason) => new()
    {
        EscrowId = EscrowId,
        Trigger = "admin_override",
        DeliveryId = DeliveryId,
        IdempotencyKey = $"{AdminId}:{EscrowId}:release:20261020",
        Reason = reason
    };

    private static RefundEscrowRequest Refund(string escrowId = EscrowId, string? reason = Reason) => new()
    {
        EscrowId = escrowId,
        Trigger = "admin_override",
        Reason = reason,
        IdempotencyKey = $"{AdminId}:{escrowId}:refund:20261020"
    };

    private Task<EscrowResult<ReleaseEscrowResponse>> AdminRelease(ReleaseEscrowRequest? request = null)
        => _service.ReleaseAsync(request ?? Release(), AdminId, "admin", adminGroupVerified: true);

    private Task<EscrowResult<RefundEscrowResponse>> AdminRefund(RefundEscrowRequest? request = null)
        => _service.RefundAsync(request ?? Refund(), AdminId, "admin", adminGroupVerified: true);

    private async Task NothingMovedAsync()
    {
        await _escrows.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    private static string? S(Dictionary<string, object> entry, string key)
        => entry.TryGetValue(key, out var value) ? value?.ToString() : null;

    private Task DecisionRow(string action)
        => _audit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            S(e, "action") == action && S(e, "actor") == AdminId
            && S(e, "fromState") == "held" && S(e, "toState") == "held"
            && (S(e, "metadata") ?? "").Contains($"reason={Reason}")
            && (S(e, "metadata") ?? "").Contains("holdReason=account_suspended")));

    // ──────────────────────────────────────────────
    // The hold write (section 18.2)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HoldForReview_WritesTheHold_ThenOneFraudHoldRowInEachAudit()
    {
        var escrow = Held("release");
        escrow["status"] = "escrowed";
        EscrowHoldRequest? sent = null;
        _escrows.TryHoldAsync(EscrowId, CreatedAt, Arg.Do<EscrowHoldRequest>(h => sent = h), null)
            .Returns(EscrowHoldWrite.Held);
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);

        var outcome = await _service.HoldForReviewAsync(escrow, "escrowed", "release", "account_suspended", "farmer", "auto-release");

        outcome.Should().Be(EscrowHoldWrite.Held);
        sent.Should().NotBeNull();
        sent!.FromStatus.Should().Be("escrowed");
        sent.HeldFor.Should().Be("release");
        sent.HoldReason.Should().Be("account_suspended");
        DateTime.TryParse(sent.HeldAt, out _).Should().BeTrue();
        // The audit row sits at the hold time, written only if absent.
        await _audit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            S(e, "action") == "fraud_hold" && S(e, "fromState") == "escrowed"
            && S(e, "toState") == "held" && S(e, "actor") == "system"
            && S(e, "timestamp") == sent.HeldAt
            && S(e, "metadata") == "holdReason=account_suspended;heldFor=release;party=farmer;sweep=auto-release"));
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            S(e, "action") == "fraud_hold" && S(e, "entityType") == "escrow"
            && S(e, "entityId") == EscrowId && S(e, "transactionId") == OrderId
            && S(e, "fromState") == "escrowed" && S(e, "toState") == "held"));
    }

    [Theory]
    [InlineData(EscrowHoldWrite.EscrowChanged)]
    [InlineData(EscrowHoldWrite.OrderChanged)]
    public async Task HoldForReview_ThatLostItsCondition_WritesNothingElse(EscrowHoldWrite lost)
    {
        var fence = new OrderFence("orders", OrderId, CreatedAt, 4, "funded");
        _escrows.TryHoldAsync(EscrowId, CreatedAt, Arg.Any<EscrowHoldRequest>(), fence).Returns(lost);

        var outcome = await _service.HoldForReviewAsync(Held("refund"), "escrowed", "refund", "account_suspended", "buyer", "auto-refund", fence);

        outcome.Should().Be(lost);
        await _audit.DidNotReceiveWithAnyArgs().AppendIfAbsentAsync(default!);
        await _txAudit.DidNotReceiveWithAnyArgs().AppendAsync(default!);
    }

    [Fact]
    public async Task HoldForReview_AnAuditWriteThatFails_KeepsTheHold()
    {
        _escrows.TryHoldAsync(EscrowId, CreatedAt, Arg.Any<EscrowHoldRequest>(), null).Returns(EscrowHoldWrite.Held);
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new InvalidOperationException("ddb"));

        var outcome = await _service.HoldForReviewAsync(Held("release"), "escrowed", "release", "account_suspended", "farmer", "auto-release");

        outcome.Should().Be(EscrowHoldWrite.Held);
    }

    // ──────────────────────────────────────────────
    // Exits: who, with what (section 18.3)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AdminRelease_FromHeld_PaysTheFarmer_WithTheDecisionOnRecordBeforeTheLock()
    {
        EscrowIs(Held("release"));
        OrderIs("delivered");
        DeliveryIs("delivered");

        var result = await AdminRelease();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Status.Should().Be("completed");
        await DecisionRow("fraud_hold_release");
        Received.InOrder(() =>
        {
            _audit.AppendAsync(Arg.Is<Dictionary<string, object>>(e => S(e, "action") == "fraud_hold_release"));
            _escrows.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "held", true);
            _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        });
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "held", Arg.Any<Dictionary<string, object>>());
        await _audit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            S(e, "action") == "release" && S(e, "fromState") == "held" && S(e, "toState") == "released"));
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            S(e, "action") == "fraud_hold_release" && S(e, "entityId") == EscrowId
            && (S(e, "metadata") ?? "").Contains($"reason={Reason}")));
    }

    [Fact]
    public async Task AdminRefund_FromHeld_ByAProfileAdminOutsideTheAdminGroup_Is403_NothingMoved()
    {
        EscrowIs(Held("refund"));
        _users.GetUserByIdAsync(AdminId).Returns(User(AdminId, "admin"));

        var refund = await _service.RefundAsync(Refund(), AdminId, "admin", adminGroupVerified: false);
        var release = await _service.ReleaseAsync(Release(), AdminId, "admin", adminGroupVerified: false);

        refund.Error!.HttpStatus.Should().Be(403);
        refund.Error.Code.Should().Be("ESCROW_021");
        release.Error!.HttpStatus.Should().Be(403);
        release.Error.Code.Should().Be("ESCROW_015");
        await NothingMovedAsync();
        await _audit.DidNotReceiveWithAnyArgs().AppendAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   too short ")]
    public async Task AdminExit_FromHeld_WithoutAReasonOfTenCharacters_Is400_NothingMoved(string? reason)
    {
        EscrowIs(Held("refund"));

        var refund = await AdminRefund(Refund(reason: reason));
        var release = await AdminRelease(Release(reason));

        foreach (var error in new[] { refund.Error, release.Error })
        {
            error!.HttpStatus.Should().Be(400);
            error.Code.Should().Be("ESCROW_VALIDATION");
            error.Field.Should().Be("reason");
        }

        await NothingMovedAsync();
    }

    [Fact]
    public async Task TheSystemCaller_CanNeverSettleAHeldEscrow()
    {
        EscrowIs(Held("release"));

        var release = await _service.ReleaseAsync(Release(), "system");
        var refund = await _service.RefundAsync(Refund(), "system");

        release.Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        release.Error.HttpStatus.Should().Be(422);
        refund.Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        await NothingMovedAsync();
    }

    [Fact]
    public async Task AdminExit_ADecisionRowThatCannotBeWritten_Is503_NothingMoved()
    {
        EscrowIs(Held("refund", orderEscrow: false, withOrder: false));
        _audit.AppendAsync(Arg.Is<Dictionary<string, object>>(e => S(e, "action") == "fraud_hold_refund"))
            .ThrowsAsync(new InvalidOperationException("ddb"));

        var result = await AdminRefund(Refund("escrow-initiate-1"));

        result.Error!.HttpStatus.Should().Be(503);
        result.Error.Code.Should().Be("ESCROW_012");
        await NothingMovedAsync();
    }

    [Theory]
    [InlineData("release", "delivered")]
    [InlineData("refund", "funded")]
    public async Task AdminRefund_ADecisionRowThatCannotBeWritten_LeavesTheOrderAsItIs(string heldFor, string orderStatus)
    {
        // The decision is recorded before the order is moved (dispute path) or cancelled (refund rules).
        EscrowIs(Held(heldFor));
        OrderIs(orderStatus, 3);
        DeliveryIs("delivered");
        OrderWritesSucceed();
        _audit.AppendAsync(Arg.Is<Dictionary<string, object>>(e => S(e, "action") == "fraud_hold_refund"))
            .ThrowsAsync(new InvalidOperationException("ddb"));

        var result = await AdminRefund();

        result.Error!.HttpStatus.Should().Be(503);
        _orderWrites.Should().BeEmpty();
        await _deliveries.DidNotReceiveWithAnyArgs().TryMarkDisputedAsync(default!, default!, default);
        await _crops.DidNotReceiveWithAnyArgs().RestoreStockAsync(default!, default!, default);
        await NothingMovedAsync();
    }

    [Fact]
    public async Task AdminRelease_ADecisionRowThatCannotBeWritten_DoesNotAdvanceALaggingOrder()
    {
        EscrowIs(Held("release"));
        OrderIs("in_transit", 2);
        DeliveryIs("delivered");
        _audit.AppendAsync(Arg.Is<Dictionary<string, object>>(e => S(e, "action") == "fraud_hold_release"))
            .ThrowsAsync(new InvalidOperationException("ddb"));

        var result = await AdminRelease();

        result.Error!.HttpStatus.Should().Be(503);
        await _orders.DidNotReceiveWithAnyArgs().TryAdvanceStatusAsync(default!, default!, default!);
        await NothingMovedAsync();
    }

    // ──────────────────────────────────────────────
    // Refund, heldFor: refund (the auto-refund sweep held it)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HeldForRefund_AFundedOrder_IsCancelledAndRestocked_ThenRefunded_FromHeld()
    {
        OrderIs("funded", activeDeliveryId: null);
        var escrow = Held("refund");
        escrow.Remove("deliveryId");
        EscrowIs(escrow);
        OrderWritesSucceed();

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        _orderWrites.Should().Equal("cancelled");
        await _crops.Received(1).RestoreStockAsync("listing-1", OrderId, 5.0);
        Received.InOrder(() =>
        {
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
            _escrows.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", "held", true);
            _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        });
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "held", Arg.Any<Dictionary<string, object>>());
        // The refund is not armed on the escrow for a sweep: no sweep selects a held escrow.
        await _escrows.DidNotReceive().UpdateStatusAsync(EscrowId, CreatedAt, "escrowed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await DecisionRow("fraud_hold_refund");
    }

    [Fact]
    public async Task HeldForRefund_ARejectedOrder_IsRefundedAsItIs()
    {
        EscrowIs(Held("refund"));
        OrderIs("rejected");

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("settled")]
    public async Task HeldForRefund_AnOrderThatMovedOn_IsRefused_WithTheMismatchAlarm_NothingMoved(string orderStatus)
    {
        EscrowIs(Held("refund"));
        OrderIs(orderStatus);

        var result = await AdminRefund();

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        _alarms.Should().ContainSingle(l => l.StartsWith("RISK_HOLD_ORDER_MISMATCH ") && l.Contains("heldFor=refund"));
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await NothingMovedAsync();
    }

    [Fact]
    public async Task HeldForRefund_ADeliveryInitiatedEscrow_IsRefundedAlone_NoOrderWrite()
    {
        var escrow = Held("refund", orderEscrow: false, withOrder: true);
        EscrowIs(escrow);
        OrderIs("funded");

        var result = await AdminRefund(Refund("escrow-initiate-1"));

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _crops.DidNotReceiveWithAnyArgs().RestoreStockAsync(default!, default!, default);
        await _escrows.Received(1).UpdateStatusAsync("escrow-initiate-1", CreatedAt, "refunded", "held", Arg.Any<Dictionary<string, object>>());
    }

    // ──────────────────────────────────────────────
    // Refund, heldFor: release (the sweep was about to pay out): the dispute path
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HeldForRelease_ADeliveredOrder_GoesToDisputeResolved_AsBuyerWins_ThenTheRefund_NoRestock()
    {
        EscrowIs(Held("release"));
        OrderIs("delivered", 3);
        DeliveryIs("delivered");
        OrderWritesSucceed();

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        _orderWrites.Should().Equal("disputed", "dispute_resolved");
        await _deliveries.Received(1).TryMarkDisputedAsync(DeliveryId, Arg.Any<string>(), Arg.Is<string?>(r => r == null));
        await _deliveries.Received(1).TryRecordDisputeResolutionAsync(DeliveryId, "buyer_wins", Arg.Any<string>());
        await _crops.DidNotReceiveWithAnyArgs().RestoreStockAsync(default!, default!, default);
        Received.InOrder(() =>
        {
            _audit.AppendAsync(Arg.Is<Dictionary<string, object>>(e => S(e, "action") == "fraud_hold_refund"));
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 3);
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
            // The verdict is recorded on the escrow while it stays held, before the money.
            _escrows.UpdateStatusAsync(EscrowId, CreatedAt, "held", "held", Arg.Is<Dictionary<string, object>>(f =>
                S(f, "disputeResolution") == "buyer_wins" && S(f, "disputeResolvedBy") == AdminId
                && f.ContainsKey("disputeResolvedAt")));
            _escrows.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", "held", true);
            _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        });
        // Never back through dispute_resolved: held to refunded directly.
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "held", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task HeldForRelease_ADisputedOrder_TakesOneHopToDisputeResolved()
    {
        EscrowIs(Held("release"));
        OrderIs("disputed", 6);
        DeliveryIs("disputed");
        OrderWritesSucceed();

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        _orderWrites.Should().Equal("dispute_resolved");
    }

    [Fact]
    public async Task HeldForRelease_AnOrderLeftInTransit_BehindADeliveredDelivery_IsAdvancedFirst()
    {
        EscrowIs(Held("release"));
        OrderIs("in_transit", 2);
        DeliveryIs("delivered");
        OrderWritesSucceed();

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        _orderWrites.Should().Equal("delivered", "disputed", "dispute_resolved");
    }

    [Fact]
    public async Task HeldForRelease_AnOrderInTransit_WithTheDeliveryNotDelivered_IsRefused_NothingWritten()
    {
        EscrowIs(Held("release"));
        OrderIs("in_transit", 2);
        DeliveryIs("inTransit");
        OrderWritesSucceed();

        var result = await AdminRefund();

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        _alarms.Should().ContainSingle(l => l.StartsWith("RISK_HOLD_ORDER_MISMATCH ") && l.Contains("heldFor=release"));
        _orderWrites.Should().BeEmpty();
        await _deliveries.DidNotReceiveWithAnyArgs().TryMarkDisputedAsync(default!, default!, default);
        await _escrows.DidNotReceive().UpdateStatusAsync(EscrowId, CreatedAt, "held", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await NothingMovedAsync();
    }

    [Fact]
    public async Task HeldForRelease_ARetryAfterAFailedRefund_OnlyRefunds()
    {
        EscrowIs(Held("release", resolution: "buyer_wins"));
        OrderIs("dispute_resolved", 9);
        DeliveryIs("disputed", "buyer_wins");

        var result = await AdminRefund();

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _deliveries.DidNotReceiveWithAnyArgs().TryRecordDisputeResolutionAsync(default!, default!, default!);
        await _escrows.DidNotReceive().UpdateStatusAsync(EscrowId, CreatedAt, "held", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task HeldForRelease_AnOrderResolvedForTheFarmer_IsRefused()
    {
        EscrowIs(Held("release"));
        OrderIs("dispute_resolved", 9);
        DeliveryIs("disputed", "farmer_wins");

        var result = await AdminRefund();

        result.Error!.HttpStatus.Should().Be(422);
        _alarms.Should().ContainSingle(l => l.StartsWith("RISK_HOLD_ORDER_MISMATCH "));
        await NothingMovedAsync();
    }

    [Theory]
    [InlineData("funded")]
    [InlineData("cancelled")]
    [InlineData("settled")]
    public async Task HeldForRelease_AnOrderOutsideTheDisputePath_IsRefused_NothingWritten(string orderStatus)
    {
        EscrowIs(Held("release"));
        OrderIs(orderStatus);
        DeliveryIs("pending");

        var result = await AdminRefund();

        result.Error!.HttpStatus.Should().Be(422);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await NothingMovedAsync();
    }

    [Fact]
    public async Task HeldForRelease_ADeliveryInitiatedEscrowWithNoOrder_IsRefundedAlone()
    {
        EscrowIs(Held("release", orderEscrow: false, withOrder: false));
        _deliveries.GetByIdAsync(DeliveryId, Arg.Any<bool>()).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "status", "delivered" }
        });

        var result = await AdminRefund(Refund("escrow-initiate-1"));

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _deliveries.DidNotReceiveWithAnyArgs().TryMarkDisputedAsync(default!, default!, default);
    }

    // ──────────────────────────────────────────────
    // Held from dispute_resolved: the verdict still decides the direction
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HeldFromAFarmerWinsVerdict_ReleasesAfterTheVerdictMirror_AndCannotBeRefunded()
    {
        EscrowIs(Held("release", from: "dispute_resolved", resolution: "farmer_wins"));
        OrderIs("dispute_resolved", 7);
        DeliveryIs("disputed", "farmer_wins");

        var refund = await AdminRefund();
        refund.Error!.HttpStatus.Should().Be(422);
        refund.Error.Message.Should().Contain("farmer_wins");

        var release = await AdminRelease();
        release.IsSuccess.Should().BeTrue(release.Error?.Message);
        await _escrows.Received(1).TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "held", true);
    }

    [Fact]
    public async Task HeldFromABuyerWinsVerdict_CannotBeReleased()
    {
        EscrowIs(Held("refund", from: "dispute_resolved", resolution: "buyer_wins"));

        var release = await AdminRelease();

        release.Error!.HttpStatus.Should().Be(422);
        release.Error.Message.Should().Contain("buyer_wins");
        await NothingMovedAsync();
    }

    // ──────────────────────────────────────────────
    // Active set, linkage, scheduling, disputes (section 18.5)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AHeldOrderEscrow_CountsAsActive_LinkageAndRefundSchedulingWriteNothing()
    {
        var held = Held("refund");
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { held });
        _escrows.GetByIdAsync(EscrowId).Returns(held);
        _escrows.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(held);

        var linked = await _service.LinkDeliveryToOrderEscrowAsync(OrderId, "del-new", Order("accepted", 5), "system");
        var scheduled = await _service.ScheduleRefundForOrderAsync(OrderId, "order_rejected", "system");

        linked.Should().BeFalse();
        scheduled.Should().BeFalse();
        // No second escrow is minted for an order whose escrow is held.
        await _escrows.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        await _escrows.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default!, default!, default!, default, default);
    }

    [Theory]
    [InlineData(true, "ESCROW_024")]
    [InlineData(false, "ESCROW_009")]
    public async Task ASecondPayment_ForADeliveryWhoseEscrowIsHeld_IsRefused(bool orderEscrow, string code)
    {
        // Section 18.5: held is active, so a held escrow still blocks a second payment for the same goods.
        var held = Held("release", orderEscrow: orderEscrow);
        _escrows.GetByDeliveryIdAsync(DeliveryId).Returns(held);

        var result = await _service.InitiateAsync(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 8_000m,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:{DeliveryId}:initiate:20261020"
        }, BuyerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be(code);
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
        await _escrows.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task ADisputeOnAHeldEscrow_IsRefused()
    {
        var held = Held("release");
        held["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-1).ToString("o");
        EscrowIs(held);

        var result = await _service.DisputeAsync(new DisputeEscrowRequest
        {
            EscrowId = EscrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = $"{BuyerId}:{EscrowId}:dispute:20261020"
        }, BuyerId);

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        await _escrows.DidNotReceive().UpdateStatusAsync(EscrowId, CreatedAt, "disputed", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    [InlineData("trucker-1")]
    public async Task EveryParty_SeesHeld_AndNoneOfTheAdminOnlyHoldFields(string party)
    {
        var held = Held("release");
        held["truckerId"] = "trucker-1";
        EscrowIs(held);
        _escrows.GetByDeliveryIdAsync(DeliveryId).Returns(held);

        var byId = await _service.GetByIdAsync(EscrowId, party);
        var byDelivery = await _service.GetByDeliveryAsync(DeliveryId, party);

        foreach (var dto in new[] { byId.Value!, byDelivery.Value! })
        {
            dto.Status.Should().Be("held");
            dto.HoldReason.Should().BeNull();
            dto.HeldAt.Should().BeNull();
            dto.HeldBy.Should().BeNull();
            dto.HeldFromStatus.Should().BeNull();
            dto.HeldFor.Should().BeNull();
        }
    }

    [Fact]
    public async Task AStaleAdminLockOnAHeldEscrow_IsParkedForTheManualPayoutQueue()
    {
        var held = Held("refund");
        held["settlementLock"] = "refund";
        held["settlementLockAt"] = DateTime.UtcNow.AddMinutes(-30).ToString("o");
        held["settlementLockId"] = "lock-1";
        _escrows.TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "refund", "held",
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), "lock-1", Arg.Any<string>())
            .Returns(true);

        var outcome = await _service.ParkStaleSettlementAsync(held, DateTime.UtcNow);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        EscrowService.StaleLockStatuses.Should().Contain("held");
    }
}

using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-41 (escrow-protocol.md 2.19.0): an <c>/escrow/initiate</c> escrow next to its order's own escrow, which holds
/// or paid the trade's money, is a second payment the sweeps only block (ESC-10 <c>double_escrow</c>). An admin in
/// the admin group refunds it with <c>POST /escrow/refund</c> and <c>doubleEscrow: true</c>: the decision is recorded
/// by a conditional write and audited before the existing refund runs, the order is never read or moved, anything
/// that is not clearly a double payment is refused with nothing written, and a replay answers the refund made.
/// </summary>
public class EscrowServiceDoubleEscrowRefundTests
{
    private const string EscrowId = "escrow-del-41";
    private const string OrderId = "ord-41";
    private const string OrderEscrowId = "escrow-order-ord-41";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string AdminId = "admin-1";
    private const string Reason = "  Buyer paid twice: once at checkout, once for the delivery.  ";
    private const string Trimmed = "Buyer paid twice: once at checkout, once for the delivery.";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _audits = new();

    public EscrowServiceDoubleEscrowRefundTests()
    {
        _escrowRepo.TryRecordDoubleEscrowRefundAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", "escrowed", Arg.Any<bool>()).Returns(true);
        _auditRepo.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(_audits.Add)).Returns(true);
        _auditRepo.AppendAsync(Arg.Do<Dictionary<string, object>>(_audits.Add)).Returns(Task.CompletedTask);
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "TB-REFUND-41" });
        _users.GetUserByIdAsync("buyer-41").Returns(new UserDto
        {
            UserId = "buyer-41",
            PhoneNumber = "+251911000043",
            DisplayName = "buyer",
            Role = "buyer",
            Woreda = "w",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
        Escrow();
        OrderEscrow("released");
    }

    private EscrowService Service() => new(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _users, _orders,
        _deliveries, transactionAuditRepo: _txAudit);

    private Dictionary<string, object> Escrow(Action<Dictionary<string, object>>? change = null)
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["deliveryId"] = "del-41",
            ["orderId"] = OrderId,
            ["buyerId"] = "buyer-41",
            ["farmerId"] = "farmer-41",
            ["amount"] = 1_500m,
            ["commission"] = 37.5m,
            ["farmerPayout"] = 1_462.5m,
            ["currency"] = "ETB",
            ["status"] = "escrowed",
            ["telebirrHoldRef"] = "HOLD-INIT-41",
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["expiresAt"] = "2026-10-08T10:00:00.0000000Z",
            [SettlementBlockFields.Reason] = "refund:trade:double_escrow",
            [SettlementBlockFields.ReasonAt] = "2026-10-08T11:00:00.0000000Z",
            ["version"] = 2
        };
        change?.Invoke(item);
        _escrowRepo.GetByIdAsync(EscrowId).Returns(item);
        _escrowRepo.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(item);
        return item;
    }

    private void OrderEscrow(string? status)
        => _escrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(status == null
            ? null
            : new Dictionary<string, object>
            {
                ["escrowId"] = OrderEscrowId,
                ["orderId"] = OrderId,
                ["source"] = "order",
                ["status"] = status,
                ["createdAt"] = CreatedAt
            });

    private static RefundEscrowRequest Request(Func<RefundEscrowRequest, RefundEscrowRequest>? change = null)
    {
        var request = new RefundEscrowRequest
        {
            EscrowId = EscrowId,
            Trigger = "admin_override",
            Reason = Reason,
            IdempotencyKey = $"admin:{EscrowId}:double_escrow_refund:1763000000000",
            DoubleEscrow = true
        };
        return change == null ? request : change(request);
    }

    private Task<EscrowResult<RefundEscrowResponse>> RefundAsync(RefundEscrowRequest? request = null, bool adminGroup = true)
        => Service().RefundAsync(request ?? Request(), AdminId, "admin",
            new AuditContext { Actor = AdminId, ActorRole = "admin", CorrelationId = "corr-41" }, adminGroupVerified: adminGroup);

    private async Task AssertNothingWrittenAsync()
    {
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryRecordDoubleEscrowRefundAsync(default!, default!, default!, default!, default!, default!, default!);
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryAcquireSettlementLockAsync(default!, default!, default!, default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
        _audits.Should().BeEmpty();
    }

    // ── The refund ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("escrowed")]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("held")]
    public async Task OrderEscrowHoldsOrPaidTheMoney_RecordsTheDecision_ThenRefundsTheExtraEscrowToTheBuyer(string orderEscrowStatus)
    {
        OrderEscrow(orderEscrowStatus);
        string? decidedAt = null;
        _escrowRepo.TryRecordDoubleEscrowRefundAsync(EscrowId, CreatedAt, OrderId, AdminId, Trimmed,
                Arg.Do<string>(at => decidedAt = at), orderEscrowStatus)
            .Returns(true);

        var result = await RefundAsync();

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        result.Value!.Status.Should().Be("refunded");
        result.Value.RefundAmount.Should().Be(1_500m);
        await _gateway.Received(1).RefundFundsAsync(Arg.Is<RefundFundsRequest>(r =>
            r.TransactionRef == $"refund:{EscrowId}" && r.Amount == 1_500m && r.BuyerPhone == "+251911000043"));
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed",
            Arg.Any<Dictionary<string, object>?>());

        // The decision's row comes first, at the decision's time, with the admin and the reason; then the refund row.
        _audits.Select(a => a["action"].ToString()).Should().Equal(DoubleEscrowFields.AuditAction, "refund");
        var decision = _audits[0];
        decision["timestamp"].Should().Be(decidedAt);
        decision["actor"].Should().Be(AdminId);
        decision["metadata"].Should().Be(
            $"orderId={OrderId};orderEscrowId={OrderEscrowId};orderEscrowStatus={orderEscrowStatus};reason={Trimmed}");
        _audits[1]["actor"].Should().Be(AdminId);

        // The order and the delivery belong to the order's own escrow: never read, never written.
        _orders.ReceivedCalls().Should().BeEmpty();
        _deliveries.ReceivedCalls().Should().BeEmpty();
        await _txAudit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["transactionId"] == OrderId && (string)e["action"] == DoubleEscrowFields.AuditAction
            && (string)e["entityId"] == EscrowId && (string)e["actor"] == AdminId && (string)e["timestamp"] == decidedAt
            && (string)e["correlationId"] == "corr-41"));
    }

    [Fact]
    public async Task WithoutTheFlag_TheSameAdminRefundStillFollowsTheOrder()
    {
        // ESC-09 Part B is unchanged: a plain admin refund reads the order first.
        _orders.GetByIdAsync(OrderId, true).Returns(new Dictionary<string, object> { ["orderId"] = OrderId, ["status"] = "settled" });

        var result = await RefundAsync(Request(r => r with { DoubleEscrow = false }));

        result.IsSuccess.Should().BeFalse();
        await _orders.Received().GetByIdAsync(OrderId, true);
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryRecordDoubleEscrowRefundAsync(default!, default!, default!, default!, default!, default!, default!);
    }

    // ── Request rules ───────────────────────────────────────────────────────

    [Fact]
    public void TheWireField_IsDoubleEscrow_AndDefaultsToFalse()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize(
            $"{{\"escrowId\":\"{EscrowId}\",\"trigger\":\"admin_override\",\"reason\":\"{Trimmed}\",\"idempotencyKey\":\"k:a:b:1\",\"doubleEscrow\":true}}",
            AppJsonContext.Default.RefundEscrowRequest)!;
        parsed.DoubleEscrow.Should().BeTrue();

        System.Text.Json.JsonSerializer.Deserialize("{\"escrowId\":\"e\"}", AppJsonContext.Default.RefundEscrowRequest)!
            .DoubleEscrow.Should().BeFalse();
    }

    [Fact]
    public async Task NotInTheAdminGroup_Is403_NothingRead()
    {
        var result = await RefundAsync(adminGroup: false);

        result.Error!.HttpStatus.Should().Be(403);
        result.Error.Code.Should().Be("ESCROW_021");
        await _escrowRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too short")]
    [InlineData("         short         ")]
    public async Task AReasonUnder10CharactersTrimmed_Is400(string? reason)
    {
        var result = await RefundAsync(Request(r => r with { Reason = reason }));

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Field.Should().Be("reason");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AReasonOver500Characters_Is400()
    {
        var result = await RefundAsync(Request(r => r with { Reason = new string('x', 501) }));

        result.Error!.Field.Should().Be("reason");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AnotherTrigger_Is400()
    {
        var result = await RefundAsync(Request(r => r with { Trigger = "delivery_timeout" }));

        result.Error!.Field.Should().Be("trigger");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task WithTheOrderMismatchOverride_Is400()
    {
        var result = await RefundAsync(Request(r => r with { OverrideOrderMismatch = true }));

        result.Error!.HttpStatus.Should().Be(400);
        result.Error.Field.Should().Be("overrideOrderMismatch");
        await AssertNothingWrittenAsync();
    }

    // ── Refusals: not clearly a double payment ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("initiated")]
    [InlineData("refunded")]
    [InlineData("cancelled")]
    public async Task OrderEscrowDoesNotHoldTheMoney_Is409_NothingWritten(string? orderEscrowStatus)
    {
        OrderEscrow(orderEscrowStatus);

        var result = await RefundAsync();

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("dispute_resolved")]
    [InlineData("disputed")]
    [InlineData("held")]
    [InlineData("initiated")]
    [InlineData("released")]
    public async Task TheEscrowIsNotEscrowed_Is409(string status)
    {
        Escrow(e => e["status"] = status);

        var result = await RefundAsync();

        result.Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheOrdersOwnEscrow_Is409()
    {
        Escrow(e => e["source"] = "order");

        (await RefundAsync()).Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AnEscrowWithoutAnOrder_Is409()
    {
        Escrow(e => e.Remove("orderId"));

        (await RefundAsync()).Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ALedgerEscrow_Is409()
    {
        Escrow(e => e[LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger);

        (await RefundAsync()).Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheOrderEscrowCannotBeRead_Is503_NothingWritten()
    {
        _escrowRepo.GetByIdAsync(OrderEscrowId, true).ThrowsAsync(new TimeoutException("read"));

        var result = await RefundAsync();

        result.Error!.HttpStatus.Should().Be(503);
        result.Error.Code.Should().Be("ESCROW_012");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheDecisionWriteLoses_AndNoDecisionIsThere_Is409_NoMoney()
    {
        _escrowRepo.TryRecordDoubleEscrowRefundAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        var result = await RefundAsync();

        result.Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await _gateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
        _audits.Should().BeEmpty();
    }

    [Fact]
    public async Task TheDecisionsAuditRowCannotBeWritten_Is503_NoMoney()
    {
        _auditRepo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new TimeoutException("audit"));

        var result = await RefundAsync();

        result.Error!.HttpStatus.Should().Be(503);
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryAcquireSettlementLockAsync(default!, default!, default!, default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
    }

    // ── Replays and retries ─────────────────────────────────────────────────

    [Fact]
    public async Task AReplayAfterTheRefund_AnswersTheRefundMade_AndWritesNothing()
    {
        Escrow(e =>
        {
            e["status"] = "refunded";
            e["telebirrRefundRef"] = "TB-REFUND-41";
            e["refundedAt"] = "2026-11-13T10:00:00.0000000Z";
            e[DoubleEscrowFields.RefundAt] = "2026-11-13T09:59:00.0000000Z";
            e[DoubleEscrowFields.RefundBy] = AdminId;
        });

        var result = await RefundAsync(Request(r => r with { IdempotencyKey = $"admin:{EscrowId}:double_escrow_refund:1763000000001" }));

        result.IsSuccess.Should().BeTrue();
        result.IsDuplicate.Should().BeTrue();
        result.Value!.Status.Should().Be("refunded");
        result.Value.TelebirrRef.Should().Be("TB-REFUND-41");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ARetryAfterAGatewayRefusal_KeepsTheFirstDecision_AndRefundsUnderTheSameReference()
    {
        const string firstAt = "2026-11-13T09:00:00.0000000Z";
        Escrow(e =>
        {
            e[DoubleEscrowFields.RefundAt] = firstAt;
            e[DoubleEscrowFields.RefundBy] = "admin-0";
            e[DoubleEscrowFields.RefundReason] = "The first admin's reason, kept.";
            e[DoubleEscrowFields.OrderEscrowStatus] = "escrowed";
        });

        var result = await RefundAsync();

        result.IsSuccess.Should().BeTrue();
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryRecordDoubleEscrowRefundAsync(default!, default!, default!, default!, default!, default!, default!);
        // The decision's row is re-ensured at the first decision (a no-op when it is there), with its admin and reason.
        var decision = _audits.First(a => a["action"].ToString() == DoubleEscrowFields.AuditAction);
        decision["timestamp"].Should().Be(firstAt);
        decision["actor"].Should().Be("admin-0");
        decision["metadata"].ToString().Should().EndWith(";orderEscrowStatus=escrowed;reason=The first admin's reason, kept.");
        await _gateway.Received(1).RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == $"refund:{EscrowId}"));
    }

    [Fact]
    public async Task ARetryAfterTheOrderEscrowWasRefunded_IsRefused_EvenWithADecisionOnRecord()
    {
        Escrow(e =>
        {
            e[DoubleEscrowFields.RefundAt] = "2026-11-13T09:00:00.0000000Z";
            e[DoubleEscrowFields.RefundBy] = AdminId;
        });
        OrderEscrow("refunded");

        (await RefundAsync()).Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        await _gateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
    }

    [Fact]
    public async Task TheDecisionWriteLosesToAnotherAdmin_UsesTheirDecision()
    {
        _escrowRepo.TryRecordDoubleEscrowRefundAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        var stale = Escrow();
        var latest = new Dictionary<string, object>(stale)
        {
            [DoubleEscrowFields.RefundAt] = "2026-11-13T09:30:00.0000000Z",
            [DoubleEscrowFields.RefundBy] = "admin-2",
            [DoubleEscrowFields.RefundReason] = "Admin two decided it first.",
            [DoubleEscrowFields.OrderEscrowStatus] = "released"
        };
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(latest);

        var result = await RefundAsync();

        result.IsSuccess.Should().BeTrue();
        _audits.First()["actor"].Should().Be("admin-2");
        _audits.First()["timestamp"].Should().Be("2026-11-13T09:30:00.0000000Z");
    }

    [Fact]
    public async Task TheDecisionWriteLosesToARefundAlreadyMade_AnswersTheReplay()
    {
        _escrowRepo.TryRecordDoubleEscrowRefundAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        var stale = Escrow();
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(new Dictionary<string, object>(stale)
        {
            ["status"] = "refunded",
            ["refundedAt"] = "2026-11-13T10:00:00.0000000Z",
            [DoubleEscrowFields.RefundAt] = "2026-11-13T09:30:00.0000000Z"
        });

        var result = await RefundAsync();

        result.IsDuplicate.Should().BeTrue();
        await _gateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
    }

    [Fact]
    public async Task TheGatewayCannotPay_TheRefundWaitsInManualPayouts_WithTheDecisionKept()
    {
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = "PAYOUT_NOT_SUPPORTED" });
        _escrowRepo.TryQueueManualPayoutAsync(EscrowId, CreatedAt, ManualPayoutFields.OpRefund, Arg.Any<string>(),
                "escrowed", Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);

        var result = await RefundAsync();

        result.Error!.HttpStatus.Should().Be(202);
        result.Error.Code.Should().Be("ESCROW_034");
        await _escrowRepo.Received(1).TryRecordDoubleEscrowRefundAsync(EscrowId, CreatedAt, OrderId, AdminId, Trimmed,
            Arg.Any<string>(), "released");
        _orders.ReceivedCalls().Should().BeEmpty();
    }
}

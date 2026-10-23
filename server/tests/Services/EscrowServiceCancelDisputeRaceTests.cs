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
/// FUP-32: a buyer's transport-failure cancel and a dispute race for the same escrow. The cancel
/// arms the refund in the order cancel's own transaction, and a freeze requires that no refund is
/// armed, so whichever write reaches the escrow first wins and the other is refused. These tests
/// cover the service side of every interleaving; the LocalStack suite runs the real conditions.
/// </summary>
public class EscrowServiceCancelDisputeRaceTests
{
    private const string OrderId = "order-race";
    private const string EscrowId = "escrow-order-order-race";
    private const string EscrowCreatedAt = "2026-10-01T09:00:00.0000000Z";
    private const string BuyerId = "buyer-race";
    private const string FarmerId = "farmer-race";
    private const string DeliveryId = "delivery-race";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();

    public EscrowServiceCancelDisputeRaceTests()
    {
        _idempotency.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(_auditRows.Add)).Returns(Task.CompletedTask);
        _audit.GetAuditTrailAsync(Arg.Any<string>()).Returns(new List<Dictionary<string, object>>());
        _escrows.BuildRefundArm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => new TransactWriteItem
            {
                Update = new Update { TableName = "escrows", UpdateExpression = $"arm {call.ArgAt<string>(0)} at {call.ArgAt<string>(3)}" }
            });
    }

    private EscrowService Service() => new(
        _escrows, _audit, _idempotency, Substitute.For<ITelebirrGateway>(),
        userRepo: null, orderRepo: _orders, deliveryRepo: _deliveries);

    private static Dictionary<string, object> Escrow(
        string escrowId = EscrowId, string status = "escrowed", params (string Key, string Value)[] extra)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "createdAt", EscrowCreatedAt },
            { "orderId", OrderId },
            { "deliveryId", DeliveryId },
            { "status", status },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", "8000" }
        };
        foreach (var (key, value) in extra)
        {
            escrow[key] = value;
        }

        return escrow;
    }

    private static Dictionary<string, object> Order() => new()
    {
        { "orderId", OrderId }, { "status", "accepted" }, { "version", "4" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }
    };

    private static Dictionary<string, object> CancelFields() => new()
    {
        { "status", "cancelled" }, { "cancelReason", "transport_failed_buyer_cancelled" }
    };

    private static DisputeEscrowRequest DisputeBy(string userId) => new()
    {
        EscrowId = EscrowId,
        DisputedBy = userId,
        Reason = "not_delivered",
        IdempotencyKey = $"{userId}:{EscrowId}:dispute:1729677600000"
    };

    // ===== Prepare: the plan carries what the transaction arms =====

    [Fact]
    public async Task Prepare_PlainOrderEscrow_IsReady_WithTheRowItRead()
    {
        var escrow = Escrow();
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { escrow });
        _escrows.GetByIdAsync(EscrowId, true).Returns(escrow);

        var plan = await Service().PrepareOrderEscrowForCancelAsync(Order(), "system");

        plan.Readiness.Should().Be(OrderEscrowCancelReadiness.Ready);
        plan.Held.Should().ContainSingle().Which.Should().BeSameAs(escrow);
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("refunded")]
    public async Task Prepare_EscrowNotAPlainHold_IsBusy_AndHoldsNothing(string status)
    {
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _escrows.GetByIdAsync(EscrowId, true).Returns(Escrow(status: status));

        var plan = await Service().PrepareOrderEscrowForCancelAsync(Order(), "system");

        plan.Readiness.Should().Be(OrderEscrowCancelReadiness.Busy);
        plan.Held.Should().BeEmpty();
    }

    // ===== The cancel: one transaction, then the ledger =====

    [Fact]
    public async Task Cancel_ArmsEveryHeldEscrowInTheOrdersTransaction_ThenAuditsEach()
    {
        var held = new[] { Escrow(), Escrow("escrow-legacy-race") };
        IReadOnlyList<TransactWriteItem>? sent = null;
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "accepted",
                Arg.Do<IReadOnlyList<TransactWriteItem>>(items => sent = items))
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, Order()));

        var write = await Service().CancelOrderWithRefundAsync(
            OrderId, CancelFields(), 4, "accepted", held, "order_transport_failed", "system");

        write.Outcome.Should().Be(OrderLinkedWriteOutcome.Written);
        sent.Should().HaveCount(2);
        sent![0].Update.UpdateExpression.Should().StartWith($"arm {EscrowId} at ");
        sent[1].Update.UpdateExpression.Should().StartWith("arm escrow-legacy-race at ");
        // One refund time for the whole cancel.
        sent[0].Update.UpdateExpression[^28..].Should().Be(sent[1].Update.UpdateExpression[^28..]);
        _escrows.Received(1).BuildRefundArm(EscrowId, EscrowCreatedAt, "order_transport_failed", Arg.Any<string>());
        _auditRows.Select(r => $"{r["escrowId"]}:{r["action"]}:{r["metadata"]}").Should().Equal(
            $"{EscrowId}:refund_scheduled:order_transport_failed",
            "escrow-legacy-race:refund_scheduled:order_transport_failed");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Theory]
    [InlineData(OrderLinkedWriteOutcome.LinkedItemRefused)]
    [InlineData(OrderLinkedWriteOutcome.OrderChanged)]
    [InlineData(OrderLinkedWriteOutcome.OrderNotFound)]
    public async Task Cancel_NotWritten_LeavesNoLedgerRow(OrderLinkedWriteOutcome outcome)
    {
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(outcome));

        var write = await Service().CancelOrderWithRefundAsync(
            OrderId, CancelFields(), 4, "accepted", new[] { Escrow() }, "order_transport_failed", "system");

        write.Outcome.Should().Be(outcome);
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_LedgerRowFails_TheCancelAndItsArmStand()
    {
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, Order()));
        _audit.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var write = await Service().CancelOrderWithRefundAsync(
            OrderId, CancelFields(), 4, "accepted", new[] { Escrow() }, "order_transport_failed", "system");

        write.Outcome.Should().Be(OrderLinkedWriteOutcome.Written);
    }

    [Fact]
    public async Task Cancel_TransactionThrows_PropagatesWithNoLedgerRow()
    {
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .ThrowsAsync(new TransactionCanceledException("TransactionConflict"));

        var write = () => Service().CancelOrderWithRefundAsync(
            OrderId, CancelFields(), 4, "accepted", new[] { Escrow() }, "order_transport_failed", "system");

        await write.Should().ThrowAsync<TransactionCanceledException>();
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WithNothingHeld_FailsClosed()
    {
        var write = () => Service().CancelOrderWithRefundAsync(
            OrderId, CancelFields(), 4, "accepted", Array.Empty<Dictionary<string, object>>(), "order_transport_failed", "system");

        await write.Should().ThrowAsync<InvalidOperationException>();
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
    }

    // ===== POST /escrow/dispute after (or racing) the cancel =====

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    public async Task Dispute_RefundAlreadyArmed_IsRefused409_BeforeAnyWrite(string party)
    {
        _escrows.GetByIdAsync(EscrowId).Returns(Escrow(extra: ("refundReason", "order_transport_failed")));

        var result = await Service().DisputeAsync(DisputeBy(party), party);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Contain("refund to the buyer is scheduled");
        await _idempotency.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Dispute_ArmLandsBetweenItsReadAndItsWrite_IsRefused409_AndFreesTheKey()
    {
        _escrows.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrows.UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("refundReason exists"));
        _escrows.GetByIdAsync(EscrowId, true).Returns(Escrow(extra: ("refundReason", "order_transport_failed")));
        var request = DisputeBy(FarmerId);

        var result = await Service().DisputeAsync(request, FarmerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Contain("refund to the buyer is scheduled");
        await _idempotency.Received(1).DeleteAsync(request.IdempotencyKey);
        _auditRows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "refund to the buyer is scheduled")]
    [InlineData(false, "Try again")]
    public async Task Dispute_MeetsTheCancelTransactionInFlight_IsAnsweredFromTheEscrow_NeverA500(bool armed, string message)
    {
        _escrows.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrows.UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new TransactionConflictException("Transaction is ongoing for the item"));
        _escrows.GetByIdAsync(EscrowId, true).Returns(armed ? Escrow(extra: ("refundReason", "order_transport_failed")) : Escrow());
        var request = DisputeBy(BuyerId);

        var result = await Service().DisputeAsync(request, BuyerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Contain(message);
        await _idempotency.Received(1).DeleteAsync(request.IdempotencyKey);
    }

    [Fact]
    public async Task Dispute_FreezeWinsTheRace_IsUnchanged()
    {
        _escrows.GetByIdAsync(EscrowId).Returns(Escrow());

        var result = await Service().DisputeAsync(DisputeBy(BuyerId), BuyerId);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed",
            Arg.Any<Dictionary<string, object>?>());
    }

    // ===== A delivery dispute (sync, escrow-cron) on an armed escrow =====

    [Fact]
    public async Task DeliveryFreeze_RefundArmed_IsRefused_Audited_AndWritesNothing()
    {
        _escrows.GetByIdAsync(EscrowId, true).Returns(Escrow(extra: ("refundReason", "admin_refund")));

        var freeze = await Service().FreezeForDeliveryDisputeAsync(DeliveryId, OrderId, BuyerId, "never arrived");

        freeze.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
        freeze.MayMirror.Should().BeFalse();
        freeze.EscrowId.Should().Be(EscrowId);
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        _auditRows.Should().ContainSingle(r => (string)r["action"] == "dispute_refused_refund_scheduled"
            && ((string)r["metadata"]).Contains("refundReason=admin_refund"));
    }

    [Fact]
    public async Task DeliveryFreeze_ArmLandsBetweenItsReadAndItsWrite_IsRefused_NotUndecided()
    {
        _escrows.GetByIdAsync(EscrowId, true).Returns(Escrow(), Escrow(extra: ("refundReason", "order_transport_failed")));
        _escrows.UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("refundReason exists"));

        var freeze = await Service().FreezeForDeliveryDisputeAsync(DeliveryId, OrderId, BuyerId, "never arrived");

        freeze.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
    }

    [Fact]
    public async Task DeliveryFreeze_AlreadyDisputedRowWithARefundReason_StillMirrors()
    {
        // A row frozen before FUP-32 over a scheduled refund is a dispute already: the mirror finishes it.
        _escrows.GetByIdAsync(EscrowId, true).Returns(Escrow(status: "disputed", extra: ("refundReason", "order_transport_failed")));

        var freeze = await Service().FreezeForDeliveryDisputeAsync(DeliveryId, OrderId, BuyerId, "never arrived");

        freeze.Outcome.Should().Be(DeliveryDisputeFreeze.AlreadyDisputed);
    }
}

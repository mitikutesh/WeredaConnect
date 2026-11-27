using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-53: the reject or cancel of a funded order and the refund arm on its escrow are one
/// transaction, as FUP-32 made the transport cancel. A dispute that froze the money first refuses
/// the close, and nothing that follows a close runs; a close with nothing in the way writes what it
/// wrote before (the order, the audit rows, the restock). Shares the setup of
/// <see cref="SyncFunctionTransportCancelTests"/>. Real conditions: CancelRefundRaceDynamoDbTests.
/// </summary>
public partial class SyncFunctionTransportCancelTests
{
    private Task<System.Text.Json.JsonElement> RejectAsync(int version = 4)
        => SendAsync(FarmerId, new Dictionary<string, object> { { "status", "rejected" }, { "version", version } });

    private async Task ClosedWithArmAsync(string reason)
    {
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items =>
                items.Count == 1 && items[0].Update.UpdateExpression == Arm(EscrowId, reason).Update.UpdateExpression));
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowAudit.Received(1).AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _escrowAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            (string)a["action"] == "refund_scheduled" && (string)a["escrowId"] == EscrowId
            && (string)a["metadata"] == reason && (string)a["actor"] == "system"
            && (string)a["fromState"] == "escrowed" && (string)a["toState"] == "escrowed"));
    }

    private async Task NothingClosedAsync()
    {
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        _auditRows.Should().BeEmpty();
    }

    // ===== Nothing in the way: the same close as before, in one write =====

    [Fact]
    public async Task FundedReject_ArmsTheRefundInTheOrdersTransaction_AndTheRestIsAsBefore()
    {
        _order = Order(status: "funded", version: 4);

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        _written!["status"].ToString().Should().Be("rejected");
        _written.Should().ContainKey("respondedAt");
        await ClosedWithArmAsync("order_rejected");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
        _auditRows.Should().ContainSingle(r => (string)r["entityType"] == "order")
            .Which.Should().Contain("toState", "rejected").And.Contain("fromState", "funded");
        Logs.Should().Contain("Order escrow refund scheduled").And.Contain("reason=order_rejected");
    }

    [Fact]
    public async Task FundedCancel_ArmsTheRefundInTheOrdersTransaction_AndTheRestIsAsBefore()
    {
        _order = Order(status: "funded", version: 4);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].ToString().Should().Be("cancelled");
        _written.Should().ContainKey("cancelledAt");
        await ClosedWithArmAsync("order_cancelled");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
    }

    [Fact]
    public async Task FundedReject_WithNoEscrowRecord_KeepsTheSeparateWriteAndSchedule()
    {
        // No record holds the money (the funding-time create failed): the close is the old two steps.
        _order = Order(status: "funded", version: 4);
        _escrow = null;

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
        Logs.Should().Contain("No refundable order escrow");
    }

    [Fact]
    public async Task FundedReject_WithoutAnEscrowService_WritesTheOrderAsBefore()
    {
        _order = Order(status: "funded", version: 4);

        var result = await SendAsync(FarmerId,
            new Dictionary<string, object> { { "status", "rejected" }, { "version", 4 } }, MakeFunction(withEscrowService: false));

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
    }

    [Fact]
    public async Task FundedReject_WhoseEscrowIsHeldForReview_ClosesFencedOnTheHold_AndArmsNothing()
    {
        // FRD-03: a reject stays allowed while the escrow is held; its admin refunds it. The close
        // lands only while the escrow is still held, so a released hold cannot slip under it.
        _order = Order(status: "funded", version: 4);
        _escrow = Escrow(status: "held");
        var fence = new TransactWriteItem { ConditionCheck = new ConditionCheck { ConditionExpression = "still held" } };
        _escrows.BuildStatusCheck(EscrowId, Arg.Any<string>(), "held").Returns(fence);

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items => items.Count == 1 && items[0] == fence));
        _escrows.DidNotReceive().BuildRefundArm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
        Logs.Should().Contain("held for review");
    }

    // ===== The money is frozen or moving: the close is refused, nothing is written =====

    [Theory]
    [InlineData("disputed", null)]
    [InlineData("dispute_resolved", null)]
    [InlineData("escrowed", "settlementLock")]
    [InlineData("escrowed", "manualPayout")]
    [InlineData("escrowed", "deliveryConfirmedAt")]
    public async Task FundedReject_WhoseMoneyIsFrozenOrMoving_IsRefused_AndNothingIsWritten(string escrowStatus, string? marker)
    {
        _order = Order(status: "funded", version: 4);
        _escrow = marker switch
        {
            null => Escrow(status: escrowStatus),
            "manualPayout" => Escrow(status: escrowStatus, extra: (ManualPayoutFields.Marker, ManualPayoutFields.MarkerOpen)),
            _ => Escrow(status: escrowStatus, extra: (marker, "2026-10-02T08:00:00.0000000Z"))
        };

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedCancel_WhoseEscrowADisputeFroze_IsRefused_AndNothingIsWritten()
    {
        _order = Order(status: "funded", version: 4);
        _escrow = Escrow(status: "disputed");

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedReject_DisputeFrozeTheEscrowAfterThePlan_PlansAgain_AndRefuses()
    {
        // The arm's condition refuses the transaction; the second plan reads the frozen escrow.
        _order = Order(status: "funded", version: 4);
        CancelWriteReturns(_ =>
        {
            _escrow = Escrow(status: "disputed");
            return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused);
        });

        var result = await RejectAsync();

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedReject_AHoldLandedAfterThePlan_PlansAgain_AndClosesFencedOnTheHold()
    {
        _order = Order(status: "funded", version: 4);
        var fence = new TransactWriteItem { ConditionCheck = new ConditionCheck { ConditionExpression = "still held" } };
        _escrows.BuildStatusCheck(EscrowId, Arg.Any<string>(), "held").Returns(fence);
        var calls = 0;
        CancelWriteReturns(call =>
        {
            if (++calls == 1)
            {
                _escrow = Escrow(status: "held");
                return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused);
            }

            return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written,
                new Dictionary<string, object>(_order) { ["status"] = "rejected", ["version"] = "5" });
        });

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        calls.Should().Be(2);
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items => items.Count == 1 && items[0] == fence));
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FundedReject_WhoseEscrowKeepsChanging_GivesUpAsTransient_AfterThreeWrites()
    {
        _order = Order(status: "funded", version: 4);
        CancelWriteReturns(_ => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused));

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(
            "The order's payment changed while the order was updated. Please try again later.");
        await _orders.Received(3).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "funded",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await NothingClosedAsync();
    }

    // ===== The write itself =====

    [Fact]
    public async Task FundedReject_TransactionCancelledByAConflict_IsTransient_AndNothingAfterItRuns()
    {
        _order = Order(status: "funded", version: 4);
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .ThrowsAsync(new TransactionCanceledException("Transaction cancelled")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "TransactionConflict" }, new() { Code = "None" } }
            });

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("The order could not be updated right now. Please try again later.");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedReject_EscrowThatCannotBeRead_IsTransient_AndNothingIsWritten()
    {
        _order = Order(status: "funded", version: 4);
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).ThrowsAsync(new InternalServerErrorException("down"));

        var result = await RejectAsync();

        result.GetProperty("message").GetString().Should().Be("The order's payment could not be read. Please try again later.");
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedReject_OrderMovedSinceTheRead_IsAConflict_WithTheServersRow()
    {
        _order = Order(status: "funded", version: 4);
        CancelWriteReturns(_ =>
        {
            _order = Order(status: "accepted", version: 5);
            return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderChanged);
        });

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("accepted");
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FundedReject_OrderGoneAtTheWrite_IsTransient()
    {
        _order = Order(status: "funded", version: 4);
        CancelWriteReturns(_ => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderNotFound));

        var result = await RejectAsync();

        result.GetProperty("message").GetString().Should().Be("Order not found.");
        await NothingClosedAsync();
    }

    [Fact]
    public async Task FundedReject_AFailedRefundAuditRow_LeavesTheCloseStanding()
    {
        _order = Order(status: "funded", version: 4);
        _escrowAudit.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new InternalServerErrorException("down"));

        var result = await RejectAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
    }
}

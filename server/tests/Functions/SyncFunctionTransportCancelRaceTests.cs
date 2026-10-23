using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-32: a dispute that freezes the escrow after the transport cancel's checks. The order
/// cancel and the refund arm are one transaction, so the cancel is refused instead of leaving a
/// cancelled order over frozen money, and nothing that follows a cancel runs. Shares the setup of
/// <see cref="SyncFunctionTransportCancelTests"/>.
/// </summary>
public partial class SyncFunctionTransportCancelTests
{
    [Fact]
    public async Task Race_DisputeFrozeTheEscrowAfterTheCheck_RefusesTheCancel_AndNothingAfterItRuns()
    {
        CancelWriteReturns(_ => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused));

        var result = await CancelAsync(BuyerId, function: MakeFunctionWithFraud());

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        // One attempt: an escrow refusal is not retried like a version conflict.
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4, "accepted",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        // The pending delivery was cancelled before the write, as on every refusal after it.
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _escrowAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        _auditRows.Select(r => (string)r["action"]).Should().Equal("delivery_cancelled");
        Logs.Should().Contain("refused: its escrow changed after the check").And.NotContain("refund scheduled");
    }

    [Fact]
    public async Task Race_DisputeFrozeTheEscrowDuringAVersionRetry_RefusesTheCancel()
    {
        var calls = 0;
        CancelWriteReturns(_ =>
        {
            if (++calls == 1)
            {
                _order = new Dictionary<string, object>(_order) { ["version"] = "5" };
                return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderChanged);
            }

            return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused);
        });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5, "accepted",
            Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Race_TransactionCancelledByAConflict_IsTransient_AndNothingAfterItRuns()
    {
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .ThrowsAsync(new TransactionCanceledException("Transaction cancelled")
            {
                CancellationReasons = new List<CancellationReason>
            {
                new() { Code = "TransactionConflict" }, new() { Code = "None" }
            }
            });

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("The order's order could not be read. Please try again later.");
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
        await _crops.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        _auditRows.Select(r => (string)r["action"]).Should().NotContain("order_cancelled");
    }

    [Fact]
    public async Task Race_OrderGoneAtTheWrite_IsTransient()
    {
        CancelWriteReturns(_ => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderNotFound));

        var result = await CancelAsync(BuyerId);

        result.GetProperty("message").GetString().Should().Be("The order could not be cancelled right now. Please try again later.");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
    }

    [Fact]
    public async Task Race_FundedOrderCancel_StillSchedulesItsRefundSeparately()
    {
        // FUP-32 changes the transport cancel only: a funded order's cancel keeps its CAS and schedule.
        _order = Order(status: "funded", version: 4);

        var result = await CancelAsync(BuyerId);

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["refundReason"] == "order_cancelled"));
    }
}

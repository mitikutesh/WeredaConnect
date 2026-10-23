using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-32: a delivery dispute through sync on an escrow whose refund is scheduled is refused with its
/// own text (not the "not held in escrow (status: escrowed)" fallback), and nothing is written.
/// Shares the dispute rig of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private const string RefundScheduledText =
        "DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being refunded to the buyer, so it can no longer be disputed. Contact support.";

    [Fact]
    public async Task DeliveryUpdate_Disputed_RefundScheduled_IsRefusedWithItsOwnText_AndNothingIsWritten()
    {
        var escrow = OrderEscrowRow();
        escrow["refundReason"] = "admin_refund";
        var rig = DisputeFunction(escrow);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains(RefundScheduledText, response.Body);
        Assert.DoesNotContain("not held in escrow", response.Body);
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
        await rig.AuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(d =>
            (string)d["action"] == "dispute_refused_refund_scheduled" && (string)d["escrowId"] == OrderEscrowId
            && (string)d["metadata"] == "deliveryId=del-001;refundReason=admin_refund"));
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_RefundArmedBetweenTheReadAndTheFreeze_IsRefusedWithTheSameText()
    {
        // A transport cancel armed the refund after the freeze read a plain escrow: the freeze write
        // fails its refundReason condition, and the re-read decides.
        var rig = DisputeFunction(null);
        var armed = OrderEscrowRow();
        armed["refundReason"] = "order_transport_failed";
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow(), armed);
        rig.EscrowRepo.UpdateStatusAsync(OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("refundReason exists"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains(RefundScheduledText, response.Body);
        Assert.DoesNotContain("DELIVERY_DISPUTE_RETRY", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }
}

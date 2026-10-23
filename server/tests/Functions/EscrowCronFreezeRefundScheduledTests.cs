using FluentAssertions;
using NSubstitute;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-32: the hourly sweeps find a legacy disputed trade whose escrow has a scheduled refund. The
/// freeze is refused for good (<c>refundReason</c> is never removed), so the settlement is blocked as
/// <c>freeze_refund_scheduled</c>, alarmed once, and the freeze is not asked again on later runs.
/// Shares the setup of <see cref="EscrowCronSweepHygieneTests"/>.
/// </summary>
public partial class EscrowCronSweepHygieneTests
{
    [Fact]
    public async Task Release_DisputedTradeOverAScheduledRefund_BlocksFreezeRefundScheduled_AndDoesNotAskAgain()
    {
        var e = Escrow("escrow-rs", confirmed: true, orderId: "ord-rs");
        e["refundReason"] = "admin_refund";
        DeliveryIs("escrow-rs", "disputed", d => d["disputedBy"] = "buyer-1");
        OrderIs("ord-rs", "disputed");
        _escrows.GetByDeliveryIdAsync("del-escrow-rs").Returns(new Dictionary<string, object> { { "escrowId", "escrow-rs" } });
        var swept = ReleaseSweep(e);

        await RunAsync(Cron());
        await RunAsync(Cron());

        await NotReleased("escrow-rs");
        _markers["escrow-rs"].Reason.Should().Be("release:trade:freeze_refund_scheduled");
        _alarms.Where(l => l.Contains("auto_release_blocked") && l.Contains("escrowId=escrow-rs")).Should().ContainSingle();
        // The freeze path ran on the first run only: one refusal row, one base-row lookup by delivery.
        AuditRows("escrow-rs", "dispute_refused_refund_scheduled").Should().ContainSingle();
        await _escrows.Received(1).GetByDeliveryIdAsync("del-escrow-rs");
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "disputed",
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        swept.AssertServed();
    }

    [Fact]
    public async Task Refund_InitiateEscrowOverADisputedTrade_WithAScheduledRefund_IsBlockedForGood_NotRefunded()
    {
        var e = Escrow("escrow-rsr", orderId: "ord-rsr");
        e["refundReason"] = "order_cancelled";
        DeliveryIs("escrow-rsr", "disputed", d => d["disputedBy"] = "farmer-1");
        OrderIs("ord-rsr", "cancelled");
        _escrows.GetByDeliveryIdAsync("del-escrow-rsr").Returns(new Dictionary<string, object> { { "escrowId", "escrow-rsr" } });
        RefundSweep(e);

        await RunAsync(Cron());
        await RunAsync(Cron());

        await NotRefunded("escrow-rsr");
        _markers["escrow-rsr"].Reason.Should().Be("refund:trade:freeze_refund_scheduled");
        _alarms.Where(l => l.Contains("auto_refund_blocked") && l.Contains("escrowId=escrow-rsr")).Should().ContainSingle();
        AuditRows("escrow-rsr", "dispute_refused_refund_scheduled").Should().ContainSingle();
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "disputed",
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }
}

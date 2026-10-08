using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-13: the money alarm lines are the producer side of the CloudWatch metric filters in
/// infra/lib/stacks/monitoring-stack.ts, so their exact shape is a contract.
/// </summary>
public class MoneyAlarmLogTests
{
    [Fact]
    public void SettlementFailed_FormatsTokenAndFields()
    {
        MoneyAlarmLog.SettlementFailed("escrow-001", "release_failed", MoneyAlarmLog.OutcomeFailed, "TELEBIRR_RELEASE_FAILED")
            .Should().Be(
                "ESCROW_SETTLEMENT_FAILED escrowId=escrow-001 action=release_failed outcome=failed code=TELEBIRR_RELEASE_FAILED");
    }

    [Fact]
    public void SettlementFailed_SanitizesIdsAndCode()
    {
        var longCode = new string('a', 100);

        var line = MoneyAlarmLog.SettlementFailed("bad id\nMANUAL_PAYOUT_OVERDUE x=1", "a b=c", null, longCode);

        // Spaces, newlines and '=' are dropped, so a value can never add a second token
        // or a second field to the line; long values are cut at 64 characters.
        line.Should().Be(
            "ESCROW_SETTLEMENT_FAILED escrowId=badidMANUAL_PAYOUT_OVERDUEx1 action=abc outcome=- "
            + $"code={new string('a', 64)}");
        line.Should().NotContain("\n");
    }

    [Fact]
    public void SettlementFailed_NullOrEmptyFieldsBecomeDash()
    {
        MoneyAlarmLog.SettlementFailed(null, "sweep_failed", MoneyAlarmLog.OutcomeUnknown, "")
            .Should().Be("ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=unknown code=-");
        MoneyAlarmLog.SettlementFailed("escrow-001", "trucker_release_failed", MoneyAlarmLog.OutcomeFailed, "!!!")
            .Should().EndWith(" code=-");
    }

    [Fact]
    public void QueueOverdueLines_FormatTokenIdAndAge()
    {
        // ESC-02 logs ref=<op>:<leg>, one line per pending leg; the colon survives.
        MoneyAlarmLog.ManualPayoutOverdue("escrow-order-ord-1", "release:farmer", 26)
            .Should().Be("MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=26");
        MoneyAlarmLog.ManualPayoutOverdue("escrow-order-ord-1", "refund:no_pending_leg", 40)
            .Should().Be("MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=refund:no_pending_leg ageHours=40");
        MoneyAlarmLog.AdminQueueOverdue("order-001", "undelivered", 30)
            .Should().Be("ADMIN_QUEUE_OVERDUE orderId=order-001 reason=undelivered ageHours=30");
        MoneyAlarmLog.AdminQueueOverdue(null, null, -5)
            .Should().Be("ADMIN_QUEUE_OVERDUE orderId=- reason=- ageHours=0");
    }

    [Fact]
    public void QueueAlarmAfter_Is24Hours()
    {
        MoneyAlarmLog.QueueAlarmAfter.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("release_failed", true)]
    [InlineData("refund_failed", true)]
    [InlineData("trucker_release_failed", true)]
    [InlineData("release_blocked_kyc", false)]
    [InlineData("release", false)]
    [InlineData("trucker_release", false)]
    [InlineData("refund", false)]
    [InlineData("hold_failed", false)]
    [InlineData("complete", false)]
    [InlineData(null, false)]
    public void IsSettlementFailureAuditAction_CountsOnlyTheThreeFailureActions(string? action, bool expected)
    {
        MoneyAlarmLog.IsSettlementFailureAuditAction(action).Should().Be(expected);
    }
}

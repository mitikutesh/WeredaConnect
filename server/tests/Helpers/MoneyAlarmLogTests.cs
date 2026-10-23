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
    /// <summary>
    /// Every token a money metric filter counts in the escrow, escrow-cron, sync and webhook
    /// log groups (infra/lib/stacks/monitoring-stack.ts). Each contains an underscore, which the
    /// infra tests enforce.
    /// </summary>
    public static readonly string[] AlarmTokens =
    {
        "ESCROW_SETTLEMENT_FAILED", "MANUAL_PAYOUT_OVERDUE", "ADMIN_QUEUE_OVERDUE",
        "ORDER_ESCROW_CREATE_FAILED", "ORDER_ESCROW_LINK_FAILED", "ORDER_ESCROW_REFUND_SCHEDULE_FAILED",
        "ORDER_PAYMENT_UNMATCHED", "ORDER_PAYMENT_DUPLICATE", "ORDER_PAYMENT_UNPROCESSED",
        "ORDER_STOCK_DECREMENT_FAILED", "ORDER_MONEY_MISMATCH",
        // ESC-45: the inbox replay heartbeat; a spelled one would hide a stalled sweep.
        "TELEBIRR_INBOX_REPLAY_RUN",
        // FRD-03: RISK_SETTLEMENT_SKIPPED is counted from FRD-03, RISK_HOLD_ORDER_MISMATCH by FRD-16.
        "RISK_SETTLEMENT_SKIPPED", "RISK_HOLD_ORDER_MISMATCH",
        // FRD-04: counted by FRD-16 from the day FRAUD_HOLD_ENABLED is turned on.
        "RISK_TABLE_UNCONFIGURED",
        // FUP-27: the dispute mirror's failed legs, and a sync delivery dispute's freeze (dispute rate).
        "DISPUTE_PROPAGATION_FAILED", "DELIVERY_DISPUTE_FROZEN"
    };

    /// <summary>A client value that spells every alarm token.</summary>
    public static readonly string Hostile = string.Join("-", AlarmTokens);

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

        // Spaces, newlines and '=' are dropped, so a value cannot add a second field or break
        // the line; the id also loses its underscores, so it cannot spell a token; long values
        // are cut at 64 characters.
        line.Should().Be(
            "ESCROW_SETTLEMENT_FAILED escrowId=badidMANUAL-PAYOUT-OVERDUEx1 action=abc outcome=- "
            + $"code={new string('a', 64)}");
        line.Should().NotContain("\n");
    }

    [Fact]
    public void Lines_WithAClientIdThatSpellsTokens_CarryOnlyTheirOwnToken()
    {
        var settlement = MoneyAlarmLog.SettlementFailed($"escrow-order-{Hostile}", "release_failed", "failed", "TELEBIRR_TIMEOUT");
        var overdue = MoneyAlarmLog.ManualPayoutOverdue($"escrow-order-{Hostile}", "release:farmer", 26);
        var queue = MoneyAlarmLog.AdminQueueOverdue(Hostile, "undelivered", 30);

        TokensIn(settlement).Should().Equal("ESCROW_SETTLEMENT_FAILED");
        TokensIn(overdue).Should().Equal("MANUAL_PAYOUT_OVERDUE");
        TokensIn(queue).Should().Equal("ADMIN_QUEUE_OVERDUE");
    }

    [Fact]
    public void OrderMoneyMismatch_FormatsTokenAndFields_AndDefangsTheIds()
    {
        MoneyAlarmLog.OrderMoneyMismatch("ord-1", "escrow-order-ord-1", "delivered", "refunded", "cannot_follow")
            .Should().Be(
                "ORDER_MONEY_MISMATCH orderId=ord-1 escrowId=escrow-order-ord-1 orderStatus=delivered escrowStatus=refunded reason=cannot_follow");
        MoneyAlarmLog.OrderMoneyMismatch(null, null, null, null, "sweep_truncated")
            .Should().Be("ORDER_MONEY_MISMATCH orderId=- escrowId=- orderStatus=- escrowStatus=- reason=sweep_truncated");
        TokensIn(MoneyAlarmLog.OrderMoneyMismatch(Hostile, $"escrow-order-{Hostile}", "delivered", "completed", "write_failed"))
            .Should().Equal("ORDER_MONEY_MISMATCH");
    }

    [Fact]
    public void RiskLines_FormatTokenAndFields_AndCarryOnlyTheirOwnToken()
    {
        MoneyAlarmLog.RiskSettlementSkipped("escrow-order-ord-1", "auto-refund", "flag_off", "account_suspended")
            .Should().Be("RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=auto-refund reason=flag_off holdReason=account_suspended");
        MoneyAlarmLog.RiskSettlementSkipped("e-1", "dispute", "user_read_failed", null)
            .Should().Be("RISK_SETTLEMENT_SKIPPED escrowId=e-1 sweep=dispute reason=user_read_failed holdReason=-");
        MoneyAlarmLog.RiskHoldOrderMismatch("e-1", "release", "order:mismatch:in_transit,delivery:mismatch:inTransit")
            .Should().Be("RISK_HOLD_ORDER_MISMATCH escrowId=e-1 heldFor=release legs=order:mismatch:in-transit.delivery:mismatch:inTransit");

        TokensIn(MoneyAlarmLog.RiskSettlementSkipped($"escrow-order-{Hostile}", "auto-release", "flag_off", "account_suspended"))
            .Should().Equal("RISK_SETTLEMENT_SKIPPED");
        TokensIn(MoneyAlarmLog.RiskHoldOrderMismatch($"escrow-order-{Hostile}", "refund", Hostile))
            .Should().Equal("RISK_HOLD_ORDER_MISMATCH");

        // FRD-04: a party score that could not be read, and a gate that reads no tier while the flag is on.
        MoneyAlarmLog.RiskSettlementSkipped("e-1", "auto-release", "score_unavailable", null)
            .Should().Be("RISK_SETTLEMENT_SKIPPED escrowId=e-1 sweep=auto-release reason=score_unavailable holdReason=-");
        MoneyAlarmLog.RiskTableUnconfigured("auto_release_and_refund")
            .Should().Be("RISK_TABLE_UNCONFIGURED action=auto_release_and_refund");
        TokensIn(MoneyAlarmLog.RiskTableUnconfigured("auto_release_and_refund")).Should().Equal("RISK_TABLE_UNCONFIGURED");
    }

    [Fact]
    public void LogId_ClientValues_CannotSpellAToken()
    {
        foreach (var value in new[] { Hostile, $"escrow-order-{Hostile}", $"/escrow/{Hostile}", $"a b\n{Hostile}\r\t\"x\"" })
        {
            var logged = MoneyAlarmLog.LogId(value);

            TokensIn(logged).Should().BeEmpty();
            logged.Should().MatchRegex("^[A-Za-z0-9.:-]{1,64}$");
        }

        MoneyAlarmLog.LogId("1f0e7c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6").Should().Be("1f0e7c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6");
        MoneyAlarmLog.LogId(null).Should().Be("-");
        MoneyAlarmLog.LogId("___").Should().Be("---");
    }

    [Fact]
    public void LogText_MessagesThatEchoInput_CannotSpellATokenOrBreakTheLine()
    {
        var message = $"The input string '{Hostile}' was not in a correct format.\nnext line\t{Hostile}";

        var logged = MoneyAlarmLog.LogText(message);

        TokensIn(logged).Should().BeEmpty();
        logged.Should().NotContain("\n").And.NotContain("\t");
        logged.Should().StartWith("The input string 'ESCROW-SETTLEMENT-FAILED-");
        MoneyAlarmLog.LogText(new string('x', 500)).Should().HaveLength(200);
        MoneyAlarmLog.LogText(null).Should().Be("-");
        MoneyAlarmLog.LogText("\n").Should().Be("-");
    }

    [Fact]
    public void Defang_OnlyTurnsUnderscoresIntoDashes()
    {
        MoneyAlarmLog.Defang(Hostile).Should().NotContain("_");
        MoneyAlarmLog.Defang("+251900100003 a_b").Should().Be("+251900100003 a-b");
        MoneyAlarmLog.Defang(null).Should().BeNull();
    }

    private static IEnumerable<string> TokensIn(string line) => AlarmTokens.Where(line.Contains);

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

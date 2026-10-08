using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-13: the JSON audit line lands in the escrow and sync log groups, whose money alarm
/// filters count any line that contains a token, so the ids a client chooses are defanged.
/// </summary>
public class AuditLoggerTests
{
    [Fact]
    public void FormatAudit_ClientIdsThatSpellTokens_CannotFireAMoneyAlarm()
    {
        var hostile = MoneyAlarmLogTests.Hostile;

        var line = AuditLogger.FormatAudit("ESCROW", "DISPUTE",
            userId: hostile, correlationId: hostile, entityType: "escrow", entityId: hostile,
            outcome: "failure", errorCode: "ESCROW_038");

        MoneyAlarmLogTests.AlarmTokens.Where(line.Contains).Should().BeEmpty();
        line.Should().Contain("\"correlationId\":\"ESCROW-SETTLEMENT-FAILED-");
        line.Should().Contain("\"entityId\":\"ESCROW-SETTLEMENT-FAILED-");
        // Server values keep their spelling.
        line.Should().Contain("\"errorCode\":\"ESCROW_038\"").And.Contain("\"action\":\"DISPUTE\"");
    }

    [Fact]
    public void FormatAudit_OrdinaryIdsAreUnchanged()
    {
        var line = AuditLogger.FormatAudit("ESCROW", "RELEASE",
            userId: "1f0e7c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6", correlationId: "abc123def456",
            entityType: "escrow", entityId: "escrow-order-ord-1", outcome: "success");

        line.Should().Contain("\"userId\":\"1f0e7c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6\"")
            .And.Contain("\"correlationId\":\"abc123def456\"")
            .And.Contain("\"entityId\":\"escrow-order-ord-1\"");
    }
}

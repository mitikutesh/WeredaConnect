using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-27: the dispute lines the monitoring stack counts. DISPUTE_PROPAGATION_FAILED feeds the
/// dispute-propagation-failed alarm (escrow, sync and escrow-cron log groups), DELIVERY_DISPUTE_FROZEN
/// the escrow dispute-rate metric (sync log group). The infra tests match these exact lines.
/// </summary>
public class MoneyAlarmLogDisputeTests
{
    private static string Hostile => MoneyAlarmLogTests.Hostile;

    private static IEnumerable<string> TokensIn(string line) => MoneyAlarmLogTests.AlarmTokens.Where(line.Contains);

    [Fact]
    public void DisputePropagationFailed_FormatsTokenAndFields()
    {
        MoneyAlarmLog.DisputePropagationFailed("escrow-order-ord-1", "ord-1", "del-1", "order:conflict")
            .Should().Be("DISPUTE_PROPAGATION_FAILED escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1 legs=order:conflict");
    }

    [Fact]
    public void DisputePropagationFailed_LegsAreDefangedLikeTheVerdictLines()
    {
        // The mirror's legs text: brackets around exception names, commas between legs, and
        // order error codes with underscores. The log line keeps one field with no `_`.
        var line = MoneyAlarmLog.DisputePropagationFailed(
            "escrow-001", "ord-1", "del-1", "delivery:[TimeoutException],order:ORDER_NOT_FOUND");

        line.Should().Be(
            "DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 orderId=ord-1 deliveryId=del-1 "
            + "legs=delivery:TimeoutException.order:ORDER-NOT-FOUND");
        TokensIn(line).Should().Equal("DISPUTE_PROPAGATION_FAILED");
    }

    [Fact]
    public void DisputePropagationFailed_MissingIdsBecomeDash()
    {
        // The mirror may know neither the order nor the delivery when its lookup failed.
        MoneyAlarmLog.DisputePropagationFailed("escrow-001", null, "", "lookup:[AmazonDynamoDBException]")
            .Should().Be("DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 orderId=- deliveryId=- legs=lookup:AmazonDynamoDBException");
    }

    [Fact]
    public void DeliveryDisputeFrozen_FormatsTokenAndFields()
    {
        MoneyAlarmLog.DeliveryDisputeFrozen("escrow-order-ord-1", "ord-1", "del-1")
            .Should().Be("DELIVERY_DISPUTE_FROZEN escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1");
        MoneyAlarmLog.DeliveryDisputeFrozen("escrow-001", null, "del-1")
            .Should().Be("DELIVERY_DISPUTE_FROZEN escrowId=escrow-001 orderId=- deliveryId=del-1");
    }

    [Fact]
    public void DisputeLines_WithClientIdsThatSpellTokens_CarryOnlyTheirOwnToken()
    {
        // Delivery and order ids are chosen by the client (offline-first creates); the escrow id
        // of an order escrow embeds the order id. None may add a token, or a second field.
        var propagation = MoneyAlarmLog.DisputePropagationFailed(
            $"escrow-order-{Hostile}", Hostile, $"{Hostile} legs=x\nDELIVERY_DISPUTE_FROZEN", $"order:{Hostile}");
        var frozen = MoneyAlarmLog.DeliveryDisputeFrozen($"escrow-order-{Hostile}", Hostile, $"{Hostile}\nDISPUTE_PROPAGATION_FAILED");

        TokensIn(propagation).Should().Equal("DISPUTE_PROPAGATION_FAILED");
        TokensIn(frozen).Should().Equal("DELIVERY_DISPUTE_FROZEN");
        propagation.Should().NotContain("\n");
        frozen.Should().NotContain("\n");
        propagation.Split(' ').Should().HaveCount(5);
        frozen.Split(' ').Should().HaveCount(4);
    }

    [Fact]
    public void Tokens_ContainAnUnderscore_AndAreNotPartOfAnotherDisputeToken()
    {
        // A client value is defanged (`_` to `-`), so a token with `_` cannot be spelled. The
        // filters must also not count the verdict mirror's own lines, which the next run repairs.
        MoneyAlarmLog.DisputePropagationFailedToken.Should().Contain("_");
        MoneyAlarmLog.DeliveryDisputeFrozenToken.Should().Contain("_");
        foreach (var other in new[]
                 {
                     "DISPUTE_VERDICT_PROPAGATION_FAILED", "DISPUTE_PROPAGATION_AUDIT_FAILED",
                     "ESCROW_SWEEP_DISPUTE_FROZEN", "DELIVERY_DISPUTE_REFUSED", "DELIVERY_DISPUTE_RETRY"
                 })
        {
            other.Should().NotContain(MoneyAlarmLog.DisputePropagationFailedToken);
            other.Should().NotContain(MoneyAlarmLog.DeliveryDisputeFrozenToken);
        }
    }
}

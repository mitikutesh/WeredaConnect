using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>Order-lifecycle 2.26.0: the pure rules of an admin's commission change on one unpaid order.</summary>
public class OrderCommissionChangeTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.025", true)]
    [InlineData("0.0001", true)]
    [InlineData("0.2", true)]
    [InlineData("0.2001", false)]
    [InlineData("-0.0001", false)]
    [InlineData("0.00005", false)]
    [InlineData("1", false)]
    public void Rates_AreZeroToTwentyPercent_WithAtMostFourDecimals(string rate, bool valid)
        => OrderCommissionChange.IsValidRate(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(valid);

    [Theory]
    [InlineData(null, false)]
    [InlineData("   too short ", false)]
    [InlineData("  ten chars. ", true)]
    public void Reasons_AreTenToFiveHundredCharacters_OnceTrimmed(string? reason, bool valid)
    {
        OrderCommissionChange.IsValidReason(reason).Should().Be(valid);
        OrderCommissionChange.IsValidReason(new string('a', 500)).Should().BeTrue();
        OrderCommissionChange.IsValidReason(new string('a', 501)).Should().BeFalse();
    }

    [Theory]
    // 170 kg x 18.99: the commission rounds to the santim, halves away from zero, in decimals (no float noise).
    [InlineData("3228.3", "500", "0.05", "161.42", "3889.72")]
    [InlineData("3228.3", "500", "0.025", "80.71", "3809.01")]
    [InlineData("3228.3", "0", "0", "0", "3228.3")]
    [InlineData("40000", "1500", "0.2", "8000", "49500")]
    public void Compute_KeepsTheCropAndTransportCosts_AndRoundsTheCommission(
        string crop, string transport, string rate, string commission, string total)
    {
        static decimal D(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var money = OrderCommissionChange.Compute(D(crop), D(transport), D(rate));

        money.CommissionAmount.Should().Be(D(commission));
        money.OrderTotal.Should().Be(D(total));
    }

    [Theory]
    [InlineData("created", null, null, null)]
    [InlineData("pending_payment", null, "2026-11-26T07:59:59Z", null)]
    [InlineData("payment_submitted", null, null, "payment_submitted")]
    [InlineData("funded", null, null, "funded")]
    [InlineData("created", "2026-11-26T07:00:00Z", null, "already verified")]
    [InlineData("pending_payment", null, "2026-11-26T08:00:01Z", "on its way")]
    public void RefusalOf_AllowsOnlyAnOrderWithNoMoneyYet(string status, string? verifiedAt, string? holdUntil, string? refusal)
    {
        var result = OrderCommissionChange.RefusalOf(status, verifiedAt, holdUntil, 3228.3m, Now);

        if (refusal == null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().Contain(refusal);
        }
    }

    [Fact]
    public void RefusalOf_AnOrderWithoutACropCost_SaysSo()
        => OrderCommissionChange.RefusalOf("created", null, null, 0m, Now).Should().Contain("no crop cost");

    [Fact]
    public void IsReplay_MatchesTheSameRateFromTheSameVersion_Only()
    {
        var order = new Dictionary<string, object>
        {
            [OrderCommissionChange.Field] = OrderCommissionChange.Stamp(0.05m, 0.025m, 3889.72m, 2, "2026-11-26T08:00:00Z")
        };

        OrderCommissionChange.IsReplay(order, 2, 0.025m).Should().BeTrue();
        OrderCommissionChange.IsReplay(order, 3, 0.025m).Should().BeFalse();
        OrderCommissionChange.IsReplay(order, 2, 0.03m).Should().BeFalse();
        OrderCommissionChange.IsReplay(new Dictionary<string, object>(), 2, 0.025m).Should().BeFalse();
        OrderCommissionChange.BeforeOf(order).Should().Be((0.05m, 3889.72m));
        OrderCommissionChange.BeforeOf(new Dictionary<string, object>()).Should().BeNull();
    }
}

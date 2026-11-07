using System.Globalization;
using FluentAssertions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-15 (fraud-detection-architecture.md section 15.2): the relationship rules as pure decisions. One
/// identity on both sides, a shared install, a repeated pair, and the settings' ranges.
/// </summary>
public class RelationshipPatternRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static UserDto User(string id, string phone, string? fayda = null, params string[] devices) => new()
    {
        UserId = id,
        PhoneNumber = phone,
        Role = "buyer",
        Woreda = "Ada'a",
        FaydaId = fayda,
        IsPhoneVerified = true,
        KnownDevices = devices.ToList(),
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private static Dictionary<string, object> Order(
        string id, string farmerId, double daysAgo, bool funded = true, string status = "accepted", string? farmerResponse = null)
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = id,
            ["buyerId"] = "buyer-1",
            ["farmerId"] = farmerId,
            ["status"] = status,
            ["createdAt"] = Now.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture)
        };
        if (funded)
        {
            order["paymentVerifiedAt"] = Now.AddDays(-daysAgo).AddMinutes(5).ToString("o", CultureInfo.InvariantCulture);
        }

        if (farmerResponse != null)
        {
            order["farmerResponse"] = farmerResponse;
        }

        return order;
    }

    // ── One identity ─────────────────────────────────────────

    [Fact]
    public void SelfTrade_SameUserId()
    {
        RelationshipPatternRules.SelfTradeReason("u-1", "u-1", null, null).Should().Be("same_user");
    }

    [Fact]
    public void SelfTrade_SamePhone()
    {
        RelationshipPatternRules.SelfTradeReason("b", "f", User("b", "+251900000001"), User("f", " +251900000001 "))
            .Should().Be("same_phone");
    }

    [Fact]
    public void SelfTrade_SameFaydaId()
    {
        RelationshipPatternRules.SelfTradeReason("b", "f", User("b", "+251900000001", "FAYDA-1"), User("f", "+251900000002", "FAYDA-1"))
            .Should().Be("same_fayda_id");
    }

    [Fact]
    public void SelfTrade_DifferentIdentities_IsNull()
    {
        RelationshipPatternRules.SelfTradeReason("b", "f", User("b", "+251900000001", "FAYDA-1"), User("f", "+251900000002", "FAYDA-2"))
            .Should().BeNull();
    }

    [Fact]
    public void SelfTrade_EmptyValuesAndMissingRows_NeverMatch()
    {
        // A tombstoned account (ACC-02) has no phone; two of them must not read as one person.
        RelationshipPatternRules.SelfTradeReason("b", "f", User("b", ""), User("f", "")).Should().BeNull();
        RelationshipPatternRules.SelfTradeReason("b", "f", User("b", "+251900000001", ""), User("f", "+251900000002", "")).Should().BeNull();
        RelationshipPatternRules.SelfTradeReason("b", "f", null, User("f", "+251900000002")).Should().BeNull();
    }

    // ── Shared install ───────────────────────────────────────

    [Fact]
    public void SharedDevices_CountsTheInstallsBothSignedInOn()
    {
        RelationshipPatternRules.SharedDeviceCount(
                User("b", "+1", null, "dev-a", "dev-b", "dev-b"), User("f", "+2", null, "dev-b", "dev-a", "dev-c"))
            .Should().Be(2);
    }

    [Fact]
    public void SharedDevices_NoneOrMissing_IsZero()
    {
        RelationshipPatternRules.SharedDeviceCount(User("b", "+1", null, "dev-a"), User("f", "+2", null, "dev-z")).Should().Be(0);
        RelationshipPatternRules.SharedDeviceCount(User("b", "+1"), User("f", "+2", null, "dev-a")).Should().Be(0);
        RelationshipPatternRules.SharedDeviceCount(null, User("f", "+2", null, "dev-a")).Should().Be(0);
        RelationshipPatternRules.SharedDeviceCount(User("b", "+1", null, " "), User("f", "+2", null, " ")).Should().Be(0);
    }

    // ── Repeated pair ────────────────────────────────────────

    [Fact]
    public void PairCount_CountsAcceptedFundedOrdersWithThisFarmer_InTheWindowEndingAtTheExaminedOrder()
    {
        var history = new List<Dictionary<string, object>>
        {
            Order("o-1", "farmer-1", 1),                                           // legacy accepted row: counts
            Order("o-2", "farmer-1", 3, status: "settled"),                        // legacy, past accepted: counts
            Order("o-3", "farmer-2", 2),                                           // another farmer
            Order("o-4", "farmer-1", 2, funded: false),                            // never paid
            Order("o-5", "farmer-1", 8),                                           // before the 7 day window
            Order("o-6", "farmer-1", 4, status: "rejected"),                       // the farmer declined it
            Order("o-7", "farmer-1", 5, status: "cancelled", farmerResponse: "expired"),
            Order("o-8", "farmer-1", 6, status: "cancelled", farmerResponse: "declined"),
            Order("o-9", "farmer-1", 2, status: "funded", farmerResponse: "pending"), // still waiting for the farmer
            Order("o-10", "farmer-1", 2, status: "funded"),                        // legacy funded: not answered yet
        };

        RelationshipPatternRules.PairOrderCount(history, Order("o-11", "farmer-1", 0.1), "farmer-1",
            RelationshipPatternSettings.Default).Should().Be(3);
    }

    [Fact]
    public void PairCount_TheExaminedOrderCountsOnce_EvenWhenTheIndexShowsIt()
    {
        var examined = Order("o-1", "farmer-1", 0.1);
        RelationshipPatternRules.PairOrderCount(new[] { examined, Order("o-2", "farmer-1", 1) }, examined, "farmer-1",
            RelationshipPatternSettings.Default).Should().Be(2);
    }

    [Fact]
    public void PairCount_AFundedCancelAfterAcceptingStillCounts()
    {
        // The farmer took the order on, then the transport failed: the trade involved both of them.
        var history = new[] { Order("o-1", "farmer-1", 1, status: "cancelled", farmerResponse: "accepted") };

        RelationshipPatternRules.PairOrderCount(history, Order("o-2", "farmer-1", 0.1), "farmer-1",
            RelationshipPatternSettings.Default).Should().Be(2);
    }

    [Theory]
    [InlineData("funded", "pending")]       // the farmer has not answered
    [InlineData("funded", null)]            // the same on a row from before the commitments
    [InlineData("rejected", "declined")]    // the farmer declined it
    [InlineData("rejected", null)]
    public void PairCount_IsZero_WhenTheExaminedOrderItselfDoesNotCount(string status, string? farmerResponse)
    {
        var accepted = new[] { Order("o-1", "farmer-1", 1), Order("o-2", "farmer-1", 2), Order("o-3", "farmer-1", 3) };

        RelationshipPatternRules.PairOrderCount(accepted, Order("o-4", "farmer-1", 0.1, status: status, farmerResponse: farmerResponse),
            "farmer-1", RelationshipPatternSettings.Default).Should().Be(0);
    }

    [Fact]
    public void PairCount_CountsOnlyOrdersCreatedAtOrBeforeTheExaminedOne()
    {
        // A burst of three accepted orders: only the last one completes the count.
        var burst = new[] { Order("o-1", "farmer-1", 1.5), Order("o-2", "farmer-1", 1), Order("o-3", "farmer-1", 0.5) };

        RelationshipPatternRules.PairOrderCount(burst, burst[0], "farmer-1", RelationshipPatternSettings.Default).Should().Be(1);
        RelationshipPatternRules.PairOrderCount(burst, burst[1], "farmer-1", RelationshipPatternSettings.Default).Should().Be(2);
        RelationshipPatternRules.PairOrderCount(burst, burst[2], "farmer-1", RelationshipPatternSettings.Default).Should().Be(3);
    }

    [Fact]
    public void PairCount_TheWindowEndsAtTheExaminedOrder_NotAtNow()
    {
        // Examined 2 days ago: an order 8.5 days ago is inside its 7 days, one 9.5 days ago is not.
        var history = new[] { Order("o-1", "farmer-1", 8.5), Order("o-2", "farmer-1", 9.5) };

        RelationshipPatternRules.PairOrderCount(history, Order("o-3", "farmer-1", 2), "farmer-1",
            RelationshipPatternSettings.Default).Should().Be(2);
    }

    [Theory]
    [InlineData("rejected", null, true)]
    [InlineData("cancelled", "declined", true)]
    [InlineData("accepted", null, false)]
    [InlineData("funded", "pending", false)]
    [InlineData("cancelled", "expired", false)]
    public void FarmerRefused_IsARejectOrADecline(string status, string? farmerResponse, bool refused)
    {
        RelationshipPatternRules.FarmerRefused(Order("o-1", "farmer-1", 1, status: status, farmerResponse: farmerResponse))
            .Should().Be(refused);
    }

    // ── Evaluate ─────────────────────────────────────────────

    [Fact]
    public void Evaluate_OneIdentity_IsSelfTrade_NotAlsoSharedDevice()
    {
        var hits = RelationshipPatternRules.Evaluate("o-1", "b", "f",
            User("b", "+251900000001", null, "dev-a"), User("f", "+251900000001", null, "dev-a"), 1, RelationshipPatternSettings.Default);

        hits.Should().ContainSingle();
        hits[0].SignalType.Should().Be("SELF_TRADE");
        hits[0].UserIds.Should().Equal("b", "f");
        hits[0].Description.Should().Contain("same_phone").And.Contain("o-1").And.NotContain("+251");
    }

    [Fact]
    public void Evaluate_SharedDevice_NotOnAnOrderTheFarmerRefused()
    {
        RelationshipPatternRules.Evaluate("o-1", "b", "f",
                User("b", "+1", null, "dev-a"), User("f", "+2", null, "dev-a"), 0, RelationshipPatternSettings.Default,
                farmerRefused: true)
            .Should().BeEmpty();
        // One identity on both sides is flagged whatever the farmer answered.
        RelationshipPatternRules.Evaluate("o-1", "b", "f",
                User("b", "+1", "F-1", "dev-a"), User("f", "+2", "F-1", "dev-a"), 0, RelationshipPatternSettings.Default,
                farmerRefused: true)
            .Select(h => h.SignalType).Should().Equal("SELF_TRADE");
    }

    [Fact]
    public void Evaluate_SharedDevice()
    {
        var hits = RelationshipPatternRules.Evaluate("o-1", "b", "f",
            User("b", "+1", null, "dev-a"), User("f", "+2", null, "dev-a"), 1, RelationshipPatternSettings.Default);

        hits.Should().ContainSingle();
        hits[0].SignalType.Should().Be("SHARED_DEVICE_TRADE");
        hits[0].Description.Should().Contain("1 shared").And.NotContain("dev-a");
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, true)]
    public void Evaluate_RepeatedPair_AtTheThreshold(int count, bool hit)
    {
        var hits = RelationshipPatternRules.Evaluate("o-1", "b", "f",
            User("b", "+1"), User("f", "+2"), count, RelationshipPatternSettings.Default);

        hits.Any(h => h.SignalType == "REPEATED_PAIR").Should().Be(hit);
    }

    [Fact]
    public void Evaluate_SameUserId_WritesOnThatUserOnce()
    {
        var hits = RelationshipPatternRules.Evaluate("o-1", "u", "u", null, null, 3, RelationshipPatternSettings.Default);

        hits.Select(h => h.SignalType).Should().Equal("SELF_TRADE", "REPEATED_PAIR");
        hits.Should().OnlyContain(h => h.UserIds.Count == 1 && h.UserIds[0] == "u");
    }

    [Fact]
    public void Evaluate_NoPattern_NoHit()
    {
        RelationshipPatternRules.Evaluate("o-1", "b", "f", User("b", "+1", "F-1", "dev-a"), User("f", "+2", "F-2", "dev-b"), 2,
            RelationshipPatternSettings.Default).Should().BeEmpty();
        RelationshipPatternRules.Evaluate("o-1", "b", "f", null, null, null, RelationshipPatternSettings.Default).Should().BeEmpty();
    }

    // ── Settings ─────────────────────────────────────────────

    [Fact]
    public void Settings_Defaults()
    {
        RelationshipPatternSettings.From(_ => null).Should().Be(new RelationshipPatternSettings(48, 1000, 3, 7));
    }

    [Theory]
    [InlineData("FRAUD_RELATIONSHIP_LOOKBACK_HOURS", "168")]
    [InlineData("FRAUD_RELATIONSHIP_MAX_ORDERS", "10000")]
    [InlineData("FRAUD_PAIR_MIN_ORDERS", "2")]
    [InlineData("FRAUD_PAIR_WINDOW_DAYS", "90")]
    public void Settings_AValueInRangeOverrides(string name, string value)
    {
        RelationshipPatternSettings.From(n => n == name ? value : null).Should().NotBe(RelationshipPatternSettings.Default);
    }

    [Theory]
    [InlineData("FRAUD_RELATIONSHIP_LOOKBACK_HOURS", "0")]
    [InlineData("FRAUD_RELATIONSHIP_LOOKBACK_HOURS", "169")]
    [InlineData("FRAUD_RELATIONSHIP_MAX_ORDERS", "10001")]
    [InlineData("FRAUD_PAIR_MIN_ORDERS", "1")]
    [InlineData("FRAUD_PAIR_WINDOW_DAYS", "abc")]
    [InlineData("FRAUD_PAIR_WINDOW_DAYS", "2.5")]
    public void Settings_AValueOutOfRangeKeepsTheDefault(string name, string value)
    {
        RelationshipPatternSettings.From(n => n == name ? value : null).Should().Be(RelationshipPatternSettings.Default);
    }

    [Fact]
    public void ScannedStatuses_AreEveryStatusThatCanHoldAVerifiedPayment()
    {
        RelationshipPatternRules.ScannedOrderStatuses.Should().BeEquivalentTo(
            "funded", "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing", "settled",
            "awaiting_transport", "disputed", "dispute_resolved", "cancelled", "rejected");
        RelationshipPatternRules.ScannedOrderStatuses.Should().NotContain(
            new[] { "created", "pending_payment", "payment_submitted", "payment_failed" });
    }
}

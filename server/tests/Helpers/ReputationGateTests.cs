using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-29: the pure checks behind <c>REPUTATION_RESTRICTED</c>, the owner fields of a new row and
/// the aggregate read from a stored map (reputation-api.md sections 7 and 8, architecture doc 6.6).
/// </summary>
public class ReputationGateTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ReputationPolicySettings S = ReputationPolicySettings.Defaults;

    private static UserDto User(string role = "farmer", bool suspended = false, ReputationSnapshot? reputation = null,
        long sum = 0, long count = 0, string? aggregatedAt = "2026-11-01T00:00:00.000Z") => new()
        {
            UserId = "u-1",
            PhoneNumber = "+251900000000",
            Role = role,
            Woreda = "Jimma",
            IsPhoneVerified = true,
            Suspended = suspended,
            Reputation = reputation,
            RatingSum = sum,
            RatingCount = count,
            RatingsAggregatedAt = aggregatedAt,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        };

    private static ReputationSnapshot Restricted(DateTime? until, string cause = ReputationCauses.Points) => new()
    {
        Tier = ReputationTiers.Restricted,
        ActivePoints = 10,
        HasRestriction = true,
        RestrictionCause = cause,
        RestrictionSince = Now.AddDays(-1),
        RestrictionUntil = until
    };

    [Fact]
    public void CheckOwn_NoRowOrNoMap_PassesEveryRole()
    {
        ReputationGate.CheckOwn(null, Now).Should().BeNull();
        foreach (var role in new[] { "farmer", "trucker", "buyer" })
        {
            ReputationGate.CheckOwn(User(role), Now).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public void CheckOwn_ARestrictedParty_IsRefused_WithTheEnd(string role)
    {
        var until = new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);

        ReputationGate.CheckOwn(User(role, reputation: Restricted(until)), Now).Should().Be(
            "REPUTATION_RESTRICTED: until=2026-11-21T09:00:00.000Z: Your account can't take new orders until 21 Nov 2026. Your current orders continue.");
    }

    [Fact]
    public void CheckOwn_UnderReview_HasNoDate()
        => ReputationGate.CheckOwn(User(reputation: Restricted(null, ReputationCauses.DeliveryOverdue)), Now)
            .Should().StartWith("REPUTATION_RESTRICTED: until=review: ");

    [Fact]
    public void CheckOwn_AnEndedRestriction_Passes_AtTheBoundary()
    {
        ReputationGate.CheckOwn(User(reputation: Restricted(Now)), Now).Should().BeNull("until is exclusive");
        ReputationGate.CheckOwn(User(reputation: Restricted(Now.AddSeconds(1))), Now).Should().NotBeNull();
    }

    [Fact]
    public void CheckOwn_ALowTierWithoutARestriction_Passes()
        => ReputationGate.CheckOwn(User(reputation: new ReputationSnapshot { Tier = ReputationTiers.Low, ActivePoints = 8 }), Now)
            .Should().BeNull();

    [Fact]
    public void CheckCounterparty_RestrictedOrSuspended_IsRefused_WithNoDates()
    {
        ReputationGate.CheckCounterparty(User(reputation: Restricted(Now.AddDays(3))), Now).Should().Be(ReputationGate.CounterpartyMessage);
        ReputationGate.CheckCounterparty(User(suspended: true), Now).Should().Be(ReputationGate.CounterpartyMessage);
        ReputationGate.CounterpartyMessage.Should().NotContain("until");
        ReputationGate.CheckCounterparty(User(), Now).Should().BeNull();
        ReputationGate.CheckCounterparty(null, Now).Should().BeNull();
        ReputationGate.CheckCounterparty(User(reputation: Restricted(Now.AddDays(-1))), Now).Should().BeNull();
    }

    [Theory]
    [InlineData("order", "ORDER_VALIDATION: This farmer or trucker can't take new orders right now.")]
    [InlineData("crop_listing", "SYNC_VALIDATION: This farmer or trucker can't take new orders right now.")]
    public void ForClient_NoBuildHeader_UsesACodeOldAppsKnow(string entity, string expected)
    {
        ReputationGate.ForClient(ReputationGate.CounterpartyMessage, null, entity).Should().Be(expected);
        ReputationGate.ForClient(ReputationGate.CounterpartyMessage, 2, entity).Should().Be(ReputationGate.CounterpartyMessage);
    }

    [Fact]
    public void ForClient_KeepsTheWholeSentence_OfTheOwnRefusal()
    {
        var message = ReputationGate.CheckOwn(User(reputation: Restricted(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc))), Now)!;

        ReputationGate.ForClient(message, null, "trip").Should().Be(
            "SYNC_VALIDATION: Your account can't take new orders until 21 Nov 2026. Your current orders continue.");
    }

    [Fact]
    public void OwnerFields_NoMap_AreGood_WithTheStoredRatings()
    {
        var fields = ReputationGate.OwnerFields(User(sum: 9, count: 2), Now, S);

        fields.Tier.Should().Be(ReputationTiers.Good);
        fields.EffectiveRating.Should().Be(4.5m);
        fields.RatingCount.Should().Be(2);
        fields.ReliabilityPct.Should().BeNull();
        fields.At.Should().Be(Now);
    }

    [Fact]
    public void OwnerFields_AnUnstampedRow_CarriesNoRating()
    {
        // The backfill skipped this user: the stored sum holds only the ratings since the deploy.
        var fields = ReputationGate.OwnerFields(User(sum: 5, count: 1, aggregatedAt: null), Now, S);

        fields.EffectiveRating.Should().BeNull();
        fields.RatingCount.Should().Be(0);
    }

    [Fact]
    public void OwnerFields_ASuspendedOwner_IsRestricted()
        => ReputationGate.OwnerFields(User(suspended: true), Now, S).Tier.Should().Be(ReputationTiers.Restricted);

    [Fact]
    public void FromSnapshot_UsesTheLivePolicy_AndDropsWhatEnded()
    {
        var snapshot = new ReputationSnapshot
        {
            Tier = ReputationTiers.Restricted,
            ActivePoints = 7,
            CompletedOrders90d = 5,
            FaultCancels90d = 1,
            NoShows90d = 0,
            HasRestriction = true,
            RestrictionCause = ReputationCauses.Points,
            RestrictionSince = Now.AddDays(-14),
            RestrictionUntil = Now.AddMinutes(-1),
            OverrideTag = ReputationCauses.TagClear,
            OverrideUntil = Now.AddDays(2)
        };

        var aggregate = ReputationPolicy.FromSnapshot(snapshot, (44, 10), suspended: false, Now, S);

        aggregate.Restriction.Should().BeNull("the restriction ended a minute ago");
        aggregate.Tier.Should().Be(ReputationTiers.Warning, "7 points are low, and the admin's clear override hides the tag");
        aggregate.EffectiveRating.Should().Be(3.7m);
        aggregate.ReliabilityPct.Should().Be(83);

        var later = ReputationPolicy.FromSnapshot(snapshot, (44, 10), suspended: false, Now.AddDays(3), S);
        later.Tier.Should().Be(ReputationTiers.Low, "the override ended too");

        ReputationPolicy.FromSnapshot(null, (0, 0), suspended: false, Now, S).Tier.Should().Be(ReputationTiers.Good);
        ReputationPolicy.FromSnapshot(snapshot with { RestrictionUntil = Now.AddDays(1) }, (0, 0), false, Now, S)
            .Tier.Should().Be(ReputationTiers.Restricted);
    }
}

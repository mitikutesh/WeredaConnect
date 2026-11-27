using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-33 (fraud-detection-architecture.md 17.5): the payout delay reads the payee's reputation tier from
/// the users row. With <c>reputationPenalties</c> on, a payee whose tier is <c>low</c> waits as new and a
/// restricted payee as flagged. With the flag off (every environment today) the classes and the reads are
/// exactly FRD-13's.
/// </summary>
public class PayoutDelayReputationTests
{
    private const string FarmerId = "farmer-1";

    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PayoutDelaySettings Hours = new(24, 72, 96, 30, 3);

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();

    public PayoutDelayReputationTests()
    {
        Score(0);
        var completed = Enumerable.Range(0, 3)
            .Select(i => new Dictionary<string, object> { { "escrowId", $"done-{i}" }, { "status", "completed" } }).ToList();
        _escrows.QueryByFarmerAsync(FarmerId, "completed", 50, null).Returns((completed, (Dictionary<string, AttributeValue>?)null));
    }

    private void Score(int score, RiskTier tier = RiskTier.Low)
        => _fraud.GetUserRiskScoreAsync(FarmerId).Returns(new RiskScoreResult { Score = score, EnforcementScore = score, Tier = tier });

    private void Payee(ReputationSnapshot? reputation)
        => _users.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            CreatedAt = Now.AddDays(-365).ToString("o"),
            UpdatedAt = Now.AddDays(-365).ToString("o"),
            Version = 1,
            Reputation = reputation
        });

    private static ReputationSnapshot Tier(string tier) => new() { Tier = tier, ActivePoints = 6 };

    private static ReputationSnapshot Restricted(DateTime? until, string storedTier = ReputationTiers.Restricted) => new()
    {
        Tier = storedTier,
        ActivePoints = 10,
        HasRestriction = true,
        RestrictionCause = "points",
        RestrictionSince = Now.AddDays(-2),
        RestrictionUntil = until
    };

    private async Task<PayoutDelayDecision> DecideAsync(bool flag)
    {
        var policy = new PayoutDelayPolicy(_fraud, _users, _escrows, Hours, RiskScoreSettings.Default) { ReadsReputationTier = flag };
        var escrow = new Dictionary<string, object> { { "escrowId", "esc-1" }, { "farmerId", FarmerId }, { "farmerPayout", 7_800m } };
        return await policy.DecideAsync(escrow, null, Now, Now.AddHours(48), Now);
    }

    private static ReputationSnapshot? SnapshotOf(string name) => name switch
    {
        "good" => Tier(ReputationTiers.Good),
        "warning" => Tier(ReputationTiers.Warning),
        "low" => Tier(ReputationTiers.Low),
        "restricted" => Restricted(Now.AddDays(12)),
        "under review" => Restricted(null),
        _ => null
    };

    // ── the flag off: exactly FRD-13 ─────────────────────────────────

    [Theory]
    [InlineData("none")]
    [InlineData("good")]
    [InlineData("warning")]
    [InlineData("low")]
    [InlineData("restricted")]
    [InlineData("under review")]
    public async Task FlagOff_ATrustedPayeeStaysTrusted_WhateverTheReputation(string name)
    {
        Payee(SnapshotOf(name));

        var decision = await DecideAsync(flag: false);

        decision.Farmer.Should().Be(PayoutDelayClass.Trusted, name);
        decision.DelayHours.Should().Be(24);
        decision.ReleaseNotBefore.Should().Be(Now.AddHours(48), "the dispute window end, as before");
        await _users.Received(1).GetUserByIdAsync(FarmerId);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("good")]
    [InlineData("warning")]
    [InlineData("low")]
    [InlineData("restricted")]
    [InlineData("under review")]
    public async Task FlagOff_APayeeWithAScoreIsNew_AndTheUsersRowIsNotRead(string name)
    {
        Payee(SnapshotOf(name));
        Score(25);

        var decision = await DecideAsync(flag: false);

        decision.Farmer.Should().Be(PayoutDelayClass.New, name);
        decision.DelayHours.Should().Be(72);
        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);
        await _escrows.DidNotReceiveWithAnyArgs().QueryByFarmerAsync(default!, default, default, default);
    }

    [Fact]
    public async Task FlagOff_IsTheDefaultOfThePolicy()
    {
        // No environment sets REPUTATION_PENALTIES_ENABLED in the tests, as in every environment today.
        new PayoutDelayPolicy(_fraud, _users, _escrows).ReadsReputationTier.Should().BeFalse();
        Payee(Restricted(Now.AddDays(12)));

        var policy = new PayoutDelayPolicy(_fraud, _users, _escrows, Hours, RiskScoreSettings.Default);
        var escrow = new Dictionary<string, object> { { "escrowId", "esc-1" }, { "farmerId", FarmerId }, { "farmerPayout", 7_800m } };

        (await policy.DecideAsync(escrow, null, Now, Now.AddHours(48), Now)).Farmer.Should().Be(PayoutDelayClass.Trusted);
    }

    // ── the flag on ──────────────────────────────────────────────────

    [Theory]
    [InlineData("none", "Trusted", 24)]
    [InlineData("good", "Trusted", 24)]
    [InlineData("warning", "Trusted", 24)]
    [InlineData("low", "New", 72)]
    [InlineData("restricted", "Flagged", 96)]
    [InlineData("under review", "Flagged", 96)]
    public async Task FlagOn_ACleanScore_TheTierDecides(string name, string expected, int hours)
    {
        Payee(SnapshotOf(name));

        var decision = await DecideAsync(flag: true);

        decision.Farmer.ToString().Should().Be(expected);
        decision.DelayHours.Should().Be(hours);
        await _users.Received(1).GetUserByIdAsync(FarmerId);
    }

    [Fact]
    public async Task FlagOn_ATrustedPayee_CostsNoExtraRead()
    {
        Payee(null);

        await DecideAsync(flag: true);

        await _users.Received(1).GetUserByIdAsync(FarmerId);
        await _fraud.Received(1).GetUserRiskScoreAsync(FarmerId);
        await _escrows.Received(1).QueryByFarmerAsync(FarmerId, "completed", 50, null);
    }

    [Fact]
    public async Task FlagOn_ARestrictedPayeeWithAScore_WaitsAsFlagged_WithOneRowRead()
    {
        Payee(Restricted(Now.AddDays(12)));
        Score(25);

        var decision = await DecideAsync(flag: true);

        decision.Farmer.Should().Be(PayoutDelayClass.Flagged);
        decision.DelayHours.Should().Be(96);
        await _users.Received(1).GetUserByIdAsync(FarmerId);
        await _escrows.DidNotReceiveWithAnyArgs().QueryByFarmerAsync(default!, default, default, default);
    }

    [Theory]
    [InlineData("good")]
    [InlineData("low")]
    public async Task FlagOn_APayeeWithAScoreAndNoRestriction_IsStillNew(string tier)
    {
        Payee(Tier(tier));
        Score(25);

        (await DecideAsync(flag: true)).Farmer.Should().Be(PayoutDelayClass.New);
    }

    [Fact]
    public async Task FlagOn_ARiskFlaggedPayee_NeedsNoRowRead()
    {
        Payee(null);
        Score(70, RiskTier.High);

        (await DecideAsync(flag: true)).Farmer.Should().Be(PayoutDelayClass.Flagged);

        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);
    }

    [Fact]
    public async Task FlagOn_AnEndedRestriction_WaitsAsNewUntilTheStoredTierIsRewritten()
    {
        // The restriction ended an hour ago; the daily job has not rewritten the stored tier yet.
        Payee(Restricted(Now.AddHours(-1)));

        (await DecideAsync(flag: true)).Farmer.Should().Be(PayoutDelayClass.New);
    }

    [Fact]
    public async Task FlagOn_AScoredPayeeWhoseRowIsMissing_IsUnscored()
    {
        _users.GetUserByIdAsync(FarmerId).Returns((UserDto?)null);
        Score(25);

        var decision = await DecideAsync(flag: true);

        decision.Farmer.Should().Be(PayoutDelayClass.Unscored);
        decision.DelayHours.Should().Be(96, "a row that cannot be read gives the longest delay, never a shorter one");
    }

    [Fact]
    public async Task FlagOn_NeverShortensTheDisputeWindow_AndNeverThrows()
    {
        _users.GetUserByIdAsync(FarmerId).Returns<UserDto?>(_ => throw new InvalidOperationException("down"));

        var decision = await DecideAsync(flag: true);

        decision.Farmer.Should().Be(PayoutDelayClass.Unscored);
        decision.ReleaseNotBefore.Should().Be(Now.AddHours(96));
    }

    [Fact]
    public void ReputationWait_ReadsTheStoredMapOnly()
    {
        PayoutDelayPolicy.ReputationWait(null, Now).Should().BeNull();
        PayoutDelayPolicy.ReputationWait(Tier(ReputationTiers.Warning), Now).Should().BeNull();
        PayoutDelayPolicy.ReputationWait(Tier(ReputationTiers.Low), Now).Should().Be(PayoutDelayClass.New);
        PayoutDelayPolicy.ReputationWait(Restricted(Now.AddSeconds(1)), Now).Should().Be(PayoutDelayClass.Flagged);
        PayoutDelayPolicy.ReputationWait(Restricted(Now), Now).Should().Be(PayoutDelayClass.New);
        // A restriction on the map decides even when the stored tier lags behind it.
        PayoutDelayPolicy.ReputationWait(Restricted(null, storedTier: ReputationTiers.Good), Now).Should().Be(PayoutDelayClass.Flagged);
    }
}

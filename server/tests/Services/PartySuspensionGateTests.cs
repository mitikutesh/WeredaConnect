using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>FRD-03 (fraud-detection-architecture.md 17.2, 18.1): the suspension gate the settlement sweeps ask.</summary>
public class PartySuspensionGateTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static UserDto User(string id, bool suspended) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000001",
        Role = "buyer",
        Woreda = "w",
        IsPhoneVerified = true,
        Suspended = suspended,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private static Dictionary<string, object> Escrow(bool withTrucker = true)
    {
        var escrow = new Dictionary<string, object> { { "escrowId", "e-1" }, { "buyerId", "b" }, { "farmerId", "f" } };
        if (withTrucker)
        {
            escrow["truckerId"] = "t";
        }

        return escrow;
    }

    private void Users(bool buyer = false, bool farmer = false, bool trucker = false)
    {
        _users.GetUserByIdAsync("b").Returns(User("b", buyer));
        _users.GetUserByIdAsync("f").Returns(User("f", farmer));
        _users.GetUserByIdAsync("t").Returns(User("t", trucker));
    }

    [Fact]
    public async Task NoPartySuspended_Settles()
    {
        Users();

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.Settles.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, false, "buyer")]
    [InlineData(false, true, false, "farmer")]
    [InlineData(false, false, true, "trucker")]
    public async Task AnySuspendedParty_HoldsForAccountSuspended(bool buyer, bool farmer, bool trucker, string party)
    {
        Users(buyer, farmer, trucker);

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.Settles.Should().BeFalse();
        decision.HoldReason.Should().Be("account_suspended");
        decision.SkipReason.Should().BeNull();
        decision.Party.Should().Be(party);
    }

    [Fact]
    public async Task AnEscrowWithoutATrucker_ReadsOnlyTheBuyerAndTheFarmer()
    {
        Users();

        await new PartySuspensionGate(_users).CheckAsync(Escrow(withTrucker: false));

        await _users.DidNotReceive().GetUserByIdAsync("t");
    }

    private static Dictionary<string, object> LegacyEscrow(decimal truckerPayout)
        => new() { { "escrowId", "e-2" }, { "buyerId", "b" }, { "farmerId", "f" }, { "truckerPayout", truckerPayout } };

    [Fact]
    public async Task ATruckerLegWithoutTruckerId_UsesThePayoutLookup()
    {
        Users(trucker: true);
        var gate = new PartySuspensionGate(_users, _ => Task.FromResult<string?>("t"));

        var decision = await gate.CheckAsync(LegacyEscrow(500m));

        decision.HoldReason.Should().Be("account_suspended");
        decision.Party.Should().Be("trucker");
    }

    [Fact]
    public async Task NoTruckerLeg_NoLookup()
    {
        Users(trucker: true);
        var looked = 0;
        var gate = new PartySuspensionGate(_users, _ => { looked++; return Task.FromResult<string?>("t"); });

        (await gate.CheckAsync(LegacyEscrow(0m))).Settles.Should().BeTrue();
        looked.Should().Be(0);
    }

    [Fact]
    public async Task ALookupThatThrows_SkipsForTheRun()
    {
        Users();
        var gate = new PartySuspensionGate(_users, _ => throw new InvalidOperationException("ddb"));

        var decision = await gate.CheckAsync(LegacyEscrow(500m));

        decision.SkipReason.Should().Be("user_read_failed");
        decision.Party.Should().Be("trucker");
    }

    [Fact]
    public async Task APartyWithoutAUserRow_IsNotSuspended()
    {
        Users();
        _users.GetUserByIdAsync("f").Returns((UserDto?)null);

        (await new PartySuspensionGate(_users).CheckAsync(Escrow())).Settles.Should().BeTrue();
    }

    [Fact]
    public async Task ARowThatCannotBeRead_SkipsForTheRun_EvenWithAnotherPartySuspended()
    {
        Users(buyer: true);
        _users.GetUserByIdAsync("f").ThrowsAsync(new InvalidOperationException("ddb"));

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.SkipReason.Should().Be("user_read_failed");
        decision.HoldReason.Should().BeNull();
    }

    [Fact]
    public async Task WithoutAUsersTable_EveryCheckFailsClosed()
    {
        var decision = await new PartySuspensionGate(null).CheckAsync(Escrow());

        decision.SkipReason.Should().Be("user_read_failed");
    }

    [Fact]
    public async Task ReadsAreMemoizedPerRun_AndAFailedReadIsNotRemembered()
    {
        Users();
        _users.GetUserByIdAsync("t").ThrowsAsync(new InvalidOperationException("ddb"));
        var gate = new PartySuspensionGate(_users);
        gate.BeginRun();

        await gate.CheckAsync(Escrow());
        await gate.CheckAsync(Escrow());

        await _users.Received(1).GetUserByIdAsync("b");
        await _users.Received(2).GetUserByIdAsync("t");

        gate.BeginRun();
        await gate.CheckAsync(Escrow());
        await _users.Received(2).GetUserByIdAsync("b");
    }

    // ── FRD-04: the parties' risk tiers (sections 14.4, 17.2, 18.1) ──

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();

    private static RiskScoreResult Scored(RiskTier tier, int score = 0, bool truncated = false)
        => new() { Score = score, EnforcementScore = score, Tier = tier, Truncated = truncated };

    private void Tiers(RiskTier buyer = RiskTier.Low, RiskTier farmer = RiskTier.Low, RiskTier trucker = RiskTier.Low)
    {
        _fraud.GetUserRiskScoreAsync("b").Returns(Scored(buyer));
        _fraud.GetUserRiskScoreAsync("f").Returns(Scored(farmer));
        _fraud.GetUserRiskScoreAsync("t").Returns(Scored(trucker));
    }

    [Theory]
    [InlineData(RiskTier.Critical, RiskTier.Low, RiskTier.Low, "buyer")]
    [InlineData(RiskTier.Low, RiskTier.Critical, RiskTier.Low, "farmer")]
    [InlineData(RiskTier.Low, RiskTier.Low, RiskTier.Critical, "trucker")]
    public async Task ACriticalParty_HoldsForRiskCritical_WithItsScore(
        RiskTier buyer, RiskTier farmer, RiskTier trucker, string party)
    {
        Users();
        Tiers(buyer, farmer, trucker);

        var decision = await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow());

        decision.HoldReason.Should().Be("risk_critical");
        decision.SkipReason.Should().BeNull();
        decision.Party.Should().Be(party);
        decision.Risk!.Tier.Should().Be(RiskTier.Critical);
    }

    [Fact]
    public async Task ATruncatedRead_IsCritical()
    {
        Users();
        Tiers();
        _fraud.GetUserRiskScoreAsync("f").Returns(Scored(RiskTier.Critical, 15, truncated: true));

        var decision = await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow());

        decision.HoldReason.Should().Be("risk_critical");
        decision.Risk!.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    public async Task MediumAndHigh_Settle(RiskTier tier)
    {
        Users();
        Tiers(tier, tier, tier);

        (await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow())).Settles.Should().BeTrue();
    }

    [Fact]
    public async Task ASuspendedParty_TakesPrecedenceOverACriticalOne_AndNoScoreIsRead()
    {
        Users(farmer: true);
        Tiers(buyer: RiskTier.Critical);

        var decision = await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow());

        decision.HoldReason.Should().Be("account_suspended");
        decision.Party.Should().Be("farmer");
        decision.Risk.Should().BeNull();
        await _fraud.DidNotReceiveWithAnyArgs().GetUserRiskScoreAsync(default!);
    }

    [Fact]
    public async Task AnUnavailableScore_SkipsForTheRun_EvenThoughItsTierReadsLow()
    {
        Users();
        Tiers();
        _fraud.GetUserRiskScoreAsync("f").Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));

        var decision = await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow());

        decision.SkipReason.Should().Be("score_unavailable");
        decision.HoldReason.Should().BeNull();
        decision.Party.Should().Be("farmer");
    }

    [Fact]
    public async Task ACriticalParty_HoldsEvenWhenAnotherCouldNotBeScored()
    {
        Users();
        Tiers(trucker: RiskTier.Critical);
        _fraud.GetUserRiskScoreAsync("b").Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));

        var decision = await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow());

        decision.HoldReason.Should().Be("risk_critical");
        decision.Party.Should().Be("trucker");
    }

    [Fact]
    public async Task AnUnconfiguredSignalsTable_IsLow()
    {
        Users();
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(RiskScoreResult.Unscored(RiskScoreSource.TableNotConfigured));

        (await new PartySuspensionGate(_users, fraudService: _fraud).CheckAsync(Escrow())).Settles.Should().BeTrue();
    }

    [Fact]
    public async Task ReadsRiskTier_OnlyWithAFraudService()
    {
        new PartySuspensionGate(_users).ReadsRiskTier.Should().BeFalse();
        new PartySuspensionGate(_users, fraudService: _fraud).ReadsRiskTier.Should().BeTrue();

        // Without one the gate is FRD-03's: a party at CRITICAL is not its concern.
        Users();
        (await new PartySuspensionGate(_users).CheckAsync(Escrow())).Settles.Should().BeTrue();
    }

    [Fact]
    public async Task ScoresAreReadOncePerPartyPerRun_AFailedOneToo()
    {
        Users();
        Tiers();
        _fraud.GetUserRiskScoreAsync("t").Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));
        var gate = new PartySuspensionGate(_users, fraudService: _fraud);
        gate.BeginRun();

        (await gate.CheckAsync(Escrow())).SkipReason.Should().Be("score_unavailable");
        (await gate.CheckAsync(Escrow())).SkipReason.Should().Be("score_unavailable");

        await _fraud.Received(1).GetUserRiskScoreAsync("b");
        await _fraud.Received(1).GetUserRiskScoreAsync("t");

        // A new run reads again: a score never outlives the sweep run that read it.
        gate.BeginRun();
        await gate.CheckAsync(Escrow());
        await _fraud.Received(2).GetUserRiskScoreAsync("b");
        await _fraud.Received(2).GetUserRiskScoreAsync("t");
    }

    [Fact]
    public async Task ALegacyTruckerFoundByTheLookup_IsScoredToo()
    {
        Users();
        Tiers(trucker: RiskTier.Critical);
        var gate = new PartySuspensionGate(_users, _ => Task.FromResult<string?>("t"), _fraud);

        var decision = await gate.CheckAsync(LegacyEscrow(500m));

        decision.HoldReason.Should().Be("risk_critical");
        decision.Party.Should().Be("trucker");
    }
}

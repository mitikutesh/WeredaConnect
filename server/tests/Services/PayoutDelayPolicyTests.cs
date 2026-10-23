using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-13 (fraud-detection-architecture.md 17.5): the payout delay's settings, the payee classes and the
/// escrow's <c>releaseNotBefore</c>. Trusted payees are paid when the dispute window ends, as before; new,
/// flagged and unscored payees wait longer; a read that fails never gives the short delay and never blocks.
/// </summary>
public class PayoutDelayPolicyTests
{
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";

    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Confirmed = Now;
    private static readonly DateTime WindowEnd = Confirmed.AddHours(48);

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();

    public PayoutDelayPolicyTests()
    {
        // Every payee trusted unless a test says otherwise: score 0, a year-old account, 3 completed escrows.
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low, 0));
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => User(ci.ArgAt<string>(0), Now.AddDays(-365)));
        Completed(FarmerId, "farmer", 3);
        Completed(TruckerId, "trucker", 3);
    }

    private static RiskScoreResult Scored(RiskTier tier, int score)
        => new() { Score = score, EnforcementScore = score, Tier = tier };

    private static UserDto User(string id, DateTime createdAt) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000001",
        Role = "farmer",
        Woreda = "w",
        IsPhoneVerified = true,
        CreatedAt = createdAt.ToString("o"),
        UpdatedAt = createdAt.ToString("o"),
        Version = 1
    };

    private void Completed(string userId, string party, int count)
    {
        var rows = Enumerable.Range(0, count)
            .Select(i => new Dictionary<string, object> { { "escrowId", $"done-{i}" }, { "status", "completed" } })
            .ToList();
        var page = (rows, (Dictionary<string, AttributeValue>?)null);
        if (party == "trucker")
        {
            _escrows.QueryByTruckerAsync(userId, "completed", 50, null).Returns(page);
        }
        else
        {
            _escrows.QueryByFarmerAsync(userId, "completed", 50, null).Returns(page);
        }
    }

    private static Dictionary<string, object> Escrow(bool truckerLeg = true, bool truckerOnRow = true)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "esc-1" }, { "buyerId", "buyer-1" }, { "farmerId", FarmerId }, { "farmerPayout", 7_800m }
        };
        if (truckerLeg)
        {
            escrow["truckerPayout"] = 500m;
        }

        if (truckerOnRow)
        {
            escrow["truckerId"] = TruckerId;
        }

        return escrow;
    }

    private PayoutDelayPolicy Policy(PayoutDelaySettings? settings = null)
        => new(_fraud, _users, _escrows, settings ?? PayoutDelaySettings.Default);

    private Task<PayoutDelayDecision> DecideAsync(Dictionary<string, object>? escrow = null, PayoutDelaySettings? settings = null)
        => Policy(settings).DecideAsync(escrow ?? Escrow(), null, Confirmed, WindowEnd, Now);

    // ── Settings ──────────────────────────────────────────────────────

    [Fact]
    public void Settings_Defaults()
    {
        var settings = PayoutDelaySettings.From(_ => null);

        settings.Should().Be(new PayoutDelaySettings(24, 72, 72, 30, 3));
        settings.HoursFor(PayoutDelayClass.Trusted).Should().Be(24);
        settings.HoursFor(PayoutDelayClass.New).Should().Be(72);
        settings.HoursFor(PayoutDelayClass.Flagged).Should().Be(72);
        settings.HoursFor(PayoutDelayClass.Unscored).Should().Be(72, "an unscored payee gets the longest delay");
    }

    [Fact]
    public void Settings_ReadsEveryVariable()
    {
        var env = new Dictionary<string, string>
        {
            { "PAYOUT_DELAY_TRUSTED_HOURS", "0" },
            { "PAYOUT_DELAY_NEW_HOURS", "60" },
            { "PAYOUT_DELAY_FLAGGED_HOURS", "96" },
            { "PAYOUT_TRUSTED_MIN_ACCOUNT_DAYS", "90" },
            { "PAYOUT_TRUSTED_MIN_COMPLETED", "0" }
        };

        var settings = PayoutDelaySettings.From(name => env.GetValueOrDefault(name));

        settings.Should().Be(new PayoutDelaySettings(0, 60, 96, 90, 0));
        settings.HoursFor(PayoutDelayClass.Unscored).Should().Be(96);
    }

    [Theory]
    [InlineData("PAYOUT_DELAY_TRUSTED_HOURS", "-1")]
    [InlineData("PAYOUT_DELAY_NEW_HOURS", "169")]
    [InlineData("PAYOUT_DELAY_FLAGGED_HOURS", "1000000")]
    [InlineData("PAYOUT_DELAY_FLAGGED_HOURS", "72h")]
    [InlineData("PAYOUT_TRUSTED_MIN_ACCOUNT_DAYS", "366")]
    [InlineData("PAYOUT_TRUSTED_MIN_COMPLETED", "51")]
    [InlineData("PAYOUT_TRUSTED_MIN_COMPLETED", "")]
    public void Settings_OutOfRangeKeepsTheDefault(string name, string value)
    {
        var settings = PayoutDelaySettings.From(n => n == name ? value : null);

        settings.Should().Be(PayoutDelaySettings.Default);
    }

    [Fact]
    public void Settings_MaxDelayIsAllowed()
    {
        var settings = PayoutDelaySettings.From(n => n == "PAYOUT_DELAY_FLAGGED_HOURS" ? "168" : null);

        settings.FlaggedHours.Should().Be(PayoutDelaySettings.MaxDelayHours);
    }

    [Fact]
    public void Settings_DelaysOutOfOrderAllFallBack()
    {
        // A trusted payee must never wait longer than a new one.
        var settings = PayoutDelaySettings.From(n => n switch
        {
            "PAYOUT_DELAY_TRUSTED_HOURS" => "96",
            "PAYOUT_DELAY_NEW_HOURS" => "48",
            _ => null
        });

        (settings.TrustedHours, settings.NewHours, settings.FlaggedHours).Should().Be((24, 72, 72));
    }

    // ── Classes and the date ──────────────────────────────────────────

    [Fact]
    public async Task TrustedPayees_PaidWhenTheWindowEnds()
    {
        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.Trusted);
        decision.Trucker.Should().Be(PayoutDelayClass.Trusted);
        decision.DelayHours.Should().Be(24);
        decision.ReleaseNotBefore.Should().Be(WindowEnd, "24 hours is inside the 48 hour window, which is never shortened");
    }

    [Fact]
    public async Task NewFarmer_YoungAccount_Waits72Hours()
    {
        _users.GetUserByIdAsync(FarmerId).Returns(User(FarmerId, Now.AddDays(-29)));

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.New);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Fact]
    public async Task NewFarmer_TooFewCompletedEscrows()
    {
        Completed(FarmerId, "farmer", 2);

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.New);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Fact]
    public async Task NewFarmer_HistoryReadFails_IsNewNotTrusted()
    {
        _escrows.QueryByFarmerAsync(FarmerId, "completed", 50, null).ThrowsAsync(new InvalidOperationException("throttled"));

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.New);
    }

    [Fact]
    public async Task HistoryReadOff_OldCleanAccountIsTrusted()
    {
        _escrows.QueryByFarmerAsync(FarmerId, "completed", 50, null).ThrowsAsync(new InvalidOperationException("never read"));

        var decision = await DecideAsync(settings: PayoutDelaySettings.Default with { TrustedMinCompleted = 0 });

        decision.Farmer.Should().Be(PayoutDelayClass.Trusted);
        await _escrows.DidNotReceive().QueryByFarmerAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Theory]
    [InlineData(RiskTier.Low, 10)]
    [InlineData(RiskTier.Medium, 30)]
    public async Task ACountedSignal_IsNotTrusted(RiskTier tier, int score)
    {
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(tier, score));

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.New);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Theory]
    [InlineData(RiskTier.High, 60)]
    [InlineData(RiskTier.Critical, 100)]
    public async Task FlaggedFarmer_WaitsTheFlaggedDelay(RiskTier tier, int score)
    {
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(tier, score));

        var decision = await DecideAsync(settings: PayoutDelaySettings.Default with { FlaggedHours = 96 });

        decision.Farmer.Should().Be(PayoutDelayClass.Flagged);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(96));
        await _users.DidNotReceive().GetUserByIdAsync(FarmerId);
    }

    [Theory]
    [InlineData(RiskScoreSource.Unavailable)]
    [InlineData(RiskScoreSource.TableNotConfigured)]
    public async Task UnscoredFarmer_GetsTheLongestDelay(RiskScoreSource source)
    {
        // Unavailable carries Tier Low and score 0: it must not read as trusted.
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(RiskScoreResult.Unscored(source));

        var decision = await DecideAsync(settings: PayoutDelaySettings.Default with { FlaggedHours = 120 });

        decision.Farmer.Should().Be(PayoutDelayClass.Unscored);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(120));
    }

    [Fact]
    public async Task UserRowMissingOrUnreadable_IsUnscored()
    {
        _users.GetUserByIdAsync(FarmerId).Returns((UserDto?)null);
        _users.GetUserByIdAsync(TruckerId).ThrowsAsync(new InvalidOperationException("throttled"));

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.Unscored);
        decision.Trucker.Should().Be(PayoutDelayClass.Unscored);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Fact]
    public async Task ScoreReadThrows_IsUnscored()
    {
        _fraud.GetUserRiskScoreAsync(FarmerId).ThrowsAsync(new InvalidOperationException("boom"));

        var decision = await DecideAsync();

        decision.Farmer.Should().Be(PayoutDelayClass.Unscored);
    }

    [Fact]
    public async Task NoFraudServiceOrNoUsers_EveryPayeeUnscored()
    {
        var noFraud = await new PayoutDelayPolicy(null, _users, _escrows, PayoutDelaySettings.Default)
            .DecideAsync(Escrow(), null, Confirmed, WindowEnd, Now);
        var noUsers = await new PayoutDelayPolicy(_fraud, null, _escrows, PayoutDelaySettings.Default)
            .DecideAsync(Escrow(), null, Confirmed, WindowEnd, Now);

        noFraud.Farmer.Should().Be(PayoutDelayClass.Unscored);
        noUsers.Farmer.Should().Be(PayoutDelayClass.Unscored);
        noFraud.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Fact]
    public async Task TheLaterPayeeDecides_FlaggedTruckerDelaysATrustedFarmer()
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.High, 65));

        var decision = await DecideAsync(settings: PayoutDelaySettings.Default with { FlaggedHours = 96 });

        decision.Farmer.Should().Be(PayoutDelayClass.Trusted);
        decision.Trucker.Should().Be(PayoutDelayClass.Flagged);
        decision.DelayHours.Should().Be(96);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(96));
    }

    [Fact]
    public async Task NoTruckerLeg_OnlyTheFarmerIsRead()
    {
        _fraud.GetUserRiskScoreAsync(TruckerId).Returns(Scored(RiskTier.High, 65));

        var decision = await DecideAsync(Escrow(truckerLeg: false));

        decision.Trucker.Should().BeNull();
        decision.ReleaseNotBefore.Should().Be(WindowEnd);
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(TruckerId);
    }

    [Fact]
    public async Task TruckerNotOnTheRow_TheDeliveryNamesIt()
    {
        var decision = await Policy().DecideAsync(Escrow(truckerOnRow: false), TruckerId, Confirmed, WindowEnd, Now);

        decision.Trucker.Should().Be(PayoutDelayClass.Trusted);
        await _fraud.Received(1).GetUserRiskScoreAsync(TruckerId);
    }

    [Fact]
    public async Task TruckerLegWithoutAnyTrucker_IsUnscored()
    {
        var decision = await DecideAsync(Escrow(truckerOnRow: false));

        decision.Trucker.Should().Be(PayoutDelayClass.Unscored);
        decision.ReleaseNotBefore.Should().Be(Confirmed.AddHours(72));
    }

    [Fact]
    public async Task NeverShortensTheDisputeWindow()
    {
        // A zero delay for everyone: the window still decides.
        var settings = new PayoutDelaySettings(0, 0, 0, 30, 3);
        _fraud.GetUserRiskScoreAsync(FarmerId).Returns(Scored(RiskTier.High, 70));

        var decision = await DecideAsync(settings: settings);

        decision.ReleaseNotBefore.Should().Be(WindowEnd);
    }

    [Fact]
    public async Task OneReadPerPayee_ThroughTheBatchMemo()
    {
        var memo = new RiskScoreMemo(_fraud);
        var policy = Policy();

        await policy.DecideAsync(Escrow(), null, Confirmed, WindowEnd, Now, memo);
        await policy.DecideAsync(Escrow(), null, Confirmed, WindowEnd, Now, memo);

        await _fraud.Received(1).GetUserRiskScoreAsync(FarmerId);
        await _fraud.Received(1).GetUserRiskScoreAsync(TruckerId);
    }

    [Fact]
    public void LogLine_NamesTheClassesNeverTheScore()
    {
        var decision = new PayoutDelayDecision(Confirmed.AddHours(72), 72, PayoutDelayClass.New, null);

        decision.LogLine("esc-1").Should().Be(
            $"PAYOUT_DELAY_SET escrowId=esc-1 releaseNotBefore={Confirmed.AddHours(72):o} hours=72 farmer=new trucker=-");
    }

    // ── Reading the stored value ──────────────────────────────────────

    [Fact]
    public void ReleaseNotBeforeOf_MissingIsNull()
    {
        PayoutDelayPolicy.ReleaseNotBeforeOf(new Dictionary<string, object>()).Should().BeNull();
        PayoutDelayPolicy.ReleaseNotBeforeOf(new Dictionary<string, object> { { "releaseNotBefore", "" } }).Should().BeNull();
    }

    [Fact]
    public void ReleaseNotBeforeOf_ParsesUtc()
    {
        var at = PayoutDelayPolicy.ReleaseNotBeforeOf(new Dictionary<string, object>
        {
            { "releaseNotBefore", "2026-10-26T12:00:00.0000000Z" }
        });

        at.Should().Be(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
        at!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ReleaseNotBeforeOf_UnparsableWaitsTheCapAfterConfirm()
    {
        var at = PayoutDelayPolicy.ReleaseNotBeforeOf(new Dictionary<string, object>
        {
            { "releaseNotBefore", "soon" },
            { "deliveryConfirmedAt", Confirmed.ToString("o") }
        });

        at.Should().Be(Confirmed.AddHours(PayoutDelaySettings.MaxDelayHours));
    }

    [Fact]
    public void EffectiveReleaseNotBefore_StoredOrTheWindowOfAnOlderEscrow()
    {
        PayoutDelayPolicy.EffectiveReleaseNotBefore(new Dictionary<string, object>
        {
            { "releaseNotBefore", "2026-10-26T12:00:00Z" },
            { "deliveryConfirmedAt", "2026-10-23T12:00:00Z" },
            { "disputeWindowEndsAt", "2026-10-25T12:00:00Z" }
        }).Should().Be("2026-10-26T12:00:00Z");

        PayoutDelayPolicy.EffectiveReleaseNotBefore(new Dictionary<string, object>
        {
            { "deliveryConfirmedAt", "2026-10-23T12:00:00Z" },
            { "disputeWindowEndsAt", "2026-10-25T12:00:00Z" }
        }).Should().Be("2026-10-25T12:00:00Z", "an escrow confirmed before FRD-13 is paid when its window ends");

        // A payee reads the date only while the escrow is escrowed.
        var confirmed = new Dictionary<string, object>
        {
            { "status", "escrowed" },
            { "releaseNotBefore", "2026-10-26T12:00:00Z" },
            { "deliveryConfirmedAt", "2026-10-23T12:00:00Z" }
        };
        PayoutDelayPolicy.PayeeReleaseNotBefore(confirmed).Should().Be("2026-10-26T12:00:00Z");
        confirmed["status"] = "held";
        PayoutDelayPolicy.PayeeReleaseNotBefore(confirmed).Should().BeNull();
        PayoutDelayPolicy.EffectiveReleaseNotBefore(confirmed).Should().Be("2026-10-26T12:00:00Z", "admins still read it");

        // A dispute sets a window without a confirmed delivery: no payout date then.
        PayoutDelayPolicy.EffectiveReleaseNotBefore(new Dictionary<string, object>
        {
            { "disputeWindowEndsAt", "2026-10-25T12:00:00Z" }
        }).Should().BeNull();
    }
}

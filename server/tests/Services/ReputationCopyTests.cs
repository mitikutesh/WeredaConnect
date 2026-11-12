using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-29 (reputation-architecture.md 6.3): when a copied field of a user changes, their active
/// crop or trip rows get the new owner fields: after an event (best effort, a failure is logged
/// and left to the heal marker), after a rating, and in the daily job before the marker is
/// deleted (a failure there keeps the marker for the next run).
/// </summary>
public class ReputationCopyTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0.AddMinutes(5));
    private readonly IReputationCopyWriter _copies = Substitute.For<IReputationCopyWriter>();
    private readonly TestLambdaLogger _logger = new();
    private readonly List<(string User, string Role, OwnerReputationFields Fields)> _copied = new();

    public ReputationCopyTests()
    {
        _repo.AddUser("farmer-1");
        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .Returns(call =>
            {
                _copied.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<OwnerReputationFields>(2)));
                return 2;
            });
    }

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask, _copies);

    private Task<ReputationRecordResult> Cancel(string slot, DateTime at) => Service().RecordAsync(new ReputationCandidate
    {
        UserId = "farmer-1",
        EventId = slot,
        Code = ReputationCodes.CancelledAfterAccept,
        Party = "farmer",
        OrderId = slot,
        BuyerId = "buyer-1",
        OccurredAt = at
    }, timeoutPenaltiesOn: true, _logger);

    [Fact]
    public async Task APenaltyThatMovesTheTier_CopiesTheNewFields_OntoTheOwnersRows()
    {
        _repo.Users["farmer-1"].RatingSum = 9;
        _repo.Users["farmer-1"].RatingCount = 2;

        await Cancel("CANCEL#o1", T0);

        var (user, role, fields) = _copied.Should().ContainSingle().Subject;
        user.Should().Be("farmer-1");
        role.Should().Be("farmer");
        fields.Tier.Should().Be(ReputationTiers.Warning, "the row stores the internal tier");
        fields.EffectiveRating.Should().Be(4.2m);
        fields.RatingCount.Should().Be(2);
        fields.At.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task ARestriction_IsCopied_SoBrowseCanHideTheOwner()
    {
        foreach (var n in Enumerable.Range(1, 4))
        {
            await Cancel($"CANCEL#o{n}", T0.AddHours(n));
        }

        _copied[^1].Fields.Tier.Should().Be(ReputationTiers.Restricted);
    }

    [Fact]
    public async Task AnEventThatChangesNoCopiedField_CopiesNothing()
    {
        await Service().RecordAsync(new ReputationCandidate
        {
            UserId = "farmer-1",
            EventId = "COMPLETED#o1",
            Code = ReputationCodes.OrderCompleted,
            Party = "farmer",
            OrderId = "o1",
            BuyerId = "buyer-1",
            OccurredAt = T0
        }, timeoutPenaltiesOn: true, _logger);

        _copied.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCopy_NeverFailsTheEvent_IsLogged_AndTheHealMarkerStays()
    {
        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .ThrowsAsync(new InvalidOperationException("crops down"));

        (await Cancel("CANCEL#o1", T0)).Should().Be(ReputationRecordResult.Recorded);

        _logger.Buffer.ToString().Should().Contain("REPUTATION_COPY_FAILED step=record");
        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.Heal);
    }

    [Fact]
    public async Task ARatingRefresh_AlwaysCopies_TheRatingCountChanged()
    {
        _repo.Users["farmer-1"].RatingSum = 10;
        _repo.Users["farmer-1"].RatingCount = 2;
        await Service().RefreshAsync("farmer-1", _logger);
        _copied.Clear();

        // A third 5 star rating: the effective rating stays 5.0, the count moves.
        _repo.Users["farmer-1"].RatingSum = 15;
        _repo.Users["farmer-1"].RatingCount = 3;
        (await Service().RefreshAsync("farmer-1", _logger)).Should().BeFalse("no stored field changed");

        _copied.Should().ContainSingle().Which.Fields.RatingCount.Should().Be(3);
    }

    [Fact]
    public async Task AnUnstampedRatingSum_IsNotUsed()
    {
        // The backfill skipped this user: the stored sum holds only the ratings since the deploy.
        _repo.Users["farmer-1"].RatingSum = 5;
        _repo.Users["farmer-1"].RatingCount = 1;
        _repo.Users["farmer-1"].RatingsAggregatedAt = null;

        await Cancel("CANCEL#o1", T0);

        _repo.Users["farmer-1"].Map!.Aggregate.EffectiveRating.Should().BeNull();
        _copied.Single().Fields.RatingCount.Should().Be(0);
    }

    [Fact]
    public async Task TheDailyJob_CopiesBeforeItDeletesTheHealMarker_AndCountsTheRows()
    {
        PutHeal();

        var result = await Service().SettleDueAsync("farmer-1", T0.AddDays(1), force: false, _logger);

        result.Healed.Should().Be(1);
        result.Copied.Should().Be(2);
        _copied.Should().ContainSingle().Which.Fields.Tier.Should().Be(ReputationTiers.Good);
        _repo.RowsOf("farmer-1").Should().NotContain(r => r.Code == ReputationCodes.Heal);
    }

    [Fact]
    public async Task TheDailyJob_AFailedCopy_KeepsTheHealMarker_ForTheNextRun()
    {
        PutHeal();
        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .ThrowsAsync(new InvalidOperationException("crops down"));

        var act = () => Service().SettleDueAsync("farmer-1", T0.AddDays(1), force: false, _logger);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.Heal);
    }

    [Fact]
    public async Task TheDailyJob_NothingDue_CopiesNothing()
    {
        await Service().SettleDueAsync("farmer-1", T0, force: false, _logger);
        _repo.Committed.Clear();
        _copied.Clear();

        await Service().SettleDueAsync("farmer-1", T0.AddHours(1), force: false, _logger);

        _copied.Should().BeEmpty();
    }

    // ===== FUP-160: the one-off owner stamp =====

    [Fact]
    public async Task TheOwnerStamp_CopiesTheReputationAsRead_AndWritesNoMap()
    {
        _repo.Users["farmer-1"].RatingSum = 9;
        _repo.Users["farmer-1"].RatingCount = 2;
        await Cancel("CANCEL#o1", T0);
        _copied.Clear();
        _repo.Committed.Clear();
        _clock.Now = T0.AddDays(1);

        (await Service().StampOwnerCopiesAsync("farmer-1")).Should().Be(2);

        var (user, role, fields) = _copied.Should().ContainSingle().Subject;
        (user, role).Should().Be(("farmer-1", "farmer"));
        fields.Tier.Should().Be(ReputationTiers.Warning);
        fields.EffectiveRating.Should().Be(4.2m);
        fields.RatingCount.Should().Be(2);
        fields.At.Should().Be(T0.AddDays(1));
        _repo.Committed.Should().BeEmpty("the stamp only copies; the users row is not written");
    }

    [Fact]
    public async Task TheOwnerStamp_PastNextChangeAt_CopiesTheRecomputedReputation()
    {
        await Cancel("CANCEL#o1", T0);
        _copied.Clear();
        _repo.Committed.Clear();
        // The points expired and the daily job has not run: the stored map still says warning.
        _clock.Now = T0.AddDays(91);

        await Service().StampOwnerCopiesAsync("farmer-1");

        _copied.Single().Fields.Tier.Should().Be(ReputationTiers.Good, "what a read would show, not the stale map");
        _repo.Committed.Should().BeEmpty();
    }

    [Fact]
    public async Task TheOwnerStamp_ATrucker_CopiesOntoTheirTrips_WithNoMapYet()
    {
        _repo.AddUser("trucker-1", "trucker");

        (await Service().StampOwnerCopiesAsync("trucker-1")).Should().Be(2);

        var (_, role, fields) = _copied.Single();
        role.Should().Be("trucker");
        fields.Tier.Should().Be(ReputationTiers.Good);
        fields.RatingCount.Should().Be(0);
        fields.EffectiveRating.Should().BeNull();
    }

    [Fact]
    public async Task TheOwnerStamp_SkipsAMissingOrDeletingUser_AndNeedsACopyWriter()
    {
        _repo.AddUser("farmer-2").DeletionState = "pending";

        (await Service().StampOwnerCopiesAsync("nobody")).Should().BeNull();
        (await Service().StampOwnerCopiesAsync("farmer-2")).Should().BeNull();
        var withoutCopies = new ReputationService(_repo, ReputationPolicySettings.Defaults, null, _clock, _ => Task.CompletedTask);
        withoutCopies.CopiesConfigured.Should().BeFalse();
        (await withoutCopies.StampOwnerCopiesAsync("farmer-1")).Should().BeNull();

        _copied.Should().BeEmpty();
    }

    private void PutHeal() => _repo.Put(new ReputationEvent
    {
        UserId = "farmer-1",
        EventId = "HEAL#2026-11-06",
        Code = ReputationCodes.Heal,
        DueDay = "2026-11-06",
        DueAt = "2026-11-06T10:00:00.000Z"
    });

    // ===== the request the copy sends =====

    [Fact]
    public void TheCopy_SetsTheFieldsWithAValue_RemovesTheOthers_AndTouchesNoVersion()
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["cropId"] = new() { S = "crop-1" },
            ["createdAt"] = new() { S = "2026-11-01T00:00:00.000Z" }
        };
        var request = ReputationCopyRepository.BuildUpdate("crops", "cropId", row, new[] { "available", "reserved" },
            new OwnerReputationFields { Tier = "low", EffectiveRating = 4.2m, RatingCount = 12, ReliabilityPct = null, At = T0 });

        request.Key.Keys.Should().BeEquivalentTo("cropId", "createdAt");
        request.UpdateExpression.Should().Be("SET #tier = :tier, #count = :count, #at = :at, #rating = :rating REMOVE #reliability");
        request.ConditionExpression.Should().Be("attribute_exists(cropId) AND #status IN (:s0, :s1)");
        request.ExpressionAttributeValues[":rating"].N.Should().Be("4.2");
        request.ExpressionAttributeValues[":count"].N.Should().Be("12");
        request.ExpressionAttributeValues[":at"].S.Should().Be("2026-11-07T09:00:00.000Z");
        request.ExpressionAttributeNames.Values.Should().NotContain(new[] { "version", "updatedAt" });
        // DynamoDB refuses a name or value that the expressions do not use.
        var text = request.UpdateExpression + " " + request.ConditionExpression;
        request.ExpressionAttributeNames.Keys.Should().OnlyContain(name => text.Contains(name));
        request.ExpressionAttributeValues.Keys.Should().OnlyContain(value => text.Contains(value));
    }

    [Fact]
    public void ARowThatAlreadyHoldsTheValues_IsNotWrittenAgain()
    {
        var fields = new OwnerReputationFields { Tier = "good", EffectiveRating = 4.5m, RatingCount = 2, ReliabilityPct = null, At = T0 };
        var row = new Dictionary<string, AttributeValue>
        {
            ["ownerTier"] = new() { S = "good" },
            ["ownerEffectiveRating"] = new() { N = "4.5" },
            ["ownerRatingCount"] = new() { N = "2" }
        };

        ReputationCopyRepository.HoldsSame(row, fields).Should().BeTrue();
        ReputationCopyRepository.HoldsSame(row, fields with { RatingCount = 3 }).Should().BeFalse();
        ReputationCopyRepository.HoldsSame(row, fields with { ReliabilityPct = 90 }).Should().BeFalse();
        ReputationCopyRepository.HoldsSame(new Dictionary<string, AttributeValue>(), fields).Should().BeFalse();
    }
}

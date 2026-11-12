using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-32 against real DynamoDB: the admin transactions and the appeals queue Query, which the
/// mocked tests cannot validate. Each admin action is one <c>TransactWriteItems</c> (the users map
/// under its version, the row updates with SET and REMOVE under their conditions, the new system
/// rows), read back from the table; the queue is one Query of <c>AppealStatusIndex</c> with its
/// start key.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminReputationDynamoDbTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly LocalDynamoDbFixture _db;
    private readonly ManualClock _clock = new(T0);
    private readonly TestLambdaLogger _logger = new();

    public AdminReputationDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private ReputationEventRepository Repo() => new(_db.Client, _db.ReputationEvents, _db.Users);

    private ReputationService Service()
        => new(Repo(), ReputationPolicySettings.Defaults, clock: _clock, backoff: _ => Task.CompletedTask,
            copies: new ReputationCopyRepository(_db.Client, _db.Crops, _db.Trips));

    private async Task<string> NewUser(string role)
    {
        var userId = $"{role}-{Guid.NewGuid():N}";
        await _db.Client.PutItemAsync(_db.Users, new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["role"] = new() { S = role },
            ["version"] = new() { N = "1" }
        });
        return userId;
    }

    private async Task Penalty(string userId, string slot, DateTime at, string code = ReputationCodes.LateCancel, string buyer = "buyer-1")
    {
        var before = _clock.Now;
        _clock.Now = at.AddSeconds(4);
        (await Service().RecordAsync(new ReputationCandidate
        {
            UserId = userId,
            EventId = slot,
            Code = code,
            Party = userId.StartsWith("trucker", StringComparison.Ordinal) ? "trucker" : "farmer",
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            BuyerId = buyer,
            OccurredAt = at
        }, timeoutPenaltiesOn: true, _logger)).Should().Be(ReputationRecordResult.Recorded);
        _clock.Now = before;
    }

    private async Task<Dictionary<string, AttributeValue>> RawRow(string userId, string eventId)
        => (await _db.Client.GetItemAsync(new GetItemRequest
        {
            TableName = _db.ReputationEvents,
            Key = ReputationEventRepository.EventKey(userId, eventId),
            ConsistentRead = true
        })).Item;

    [LocalDynamoDbFact]
    public async Task Excuse_OneTransaction_ExcusesTheRow_ClosesItsAppeal_LiftsTheRestriction_AndWritesTheMap()
    {
        var userId = await NewUser("farmer");
        await Penalty(userId, "CANCEL#o1", T0.AddDays(-3));
        await Penalty(userId, "CANCEL#o2", T0.AddDays(-2), buyer: "buyer-2");
        await Penalty(userId, "CANCEL#o3", T0.AddDays(-1), buyer: "buyer-3");
        (await Repo().TryAppealAsync(userId, "CANCEL#o3", "The road was closed.", null, T0, openReview: false)).Should().BeTrue();
        (await Repo().GetUserStateAsync(userId))!.StoredTier.Should().Be("restricted");

        var result = await Service().DecideAsync(userId, "CANCEL#o3", "excuse", "Flood confirmed.", null, "admin-1", _logger);

        result.Outcome.Should().Be(ReputationAdminOutcome.Applied);
        var raw = await RawRow(userId, "CANCEL#o3");
        raw["status"].S.Should().Be("excused");
        raw["appealDecision"].S.Should().Be("excused");
        raw["decidedBy"].S.Should().Be("admin-1");
        raw["decisionNote"].S.Should().Be("Flood confirmed.");
        raw["decidedAt"].S.Should().Be(ReputationPolicy.Iso(T0));
        raw["version"].N.Should().Be("2");
        raw["appealText"].S.Should().Be("The road was closed.", "the party's text stays on the row");
        raw.Should().NotContainKeys("appealStatus", "appealedAt", "dueDay", "dueAt");

        var rows = (await Repo().QueryEventsAsync(userId, 100)).Rows;
        var restriction = rows.Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Status.Should().Be(ReputationStatuses.Lifted);
        restriction.LiftedBy.Should().Be("admin-1");
        restriction.LiftedAt.Should().Be(ReputationPolicy.Iso(T0));
        restriction.DueDay.Should().BeNull();

        var user = await Repo().GetUserStateAsync(userId);
        user!.StoredTier.Should().Be("low");
        user.StoredActivePoints.Should().Be(8);
        user.Snapshot!.HasRestriction.Should().BeFalse();

        // The same decision again finds it decided and writes nothing; the other one is refused.
        (await Service().DecideAsync(userId, "CANCEL#o3", "excuse", "Again.", null, "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.AlreadyApplied);
        (await Service().DecideAsync(userId, "CANCEL#o3", "uphold", "Other.", null, "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.EventDecided);
        (await RawRow(userId, "CANCEL#o3"))["decidedBy"].S.Should().Be("admin-1");
    }

    [LocalDynamoDbFact]
    public async Task Uphold_OfAReview_ClosesIt_AndGivesTheRestrictionItsEndOnTheDueIndex()
    {
        var userId = await NewUser("trucker");
        await Penalty(userId, "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);
        (await RawRow(userId, "OVERDUE#o1"))["appealStatus"].S.Should().Be("review");

        var result = await Service().DecideAsync(userId, "OVERDUE#o1", "uphold", "No delivery was made.", null, "admin-1", _logger);

        result.Outcome.Should().Be(ReputationAdminOutcome.Applied);
        var raw = await RawRow(userId, "OVERDUE#o1");
        raw["status"].S.Should().Be("active");
        raw["appealDecision"].S.Should().Be("upheld");
        raw.Should().NotContainKeys("appealStatus", "appealedAt");
        raw.Should().ContainKey("dueDay", "the points still expire");

        var restriction = (await Repo().QueryEventsAsync(userId, 100)).Rows.Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Until.Should().Be(T0.AddDays(14));
        restriction.DueDay.Should().Be(ReputationPolicy.Day(T0.AddDays(14)));
        (await Repo().QueryDueAsync(ReputationPolicy.Day(T0.AddDays(14)), ReputationPolicy.Iso(T0.AddDays(15))))
            .Should().Contain((userId, restriction.EventId), "the daily job will end it");
        (await Repo().GetUserStateAsync(userId))!.Snapshot!.RestrictionUntil.Should().Be(T0.AddDays(14));
    }

    [LocalDynamoDbFact]
    public async Task Uphold_FromAPageThatDidNotShowTheText_IsRefused_AndWithItGoesThrough()
    {
        var userId = await NewUser("trucker");
        await Penalty(userId, "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);
        (await Repo().TryAppealAsync(userId, "OVERDUE#o1", "The bridge was closed.", null, T0.AddMinutes(5), openReview: true)).Should().BeTrue();
        var mapVersion = (await Repo().GetUserStateAsync(userId))!.MapVersion;

        // The admin's page was loaded before the text: it sends no seenAppealTextAt.
        (await Service().DecideAsync(userId, "OVERDUE#o1", "uphold", "No delivery was made.", null, "admin-1", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.AppealChanged);

        var raw = await RawRow(userId, "OVERDUE#o1");
        raw["appealStatus"].S.Should().Be("review");
        raw.Should().NotContainKey("appealDecision");
        raw["version"].N.Should().Be("1");
        (await Repo().GetUserStateAsync(userId))!.MapVersion.Should().Be(mapVersion, "nothing was written");

        // Reloaded: the page shows the text and sends when it arrived.
        var seen = raw["appealTextAt"].S;
        seen.Should().Be(ReputationPolicy.Iso(T0.AddMinutes(5)));
        (await Service().DecideAsync(userId, "OVERDUE#o1", "uphold", "No delivery was made.", null, "admin-1", _logger, seen))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);
        (await RawRow(userId, "OVERDUE#o1"))["appealDecision"].S.Should().Be("upheld");
    }

    [LocalDynamoDbFact]
    public async Task Uphold_LosesItsCondition_WhenThePartysTextArrivedAfterTheRead()
    {
        var userId = await NewUser("farmer");
        await Penalty(userId, "CANCEL#o1", T0.AddDays(-1));
        await Repo().TryAppealAsync(userId, "CANCEL#o1", "First text.", null, T0, openReview: false);
        var user = (await Repo().GetUserStateAsync(userId))!;
        var row = (await Repo().GetEventAsync(userId, "CANCEL#o1"))!;

        // A write planned on a stale read: it believes the row has no appeal text.
        var stale = await Repo().CommitAsync(new ReputationWrite
        {
            UserId = userId,
            ExpectedMapVersion = user.MapVersion,
            Map = new ReputationMapWrite
            {
                Aggregate = new ReputationAggregate { ActivePoints = 4, Tier = "warning" },
                Version = user.MapVersion!.Value + 1,
                UpdatedAt = T0
            },
            RowUpdates = new[]
            {
                new ReputationRowUpdate
                {
                    EventId = "CANCEL#o1",
                    FromStatus = ReputationStatuses.Active,
                    SeenVersion = row.Version,
                    RequiredAppealStatus = "pending",
                    CheckAppealTextAt = true,
                    SeenAppealTextAt = null,
                    AppealDecision = "upheld",
                    DecidedAt = ReputationPolicy.Iso(T0),
                    DecidedBy = "admin-1",
                    DecisionNote = "Stale.",
                    CloseAppeal = true
                }
            }
        });

        stale.Should().Be(ReputationCommitOutcome.Conflict);
        (await RawRow(userId, "CANCEL#o1"))["appealStatus"].S.Should().Be("pending", "nothing was written");
        (await Repo().GetUserStateAsync(userId))!.MapVersion.Should().Be(user.MapVersion, "the map is in the same transaction");
    }

    [LocalDynamoDbFact]
    public async Task TagUntagRestrictAndLift_WriteTheirSystemRows_AndTheMap()
    {
        var userId = await NewUser("farmer");
        var service = Service();

        (await service.ApplyAdminActionAsync(userId, "tag", "Complaints from buyers.", T0.AddDays(30), "admin-1", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);
        var user = await Repo().GetUserStateAsync(userId);
        user!.StoredTier.Should().Be("low");
        user.Snapshot!.OverrideTag.Should().Be("low");
        user.Snapshot.OverrideUntil.Should().Be(T0.AddDays(30));
        (await service.ApplyAdminActionAsync(userId, "tag", "Complaints from buyers.", T0.AddDays(30), "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.AlreadyApplied);

        _clock.Now = T0.AddMinutes(1);
        (await service.ApplyAdminActionAsync(userId, "untag", "Cleared.", T0.AddDays(10), "admin-1", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);
        var rows = (await Repo().QueryEventsAsync(userId, 100)).Rows;
        var tag = rows.Single(r => r.Code == ReputationCodes.AdminTag);
        tag.Status.Should().Be(ReputationStatuses.Replaced);
        tag.DueDay.Should().BeNull();
        tag.By.Should().Be("admin-1");
        rows.Single(r => r.Code == ReputationCodes.AdminUntag).Until.Should().Be(T0.AddDays(10));
        (await Repo().GetUserStateAsync(userId))!.StoredTier.Should().Be("good");

        _clock.Now = T0.AddMinutes(2);
        (await service.ApplyAdminActionAsync(userId, "restrict", "Three no-shows reported.", T0.AddDays(60), "admin-1", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);
        (await service.ApplyAdminActionAsync(userId, "restrict", "Another reason.", T0.AddDays(90), "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.AlreadyRestricted);
        var restricted = await Repo().GetUserStateAsync(userId);
        restricted!.StoredTier.Should().Be("restricted");
        restricted.Snapshot!.RestrictionCause.Should().Be("admin");
        restricted.Snapshot.RestrictionUntil.Should().Be(T0.AddDays(60));

        _clock.Now = T0.AddMinutes(3);
        (await service.ApplyAdminActionAsync(userId, "lift", "Complaints withdrawn.", null, "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);
        var restriction = (await Repo().QueryEventsAsync(userId, 100)).Rows.Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Status.Should().Be(ReputationStatuses.Lifted);
        restriction.Reason.Should().Be("Three no-shows reported.");
        restriction.LiftedBy.Should().Be("admin-2");
        var lifted = await Repo().GetUserStateAsync(userId);
        lifted!.StoredTier.Should().Be("good");
        lifted.Snapshot!.HasRestriction.Should().BeFalse();
        (await service.ApplyAdminActionAsync(userId, "lift", "Again.", null, "admin-2", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.NotRestricted);
    }

    [LocalDynamoDbFact]
    public async Task ARestriction_ReachesTheOwnersListing_InTheSameCall()
    {
        var userId = await NewUser("farmer");
        var cropId = $"crop-{Guid.NewGuid():N}";
        await _db.Client.PutItemAsync(_db.Crops, new Dictionary<string, AttributeValue>
        {
            ["cropId"] = new() { S = cropId },
            ["createdAt"] = new() { S = "2026-11-01T00:00:00.000Z" },
            ["farmerId"] = new() { S = userId },
            ["status"] = new() { S = "available" },
            ["version"] = new() { N = "1" }
        });

        // The owner index is eventually consistent: wait until the copy can see the row.
        for (var i = 0; i < 20; i++)
        {
            var seen = await _db.Client.QueryAsync(new QueryRequest
            {
                TableName = _db.Crops,
                IndexName = "FarmerIdCreatedAtIndex",
                KeyConditionExpression = "farmerId = :f",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":f"] = new() { S = userId } }
            });
            if (seen.Count > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        (await Service().ApplyAdminActionAsync(userId, "restrict", "Three no-shows reported.", T0.AddDays(60), "admin-1", _logger))
            .Outcome.Should().Be(ReputationAdminOutcome.Applied);

        var crop = (await _db.Client.QueryAsync(new QueryRequest
        {
            TableName = _db.Crops,
            KeyConditionExpression = "cropId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = cropId } },
            ConsistentRead = true
        })).Items.Single();
        crop["ownerTier"].S.Should().Be("restricted");
        crop["version"].N.Should().Be("1", "a copy never bumps the listing's version");
    }

    [LocalDynamoDbFact]
    public async Task SuspensionRefresh_WritesTheRestrictedTier_AndCopiesIt()
    {
        var userId = await NewUser("trucker");
        await _db.Client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = userId } },
            UpdateExpression = "SET suspended = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } }
        });

        await Service().RefreshAfterSuspensionAsync(userId, _logger);

        var user = await Repo().GetUserStateAsync(userId);
        user!.StoredTier.Should().Be("restricted");
        user.MapVersion.Should().Be(1);
        (await Repo().QueryEventsAsync(userId, 100)).Rows.Should().ContainSingle(r => r.Code == ReputationCodes.TierChanged);
    }

    [LocalDynamoDbFact]
    public async Task AppealsQueue_ReadsOnePartitionOldestFirst_AndPagesWithItsCursor()
    {
        // Its own statuses would collide with the other tests' rows on the shared index, so the
        // rows carry a status only this test reads.
        var status = $"pending-{Guid.NewGuid():N}";
        var userId = await NewUser("farmer");
        for (var i = 1; i <= 3; i++)
        {
            await _db.Client.PutItemAsync(_db.ReputationEvents, ReputationEventRepository.ToItem(new ReputationEvent
            {
                UserId = userId,
                EventId = $"CANCEL#o{i}",
                Code = ReputationCodes.LateCancel,
                Party = "farmer",
                BasePoints = 4,
                Points = 4,
                OrderId = $"o{i}",
                OccurredAt = T0,
                RecordedAt = T0,
                PointsExpireAt = T0.AddDays(90),
                AppealStatus = status,
                AppealedAt = ReputationPolicy.Iso(T0.AddHours(i)),
                AppealText = $"Text {i}",
                AppealPhotoRef = i == 2 ? $"uploads/appeals/{userId}/p.webp" : null,
                PurgeAt = 1900000000
            }));
        }

        IReadOnlyList<ReputationEvent> first = Array.Empty<ReputationEvent>();
        ReputationAppealCursor? next = null;
        for (var i = 0; i < 20 && first.Count < 2; i++)
        {
            (first, next) = await Repo().QueryAppealsAsync(status, 2, null);
            if (first.Count < 2)
            {
                await Task.Delay(100);
            }
        }

        first.Select(r => r.EventId).Should().Equal("CANCEL#o1", "CANCEL#o2");
        first[1].AppealPhotoRef.Should().Be($"uploads/appeals/{userId}/p.webp", "the index projects every attribute");
        first[0].AppealText.Should().Be("Text 1");
        next.Should().NotBeNull();

        var (second, end) = await Repo().QueryAppealsAsync(status, 2, next);
        second.Select(r => r.EventId).Should().Equal("CANCEL#o3");
        end.Should().BeNull();
        (await Repo().QueryAppealsAsync($"none-{Guid.NewGuid():N}", 10, null)).Rows.Should().BeEmpty();
    }
}

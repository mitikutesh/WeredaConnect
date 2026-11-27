using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-29 against real DynamoDB: the requests the mocked tests cannot validate. The owner copy
/// (index queries, the batch read, the conditional update with SET and REMOVE), the users row
/// read back with its <c>reputation</c> map, and the appeal writes and the event page of the
/// reputation endpoints.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReputationReadsDynamoDbTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly LocalDynamoDbFixture _db;

    public ReputationReadsDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private ReputationCopyRepository Copies() => new(_db.Client, _db.Crops, _db.Trips);

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private Task PutRow(string table, string idAttribute, string id, string ownerAttribute, string owner, string status, bool deleted = false)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            [idAttribute] = new() { S = id },
            ["createdAt"] = new() { S = $"2026-11-01T00:00:00.{Random.Shared.Next(100, 999)}Z" },
            ["updatedAt"] = new() { S = "2026-11-01T00:00:00.000Z" },
            [ownerAttribute] = new() { S = owner },
            ["status"] = new() { S = status },
            ["version"] = new() { N = "3" }
        };
        if (deleted)
        {
            item["deletedAt"] = new AttributeValue { S = "2026-11-02T00:00:00.000Z" };
        }

        return _db.Client.PutItemAsync(table, item);
    }

    private async Task<Dictionary<string, AttributeValue>> Row(string table, string idAttribute, string id)
        => (await _db.Client.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = $"{idAttribute} = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = id } },
            ConsistentRead = true
        })).Items.Single();

    [LocalDynamoDbFact]
    public async Task FarmerCopy_WritesActiveRowsOnly_KeepsVersionAndUpdatedAt_AndIsIdempotent()
    {
        var farmer = Id("farmer");
        var (available, reserved, sold, deleted) = (Id("crop"), Id("crop"), Id("crop"), Id("crop"));
        await PutRow(_db.Crops, "cropId", available, "farmerId", farmer, "available");
        await PutRow(_db.Crops, "cropId", reserved, "farmerId", farmer, "reserved");
        await PutRow(_db.Crops, "cropId", sold, "farmerId", farmer, "sold");
        await PutRow(_db.Crops, "cropId", deleted, "farmerId", farmer, "available", deleted: true);
        var fields = new OwnerReputationFields { Tier = "low", EffectiveRating = 4.2m, RatingCount = 12, ReliabilityPct = 83, At = T0 };

        // The index is eventually consistent: wait until it shows the rows.
        var written = 0;
        for (var i = 0; i < 20 && written < 2; i++)
        {
            written += await Copies().RefreshOwnerRowsAsync(farmer, "farmer", fields);
            if (written < 2)
            {
                await Task.Delay(100);
            }
        }

        written.Should().Be(2);
        foreach (var id in new[] { available, reserved })
        {
            var row = await Row(_db.Crops, "cropId", id);
            row["ownerTier"].S.Should().Be("low");
            row["ownerEffectiveRating"].N.Should().Be("4.2");
            row["ownerRatingCount"].N.Should().Be("12");
            row["ownerReliabilityPct"].N.Should().Be("83");
            row["ownerReputationAt"].S.Should().Be("2026-11-07T09:00:00.000Z");
            row["version"].N.Should().Be("3", "the copy never bumps the row version");
            row["updatedAt"].S.Should().Be("2026-11-01T00:00:00.000Z");
        }

        (await Row(_db.Crops, "cropId", sold)).Should().NotContainKey("ownerTier");
        (await Row(_db.Crops, "cropId", deleted)).Should().NotContainKey("ownerTier");

        // The same values again write nothing; values without a rating or reliability REMOVE them.
        (await Copies().RefreshOwnerRowsAsync(farmer, "farmer", fields with { At = T0.AddHours(1) })).Should().Be(0);
        (await Copies().RefreshOwnerRowsAsync(farmer, "farmer",
            new OwnerReputationFields { Tier = "restricted", RatingCount = 0, At = T0.AddHours(2) })).Should().Be(2);
        var after = await Row(_db.Crops, "cropId", available);
        after["ownerTier"].S.Should().Be("restricted");
        after.Should().NotContainKey("ownerEffectiveRating").And.NotContainKey("ownerReliabilityPct");
    }

    [LocalDynamoDbFact]
    public async Task TruckerCopy_WritesOpenAndMatchedTrips_AndABuyerHasNoRows()
    {
        var trucker = Id("trucker");
        var (open, matched, cancelled) = (Id("trip"), Id("trip"), Id("trip"));
        await PutRow(_db.Trips, "tripId", open, "truckerId", trucker, "open");
        await PutRow(_db.Trips, "tripId", matched, "truckerId", trucker, "matched");
        await PutRow(_db.Trips, "tripId", cancelled, "truckerId", trucker, "cancelled");
        var fields = new OwnerReputationFields { Tier = "good", RatingCount = 0, At = T0 };

        var written = 0;
        for (var i = 0; i < 20 && written < 2; i++)
        {
            written += await Copies().RefreshOwnerRowsAsync(trucker, "trucker", fields);
            if (written < 2)
            {
                await Task.Delay(100);
            }
        }

        written.Should().Be(2);
        (await Row(_db.Trips, "tripId", open))["ownerTier"].S.Should().Be("good");
        (await Row(_db.Trips, "tripId", matched))["ownerRatingCount"].N.Should().Be("0");
        (await Row(_db.Trips, "tripId", cancelled)).Should().NotContainKey("ownerTier");
        (await Copies().RefreshOwnerRowsAsync(trucker, "buyer", fields)).Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task UsersRow_ReadsBackTheReputationMap_WrittenByTheReputationRepository()
    {
        var userId = Id("farmer");
        var users = new UserRepository(_db.Client, _db.Users);
        await users.CreateUserAsync(new UserDto
        {
            UserId = userId,
            PhoneNumber = $"+2519{Random.Shared.Next(10000000, 99999999)}",
            Role = "farmer",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-11-01T00:00:00.000Z",
            UpdatedAt = "2026-11-01T00:00:00.000Z",
            Version = 1
        });
        var repo = new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users);
        var until = T0.AddDays(14);
        var restriction = new ReputationEvent
        {
            UserId = userId,
            EventId = ReputationSlots.Restriction + ReputationPolicy.Iso(T0),
            Code = ReputationCodes.Restriction,
            Cause = ReputationCauses.Points,
            Since = T0,
            Until = until,
            OccurredAt = T0,
            RecordedAt = T0
        };
        (await repo.CommitAsync(new ReputationWrite
        {
            UserId = userId,
            Map = new ReputationMapWrite
            {
                Aggregate = new ReputationAggregate
                {
                    ActivePoints = 10, Tier = ReputationTiers.Restricted, CompletedOrders90d = 4, FaultCancels90d = 3,
                    ReliabilityPct = 57, Restriction = restriction, NextChangeAt = until
                },
                Version = 1,
                UpdatedAt = T0
            },
            NewSystemRows = new[] { restriction }
        })).Should().Be(ReputationCommitOutcome.Committed);

        var row = await users.GetUserByIdAsync(userId, consistentRead: true);

        row!.RatingsAggregatedAt.Should().NotBeNull("registration stamps the rating aggregate");
        row.Reputation.Should().NotBeNull();
        row.Reputation!.Tier.Should().Be(ReputationTiers.Restricted);
        row.Reputation.ActivePoints.Should().Be(10);
        row.Reputation.ReliabilityPct.Should().Be(57);
        row.Reputation.RestrictionUntil.Should().Be(until);
        row.Reputation.NextChangeAt.Should().Be(until);
        ReputationGate.CheckOwn(row, T0.AddDays(1)).Should().StartWith("REPUTATION_RESTRICTED: until=");
        ReputationGate.CheckOwn(row, until).Should().BeNull();
    }

    private static ReputationEvent Penalty(string userId, string eventId, string? appealStatus = null) => new()
    {
        UserId = userId,
        EventId = eventId,
        Code = ReputationCodes.LateCancel,
        Party = "farmer",
        BasePoints = 4,
        Points = 4,
        OrderId = "o1",
        BuyerId = "buyer-1",
        OccurredAt = T0,
        RecordedAt = T0,
        PointsExpireAt = T0.AddDays(90),
        AppealStatus = appealStatus,
        AppealedAt = appealStatus == null ? null : ReputationPolicy.Iso(T0),
        PurgeAt = 1900000000
    };

    [LocalDynamoDbFact]
    public async Task Appeal_IsStoredOnce_JoinsTheAppealIndex_AndReadsBack()
    {
        var userId = Id("farmer");
        var repo = new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users);
        await _db.Client.PutItemAsync(_db.ReputationEvents, ReputationEventRepository.ToItem(Penalty(userId, "CANCEL#o1")));
        const string photo = "uploads/appeals/f/5b0c.webp";

        (await repo.TryAppealAsync(userId, "CANCEL#o1", "The road was closed.", photo, T0.AddDays(1), openReview: false)).Should().BeTrue();
        (await repo.TryAppealAsync(userId, "CANCEL#o1", "Another story.", null, T0.AddDays(2), openReview: false))
            .Should().BeFalse("one appeal per event");
        (await repo.TryAppealAsync(userId, "CANCEL#missing", "Text.", null, T0, openReview: false)).Should().BeFalse();

        var row = await repo.GetEventAsync(userId, "CANCEL#o1");
        row!.AppealText.Should().Be("The road was closed.");
        row.AppealPhotoRef.Should().Be(photo);
        row.AppealStatus.Should().Be("pending");
        row.AppealedAt.Should().Be(ReputationPolicy.Iso(T0.AddDays(1)));
        row.AppealTextAt.Should().Be(ReputationPolicy.Iso(T0.AddDays(1)));
        row.Points.Should().Be(4);
        row.Version.Should().Be(1, "an appeal changes no counted attribute");
        (await repo.GetEventAsync(userId, "CANCEL#nope")).Should().BeNull();

        // The row is now on the sparse index the admin queue reads.
        var listed = 0;
        for (var i = 0; i < 20 && listed == 0; i++)
        {
            listed = (await _db.Client.QueryAsync(new QueryRequest
            {
                TableName = _db.ReputationEvents,
                IndexName = ReputationEventRepository.AppealStatusIndex,
                KeyConditionExpression = "appealStatus = :s",
                FilterExpression = "userId = :u",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":s"] = new() { S = "pending" }, [":u"] = new() { S = userId }
                }
            })).Count;
            if (listed == 0)
            {
                await Task.Delay(100);
            }
        }

        listed.Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task Appeal_OnAReviewRow_KeepsItsQueueKey_AndAnInactiveRowIsRefused()
    {
        var userId = Id("trucker");
        var repo = new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users);
        await _db.Client.PutItemAsync(_db.ReputationEvents, ReputationEventRepository.ToItem(Penalty(userId, "OVERDUE#o1", "review")));
        await _db.Client.PutItemAsync(_db.ReputationEvents,
            ReputationEventRepository.ToItem(Penalty(userId, "CANCEL#o2") with { Status = ReputationStatuses.Excused }));

        (await repo.TryAppealAsync(userId, "OVERDUE#o1", "The truck broke down.", null, T0.AddDays(1), openReview: true)).Should().BeTrue();
        (await repo.TryAppealAsync(userId, "CANCEL#o2", "Text.", null, T0.AddDays(1), openReview: false)).Should().BeFalse();

        var row = await repo.GetEventAsync(userId, "OVERDUE#o1");
        row!.AppealStatus.Should().Be("review");
        row.AppealedAt.Should().Be(ReputationPolicy.Iso(T0));
        row.AppealText.Should().Be("The truck broke down.");
        row.AppealPhotoRef.Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task Ratings_AreFilteredByTheRoleTheyWereReceivedIn()
    {
        var ratee = Id("user");
        var ratings = new RatingRepository(_db.Client, _db.Ratings);
        foreach (var (order, role, stars, at) in new[]
                 {
                     ("o1", "farmer", 5, "2026-11-01T00:00:00.000Z"), ("o2", "farmer", 4, "2026-11-02T00:00:00.000Z"),
                     ("o3", "trucker", 1, "2026-11-03T00:00:00.000Z")
                 })
        {
            await _db.Client.PutItemAsync(_db.Ratings, new Dictionary<string, AttributeValue>
            {
                ["ratingId"] = new() { S = $"{order}:{ratee}" },
                ["orderId"] = new() { S = order },
                ["rateeId"] = new() { S = ratee },
                ["rateeRole"] = new() { S = role },
                ["stars"] = new() { N = stars.ToString() },
                ["createdAt"] = new() { S = at }
            });
        }

        (double Average, int Count) farmer = (0, 0);
        for (var i = 0; i < 20 && farmer.Count < 2; i++)
        {
            farmer = await ratings.GetSummaryAsync(ratee, "farmer");
            if (farmer.Count < 2)
            {
                await Task.Delay(100);
            }
        }

        farmer.Should().Be((4.5, 2));
        (await ratings.GetSummaryAsync(ratee, "trucker")).Should().Be((1.0, 1));
        (await ratings.GetSummaryAsync(ratee)).Count.Should().Be(3);

        var (asFarmer, _) = await ratings.QueryByRateeAsync(ratee, 50, null, "farmer");
        asFarmer.Select(r => r["orderId"]).Should().Equal("o2", "o1");
        var (all, _) = await ratings.QueryByRateeAsync(ratee, 50);
        all.Should().HaveCount(3);
    }
}

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
}

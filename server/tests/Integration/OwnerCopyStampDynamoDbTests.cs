using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-160 against real DynamoDB: the order-cron's one-off owner stamp walks the active partitions of
/// the crop and trip status indexes, reads each owner's users row and copies their reputation onto
/// the active rows only, without touching a row's <c>version</c> or <c>updatedAt</c> or the users
/// row. An owner without a <c>woreda</c> is reached, an owner without a users row is skipped, and a
/// rerun writes nothing.
/// </summary>
[Collection(OwnerStampDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OwnerCopyStampDynamoDbTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);
    private const string UpdatedAt = "2026-11-01T00:00:00.000Z";

    private readonly LocalDynamoDbFixture _db;

    public OwnerCopyStampDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private OwnerCopyStampJob Job()
    {
        var service = new ReputationService(
            new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users),
            ReputationPolicySettings.Defaults,
            clock: new ManualClock(T0.AddHours(1)),
            backoff: _ => Task.CompletedTask,
            copies: new ReputationCopyRepository(_db.Client, _db.Crops, _db.Trips));
        return new OwnerCopyStampJob(service, _db.Client, _db.Crops, _db.Trips);
    }

    private static CronRunSlice FullSlice() => new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4));

    private async Task CreateUser(string userId, string role, string? woreda)
    {
        await new UserRepository(_db.Client, _db.Users).CreateUserAsync(new UserDto
        {
            UserId = userId,
            PhoneNumber = $"+2519{Random.Shared.Next(10000000, 99999999)}",
            Role = role,
            Woreda = woreda ?? "",
            IsPhoneVerified = true,
            CreatedAt = UpdatedAt,
            UpdatedAt = UpdatedAt,
            Version = 1
        });
        if (woreda == null)
        {
            // A row without the attribute, as RoleWaredaIndex leaves out (FUP-162).
            await _db.Client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _db.Users,
                Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = userId } },
                UpdateExpression = "REMOVE woreda"
            });
        }
    }

    private Task PutCrop(string cropId, string farmerId, string status) => _db.Client.PutItemAsync(_db.Crops, new Dictionary<string, AttributeValue>
    {
        ["cropId"] = new() { S = cropId },
        ["createdAt"] = new() { S = $"2026-11-01T00:00:00.{Random.Shared.Next(100, 999)}Z" },
        ["updatedAt"] = new() { S = UpdatedAt },
        ["farmerId"] = new() { S = farmerId },
        ["status"] = new() { S = status },
        ["version"] = new() { N = "3" }
    });

    private Task PutTrip(string tripId, string truckerId, string status) => _db.Client.PutItemAsync(_db.Trips, new Dictionary<string, AttributeValue>
    {
        ["tripId"] = new() { S = tripId },
        ["createdAt"] = new() { S = $"2026-11-01T00:00:00.{Random.Shared.Next(100, 999)}Z" },
        ["updatedAt"] = new() { S = UpdatedAt },
        ["departureDate"] = new() { S = "2026-11-20T21:00:00.000Z" },
        ["truckerId"] = new() { S = truckerId },
        ["status"] = new() { S = status },
        ["version"] = new() { N = "3" }
    });

    private async Task<Dictionary<string, AttributeValue>> Row(string table, string idAttribute, string id)
        => (await _db.Client.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = $"{idAttribute} = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = id } },
            ConsistentRead = true
        })).Items.Single();

    private async Task<Dictionary<string, AttributeValue>> UserRow(string userId)
        => (await _db.Client.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = userId } },
            ConsistentRead = true
        })).Item;

    /// <summary>The indexes are eventually consistent: wait until a partition shows its rows.</summary>
    private async Task WaitForIndex(string table, string index, string status, int count)
    {
        for (var i = 0; i < 20; i++)
        {
            var seen = (await _db.Client.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = index,
                KeyConditionExpression = "#s = :s",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = new() { S = status } }
            })).Count;
            if (seen >= count)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    [LocalDynamoDbFact]
    public async Task Stamp_WritesTheOwnersActiveRows_ReachesOwnersWithoutAWoreda_AndARerunWritesNothing()
    {
        // A farmer with a woreda and two ratings (no reputation map yet), a restricted farmer and a
        // trucker without a woreda, and a listing whose owner has no users row.
        var (farmerA, farmerB, truckerC, ghost) = (Id("farmer"), Id("farmer"), Id("trucker"), Id("farmer"));
        await CreateUser(farmerA, "farmer", "Jimma");
        await CreateUser(farmerB, "farmer", null);
        await CreateUser(truckerC, "trucker", null);
        await _db.Client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = farmerA } },
            UpdateExpression = "SET ratingSum = :sum, ratingCount = :count",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":sum"] = new() { N = "9" },
                [":count"] = new() { N = "2" }
            }
        });
        var until = T0.AddDays(14);
        var restriction = new ReputationEvent
        {
            UserId = farmerB,
            EventId = ReputationSlots.Restriction + ReputationPolicy.Iso(T0),
            Code = ReputationCodes.Restriction,
            Cause = ReputationCauses.Points,
            Since = T0,
            Until = until,
            OccurredAt = T0,
            RecordedAt = T0
        };
        (await new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users).CommitAsync(new ReputationWrite
        {
            UserId = farmerB,
            Map = new ReputationMapWrite
            {
                Aggregate = new ReputationAggregate
                {
                    ActivePoints = 10,
                    Tier = ReputationTiers.Restricted,
                    Restriction = restriction,
                    NextChangeAt = until
                },
                Version = 1,
                UpdatedAt = T0
            },
            NewSystemRows = new[] { restriction }
        })).Should().Be(ReputationCommitOutcome.Committed);

        var (a1, a2, a3, b1, g1) = (Id("crop"), Id("crop"), Id("crop"), Id("crop"), Id("crop"));
        await PutCrop(a1, farmerA, "available");
        await PutCrop(a2, farmerA, "reserved");
        await PutCrop(a3, farmerA, "sold");
        await PutCrop(b1, farmerB, "available");
        await PutCrop(g1, ghost, "available");
        var (c1, c2, c3) = (Id("trip"), Id("trip"), Id("trip"));
        await PutTrip(c1, truckerC, "open");
        await PutTrip(c2, truckerC, "matched");
        await PutTrip(c3, truckerC, "cancelled");
        await WaitForIndex(_db.Crops, OwnerCopyStampJob.CropStatusIndex, "available", 3);
        await WaitForIndex(_db.Crops, OwnerCopyStampJob.CropStatusIndex, "reserved", 1);
        await WaitForIndex(_db.Trips, OwnerCopyStampJob.TripStatusIndex, "open", 1);
        await WaitForIndex(_db.Trips, OwnerCopyStampJob.TripStatusIndex, "matched", 1);

        var first = await Job().RunAsync(null, FullSlice(), new TestLambdaContext());

        first["outcome"].Should().Be("ok");
        first["owners"].Should().Be("4");
        first["users"].Should().Be("3");
        first["skipped"].Should().Be("1", "the listing's owner has no users row");
        first["rows"].Should().Be("5");
        first["failed"].Should().Be("0");
        first.Should().NotContainKey("next");

        foreach (var id in new[] { a1, a2 })
        {
            var row = await Row(_db.Crops, "cropId", id);
            row["ownerTier"].S.Should().Be(ReputationTiers.Good);
            row["ownerEffectiveRating"].N.Should().Be("4.5");
            row["ownerRatingCount"].N.Should().Be("2");
            row.Should().NotContainKey("ownerReliabilityPct");
            row["ownerReputationAt"].S.Should().Be(ReputationPolicy.Iso(T0.AddHours(1)));
            row["version"].N.Should().Be("3", "the copy never bumps the row version");
            row["updatedAt"].S.Should().Be(UpdatedAt);
        }

        var restricted = await Row(_db.Crops, "cropId", b1);
        restricted["ownerTier"].S.Should().Be(ReputationTiers.Restricted, "the stored map is read, so browse can hide the owner");
        restricted["version"].N.Should().Be("3");
        foreach (var id in new[] { c1, c2 })
        {
            var trip = await Row(_db.Trips, "tripId", id);
            trip["ownerTier"].S.Should().Be(ReputationTiers.Good);
            trip["ownerRatingCount"].N.Should().Be("0");
            trip.Should().NotContainKey("ownerEffectiveRating");
            trip["version"].N.Should().Be("3");
            trip["updatedAt"].S.Should().Be(UpdatedAt);
        }

        (await Row(_db.Crops, "cropId", a3)).Should().NotContainKey("ownerTier", "a sold listing is not active");
        (await Row(_db.Crops, "cropId", g1)).Should().NotContainKey("ownerTier");
        (await Row(_db.Trips, "tripId", c3)).Should().NotContainKey("ownerTier");
        (await UserRow(farmerA)).Should().NotContainKey("reputation", "the stamp writes no users row");
        (await UserRow(truckerC)).Should().NotContainKey("reputation");

        // A rerun later finds every row holding the values and writes nothing.
        var second = await new OwnerCopyStampJob(
                new ReputationService(
                    new ReputationEventRepository(_db.Client, _db.ReputationEvents, _db.Users),
                    ReputationPolicySettings.Defaults,
                    clock: new ManualClock(T0.AddHours(5)),
                    copies: new ReputationCopyRepository(_db.Client, _db.Crops, _db.Trips)),
                _db.Client, _db.Crops, _db.Trips)
            .RunAsync(null, FullSlice(), new TestLambdaContext());

        second["outcome"].Should().Be("ok");
        second["users"].Should().Be("3");
        second["rows"].Should().Be("0");
        (await Row(_db.Crops, "cropId", a1))["ownerReputationAt"].S.Should().Be(ReputationPolicy.Iso(T0.AddHours(1)), "nothing was rewritten");
    }
}

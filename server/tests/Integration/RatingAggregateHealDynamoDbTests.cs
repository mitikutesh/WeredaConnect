using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using Amazon.Runtime;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-162: its own users table (with <c>RoleWaredaIndex</c>, which the shared fixture leaves out)
/// and ratings table, because the backfill action Scans the whole users table: rows the other
/// classes seed never reach it, and its stamps never reach theirs.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RatingAggregateDynamoDbCollection : ICollectionFixture<RatingAggregateDynamoDbFixture>
{
    public const string Name = "RatingAggregateDynamoDb";
}

/// <summary>The users and ratings tables with their deployed keys and indexes (<c>database-stack.ts</c>).</summary>
public sealed class RatingAggregateDynamoDbFixture : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public IAmazonDynamoDB Client { get; private set; } = null!;
    public string Users => $"users-agg-{_suffix}";
    public string Ratings => $"ratings-agg-{_suffix}";

    public async Task InitializeAsync()
    {
        var endpoint = LocalDynamoDbEnvironment.Endpoint;
        if (endpoint == null)
        {
            if (LocalDynamoDbEnvironment.Required)
            {
                throw new InvalidOperationException("REQUIRE_LOCALSTACK is set but AWS_ENDPOINT_URL is not.");
            }

            return; // every test in the collection is skipped
        }

        Client = new AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), new AmazonDynamoDBConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1"
        });
        await CreateAsync(Users, "userId", "RoleWaredaIndex", "role", "woreda");
        await CreateAsync(Ratings, "ratingId", "RateeIdCreatedAtIndex", "rateeId", "createdAt");
    }

    public async Task DisposeAsync()
    {
        foreach (var table in new[] { Users, Ratings })
        {
            try
            {
                await Client.DeleteTableAsync(table);
            }
            catch (Exception)
            {
                // Best-effort cleanup of a throwaway table (or no client: the tests were skipped).
            }
        }

        Client?.Dispose();
    }

    private async Task CreateAsync(string table, string key, string index, string hash, string range)
    {
        await Client.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = new List<KeySchemaElement> { new(key, KeyType.HASH) },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new(key, ScalarAttributeType.S), new(hash, ScalarAttributeType.S), new(range, ScalarAttributeType.S)
            },
            GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
            {
                new()
                {
                    IndexName = index,
                    KeySchema = new List<KeySchemaElement> { new(hash, KeyType.HASH), new(range, KeyType.RANGE) },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                }
            },
            BillingMode = BillingMode.PAY_PER_REQUEST
        });

        for (var i = 0; i < 50; i++)
        {
            if ((await Client.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}

/// <summary>
/// FUP-162 against real DynamoDB: the rating create's heal of an unstamped ratee, concurrent creates
/// and heals counting every rating once, and the backfill action's index walk, users Scan and cursor.
/// </summary>
[Collection(RatingAggregateDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RatingAggregateHealDynamoDbTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly RatingAggregateDynamoDbFixture _db;

    public RatingAggregateHealDynamoDbTests(RatingAggregateDynamoDbFixture db)
    {
        _db = db;
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private RatingRepository Repo() => new(_db.Client, _db.Ratings, _db.Users);

    private RatingAggregateHeal Heal() => new(_db.Client, _db.Users, _db.Ratings) { Clock = () => Now };

    private RatingAggregateBackfill Backfill() => new(_db.Client, _db.Users, _db.Ratings);

    /// <summary>A users row as written before ESC-28: no rating fields at all unless given.</summary>
    private async Task<string> PutUser(
        string role, string? woreda = null, string? aggregatedAt = null, long? sum = null, long? count = null, string? deletionState = null)
    {
        var userId = Id(role);
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["role"] = new() { S = role },
            ["createdAt"] = new() { S = "2026-01-01T00:00:00.000Z" },
            ["version"] = new() { N = "1" }
        };
        if (woreda != null)
        {
            item["woreda"] = new AttributeValue { S = woreda };
        }

        if (aggregatedAt != null)
        {
            item["ratingsAggregatedAt"] = new AttributeValue { S = aggregatedAt };
        }

        if (sum != null)
        {
            item["ratingSum"] = new AttributeValue { N = sum.Value.ToString(CultureInfo.InvariantCulture) };
            item["ratingCount"] = new AttributeValue { N = count!.Value.ToString(CultureInfo.InvariantCulture) };
        }

        if (deletionState != null)
        {
            item["deletionState"] = new AttributeValue { S = deletionState };
        }

        await _db.Client.PutItemAsync(_db.Users, item);
        return userId;
    }

    /// <summary>A rating created before ESC-28: no <c>inAggregate</c>.</summary>
    private async Task<string> PutLegacyRating(string rateeId, string rateeRole, int stars)
    {
        var orderId = Id("order");
        var ratingId = $"{orderId}:{rateeId}";
        await _db.Client.PutItemAsync(_db.Ratings, new Dictionary<string, AttributeValue>
        {
            ["ratingId"] = new() { S = ratingId },
            ["orderId"] = new() { S = orderId },
            ["raterId"] = new() { S = "buyer-1" },
            ["rateeId"] = new() { S = rateeId },
            ["rateeRole"] = new() { S = rateeRole },
            ["stars"] = new() { N = stars.ToString(CultureInfo.InvariantCulture) },
            ["createdAt"] = new() { S = DateTime.UtcNow.ToString("o") },
            ["version"] = new() { N = "1" }
        });
        return ratingId;
    }

    /// <summary>A rating through the deployed create (rating-api.md "Stored aggregate" step 1); retried on a cancelled transaction.</summary>
    private async Task<string> Create(string rateeId, string rateeRole, int stars)
    {
        var orderId = Id("order");
        var ratingId = $"{orderId}:{rateeId}";
        var data = new Dictionary<string, object>
        {
            ["orderId"] = orderId,
            ["raterId"] = "buyer-1",
            ["rateeId"] = rateeId,
            ["rateeRole"] = rateeRole,
            ["stars"] = stars
        };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await Repo().CreateAsync(ratingId, data);
                result.IsSuccess.Should().BeTrue();
                return ratingId;
            }
            catch (TransactionCanceledException) when (attempt < 20)
            {
                // A concurrent transaction on the ratee's row (the client retries the sync operation).
                await Task.Delay(Random.Shared.Next(5, 30));
            }
        }
    }

    private async Task<Dictionary<string, AttributeValue>> Row(string table, string key, string id)
        => (await _db.Client.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue> { [key] = new() { S = id } },
            ConsistentRead = true
        })).Item;

    private Task<Dictionary<string, AttributeValue>> User(string userId) => Row(_db.Users, "userId", userId);

    private async Task<bool?> InAggregate(string ratingId)
        => (await Row(_db.Ratings, "ratingId", ratingId)).TryGetValue("inAggregate", out var flag) ? flag.BOOL : null;

    private static long Number(Dictionary<string, AttributeValue> row, string name)
        => row.TryGetValue(name, out var value) ? long.Parse(value.N, CultureInfo.InvariantCulture) : 0;

    /// <summary>The indexes are eventually consistent: wait until the ratee index shows the ratings.</summary>
    private async Task WaitForRatings(string rateeId, int count)
    {
        for (var i = 0; i < 50; i++)
        {
            var page = await _db.Client.QueryAsync(new QueryRequest
            {
                TableName = _db.Ratings,
                IndexName = RatingAggregateBackfill.RateeIndex,
                KeyConditionExpression = "rateeId = :u",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = rateeId } }
            });
            if (page.Items.Count >= count)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The ratee index never showed {count} ratings.");
    }

    /// <summary>A budget that allows <paramref name="rows"/> MayStartRow calls (the slice reads the clock once when made).</summary>
    private static CronRunSlice Budget(int rows = int.MaxValue)
    {
        var reads = 0;
        return new CronRunBudget(() => reads++ <= rows ? TimeSpan.Zero : TimeSpan.FromMinutes(10))
            .Slice(TimeSpan.FromMinutes(4));
    }

    [LocalDynamoDbFact]
    public async Task RatingCreate_ForAnUnstampedFarmerWithoutWoreda_StampsTheFullAggregate()
    {
        var farmer = await PutUser("farmer");
        var legacy5 = await PutLegacyRating(farmer, "farmer", 5);
        var legacy3 = await PutLegacyRating(farmer, "farmer", 3);
        var oldRole = await PutLegacyRating(farmer, "trucker", 1);
        await WaitForRatings(farmer, 3);

        var created = await Create(farmer, "farmer", 4);
        var beforeHeal = await User(farmer);
        Number(beforeHeal, "ratingSum").Should().Be(4, "the create ADDs only its own stars");
        beforeHeal.Should().NotContainKey("ratingsAggregatedAt");

        (await Heal().TryStampAsync(farmer, "farmer", new TestLambdaLogger())).Should().BeTrue();

        var row = await User(farmer);
        Number(row, "ratingSum").Should().Be(12);
        Number(row, "ratingCount").Should().Be(3);
        row["ratingsAggregatedAt"].S.Should().Be("2026-11-12T09:00:00.000Z");
        (await InAggregate(legacy5)).Should().BeTrue();
        (await InAggregate(legacy3)).Should().BeTrue();
        (await InAggregate(oldRole)).Should().BeFalse("a rating in another role stays out");
        (await InAggregate(created)).Should().BeTrue();

        // A second heal finds the row stamped and writes nothing; later creates keep adding.
        (await Heal().TryStampAsync(farmer, "farmer", new TestLambdaLogger())).Should().BeFalse();
        await Create(farmer, "farmer", 2);
        var after = await User(farmer);
        Number(after, "ratingSum").Should().Be(14);
        Number(after, "ratingCount").Should().Be(4);
    }

    [LocalDynamoDbFact]
    public async Task ConcurrentCreatesHealsAndABackfill_CountEveryRatingExactlyOnce()
    {
        var trucker = await PutUser("trucker");
        var legacyStars = new[] { 1, 2, 3, 4, 5, 5 };
        var legacy = new List<string>();
        foreach (var stars in legacyStars)
        {
            legacy.Add(await PutLegacyRating(trucker, "trucker", stars));
        }

        await WaitForRatings(trucker, legacyStars.Length);

        var newStars = new[] { 5, 4, 3, 2, 1, 5, 4, 3 };
        var creates = newStars.Select(stars => Create(trucker, "trucker", stars)).ToList();
        var heals = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var i = 0; i < 20 && !(await User(trucker)).ContainsKey("ratingsAggregatedAt"); i++)
            {
                await Heal().TryStampAsync(trucker, "trucker", new TestLambdaLogger());
            }
        }).ToList();
        var backfill = Task.Run(async () =>
        {
            try
            {
                await Backfill().BackfillUserAsync(trucker, "trucker", Now);
            }
            catch (AmazonDynamoDBException)
            {
                // Lost the race to a heal (a cancelled transaction): nothing written.
            }
        });

        var created = await Task.WhenAll(creates);
        await Task.WhenAll(heals);
        await backfill;

        var row = await User(trucker);
        row.Should().ContainKey("ratingsAggregatedAt");
        Number(row, "ratingCount").Should().Be(legacyStars.Length + newStars.Length);
        Number(row, "ratingSum").Should().Be(legacyStars.Sum() + newStars.Sum());
        foreach (var ratingId in legacy.Concat(created))
        {
            (await InAggregate(ratingId)).Should().BeTrue();
        }
    }

    [LocalDynamoDbFact]
    public async Task BackfillAction_WalksTheIndex_ScansTheRest_AndARerunWritesNothing()
    {
        var indexed = await PutUser("farmer", woreda: "Adama");
        await PutLegacyRating(indexed, "farmer", 4);
        var unindexed = await PutUser("trucker");
        var unindexed5 = await PutLegacyRating(unindexed, "trucker", 5);
        await PutLegacyRating(unindexed, "trucker", 2);
        var stamped = await PutUser("farmer", aggregatedAt: "2026-11-07T00:00:00.000Z", sum: 7, count: 2);
        var strayOnStamped = await PutLegacyRating(stamped, "farmer", 3);
        var buyer = await PutUser("buyer");
        var deleting = await PutUser("farmer", deletionState: "in_progress");
        await PutLegacyRating(deleting, "farmer", 5);
        await WaitForRatings(indexed, 1);
        await WaitForRatings(unindexed, 2);
        await WaitForRatings(deleting, 1);

        var summary = await Backfill().RunAsync(Now, Budget(), new TestLambdaContext());

        summary["outcome"].Should().Be("ok");
        summary.Should().NotContainKey("scanCursor");
        int.Parse(summary["unindexed"], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
        var walkedRow = await User(indexed);
        Number(walkedRow, "ratingSum").Should().Be(4);
        walkedRow.Should().ContainKey("ratingsAggregatedAt");
        var scannedRow = await User(unindexed);
        Number(scannedRow, "ratingSum").Should().Be(7);
        Number(scannedRow, "ratingCount").Should().Be(2);
        scannedRow["ratingsAggregatedAt"].S.Should().Be("2026-11-12T09:00:00.000Z");
        (await InAggregate(unindexed5)).Should().BeTrue();
        var stampedRow = await User(stamped);
        Number(stampedRow, "ratingSum").Should().Be(7, "a stamped row is never added to again");
        stampedRow["ratingsAggregatedAt"].S.Should().Be("2026-11-07T00:00:00.000Z");
        (await InAggregate(strayOnStamped)).Should().BeNull();
        (await User(buyer)).Should().NotContainKey("ratingsAggregatedAt");
        (await User(deleting)).Should().NotContainKey("ratingsAggregatedAt");

        var rerun = await Backfill().RunAsync(Now.AddHours(1), Budget(), new TestLambdaContext());

        rerun["outcome"].Should().Be("ok");
        rerun["users"].Should().Be("0");
        rerun["ratings"].Should().Be("0");
        Number(await User(unindexed), "ratingSum").Should().Be(7);
    }

    [LocalDynamoDbFact]
    public async Task BackfillAction_StoppedInTheScan_ResumesFromItsCursor_AndStampsEachUserOnce()
    {
        var farmers = new Dictionary<string, int>();
        foreach (var stars in new[] { 2, 3, 4 })
        {
            var farmer = await PutUser("farmer");
            await PutLegacyRating(farmer, "farmer", stars);
            farmers[farmer] = stars;
        }

        foreach (var farmer in farmers.Keys)
        {
            await WaitForRatings(farmer, 1);
        }

        // Two rows: the Scan's page and its first user; the second user finds the budget spent.
        var first = await Backfill().RunAsync(Now, Budget(rows: 2), new TestLambdaContext());

        first["outcome"].Should().Be("partial");
        first["unindexed"].Should().Be("1");
        var cursor = first["scanCursor"];
        farmers.Should().ContainKey(cursor, "the cursor is the last user the stopped run handled");
        (await User(cursor)).Should().ContainKey("ratingsAggregatedAt");

        var second = await Backfill().RunAsync(Now, Budget(), new TestLambdaContext(), scanFrom: cursor);

        second["outcome"].Should().Be("ok");
        second["unindexed"].Should().Be("2");
        foreach (var (farmer, stars) in farmers)
        {
            var row = await User(farmer);
            row.Should().ContainKey("ratingsAggregatedAt");
            Number(row, "ratingSum").Should().Be(stars);
            Number(row, "ratingCount").Should().Be(1);
        }

        (await Backfill().RunAsync(Now, Budget(), new TestLambdaContext()))["users"].Should().Be("0");
    }
}

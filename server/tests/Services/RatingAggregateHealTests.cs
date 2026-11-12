using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-162: farmers and truckers without <c>ratingsAggregatedAt</c> (no woreda, so not on
/// <c>RoleWaredaIndex</c>). The backfill action's users Scan after the index walk (filter, pages,
/// budget, cursor), the bounded heal the rating create calls, and the action's <c>scanFrom</c>.
/// The requests themselves are checked against real DynamoDB in
/// <c>Integration/RatingAggregateHealDynamoDbTests</c>.
/// </summary>
public class RatingAggregateHealTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    public RatingAggregateHealTests()
    {
        // The index walk finds nobody unless a test says otherwise.
        IndexUsers();
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
    }

    private static Dictionary<string, AttributeValue> UserItem(string userId, string role = "farmer")
        => new() { ["userId"] = new() { S = userId }, ["role"] = new() { S = role } };

    private static Dictionary<string, AttributeValue> Rating(string ratingId, int stars, string role = "farmer")
        => new()
        {
            ["ratingId"] = new() { S = ratingId },
            ["stars"] = new() { N = stars.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ["rateeRole"] = new() { S = role }
        };

    private void IndexUsers(params Dictionary<string, AttributeValue>[] farmers)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == RatingAggregateBackfill.RoleIndex), Arg.Any<CancellationToken>())
            .Returns(ci => new QueryResponse
            {
                Items = ((QueryRequest)ci[0]).ExpressionAttributeValues[":role"].S == "farmer"
                    ? farmers.ToList()
                    : new List<Dictionary<string, AttributeValue>>()
            });
    }

    private void Ratings(params Dictionary<string, AttributeValue>[] ratings)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == RatingAggregateBackfill.RateeIndex), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = ratings.ToList() });
    }

    /// <summary>The users Scan answers these pages in turn and records where each call started (the request object is reused).</summary>
    private List<string?> ScanPages(params ScanResponse[] pages)
    {
        var starts = new List<string?>();
        var call = 0;
        _dynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var request = (ScanRequest)ci[0];
                starts.Add(request.ExclusiveStartKey is { Count: > 0 } key ? key["userId"].S : null);
                return pages[Math.Min(call++, pages.Length - 1)];
            });
        return starts;
    }

    private static ScanResponse Page(string? lastKey, params Dictionary<string, AttributeValue>[] users) => new()
    {
        Items = users.ToList(),
        LastEvaluatedKey = lastKey == null
            ? new Dictionary<string, AttributeValue>()
            : new Dictionary<string, AttributeValue> { ["userId"] = new() { S = lastKey } }
    };

    private RatingAggregateBackfill Backfill() => new(_dynamoDb, "users", "ratings");

    /// <summary>A budget that allows <paramref name="rows"/> MayStartRow calls (the slice reads the clock once when made).</summary>
    private static CronRunSlice Budget(int rows = int.MaxValue)
    {
        var reads = 0;
        return new CronRunBudget(() => reads++ <= rows ? TimeSpan.Zero : TimeSpan.FromMinutes(10))
            .Slice(TimeSpan.FromMinutes(4));
    }

    private List<TransactWriteItemsRequest> Transactions()
        => _dynamoDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAmazonDynamoDB.TransactWriteItemsAsync))
            .Select(c => (TransactWriteItemsRequest)c.GetArguments()[0]!)
            .ToList();

    // ===== the action's users Scan =====

    [Fact]
    public async Task Backfill_ScansForTheUsersTheIndexMisses_AndStampsThem()
    {
        ScanRequest? scan = null;
        _dynamoDb.ScanAsync(Arg.Do<ScanRequest>(r => scan = r), Arg.Any<CancellationToken>())
            .Returns(Page(null, UserItem("trucker-9", "trucker")));
        Ratings(Rating("o1:trucker-9", 4, "trucker"), Rating("o2:trucker-9", 2, "trucker"));

        var summary = await Backfill().RunAsync(Now, Budget(), _context);

        summary["outcome"].Should().Be("ok");
        summary["users"].Should().Be("1");
        summary["unindexed"].Should().Be("1");
        summary["ratings"].Should().Be("2");
        summary.Should().NotContainKey("scanCursor");
        scan!.TableName.Should().Be("users");
        scan.IndexName.Should().BeNull("a base-table Scan: the index is what misses these rows");
        scan.FilterExpression.Should().Be("#role IN (:farmer, :trucker) AND attribute_not_exists(#raa) AND attribute_not_exists(#ds)");
        scan.ExpressionAttributeNames["#raa"].Should().Be("ratingsAggregatedAt");
        scan.ExpressionAttributeNames["#ds"].Should().Be("deletionState");
        scan.ProjectionExpression.Should().Be("userId, #role");
        scan.ExclusiveStartKey.Should().BeNullOrEmpty();
        var user = Transactions().Single().TransactItems.Last().Update;
        user.Key["userId"].S.Should().Be("trucker-9");
        user.ExpressionAttributeValues[":role"].S.Should().Be("trucker");
        user.ExpressionAttributeValues[":sum"].N.Should().Be("6");
        user.ExpressionAttributeValues[":count"].N.Should().Be("2");
        user.UpdateExpression.Should().Be("ADD #rs :sum, #rc :count SET #raa = :now");
        Logs.Should().Contain("REPUTATION_BACKFILL users=1 ratings=2").And.Contain("unindexed=1 scanDone=true");
    }

    [Fact]
    public async Task Backfill_Scan_SkipsAUserTheWalkAlreadyTried()
    {
        // The Scan is eventually consistent: it can still show a row the walk just stamped.
        IndexUsers(UserItem("farmer-1"));
        ScanPages(Page(null, UserItem("farmer-1")));
        Ratings(Rating("o1:farmer-1", 5));

        var summary = await Backfill().RunAsync(Now, Budget(), _context);

        summary["users"].Should().Be("1");
        summary["unindexed"].Should().Be("0");
        Transactions().Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_Scan_ReadsEveryPage_FromScanFrom()
    {
        var starts = ScanPages(
            Page("u-7", UserItem("u-6")),
            Page(null, UserItem("u-8", "trucker")));
        Ratings();

        var summary = await Backfill().RunAsync(Now, Budget(), _context, scanFrom: "u-5");

        starts.Should().Equal("u-5", "u-7");
        summary["unindexed"].Should().Be("2");
        summary["outcome"].Should().Be("ok");
        Transactions().Select(t => t.TransactItems.Last().Update.Key["userId"].S).Should().Equal("u-6", "u-8");
    }

    [Fact]
    public async Task Backfill_Scan_StopsOnTheBudget_AndAnswersTheLastUserItHandled()
    {
        ScanPages(Page(null, UserItem("a"), UserItem("b"), UserItem("c")));
        Ratings();

        // Three rows: the page, then a; b finds the budget spent.
        var summary = await Backfill().RunAsync(Now, Budget(rows: 2), _context);

        summary["outcome"].Should().Be("partial");
        summary["unindexed"].Should().Be("1");
        summary["scanCursor"].Should().Be("a", "the next run resumes after a, so b and c are neither skipped nor repeated");
        Logs.Should().Contain("scanDone=false");
    }

    [Fact]
    public async Task Backfill_Scan_StoppedBeforeItsFirstPage_AnswersTheScanFromItWasGiven()
    {
        ScanPages(Page(null, UserItem("a")));

        var summary = await Backfill().RunAsync(Now, Budget(rows: 0), _context, scanFrom: "u-5");

        summary["outcome"].Should().Be("partial");
        summary["scanCursor"].Should().Be("u-5");
        await _dynamoDb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backfill_AWalkWithUsersLeft_DoesNotStartTheScan()
    {
        IndexUsers(UserItem("farmer-1"), UserItem("farmer-2"));
        ScanPages(Page(null, UserItem("a")));
        Ratings();

        // One row: farmer-1; farmer-2 is left over.
        var summary = await Backfill().RunAsync(Now, Budget(rows: 1), _context, scanFrom: "u-5");

        summary["leftover"].Should().Be("1");
        summary["outcome"].Should().Be("partial");
        summary["scanCursor"].Should().Be("u-5", "the Scan has not moved, so the next run starts it from the same place");
        await _dynamoDb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backfill_Scan_AFailedUser_IsCountedAndTheScanGoesOn()
    {
        ScanPages(Page(null, UserItem("a"), UserItem("b")));
        Ratings();
        _dynamoDb.TransactWriteItemsAsync(
                Arg.Is<TransactWriteItemsRequest>(r => r.TransactItems.Last().Update.Key["userId"].S == "a"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("stamped meanwhile"));

        var summary = await Backfill().RunAsync(Now, Budget(), _context);

        summary["failed"].Should().Be("1");
        summary["unindexed"].Should().Be("1");
        summary["outcome"].Should().Be("partial");
        summary.Should().NotContainKey("scanCursor", "the Scan finished: a rerun finds a again");
    }

    // ===== the heal the rating create calls =====

    private RatingAggregateHeal Heal() => new(_dynamoDb, "users", "ratings") { Clock = () => Now };

    [Fact]
    public async Task Heal_AnUnstampedFarmer_AddsTheRolesStars_FlagsEachRating_AndStampsInOneTransaction()
    {
        QueryRequest? query = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => query = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = { Rating("o1:farmer-1", 5), Rating("o2:farmer-1", 1, "trucker") } });

        var stamped = await Heal().TryStampAsync("farmer-1", "farmer", _context.Logger);

        stamped.Should().BeTrue();
        query!.IndexName.Should().Be(RatingAggregateBackfill.RateeIndex);
        query.Limit.Should().Be(RatingAggregateHeal.MaxRatings + 1, "a bounded read on a request path");
        query.FilterExpression.Should().Be("attribute_not_exists(#ia)");
        var items = Transactions().Single().TransactItems;
        items.Should().HaveCount(3);
        items.Take(2).Should().OnlyContain(i => i.Update.ConditionExpression == "attribute_not_exists(#ia)");
        items[1].Update.ExpressionAttributeValues[":ia"].BOOL.Should().BeFalse("a rating in another role stays out of the aggregate");
        var user = items[2].Update;
        user.ExpressionAttributeValues[":sum"].N.Should().Be("5");
        user.ExpressionAttributeValues[":count"].N.Should().Be("1");
        user.ExpressionAttributeValues[":now"].S.Should().Be("2026-11-12T09:00:00.000Z");
        user.ConditionExpression.Should().Be(
            "attribute_exists(userId) AND attribute_not_exists(#raa) AND attribute_not_exists(#ds) AND #role = :role");
        Logs.Should().Contain("RATING_AGGREGATE_HEALED");
    }

    [Fact]
    public async Task Heal_MoreRatingsThanOneTransaction_WritesNothing()
    {
        Ratings(Enumerable.Range(0, RatingAggregateHeal.MaxRatings + 1).Select(i => Rating($"o{i}:farmer-1", 4)).ToArray());

        var stamped = await Heal().TryStampAsync("farmer-1", "farmer", _context.Logger);

        stamped.Should().BeFalse();
        Transactions().Should().BeEmpty();
        Logs.Should().Contain("RATING_AGGREGATE_HEAL_SKIPPED");
    }

    [Fact]
    public async Task Heal_StopsReadingAfterItsPages_AndWritesNothing()
    {
        // A user with many flagged ratings and few without the flag: each page brings one.
        var calls = 0;
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse
            {
                Items = { Rating($"o{calls}:farmer-1", 4) },
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["ratingId"] = new() { S = $"o{calls++}:farmer-1" } }
            });

        var stamped = await Heal().TryStampAsync("farmer-1", "farmer", _context.Logger);

        stamped.Should().BeFalse();
        calls.Should().Be(RatingAggregateBackfill.MaxSmallPages);
        Transactions().Should().BeEmpty();
    }

    [Fact]
    public async Task Heal_ABuyer_IsNotTouched()
    {
        (await Heal().TryStampAsync("buyer-1", "buyer", _context.Logger)).Should().BeFalse();

        _dynamoDb.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Heal_ALostRace_ReturnsFalse_AndLogsNoFailure()
    {
        Ratings(Rating("o1:farmer-1", 5));
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("stamped meanwhile"));

        (await Heal().TryStampAsync("farmer-1", "farmer", _context.Logger)).Should().BeFalse();

        Logs.Should().NotContain(RatingAggregateHeal.FailedToken);
    }

    [Fact]
    public async Task Heal_AnyOtherFailure_IsLogged_AndNeverThrows()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        (await Heal().TryStampAsync("farmer-1", "farmer", _context.Logger)).Should().BeFalse();

        Logs.Should().Contain(RatingAggregateHeal.FailedToken).And.Contain("[AmazonDynamoDBException]");
    }

    // ===== the action's scanFrom =====

    private static APIGatewayProxyRequest Body(string json) => new() { Body = json };

    [Fact]
    public async Task Cron_ScanFrom_ResumesTheScan()
    {
        var starts = ScanPages(Page(null));
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { RatingBackfill = Backfill() };

        var response = await cron.FunctionHandler(
            Body("{\"action\":\"reputation_backfill_ratings\",\"scanFrom\":\"u-5\"}"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"ok\"").And.Contain("\"unindexed\":\"0\"");
        starts.Should().Equal("u-5");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task Cron_ABadScanFrom_IsRefused(string value)
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { RatingBackfill = Backfill() };

        var response = await cron.FunctionHandler(
            Body($"{{\"action\":\"reputation_backfill_ratings\",\"scanFrom\":{value}}}"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ORDER_CRON_VALIDATION").And.Contain("scanFrom");
        _dynamoDb.ReceivedCalls().Should().BeEmpty();
    }
}

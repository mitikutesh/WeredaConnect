using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-28 (rating-api.md 1.2.0 "Stored aggregate"): the rating create is one transaction with the
/// ratee's <c>ADD ratingSum, ratingCount</c> and their heal marker; a role that changed puts the
/// rating alone with <c>inAggregate: false</c>; an existing rating is the usual conflict; the client
/// never sees <c>inAggregate</c>. The one-time backfill adds each rating once, a rerun nothing.
/// </summary>
public class RatingRepositoryAggregateTests
{
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();

    private static Dictionary<string, object> Data(string role = "farmer") => new()
    {
        ["ratingId"] = "order-1:farmer-1",
        ["orderId"] = "order-1",
        ["raterId"] = "buyer-1",
        ["rateeId"] = "farmer-1",
        ["rateeRole"] = role,
        ["stars"] = 4L,
        ["inAggregate"] = true // a client cannot set it
    };

    [Fact]
    public async Task Create_IsOneTransaction_RatingSumCountAndHeal()
    {
        TransactWriteItemsRequest? sent = null;
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        var repo = new RatingRepository(_dynamoDb, "ratings", "users", "reputation-events");

        var result = await repo.CreateAsync("order-1:farmer-1", Data());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().NotContainKey("inAggregate");
        var items = sent!.TransactItems;
        items.Should().HaveCount(3);
        items[0].Put.TableName.Should().Be("ratings");
        items[0].Put.ConditionExpression.Should().Be("attribute_not_exists(ratingId)");
        items[0].Put.Item["inAggregate"].BOOL.Should().BeTrue();
        items[1].Update.TableName.Should().Be("users");
        items[1].Update.Key["userId"].S.Should().Be("farmer-1");
        items[1].Update.UpdateExpression.Should().Be("ADD #rs :stars, #rc :one");
        items[1].Update.ConditionExpression.Should().Be("attribute_exists(userId) AND #role = :role AND attribute_not_exists(#ds)");
        items[1].Update.ExpressionAttributeValues[":stars"].N.Should().Be("4");
        items[1].Update.ExpressionAttributeValues[":role"].S.Should().Be("farmer");
        items[2].Update.TableName.Should().Be("reputation-events");
        items[2].Update.Key["eventId"].S.Should().StartWith("HEAL#");
    }

    [Fact]
    public async Task Create_RatingExists_IsAConflict()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("x")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" } }
            });
        var repo = new RatingRepository(_dynamoDb, "ratings", "users", "reputation-events");

        var result = await repo.CreateAsync("order-1:farmer-1", Data());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("SYNC_CONFLICT");
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_RoleChanged_PutsTheRatingAlone_OutOfTheAggregate()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("x")
            {
                CancellationReasons = new List<CancellationReason> { new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" } }
            });
        PutItemRequest? put = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        var repo = new RatingRepository(_dynamoDb, "ratings", "users", "reputation-events");

        var result = await repo.CreateAsync("order-1:farmer-1", Data());

        result.IsSuccess.Should().BeTrue();
        put!.Item["inAggregate"].BOOL.Should().BeFalse();
        put.ConditionExpression.Should().Be("attribute_not_exists(ratingId)");
    }

    [Fact]
    public async Task Create_WithoutTheUsersTable_IsThePlainPut()
    {
        PutItemRequest? put = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        await new RatingRepository(_dynamoDb, "ratings").CreateAsync("order-1:farmer-1", Data());

        put!.Item.Should().NotContainKey("inAggregate");
        await _dynamoDb.DidNotReceive().TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>());
    }

    // ===== the backfill (architecture doc 12.2) =====

    private void Users(params Dictionary<string, AttributeValue>[] farmers)
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == RatingAggregateBackfill.RoleIndex), Arg.Any<CancellationToken>())
            .Returns(ci => new QueryResponse
            {
                Items = ((QueryRequest)ci[0]).ExpressionAttributeValues[":role"].S == "farmer"
                    ? farmers.ToList()
                    : new List<Dictionary<string, AttributeValue>>()
            });
    }

    [Fact]
    public async Task Backfill_AddsTheRolesStars_FlagsEveryRating_StampsTheUser()
    {
        Users(new Dictionary<string, AttributeValue> { ["userId"] = new() { S = "farmer-1" } });
        QueryRequest? ratingsQuery = null;
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == RatingAggregateBackfill.RateeIndex), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ratingsQuery = (QueryRequest)ci[0];
                return new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new() { ["ratingId"] = new() { S = "o1:farmer-1" }, ["stars"] = new() { N = "5" }, ["rateeRole"] = new() { S = "farmer" } },
                        new() { ["ratingId"] = new() { S = "o2:farmer-1" }, ["stars"] = new() { N = "3" }, ["rateeRole"] = new() { S = "farmer" } },
                        new() { ["ratingId"] = new() { S = "o3:farmer-1" }, ["stars"] = new() { N = "1" }, ["rateeRole"] = new() { S = "trucker" } }
                    }
                };
            });
        TransactWriteItemsRequest? sent = null;
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        var backfill = new RatingAggregateBackfill(_dynamoDb, "users", "ratings");

        var summary = await backfill.RunAsync(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4)), new TestLambdaContext());

        summary["users"].Should().Be("1");
        summary["ratings"].Should().Be("3");
        ratingsQuery!.FilterExpression.Should().Be("attribute_not_exists(#ia)");
        var items = sent!.TransactItems;
        items.Should().HaveCount(4);
        items.Take(3).Should().OnlyContain(i => i.Update.ConditionExpression == "attribute_not_exists(#ia)");
        items[2].Update.ExpressionAttributeValues[":ia"].BOOL.Should().BeFalse();
        items[0].Update.ExpressionAttributeValues[":ia"].BOOL.Should().BeTrue();
        var user = items[3].Update;
        user.UpdateExpression.Should().Be("ADD #rs :sum, #rc :count SET #raa = :now");
        user.ExpressionAttributeValues[":sum"].N.Should().Be("8");
        user.ExpressionAttributeValues[":count"].N.Should().Be("2");
        user.ConditionExpression.Should().Contain("attribute_not_exists(#raa)");
    }

    [Fact]
    public async Task Backfill_Rerun_SkipsStampedUsers_AndAddsNothing()
    {
        Users(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["ratingsAggregatedAt"] = new() { S = "2026-11-20T00:00:00.000Z" }
        });
        var backfill = new RatingAggregateBackfill(_dynamoDb, "users", "ratings");

        var summary = await backfill.RunAsync(DateTime.UtcNow, new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4)), new TestLambdaContext());

        summary["users"].Should().Be("0");
        summary["skipped"].Should().Be("1");
        await _dynamoDb.DidNotReceive().TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backfill_AUserWithNoRatings_IsStampedAtZero()
    {
        Users(new Dictionary<string, AttributeValue> { ["userId"] = new() { S = "farmer-1" } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == RatingAggregateBackfill.RateeIndex), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        TransactWriteItemsRequest? sent = null;
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        await new RatingAggregateBackfill(_dynamoDb, "users", "ratings")
            .RunAsync(DateTime.UtcNow, new CronRunBudget(() => TimeSpan.Zero).Slice(TimeSpan.FromMinutes(4)), new TestLambdaContext());

        sent!.TransactItems.Should().ContainSingle();
        sent.TransactItems[0].Update.ExpressionAttributeValues[":sum"].N.Should().Be("0");
    }
}

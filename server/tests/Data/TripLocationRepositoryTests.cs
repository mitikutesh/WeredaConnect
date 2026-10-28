using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// LT-05: the trip-locations table. The put is conditional on the sort key, so a resent ping writes
/// nothing twice; the hour read lists sort keys strongly consistent over every page. These tests
/// assert the request shapes; no DynamoDB integration test runs them yet.
/// </summary>
public class TripLocationRepositoryTests
{
    private const string Table = "test-trip-locations";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly TripLocationRepository _repo;

    public TripLocationRepositoryTests()
    {
        _repo = new TripLocationRepository(_dynamoDb, Table);
    }

    private static TripLocationRow Fix() => new(
        "trip-101", "2026-10-27T08:42:00.000Z#fix", "trucker-789",
        8.7523, 38.9785, 0.00001, "hourly", false, null, "2026-10-27T08:42:03.000Z", 1_798_000_000);

    [Fact]
    public async Task TryPut_AFix_WritesItsAttributes_ConditionalOnItsKey()
    {
        PutItemRequest? sent = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var isNew = await _repo.TryPutAsync(Fix());

        isNew.Should().BeTrue();
        sent!.TableName.Should().Be(Table);
        sent.ConditionExpression.Should().Be("attribute_not_exists(sortKey)");
        sent.Item["tripId"].S.Should().Be("trip-101");
        sent.Item["sortKey"].S.Should().Be("2026-10-27T08:42:00.000Z#fix");
        sent.Item["truckerId"].S.Should().Be("trucker-789");
        sent.Item["latitude"].N.Should().Be("8.7523");
        sent.Item["longitude"].N.Should().Be("38.9785");
        // Fixed-point text, never "1E-05".
        sent.Item["accuracyM"].N.Should().Be("0.00001");
        sent.Item["source"].S.Should().Be("hourly");
        sent.Item["mocked"].BOOL.Should().BeFalse();
        sent.Item["receivedAt"].S.Should().Be("2026-10-27T08:42:03.000Z");
        sent.Item["expiresAt"].N.Should().Be("1798000000");
        sent.Item.Should().NotContainKey("pausedUntil");
    }

    [Fact]
    public async Task TryPut_AnEvent_OmitsTheFixAttributes()
    {
        PutItemRequest? sent = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _repo.TryPutAsync(new TripLocationRow(
            "trip-101", "2026-10-27T08:42:00.000Z#paused", "trucker-789",
            null, null, null, null, null, "2026-10-27T09:12:00.000Z", "2026-10-27T08:42:03.000Z", 1_798_000_000));

        sent!.Item["pausedUntil"].S.Should().Be("2026-10-27T09:12:00.000Z");
        foreach (var key in new[] { "latitude", "longitude", "accuracyM", "source", "mocked" })
        {
            sent.Item.Should().NotContainKey(key);
        }
    }

    [Fact]
    public async Task TryPut_AnExistingKey_IsARepeat()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));

        var isNew = await _repo.TryPutAsync(Fix());

        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task ListSortKeys_ReadsThePrefix_StronglyConsistent_OverEveryPage()
    {
        var requests = new List<QueryRequest>();
        var page2 = new Dictionary<string, AttributeValue>
        {
            ["tripId"] = new() { S = "trip-101" },
            ["sortKey"] = new() { S = "2026-10-27T08:10:00.000Z#fix" }
        };
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => requests.Add(r)), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new() { ["sortKey"] = new AttributeValue { S = "2026-10-27T08:05:00.000Z#fix" } },
                        new() { ["sortKey"] = new AttributeValue { S = "2026-10-27T08:10:00.000Z#paused" } }
                    },
                    LastEvaluatedKey = page2
                },
                new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new() { ["sortKey"] = new AttributeValue { S = "2026-10-27T08:40:00.000Z#fix" } }
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

        var keys = await _repo.ListSortKeysAsync("trip-101", "2026-10-27T08:");

        keys.Should().Equal(
            "2026-10-27T08:05:00.000Z#fix", "2026-10-27T08:10:00.000Z#paused", "2026-10-27T08:40:00.000Z#fix");
        requests.Should().HaveCount(2);
        var first = requests[0];
        first.TableName.Should().Be(Table);
        first.KeyConditionExpression.Should().Be("#t = :t AND begins_with(#sk, :prefix)");
        first.ExpressionAttributeNames["#t"].Should().Be("tripId");
        first.ExpressionAttributeNames["#sk"].Should().Be("sortKey");
        first.ExpressionAttributeValues[":t"].S.Should().Be("trip-101");
        first.ExpressionAttributeValues[":prefix"].S.Should().Be("2026-10-27T08:");
        first.ProjectionExpression.Should().Be("#sk");
        first.ConsistentRead.Should().BeTrue();
        first.IndexName.Should().BeNullOrEmpty();
        requests[1].ExclusiveStartKey.Should().BeSameAs(page2);
    }

    [Fact]
    public async Task ListByTrip_ReadsTheWholeTripInCaptureOrder_OverEveryPage_AndParsesTheRows()
    {
        var requests = new List<QueryRequest>();
        var page2 = new Dictionary<string, AttributeValue>
        {
            ["tripId"] = new() { S = "trip-101" },
            ["sortKey"] = new() { S = "2026-10-27T07:55:00.000Z#paused" }
        };
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => requests.Add(r)), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new()
                        {
                            ["tripId"] = new AttributeValue { S = "trip-101" },
                            ["sortKey"] = new AttributeValue { S = "2026-10-27T05:05:00.000Z#fix" },
                            ["truckerId"] = new AttributeValue { S = "trucker-789" },
                            ["latitude"] = new AttributeValue { N = "8.1504" },
                            ["longitude"] = new AttributeValue { N = "38.8196" },
                            ["accuracyM"] = new AttributeValue { N = "12.5" },
                            ["source"] = new AttributeValue { S = "scan" },
                            ["mocked"] = new AttributeValue { BOOL = true },
                            ["receivedAt"] = new AttributeValue { S = "2026-10-27T05:05:02.000Z" },
                            ["expiresAt"] = new AttributeValue { N = "1798000000" }
                        },
                        // No sort key: skipped.
                        new() { ["tripId"] = new AttributeValue { S = "trip-101" } }
                    },
                    LastEvaluatedKey = page2
                },
                new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new()
                        {
                            ["tripId"] = new AttributeValue { S = "trip-101" },
                            ["sortKey"] = new AttributeValue { S = "2026-10-27T07:55:00.000Z#paused" },
                            ["truckerId"] = new AttributeValue { S = "trucker-789" },
                            ["pausedUntil"] = new AttributeValue { S = "2026-10-27T08:25:00.000Z" },
                            ["receivedAt"] = new AttributeValue { S = "2026-10-27T07:55:01.000Z" },
                            ["expiresAt"] = new AttributeValue { N = "1798000000" }
                        }
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

        var rows = await _repo.ListByTripAsync("trip-101");

        rows.Should().Equal(
            new TripLocationRow("trip-101", "2026-10-27T05:05:00.000Z#fix", "trucker-789", 8.1504, 38.8196, 12.5, "scan", true,
                null, "2026-10-27T05:05:02.000Z", 1_798_000_000),
            new TripLocationRow("trip-101", "2026-10-27T07:55:00.000Z#paused", "trucker-789", null, null, null, null, null,
                "2026-10-27T08:25:00.000Z", "2026-10-27T07:55:01.000Z", 1_798_000_000));
        requests.Should().HaveCount(2);
        var first = requests[0];
        first.TableName.Should().Be(Table);
        first.KeyConditionExpression.Should().Be("#t = :t");
        first.ExpressionAttributeNames["#t"].Should().Be("tripId");
        first.ExpressionAttributeValues[":t"].S.Should().Be("trip-101");
        first.ScanIndexForward.Should().BeTrue();
        first.IndexName.Should().BeNullOrEmpty();
        requests[1].ExclusiveStartKey.Should().BeSameAs(page2);
    }
}

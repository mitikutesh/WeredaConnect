using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-21: the notifications inbox. The put is conditional on the sort key, so a stream retry
/// writes nothing twice; a user's rows read newest first, optionally only those after a cursor.
/// These tests assert the request shapes; no DynamoDB integration test runs them yet.
/// </summary>
public class NotificationRepositoryTests
{
    private const string Table = "test-notifications";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly NotificationRepository _repo;

    public NotificationRepositoryTests()
    {
        _repo = new NotificationRepository(_dynamoDb, Table);
    }

    private static NotificationRow Row(string? orderId = "order-1") => new(
        "farmer-1",
        "2026-10-26T12:00:00.0000000Z#order_request#order-1",
        "order_request",
        "order",
        "order-1",
        orderId,
        new Dictionary<string, string> { ["buyerName"] = "Meron Alemu", ["quantityKg"] = "500" },
        "2026-10-26T12:00:00.0000000Z",
        1_800_000_000);

    [Fact]
    public async Task TryPut_WritesTheRow_ConditionalOnItsKey()
    {
        PutItemRequest? sent = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var isNew = await _repo.TryPutAsync(Row());

        isNew.Should().BeTrue();
        sent!.TableName.Should().Be(Table);
        sent.ConditionExpression.Should().Be("attribute_not_exists(sortKey)");
        sent.Item["userId"].S.Should().Be("farmer-1");
        sent.Item["sortKey"].S.Should().Be("2026-10-26T12:00:00.0000000Z#order_request#order-1");
        sent.Item["type"].S.Should().Be("order_request");
        sent.Item["orderId"].S.Should().Be("order-1");
        sent.Item["params"].M["buyerName"].S.Should().Be("Meron Alemu");
        sent.Item["expiresAt"].N.Should().Be("1800000000");
        sent.Item["writtenAt"].S.Should().Be("2026-10-26T12:00:00.0000000Z");
    }

    [Fact]
    public async Task TryPut_WithoutAnOrder_OmitsTheAttribute()
    {
        PutItemRequest? sent = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _repo.TryPutAsync(Row(orderId: null));

        sent!.Item.Should().NotContainKey("orderId");
    }

    [Fact]
    public async Task TryPut_ExistingKey_IsADuplicate()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));

        var isNew = await _repo.TryPutAsync(Row());

        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task TryPut_OtherFailures_Propagate()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var act = () => _repo.TryPutAsync(Row());

        await act.Should().ThrowAsync<ProvisionedThroughputExceededException>();
    }

    [Fact]
    public async Task QueryByUser_ReadsTheCallersRows_NewestFirst()
    {
        QueryRequest? sent = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new()
                {
                    new()
                    {
                        ["userId"] = new AttributeValue { S = "farmer-1" },
                        ["sortKey"] = new AttributeValue { S = "k-2" },
                        ["type"] = new AttributeValue { S = "order_request" },
                        ["entity"] = new AttributeValue { S = "order" },
                        ["entityId"] = new AttributeValue { S = "order-1" },
                        ["params"] = new AttributeValue
                        {
                            M = new Dictionary<string, AttributeValue> { ["buyerName"] = new() { S = "Meron Alemu" } }
                        },
                        ["createdAt"] = new AttributeValue { S = "2026-10-26T12:00:00Z" },
                        ["expiresAt"] = new AttributeValue { N = "1800000000" }
                    }
                },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>
                {
                    ["userId"] = new() { S = "farmer-1" },
                    ["sortKey"] = new() { S = "k-2" }
                }
            });

        var (items, lastKey) = await _repo.QueryByUserAsync("farmer-1", limit: 20);

        sent!.KeyConditionExpression.Should().Be("userId = :u");
        sent.ExpressionAttributeValues[":u"].S.Should().Be("farmer-1");
        sent.ScanIndexForward.Should().BeFalse();
        sent.Limit.Should().Be(20);
        items.Should().ContainSingle();
        items[0].Params["buyerName"].Should().Be("Meron Alemu");
        items[0].OrderId.Should().BeNull();
        items[0].ExpiresAt.Should().Be(1_800_000_000);
        lastKey.Should().NotBeNull();
    }

    [Fact]
    public async Task QueryByUser_WrittenAfter_ReadsTheWriteTimeIndex_StronglyConsistent()
    {
        QueryRequest? sent = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new() });

        await _repo.QueryByUserAsync("farmer-1", limit: 20, writtenAfter: "2026-10-26T11:58:00.0000000Z");

        sent!.IndexName.Should().Be(NotificationRepository.WrittenAtIndex);
        sent.KeyConditionExpression.Should().Be("userId = :u AND writtenAt > :after");
        sent.ExpressionAttributeValues[":after"].S.Should().Be("2026-10-26T11:58:00.0000000Z");
        sent.ConsistentRead.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 50)]
    public async Task QueryByUser_ClampsThePageSize(int requested, int expected)
    {
        QueryRequest? sent = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new() });

        var (_, lastKey) = await _repo.QueryByUserAsync("farmer-1", requested);

        sent!.Limit.Should().Be(expected);
        lastKey.Should().BeNull();
    }
}

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class MessageRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly MessageRepository _repository;

    private const string TableName = "test-messages";

    public MessageRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new MessageRepository(_mockDynamoDb, TableName);
    }

    private static Dictionary<string, AttributeValue> MessageItem(string messageId, string createdAt) => new()
    {
        { "messageId", new AttributeValue { S = messageId } },
        { "createdAt", new AttributeValue { S = createdAt } }
    };

    private static QueryResponse Page(
        List<Dictionary<string, AttributeValue>> items,
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null) => new()
    {
        Items = items,
        LastEvaluatedKey = lastEvaluatedKey ?? new Dictionary<string, AttributeValue>()
    };

    // Regression: the since-query returned a single capped page. The sync-down
    // caller merges recipient + sender results and advances the client cursor to
    // the merged max createdAt, so a truncated direction permanently dropped its
    // unreturned range. The query must drain ALL pages via ExclusiveStartKey.
    [Fact]
    public async Task QueryByRecipientSinceAsync_DrainsAllPages()
    {
        var requests = new List<QueryRequest>();
        var page1 = Page(
            new List<Dictionary<string, AttributeValue>>
            {
                MessageItem("msg-1", "2026-07-01T00:00:01Z"),
                MessageItem("msg-2", "2026-07-01T00:00:02Z")
            },
            lastEvaluatedKey: MessageItem("msg-2", "2026-07-01T00:00:02Z"));
        var page2 = Page(
            new List<Dictionary<string, AttributeValue>>
            {
                MessageItem("msg-3", "2026-07-01T00:00:03Z")
            });
        _mockDynamoDb.QueryAsync(Arg.Do<QueryRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(page1, page2);

        var result = await _repository.QueryByRecipientSinceAsync(
            "user-1", "2026-06-30T00:00:00Z");

        Assert.Equal(3, result.Count);
        Assert.Equal(2, requests.Count);
        // First page starts fresh; the second must resume from the returned key.
        Assert.True(requests[0].ExclusiveStartKey == null || requests[0].ExclusiveStartKey.Count == 0);
        Assert.Equal("msg-2", requests[1].ExclusiveStartKey["messageId"].S);
        Assert.All(requests, r =>
        {
            Assert.Equal("RecipientIdCreatedAtIndex", r.IndexName);
            Assert.Equal("#k = :val AND #ca > :since", r.KeyConditionExpression);
            Assert.True(r.ScanIndexForward); // oldest first so the cursor advances monotonically
        });
    }

    // Regression: DynamoDB rejects a request whose ExpressionAttributeNames
    // contains an unused key (ValidationException). The recipient-bootstrap case
    // (empty `since`, no local messages yet) has no createdAt clause, so #ca must
    // NOT be declared then — declaring it made first-message bootstrap impossible.
    [Fact]
    public async Task QueryByRecipientSinceAsync_EmptySince_DoesNotDeclareUnusedCreatedAtName()
    {
        var requests = new List<QueryRequest>();
        _mockDynamoDb.QueryAsync(Arg.Do<QueryRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(Page(new List<Dictionary<string, AttributeValue>>
            {
                MessageItem("msg-1", "2026-07-01T00:00:01Z")
            }));

        var result = await _repository.QueryByRecipientSinceAsync("user-1", "");

        var request = Assert.Single(requests);
        Assert.Equal("#k = :val", request.KeyConditionExpression);
        Assert.False(request.ExpressionAttributeNames.ContainsKey("#ca"));
        Assert.False(request.ExpressionAttributeValues.ContainsKey(":since"));
        Assert.Equal("recipientId", request.ExpressionAttributeNames["#k"]);
        Assert.Single(result);
    }

    [Fact]
    public async Task QueryBySenderSinceAsync_WithSince_QueriesSenderIndexWithCreatedAtClause()
    {
        var requests = new List<QueryRequest>();
        _mockDynamoDb.QueryAsync(Arg.Do<QueryRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(Page(new List<Dictionary<string, AttributeValue>>()));

        await _repository.QueryBySenderSinceAsync("user-1", "2026-06-30T00:00:00Z");

        var request = Assert.Single(requests);
        Assert.Equal("SenderIdCreatedAtIndex", request.IndexName);
        Assert.Equal("#k = :val AND #ca > :since", request.KeyConditionExpression);
        Assert.Equal("senderId", request.ExpressionAttributeNames["#k"]);
        Assert.Equal("createdAt", request.ExpressionAttributeNames["#ca"]);
        Assert.Equal("2026-06-30T00:00:00Z", request.ExpressionAttributeValues[":since"].S);
    }
}

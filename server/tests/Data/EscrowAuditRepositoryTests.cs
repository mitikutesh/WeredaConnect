using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class EscrowAuditRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly EscrowAuditRepository _repository;

    private const string TableName = "test-escrow-audit";

    public EscrowAuditRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new EscrowAuditRepository(_mockDynamoDb, TableName);
    }

    private static Dictionary<string, AttributeValue> AuditItem(
        string auditId,
        string escrowId,
        string timestamp)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "auditId", new AttributeValue { S = auditId } },
            { "escrowId", new AttributeValue { S = escrowId } },
            { "action", new AttributeValue { S = "release" } },
            { "fromState", new AttributeValue { S = "escrowed" } },
            { "toState", new AttributeValue { S = "released" } },
            { "actor", new AttributeValue { S = "system" } },
            { "timestamp", new AttributeValue { S = timestamp } }
        };
    }

    [Fact]
    public async Task GetRecentAcrossEscrowsAsync_SortsNewestFirstAcrossEscrows()
    {
        // Scan returns items in arbitrary (non-chronological) order, spanning escrows.
        var items = new List<Dictionary<string, AttributeValue>>
        {
            AuditItem("a1", "escrow-1", "2026-06-01T10:00:00Z"),
            AuditItem("a3", "escrow-2", "2026-06-03T10:00:00Z"),
            AuditItem("a2", "escrow-1", "2026-06-02T10:00:00Z")
        };
        _mockDynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = items });

        var result = await _repository.GetRecentAcrossEscrowsAsync(100);

        Assert.Equal(3, result.Count);
        // Newest first, regardless of scan order or escrow.
        Assert.Equal("a3", result[0]["auditId"]);
        Assert.Equal("a2", result[1]["auditId"]);
        Assert.Equal("a1", result[2]["auditId"]);
        // Spans multiple escrows (the global feed, not a single partition).
        Assert.Equal("escrow-2", result[0]["escrowId"]);

        await _mockDynamoDb.Received(1).ScanAsync(
            Arg.Is<ScanRequest>(r => r.TableName == TableName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecentAcrossEscrowsAsync_RespectsLimit()
    {
        var items = new List<Dictionary<string, AttributeValue>>
        {
            AuditItem("a1", "escrow-1", "2026-06-01T10:00:00Z"),
            AuditItem("a2", "escrow-1", "2026-06-02T10:00:00Z"),
            AuditItem("a3", "escrow-2", "2026-06-03T10:00:00Z")
        };
        _mockDynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = items });

        var result = await _repository.GetRecentAcrossEscrowsAsync(2);

        // Returns only the newest [limit] entries.
        Assert.Equal(2, result.Count);
        Assert.Equal("a3", result[0]["auditId"]);
        Assert.Equal("a2", result[1]["auditId"]);
    }

    [Fact]
    public async Task GetRecentAcrossEscrowsAsync_EmptyTable_ReturnsEmpty()
    {
        _mockDynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = [] });

        var result = await _repository.GetRecentAcrossEscrowsAsync(100);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AppendIfAbsentAsync_PutsConditionallyAndReportsAWrite()
    {
        PutItemRequest? put = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var written = await _repository.AppendIfAbsentAsync(new Dictionary<string, object>
        {
            { "escrowId", "escrow-1" }, { "timestamp", "2026-10-02T10:00:00.0000000Z" }, { "action", "manual_payout_paid" }
        });

        Assert.True(written);
        Assert.NotNull(put);
        Assert.Equal("attribute_not_exists(escrowId)", put!.ConditionExpression);
        Assert.Equal("2026-10-02T10:00:00.0000000Z", put.Item["timestamp"].S);
    }

    [Fact]
    public async Task AppendIfAbsentAsync_ExistingRow_ReturnsFalseAndDoesNotThrow()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ConditionalCheckFailedException("exists"));

        var written = await _repository.AppendIfAbsentAsync(new Dictionary<string, object>
        {
            { "escrowId", "escrow-1" }, { "timestamp", "2026-10-02T10:00:00.0000000Z" }, { "action", "refund" }
        });

        Assert.False(written);
    }
}

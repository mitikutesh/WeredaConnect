using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.RateLimiting;
using Xunit;

namespace WeredaConnect.Server.Tests.RateLimiting;

public class RateLimitRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly RateLimitRepository _repository;

    private const string TableName = "test-rate-limits";

    public RateLimitRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new RateLimitRepository(_mockDynamoDb, TableName);
    }

    // --- GetAsync ---

    [Fact]
    public async Task GetAsync_Found_ReturnsEntry()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "LOGIN#+251912345678" } },
            { "count", new AttributeValue { N = "2" } },
            { "lockedUntil", new AttributeValue { S = "2026-02-12T10:15:00Z" } },
            { "ttl", new AttributeValue { N = "1739356500" } }
        };
        _mockDynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = item });

        var entry = await _repository.GetAsync("LOGIN#+251912345678");

        Assert.NotNull(entry);
        Assert.Equal("LOGIN#+251912345678", entry.Pk);
        Assert.Equal(2, entry.Count);
        Assert.Equal("2026-02-12T10:15:00Z", entry.LockedUntil);
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        _mockDynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

        var entry = await _repository.GetAsync("LOGIN#+251999999999");

        Assert.Null(entry);
    }

    [Fact]
    public async Task GetAsync_NullItem_ReturnsNull()
    {
        _mockDynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = null });

        var entry = await _repository.GetAsync("LOGIN#+251999999999");

        Assert.Null(entry);
    }

    // --- IncrementAsync ---

    [Fact]
    public async Task IncrementAsync_ReturnsNewCount()
    {
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "pk", new AttributeValue { S = "LOGIN#+251912345678" } },
                    { "count", new AttributeValue { N = "3" } },
                    { "ttl", new AttributeValue { N = "1739356500" } }
                }
            });

        var count = await _repository.IncrementAsync("LOGIN#+251912345678", 900);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task IncrementAsync_UsesCorrectTableAndKey()
    {
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "pk", new AttributeValue { S = "OTP#+251912345678" } },
                    { "count", new AttributeValue { N = "1" } },
                    { "ttl", new AttributeValue { N = "1739356500" } }
                }
            });

        await _repository.IncrementAsync("OTP#+251912345678", 300);

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == TableName &&
                r.Key["pk"].S == "OTP#+251912345678" &&
                r.ReturnValues == ReturnValue.ALL_NEW),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncrementAsync_AnchorsTtlToFirstRequest()
    {
        // The TTL must be anchored with if_not_exists so the fixed window does not
        // slide forward on every increment (which would let the counter grow
        // without ever resetting).
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "pk", new AttributeValue { S = "SYNC#user-1" } },
                    { "count", new AttributeValue { N = "1" } },
                    { "ttl", new AttributeValue { N = "1739356500" } }
                }
            });

        await _repository.IncrementAsync("SYNC#user-1", 60);

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.UpdateExpression.Contains("if_not_exists(#ttl, :ttl)")),
            Arg.Any<CancellationToken>());
    }

    // --- SetLockAsync ---

    [Fact]
    public async Task SetLockAsync_SetsLockedUntilAndTtl()
    {
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var lockedUntil = "2026-02-12T10:15:00Z";
        await _repository.SetLockAsync("LOGIN#+251912345678", lockedUntil, 900);

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == TableName &&
                r.Key["pk"].S == "LOGIN#+251912345678" &&
                r.ExpressionAttributeValues[":lockedUntil"].S == lockedUntil),
            Arg.Any<CancellationToken>());
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_DeletesCorrectKey()
    {
        _mockDynamoDb.DeleteItemAsync(Arg.Any<DeleteItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteItemResponse());

        await _repository.DeleteAsync("LOGIN#+251912345678");

        await _mockDynamoDb.Received(1).DeleteItemAsync(
            Arg.Is<DeleteItemRequest>(r =>
                r.TableName == TableName &&
                r.Key["pk"].S == "LOGIN#+251912345678"),
            Arg.Any<CancellationToken>());
    }

    // --- FromItem ---

    [Fact]
    public void FromItem_MissingOptionalFields_DefaultsCorrectly()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "REGISTER#+251912345678#2026-02-12" } },
            { "count", new AttributeValue { N = "1" } },
            { "ttl", new AttributeValue { N = "1739356500" } }
        };

        var entry = RateLimitRepository.FromItem(item);

        Assert.Equal("REGISTER#+251912345678#2026-02-12", entry.Pk);
        Assert.Equal(1, entry.Count);
        Assert.Null(entry.LockedUntil);
    }

    [Fact]
    public void FromItem_MissingCount_DefaultsToZero()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = "LOGIN#+251912345678" } },
            { "ttl", new AttributeValue { N = "1739356500" } }
        };

        var entry = RateLimitRepository.FromItem(item);

        Assert.Equal(0, entry.Count);
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullDynamoDb_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new RateLimitRepository(null!, "table"));
    }

    [Fact]
    public void Constructor_NullTableName_ThrowsArgumentNull()
    {
        var mockDb = Substitute.For<IAmazonDynamoDB>();
        Assert.Throws<ArgumentNullException>(() => new RateLimitRepository(mockDb, null!));
    }
}

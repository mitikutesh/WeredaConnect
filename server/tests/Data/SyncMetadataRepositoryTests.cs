using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class SyncMetadataRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly SyncMetadataRepository _repository;

    private const string TableName = "test-sync-metadata";

    public SyncMetadataRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new SyncMetadataRepository(_mockDynamoDb, TableName);
    }

    // --- IdempotencyKeyExistsAsync ---

    [Fact]
    public async Task IdempotencyKeyExistsAsync_KeyExists_ReturnsTrue()
    {
        var idempotencyKey = "user:crop_listing:crop-001:create:12345";

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Count = 1 });

        var result = await _repository.IdempotencyKeyExistsAsync(idempotencyKey);

        Assert.True(result);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.TableName == TableName &&
                r.KeyConditionExpression == "idempotencyKey = :key" &&
                r.ExpressionAttributeValues[":key"].S == idempotencyKey &&
                r.Limit == 1 &&
                r.Select == Select.COUNT),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdempotencyKeyExistsAsync_KeyDoesNotExist_ReturnsFalse()
    {
        var idempotencyKey = "user:crop_listing:crop-002:create:67890";

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Count = 0 });

        var result = await _repository.IdempotencyKeyExistsAsync(idempotencyKey);

        Assert.False(result);
    }

    [Fact]
    public async Task IdempotencyKeyExistsAsync_UsesLimitOne()
    {
        var idempotencyKey = "user:crop_listing:crop-003:update:11111";

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Count = 0 });

        await _repository.IdempotencyKeyExistsAsync(idempotencyKey);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.Limit == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdempotencyKeyExistsAsync_UsesCOUNTSelect()
    {
        var idempotencyKey = "user:crop_listing:crop-004:delete:22222";

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Count = 0 });

        await _repository.IdempotencyKeyExistsAsync(idempotencyKey);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.Select == Select.COUNT),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdempotencyKeyExistsAsync_DynamoDbThrows_PropagatesException()
    {
        var idempotencyKey = "user:crop_listing:crop-005:create:33333";

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("DynamoDB error"));

        await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => _repository.IdempotencyKeyExistsAsync(idempotencyKey));
    }

    // --- StoreIdempotencyKeyAsync ---

    [Fact]
    public async Task StoreIdempotencyKeyAsync_StoresKeyWithCorrectAttributes()
    {
        var idempotencyKey = "user:crop_listing:crop-100:create:99999";
        var operationId = "op-100";
        var entityType = "crop_listing";
        var result = "success";

        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _repository.StoreIdempotencyKeyAsync(
            idempotencyKey,
            operationId,
            entityType,
            result);

        await _mockDynamoDb.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(r =>
                r.TableName == TableName &&
                r.Item["idempotencyKey"].S == idempotencyKey &&
                r.Item["operationId"].S == operationId &&
                r.Item["entityType"].S == entityType &&
                r.Item["result"].S == result),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreIdempotencyKeyAsync_SetsTimestamp()
    {
        var idempotencyKey = "user:crop_listing:crop-101:create:88888";
        var operationId = "op-101";
        var entityType = "crop_listing";
        var result = "success";

        PutItemRequest? capturedRequest = null;
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<PutItemRequest>();
                return new PutItemResponse();
            });

        await _repository.StoreIdempotencyKeyAsync(
            idempotencyKey,
            operationId,
            entityType,
            result);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Item.ContainsKey("timestamp"));
        Assert.NotNull(capturedRequest.Item["timestamp"].S);

        // Verify timestamp is in ISO 8601 format (contains 'T' and 'Z')
        Assert.Contains("T", capturedRequest.Item["timestamp"].S);
        Assert.Contains("Z", capturedRequest.Item["timestamp"].S);
    }

    [Fact]
    public async Task StoreIdempotencyKeyAsync_SetsTtlExpiry()
    {
        var idempotencyKey = "user:crop_listing:crop-102:create:77777";
        var operationId = "op-102";
        var entityType = "crop_listing";
        var result = "success";

        PutItemRequest? capturedRequest = null;
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<PutItemRequest>();
                return new PutItemResponse();
            });

        var beforeCall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _repository.StoreIdempotencyKeyAsync(
            idempotencyKey,
            operationId,
            entityType,
            result);

        var afterCall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Item.ContainsKey("expiresAt"));

        var expiresAtValue = long.Parse(capturedRequest.Item["expiresAt"].N);

        // TTL should be approximately 7 days from now (604800 seconds = 7 days)
        var sevenDaysInSeconds = 7 * 24 * 60 * 60;
        var expectedMinExpiry = beforeCall + sevenDaysInSeconds;
        var expectedMaxExpiry = afterCall + sevenDaysInSeconds;

        Assert.InRange(expiresAtValue, expectedMinExpiry, expectedMaxExpiry);
    }

    [Fact]
    public async Task StoreIdempotencyKeyAsync_Ttl7Days()
    {
        var idempotencyKey = "user:crop_listing:crop-103:create:66666";
        var operationId = "op-103";
        var entityType = "crop_listing";
        var result = "success";

        PutItemRequest? capturedRequest = null;
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRequest = callInfo.Arg<PutItemRequest>();
                return new PutItemResponse();
            });

        await _repository.StoreIdempotencyKeyAsync(
            idempotencyKey,
            operationId,
            entityType,
            result);

        Assert.NotNull(capturedRequest);
        var expiresAt = long.Parse(capturedRequest!.Item["expiresAt"].N);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Verify TTL is approximately 7 days (with 1 minute tolerance for test execution time)
        var sevenDays = 7 * 24 * 60 * 60;
        var tolerance = 60; // 1 minute
        Assert.InRange(expiresAt, now + sevenDays - tolerance, now + sevenDays + tolerance);
    }

    [Fact]
    public async Task StoreIdempotencyKeyAsync_DynamoDbThrows_PropagatesException()
    {
        var idempotencyKey = "user:crop_listing:crop-104:create:55555";
        var operationId = "op-104";
        var entityType = "crop_listing";
        var result = "error";

        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("Throughput exceeded"));

        await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
            () => _repository.StoreIdempotencyKeyAsync(
                idempotencyKey,
                operationId,
                entityType,
                result));
    }

    [Fact]
    public async Task StoreIdempotencyKeyAsync_StoresAllResultTypes()
    {
        var testCases = new[] { "success", "conflict", "error", "duplicate" };

        foreach (var resultType in testCases)
        {
            _mockDynamoDb.ClearReceivedCalls();
            _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
                .Returns(new PutItemResponse());

            await _repository.StoreIdempotencyKeyAsync(
                $"key-{resultType}",
                $"op-{resultType}",
                "crop_listing",
                resultType);

            await _mockDynamoDb.Received(1).PutItemAsync(
                Arg.Is<PutItemRequest>(r => r.Item["result"].S == resultType),
                Arg.Any<CancellationToken>());
        }
    }

    // --- Constructor ---

    [Fact]
    public void Constructor_NullDynamoDb_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SyncMetadataRepository(null!, "test-table"));
    }

    [Fact]
    public void Constructor_NullTableName_ThrowsArgumentNullException()
    {
        var mockDb = Substitute.For<IAmazonDynamoDB>();
        Assert.Throws<ArgumentNullException>(
            () => new SyncMetadataRepository(mockDb, null!));
    }
}

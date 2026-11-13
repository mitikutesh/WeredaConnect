using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class CropRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly CropRepository _repository;

    private const string TableName = "test-crops";

    public CropRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new CropRepository(_mockDynamoDb, TableName);
    }

    private static Dictionary<string, object> CreateTestCropData(string cropType = "teff") => new()
    {
        { "cropType", cropType },
        { "quantity", 100 },
        { "pricePerKg", 45.5 },
        { "farmerId", "user-001" },
        { "woreda", "Adama" },
        { "status", "active" }
    };

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_Success_ReturnsVersion1()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var data = CreateTestCropData();
        var result = await _repository.CreateAsync("crop-001", data);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("crop-001", result.Value["cropId"]);
        Assert.Equal("1", result.Value["version"]);
        Assert.Equal("teff", result.Value["cropType"]);

        await _mockDynamoDb.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(r =>
                r.TableName == TableName &&
                r.ConditionExpression == "attribute_not_exists(cropId)" &&
                r.Item["cropId"].S == "crop-001" &&
                r.Item["version"].N == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_Duplicate_Returns409Conflict()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("Item already exists"));

        var data = CreateTestCropData();
        var result = await _repository.CreateAsync("crop-001", data);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("SYNC_CONFLICT", result.Error.Code);
        Assert.Contains("already exists", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // SIM-39: createdAt is the sort key and is stamped fresh, so the put's
    // condition cannot see an existing row; the partition is checked first.
    [Fact]
    public async Task CreateAsync_ExistingCropId_Returns409WithoutWriting()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { { "cropId", new AttributeValue { S = "crop-001" } } }
                }
            });

        var result = await _repository.CreateAsync("crop-001", CreateTestCropData());

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("SYNC_CONFLICT", result.Error.Code);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(q => q.ConsistentRead == true && q.Limit == 1),
            Arg.Any<CancellationToken>());
        await _mockDynamoDb.DidNotReceive().PutItemAsync(
            Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_SetsTimestamps()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var data = CreateTestCropData();
        var result = await _repository.CreateAsync("crop-001", data);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.ContainsKey("createdAt"));
        Assert.True(result.Value.ContainsKey("updatedAt"));
    }

    [Fact]
    public async Task CreateAsync_DoesNotOverwriteReservedFields()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var data = new Dictionary<string, object>
        {
            { "cropType", "teff" },
            { "cropId", "malicious-id" },
            { "version", 999 },
            { "createdAt", "2020-01-01T00:00:00Z" },
            { "updatedAt", "2020-01-01T00:00:00Z" }
        };

        var result = await _repository.CreateAsync("crop-001", data);

        Assert.True(result.IsSuccess);
        Assert.Equal("crop-001", result.Value!["cropId"]); // Uses parameter, not data
        Assert.Equal("1", result.Value["version"]); // Always 1 for create
    }

    // --- UpdateAsync ---

    [Fact]
    public async Task UpdateAsync_Success_IncrementsVersion()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "2" } },
            { "cropType", new AttributeValue { S = "teff" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });

        var updatedItem = new Dictionary<string, AttributeValue>(existingItem)
        {
            ["version"] = new AttributeValue { N = "3" },
            ["quantity"] = new AttributeValue { N = "150" }
        };

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = updatedItem });

        var data = new Dictionary<string, object> { { "quantity", 150 } };
        var result = await _repository.UpdateAsync("crop-001", data, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal("3", result.Value!["version"]);
        Assert.Equal("150", result.Value["quantity"]);

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == TableName &&
                r.ConditionExpression == NotDeletedAtVersion &&
                r.ExpressionAttributeValues[":expectedVersion"].N == "2" &&
                r.ExpressionAttributeValues[":newVersion"].N == "3"),
            Arg.Any<CancellationToken>());
    }

    // --- FUP-152: never onto a deleted listing ---

    /// <summary>The update's condition: the version the client read, and not deleted (stamp or status).</summary>
    private const string NotDeletedAtVersion =
        "#v = :expectedVersion AND attribute_not_exists(#deleted) AND (attribute_not_exists(#st) OR #st <> :deletedStatus)";

    private static Dictionary<string, AttributeValue> LiveListing(int version) => new()
    {
        { "cropId", new AttributeValue { S = "crop-001" } },
        { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
        { "version", new AttributeValue { N = version.ToString() } },
        { "status", new AttributeValue { S = "available" } }
    };

    private static Dictionary<string, AttributeValue> DeletedListing(int version, bool stamped)
    {
        var item = LiveListing(version);
        item["status"] = new AttributeValue { S = "deleted" };
        if (stamped)
        {
            item["deletedAt"] = new AttributeValue { S = "2026-02-15T10:00:00Z" };
        }

        return item;
    }

    [Fact]
    public async Task UpdateAsync_TheConditionRefusesADeletedListing_AndAsksForTheRowItSaw()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [LiveListing(2)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = LiveListing(3) });

        await _repository.UpdateAsync("crop-001", new Dictionary<string, object> { { "pricePerKg", 50.0 } }, 2);

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ConditionExpression == NotDeletedAtVersion &&
                r.ExpressionAttributeNames["#deleted"] == "deletedAt" &&
                r.ExpressionAttributeNames["#st"] == "status" &&
                r.ExpressionAttributeValues[":deletedStatus"].S == "deleted" &&
                r.ReturnValuesOnConditionCheckFailure == ReturnValuesOnConditionCheckFailure.ALL_OLD &&
                !r.UpdateExpression.Contains("#deleted")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateAsync_DeletedWhenTheConditionRan_ReturnsAlreadyDeleted(bool stamped)
    {
        // The read saw a live listing at version 2; a delete landed before the write (version 3).
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [LiveListing(2)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("deleted") { Item = DeletedListing(3, stamped) });

        var result = await _repository.UpdateAsync(
            "crop-001", new Dictionary<string, object> { { "pricePerKg", 50.0 } }, 2);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal(CropRepository.AlreadyDeletedCode, result.Error.Code);
    }

    [Fact]
    public async Task UpdateAsync_ConditionFailsWithoutTheRow_TheReadDecides()
    {
        // No ALL_OLD item on the exception: the read before the write says deleted.
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [DeletedListing(4, stamped: true)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("deleted"));

        var result = await _repository.UpdateAsync(
            "crop-001", new Dictionary<string, object> { { "quantity", 3.0 } }, 4);

        Assert.Equal(CropRepository.AlreadyDeletedCode, result.Error!.Code);
    }

    [Fact]
    public async Task UpdateAsync_LiveListingAtAnotherVersion_IsStillAVersionConflict()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [LiveListing(5)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("version") { Item = LiveListing(5) });

        var result = await _repository.UpdateAsync(
            "crop-001", new Dictionary<string, object> { { "quantity", 3.0 } }, 2);

        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("SYNC_CONFLICT", result.Error.Code);
        Assert.Contains("Version conflict", result.Error.Message);
    }

    // --- SIM-13: stock restore ---

    private static Dictionary<string, AttributeValue> ListingItem(
        string quantity, string status, int version, bool numericQuantity = true, params string[] restoredFor)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } },
            { "status", new AttributeValue { S = status } },
            { "quantity", numericQuantity ? new AttributeValue { N = quantity } : new AttributeValue { S = quantity } }
        };
        if (restoredFor.Length > 0)
        {
            item["stockRestoredFor"] = new AttributeValue { SS = restoredFor.ToList() };
        }

        return item;
    }

    [Fact]
    public async Task TryRestoreStockAsync_AddsQuintalsFlipsReservedAndRecordsTheOrder()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("0", "reserved", 4)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var restored = await _repository.TryRestoreStockAsync("crop-001", "order-9", 5);

        Assert.True(restored);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "5.00"
                && r.ExpressionAttributeValues[":expectedVersion"].N == "4"
                && r.ExpressionAttributeValues[":orderIdSet"].SS.Contains("order-9")
                && r.ExpressionAttributeValues[":available"].S == "available"
                && r.ConditionExpression.Contains("NOT contains(#restored, :orderId)")
                && r.UpdateExpression.Contains("ADD #restored :orderIdSet")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRestoreStockAsync_AlreadyRestoredForThatOrder_IsANoOp()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("55", "available", 5, true, "order-9")] });

        var restored = await _repository.TryRestoreStockAsync("crop-001", "order-9", 5);

        Assert.False(restored);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRestoreStockAsync_ParsesTheOldStringQuantityAndLeavesStatusAlone()
    {
        // Listings decremented before SIM-13 hold quantity as a formatted string.
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("55.00", "available", 2, numericQuantity: false)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var restored = await _repository.TryRestoreStockAsync("crop-001", "order-9", 5);

        Assert.True(restored);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "60.00"
                && !r.ExpressionAttributeValues.ContainsKey(":available")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRestoreStockAsync_VersionConflict_ReReadsAndRetries()
    {
        // Race: another funding or restore moved the version between read and write.
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [ListingItem("10", "available", 2)] },
                new QueryResponse { Items = [ListingItem("8", "available", 3)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new ConditionalCheckFailedException("version moved"),
                _ => new UpdateItemResponse());

        var restored = await _repository.TryRestoreStockAsync("crop-001", "order-9", 5);

        Assert.True(restored);
        await _mockDynamoDb.Received(2).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "13.00"
                && r.ExpressionAttributeValues[":expectedVersion"].N == "3"),
            Arg.Any<CancellationToken>());
    }

    // --- ESC-01: idempotent stock decrement at funding ---

    [Fact]
    public async Task TryDecrementStockAsync_TakesQuintalsAndRecordsTheOrder()
    {
        // SIM-07: a 500 kg order (5 quintals) on a 60-quintal listing leaves 55.
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("60", "available", 1)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.Decremented, result);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "55.00"
                && r.ExpressionAttributeValues[":expectedVersion"].N == "1"
                && r.ExpressionAttributeValues[":orderIdSet"].SS.Contains("order-9")
                && !r.ExpressionAttributeValues.ContainsKey(":reserved")
                && r.ConditionExpression.Contains("NOT contains(#decremented, :orderId)")
                && r.UpdateExpression.Contains("ADD #decremented :orderIdSet")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryDecrementStockAsync_SellingOut_ReservesTheListing()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("5", "available", 3)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.Decremented, result);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "0.00"
                && r.ExpressionAttributeValues[":reserved"].S == "reserved"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryDecrementStockAsync_AlreadyTakenForThatOrder_IsANoOp()
    {
        // A payment redelivery repeats the funding side effects; the stock must not drop twice.
        var item = ListingItem("55", "available", 2);
        item["stockDecrementedFor"] = new AttributeValue { SS = ["order-9"] };
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [item] });

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.AlreadyApplied, result);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryDecrementStockAsync_VersionConflict_ReReadsAndRetries()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = [ListingItem("60", "available", 1)] },
                new QueryResponse { Items = [ListingItem("58", "available", 2)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new ConditionalCheckFailedException("version moved"),
                _ => new UpdateItemResponse());

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.Decremented, result);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeValues[":q"].N == "53.00"
                && r.ExpressionAttributeValues[":expectedVersion"].N == "2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryDecrementStockAsync_ListingMissing_ReturnsListingNotFound()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.ListingNotFound, result);
    }

    [Fact]
    public async Task TryDecrementStockAsync_KeepsConflicting_ReturnsFailed()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [ListingItem("60", "available", 1)] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("version moved"));

        var result = await _repository.TryDecrementStockAsync("crop-001", "order-9", 5);

        Assert.Equal(StockDecrementResult.Failed, result);
        await _mockDynamoDb.Received(4).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_VersionConflict_Returns409()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "5" } },
            { "cropType", new AttributeValue { S = "teff" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));

        var data = new Dictionary<string, object> { { "quantity", 150 } };
        var result = await _repository.UpdateAsync("crop-001", data, 2);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("SYNC_CONFLICT", result.Error.Code);
        Assert.Contains("Version conflict", result.Error.Message);
    }

    [Fact]
    public async Task UpdateAsync_NotFound_Returns404()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var data = new Dictionary<string, object> { { "quantity", 150 } };
        var result = await _repository.UpdateAsync("crop-nonexistent", data, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.Error!.HttpStatus);
        Assert.Equal("SYNC_VALIDATION", result.Error.Code);
        Assert.Contains("not found", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMultipleFields()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "1" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "cropId", new AttributeValue { S = "crop-001" } },
                    { "version", new AttributeValue { N = "2" } },
                    { "quantity", new AttributeValue { N = "200" } },
                    { "pricePerKg", new AttributeValue { N = "50.0" } },
                    { "status", new AttributeValue { S = "sold" } }
                }
            });

        var data = new Dictionary<string, object>
        {
            { "quantity", 200 },
            { "pricePerKg", 50.0 },
            { "status", "sold" }
        };

        var result = await _repository.UpdateAsync("crop-001", data, 1);

        Assert.True(result.IsSuccess);
        Assert.Equal("200", result.Value!["quantity"]);
        Assert.Equal("50.0", result.Value["pricePerKg"]);
        Assert.Equal("sold", result.Value["status"]);
    }

    // --- FUP-132: deleted by update, and rows deleted before the stamp was written ---

    [Fact]
    public async Task UpdateAsync_ToDeleted_StampsDeletedAtLikeADelete()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "2" } },
            { "status", new AttributeValue { S = "available" } }
        };
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = existingItem });

        var result = await _repository.UpdateAsync(
            "crop-001", new Dictionary<string, object> { { "status", "deleted" } }, 2);

        Assert.True(result.IsSuccess);
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ExpressionAttributeNames["#deleted"] == "deletedAt" &&
                r.UpdateExpression.Contains("#deleted = if_not_exists(#deleted, :updated)") &&
                r.ExpressionAttributeValues.ContainsKey(":updated") &&
                r.ConditionExpression == NotDeletedAtVersion),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("sold")]
    [InlineData("reserved")]
    [InlineData("available")]
    public async Task UpdateAsync_ToAnotherStatus_WritesNoDeletedAt(string status)
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "2" } }
        };
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = existingItem });

        await _repository.UpdateAsync(
            "crop-001", new Dictionary<string, object> { { "status", status } }, 2);

        // FUP-152: deletedAt is named for the condition, never set.
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                !r.UpdateExpression.Contains("#deleted") &&
                !r.UpdateExpression.Contains("deletedAt")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("deleted", null, true)]
    [InlineData("DELETED", null, true)]
    [InlineData("deleted", "2026-02-15T10:00:00Z", true)]
    [InlineData("available", "2026-02-15T10:00:00Z", true)]
    [InlineData("available", null, false)]
    [InlineData("sold", "", false)]
    [InlineData(null, null, false)]
    public void IsDeleted_ByStampOrByStatus(string? status, string? deletedAt, bool expected)
    {
        var crop = new Dictionary<string, object> { { "cropId", "crop-001" } };
        if (status != null)
        {
            crop["status"] = status;
        }

        if (deletedAt != null)
        {
            crop["deletedAt"] = deletedAt;
        }

        Assert.Equal(expected, CropRepository.IsDeleted(crop));
    }

    private static Dictionary<string, AttributeValue> ListRow(string cropId, string status) => new()
    {
        { "cropId", new AttributeValue { S = cropId } },
        { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
        { "farmerId", new AttributeValue { S = "user-001" } },
        { "cropType", new AttributeValue { S = "teff" } },
        { "status", new AttributeValue { S = status } }
    };

    /// <summary>One live row and one row with status 'deleted' and no deletedAt, on every read.</summary>
    private void ListsReturnALegacyDeletedRow()
    {
        var rows = new List<Dictionary<string, AttributeValue>>
        {
            ListRow("crop-live", "available"), ListRow("crop-legacy-deleted", "deleted")
        };
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse { Items = rows.ToList(), LastEvaluatedKey = [] });
        _mockDynamoDb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ScanResponse { Items = rows.ToList(), LastEvaluatedKey = [] });
        _mockDynamoDb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>
                {
                    { TableName, rows.ToList() }
                }
            });
    }

    private static void AssertOnlyTheLiveRow(List<Dictionary<string, object>> items)
    {
        var item = Assert.Single(items);
        Assert.Equal("crop-live", item["cropId"]);
    }

    [Fact]
    public async Task QueryCropsAsync_FarmerOwnList_DropsARowDeletedByStatusOnly()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync(farmerId: "user-001");

        AssertOnlyTheLiveRow(items);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.IndexName == "FarmerIdCreatedAtIndex"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryCropsAsync_FarmerAskingForDeleted_GetsNothing()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync(farmerId: "user-001", status: "deleted");

        Assert.Empty(items);
    }

    [Fact]
    public async Task QueryCropsAsync_BuyerBrowseByStatus_DropsARowDeletedByStatusOnly()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync(status: "available");

        AssertOnlyTheLiveRow(items);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.IndexName == "StatusCreatedAtIndex"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryCropsAsync_StatusDeletedPartition_NeverReturnsTheLegacyRow()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync(status: "deleted");

        Assert.DoesNotContain(items, i => (string)i["cropId"] == "crop-legacy-deleted");
    }

    [Fact]
    public async Task QueryCropsAsync_MatchingByCropType_DropsARowDeletedByStatusOnly()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync(cropType: "teff");

        AssertOnlyTheLiveRow(items);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.IndexName == "CropTypeCreatedAtIndex"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryCropsAsync_UnfilteredScan_DropsARowDeletedByStatusOnly()
    {
        ListsReturnALegacyDeletedRow();

        var (items, _) = await _repository.QueryCropsAsync();

        AssertOnlyTheLiveRow(items);
        await _mockDynamoDb.Received(1).ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    // --- DeleteAsync ---

    [Fact]
    public async Task DeleteAsync_Success_SoftDeletesAndIncrementsVersion()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "1" } },
            { "status", new AttributeValue { S = "active" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });

        var deletedItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "2" } },
            { "status", new AttributeValue { S = "deleted" } },
            { "deletedAt", new AttributeValue { S = "2026-02-15T10:00:00Z" } }
        };

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse { Attributes = deletedItem });

        var result = await _repository.DeleteAsync("crop-001", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value!["version"]);
        Assert.Equal("deleted", result.Value["status"]);
        Assert.True(result.Value.ContainsKey("deletedAt"));

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == TableName &&
                r.ConditionExpression == "#v = :expectedVersion"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NotFound_Returns404()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var result = await _repository.DeleteAsync("crop-nonexistent", 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.Error!.HttpStatus);
        Assert.Equal("SYNC_VALIDATION", result.Error.Code);
        Assert.Contains("not found", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_VersionConflict_Returns409()
    {
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "5" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [existingItem] });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));

        var result = await _repository.DeleteAsync("crop-001", 2);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        Assert.Equal("SYNC_CONFLICT", result.Error.Code);
        Assert.Contains("Version conflict", result.Error.Message);
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_Found_ReturnsData()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-001" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
            { "version", new AttributeValue { N = "1" } },
            { "cropType", new AttributeValue { S = "teff" } },
            { "quantity", new AttributeValue { N = "100" } },
            { "pricePerKg", new AttributeValue { N = "45.5" } },
            { "status", new AttributeValue { S = "active" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [item] });

        var result = await _repository.GetByIdAsync("crop-001");

        Assert.NotNull(result);
        Assert.Equal("crop-001", result["cropId"]);
        Assert.Equal("teff", result["cropType"]);
        Assert.Equal("100", result["quantity"]);
        Assert.Equal("45.5", result["pricePerKg"]);
        Assert.Equal("active", result["status"]);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.TableName == TableName &&
                r.KeyConditionExpression == "cropId = :id" &&
                r.Limit == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var result = await _repository.GetByIdAsync("crop-nonexistent");

        Assert.Null(result);
    }

    // --- Marshalling (ToAttributeValue / FromItem) ---

    [Fact]
    public void ToAttributeValue_String_ConvertsCorrectly()
    {
        var attr = CropRepository.ToAttributeValue("teff");
        Assert.Equal("teff", attr.S);
    }

    [Fact]
    public void ToAttributeValue_Int_ConvertsCorrectly()
    {
        var attr = CropRepository.ToAttributeValue(100);
        Assert.Equal("100", attr.N);
    }

    [Fact]
    public void ToAttributeValue_Double_ConvertsCorrectly()
    {
        var attr = CropRepository.ToAttributeValue(45.5);
        Assert.NotNull(attr.N);
        // Culture-invariant: 45.5 might be "45.5" or "45,5" depending on locale
        Assert.True(attr.N == "45.5" || attr.N == "45,5");
    }

    [Fact]
    public void ToAttributeValue_Boolean_ConvertsCorrectly()
    {
        var trueAttr = CropRepository.ToAttributeValue(true);
        var falseAttr = CropRepository.ToAttributeValue(false);

        Assert.True(trueAttr.BOOL);
        Assert.False(falseAttr.BOOL);
    }

    [Fact]
    public void ToAttributeValue_Null_ConvertsCorrectly()
    {
        var attr = CropRepository.ToAttributeValue(null!);
        Assert.True(attr.NULL);
    }

    [Fact]
    public void FromItem_ConvertsAllFieldTypes()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "stringField", new AttributeValue { S = "test" } },
            { "numberField", new AttributeValue { N = "123" } },
            { "boolField", new AttributeValue { BOOL = true } },
            { "nullField", new AttributeValue { NULL = true } }
        };

        var result = CropRepository.FromItem(item);

        Assert.Equal("test", result["stringField"]);
        Assert.Equal("123", result["numberField"]);
        Assert.Equal(true, result["boolField"]);
        Assert.False(result.ContainsKey("nullField")); // Nulls are skipped
    }

    [Fact]
    public void FromItem_ToItem_RoundTrip()
    {
        var original = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "teff" },
            { "quantity", 100 },
            { "pricePerKg", 45.5 },
            { "isOrganic", true }
        };

        // Convert to DynamoDB item
        var dynamoItem = new Dictionary<string, AttributeValue>();
        foreach (var kvp in original)
        {
            dynamoItem[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
        }

        // Convert back
        var restored = CropRepository.FromItem(dynamoItem);

        Assert.Equal("crop-001", restored["cropId"]);
        Assert.Equal("teff", restored["cropType"]);
        Assert.Equal("100", restored["quantity"]); // Numbers are stored as strings in DynamoDB
        // Culture-invariant: 45.5 might be "45.5" or "45,5" depending on locale
        Assert.True(restored["pricePerKg"].ToString() == "45.5" || restored["pricePerKg"].ToString() == "45,5");
        Assert.Equal(true, restored["isOrganic"]);
    }
}

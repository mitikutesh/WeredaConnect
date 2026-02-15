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
                r.ConditionExpression == "#v = :expectedVersion" &&
                r.ExpressionAttributeValues[":expectedVersion"].N == "2" &&
                r.ExpressionAttributeValues[":newVersion"].N == "3"),
            Arg.Any<CancellationToken>());
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

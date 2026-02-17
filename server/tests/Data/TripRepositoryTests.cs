using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class TripRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly TripRepository _repository;

    private const string TableName = "test-trips";

    public TripRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new TripRepository(_mockDynamoDb, TableName);
    }

    private static Dictionary<string, AttributeValue> CreateTripDynamoItem(
        string tripId = "trip-001",
        string status = "open")
    {
        return new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = tripId } },
            { "truckerId", new AttributeValue { S = "trucker-789" } },
            { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
            { "truckerPhone", new AttributeValue { S = "+251912345678" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "departureDate", new AttributeValue { S = "2026-02-18T06:00:00Z" } },
            { "capacity", new AttributeValue { N = "150" } },
            { "pricePerQuintal", new AttributeValue { N = "120" } },
            { "status", new AttributeValue { S = status } },
            { "matchedListings", new AttributeValue { S = "[]" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = "1" } }
        };
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_Found_ReturnsData()
    {
        var item = CreateTripDynamoItem("trip-001");

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [item] });

        var result = await _repository.GetByIdAsync("trip-001");

        Assert.NotNull(result);
        Assert.Equal("trip-001", result["tripId"]);
        Assert.Equal("Tadesse Worku", result["truckerName"]);
        Assert.Equal("Bishoftu", result["fromWoreda"]);
        Assert.Equal("Addis Ababa", result["toWoreda"]);
        Assert.Equal("150", result["capacity"]);
        Assert.Equal("120", result["pricePerQuintal"]);
        Assert.Equal("open", result["status"]);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.TableName == TableName &&
                r.KeyConditionExpression == "tripId = :id" &&
                r.Limit == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var result = await _repository.GetByIdAsync("trip-nonexistent");

        Assert.Null(result);
    }

    // --- QueryTripsAsync ---

    [Fact]
    public async Task QueryTripsAsync_NoFilters_QueriesWithDefaultStatusOpen()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem()],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (items, lastKey) = await _repository.QueryTripsAsync();

        Assert.Single(items);
        Assert.Equal("trip-001", items[0]["tripId"]);
        Assert.Null(lastKey);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.TableName == TableName &&
                r.IndexName == "StatusDepartureDateIndex" &&
                r.KeyConditionExpression == "#status = :status" &&
                r.ExpressionAttributeValues[":status"].S == "open" &&
                r.ScanIndexForward == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithStatus_UsesProvidedStatus()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(status: "matched");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.ExpressionAttributeValues[":status"].S == "matched"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithFromWoreda_AddsFilterExpression()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(fromWoreda: "Bishoftu");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.FilterExpression != null &&
                r.FilterExpression.Contains("#fromWoreda = :fromWoreda") &&
                r.ExpressionAttributeValues[":fromWoreda"].S == "Bishoftu"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithToWoreda_AddsFilterExpression()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(toWoreda: "Addis Ababa");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.FilterExpression != null &&
                r.FilterExpression.Contains("#toWoreda = :toWoreda") &&
                r.ExpressionAttributeValues[":toWoreda"].S == "Addis Ababa"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithBothWoredas_CombinesFilters()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(fromWoreda: "Bishoftu", toWoreda: "Addis Ababa");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.FilterExpression != null &&
                r.FilterExpression.Contains("#fromWoreda = :fromWoreda") &&
                r.FilterExpression.Contains("#toWoreda = :toWoreda") &&
                r.FilterExpression.Contains("AND")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_ExcludesDeletedItems()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync();

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.FilterExpression != null &&
                r.FilterExpression.Contains("attribute_not_exists(#deletedAt)")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithLimit_RespectsLimit()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(limit: 30);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.Limit == 30),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithInvalidLimit_DefaultsTo20()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(limit: 0);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.Limit == 20),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithPagination_PassesExclusiveStartKey()
    {
        var startKey = new Dictionary<string, AttributeValue>
        {
            { "status", new AttributeValue { S = "open" } },
            { "departureDate", new AttributeValue { S = "2026-02-18T06:00:00Z" } },
            { "tripId", new AttributeValue { S = "trip-last" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsAsync(exclusiveStartKey: startKey);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.ExclusiveStartKey != null &&
                r.ExclusiveStartKey.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_WithMoreResults_ReturnsLastEvaluatedKey()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            { "status", new AttributeValue { S = "open" } },
            { "departureDate", new AttributeValue { S = "2026-02-18T06:00:00Z" } },
            { "tripId", new AttributeValue { S = "trip-last" } }
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem()],
                LastEvaluatedKey = lastKey
            });

        var (items, resultKey) = await _repository.QueryTripsAsync();

        Assert.Single(items);
        Assert.NotNull(resultKey);
        Assert.Equal(3, resultKey.Count);
    }

    [Fact]
    public async Task QueryTripsAsync_WithNoMoreResults_ReturnsNullLastKey()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem()],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (items, resultKey) = await _repository.QueryTripsAsync();

        Assert.Single(items);
        Assert.Null(resultKey);
    }

    [Fact]
    public async Task QueryTripsAsync_MultipleResults_ReturnsAll()
    {
        var items = new List<Dictionary<string, AttributeValue>>
        {
            CreateTripDynamoItem("trip-001"),
            CreateTripDynamoItem("trip-002"),
            CreateTripDynamoItem("trip-003")
        };

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = items,
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (result, _) = await _repository.QueryTripsAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("trip-001", result[0]["tripId"]);
        Assert.Equal("trip-002", result[1]["tripId"]);
        Assert.Equal("trip-003", result[2]["tripId"]);
    }
}

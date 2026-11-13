using System.Globalization;
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
        string status = "open",
        string departureDate = "2026-02-18T06:00:00Z")
    {
        return new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = tripId } },
            { "truckerId", new AttributeValue { S = "trucker-789" } },
            { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
            { "truckerPhone", new AttributeValue { S = "+251912345678" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "departureDate", new AttributeValue { S = departureDate } },
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
                r.KeyConditionExpression == "#status = :status AND #departureDate >= :departureCutoff" &&
                r.ExpressionAttributeValues[":status"].S == "open" &&
                r.ScanIndexForward == true),
            Arg.Any<CancellationToken>());
    }

    // --- L-7: open trips whose Ethiopia day is over are not listed ---

    [Theory]
    [InlineData("2026-11-04T08:30:00Z", "2026-11-03T21:00:00.000Z")] // 11:30 in Addis Ababa
    [InlineData("2026-11-04T20:59:59Z", "2026-11-03T21:00:00.000Z")] // 23:59:59: today's trips stay
    [InlineData("2026-11-04T21:00:00Z", "2026-11-04T21:00:00.000Z")] // midnight: 4 Nov has left
    public async Task QueryTripsAsync_Open_KeyConditionStartsAtTodayInEthiopia(string now, string cutoff)
    {
        var repository = new TripRepository(_mockDynamoDb, TableName, new FixedClock(now));
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [], LastEvaluatedKey = new Dictionary<string, AttributeValue>() });

        await repository.QueryTripsAsync(fromWoreda: "Bishoftu");

        // On the sort key, not in the filter: departed trips are never read.
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.KeyConditionExpression == "#status = :status AND #departureDate >= :departureCutoff" &&
                r.ExpressionAttributeNames["#departureDate"] == "departureDate" &&
                r.ExpressionAttributeValues[":departureCutoff"].S == cutoff &&
                !r.FilterExpression.Contains("departureDate")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_Open_KeepsATripLeavingToday()
    {
        // A trip for 4 Nov is stored as Ethiopia midnight, 2026-11-03T21:00:00.000Z: the
        // cutoff at 23:00 in Addis Ababa is that same value, so >= keeps it.
        var repository = new TripRepository(_mockDynamoDb, TableName, new FixedClock("2026-11-04T20:00:00Z"));
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem(departureDate: "2026-11-03T21:00:00.000Z")],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (items, _) = await repository.QueryTripsAsync();

        Assert.Single(items);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.ExpressionAttributeValues[":departureCutoff"].S == "2026-11-03T21:00:00.000Z"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("matched")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    public async Task QueryTripsAsync_OtherStatuses_KeepEveryDeparture(string status)
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [], LastEvaluatedKey = new Dictionary<string, AttributeValue>() });

        await _repository.QueryTripsAsync(status: status);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.KeyConditionExpression == "#status = :status" &&
                !r.ExpressionAttributeValues.ContainsKey(":departureCutoff")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsAsync_PageTokenFromBeforeMidnight_StartsAtTheTokenAndDropsTheLeft()
    {
        // Page 1 was read on 4 Nov; page 2 comes after Ethiopia midnight. DynamoDB refuses a
        // start key outside the key condition, so the condition starts at the token and the
        // 4 Nov trips on the page are dropped.
        var repository = new TripRepository(_mockDynamoDb, TableName, new FixedClock("2026-11-04T21:05:00Z"));
        var startKey = new Dictionary<string, AttributeValue>
        {
            { "status", new AttributeValue { S = "open" } },
            { "departureDate", new AttributeValue { S = "2026-11-03T21:00:00.000Z" } },
            { "tripId", new AttributeValue { S = "trip-last" } }
        };
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items =
                [
                    CreateTripDynamoItem("trip-left", departureDate: "2026-11-03T21:00:00.000Z"),
                    CreateTripDynamoItem("trip-tomorrow", departureDate: "2026-11-05T21:00:00.000Z")
                ],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (items, _) = await repository.QueryTripsAsync(exclusiveStartKey: startKey);

        Assert.Single(items);
        Assert.Equal("trip-tomorrow", items[0]["tripId"]);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.ExclusiveStartKey == startKey &&
                r.ExpressionAttributeValues[":departureCutoff"].S == "2026-11-03T21:00:00.000Z"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsByTruckerAsync_KeepsTheTruckersPastTrips()
    {
        // The trucker's own My Trips list shows past trips (labelled on the phone).
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem(departureDate: "2026-07-14T21:00:00.000Z")],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var (items, _) = await _repository.QueryTripsByTruckerAsync("trucker-789", status: "open");

        Assert.Single(items);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.KeyConditionExpression == "#truckerId = :truckerId" &&
                !r.ExpressionAttributeValues.ContainsKey(":departureCutoff")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTripsByTruckerAsync_ReadsTheTruckersTripsNewestCreatedFirst()
    {
        // FUP-385 (trip-listing-api 2.9.0): the trucker's phone stops paging its own-trip pull at
        // a page that ends on an old delivered trip, which holds only because every later page
        // holds trips created before it.
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = [CreateTripDynamoItem()],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        await _repository.QueryTripsByTruckerAsync("trucker-789", limit: 50);

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "TruckerIdCreatedAtIndex" &&
                r.KeyConditionExpression == "#truckerId = :truckerId" &&
                r.ExpressionAttributeValues[":truckerId"].S == "trucker-789" &&
                r.ScanIndexForward == false &&
                r.Limit == 50),
            Arg.Any<CancellationToken>());
    }

    private sealed class FixedClock(string nowUtc) : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);

        public override DateTimeOffset GetUtcNow() => _now;
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

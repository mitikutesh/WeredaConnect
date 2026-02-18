using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class TripFunctionTests
{
    private readonly ITripRepository _mockTripRepo;
    private readonly TripFunction _function;
    private readonly TestLambdaContext _context;

    public TripFunctionTests()
    {
        _mockTripRepo = Substitute.For<ITripRepository>();
        _function = new TripFunction(_mockTripRepo);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path = "/trips",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams
        };
    }

    private static Dictionary<string, object> CreateTripItem(
        string tripId = "trip-001",
        string status = "open",
        string fromWoreda = "Bishoftu",
        string toWoreda = "Addis Ababa")
    {
        return new Dictionary<string, object>
        {
            { "tripId", tripId },
            { "truckerId", "trucker-789" },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251912345678" },
            { "fromWoreda", fromWoreda },
            { "toWoreda", toWoreda },
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", "150" },
            { "pricePerQuintal", "120" },
            { "status", status },
            { "matchedListings", "[]" },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "1" }
        };
    }

    // --- HandleGetTrips Tests ---

    [Fact]
    public async Task HandleGetTrips_Success_ReturnsEmptyList()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"trips\":[]", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_Success_ReturnsTripsList()
    {
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-001"),
            CreateTripItem("trip-002", "matched", "Debre Birhan", "Adama")
        };

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
        Assert.Contains("trip-002", response.Body);
        Assert.Contains("Bishoftu", response.Body);
        Assert.Contains("Debre Birhan", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithFromWoredaFilter_PassesToRepository()
    {
        _mockTripRepo.QueryTripsAsync(
            "Bishoftu",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "fromWoreda", "Bishoftu" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockTripRepo.Received(1).QueryTripsAsync(
            "Bishoftu",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_WithToWoredaFilter_PassesToRepository()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            "Addis Ababa",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "toWoreda", "Addis Ababa" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockTripRepo.Received(1).QueryTripsAsync(
            Arg.Any<string?>(),
            "Addis Ababa",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_WithStatusFilter_PassesToRepository()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "matched",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "status", "matched" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockTripRepo.Received(1).QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "matched",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_WithMultipleFilters_PassesAllToRepository()
    {
        _mockTripRepo.QueryTripsAsync("Bishoftu", "Addis Ababa", "open", 30, Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string>
        {
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "status", "open" },
            { "limit", "30" }
        };

        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockTripRepo.Received(1).QueryTripsAsync("Bishoftu", "Addis Ababa", "open", 30, Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_WithValidLimit_UsesProvidedLimit()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            50,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "limit", "50" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetTrips_WithNoLimit_UsesDefaultLimit20()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            20,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetTrips_WithInvalidLimit_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "abc" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Invalid limit", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetTrips_WithLimitTooLow_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "0" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithLimitTooHigh_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "51" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithMinCapacityFilter_FiltersResults()
    {
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-small"),
            CreateTripItem("trip-large")
        };
        // trip-small has capacity 150 (from CreateTripItem), trip-large has capacity 150
        // Override to different values
        trips[0]["capacity"] = "50";
        trips[1]["capacity"] = "200";

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var queryParams = new Dictionary<string, string> { { "minCapacity", "100" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-large", response.Body);
        Assert.DoesNotContain("trip-small", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithMaxPriceFilter_FiltersResults()
    {
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-cheap"),
            CreateTripItem("trip-expensive")
        };
        trips[0]["pricePerQuintal"] = "80";
        trips[1]["pricePerQuintal"] = "200";

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var queryParams = new Dictionary<string, string> { { "maxPrice", "100" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-cheap", response.Body);
        Assert.DoesNotContain("trip-expensive", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithInvalidMinCapacity_Returns400()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "minCapacity", "abc" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("minCapacity", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetTrips_WithInvalidMaxPrice_Returns400()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "maxPrice", "xyz" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("maxPrice", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetTrips_WithDepartureDateFilters_FiltersResults()
    {
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-early"),
            CreateTripItem("trip-mid"),
            CreateTripItem("trip-late")
        };
        trips[0]["departureDate"] = "2026-02-15T06:00:00Z";
        trips[1]["departureDate"] = "2026-02-18T06:00:00Z";
        trips[2]["departureDate"] = "2026-02-25T06:00:00Z";

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var queryParams = new Dictionary<string, string>
        {
            { "departureDateFrom", "2026-02-17T00:00:00Z" },
            { "departureDateTo", "2026-02-20T00:00:00Z" }
        };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-mid", response.Body);
        Assert.DoesNotContain("trip-early", response.Body);
        Assert.DoesNotContain("trip-late", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithNextToken_ReturnsNextPage()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = "trip-last" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } }
        };

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem() }, lastKey));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("nextToken", response.Body);
        Assert.DoesNotContain("\"nextToken\":null", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithInvalidNextToken_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "nextToken", "invalid-token-!!!" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Invalid or tampered nextToken", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_WithTamperedNextToken_Returns400()
    {
        // Craft a valid base64 payload but with a wrong signature
        var fakePayload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"tripId\":\"hacked\"}"));
        var tamperedToken = $"{fakePayload}.invalidSignature";
        var queryParams = new Dictionary<string, string> { { "nextToken", tamperedToken } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Invalid or tampered nextToken", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_RepositoryException_Returns500()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("TRIP_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to query", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // --- HandleGetTripById Tests ---

    [Fact]
    public async Task HandleGetTripById_Found_ReturnsTrip()
    {
        var trip = CreateTripItem("trip-001");
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
        Assert.Contains("Tadesse Worku", response.Body);
        Assert.Contains("Bishoftu", response.Body);
        Assert.Contains("\"trip\":", response.Body);
    }

    [Fact]
    public async Task HandleGetTripById_NotFound_Returns404()
    {
        _mockTripRepo.GetByIdAsync("trip-nonexistent").Returns((Dictionary<string, object>?)null);

        var pathParams = new Dictionary<string, string> { { "id", "trip-nonexistent" } };
        var request = MakeGetRequest("/trips/trip-nonexistent", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("TRIP_NOT_FOUND", response.Body);
        Assert.Contains("not found", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetTripById_CancelledTrip_Returns404()
    {
        var cancelledTrip = CreateTripItem("trip-001", "cancelled");
        cancelledTrip["deletedAt"] = "2026-02-17T00:00:00Z";

        _mockTripRepo.GetByIdAsync("trip-001").Returns(cancelledTrip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("TRIP_NOT_FOUND", response.Body);
        Assert.Contains("cancelled", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetTripById_MissingId_Returns400()
    {
        var pathParams = new Dictionary<string, string> { { "id", "" } };
        var request = MakeGetRequest("/trips/", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Missing trip ID", response.Body);
    }

    [Fact]
    public async Task HandleGetTripById_RepositoryException_Returns500()
    {
        _mockTripRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("TRIP_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to retrieve", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Method Not Allowed Tests ---

    [Fact]
    public async Task FunctionHandler_PostMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/trips"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Method not allowed", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_PutMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "PUT",
            Path = "/trips/trip-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DeleteMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "DELETE",
            Path = "/trips/trip-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
    }

    // --- Mapper Tests ---

    [Fact]
    public async Task MapToTripDto_HandlesAllFieldTypes()
    {
        var trip = new Dictionary<string, object>
        {
            { "tripId", "trip-001" },
            { "truckerId", "trucker-789" },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251912345678" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", "150" },
            { "pricePerQuintal", "120" },
            { "status", "open" },
            { "matchedListings", "[]" },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "1" }
        };

        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"tripId\":\"trip-001\"", response.Body);
        Assert.Contains("\"truckerName\":\"Tadesse Worku\"", response.Body);
        Assert.Contains("\"fromWoreda\":\"Bishoftu\"", response.Body);
        Assert.Contains("\"version\":1", response.Body);
    }

    [Fact]
    public async Task MapToTripDto_HandlesMissingOptionalFields()
    {
        var trip = new Dictionary<string, object>
        {
            { "tripId", "trip-001" },
            { "truckerId", "trucker-789" },
            { "truckerName", "Tadesse Worku" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", "150" },
            { "pricePerQuintal", "120" },
            { "status", "open" },
            { "matchedListings", "[]" },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "1" }
            // No truckerPhone, deletedAt
        };

        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"tripId\":\"trip-001\"", response.Body);
        Assert.Contains("\"status\":\"open\"", response.Body);
    }

    [Fact]
    public void MapToTripDto_WithMatchedListings_ParsesCorrectly()
    {
        var trip = new Dictionary<string, object>
        {
            { "tripId", "trip-001" },
            { "truckerId", "trucker-789" },
            { "truckerName", "Tadesse" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", "150" },
            { "pricePerQuintal", "120" },
            { "status", "matched" },
            { "matchedListings", "[\"crop-001\",\"crop-002\"]" },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "2" }
        };

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal("trip-001", dto.TripId);
        Assert.Equal(2, dto.MatchedListings.Count);
        Assert.Contains("crop-001", dto.MatchedListings);
        Assert.Contains("crop-002", dto.MatchedListings);
    }

    [Fact]
    public void MapToTripDto_WithEmptyMatchedListings_ReturnsEmptyList()
    {
        var trip = new Dictionary<string, object>
        {
            { "tripId", "trip-001" },
            { "truckerId", "trucker-789" },
            { "truckerName", "Tadesse" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", "150" },
            { "pricePerQuintal", "120" },
            { "status", "open" },
            { "matchedListings", "[]" },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "1" }
        };

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Empty(dto.MatchedListings);
    }

    // --- Security Headers Tests ---

    [Fact]
    public async Task FunctionHandler_IncludesSecurityHeaders()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Headers);
        Assert.True(response.Headers.ContainsKey("Content-Type"));
        Assert.Equal("application/json", response.Headers["Content-Type"]);
    }

    // --- Phone Number Visibility Tests (Contract: truckerPhone hidden for unauthenticated) ---

    [Fact]
    public async Task HandleGetTripById_Unauthenticated_HidesPhoneNumber()
    {
        var trip = CreateTripItem("trip-001");
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        // No Authorization header or authorizer context
        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
        // Phone should NOT be present for unauthenticated users
        Assert.DoesNotContain("+251912345678", response.Body);
    }

    [Fact]
    public void MapToTripDto_IncludePhone_ShowsPhoneNumber()
    {
        var trip = CreateTripItem("trip-001");

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal("+251912345678", dto.TruckerPhone);
    }

    [Fact]
    public void MapToTripDto_ExcludePhone_HidesPhoneNumber()
    {
        var trip = CreateTripItem("trip-001");

        var dto = TripFunction.MapToTripDto(trip, includePhone: false);

        Assert.Null(dto.TruckerPhone);
    }

    // --- Amharic/Multilingual Woreda Names ---

    [Fact]
    public void MapToTripDto_AmharicWoredaNames_PreservedCorrectly()
    {
        var trip = CreateTripItem("trip-amharic");
        trip["fromWoreda"] = "ቢሾፍቱ";
        trip["toWoreda"] = "አዲስ አበባ";
        trip["truckerName"] = "ታደሰ ወርቁ";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal("ቢሾፍቱ", dto.FromWoreda);
        Assert.Equal("አዲስ አበባ", dto.ToWoreda);
        Assert.Equal("ታደሰ ወርቁ", dto.TruckerName);
    }

    // --- Capacity Boundary Tests (Contract: 0.1-1000 quintals) ---

    [Fact]
    public void MapToTripDto_MinimumCapacity_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-min-cap");
        trip["capacity"] = "0.1";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(0.1, dto.Capacity, precision: 3);
    }

    [Fact]
    public void MapToTripDto_MaximumCapacity_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-max-cap");
        trip["capacity"] = "1000";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(1000.0, dto.Capacity);
    }

    [Fact]
    public void MapToTripDto_DecimalCapacity_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-dec-cap");
        trip["capacity"] = "150.75";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(150.75, dto.Capacity, precision: 2);
    }

    // --- Price Boundary Tests (Contract: 0.01-10000 ETB) ---

    [Fact]
    public void MapToTripDto_MinimumPrice_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-min-price");
        trip["pricePerQuintal"] = "0.01";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(0.01, dto.PricePerQuintal, precision: 3);
    }

    [Fact]
    public void MapToTripDto_MaximumPrice_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-max-price");
        trip["pricePerQuintal"] = "10000";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(10000.0, dto.PricePerQuintal);
    }

    // --- Status Value Tests (Contract: all valid status values) ---

    [Theory]
    [InlineData("open")]
    [InlineData("matched")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    public void MapToTripDto_AllStatuses_ParsedCorrectly(string status)
    {
        var trip = CreateTripItem("trip-status-test", status);

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(status, dto.Status);
    }

    // --- Version Handling Tests ---

    [Fact]
    public void MapToTripDto_VersionAsInteger_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-ver");
        trip["version"] = "5";

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(5, dto.Version);
    }

    [Fact]
    public void MapToTripDto_MissingVersion_DefaultsToZero()
    {
        var trip = CreateTripItem("trip-no-ver");
        trip.Remove("version");

        var dto = TripFunction.MapToTripDto(trip);

        Assert.Equal(0, dto.Version);
    }

    // --- Edge Case: Empty Query Params ---

    [Fact]
    public async Task HandleGetTrips_NullQueryParams_UsesDefaults()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            20,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/trips",
            QueryStringParameters = null
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    // --- Constructor Tests ---

    [Fact]
    public void Constructor_NullRepo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TripFunction(null!));
    }
}

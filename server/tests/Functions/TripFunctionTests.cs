using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
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

    // F-36: browse-all is restricted to Buyer/Admin, so the default browse caller
    // in these tests is an authenticated buyer (the legitimate marketplace browser).
    private static APIGatewayProxyRequest MakeGetRequest(
        string path = "/trips",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null)
    {
        return MakeAuthenticatedGetRequest("buyer-default", "buyer", path, queryParams, pathParams);
    }

    // Unauthenticated request (no authorizer context) — used for F-36 negative tests.
    private static APIGatewayProxyRequest MakeUnauthenticatedGetRequest(
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

    private static APIGatewayProxyRequest MakeAuthenticatedGetRequest(
        string userId,
        string role,
        string path = "/trips",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "sub", userId },
                    { "custom:role", role }
                }
            }
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

    // --- FUP-126: paging that never hands out a token to an empty last page (as SIM-26 for crops) ---

    private static Dictionary<string, AttributeValue> PageKey(string tripId) => new()
    {
        { "tripId", new AttributeValue { S = tripId } },
        { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } }
    };

    private static List<Dictionary<string, object>> Page(int count, string prefix = "trip")
        => Enumerable.Range(1, count).Select(i => CreateTripItem($"{prefix}-{i}")).ToList();

    /// <summary>The buyer browse read (StatusDepartureDateIndex) asked for exactly this.</summary>
    private void BrowseReturns(
        int limit,
        Dictionary<string, AttributeValue>? startKey,
        List<Dictionary<string, object>> items,
        Dictionary<string, AttributeValue>? lastKey)
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            limit,
            startKey)
            .Returns((items, lastKey));
    }

    private static string? NextTokenTripId(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("nextToken", out var token))
        {
            return null;
        }

        return PaginationTokenHelper.DecodeNextToken(token.GetString()!)?["tripId"].S;
    }

    private static List<string> TripIds(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("trips").EnumerateArray()
            .Select(t => t.GetProperty("tripId").GetString()!)
            .ToList();
    }

    private static Dictionary<string, string> Limit(int limit) => new() { { "limit", limit.ToString() } };

    [Fact]
    public async Task HandleGetTrips_FullPageWithNothingAfter_HasNoNextToken()
    {
        // The FUP-126 case: DynamoDB hands back a key after exactly `limit` trips even when
        // nothing follows. The one-trip look-ahead finds nothing, so no token is sent.
        var k1 = PageKey("trip-2");
        BrowseReturns(2, null, Page(2), k1);
        BrowseReturns(1, k1, new List<Dictionary<string, object>>(), null);

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: Limit(2)), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { "trip-1", "trip-2" }, TripIds(response.Body));
        Assert.Null(NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
        Assert.Contains("\"total\":2", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_FullPageWithATripAfter_TokenStartsAtTheLookAhead()
    {
        var k1 = PageKey("trip-2");
        var k2 = PageKey("next-1");
        BrowseReturns(2, null, Page(2), k1);
        BrowseReturns(1, k1, Page(1, "next"), k2);

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: Limit(2)), _context);

        Assert.Equal(200, response.StatusCode);
        // The look-ahead trip belongs to the next page, not this one.
        Assert.Equal(new[] { "trip-1", "trip-2" }, TripIds(response.Body));
        Assert.Equal("trip-2", NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_CancelledTripsDropped_PageIsFilledFromTheNextKey()
    {
        // SIM-31 drops a trip cancelled by a status update after DynamoDB counted it. The page
        // used to come back short with a token; now it reads on until it is full.
        var k1 = PageKey("trip-cancelled");
        BrowseReturns(3, null, new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-1"),
            CreateTripItem("trip-cancelled", "cancelled"),
            CreateTripItem("trip-2")
        }, k1);
        BrowseReturns(1, k1, Page(1, "more"), null);

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: Limit(3)), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { "trip-1", "trip-2", "more-1" }, TripIds(response.Body));
        Assert.Null(NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_QueryFilters_RunInsideThePageFill()
    {
        // minCapacity is applied in memory: a trip it drops does not leave the page short.
        var small = CreateTripItem("trip-small");
        small["capacity"] = "10";
        var k1 = PageKey("trip-small");
        BrowseReturns(2, null, new List<Dictionary<string, object>> { CreateTripItem("trip-1"), small }, k1);
        BrowseReturns(1, k1, Page(1, "more"), null);

        var queryParams = new Dictionary<string, string> { { "limit", "2" }, { "minCapacity", "100" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { "trip-1", "more-1" }, TripIds(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_LookAheadSkipsFilteredTrips()
    {
        // The first look-ahead read only a cancelled trip: the token skips it, and a trip that
        // passes the filters follows, so there is a next page.
        var k1 = PageKey("trip-2");
        var k2 = PageKey("trip-cancelled");
        var k3 = PageKey("next-1");
        BrowseReturns(2, null, Page(2), k1);
        BrowseReturns(1, k1, new List<Dictionary<string, object>> { CreateTripItem("trip-cancelled", "cancelled") }, k2);
        BrowseReturns(1, k2, Page(1, "next"), k3);

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: Limit(2)), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { "trip-1", "trip-2" }, TripIds(response.Body));
        Assert.Equal("trip-cancelled", NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_OnlyFilteredTripsAfterAFullPage_HasNoNextToken()
    {
        // Everything after the page is cancelled: nothing a client can see follows.
        var k1 = PageKey("trip-2");
        BrowseReturns(2, null, Page(2), k1);
        BrowseReturns(1, k1, new List<Dictionary<string, object>> { CreateTripItem("trip-cancelled", "cancelled") }, null);

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: Limit(2)), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, TripIds(response.Body).Count);
        Assert.Null(NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_ReadBudgetSpent_ReturnsTheKeyItReached()
    {
        // A long run of filtered-out trips: the reads are bounded and the page may be short,
        // but the token still points past what was read.
        var key = PageKey("cancelled-n");
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem("cancelled-n", "cancelled") }, key));

        var response = await _function.FunctionHandler(MakeGetRequest(), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(TripIds(response.Body));
        Assert.Equal("cancelled-n", NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
        await _mockTripRepo.Received(PageFill.MaxReads).QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_IncomingToken_IsTheFirstReadsStartKey()
    {
        var queryParams = new Dictionary<string, string>
        {
            { "limit", "1" },
            { "nextToken", PaginationTokenHelper.EncodeNextToken(PageKey("trip-start")) }
        };
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((Page(1), null));

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTripRepo.Received(1).QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            1,
            Arg.Is<Dictionary<string, AttributeValue>?>(k => k != null && k["tripId"].S == "trip-start"));
    }

    [Fact]
    public async Task HandleGetTrips_TruckerOwnList_IsFilledAndStaysOwnOnly()
    {
        // The trucker's own list pages the same way, and every read is scoped to the trucker:
        // the fill never widens the query to the browse index.
        var k1 = PageKey("trip-2");
        _mockTripRepo.QueryTripsByTruckerAsync("trucker-001", Arg.Any<string?>(), 2, null)
            .Returns((Page(2), k1));
        _mockTripRepo.QueryTripsByTruckerAsync("trucker-001", Arg.Any<string?>(), 1, k1)
            .Returns((new List<Dictionary<string, object>>(), null));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest("trucker-001", "trucker", queryParams: Limit(2)), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, TripIds(response.Body).Count);
        Assert.Null(NextTokenTripId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
        await _mockTripRepo.Received(2).QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockTripRepo.DidNotReceive().QueryTripsByTruckerAsync(
            Arg.Is<string>(id => id != "trucker-001"),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockTripRepo.DidNotReceive().QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_InvalidMinCapacity_Returns400WithoutReading()
    {
        var queryParams = new Dictionary<string, string> { { "minCapacity", "-1" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("minCapacity", response.Body, StringComparison.OrdinalIgnoreCase);
        await _mockTripRepo.DidNotReceive().QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
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

    [Theory]
    [InlineData("buyer-default", "buyer")]
    [InlineData("trucker-789", "trucker")]
    public async Task HandleGetTripById_OpenTripThatLeftDaysAgo_IsStillReturned(string userId, string role)
    {
        // L-7 hides a departed open trip from the browse only: its orders, the buyer's
        // live trip and the trucker's own screens still read it by id.
        var trip = CreateTripItem("trip-001");
        trip["departureDate"] = DateTime.UtcNow.AddDays(-10)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeAuthenticatedGetRequest(userId, role, "/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
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

    // F-SEC-03: Cancelled via status update (no deletedAt) should also return 404
    [Fact]
    public async Task HandleGetTripById_CancelledViaStatusOnly_Returns404()
    {
        // Trip cancelled via status update path — has status=cancelled but NO deletedAt
        var cancelledTrip = CreateTripItem("trip-001", "cancelled");
        // Intentionally no deletedAt key

        _mockTripRepo.GetByIdAsync("trip-001").Returns(cancelledTrip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("TRIP_NOT_FOUND", response.Body);
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

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

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

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

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

    // --- Phone Number Visibility Tests (ESC-48: truckerPhone only for the owner and admins) ---

    [Fact]
    public async Task HandleGetTripById_Unauthenticated_Forbidden()
    {
        // F-36: unauthenticated callers may NOT view trips (only Buyer/Admin + owning Trucker).
        var trip = CreateTripItem("trip-001");
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeUnauthenticatedGetRequest("/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
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

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

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

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(0.1, dto.Capacity, precision: 3);
    }

    [Fact]
    public void MapToTripDto_MaximumCapacity_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-max-cap");
        trip["capacity"] = "1000";

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(1000.0, dto.Capacity);
    }

    [Fact]
    public void MapToTripDto_DecimalCapacity_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-dec-cap");
        trip["capacity"] = "150.75";

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(150.75, dto.Capacity, precision: 2);
    }

    // --- Price Boundary Tests (Contract: 0.01-10000 ETB) ---

    [Fact]
    public void MapToTripDto_MinimumPrice_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-min-price");
        trip["pricePerQuintal"] = "0.01";

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(0.01, dto.PricePerQuintal, precision: 3);
    }

    [Fact]
    public void MapToTripDto_MaximumPrice_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-max-price");
        trip["pricePerQuintal"] = "10000";

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

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

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(status, dto.Status);
    }

    // --- Version Handling Tests ---

    [Fact]
    public void MapToTripDto_VersionAsInteger_ParsesCorrectly()
    {
        var trip = CreateTripItem("trip-ver");
        trip["version"] = "5";

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

        Assert.Equal(5, dto.Version);
    }

    [Fact]
    public void MapToTripDto_MissingVersion_DefaultsToZero()
    {
        var trip = CreateTripItem("trip-no-ver");
        trip.Remove("version");

        var dto = TripFunction.MapToTripDto(trip, includePhone: true);

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

        // F-36: browse requires an authorized buyer; null query params still apply defaults.
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    // --- IsAuthenticated Bypass Tests (F-11 Finding 4) ---

    [Fact]
    public async Task HandleGetTrips_FakeAuthorizationHeader_Forbidden()
    {
        // F-36: a fake/garbage bearer token is not a buyer/admin → forbidden (was: phone hidden).
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/trips",
            Headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer fake-garbage-token" }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);

        // A fake header must never reach the repository.
        await _mockTripRepo.DidNotReceive().QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTripById_FakeAuthorizationHeader_Forbidden()
    {
        var trip = CreateTripItem("trip-001");
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        // Fake Authorization header but no authorizer context → not buyer/admin → 403.
        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/trips/trip-001",
            PathParameters = pathParams,
            Headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer fake-garbage-token" }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_ValidBuyerClaims_BrowsesWithoutPhoneNumber()
    {
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-001")
        };

        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        // F-36: an authenticated buyer may browse all trips; ESC-48: without the trucker's phone,
        // so the two cannot agree to trade off the platform before any order.
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
        Assert.DoesNotContain("251912345678", response.Body);
        Assert.DoesNotContain("truckerPhone", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_OwnerTrucker_SeesOwnPhoneNumber()
    {
        _mockTripRepo.QueryTripsByTruckerAsync(
            "trucker-789",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem() }, null));

        var response = await _function.FunctionHandler(MakeAuthenticatedGetRequest("trucker-789", "trucker"), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("+251912345678", doc.RootElement.GetProperty("trips")[0].GetProperty("truckerPhone").GetString());
    }

    [Theory]
    [InlineData("buyer-001", "buyer", false)]
    [InlineData("trucker-789", "trucker", true)]
    [InlineData("admin-001", "admin", true)]
    public async Task HandleGetTripById_TruckerPhone_OnlyForTheOwnerAndAdmins(string userId, string role, bool expected)
    {
        _mockTripRepo.GetByIdAsync("trip-001").Returns(CreateTripItem("trip-001"));

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var response = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest(userId, role, "/trips/trip-001", pathParams: pathParams), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        var trip = doc.RootElement.GetProperty("trip");
        var phone = trip.TryGetProperty("truckerPhone", out var p) ? p.GetString() : null;
        Assert.Equal(expected ? "+251912345678" : null, phone);
    }

    [Fact]
    public async Task HandleGetTrips_Admin_SeesPhoneNumber()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem("trip-001") }, null));

        var response = await _function.FunctionHandler(MakeAuthenticatedGetRequest("admin-001", "admin"), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("+251912345678", doc.RootElement.GetProperty("trips")[0].GetProperty("truckerPhone").GetString());
    }

    // --- Role-Based Visibility Tests ---

    [Fact]
    public async Task HandleGetTrips_TruckerRole_ForcesTruckerIdToOwnUserId()
    {
        _mockTripRepo.QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem() }, null));

        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        // Verify the repository was called with the trucker's own ID
        await _mockTripRepo.Received(1).QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_TruckerRole_CannotQueryOtherTruckerTrips()
    {
        var queryParams = new Dictionary<string, string> { { "truckerId", "other-trucker" } };
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_TruckerRole_CanQueryOwnTripsExplicitly()
    {
        _mockTripRepo.QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "truckerId", "trucker-001" } };
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetTrips_BuyerRole_CanBrowseAllTrips()
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem() }, null));

        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetTrips_FarmerRole_Forbidden()
    {
        // F-36: farmers may NOT browse trips (only Buyer/Admin see all listings).
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);

        await _mockTripRepo.DidNotReceive().QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_Unauthenticated_Forbidden()
    {
        // F-36: unauthenticated callers may NOT browse all trips.
        var request = MakeUnauthenticatedGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetTripById_TruckerRole_CanViewOwnTrip()
    {
        var trip = CreateTripItem("trip-001");
        trip["truckerId"] = "trucker-001";
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker",
            "/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-001", response.Body);
    }

    [Fact]
    public async Task HandleGetTripById_TruckerRole_CannotViewOtherTruckerTrip()
    {
        var trip = CreateTripItem("trip-001");
        trip["truckerId"] = "other-trucker";
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker",
            "/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetTripById_BuyerRole_CanViewAnyTrip()
    {
        var trip = CreateTripItem("trip-001");
        trip["truckerId"] = "some-trucker";
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer",
            "/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetTripById_FarmerRole_Forbidden()
    {
        // F-36: farmers may NOT view trips (only Buyer/Admin + owning Trucker).
        var trip = CreateTripItem("trip-001");
        trip["truckerId"] = "some-trucker";
        _mockTripRepo.GetByIdAsync("trip-001").Returns(trip);

        var pathParams = new Dictionary<string, string> { { "id", "trip-001" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer",
            "/trips/trip-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    // --- SIM-31: cancelled/deleted trips are never listed (list and detail agree) ---

    [Fact]
    public async Task HandleGetTrips_TruckerRole_ExcludesCancelledTrips()
    {
        // A trip cancelled through a status update has status=cancelled but no deletedAt,
        // so the repository's deletedAt filter lets it through.
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-open", "open"),
            CreateTripItem("trip-cancelled", "cancelled"),
            CreateTripItem("trip-delivered", "delivered")
        };
        _mockTripRepo.QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-open", response.Body);
        Assert.Contains("trip-delivered", response.Body);
        Assert.DoesNotContain("trip-cancelled", response.Body);
        Assert.Contains("\"total\":2", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_BuyerRole_ExcludesCancelledAndDeletedTrips()
    {
        var deletedTrip = CreateTripItem("trip-deleted", "open");
        deletedTrip["deletedAt"] = "2026-02-17T00:00:00Z";
        var trips = new List<Dictionary<string, object>>
        {
            CreateTripItem("trip-open", "open"),
            CreateTripItem("trip-cancelled", "cancelled"),
            deletedTrip
        };
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, null));

        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("trip-open", response.Body);
        Assert.DoesNotContain("trip-cancelled", response.Body);
        Assert.DoesNotContain("trip-deleted", response.Body);
        Assert.Contains("\"total\":1", response.Body);
    }

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("trucker-001", "trucker")]
    public async Task HandleGetTrips_StatusCancelled_ReturnsEmptyWithoutQuery(string userId, string role)
    {
        var queryParams = new Dictionary<string, string> { { "status", "cancelled" } };
        var request = MakeAuthenticatedGetRequest(userId, role, queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"trips\":[]", response.Body);
        Assert.Contains("\"hasMore\":false", response.Body);
        Assert.Contains("\"total\":0", response.Body);

        await _mockTripRepo.DidNotReceive().QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockTripRepo.DidNotReceive().QueryTripsByTruckerAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetTrips_FarmerRole_StatusCancelled_StillForbidden()
    {
        var queryParams = new Dictionary<string, string> { { "status", "cancelled" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("TRIP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_StatusCancelled_InvalidLimit_StillReturns400()
    {
        var queryParams = new Dictionary<string, string>
        {
            { "status", "cancelled" },
            { "limit", "0" }
        };
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetTrips_StatusCancelled_InvalidMinCapacity_StillReturns400()
    {
        var queryParams = new Dictionary<string, string>
        {
            { "status", "cancelled" },
            { "minCapacity", "abc" }
        };
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("minCapacity", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("open", false, true)]
    [InlineData("delivered", false, true)]
    [InlineData("cancelled", false, false)]
    [InlineData("matched", true, false)]
    public async Task ListAndDetail_Agree_OnVisibility(string status, bool hasDeletedAt, bool expectedVisible)
    {
        var trip = CreateTripItem("trip-sim31", status);
        trip["truckerId"] = "trucker-001";
        if (hasDeletedAt)
        {
            trip["deletedAt"] = "2026-02-17T00:00:00Z";
        }

        _mockTripRepo.GetByIdAsync("trip-sim31").Returns(trip);
        _mockTripRepo.QueryTripsByTruckerAsync(
            "trucker-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { trip }, null));

        var listResponse = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest("trucker-001", "trucker"), _context);
        var detailResponse = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest("trucker-001", "trucker", "/trips/trip-sim31",
                pathParams: new Dictionary<string, string> { { "id", "trip-sim31" } }),
            _context);

        Assert.Equal(200, listResponse.StatusCode);
        var listContainsTrip = listResponse.Body.Contains("trip-sim31");
        Assert.Equal(detailResponse.StatusCode == 200, listContainsTrip);
        Assert.Equal(expectedVisible, listContainsTrip);
        Assert.Equal(expectedVisible ? 200 : 404, detailResponse.StatusCode);
    }

    [Theory]
    [InlineData("open", false, false)]
    [InlineData("delivered", false, false)]
    [InlineData("cancelled", false, true)]
    [InlineData("open", true, true)]
    public void IsCancelledOrDeleted_Cases(string status, bool hasDeletedAt, bool expected)
    {
        var trip = CreateTripItem("trip-001", status);
        if (hasDeletedAt)
        {
            trip["deletedAt"] = "2026-02-17T00:00:00Z";
        }

        Assert.Equal(expected, TripFunction.IsCancelledOrDeleted(trip));
    }

    // --- FUP-122: the order lead the browse reports ---

    [Theory]
    [InlineData(true, 24)]
    [InlineData(false, 0)]
    public async Task HandleGetTrips_ReportsTheOrderLead_FromTheCommitmentsFlag(bool enabled, int expectedHours)
    {
        _mockTripRepo.QueryTripsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateTripItem() }, null));
        var function = new TripFunction(_mockTripRepo) { CommitmentsEnabled = enabled };

        var response = await function.FunctionHandler(MakeGetRequest(), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal(expectedHours, doc.RootElement.GetProperty("orderLeadHours").GetInt32());
        Assert.Single(doc.RootElement.GetProperty("trips").EnumerateArray());
    }

    [Fact]
    public async Task HandleGetTrips_EmptyPage_StillReportsTheOrderLead()
    {
        var function = new TripFunction(_mockTripRepo) { CommitmentsEnabled = true };

        var response = await function.FunctionHandler(
            MakeGetRequest(queryParams: new Dictionary<string, string> { { "status", "cancelled" } }), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal(24, doc.RootElement.GetProperty("orderLeadHours").GetInt32());
    }

    // --- Constructor Tests ---

    [Fact]
    public void Constructor_NullRepo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TripFunction(null!));
    }
}

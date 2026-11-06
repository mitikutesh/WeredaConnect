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

public class CropFunctionTests
{
    private readonly ICropRepository _mockCropRepo;
    private readonly CropFunction _function;
    private readonly TestLambdaContext _context;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CropFunctionTests()
    {
        Environment.SetEnvironmentVariable("PAGINATION_SECRET", "test-pagination-secret-for-unit-tests");
        _mockCropRepo = Substitute.For<ICropRepository>();
        _function = new CropFunction(_mockCropRepo);
        _context = new TestLambdaContext();
    }

    // F-36: browse-all is restricted to Buyer/Admin, so the default browse caller
    // in these tests is an authenticated buyer (the legitimate marketplace browser).
    private static APIGatewayProxyRequest MakeGetRequest(
        string path = "/crops",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null)
    {
        return MakeAuthenticatedGetRequest("buyer-default", "buyer", path, queryParams, pathParams);
    }

    // Unauthenticated request (no authorizer context) — used for F-36 negative tests.
    private static APIGatewayProxyRequest MakeUnauthenticatedGetRequest(
        string path = "/crops",
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
        string path = "/crops",
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

    private static Dictionary<string, object> CreateCropItem(
        string cropId = "crop-001",
        string cropType = "teff",
        string status = "available")
    {
        return new Dictionary<string, object>
        {
            { "cropId", cropId },
            { "farmerId", "user-001" },
            { "cropType", cropType },
            { "woreda", "Adama" },
            { "quantity", "100" },
            { "pricePerKg", "45.5" },
            { "status", status },
            { "createdAt", "2026-01-01T00:00:00Z" },
            { "updatedAt", "2026-01-01T00:00:00Z" },
            { "version", "1" },
            { "farmerName", "Test Farmer" },
            { "description", "High quality teff" },
            { "location", "Adama City" }
        };
    }

    // --- HandleGetCrops Tests ---

    [Fact]
    public async Task HandleGetCrops_Success_ReturnsEmptyList()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"crops\":[]", response.Body);
        // JSON serializer may omit null values, so just check that crops is present
        Assert.DoesNotContain("\"nextToken\":\"", response.Body);
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_Success_ReturnsCropsList()
    {
        var crops = new List<Dictionary<string, object>>
        {
            CreateCropItem("crop-001", "teff"),
            CreateCropItem("crop-002", "wheat", "pending")
        };

        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((crops, null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("crop-001", response.Body);
        Assert.Contains("crop-002", response.Body);
        Assert.Contains("teff", response.Body);
        Assert.Contains("wheat", response.Body);
        Assert.Contains("\"farmerName\":\"Test Farmer\"", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_WithWoredaFilter_PassesToRepository()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            "Adama",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "woreda", "Adama" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            "Adama",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_WithCropTypeFilter_PassesToRepository()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "teff",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "cropType", "teff" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "teff",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_WithStatusFilter_PassesToRepository()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "available",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "status", "available" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "available",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_WithMultipleFilters_PassesAllToRepository()
    {
        _mockCropRepo.QueryCropsAsync(Arg.Any<string?>(), "Adama", "teff", "available", 50, Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string>
        {
            { "woreda", "Adama" },
            { "cropType", "teff" },
            { "status", "available" },
            { "limit", "50" }
        };

        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockCropRepo.Received(1).QueryCropsAsync(Arg.Any<string?>(), "Adama", "teff", "available", 50, Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_WithValidLimit_UsesProvidedLimit()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
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
    public async Task HandleGetCrops_WithNoLimit_UsesDefaultLimit20()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
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
    public async Task HandleGetCrops_WithInvalidLimit_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "abc" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
        Assert.Contains("Invalid limit", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetCrops_WithLimitTooLow_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "0" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
    }

    [Theory]
    [InlineData("51")]
    [InlineData("100")]
    [InlineData("101")]
    public async Task HandleGetCrops_WithLimitAbove50_ServedAs50(string limit)
    {
        // SIM-26: every list caps at 50. A larger value is clamped, not refused, because shipped
        // web builds ask for 100.
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "limit", limit } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            50,
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockCropRepo.DidNotReceive().QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<int>(n => n > 50),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_WithNextToken_ReturnsNextPage()
    {
        var lastKey = PageKey("crop-last");
        var startKey = PageKey("crop-start");
        var queryParams = new Dictionary<string, string>
        {
            { "limit", "1" },
            { "nextToken", PaginationTokenHelper.EncodeNextToken(startKey) }
        };
        // The page read and the look-ahead both find a listing.
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            1,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((Page(1), lastKey));

        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        // The incoming token is decoded and handed to the repository as the start key.
        await _mockCropRepo.Received().QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            1,
            Arg.Is<Dictionary<string, AttributeValue>?>(k => k != null && k["cropId"].S == "crop-start"));
        Assert.Equal("crop-last", NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
    }

    // --- SIM-26: paging that never hands out a token to an empty last page ---

    private static Dictionary<string, AttributeValue> PageKey(string cropId) => new()
    {
        { "cropId", new AttributeValue { S = cropId } },
        { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } }
    };

    private static List<Dictionary<string, object>> Page(int count, string prefix = "crop")
        => Enumerable.Range(1, count).Select(i => CreateCropItem($"{prefix}-{i}")).ToList();

    private void ReadReturns(
        int limit,
        Dictionary<string, AttributeValue>? startKey,
        List<Dictionary<string, object>> items,
        Dictionary<string, AttributeValue>? lastKey)
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            limit,
            startKey)
            .Returns((items, lastKey));
    }

    private static string? NextTokenCropId(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("nextToken", out var token))
        {
            return null;
        }

        return PaginationTokenHelper.DecodeNextToken(token.GetString()!)?["cropId"].S;
    }

    private static int CropCount(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("crops").GetArrayLength();
    }

    [Fact]
    public async Task HandleGetCrops_FullPageWithNothingAfter_HasNoNextToken()
    {
        // The SIM-26 case: DynamoDB hands back a key after exactly `limit` listings even when
        // nothing follows. The one-listing look-ahead finds nothing, so no token is sent.
        var k1 = PageKey("crop-2");
        ReadReturns(2, null, Page(2), k1);
        ReadReturns(1, k1, new List<Dictionary<string, object>>(), null);

        var queryParams = new Dictionary<string, string> { { "limit", "2" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, CropCount(response.Body));
        Assert.Null(NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_FullPageWithAListingAfter_TokenStartsAtTheLookAhead()
    {
        var k1 = PageKey("crop-2");
        var k2 = PageKey("crop-3");
        ReadReturns(2, null, Page(2), k1);
        ReadReturns(1, k1, Page(1, "next"), k2);

        var queryParams = new Dictionary<string, string> { { "limit", "2" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        // The look-ahead listing belongs to the next page, not this one.
        Assert.Equal(2, CropCount(response.Body));
        Assert.DoesNotContain("next-1", response.Body);
        Assert.Equal("crop-2", NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_ShortRead_IsFilledFromTheNextKey()
    {
        // Filters run after DynamoDB counts Limit, so one read can come back short.
        var k1 = PageKey("crop-1");
        ReadReturns(3, null, Page(1), k1);
        ReadReturns(2, k1, Page(2, "more"), null);

        var queryParams = new Dictionary<string, string> { { "limit", "3" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, CropCount(response.Body));
        Assert.Null(NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":false", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_LookAheadSkipsFilteredListings()
    {
        // The first look-ahead read only filtered-out listings: the token skips them.
        var k1 = PageKey("crop-2");
        var k2 = PageKey("deleted-1");
        var k3 = PageKey("crop-3");
        ReadReturns(2, null, Page(2), k1);
        ReadReturns(1, k1, new List<Dictionary<string, object>>(), k2);
        ReadReturns(1, k2, Page(1, "next"), k3);

        var queryParams = new Dictionary<string, string> { { "limit", "2" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, CropCount(response.Body));
        Assert.Equal("deleted-1", NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_ReadBudgetSpent_ReturnsTheKeyItReached()
    {
        // A long run of filtered-out listings: the reads are bounded and the page may be short,
        // but the token still points past what was read.
        var key = PageKey("deleted-n");
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), key));

        var response = await _function.FunctionHandler(MakeGetRequest(), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(0, CropCount(response.Body));
        Assert.Equal("deleted-n", NextTokenCropId(response.Body));
        Assert.Contains("\"hasMore\":true", response.Body);
        await _mockCropRepo.Received(CropFunction.MaxReadsPerPage).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // --- SIM-26: the default status filter ---

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("admin-001", "admin")]
    public async Task HandleGetCrops_BrowseWithoutStatus_ShowsAvailableListings(string userId, string role)
    {
        SetUpEmptyList();

        var response = await _function.FunctionHandler(MakeAuthenticatedGetRequest(userId, role), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "available",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_BrowseWithStatus_KeepsIt()
    {
        SetUpEmptyList();

        var queryParams = new Dictionary<string, string> { { "status", "reserved" } };
        var response = await _function.FunctionHandler(MakeGetRequest(queryParams: queryParams), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockCropRepo.Received(1).QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "reserved",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_FarmerWithoutStatus_ListsEveryStatusOfOwnListings()
    {
        // The farmer's home counts reserved listings, so their own list is not filtered.
        SetUpEmptyList();

        var response = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest("farmer-001", "farmer"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockCropRepo.Received(1).QueryCropsAsync(
            "farmer-001",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            null,
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    private void SetUpEmptyList()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));
    }

    [Fact]
    public async Task HandleGetCrops_WithInvalidNextToken_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "nextToken", "invalid-token-!!!" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
        Assert.Contains("Invalid or tampered nextToken", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_RepositoryException_Returns500()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("CROP_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to query", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // --- HandleGetCropById Tests ---

    [Fact]
    public async Task HandleGetCropById_Found_ReturnsCrop()
    {
        var crop = CreateCropItem("crop-001");
        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeGetRequest("/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("crop-001", response.Body);
        Assert.Contains("teff", response.Body);
        Assert.Contains("\"crop\":", response.Body);
        Assert.Contains("\"farmerName\":\"Test Farmer\"", response.Body);
    }

    [Fact]
    public async Task HandleGetCropById_NotFound_Returns404()
    {
        _mockCropRepo.GetByIdAsync("crop-nonexistent").Returns((Dictionary<string, object>?)null);

        var pathParams = new Dictionary<string, string> { { "id", "crop-nonexistent" } };
        var request = MakeGetRequest("/crops/crop-nonexistent", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("CROP_NOT_FOUND", response.Body);
        Assert.Contains("not found", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetCropById_DeletedCrop_Returns404()
    {
        var deletedCrop = CreateCropItem("crop-001");
        deletedCrop["status"] = "deleted";
        deletedCrop["deletedAt"] = "2026-02-01T00:00:00Z";

        _mockCropRepo.GetByIdAsync("crop-001").Returns(deletedCrop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeGetRequest("/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("CROP_NOT_FOUND", response.Body);
        Assert.Contains("deleted", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetCropById_MissingId_Returns400()
    {
        var pathParams = new Dictionary<string, string> { { "id", "" } };
        var request = MakeGetRequest("/crops/", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
        Assert.Contains("Missing crop ID", response.Body);
    }

    [Fact]
    public async Task HandleGetCropById_NoPathParameters_Returns500OrError()
    {
        // When PathParameters is null and we try to access "id", it will throw NullReferenceException
        // which gets caught and returns 500
        var request = MakeGetRequest("/crops/crop-001");
        var response = await _function.FunctionHandler(request, _context);

        // Should return either 400 or 500 depending on implementation
        Assert.True(response.StatusCode == 400 || response.StatusCode == 500);
    }

    [Fact]
    public async Task HandleGetCropById_RepositoryException_Returns500()
    {
        _mockCropRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeGetRequest("/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("CROP_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to retrieve", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Method Not Allowed Tests ---

    [Fact]
    public async Task FunctionHandler_PostMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/crops"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
        Assert.Contains("Method not allowed", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_PutMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "PUT",
            Path = "/crops/crop-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DeleteMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "DELETE",
            Path = "/crops/crop-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
    }

    // --- Mapper Tests ---

    [Fact]
    public async Task MapToCropDto_HandlesAllFieldTypes()
    {
        var crop = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "user-001" },
            { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" },
            { "woreda", "Adama" },
            { "quantity", "150" },
            { "pricePerKg", "50.75" },
            { "status", "available" },
            { "createdAt", "2026-01-01T00:00:00Z" },
            { "updatedAt", "2026-02-01T00:00:00Z" },
            { "version", "3" },
            { "description", "Organic teff" },
            { "location", "Farm 123" },
            { "latitude", "8.54" },
            { "longitude", "39.27" }
        };

        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeGetRequest("/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cropId\":\"crop-001\"", response.Body);
        Assert.Contains("\"farmerName\":\"Abebe Kebede\"", response.Body);
        Assert.Contains("\"quantityKg\":150", response.Body);
        Assert.Contains("\"version\":3", response.Body);
        // GPS coordinates are surfaced so the web client can score true
        // haversine distance (parity with the mobile on-device matcher).
        Assert.Contains("\"latitude\":8.54", response.Body);
        Assert.Contains("\"longitude\":39.27", response.Body);
    }

    [Fact]
    public async Task MapToCropDto_HandlesMissingOptionalFields()
    {
        var crop = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "user-001" },
            { "cropType", "teff" },
            { "woreda", "Adama" },
            { "quantity", "100" },
            { "pricePerKg", "45.5" },
            { "status", "available" },
            { "createdAt", "2026-01-01T00:00:00Z" },
            { "updatedAt", "2026-01-01T00:00:00Z" },
            { "version", "1" }
            // No description, location, deletedAt
        };

        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeGetRequest("/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // JSON serializer may omit null values, so just verify the required fields are present
        Assert.Contains("\"cropId\":\"crop-001\"", response.Body);
        Assert.Contains("\"status\":\"available\"", response.Body);
        // Ensure no description/location/deletedAt string values are present (empty strings would be bad)
        Assert.DoesNotContain("\"description\":\"\"", response.Body);
    }

    // --- Security Headers Tests ---

    [Fact]
    public async Task FunctionHandler_IncludesSecurityHeaders()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
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

    // --- Role-Based Visibility Tests ---

    [Fact]
    public async Task HandleGetCrops_FarmerRole_ForcesFarmerIdToOwnUserId()
    {
        _mockCropRepo.QueryCropsAsync(
            "farmer-001",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateCropItem() }, null));

        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        // Verify the repository was called with the farmer's own ID
        await _mockCropRepo.Received(1).QueryCropsAsync(
            "farmer-001",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_FarmerRole_CannotQueryOtherFarmerCrops()
    {
        var queryParams = new Dictionary<string, string> { { "farmerId", "other-farmer" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("CROP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_FarmerRole_CanQueryOwnCropsExplicitly()
    {
        _mockCropRepo.QueryCropsAsync(
            "farmer-001",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "farmerId", "farmer-001" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetCrops_BuyerRole_CanBrowseAllCrops()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateCropItem() }, null));

        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        // Verify no farmerId filter was forced (every farmer's available listings)
        await _mockCropRepo.Received(1).QueryCropsAsync(
            null,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "available",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_TruckerRole_Forbidden()
    {
        // F-36: truckers may NOT browse crops (only Buyer/Admin see all listings).
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("CROP_FORBIDDEN", response.Body);

        // The repository must never be queried for a forbidden role.
        await _mockCropRepo.DidNotReceive().QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_Unauthenticated_Forbidden()
    {
        // F-36: unauthenticated callers may NOT browse all crops.
        var request = MakeUnauthenticatedGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("CROP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetCropById_FarmerRole_CanViewOwnCrop()
    {
        var crop = CreateCropItem("crop-001");
        crop["farmerId"] = "farmer-001";
        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer",
            "/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("crop-001", response.Body);
    }

    [Fact]
    public async Task HandleGetCropById_FarmerRole_CannotViewOtherFarmerCrop()
    {
        var crop = CreateCropItem("crop-001");
        crop["farmerId"] = "other-farmer";
        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeAuthenticatedGetRequest("farmer-001", "farmer",
            "/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("CROP_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetCropById_BuyerRole_CanViewAnyCrop()
    {
        var crop = CreateCropItem("crop-001");
        crop["farmerId"] = "some-farmer";
        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeAuthenticatedGetRequest("buyer-001", "buyer",
            "/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetCropById_TruckerRole_Forbidden()
    {
        // F-36: truckers may NOT view crop listings (only Buyer/Admin + owning Farmer).
        var crop = CreateCropItem("crop-001");
        crop["farmerId"] = "some-farmer";
        _mockCropRepo.GetByIdAsync("crop-001").Returns(crop);

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker",
            "/crops/crop-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("CROP_FORBIDDEN", response.Body);
    }

    // --- ESC-48: the farmer's phone goes only to the listing's farmer and admins ---

    private const string FarmerPhone = "+251911000111";

    private void SetUpListOf(Dictionary<string, object> crop)
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { crop }, null));
    }

    private static Dictionary<string, object> CropWithPhone()
    {
        var crop = CreateCropItem("crop-001");
        crop["farmerPhone"] = FarmerPhone;
        return crop;
    }

    private static string? FarmerPhoneOf(string body, bool list)
    {
        using var doc = JsonDocument.Parse(body);
        var crop = list ? doc.RootElement.GetProperty("crops")[0] : doc.RootElement.GetProperty("crop");
        return crop.TryGetProperty("farmerPhone", out var phone) ? phone.GetString() : null;
    }

    [Theory]
    [InlineData("buyer-001", "buyer", false)]
    [InlineData("user-001", "farmer", true)]
    [InlineData("admin-001", "admin", true)]
    public async Task HandleGetCrops_FarmerPhone_OnlyForTheOwnerAndAdmins(string userId, string role, bool expected)
    {
        SetUpListOf(CropWithPhone());

        var response = await _function.FunctionHandler(MakeAuthenticatedGetRequest(userId, role), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected ? FarmerPhone : null, FarmerPhoneOf(response.Body, list: true));
    }

    [Theory]
    [InlineData("buyer-001", "buyer", false)]
    [InlineData("user-001", "farmer", true)]
    [InlineData("admin-001", "admin", true)]
    public async Task HandleGetCropById_FarmerPhone_OnlyForTheOwnerAndAdmins(string userId, string role, bool expected)
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(CropWithPhone());

        var pathParams = new Dictionary<string, string> { { "id", "crop-001" } };
        var response = await _function.FunctionHandler(
            MakeAuthenticatedGetRequest(userId, role, "/crops/crop-001", pathParams: pathParams), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected ? FarmerPhone : null, FarmerPhoneOf(response.Body, list: false));
        if (!expected)
        {
            Assert.DoesNotContain("911000111", response.Body);
        }
    }
}

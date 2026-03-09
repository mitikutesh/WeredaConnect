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

    private static APIGatewayProxyRequest MakeGetRequest(
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

    [Fact]
    public async Task HandleGetCrops_WithLimitTooHigh_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "101" } };
        var request = MakeGetRequest(queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("CROP_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetCrops_WithNextToken_ReturnsNextPage()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = "crop-last" } },
            { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } }
        };

        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateCropItem() }, lastKey));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("nextToken", response.Body);
        Assert.DoesNotContain("\"nextToken\":null", response.Body);
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
            { "location", "Farm 123" }
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

        // Verify no farmerId filter was forced
        await _mockCropRepo.Received(1).QueryCropsAsync(
            null,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_TruckerRole_CanBrowseAllCrops()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateCropItem() }, null));

        var request = MakeAuthenticatedGetRequest("trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        // Verify no farmerId filter was forced (truckers browse all crops)
        await _mockCropRepo.Received(1).QueryCropsAsync(
            null,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetCrops_Unauthenticated_CanBrowseAllCrops()
    {
        _mockCropRepo.QueryCropsAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateCropItem() }, null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
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
}

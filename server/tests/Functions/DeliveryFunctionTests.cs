using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

public class DeliveryFunctionTests
{
    private readonly IDeliveryRepository _mockDeliveryRepo;
    private readonly IAmazonS3 _mockS3;
    private readonly DeliveryFunction _function;
    private readonly DeliveryFunction _functionWithS3;
    private readonly TestLambdaContext _context;
    private const string TestBucket = "test-uploads-bucket";

    public DeliveryFunctionTests()
    {
        _mockDeliveryRepo = Substitute.For<IDeliveryRepository>();
        _mockS3 = Substitute.For<IAmazonS3>();
        _function = new DeliveryFunction(_mockDeliveryRepo);
        _functionWithS3 = new DeliveryFunction(_mockDeliveryRepo, _mockS3, TestBucket);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path = "/deliveries",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null,
        Dictionary<string, string>? headers = null,
        APIGatewayProxyRequest.ProxyRequestContext? requestContext = null)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            Headers = headers,
            RequestContext = requestContext
        };
    }

    private static APIGatewayProxyRequest MakeAuthenticatedRequest(
        string userId,
        string path = "/deliveries",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null,
        string? role = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static Dictionary<string, object> CreateDeliveryItem(
        string deliveryId = "del-001",
        string status = "pending",
        string farmerId = "farmer-123",
        string truckerId = "trucker-789",
        string buyerId = "buyer-456")
    {
        return new Dictionary<string, object>
        {
            { "deliveryId", deliveryId },
            { "tripId", "trip-001" },
            { "listingId", "listing-001" },
            { "farmerId", farmerId },
            { "farmerName", "Abebe Kebede" },
            { "farmerPhone", "+251911111111" },
            { "truckerId", truckerId },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251922222222" },
            { "buyerId", buyerId },
            { "buyerName", "Mekonnen Tadesse" },
            { "buyerPhone", "+251933333333" },
            { "qrToken", "qr-token-abc123" },
            { "status", status },
            { "createdAt", "2026-02-16T10:30:00Z" },
            { "updatedAt", "2026-02-16T10:30:00Z" },
            { "version", "1" }
        };
    }

    // --- HandleGetDeliveries Tests ---

    [Fact]
    public async Task HandleGetDeliveries_Unauthenticated_Returns401()
    {
        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        Assert.Contains("Authentication required", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_Authenticated_ReturnsEmptyList()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeAuthenticatedRequest("user-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"deliveries\":[]", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithFarmerId_MatchingCaller_QueriesByFarmer()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem(farmerId: "farmer-123")
        };
        _mockDeliveryRepo.QueryByFarmerAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries, null));

        var request = MakeAuthenticatedRequest(
            "farmer-123",
            queryParams: new Dictionary<string, string> { { "farmerId", "farmer-123" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        // Must route to the farmer GSI, NOT the pending-defaulting status query.
        await _mockDeliveryRepo.Received(1).QueryByFarmerAsync(
            "farmer-123",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockDeliveryRepo.DidNotReceive().QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // --- Default scoping by role (SIM-16) ---

    [Fact]
    public async Task HandleGetDeliveries_NoScope_FarmerClaim_QueriesByFarmer()
    {
        _mockDeliveryRepo.QueryByFarmerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateDeliveryItem(farmerId: "farmer-123") }, null));

        var request = MakeAuthenticatedRequest("farmer-123", role: "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        await _mockDeliveryRepo.Received(1).QueryByFarmerAsync("farmer-123", null, 20, null);
        await _mockDeliveryRepo.DidNotReceive().QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_NoScope_TruckerClaim_QueriesByTruckerWithStatus()
    {
        _mockDeliveryRepo.QueryByTruckerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateDeliveryItem(status: "inTransit") }, null));

        var request = MakeAuthenticatedRequest(
            "trucker-789",
            queryParams: new Dictionary<string, string> { { "status", "inTransit" } },
            role: "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        await _mockDeliveryRepo.Received(1).QueryByTruckerAsync("trucker-789", "inTransit", 20, null);
        await _mockDeliveryRepo.DidNotReceive().QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_NoScope_BuyerClaim_QueriesParticipantWithStatus()
    {
        // Regression: the buyer's plain read hit the pending-status query and missed
        // her own in-transit delivery; it now reads the ParticipantIndex (buyerId).
        _mockDeliveryRepo.QueryByParticipantAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(),
            Arg.Any<string?>())
            .Returns((new List<Dictionary<string, object>> { CreateDeliveryItem(status: "inTransit") }, null));

        var request = MakeAuthenticatedRequest(
            "buyer-456",
            queryParams: new Dictionary<string, string> { { "status", "inTransit" } },
            role: "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        await _mockDeliveryRepo.Received(1).QueryByParticipantAsync("buyer-456", 20, null, "inTransit");
        await _mockDeliveryRepo.DidNotReceive().QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_NoScope_NoRoleClaim_KeepsStatusQuery()
    {
        // An access token carries no role: the legacy status query stays, still
        // post-filtered to the caller's own rows.
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeAuthenticatedRequest("user-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockDeliveryRepo.Received(1).QueryDeliveriesAsync(null, null, null, 20, null);
        await _mockDeliveryRepo.DidNotReceive().QueryByParticipantAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_TripIdWithFarmerClaim_KeepsTripQuery()
    {
        // An entity filter is not overridden by the role default.
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeAuthenticatedRequest(
            "farmer-123",
            queryParams: new Dictionary<string, string> { { "tripId", "trip-001" } },
            role: "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockDeliveryRepo.Received(1).QueryDeliveriesAsync(null, "trip-001", null, 20, null);
        await _mockDeliveryRepo.DidNotReceive().QueryByFarmerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_RoleParam_OverridesClaim_ScopedToCaller()
    {
        // ?role= picks the index, but the key is always the caller's own id.
        _mockDeliveryRepo.QueryByTruckerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeAuthenticatedRequest(
            "user-009",
            queryParams: new Dictionary<string, string> { { "role", "trucker" } },
            role: "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockDeliveryRepo.Received(1).QueryByTruckerAsync("user-009", null, 20, null);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithTruckerId_MatchingCaller_QueriesByTrucker()
    {
        _mockDeliveryRepo.QueryByTruckerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateDeliveryItem() }, null));

        var request = MakeAuthenticatedRequest(
            "trucker-789",
            queryParams: new Dictionary<string, string> { { "truckerId", "trucker-789" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        await _mockDeliveryRepo.Received(1).QueryByTruckerAsync("trucker-789", null, 20, null);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithTruckerId_NotCaller_Returns403()
    {
        var request = MakeAuthenticatedRequest(
            "trucker-789",
            queryParams: new Dictionary<string, string> { { "truckerId", "someone-else" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await _mockDeliveryRepo.DidNotReceive().QueryByTruckerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_WithFarmerId_NotCaller_Returns403()
    {
        // A farmer must only be able to query their OWN deliveries.
        var request = MakeAuthenticatedRequest(
            "farmer-123",
            queryParams: new Dictionary<string, string> { { "farmerId", "someone-else" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await _mockDeliveryRepo.DidNotReceive().QueryByFarmerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_Authenticated_ReturnsDeliveriesList()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem("del-001"),
            CreateDeliveryItem("del-002", "pickedUp")
        };
        deliveries[0]["orderId"] = "order-9"; // SIM-14: exposed so clients can tie a delivery to its order

        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries, null));

        // Must be a participant to see results (farmer-123 is default farmerId)
        var request = MakeAuthenticatedRequest("farmer-123");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        Assert.Contains("del-002", response.Body);
        Assert.Contains("\"orderId\":\"order-9\"", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_NonParticipant_ReturnsEmptyList()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem("del-001")
        };

        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries, null));

        // Non-participant should see empty results
        var request = MakeAuthenticatedRequest("random-user-999");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"deliveries\":[]", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_QrTokenHiddenInListView()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem("del-001")
        };

        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries, null));

        var request = MakeAuthenticatedRequest("farmer-123");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // QR token should be hidden in list view
        Assert.DoesNotContain("qr-token-abc123", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithStatusFilter_PassesToRepository()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            "pickedUp",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "status", "pickedUp" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockDeliveryRepo.Received(1).QueryDeliveriesAsync(
            "pickedUp",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_WithTripIdFilter_PassesToRepository()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            "trip-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "tripId", "trip-001" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockDeliveryRepo.Received(1).QueryDeliveriesAsync(
            Arg.Any<string?>(),
            "trip-001",
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_WithListingIdFilter_PassesToRepository()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "listing-001",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "listingId", "listing-001" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);

        await _mockDeliveryRepo.Received(1).QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            "listing-001",
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task HandleGetDeliveries_WithValidLimit_UsesProvidedLimit()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            30,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var queryParams = new Dictionary<string, string> { { "limit", "30" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithInvalidLimit_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "abc" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Invalid limit", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithLimitTooLow_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "0" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithLimitTooHigh_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "limit", "51" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithNextToken_ReturnsNextPage()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-last" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } }
        };

        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateDeliveryItem() }, lastKey));

        // Must be participant to see delivery in results
        var request = MakeAuthenticatedRequest("farmer-123");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("nextToken", response.Body);
        Assert.DoesNotContain("\"nextToken\":null", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithInvalidNextToken_Returns400()
    {
        var queryParams = new Dictionary<string, string> { { "nextToken", "invalid-token-!!!" } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Invalid or tampered nextToken", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_WithTamperedNextToken_Returns400()
    {
        // Craft a valid base64 payload but with a wrong signature
        var fakePayload = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"deliveryId\":\"hacked\"}"));
        var tamperedToken = $"{fakePayload}.invalidSignature";
        var queryParams = new Dictionary<string, string> { { "nextToken", tamperedToken } };
        var request = MakeAuthenticatedRequest("user-001", queryParams: queryParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Invalid or tampered nextToken", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveries_RepositoryException_Returns500()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var request = MakeAuthenticatedRequest("user-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DELIVERY_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to query", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetDeliveries_PhoneNumbersHiddenInListView()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem("del-001")
        };

        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries, null));

        // Must be participant to see results, but phones still hidden in list view
        var request = MakeAuthenticatedRequest("farmer-123");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // Phone numbers should be hidden in list view (check number without + since JSON escapes it)
        Assert.DoesNotContain("251911111111", response.Body);
        Assert.DoesNotContain("251922222222", response.Body);
        Assert.DoesNotContain("251933333333", response.Body);
    }

    // --- HandleGetDeliveryById Tests ---

    [Fact]
    public async Task HandleGetDeliveryById_Found_ReturnsDelivery()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("trucker-789", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        Assert.Contains("Abebe Kebede", response.Body);
        Assert.Contains("\"delivery\":", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_NotFound_Returns404()
    {
        _mockDeliveryRepo.GetByIdAsync("del-nonexistent").Returns((Dictionary<string, object>?)null);

        var pathParams = new Dictionary<string, string> { { "id", "del-nonexistent" } };
        var request = MakeAuthenticatedRequest("user-001", "/deliveries/del-nonexistent", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("DELIVERY_NOT_FOUND", response.Body);
        Assert.Contains("not found", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // ESC-08: a party's cancel writes no deletedAt, so every party still reads the cancelled row.
    [Theory]
    [InlineData("farmer-123", 200)]
    [InlineData("trucker-789", 200)]
    [InlineData("buyer-456", 200)]
    [InlineData("outsider-001", 403)]
    public async Task HandleGetDeliveryById_ClientCancelledDelivery_ReturnedToEveryPartyOnly(string userId, int expected)
    {
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(CreateDeliveryItem("del-001", "cancelled"));

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest(userId, "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(expected, response.StatusCode);
        if (expected == 200)
        {
            Assert.Contains("\"status\":\"cancelled\"", response.Body);
        }
        else
        {
            Assert.DoesNotContain("Abebe Kebede", response.Body);
        }
    }

    [Fact]
    public async Task HandleGetDeliveryById_LegacySoftDeletedDelivery_Returns404()
    {
        var cancelledDelivery = CreateDeliveryItem("del-001", "cancelled");
        cancelledDelivery["deletedAt"] = "2026-02-17T00:00:00Z";

        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(cancelledDelivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("farmer-123", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("DELIVERY_NOT_FOUND", response.Body);
        Assert.Contains("cancelled", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleGetDeliveryById_MissingId_Returns400()
    {
        var pathParams = new Dictionary<string, string> { { "id", "" } };
        var request = MakeAuthenticatedRequest("user-001", "/deliveries/", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Missing delivery ID", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_RepositoryException_Returns500()
    {
        _mockDeliveryRepo.GetByIdAsync(Arg.Any<string>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("user-001", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DELIVERY_SERVER_ERROR", response.Body);
        Assert.Contains("Failed to retrieve", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Participant Access Tests ---

    // ESC-48: no party gets a phone number from the delivery; contact comes from the order,
    // only while its contact is open. Admins still see them in the transaction view.
    [Theory]
    [InlineData("farmer-123")]
    [InlineData("trucker-789")]
    [InlineData("buyer-456")]
    public async Task HandleGetDeliveryById_Participant_GetsTheDeliveryWithoutPhones(string userId)
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest(userId, "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("del-001", response.Body);
        Assert.DoesNotContain("farmerPhone", response.Body);
        Assert.DoesNotContain("truckerPhone", response.Body);
        Assert.DoesNotContain("buyerPhone", response.Body);
        Assert.DoesNotContain("251911111111", response.Body);
        Assert.DoesNotContain("251922222222", response.Body);
        Assert.DoesNotContain("251933333333", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_NonParticipant_Returns403()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("random-user-999", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        Assert.Contains("You are not a participant in this delivery", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_Unauthenticated_Returns401()
    {
        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeGetRequest("/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        Assert.Contains("Authentication required", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_Participant_ShowsQrToken()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("trucker-789", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("qr-token-abc123", response.Body);
    }

    // --- GPS Fields Tests ---

    [Fact]
    public void MapToDeliveryDto_WithGpsPickup_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001");
        delivery["gpsPickup"] = new Dictionary<string, object>
        {
            { "latitude", "8.9806" },
            { "longitude", "38.7578" }
        };

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.NotNull(dto.GpsPickup);
        Assert.Equal(8.9806, dto.GpsPickup.Latitude, precision: 4);
        Assert.Equal(38.7578, dto.GpsPickup.Longitude, precision: 4);
    }

    [Fact]
    public void MapToDeliveryDto_WithGpsDropoff_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001");
        delivery["gpsDropoff"] = new Dictionary<string, object>
        {
            { "latitude", "9.0192" },
            { "longitude", "38.7525" }
        };

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.NotNull(dto.GpsDropoff);
        Assert.Equal(9.0192, dto.GpsDropoff.Latitude, precision: 4);
        Assert.Equal(38.7525, dto.GpsDropoff.Longitude, precision: 4);
    }

    [Fact]
    public void MapToDeliveryDto_WithBothGpsFields_ParsesBoth()
    {
        var delivery = CreateDeliveryItem("del-001");
        delivery["gpsPickup"] = new Dictionary<string, object>
        {
            { "latitude", "8.9806" },
            { "longitude", "38.7578" }
        };
        delivery["gpsDropoff"] = new Dictionary<string, object>
        {
            { "latitude", "9.0192" },
            { "longitude", "38.7525" }
        };

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.NotNull(dto.GpsPickup);
        Assert.NotNull(dto.GpsDropoff);
    }

    [Fact]
    public void MapToDeliveryDto_WithoutGpsFields_ReturnsNull()
    {
        var delivery = CreateDeliveryItem("del-001");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Null(dto.GpsPickup);
        Assert.Null(dto.GpsDropoff);
    }

    // --- Status Value Tests ---

    [Theory]
    [InlineData("pending")]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public void MapToDeliveryDto_AllStatuses_ParsedCorrectly(string status)
    {
        var delivery = CreateDeliveryItem("del-status-test", status);

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal(status, dto.Status);
    }

    // --- Optional Fields Tests ---

    [Fact]
    public void MapToDeliveryDto_WithOptionalTimestamps_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001", "delivered");
        delivery["pickedUpAt"] = "2026-02-17T08:00:00Z";
        delivery["confirmedAt"] = "2026-02-17T16:00:00Z";

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal("2026-02-17T08:00:00Z", dto.PickedUpAt);
        Assert.Equal("2026-02-17T16:00:00Z", dto.ConfirmedAt);
    }

    [Fact]
    public void MapToDeliveryDto_WithPhotoRefs_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001");
        delivery["pickupPhotoRef"] = "s3://bucket/pickup-photo.jpg";
        delivery["dropoffPhotoRef"] = "s3://bucket/dropoff-photo.jpg";

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal("s3://bucket/pickup-photo.jpg", dto.PickupPhotoRef);
        Assert.Equal("s3://bucket/dropoff-photo.jpg", dto.DropoffPhotoRef);
    }

    [Fact]
    public void MapToDeliveryDto_WithDisputeReason_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001", "disputed");
        delivery["disputeReason"] = "Cargo damaged during transport";

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal("Cargo damaged during transport", dto.DisputeReason);
    }

    [Fact]
    public void MapToDeliveryDto_WithoutOptionalFields_ReturnsNull()
    {
        var delivery = CreateDeliveryItem("del-001");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Null(dto.PickedUpAt);
        Assert.Null(dto.ConfirmedAt);
        Assert.Null(dto.PickupPhotoRef);
        Assert.Null(dto.DropoffPhotoRef);
        Assert.Null(dto.DisputeReason);
        Assert.Null(dto.DeletedAt);
    }

    // --- Amharic Names Tests ---

    [Fact]
    public void MapToDeliveryDto_AmharicNames_PreservedCorrectly()
    {
        var delivery = CreateDeliveryItem("del-amharic");
        delivery["farmerName"] = "አበበ ከበደ";
        delivery["truckerName"] = "ታደሰ ወርቁ";
        delivery["buyerName"] = "መኮንን ታደሰ";

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal("አበበ ከበደ", dto.FarmerName);
        Assert.Equal("ታደሰ ወርቁ", dto.TruckerName);
        Assert.Equal("መኮንን ታደሰ", dto.BuyerName);
    }

    // --- Version Handling Tests ---

    [Fact]
    public void MapToDeliveryDto_VersionAsString_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-ver");
        delivery["version"] = "5";

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal(5, dto.Version);
    }

    [Fact]
    public void MapToDeliveryDto_MissingVersion_DefaultsToZero()
    {
        var delivery = CreateDeliveryItem("del-no-ver");
        delivery.Remove("version");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal(0, dto.Version);
    }

    // --- Method Not Allowed Tests ---

    [Fact]
    public async Task FunctionHandler_PostMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/deliveries"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Method not allowed", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_PutMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "PUT",
            Path = "/deliveries/del-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DeleteMethod_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "DELETE",
            Path = "/deliveries/del-001"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    // --- Security Headers Tests ---

    [Fact]
    public async Task FunctionHandler_IncludesSecurityHeaders()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = MakeAuthenticatedRequest("user-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Headers);
        Assert.True(response.Headers.ContainsKey("Content-Type"));
        Assert.Equal("application/json", response.Headers["Content-Type"]);
    }

    // --- Constructor Tests ---

    [Fact]
    public void Constructor_NullRepo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DeliveryFunction(null!));
    }

    // --- Mapper Field Coverage Tests ---

    [Fact]
    public void MapToDeliveryDto_AllRequiredFields_MappedCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.Equal("del-001", dto.DeliveryId);
        Assert.Equal("trip-001", dto.TripId);
        Assert.Equal("listing-001", dto.ListingId);
        Assert.Equal("farmer-123", dto.FarmerId);
        Assert.Equal("Abebe Kebede", dto.FarmerName);
        Assert.Equal("trucker-789", dto.TruckerId);
        Assert.Equal("Tadesse Worku", dto.TruckerName);
        Assert.Equal("buyer-456", dto.BuyerId);
        Assert.Equal("Mekonnen Tadesse", dto.BuyerName);
        Assert.Equal("qr-token-abc123", dto.QrToken);
        Assert.Equal("pending", dto.Status);
        Assert.Equal("2026-02-16T10:30:00Z", dto.CreatedAt);
        Assert.Equal("2026-02-16T10:30:00Z", dto.UpdatedAt);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public void MapToDeliveryDto_IncludePhone_ShowsAllPhones()
    {
        var delivery = CreateDeliveryItem("del-001");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery, includePhone: true);

        Assert.Equal("+251911111111", dto.FarmerPhone);
        Assert.Equal("+251922222222", dto.TruckerPhone);
        Assert.Equal("+251933333333", dto.BuyerPhone);
    }

    [Fact]
    public void MapToDeliveryDto_ExcludePhone_HidesAllPhones()
    {
        var delivery = CreateDeliveryItem("del-001");

        var dto = DeliveryFunction.MapToDeliveryDto(delivery, includePhone: false);

        Assert.Null(dto.FarmerPhone);
        Assert.Null(dto.TruckerPhone);
        Assert.Null(dto.BuyerPhone);
    }

    // --- Edge Cases ---

    [Fact]
    public async Task HandleGetDeliveries_NullQueryParams_UsesDefaults()
    {
        _mockDeliveryRepo.QueryDeliveriesAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            20,
            Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), null));

        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/deliveries",
            QueryStringParameters = null,
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", "user-001" }
                }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetDeliveryById_AuthWithOnlyAuthorizationHeader_Returns401()
    {
        // SECURITY: Raw Authorization headers are NOT trusted — only JWT authorizer claims are.
        // A request with only an Authorization header (no authorizer claims) must be rejected.
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var headers = new Dictionary<string, string> { { "Authorization", "Bearer test-token" } };
        var request = MakeGetRequest("/deliveries/del-001", pathParams: pathParams, headers: headers);
        var response = await _function.FunctionHandler(request, _context);

        // Without authorizer claims, request is unauthenticated — returns 401
        Assert.Equal(401, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public void MapToDeliveryDto_GpsWithDoubleValues_ParsesCorrectly()
    {
        var delivery = CreateDeliveryItem("del-001");
        delivery["gpsPickup"] = new Dictionary<string, object>
        {
            { "latitude", 8.9806 },
            { "longitude", 38.7578 }
        };

        var dto = DeliveryFunction.MapToDeliveryDto(delivery);

        Assert.NotNull(dto.GpsPickup);
        Assert.Equal(8.9806, dto.GpsPickup.Latitude, precision: 4);
        Assert.Equal(38.7578, dto.GpsPickup.Longitude, precision: 4);
    }

    // --- Photo Upload (POST /deliveries/{id}/photos) Tests ---

    private static APIGatewayProxyRequest MakePhotoUploadRequest(
        string userId,
        string deliveryId,
        string body)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/deliveries/{deliveryId}/photos",
            Body = body,
            PathParameters = new Dictionary<string, string> { { "id", deliveryId } },
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId }
                }
            }
        };
    }

    [Fact]
    public async Task HandleUploadPhoto_ValidRequest_ReturnsPresignedUrl()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);
        _mockS3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("https://s3.amazonaws.com/test-bucket/presigned-url");

        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("uploadUrl", response.Body);
        Assert.Contains("photoRef", response.Body);
        Assert.Contains("uploads/photos/del-001/pickup-", response.Body);
        Assert.Contains(".webp", response.Body);
        Assert.Contains("expiresInSeconds", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_ResponseMatchesContract()
    {
        // delivery-verification-api.md section 4 (1.7.0): JSON {type} in, a pre-signed PUT
        // out, and the photoRef is the key the sync photo-ref guard accepts.
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);
        _mockS3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("https://s3.amazonaws.com/test-bucket/presigned-url");

        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        Assert.Equal(
            new[] { "expiresInSeconds", "issuedAt", "photoRef", "uploadUrl" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("https://s3.amazonaws.com/test-bucket/presigned-url", root.GetProperty("uploadUrl").GetString());
        Assert.Matches(@"^uploads/photos/del-001/pickup-\d+\.webp$", root.GetProperty("photoRef").GetString());
        Assert.True(DateTime.TryParse(root.GetProperty("issuedAt").GetString(), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.Equal(900, root.GetProperty("expiresInSeconds").GetInt32());
    }

    [Fact]
    public async Task HandleUploadPhoto_DropoffType_ReturnsPresignedUrl()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);
        _mockS3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("https://s3.amazonaws.com/test-bucket/presigned-url");

        var request = MakePhotoUploadRequest("trucker-789", "del-001", "{\"type\":\"dropoff\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("uploads/photos/del-001/dropoff-", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_Unauthenticated_Returns401()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/deliveries/del-001/photos",
            Body = "{\"type\":\"pickup\"}",
            PathParameters = new Dictionary<string, string> { { "id", "del-001" } }
        };

        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_MissingType_Returns400()
    {
        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("type", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleUploadPhoto_InvalidType_Returns400()
    {
        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"selfie\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("pickup", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleUploadPhoto_EmptyBody_Returns400()
    {
        var request = MakePhotoUploadRequest("farmer-123", "del-001", "");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_DeliveryNotFound_Returns404()
    {
        _mockDeliveryRepo.GetByIdAsync("del-nonexistent").Returns((Dictionary<string, object>?)null);

        var request = MakePhotoUploadRequest("farmer-123", "del-nonexistent", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("DELIVERY_NOT_FOUND", response.Body);
    }

    // ESC-08: a party's cancel writes no deletedAt; a legacy soft-deleted row still has it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleUploadPhoto_CancelledDelivery_Returns404(bool legacySoftDelete)
    {
        var delivery = CreateDeliveryItem("del-001", "cancelled");
        if (legacySoftDelete)
        {
            delivery["deletedAt"] = "2026-02-17T00:00:00Z";
        }

        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("DELIVERY_NOT_FOUND", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_NonParticipant_Returns403()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var request = MakePhotoUploadRequest("random-user-999", "del-001", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_NoS3Config_Returns500()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        // Use _function which has no S3 client
        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DELIVERY_SERVER_ERROR", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_MissingDeliveryId_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/deliveries//photos",
            Body = "{\"type\":\"pickup\"}",
            PathParameters = new Dictionary<string, string> { { "id", "" } },
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", "farmer-123" }
                }
            }
        };

        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_S3Exception_Returns500()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);
        _mockS3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Throws(new AmazonS3Exception("S3 error"));

        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        var response = await _functionWithS3.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DELIVERY_SERVER_ERROR", response.Body);
    }

    [Fact]
    public async Task HandleUploadPhoto_PresignedUrlContainsWebPContentType()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);
        _mockS3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns("https://s3.amazonaws.com/test-bucket/presigned");

        var request = MakePhotoUploadRequest("farmer-123", "del-001", "{\"type\":\"pickup\"}");
        await _functionWithS3.FunctionHandler(request, _context);

        // Verify S3 was called with correct content type
        _mockS3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.ContentType == "image/webp" &&
            r.BucketName == TestBucket &&
            r.Verb == HttpVerb.PUT &&
            r.Key.StartsWith("uploads/photos/del-001/pickup-") &&
            r.Key.EndsWith(".webp")));
    }
}

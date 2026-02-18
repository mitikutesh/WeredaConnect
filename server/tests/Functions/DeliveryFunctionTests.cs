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
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

public class DeliveryFunctionTests
{
    private readonly IDeliveryRepository _mockDeliveryRepo;
    private readonly DeliveryFunction _function;
    private readonly TestLambdaContext _context;

    public DeliveryFunctionTests()
    {
        _mockDeliveryRepo = Substitute.For<IDeliveryRepository>();
        _function = new DeliveryFunction(_mockDeliveryRepo);
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
        Dictionary<string, string>? pathParams = null)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId }
                }
            }
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
    public async Task HandleGetDeliveries_Authenticated_ReturnsDeliveriesList()
    {
        var deliveries = new List<Dictionary<string, object>>
        {
            CreateDeliveryItem("del-001"),
            CreateDeliveryItem("del-002", "pickedUp")
        };

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

    [Fact]
    public async Task HandleGetDeliveryById_CancelledDelivery_Returns404()
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

    [Fact]
    public async Task HandleGetDeliveryById_FarmerIsParticipant_ShowsPhones()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("farmer-123", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // System.Text.Json escapes '+' as \u002B
        Assert.Contains("farmerPhone", response.Body);
        Assert.Contains("truckerPhone", response.Body);
        Assert.Contains("buyerPhone", response.Body);
        Assert.Contains("251911111111", response.Body);
        Assert.Contains("251922222222", response.Body);
        Assert.Contains("251933333333", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_TruckerIsParticipant_ShowsPhones()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("trucker-789", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("farmerPhone", response.Body);
        Assert.Contains("truckerPhone", response.Body);
        Assert.Contains("buyerPhone", response.Body);
        Assert.Contains("251911111111", response.Body);
        Assert.Contains("251922222222", response.Body);
        Assert.Contains("251933333333", response.Body);
    }

    [Fact]
    public async Task HandleGetDeliveryById_BuyerIsParticipant_ShowsPhones()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var request = MakeAuthenticatedRequest("buyer-456", "/deliveries/del-001", pathParams: pathParams);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("farmerPhone", response.Body);
        Assert.Contains("truckerPhone", response.Body);
        Assert.Contains("buyerPhone", response.Body);
        Assert.Contains("251911111111", response.Body);
        Assert.Contains("251922222222", response.Body);
        Assert.Contains("251933333333", response.Body);
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
    public async Task HandleGetDeliveryById_AuthWithAuthorizationHeader_NonParticipant_Returns403()
    {
        var delivery = CreateDeliveryItem("del-001");
        _mockDeliveryRepo.GetByIdAsync("del-001").Returns(delivery);

        var pathParams = new Dictionary<string, string> { { "id", "del-001" } };
        var headers = new Dictionary<string, string> { { "Authorization", "Bearer test-token" } };
        var request = MakeGetRequest("/deliveries/del-001", pathParams: pathParams, headers: headers);
        var response = await _function.FunctionHandler(request, _context);

        // Non-participant should be rejected with 403
        Assert.Equal(403, response.StatusCode);
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
}

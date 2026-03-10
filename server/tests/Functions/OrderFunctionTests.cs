using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class OrderFunctionTests
{
    private readonly IOrderRepository _mockOrderRepo;
    private readonly OrderFunction _function;
    private readonly TestLambdaContext _context;

    public OrderFunctionTests()
    {
        _mockOrderRepo = Substitute.For<IOrderRepository>();
        _function = new OrderFunction(_mockOrderRepo);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        string path = "/orders",
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null,
        string? userId = "buyer-001")
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = userId != null
                    ? new APIGatewayCustomAuthorizerContext { { "sub", userId } }
                    : null
            }
        };

        return request;
    }

    private static Dictionary<string, object> CreateOrderItem(
        string orderId = "order-001",
        string status = "created",
        string buyerId = "buyer-001",
        string farmerId = "farmer-001")
    {
        return new Dictionary<string, object>
        {
            { "orderId", orderId },
            { "listingId", "listing-001" },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
            { "buyerName", "Abebe Kebede" },
            { "farmerName", "Mulu Tadesse" },
            { "cropType", "teff" },
            { "quantity", "500" },
            { "pricePerUnit", "45" },
            { "cropCost", "22500" },
            { "commissionRate", "0.05" },
            { "commissionAmount", "1125" },
            { "orderTotal", "23625" },
            { "status", status },
            { "createdAt", "2026-03-01T10:00:00Z" },
            { "updatedAt", "2026-03-01T10:00:00Z" },
            { "version", "1" }
        };
    }

    // --- GET /orders Tests ---

    [Fact]
    public async Task HandleGetOrders_Success_ReturnsEmptyList()
    {
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"orders\":[]", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_Success_ReturnsOrdersList()
    {
        var orders = new List<Dictionary<string, object>>
        {
            CreateOrderItem("order-001"),
            CreateOrderItem("order-002", "funded")
        };

        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((orders, (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("order-001", response.Body);
        Assert.Contains("order-002", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_InvalidLimit_Returns400()
    {
        var request = MakeGetRequest(queryParams: new Dictionary<string, string> { { "limit", "999" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ORDER_VALIDATION", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_ServerError_Returns500()
    {
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new Exception("DynamoDB error"));

        var request = MakeGetRequest();
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("ORDER_SERVER_ERROR", response.Body);
    }

    // --- GET /orders/{id} Tests ---

    [Fact]
    public async Task HandleGetOrderById_Success_ReturnsOrder()
    {
        var order = CreateOrderItem();
        _mockOrderRepo.GetByIdAsync("order-001").Returns(order);

        var request = MakeGetRequest(
            path: "/orders/order-001",
            pathParams: new Dictionary<string, string> { { "id", "order-001" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("order-001", response.Body);
        Assert.Contains("teff", response.Body);
    }

    [Fact]
    public async Task HandleGetOrderById_NotFound_Returns404()
    {
        _mockOrderRepo.GetByIdAsync("nonexistent").Returns((Dictionary<string, object>?)null);

        var request = MakeGetRequest(
            path: "/orders/nonexistent",
            pathParams: new Dictionary<string, string> { { "id", "nonexistent" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("ORDER_NOT_FOUND", response.Body);
    }

    [Fact]
    public async Task HandleGetOrderById_MissingId_Returns400()
    {
        var request = MakeGetRequest(
            path: "/orders/",
            pathParams: new Dictionary<string, string> { { "id", "" } });
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    // --- Method not allowed ---

    [Fact]
    public async Task HandlePost_Returns405()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/orders",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "user-1" } }
            }
        };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(405, response.StatusCode);
    }

    // --- GET /orders/{id} Authorization Tests ---

    [Fact]
    public async Task HandleGetOrderById_UnauthorizedUser_Returns403()
    {
        var order = CreateOrderItem(buyerId: "buyer-001", farmerId: "farmer-001");
        _mockOrderRepo.GetByIdAsync("order-001").Returns(order);

        var request = MakeGetRequest(
            path: "/orders/order-001",
            pathParams: new Dictionary<string, string> { { "id", "order-001" } },
            userId: "unrelated-user");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ORDER_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetOrderById_FarmerCanAccess_Returns200()
    {
        var order = CreateOrderItem(buyerId: "buyer-001", farmerId: "farmer-001");
        _mockOrderRepo.GetByIdAsync("order-001").Returns(order);

        var request = MakeGetRequest(
            path: "/orders/order-001",
            pathParams: new Dictionary<string, string> { { "id", "order-001" } },
            userId: "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("order-001", response.Body);
    }

    // --- GET /orders Authorization Tests ---

    [Fact]
    public async Task HandleGetOrders_WithOtherBuyerId_Returns403()
    {
        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "buyerId", "other-buyer" } },
            userId: "buyer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ORDER_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_WithOtherFarmerId_Returns403()
    {
        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "farmerId", "other-farmer" } },
            userId: "buyer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ORDER_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_WithOwnFarmerId_Returns200()
    {
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "farmerId", "farmer-001" } },
            userId: "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task HandleGetOrders_WithOtherTruckerId_Returns403()
    {
        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "truckerId", "other-trucker" } },
            userId: "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("ORDER_FORBIDDEN", response.Body);
    }

    [Fact]
    public async Task HandleGetOrders_WithOwnTruckerId_Returns200()
    {
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "truckerId", "trucker-001" } },
            userId: "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    // --- MapToOrderDto Tests ---

    [Fact]
    public void MapToOrderDto_MapsAllFields()
    {
        var item = CreateOrderItem();
        item["tripId"] = "trip-001";
        item["truckerId"] = "trucker-001";
        item["truckerName"] = "Bekele";
        item["transportCost"] = "1200";
        item["paymentMethod"] = "cbe_transfer";
        item["cbeTransactionId"] = "CBE123456";

        var dto = OrderFunction.MapToOrderDto(item);

        Assert.Equal("order-001", dto.OrderId);
        Assert.Equal("teff", dto.CropType);
        Assert.Equal(500, dto.Quantity);
        Assert.Equal(45, dto.PricePerUnit);
        Assert.Equal(22500, dto.CropCost);
        Assert.Equal(0.05, dto.CommissionRate);
        Assert.Equal(1125, dto.CommissionAmount);
        Assert.Equal(23625, dto.OrderTotal);
        Assert.Equal("trip-001", dto.TripId);
        Assert.Equal("trucker-001", dto.TruckerId);
        Assert.Equal("Bekele", dto.TruckerName);
        Assert.Equal(1200, dto.TransportCost);
        Assert.Equal("cbe_transfer", dto.PaymentMethod);
        Assert.Equal("CBE123456", dto.CbeTransactionId);
        Assert.Equal("created", dto.Status);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public void MapToOrderDto_HandlesNullOptionalFields()
    {
        var item = CreateOrderItem();
        var dto = OrderFunction.MapToOrderDto(item);

        Assert.Null(dto.TripId);
        Assert.Null(dto.TruckerId);
        Assert.Null(dto.TransportCost);
        Assert.Null(dto.PaymentMethod);
        Assert.Null(dto.CbeTransactionId);
        Assert.Null(dto.RespondedAt);
        Assert.Null(dto.RejectionReason);
        Assert.Null(dto.CancelledAt);
        Assert.Null(dto.CancelReason);
    }
}

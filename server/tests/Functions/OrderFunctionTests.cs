using System.Text.Json;
using Amazon.DynamoDBv2;
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
        string? userId = "buyer-001",
        string? role = null)
    {
        APIGatewayCustomAuthorizerContext? authorizer = null;
        if (userId != null)
        {
            authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
            if (role != null)
            {
                authorizer["custom:role"] = role;
            }
        }

        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = authorizer
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

    // --- GET /orders default scoping by the role claim (SIM-16) ---

    [Theory]
    [InlineData("farmer", "farmer-001", null, "farmer-001", null)]
    [InlineData("trucker", "trucker-001", null, null, "trucker-001")]
    [InlineData("buyer", "buyer-001", "buyer-001", null, null)]
    [InlineData(null, "buyer-001", "buyer-001", null, null)]
    public async Task HandleGetOrders_NoPartyFilter_ScopesByRoleClaim(
        string? role, string userId, string? expectedBuyer, string? expectedFarmer, string? expectedTrucker)
    {
        // Farmers and truckers used to be silently scoped as buyer and read [].
        // A token without a role claim keeps the buyer default.
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest(userId: userId, role: role);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockOrderRepo.Received(1).QueryOrdersAsync(
            expectedBuyer, expectedFarmer, expectedTrucker, null, null, 20, null);
    }

    [Fact]
    public async Task HandleGetOrders_ExplicitPartyFilter_WinsOverRoleClaim()
    {
        _mockOrderRepo.QueryOrdersAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "farmerId", "farmer-001" }, { "status", "funded" } },
            userId: "farmer-001", role: "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockOrderRepo.Received(1).QueryOrdersAsync(null, "farmer-001", null, "funded", null, 20, null);
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
        Assert.Null(dto.PickupDeadlineAt);
        Assert.Null(dto.AdminQueueReason);
        Assert.Null(dto.AdminQueuedAt);
    }

    [Fact]
    public void MapToOrderDto_CarriesThePickupDeadlineAndTheAdminQueueEntry()
    {
        // ESC-06: clients show Cancel from pickupDeadlineAt; the admin transaction detail shows the queue.
        var item = CreateOrderItem();
        item["pickupDeadlineAt"] = "2026-10-12T08:00:00.0000000Z";
        item["adminQueue"] = "open";
        item["adminQueuedAt"] = "2026-10-30T08:00:00.0000000Z";
        item["adminQueueReason"] = "hold_cap";

        var dto = OrderFunction.MapToOrderDto(item);

        Assert.Equal("2026-10-12T08:00:00.0000000Z", dto.PickupDeadlineAt);
        Assert.Equal("hold_cap", dto.AdminQueueReason);
        Assert.Equal("2026-10-30T08:00:00.0000000Z", dto.AdminQueuedAt);
    }

    // --- ESC-48: contactOpen and the parties' phones ---

    private const string BuyerPhone = "+251911000001";
    private const string FarmerPhone = "+251911000002";
    private const string TruckerPhone = "+251911000003";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private OrderFunction FunctionWithUsers()
    {
        _users.GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(call => (IReadOnlyDictionary<string, string>)call.Arg<IReadOnlyCollection<string>>()
                .Select(id => (id, phone: id switch
                {
                    "buyer-001" => BuyerPhone,
                    "farmer-001" => FarmerPhone,
                    "trucker-001" => TruckerPhone,
                    _ => null
                }))
                .Where(p => p.phone != null)
                .ToDictionary(p => p.id, p => p.phone!));
        return new OrderFunction(_mockOrderRepo, _users);
    }

    private static Dictionary<string, object> TripOrder(string status, string? overrideFlag = null)
    {
        var order = CreateOrderItem(status: status);
        order["truckerId"] = "trucker-001";
        order["tripId"] = "trip-001";
        if (overrideFlag != null)
        {
            order[overrideFlag] = "2026-10-10T10:00:00Z";
        }

        return order;
    }

    private void ReturnsList(params Dictionary<string, object>[] orders)
        => _mockOrderRepo.QueryOrdersAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((orders.ToList(), (Dictionary<string, AttributeValue>?)null));

    private static APIGatewayProxyRequest DetailRequest(string userId)
        => MakeGetRequest(
            path: "/orders/order-001",
            pathParams: new Dictionary<string, string> { { "id", "order-001" } },
            userId: userId);

    private static JsonElement OrderOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var order = root.TryGetProperty("order", out var single) ? single : root.GetProperty("orders")[0];
        return order.Clone();
    }

    private static string? PhoneOf(JsonElement order, string field)
        => order.TryGetProperty(field, out var value) ? value.GetString() : null;

    [Theory]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    [InlineData("disputed")]
    public async Task GetOrderById_OpenOrder_EveryPartyGetsThePartiesPhones(string status)
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder(status));
        var fn = FunctionWithUsers();

        foreach (var caller in new[] { "buyer-001", "farmer-001", "trucker-001" })
        {
            var response = await fn.FunctionHandler(DetailRequest(caller), _context);

            Assert.Equal(200, response.StatusCode);
            var order = OrderOf(response.Body);
            Assert.True(order.GetProperty("contactOpen").GetBoolean());
            Assert.Equal(BuyerPhone, PhoneOf(order, "buyerPhone"));
            Assert.Equal(FarmerPhone, PhoneOf(order, "farmerPhone"));
            Assert.Equal(TruckerPhone, PhoneOf(order, "truckerPhone"));
        }
    }

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_submitted")]
    [InlineData("payment_failed")]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("dispute_resolved")]
    public async Task GetOrderById_ClosedOrder_NoPhonesForAnyParty(string status)
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder(status));
        var fn = FunctionWithUsers();

        foreach (var caller in new[] { "buyer-001", "farmer-001", "trucker-001" })
        {
            var response = await fn.FunctionHandler(DetailRequest(caller), _context);

            Assert.Equal(200, response.StatusCode);
            var order = OrderOf(response.Body);
            Assert.False(order.GetProperty("contactOpen").GetBoolean());
            Assert.Null(PhoneOf(order, "buyerPhone"));
            Assert.Null(PhoneOf(order, "farmerPhone"));
            Assert.Null(PhoneOf(order, "truckerPhone"));
        }

        await _users.DidNotReceive().GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Theory]
    [InlineData("refundOverrideAt")]
    [InlineData("disputeOverrideAt")]
    public async Task GetOrderById_RefundedByAdminOverride_IsClosed(string flag)
    {
        // An admin refund over goods on the move leaves the order in its status but flagged.
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder("in_transit", flag));

        var response = await FunctionWithUsers().FunctionHandler(DetailRequest("buyer-001"), _context);

        var order = OrderOf(response.Body);
        Assert.False(order.GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(order, "farmerPhone"));
    }

    [Fact]
    public async Task GetOrders_List_PhonesOnlyOnOpenOrders()
    {
        var open = TripOrder("accepted");
        var closed = TripOrder("funded");
        closed["orderId"] = "order-002";
        ReturnsList(open, closed);

        var response = await FunctionWithUsers().FunctionHandler(MakeGetRequest(userId: "buyer-001", role: "buyer"), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        var orders = doc.RootElement.GetProperty("orders");
        Assert.True(orders[0].GetProperty("contactOpen").GetBoolean());
        Assert.Equal(FarmerPhone, PhoneOf(orders[0], "farmerPhone"));
        Assert.Equal(TruckerPhone, PhoneOf(orders[0], "truckerPhone"));
        Assert.False(orders[1].GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(orders[1], "farmerPhone"));
        // One batched read, for the open order's parties only.
        await _users.Received(1).GetPhoneNumbersAsync(Arg.Is<IReadOnlyCollection<string>>(ids =>
            ids.Count == 3 && ids.Contains("buyer-001") && ids.Contains("farmer-001") && ids.Contains("trucker-001")));
    }

    [Fact]
    public async Task GetOrderById_SelfPickup_HasNoTruckerPhone()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(CreateOrderItem(status: "accepted"));

        var response = await FunctionWithUsers().FunctionHandler(DetailRequest("farmer-001"), _context);

        var order = OrderOf(response.Body);
        Assert.True(order.GetProperty("contactOpen").GetBoolean());
        Assert.Equal(BuyerPhone, PhoneOf(order, "buyerPhone"));
        Assert.Null(PhoneOf(order, "truckerPhone"));
    }

    [Fact]
    public async Task GetOrderById_PhoneReadFails_StillReturnsTheOrderWithoutPhones()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder("accepted"));
        _users.GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var response = await new OrderFunction(_mockOrderRepo, _users).FunctionHandler(DetailRequest("buyer-001"), _context);

        Assert.Equal(200, response.StatusCode);
        var order = OrderOf(response.Body);
        Assert.True(order.GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(order, "farmerPhone"));
        var log = ((TestLambdaLogger)_context.Logger).Buffer.ToString();
        Assert.Contains("AmazonDynamoDBException", log);
        Assert.DoesNotContain("throttled", log);
    }

    [Fact]
    public async Task GetOrderById_NoUsersTable_ContactOpenButNoPhones()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder("accepted"));

        var response = await _function.FunctionHandler(DetailRequest("buyer-001"), _context);

        var order = OrderOf(response.Body);
        Assert.True(order.GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(order, "farmerPhone"));
    }

    [Fact]
    public async Task GetOrderById_NonParty_StillForbidden()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(TripOrder("accepted"));

        var response = await FunctionWithUsers().FunctionHandler(DetailRequest("stranger-001"), _context);

        Assert.Equal(403, response.StatusCode);
        await _users.DidNotReceive().GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // --- ESC-15: contact per party ---

    private static Dictionary<string, object> CommittedTripOrder(string status, string farmer, string trucker)
    {
        var order = TripOrder(status);
        order["farmerResponse"] = farmer;
        order["truckerResponse"] = trucker;
        return order;
    }

    [Theory]
    [InlineData("funded", "pending")]
    [InlineData("awaiting_transport", "declined")]
    public async Task GetOrderById_FarmerAcceptedButNoTrucker_BuyerAndFarmerTalk_TruckerIsOut(string status, string truckerResponse)
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(CommittedTripOrder(status, "accepted", truckerResponse));
        var fn = FunctionWithUsers();

        foreach (var caller in new[] { "buyer-001", "farmer-001" })
        {
            var order = OrderOf((await fn.FunctionHandler(DetailRequest(caller), _context)).Body);

            Assert.True(order.GetProperty("contactOpen").GetBoolean());
            Assert.Equal(BuyerPhone, PhoneOf(order, "buyerPhone"));
            Assert.Equal(FarmerPhone, PhoneOf(order, "farmerPhone"));
            Assert.Null(PhoneOf(order, "truckerPhone"));
        }

        // The buyer and farmer reads never asked for the trucker's number.
        await _users.DidNotReceive().GetPhoneNumbersAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("trucker-001")));
        _users.ClearReceivedCalls();

        var truckerView = OrderOf((await fn.FunctionHandler(DetailRequest("trucker-001"), _context)).Body);

        Assert.False(truckerView.GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(truckerView, "buyerPhone"));
        Assert.Null(PhoneOf(truckerView, "farmerPhone"));
        Assert.Null(PhoneOf(truckerView, "truckerPhone"));
        await _users.DidNotReceive().GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task GetOrderById_TruckerCommitted_EveryPartyGetsEveryPhone()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(CommittedTripOrder("accepted", "accepted", "accepted"));
        var fn = FunctionWithUsers();

        foreach (var caller in new[] { "buyer-001", "farmer-001", "trucker-001" })
        {
            var order = OrderOf((await fn.FunctionHandler(DetailRequest(caller), _context)).Body);

            Assert.True(order.GetProperty("contactOpen").GetBoolean());
            Assert.Equal(BuyerPhone, PhoneOf(order, "buyerPhone"));
            Assert.Equal(FarmerPhone, PhoneOf(order, "farmerPhone"));
            Assert.Equal(TruckerPhone, PhoneOf(order, "truckerPhone"));
        }
    }

    [Fact]
    public async Task GetOrderById_FarmerStillDeciding_IsClosedForEveryone()
    {
        _mockOrderRepo.GetByIdAsync("order-001").Returns(CommittedTripOrder("funded", "pending", "not_asked"));
        var fn = FunctionWithUsers();

        foreach (var caller in new[] { "buyer-001", "farmer-001", "trucker-001" })
        {
            var order = OrderOf((await fn.FunctionHandler(DetailRequest(caller), _context)).Body);

            Assert.False(order.GetProperty("contactOpen").GetBoolean());
            Assert.Null(PhoneOf(order, "farmerPhone"));
        }

        await _users.DidNotReceive().GetPhoneNumbersAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task GetOrders_List_TruckerCaller_SeesOnlyTheOrdersTheyCommittedTo()
    {
        var committed = CommittedTripOrder("accepted", "accepted", "accepted");
        var asked = CommittedTripOrder("funded", "accepted", "pending");
        asked["orderId"] = "order-002";
        ReturnsList(committed, asked);

        var response = await FunctionWithUsers().FunctionHandler(MakeGetRequest(userId: "trucker-001", role: "trucker"), _context);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        var orders = doc.RootElement.GetProperty("orders");
        Assert.True(orders[0].GetProperty("contactOpen").GetBoolean());
        Assert.Equal(BuyerPhone, PhoneOf(orders[0], "buyerPhone"));
        Assert.Equal(FarmerPhone, PhoneOf(orders[0], "farmerPhone"));
        Assert.False(orders[1].GetProperty("contactOpen").GetBoolean());
        Assert.Null(PhoneOf(orders[1], "buyerPhone"));
        Assert.Null(PhoneOf(orders[1], "farmerPhone"));
    }

    [Fact]
    public void MapToOrderDto_CarriesTheCommitmentFields()
    {
        var item = CommittedTripOrder("awaiting_transport", "accepted", "declined");
        item["fulfillment"] = "trucker";
        item["pickupDate"] = "2026-10-30T06:00:00.0000000Z";
        item["farmerRespondedAt"] = "2026-10-26T10:00:00.0000000Z";
        item["farmerDeadlineAt"] = "2026-10-27T09:00:00.0000000Z";
        item["truckerAskedAt"] = "2026-10-26T10:00:00.0000000Z";
        item["truckerRespondedAt"] = "2026-10-26T12:00:00.0000000Z";
        item["truckerDeadlineAt"] = "2026-10-27T10:00:00.0000000Z";
        item["buyerChoiceDeadlineAt"] = "2026-10-28T12:00:00.0000000Z";
        item["transportFailure"] = "trucker_declined";
        item["declineReason"] = "route";
        item["faultParty"] = "trucker";

        var dto = OrderFunction.MapToOrderDto(item);

        Assert.Equal("trucker", dto.Fulfillment);
        Assert.Equal("2026-10-30T06:00:00.0000000Z", dto.PickupDate);
        Assert.Equal("accepted", dto.FarmerResponse);
        Assert.Equal("2026-10-26T10:00:00.0000000Z", dto.FarmerRespondedAt);
        Assert.Equal("2026-10-27T09:00:00.0000000Z", dto.FarmerDeadlineAt);
        Assert.Equal("declined", dto.TruckerResponse);
        Assert.Equal("2026-10-26T10:00:00.0000000Z", dto.TruckerAskedAt);
        Assert.Equal("2026-10-26T12:00:00.0000000Z", dto.TruckerRespondedAt);
        Assert.Equal("2026-10-27T10:00:00.0000000Z", dto.TruckerDeadlineAt);
        Assert.Equal("2026-10-28T12:00:00.0000000Z", dto.BuyerChoiceDeadlineAt);
        Assert.Equal("trucker_declined", dto.TransportFailure);
        Assert.Equal("route", dto.DeclineReason);
        Assert.Null(dto.CancelledBy);
        Assert.Equal("trucker", dto.FaultParty);
        // No caller: the buyer-farmer contact.
        Assert.True(dto.ContactOpen);
    }

    [Fact]
    public void MapToOrderDto_LegacyRow_HasNoCommitmentFields()
    {
        var dto = OrderFunction.MapToOrderDto(TripOrder("accepted"));

        Assert.Null(dto.Fulfillment);
        Assert.Null(dto.FarmerResponse);
        Assert.Null(dto.TruckerResponse);
        Assert.Null(dto.TransportFailure);
    }

    [Fact]
    public void MapToOrderDto_WithoutPhones_StillComputesContactOpen()
    {
        // The admin transaction view maps orders without phones.
        Assert.True(OrderFunction.MapToOrderDto(TripOrder("delivered")).ContactOpen);
        Assert.False(OrderFunction.MapToOrderDto(TripOrder("funded")).ContactOpen);
        Assert.Null(OrderFunction.MapToOrderDto(TripOrder("delivered")).FarmerPhone);
    }
}

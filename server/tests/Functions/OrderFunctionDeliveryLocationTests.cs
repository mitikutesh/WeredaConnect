using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-14: <c>GET /orders</c> and <c>GET /orders/{id}</c> return the order's own drop-off point
/// (<c>deliveryLatitude</c>, <c>deliveryLongitude</c>) to the buyer, the farmer, and the order's trucker
/// while the money is held, committed or only asked (wider than the note). The admin view reads it.
/// </summary>
public class OrderFunctionDeliveryLocationTests
{
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly OrderFunction _function;

    public OrderFunctionDeliveryLocationTests()
    {
        _function = new OrderFunction(_orders);
    }

    [Theory]
    [InlineData("buyer-001", "accepted", "pending")]
    [InlineData("farmer-001", "funded", "pending")]
    [InlineData("trucker-001", "accepted", "accepted")]
    [InlineData("trucker-001", "in_transit", "accepted")]
    public async Task ThePartiesWhoMayReadIt_GetThePoint_AsNumbers(string caller, string status, string truckerResponse)
    {
        _orders.GetByIdAsync("order-001").Returns(Order(status, truckerResponse));

        var order = await DetailAsync(caller);

        PointOf(order).Should().Be((8.5423, 39.2687));
    }

    [Theory]
    [InlineData("funded", "pending")]
    [InlineData("funded", "not_asked")]
    [InlineData("awaiting_transport", "declined")]
    public async Task ATruckerOnlyAsked_GetsThePoint_ButNotTheNote(string status, string truckerResponse)
    {
        // The trucker decides on the transport request knowing where the goods go.
        var stored = Order(status, truckerResponse);
        stored["dropoffNote"] = "Merkato";
        _orders.GetByIdAsync("order-001").Returns(stored);

        var order = await DetailAsync("trucker-001");

        PointOf(order).Should().Be((8.5423, 39.2687));
        order.TryGetProperty("dropoffNote", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("pending_payment", "none")]
    [InlineData("cancelled", "accepted")]
    public async Task ATruckerOfAnOrderWhoseMoneyIsNotHeld_SeesTheOrder_WithoutThePoint(string status, string truckerResponse)
    {
        _orders.GetByIdAsync("order-001").Returns(Order(status, truckerResponse));

        var order = await DetailAsync("trucker-001");

        order.GetProperty("orderId").GetString().Should().Be("order-001");
        PointOf(order).Should().BeNull();
        order.TryGetProperty("deliveryLatitude", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheTruckersList_CarriesThePoint_OnlyWhileTheMoneyIsHeld()
    {
        var committed = Order("accepted", "accepted");
        var asked = Order("funded", "pending", orderId: "order-002");
        var unpaid = Order("pending_payment", "none", orderId: "order-003");
        _orders.QueryOrdersAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { committed, asked, unpaid }, (Dictionary<string, AttributeValue>?)null));

        var response = await _function.FunctionHandler(Request("/orders", "trucker-001", role: "trucker"), _context);

        response.StatusCode.Should().Be(200);
        var orders = JsonDocument.Parse(response.Body).RootElement.GetProperty("orders");
        PointOf(orders[0]).Should().Be((8.5423, 39.2687));
        PointOf(orders[1]).Should().Be((8.5423, 39.2687));
        PointOf(orders[2]).Should().BeNull();
    }

    [Fact]
    public void TheAdminView_ReadsThePoint_AndAMapWithoutACaller_DoesNot()
    {
        var order = Order("disputed", "accepted");

        var admin = OrderFunction.MapToOrderDto(order);
        admin.DeliveryLatitude.Should().Be(8.5423);
        admin.DeliveryLongitude.Should().Be(39.2687);
        OrderFunction.MapToOrderDto(order, null).DeliveryLatitude.Should().BeNull();
    }

    [Fact]
    public void AnOrderWithoutAPoint_HasNone()
    {
        var order = Order("accepted", "accepted");
        order.Remove("deliveryLatitude");
        order.Remove("deliveryLongitude");

        var dto = OrderFunction.MapToOrderDto(order, null, "buyer-001");
        dto.DeliveryLatitude.Should().BeNull();
        dto.DeliveryLongitude.Should().BeNull();
    }

    private async Task<JsonElement> DetailAsync(string caller)
    {
        var response = await _function.FunctionHandler(Detail(caller), _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("order").Clone();
    }

    private static (double, double)? PointOf(JsonElement order)
        => order.TryGetProperty("deliveryLatitude", out var lat) && lat.ValueKind == JsonValueKind.Number
           && order.TryGetProperty("deliveryLongitude", out var lng) && lng.ValueKind == JsonValueKind.Number
            ? (lat.GetDouble(), lng.GetDouble())
            : null;

    private static APIGatewayProxyRequest Detail(string caller)
    {
        var request = Request("/orders/order-001", caller);
        request.PathParameters = new Dictionary<string, string> { { "id", "order-001" } };
        return request;
    }

    private static APIGatewayProxyRequest Request(string path, string caller, string? role = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", caller } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static Dictionary<string, object> Order(string status, string truckerResponse, string orderId = "order-001") => new()
    {
        { "orderId", orderId }, { "listingId", "listing-001" },
        { "buyerId", "buyer-001" }, { "farmerId", "farmer-001" },
        { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
        { "tripId", "trip-001" }, { "truckerId", "trucker-001" }, { "truckerName", "Tadesse Worku" },
        { "cropType", "teff" }, { "quantity", "500" }, { "pricePerUnit", "45" },
        { "cropCost", "22500" }, { "commissionRate", "0.05" }, { "commissionAmount", "1125" },
        { "orderTotal", "23625" }, { "status", status }, { "truckerResponse", truckerResponse },
        // As the orders table returns numbers: invariant text.
        { "deliveryLatitude", "8.5423" }, { "deliveryLongitude", "39.2687" },
        { "createdAt", "2026-10-30T08:00:00Z" }, { "updatedAt", "2026-10-30T08:00:00Z" }, { "version", "3" }
    };
}

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
/// LT-12: <c>GET /orders</c> and <c>GET /orders/{id}</c> return the drop-off note to the order's
/// buyer and farmer, and to its trucker only once committed; the order's visibility itself is
/// unchanged (a non-party still gets 403). The admin transaction view reads it.
/// </summary>
public class OrderFunctionDropoffNoteTests
{
    private const string Note = "Merkato, near Anwar Mosque";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly OrderFunction _function;

    public OrderFunctionDropoffNoteTests()
    {
        _function = new OrderFunction(_orders);
    }

    [Theory]
    [InlineData("buyer-001", "accepted", "pending")]
    [InlineData("farmer-001", "funded", "pending")]
    [InlineData("trucker-001", "accepted", "accepted")]
    [InlineData("trucker-001", "in_transit", "accepted")]
    public async Task ThePartiesWhoMayReadIt_GetTheNote(string caller, string status, string truckerResponse)
    {
        _orders.GetByIdAsync("order-001").Returns(Order(status, truckerResponse));

        var order = await DetailAsync(caller);

        order.GetProperty("dropoffNote").GetString().Should().Be(Note);
    }

    [Theory]
    [InlineData("funded", "pending")]
    [InlineData("awaiting_transport", "declined")]
    [InlineData("cancelled", "accepted")]
    public async Task ATruckerNotCommitted_SeesTheOrder_WithoutTheNote(string status, string truckerResponse)
    {
        _orders.GetByIdAsync("order-001").Returns(Order(status, truckerResponse));

        var order = await DetailAsync("trucker-001");

        order.GetProperty("orderId").GetString().Should().Be("order-001");
        NoteOf(order).Should().BeNull();
    }

    [Fact]
    public async Task ANonParty_StillGets403()
    {
        _orders.GetByIdAsync("order-001").Returns(Order("accepted", "accepted"));

        var response = await _function.FunctionHandler(Detail("someone-else"), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().NotContain(Note);
    }

    [Fact]
    public async Task TheTruckersList_CarriesTheNote_OnlyOnTheCommittedOrders()
    {
        var committed = Order("accepted", "accepted");
        var asked = Order("funded", "pending", orderId: "order-002");
        _orders.QueryOrdersAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { committed, asked }, (Dictionary<string, AttributeValue>?)null));

        var response = await _function.FunctionHandler(Request("/orders", "trucker-001", role: "trucker"), _context);

        response.StatusCode.Should().Be(200);
        var orders = JsonDocument.Parse(response.Body).RootElement.GetProperty("orders");
        NoteOf(orders[0]).Should().Be(Note);
        NoteOf(orders[1]).Should().BeNull();
    }

    [Fact]
    public void TheAdminView_ReadsTheNote_AndAMapWithoutACaller_DoesNot()
    {
        var order = Order("disputed", "accepted");

        OrderFunction.MapToOrderDto(order).DropoffNote.Should().Be(Note);
        OrderFunction.MapToOrderDto(order, null).DropoffNote.Should().BeNull();
    }

    [Fact]
    public void AnOrderWithoutANote_HasNone()
    {
        var order = Order("accepted", "accepted");
        order.Remove("dropoffNote");

        OrderFunction.MapToOrderDto(order, null, "buyer-001").DropoffNote.Should().BeNull();
    }

    private async Task<JsonElement> DetailAsync(string caller)
    {
        var response = await _function.FunctionHandler(Detail(caller), _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("order").Clone();
    }

    private static string? NoteOf(JsonElement order)
        => order.TryGetProperty("dropoffNote", out var note) && note.ValueKind == JsonValueKind.String
            ? note.GetString()
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
        { "dropoffNote", Note },
        { "createdAt", "2026-10-30T08:00:00Z" }, { "updatedAt", "2026-10-30T08:00:00Z" }, { "version", "3" }
    };
}

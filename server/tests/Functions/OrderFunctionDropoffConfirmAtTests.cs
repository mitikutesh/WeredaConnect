using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-370 (order-lifecycle 2.22.0): <c>GET /orders</c> and <c>GET /orders/{id}</c> carry <c>dropoffConfirmAt</c>,
/// T9 computed with the order timings the order cron judges by (here a non-default <c>silent-buyer-hours</c>),
/// to the order's parties while its drop-off waits for T9; never with the judgement off, never on another
/// order, never in the admin view.
/// </summary>
public class OrderFunctionDropoffConfirmAtTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";

    private static readonly DateTime Received = new(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The SSM <c>silent-buyer-hours</c> set to 36 instead of the default 48.</summary>
    private static readonly OrderTimings Timings = OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(36) };

    private static readonly string Expected = OrderCommitment.Iso(Received.AddHours(36));

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly TestLambdaContext _context = new();

    private OrderFunction Function(bool judgementOn = true)
        => new(_orders) { DropoffJudgementOn = judgementOn, OrderTimingsSource = () => Timings };

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public async Task EachParty_GetsT9_WithTheSsmWindow_NotTheDefault48Hours(string caller)
    {
        _orders.GetByIdAsync("order-001").Returns(Order());

        var order = await DetailAsync(Function(), caller);

        order.GetProperty("dropoffConfirmAt").GetString().Should().Be(Expected);
        order.GetProperty("dropoffConfirmAt").GetString().Should().NotBe(OrderCommitment.Iso(Received.AddHours(48)));
    }

    [Fact]
    public async Task TheBuyersList_CarriesIt_OnTheOrderWaitingForT9_Only()
    {
        var waiting = Order();
        var judged = Order(judgement: DropoffJudgement.Outcome.AutoConfirmed, orderId: "order-002", status: "delivered");
        _orders.QueryOrdersAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { waiting, judged }, (Dictionary<string, AttributeValue>?)null));

        var response = await Function().FunctionHandler(Request("/orders", BuyerId, role: "buyer"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var orders = JsonDocument.Parse(response.Body).RootElement.GetProperty("orders");
        orders[0].GetProperty("dropoffConfirmAt").GetString().Should().Be(Expected);
        orders[1].TryGetProperty("dropoffConfirmAt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WithTheJudgementOff_ItIsLeftOut_AndTheTimingsAreNotRead()
    {
        var read = false;
        _orders.GetByIdAsync("order-001").Returns(Order());
        var function = new OrderFunction(_orders)
        {
            DropoffJudgementOn = false,
            OrderTimingsSource = () =>
            {
                read = true;
                return Timings;
            }
        };

        var order = await DetailAsync(function, BuyerId);

        order.TryGetProperty("dropoffConfirmAt", out _).Should().BeFalse();
        order.GetProperty("dropoffJudgement").GetString().Should().Be(DropoffJudgement.Outcome.Pending);
        read.Should().BeFalse();
    }

    [Fact]
    public async Task BeforeTheTruckersCheckIn_ItIsLeftOut()
    {
        var order = Order();
        order.Remove(DropoffJudgement.CheckInField);
        _orders.GetByIdAsync("order-001").Returns(order);

        var read = await DetailAsync(Function(), BuyerId);

        read.TryGetProperty("dropoffConfirmAt", out _).Should().BeFalse();
        read.GetProperty("dropoffDeadlineAt").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ANonParty_StillGets403()
    {
        _orders.GetByIdAsync("order-001").Returns(Order());

        var response = await Function().FunctionHandler(Detail("someone-else"), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().NotContain("dropoffConfirmAt");
    }

    [Fact]
    public void TheAdminView_AndAMapWithoutACaller_CarryNone()
    {
        OrderFunction.MapToOrderDto(Order()).DropoffConfirmAt.Should().BeNull();
        OrderFunction.MapToOrderDto(Order(), null, null, () => Timings).DropoffConfirmAt.Should().BeNull();
        OrderFunction.MapToOrderDto(Order(), null, BuyerId, () => Timings).DropoffConfirmAt.Should().Be(Expected);
    }

    private async Task<JsonElement> DetailAsync(OrderFunction function, string caller)
    {
        var response = await function.FunctionHandler(Detail(caller), _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("order").Clone();
    }

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

    /// <summary>A trucked order on the road whose trucker checked in at the drop-off before T8.</summary>
    private static Dictionary<string, object> Order(
        string judgement = DropoffJudgement.Outcome.Pending, string orderId = "order-001", string status = "in_transit") => new()
    {
        { "orderId", orderId }, { "listingId", "listing-001" },
        { "buyerId", BuyerId }, { "farmerId", FarmerId },
        { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
        { "tripId", "trip-001" }, { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" },
        { "cropType", "teff" }, { "quantity", "500" }, { "pricePerUnit", "45" },
        { "cropCost", "22500" }, { "commissionRate", "0.05" }, { "commissionAmount", "1125" },
        { "orderTotal", "23625" }, { "status", status }, { "truckerResponse", "accepted" },
        { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
        { DropoffJudgement.Field, judgement },
        { DropoffJudgement.DeadlineField, OrderCommitment.Iso(Received.AddHours(20)) },
        {
            DropoffJudgement.CheckInField, new Dictionary<string, object>
            {
                { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(Received) },
                { PickupJudgement.EntryDistanceChecked, true },
                { PickupJudgement.EntryLate, false }
            }
        },
        { "createdAt", "2026-11-17T08:00:00Z" }, { "updatedAt", "2026-11-20T08:00:00Z" }, { "version", "7" }
    };
}

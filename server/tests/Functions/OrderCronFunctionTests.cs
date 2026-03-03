using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class OrderCronFunctionTests
{
    private readonly IOrderRepository _mockOrderRepo;
    private readonly OrderCronFunction _function;
    private readonly TestLambdaContext _context;

    public OrderCronFunctionTests()
    {
        _mockOrderRepo = Substitute.For<IOrderRepository>();
        _function = new OrderCronFunction(_mockOrderRepo);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeRequest(string action)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = $"{{\"action\":\"{action}\"}}"
        };
    }

    [Fact]
    public async Task HandleAutoCancelExpired_NoExpiredOrders_Returns200()
    {
        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"0\"", response.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_CancelsExpiredCreatedOrders()
    {
        var expiredOrder = new Dictionary<string, object>
        {
            { "orderId", "order-expired" },
            { "status", "created" },
            { "version", "1" },
            { "createdAt", DateTime.UtcNow.AddHours(-25).ToString("o") }
        };

        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>> { expiredOrder });
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());

        _mockOrderRepo.UpdateAsync("order-expired", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-expired" },
                { "status", "cancelled" }
            }));

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"1\"", response.Body);
    }

    [Fact]
    public async Task HandleAutoCancelExpired_CancelsExpiredPendingPaymentOrders()
    {
        var expiredOrder = new Dictionary<string, object>
        {
            { "orderId", "order-pending-expired" },
            { "status", "pending_payment" },
            { "version", "2" },
            { "createdAt", DateTime.UtcNow.AddHours(-30).ToString("o") }
        };

        _mockOrderRepo.QueryByStatusForCronAsync("created", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _mockOrderRepo.QueryByStatusForCronAsync("pending_payment", Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>> { expiredOrder });

        _mockOrderRepo.UpdateAsync("order-pending-expired", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-pending-expired" },
                { "status", "cancelled" }
            }));

        var request = MakeRequest("auto_cancel_expired");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"cancelled\":\"1\"", response.Body);
    }

    [Fact]
    public async Task Handle_MissingAction_Returns400()
    {
        var request = new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{}" };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ORDER_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task Handle_UnknownAction_Returns400()
    {
        var request = MakeRequest("unknown_action");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ORDER_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task Handle_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { HttpMethod = "POST", Body = "not json" };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }
}

using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>Tests for the rating entity handler in SyncFunction.</summary>
public class SyncFunctionRatingTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IRatingRepository _ratings = Substitute.For<IRatingRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionRatingTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _ratings.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object>
                {
                    { "ratingId", "order-1:farmer-1" },
                    { "version", "1" },
                }));
        _fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries",
            orderRepo: _orders, ratingRepo: _ratings);
    }

    private void OrderReturns(string status = "delivered", string buyerId = "buyer-1",
        string farmerId = "farmer-1", string? truckerId = "trucker-1")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-1" },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
            { "status", status },
        };
        if (truckerId != null)
        {
            order["truckerId"] = truckerId;
        }

        _orders.GetByIdAsync("order-1").Returns(order);
    }

    private static APIGatewayProxyRequest Req(SyncOperationRequest op, string userId, string? role)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        var body = new SyncRequest
        {
            SyncBatchId = "b1",
            ClientId = "d1",
            Operations = new List<SyncOperationRequest> { op },
        };
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static SyncOperationRequest RatingOp(
        string rateeId = "farmer-1",
        string rateeRole = "farmer",
        string raterId = "buyer-1",
        int stars = 5,
        string? ratingId = null)
    {
        ratingId ??= $"order-1:{rateeId}";
        return new SyncOperationRequest
        {
            OperationId = "op-rating",
            IdempotencyKey = $"{raterId}:rating:{ratingId}:create:1",
            EntityType = "rating",
            EntityId = ratingId,
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "ratingId", ratingId },
                { "orderId", "order-1" },
                { "raterId", raterId },
                { "rateeId", rateeId },
                { "rateeRole", rateeRole },
                { "stars", stars },
            },
            Timestamp = "2026-07-09T10:00:00Z",
        };
    }

    [Fact]
    public async Task BuyerRatesFarmer_Succeeds()
    {
        OrderReturns();

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1",
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task NonBuyerCannotRate()
    {
        OrderReturns();

        var response = await _fn.FunctionHandler(Req(RatingOp(), "farmer-1", "farmer"), _ctx);

        Assert.Contains("RATING_UNAUTHORIZED", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingBeforeDelivery_IsRejected()
    {
        OrderReturns(status: "created");

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("disbursing")]
    [InlineData("settled")]
    public async Task RatingADeliveredOrder_IsAllowedWhileItsMoneyIsPaidOutAndAfter(string status)
    {
        // ESC-09: the order follows the money (delivered, disbursing, settled); disbursing can last
        // days in the manual payout queue, so the buyer may rate during it.
        OrderReturns(status: status);

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("dispute_resolved")]
    [InlineData("accepted")]
    public async Task RatingAnOrderNotDelivered_IsRejected_WithTheStatusesThatAllowIt(string status)
    {
        OrderReturns(status: status);

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN: You can only rate a delivered order (delivered, disbursing or settled).", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingOnDisputedOrder_IsRejected()
    {
        // SIM-12: a dispute (escrow or delivery) moves the order to disputed, which keeps
        // ratings blocked until the dispute is resolved.
        OrderReturns(status: "disputed");

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingSomeoneElsesOrder_IsRejected()
    {
        OrderReturns(buyerId: "buyer-2");

        var response = await _fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task InvalidStars_IsRejected()
    {
        OrderReturns();

        var response = await _fn.FunctionHandler(Req(RatingOp(stars: 6), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_VALIDATION", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RateeNotOnOrder_IsRejected()
    {
        OrderReturns();

        var response = await _fn.FunctionHandler(
            Req(RatingOp(rateeId: "stranger-9"), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_VALIDATION", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }
}

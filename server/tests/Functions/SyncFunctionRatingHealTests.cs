using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-162: a rating for a farmer or trucker whose users row has no <c>ratingsAggregatedAt</c>
/// stamps their aggregate after the create; a stamped row, a ratee in another role and a refused
/// create do not.
/// </summary>
public class SyncFunctionRatingHealTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IRatingRepository _ratings = Substitute.For<IRatingRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRatingAggregateHeal _heal = Substitute.For<IRatingAggregateHeal>();
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionRatingHealTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _ratings.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "ratingId", "order-1:farmer-1" }, { "version", "1" } }));
        _orders.GetByIdAsync("order-1").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { "truckerId", "trucker-1" },
            { "status", "settled" },
        });
    }

    private SyncFunction Fn(IUserRepository? users = null) =>
        new(_meta, _crop, _ddb, "trips", "deliveries", userRepo: users ?? _users, orderRepo: _orders, ratingRepo: _ratings)
        {
            RatingHeal = _heal
        };

    private void Ratee(string userId, string role, string? aggregatedAt) =>
        _users.GetUserByIdAsync(userId).Returns(new UserDto
        {
            UserId = userId,
            PhoneNumber = "+251900000002",
            Role = role,
            Woreda = "",
            IsPhoneVerified = true,
            RatingsAggregatedAt = aggregatedAt,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1,
        });

    private static APIGatewayProxyRequest Rate(string rateeId = "farmer-1", string rateeRole = "farmer")
    {
        var ratingId = $"order-1:{rateeId}";
        var body = new SyncRequest
        {
            SyncBatchId = "b1",
            ClientId = "d1",
            Operations = new List<SyncOperationRequest>
            {
                new()
                {
                    OperationId = "op-rating",
                    IdempotencyKey = $"buyer-1:rating:{ratingId}:create:1",
                    EntityType = "rating",
                    EntityId = ratingId,
                    Action = "create",
                    Data = new Dictionary<string, object>
                    {
                        { "ratingId", ratingId },
                        { "orderId", "order-1" },
                        { "raterId", "buyer-1" },
                        { "rateeId", rateeId },
                        { "rateeRole", rateeRole },
                        { "stars", 4 },
                    },
                    Timestamp = "2026-11-12T10:00:00Z",
                },
            },
        };
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", "buyer-1" }, { "custom:role", "buyer" } }
            },
        };
    }

    [Theory]
    [InlineData("farmer-1", "farmer")]
    [InlineData("trucker-1", "trucker")]
    public async Task ARatingForAnUnstampedRatee_StampsTheirAggregate(string rateeId, string role)
    {
        Ratee(rateeId, role, aggregatedAt: null);

        var response = await Fn().FunctionHandler(Rate(rateeId, role), _ctx);

        Assert.Contains("success", response.Body);
        await _heal.Received(1).TryStampAsync(rateeId, role, Arg.Any<ILambdaLogger>());
    }

    [Fact]
    public async Task AStampedRatee_IsLeftAlone()
    {
        Ratee("farmer-1", "farmer", aggregatedAt: "2026-11-07T00:00:00.000Z");

        await Fn().FunctionHandler(Rate(), _ctx);

        await _heal.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILambdaLogger>());
    }

    [Fact]
    public async Task ARateeNowInAnotherRole_IsLeftAlone()
    {
        // The create put the rating outside the aggregate (inAggregate false); the heal's role condition would refuse anyway.
        Ratee("farmer-1", "trucker", aggregatedAt: null);

        await Fn().FunctionHandler(Rate(), _ctx);

        await _heal.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILambdaLogger>());
    }

    [Fact]
    public async Task ARatingThatWasNotCreated_HealsNothing()
    {
        Ratee("farmer-1", "farmer", aggregatedAt: null);
        _ratings.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Rating already exists.", "ratingId"));

        var response = await Fn().FunctionHandler(Rate(), _ctx);

        Assert.Contains("conflict", response.Body);
        await _heal.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILambdaLogger>());
    }

    [Fact]
    public async Task AHealThatStampsNothing_LeavesTheRatingASuccess()
    {
        Ratee("farmer-1", "farmer", aggregatedAt: null);
        _heal.TryStampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ILambdaLogger>()).Returns(false);

        var response = await Fn().FunctionHandler(Rate(), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task TheHeal_RunsBeforeTheReputationRefresh_SoTheRefreshReadsTheStamp()
    {
        Ratee("farmer-1", "farmer", aggregatedAt: null);
        var order = new List<string>();
        _heal.TryStampAsync("farmer-1", "farmer", Arg.Any<ILambdaLogger>()).Returns(_ =>
        {
            order.Add("heal");
            return true;
        });
        var events = Substitute.For<IReputationEventRepository>();
        events.GetUserStateAsync("farmer-1").Returns(_ =>
        {
            order.Add("refresh"); // the refresh's first read: the users row
            return (ReputationUserState?)null;
        });
        var fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries", userRepo: _users, orderRepo: _orders, ratingRepo: _ratings)
        {
            RatingHeal = _heal,
            Reputation = new ReputationService(events, ReputationPolicySettings.Defaults)
        };

        var response = await fn.FunctionHandler(Rate(), _ctx);

        Assert.Contains("success", response.Body);
        Assert.Equal(new[] { "heal", "refresh" }, order);
    }
}

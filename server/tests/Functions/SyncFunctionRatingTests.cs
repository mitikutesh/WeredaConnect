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

    // --- ACC-02: no rating for a deleted account ---

    private SyncFunction WithRatee(string? deletionState)
    {
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("farmer-1").Returns(new UserDto
        {
            UserId = "farmer-1",
            PhoneNumber = deletionState == null ? "+251900000002" : "",
            Role = "farmer",
            Woreda = deletionState == null ? "Adama" : "",
            IsPhoneVerified = deletionState == null,
            DeletionState = deletionState,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1,
        });
        return new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries",
            userRepo: users, orderRepo: _orders, ratingRepo: _ratings);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    public async Task RatingForAnAccountBeingOrAlreadyDeleted_IsRefused(string state)
    {
        OrderReturns(status: "settled");

        var response = await WithRatee(state).FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("SYNC_VALIDATION", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingForALiveAccount_StillSucceeds_WithTheUsersTable()
    {
        OrderReturns(status: "settled");

        var response = await WithRatee(null).FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    // Persona sim 2026-11-19, BUG-3: a buyer account on the farmer's own phone bought from that
    // farmer and left a public 5 star rating. The rating is stored, but outside the aggregate.

    private static UserDto User(string userId, string role, string phone, string? faydaId = null, params string[] devices) => new()
    {
        UserId = userId,
        PhoneNumber = phone,
        Role = role,
        Woreda = "Debre Birhan",
        IsPhoneVerified = true,
        FaydaId = faydaId,
        KnownDevices = devices.ToList(),
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1,
    };

    private SyncFunction WithParties(UserDto rater, UserDto ratee)
    {
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync(rater.UserId).Returns(rater);
        users.GetUserByIdAsync(ratee.UserId).Returns(ratee);
        _ratings.CreateOutsideAggregateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<string>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "ratingId", "order-1:farmer-1" }, { "version", "1" } }));
        return new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries",
            userRepo: users, orderRepo: _orders, ratingRepo: _ratings);
    }

    [Fact]
    public async Task RatingBetweenAccountsOnOneDevice_IsStoredOutsideTheAggregate_AndAnsweredLikeAnyOther()
    {
        OrderReturns(status: "settled");
        var fn = WithParties(
            User("buyer-1", "buyer", "+251900000001", null, "device-other", "device-shared"),
            User("farmer-1", "farmer", "+251900000002", null, "device-shared"));

        var linked = await fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        await _ratings.Received(1).CreateOutsideAggregateAsync(
            "order-1:farmer-1", Arg.Any<Dictionary<string, object>>(), "linked_accounts");
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());

        // No hint to the rater: the answer is byte for byte the one a counted rating gets.
        var counted = await WithParties(
                User("buyer-1", "buyer", "+251900000001", null, "device-a"),
                User("farmer-1", "farmer", "+251900000002", null, "device-b"))
            .FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);
        Assert.Contains("success", linked.Body);
        Assert.Equal(counted.Body, linked.Body);
    }

    [Theory]
    [InlineData("+251900000002", "FAYDA-9", "FAYDA-9")] // one Fayda ID on both rows
    [InlineData("+251900000001", null, null)] // one phone number on both rows
    public async Task RatingBetweenTwoAccountsOfOneIdentity_IsStoredOutsideTheAggregate(
        string farmerPhone, string? buyerFayda, string? farmerFayda)
    {
        OrderReturns(status: "delivered");
        var fn = WithParties(
            User("buyer-1", "buyer", "+251900000001", buyerFayda),
            User("farmer-1", "farmer", farmerPhone, farmerFayda));

        var response = await fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateOutsideAggregateAsync(
            "order-1:farmer-1", Arg.Any<Dictionary<string, object>>(), "linked_accounts");
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingBetweenUnrelatedAccounts_StillCounts()
    {
        OrderReturns(status: "settled");
        var fn = WithParties(
            User("buyer-1", "buyer", "+251900000001", "FAYDA-1", "device-a"),
            User("farmer-1", "farmer", "+251900000002", "FAYDA-2", "device-b"));

        var response = await fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
        await _ratings.DidNotReceive().CreateOutsideAggregateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RatingByAccountsThatNeverSentADeviceId_StillCounts()
    {
        // Apps from before X-Device-Id have no knownDevices: two empty lists share nothing.
        OrderReturns(status: "settled");
        var fn = WithParties(User("buyer-1", "buyer", "+251900000001"), User("farmer-1", "farmer", "+251900000002"));

        await fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingOfATruckerOnTheRatersDevice_IsStoredOutsideTheAggregateToo()
    {
        OrderReturns(status: "settled");
        var fn = WithParties(
            User("buyer-1", "buyer", "+251900000001", null, "device-shared"),
            User("trucker-1", "trucker", "+251900000003", null, "device-shared"));

        var response = await fn.FunctionHandler(
            Req(RatingOp(rateeId: "trucker-1", rateeRole: "trucker"), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateOutsideAggregateAsync(
            "order-1:trucker-1", Arg.Any<Dictionary<string, object>>(), "linked_accounts");
    }

    [Fact]
    public async Task ARaterRowThatCannotBeRead_CountsTheRating_AndNeverFailsIt()
    {
        OrderReturns(status: "settled");
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("farmer-1").Returns(User("farmer-1", "farmer", "+251900000002", null, "device-shared"));
        users.GetUserByIdAsync("buyer-1").Returns<UserDto?>(_ => throw new InvalidOperationException("users down"));
        var fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries",
            userRepo: users, orderRepo: _orders, ratingRepo: _ratings);

        var response = await fn.FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    // Persona sim 2026-11-19, BUG-1: the order read delivered while its escrow was disputed (a
    // dispute raised in transit), and the rating went through.

    private SyncFunction WithOrderEscrow(IEscrowRepository escrows)
        => new(_meta, _crop, _ddb, "trips", "deliveries",
            orderRepo: _orders, escrowRepo: escrows, ratingRepo: _ratings);

    private static IEscrowRepository EscrowIn(string status)
    {
        var escrows = Substitute.For<IEscrowRepository>();
        escrows.GetByIdAsync("escrow-order-order-1", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-order-1" }, { "orderId", "order-1" }, { "status", status },
        });
        return escrows;
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    public async Task RatingOfADeliveredOrder_WhoseEscrowIsInDispute_IsRefused(string escrowStatus)
    {
        OrderReturns(status: "delivered");

        var response = await WithOrderEscrow(EscrowIn(escrowStatus))
            .FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN: This order is in dispute and cannot be rated.", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("escrowed")]
    [InlineData("released")]
    public async Task RatingOfADeliveredOrder_WhoseEscrowIsNotInDispute_Succeeds(string escrowStatus)
    {
        OrderReturns(status: "delivered");

        var response = await WithOrderEscrow(EscrowIn(escrowStatus))
            .FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
        await _ratings.Received(1).CreateAsync("order-1:farmer-1", Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("dispute_resolved", false)]
    [InlineData("escrowed", true)]
    public async Task RatingOfADeliveredOrder_WhoseEscrowIsHeld_CountsByTheStatusItWasHeldFrom(string heldFrom, bool rated)
    {
        OrderReturns(status: "delivered");
        var escrows = EscrowIn("held");
        (await escrows.GetByIdAsync("escrow-order-order-1", true))!["heldFromStatus"] = heldFrom;

        var response = await WithOrderEscrow(escrows).FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Equal(!rated, response.Body.Contains("RATING_FORBIDDEN: This order is in dispute"));
        await _ratings.Received(rated ? 1 : 0).CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingOfADeliveredOrder_WhoseMoneyIsADisputedDeliveryEscrow_IsRefused()
    {
        // No order escrow: the money was paid through POST /escrow/initiate and is found by the delivery.
        OrderReturns(status: "delivered");
        (await _orders.GetByIdAsync("order-1"))!["activeDeliveryId"] = "del-1";
        var escrows = Substitute.For<IEscrowRepository>();
        escrows.GetByDeliveryIdAsync("del-1").Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-abc" }, { "deliveryId", "del-1" }, { "status", "disputed" },
        });

        var response = await WithOrderEscrow(escrows).FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("RATING_FORBIDDEN: This order is in dispute and cannot be rated.", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task RatingOfADeliveredOrder_WithoutAnEscrowRecord_Succeeds()
    {
        // A cash order, or one from before order escrows: nothing says it is in dispute.
        OrderReturns(status: "delivered");

        var response = await WithOrderEscrow(Substitute.For<IEscrowRepository>())
            .FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task RatingOfADeliveredOrder_WhoseEscrowCannotBeRead_IsRefusedForNow_WithoutACode()
    {
        // Unprefixed, so the clients retry it: RATING_FORBIDDEN would be permanent.
        OrderReturns(status: "delivered");
        var escrows = Substitute.For<IEscrowRepository>();
        escrows.GetByIdAsync("escrow-order-order-1", true)
            .Returns<Dictionary<string, object>?>(_ => throw new InvalidOperationException("throttled"));

        var response = await WithOrderEscrow(escrows).FunctionHandler(Req(RatingOp(), "buyer-1", "buyer"), _ctx);

        Assert.Contains("\"status\":\"error\"", response.Body);
        Assert.Contains("payment could not be read. Please try again later.", response.Body);
        Assert.DoesNotContain("RATING_", response.Body);
        await _ratings.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }
}

using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-04 (fraud-detection-architecture.md 17, 17.1, 17.3, 17.4): the buyer's risk tier on the two REST payment
/// paths. <c>POST /escrow/initiate</c>: role, tier, CRITICAL (<c>403 FRAUD_ACCOUNT_HELD</c>), the tiered step-up,
/// then velocity. <c>POST /escrow/order-checkout</c>: role and body, CRITICAL before the velocity count and
/// before any Telebirr call; MEDIUM and HIGH are not asked again there (the step-up ran at payment_submitted).
/// </summary>
public class EscrowFunctionRiskGateTests
{
    private const string BuyerId = "buyer-456";
    private const string FarmerId = "farmer-123";
    private const string AccountHeldMessage = "Your account is under review. Some actions are paused. Contact support.";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkouts = Substitute.For<IOrderCheckoutRepository>();
    private readonly TestLambdaContext _context = new();

    public EscrowFunctionRiskGateTests()
    {
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low));
        // Initiate stops at the velocity gate, the last one before the service: a request that gets there
        // passed the tier and the step-up.
        _fraud.CheckPaymentVelocityAsync(BuyerId)
            .Returns(FraudCheckResult.Rejected("FRAUD_VELOCITY_EXCEEDED", "Too many payments."));
        _users.GetUserByIdAsync(BuyerId).Returns(Buyer(faydaAuthenticatedAt: null));

        // Order checkout: a payment_submitted order of the buyer, a live gateway with a hosted checkout.
        _orders.GetByIdAsync("order-1", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "orderTotal", "1500" }, { "status", "payment_submitted" }, { "version", "2" }
        });
        _checkouts.TryOpenSlotAsync(Arg.Any<OrderCheckoutSlot>(), Arg.Any<int?>()).Returns(true);
        _checkouts.StoreMappingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(true);
        _gateway.SupportsHostedCheckout.Returns(true);
        _gateway.HoldFundsAsync(Arg.Any<HoldFundsRequest>()).Returns(new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = "PENDING-WEBHOOK-1",
            ToPayUrl = "https://telebirr.example/checkout?prepay_id=prepay-1",
            PrepayId = "prepay-1"
        });
    }

    private EscrowFunction Function(bool withFraudService = true, bool withUsers = true)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway,
            orderRepo: _orders, orderCheckoutRepo: _checkouts);
        return new EscrowFunction(service, withFraudService ? _fraud : null, withUsers ? _users : null);
    }

    private static RiskScoreResult Scored(RiskTier tier) => new()
    {
        Score = tier switch { RiskTier.Medium => 30, RiskTier.High => 60, RiskTier.Critical => 100, _ => 0 },
        EnforcementScore = tier == RiskTier.Critical ? 100 : 0,
        Tier = tier
    };

    private static UserDto Buyer(string? faydaAuthenticatedAt) => new()
    {
        UserId = BuyerId,
        PhoneNumber = "+251900000001",
        Role = "buyer",
        Woreda = "Jimma",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaAuthenticatedAt = faydaAuthenticatedAt,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private void BuyerIs(RiskScoreResult score) => _fraud.GetUserRiskScoreAsync(BuyerId).Returns(score);

    private void FreshFaydaLogin() =>
        _users.GetUserByIdAsync(BuyerId).Returns(Buyer(DateTime.UtcNow.AddMinutes(-1).ToString("o")));

    private static long FreshPassword => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long StalePassword => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

    private static APIGatewayProxyRequest Post(string path, string body, long? authTime = null, string role = "buyer")
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", BuyerId }, { "custom:role", role } };
        if (authTime != null)
        {
            authorizer["auth_time"] = authTime.Value.ToString();
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = path,
            Body = body,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static APIGatewayProxyRequest Initiate(decimal amount, long? authTime, string role = "buyer") => Post("/escrow/initiate",
        JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = "del-001",
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = amount,
            Currency = "ETB",
            IdempotencyKey = $"{BuyerId}:del-001:initiate:1708084800777"
        }, AppJsonContext.Default.InitiateEscrowRequest), authTime, role);

    private static APIGatewayProxyRequest Checkout() => Post("/escrow/order-checkout", """{"orderId":"order-1"}""");

    private static string ErrorCode(APIGatewayProxyResponse response)
        => JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    private static void AssertAccountHeld(APIGatewayProxyResponse response)
    {
        Assert.Equal(403, response.StatusCode);
        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");
        Assert.Equal("FRAUD_ACCOUNT_HELD", error.GetProperty("code").GetString());
        Assert.Equal(AccountHeldMessage, error.GetProperty("message").GetString());
    }

    /// <summary>The request passed the tier and the step-up and stopped at the velocity gate (stubbed to refuse).</summary>
    private static void AssertReachedVelocity(APIGatewayProxyResponse response)
    {
        Assert.Equal(429, response.StatusCode);
        Assert.Equal("ESCROW_011", ErrorCode(response));
    }

    // ===== POST /escrow/initiate =====

    [Theory]
    [InlineData(2_000)]
    [InlineData(25_000)]
    public async Task Initiate_Critical_Is403BeforeTheStepUpAndTheVelocity_NothingHeld(int amount)
    {
        BuyerIs(Scored(RiskTier.Critical));
        FreshFaydaLogin();

        var response = await Function().FunctionHandler(Initiate(amount, FreshPassword), _context);

        AssertAccountHeld(response);
        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);
        await _fraud.DidNotReceiveWithAnyArgs().CheckPaymentVelocityAsync(default!);
        await _fraud.DidNotReceiveWithAnyArgs().RecordPaymentInitiatedAsync(default!);
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
        await _escrows.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        await _idempotency.DidNotReceiveWithAnyArgs().StoreAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Initiate_Critical_TheRoleCheckStillComesFirst()
    {
        BuyerIs(Scored(RiskTier.Critical));

        var response = await Function().FunctionHandler(Initiate(2_000, FreshPassword, role: "farmer"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("ESCROW_025", ErrorCode(response));
        await _fraud.DidNotReceiveWithAnyArgs().GetUserRiskScoreAsync(default!);
    }

    [Fact]
    public async Task Initiate_Low_IsTheAmountTierAsBefore()
    {
        var function = Function();

        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(2_000, StalePassword), _context)));
        AssertReachedVelocity(await function.FunctionHandler(Initiate(2_000, FreshPassword), _context));
        // Up to 10,000 ETB a LOW buyer's profile is not read for the step-up.
        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);

        // Above it a fresh Fayda login alone pays, as today.
        FreshFaydaLogin();
        AssertReachedVelocity(await function.FunctionHandler(Initiate(25_000, null), _context));
    }

    [Fact]
    public async Task Initiate_Medium_UpTo10K_IsThePasswordAsForLow()
    {
        BuyerIs(Scored(RiskTier.Medium));
        var function = Function();

        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(2_000, StalePassword), _context)));
        AssertReachedVelocity(await function.FunctionHandler(Initiate(2_000, FreshPassword), _context));
    }

    [Fact]
    public async Task Initiate_Medium_Above10K_NeedsTheFaydaLoginAndThenThePassword()
    {
        BuyerIs(Scored(RiskTier.Medium));
        var function = Function();

        Assert.Equal("AUTH_041", ErrorCode(await function.FunctionHandler(Initiate(25_000, FreshPassword), _context)));
        FreshFaydaLogin();
        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(25_000, null), _context)));
        AssertReachedVelocity(await function.FunctionHandler(Initiate(25_000, FreshPassword), _context));
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(25_000)]
    public async Task Initiate_High_AtAnyAmount_NeedsTheFaydaLoginFirst_ThenThePassword(int amount)
    {
        BuyerIs(Scored(RiskTier.High));
        var function = Function();

        var first = await function.FunctionHandler(Initiate(amount, FreshPassword), _context);
        Assert.Equal(403, first.StatusCode);
        Assert.Equal("AUTH_041", ErrorCode(first));
        // The amount-neutral text (the JSON escapes the apostrophe).
        Assert.Contains("you with Fayda to continue with this payment.", first.Body);
        // Below 10,000 ETB too, the buyer's last Fayda login is read.
        await _users.Received().GetUserByIdAsync(BuyerId);

        FreshFaydaLogin();
        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(amount, StalePassword), _context)));
        AssertReachedVelocity(await function.FunctionHandler(Initiate(amount, FreshPassword), _context));
    }

    [Fact]
    public async Task Initiate_High_WithoutTheUsersTable_FailsClosedOnFayda()
    {
        BuyerIs(Scored(RiskTier.High));

        var response = await Function(withUsers: false).FunctionHandler(Initiate(2_000, FreshPassword), _context);

        Assert.Equal("AUTH_041", ErrorCode(response));
    }

    [Theory]
    [InlineData(RiskScoreSource.Unavailable)]
    [InlineData(RiskScoreSource.TableNotConfigured)]
    public async Task Initiate_AnUnscoredBuyer_IsLow_TheBaselineStepUpStillApplies(RiskScoreSource source)
    {
        BuyerIs(RiskScoreResult.Unscored(source));
        var function = Function();

        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(2_000, StalePassword), _context)));
        AssertReachedVelocity(await function.FunctionHandler(Initiate(2_000, FreshPassword), _context));
    }

    [Fact]
    public async Task Initiate_WithoutAFraudService_HasNoTier_AndKeepsTodaysStepUp()
    {
        // No RATE_LIMITS_TABLE: no fraud service, so no tier and no velocity check (LOW, section 14.4).
        var function = Function(withFraudService: false);

        Assert.Equal("AUTH_040", ErrorCode(await function.FunctionHandler(Initiate(2_000, StalePassword), _context)));
        var fresh = await function.FunctionHandler(Initiate(2_000, FreshPassword), _context);
        Assert.DoesNotContain("FRAUD_ACCOUNT_HELD", fresh.Body);
        Assert.DoesNotContain("AUTH_04", fresh.Body);
    }

    // ===== POST /escrow/order-checkout =====

    [Fact]
    public async Task OrderCheckout_Critical_Is403BeforeTheVelocityAndAnyTelebirrCall()
    {
        BuyerIs(Scored(RiskTier.Critical));

        var response = await Function().FunctionHandler(Checkout(), _context);

        AssertAccountHeld(response);
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
        await _fraud.DidNotReceiveWithAnyArgs().CheckPaymentVelocityAsync(default!);
        await _fraud.DidNotReceiveWithAnyArgs().RecordPaymentInitiatedAsync(default!);
        await _orders.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default);
        await _checkouts.DidNotReceiveWithAnyArgs().TryOpenSlotAsync(default!, default);
    }

    [Fact]
    public async Task OrderCheckout_Critical_ABadBodyIsStillA400First()
    {
        BuyerIs(Scored(RiskTier.Critical));

        var response = await Function().FunctionHandler(Post("/escrow/order-checkout", "{}"), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("ESCROW_033", ErrorCode(response));
    }

    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    public async Task OrderCheckout_BelowCritical_OpensTheCheckoutAsBefore_NoStepUpHere(RiskTier tier)
    {
        BuyerIs(Scored(tier));
        _fraud.CheckPaymentVelocityAsync(BuyerId).Returns(FraudCheckResult.Allowed());

        // No auth_time and no Fayda login: the step-up ran at payment_submitted.
        var response = await Function().FunctionHandler(Checkout(), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("prepay_id=prepay-1", response.Body);
        await _gateway.Received(1).HoldFundsAsync(Arg.Any<HoldFundsRequest>());
    }

    // ===== FRD-08: ESCROW_025 records ROLE_FORBIDDEN on the caller =====

    [Fact]
    public async Task Initiate_NonBuyerRole_WritesOneRoleForbiddenOnTheCaller()
    {
        var response = await Function().FunctionHandler(Initiate(2_000, FreshPassword, role: "farmer"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("ESCROW_025", ErrorCode(response));
        await _fraud.Received(1).RecordFraudSignalAsync(
            BuyerId, FraudDetectionService.RoleForbiddenSignal, "del-001", "escrow", Arg.Any<string?>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            FarmerId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task OrderCheckout_NonBuyerRole_WritesOneRoleForbiddenOnTheCaller()
    {
        var response = await Function().FunctionHandler(
            Post("/escrow/order-checkout", """{"orderId":"order-1"}""", role: "trucker"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("ESCROW_025", ErrorCode(response));
        await _fraud.Received(1).RecordFraudSignalAsync(
            BuyerId, FraudDetectionService.RoleForbiddenSignal, Arg.Is<string?>(e => e == null), "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task Initiate_WithoutTheRoleClaim_IsStill403AndWritesNothing()
    {
        // An access token instead of the ID token: a client set-up fault, not a probe.
        var request = Initiate(2_000, FreshPassword);
        request.RequestContext.Authorizer.Remove("custom:role");

        var response = await Function().FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("ESCROW_025", ErrorCode(response));
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task Initiate_NonBuyerRole_AFailingSignalWriteLeavesThe403Unchanged()
    {
        var working = await Function().FunctionHandler(Initiate(2_000, FreshPassword, role: "farmer"), _context);
        _fraud.RecordFraudSignalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("table down"));

        var broken = await Function().FunctionHandler(Initiate(2_000, FreshPassword, role: "farmer"), _context);

        Assert.Equal(working.StatusCode, broken.StatusCode);
        Assert.Equal(working.Body, broken.Body);
    }
}

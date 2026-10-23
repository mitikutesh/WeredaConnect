using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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
/// FRD-04 (fraud-detection-architecture.md 17, 17.1, 17.3, 17.4): the caller's risk tier on <c>/sync</c>. CRITICAL
/// refuses the farmer's crop create, the trucker's trip create, the buyer's order create and the buyer's
/// <c>payment_submitted</c> with <c>FRAUD_ACCOUNT_HELD</c> and writes nothing; MEDIUM and HIGH add proofs to the
/// payment step-up; LOW, an unconfigured signals table and a failed score read keep today's behaviour.
/// </summary>
public class SyncFunctionRiskGateTests
{
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string AccountHeld =
        "FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionRiskGateTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        foreach (var id in new[] { FarmerId, TruckerId, BuyerId })
        {
            _users.GetUserByIdAsync(id).Returns(Profile(id));
        }

        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low));
        _fraud.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(FraudCheckResult.Allowed());

        _crops.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", ci.ArgAt<string>(0) }, { "version", "1" } }));
        // The listing the order create binds to: the farmer's teff, 10 quintals at 45.5 ETB/kg.
        _crops.GetByIdAsync("listing-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "listingId", "listing-001" }, { "farmerId", FarmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" }, { "quantity", 10.0 },
            { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _orders.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", ci.ArgAt<string>(0) }, { "status", "created" }, { "version", "1" }
            }));
        _orders.UpdateAsync("order-pay", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-pay" }, { "status", "payment_submitted" }, { "version", "2" }
            }));
    }

    private SyncFunction Function(bool withFraudService = true) => new(
        _meta, _crops, _ddb, "trips", "deliveries",
        fraudService: withFraudService ? _fraud : null, userRepo: _users, orderRepo: _orders);

    private static UserDto Profile(string id, string? faydaAuthenticatedAt = null) => new()
    {
        UserId = id,
        PhoneNumber = "+251933333333",
        DisplayName = "Abebe Kebede",
        Role = "buyer",
        Woreda = "Jimma",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaAuthenticatedAt = faydaAuthenticatedAt,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    private static RiskScoreResult Scored(RiskTier tier, bool truncated = false) => new()
    {
        Score = tier switch { RiskTier.Medium => 30, RiskTier.High => 60, RiskTier.Critical => 100, _ => 0 },
        EnforcementScore = tier switch { RiskTier.Critical => 100, _ => 0 },
        Tier = tier,
        Truncated = truncated
    };

    private void CallerIs(string userId, RiskScoreResult score) => _fraud.GetUserRiskScoreAsync(userId).Returns(score);

    private static APIGatewayProxyRequest Request(string userId, string role, long? authTime, params SyncOperationRequest[] ops)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } };
        if (authTime != null)
        {
            authorizer["auth_time"] = authTime.Value.ToString();
        }

        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-1",
                ClientId = "device-1",
                Operations = ops.ToList()
            }, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static long FreshPassword => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long StalePassword => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
    private static string FreshFayda => DateTime.UtcNow.AddMinutes(-1).ToString("o");

    private static SyncOperationRequest CropCreate(string entityId = "crop-001") => new()
    {
        OperationId = $"op-{entityId}",
        IdempotencyKey = $"user:crop_listing:{entityId}:create:12345",
        EntityType = "crop_listing",
        EntityId = entityId,
        Action = "create",
        Data = new Dictionary<string, object>
        {
            { "farmerId", FarmerId }, { "cropType", "teff" }, { "quantity", 50 }, { "pricePerKg", 45.5 },
            { "woreda", "Jimma" }, { "locationName", "Jimma" }, { "description", "White teff" }
        }
    };

    private static SyncOperationRequest TripCreate() => new()
    {
        OperationId = "op-trip",
        IdempotencyKey = "user:trip:trip-001:create:12345",
        EntityType = "trip",
        EntityId = "trip-001",
        Action = "create",
        Data = new Dictionary<string, object>
        {
            { "truckerId", TruckerId }, { "fromWoreda", "Bishoftu" }, { "toWoreda", "Addis Ababa" },
            { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") }, { "capacity", 150.0 },
            { "pricePerQuintal", 120.0 }
        }
    };

    private static SyncOperationRequest OrderCreate() => new()
    {
        OperationId = "op-order",
        IdempotencyKey = "user:order:order-001:create:12345",
        EntityType = "order",
        EntityId = "order-001",
        Action = "create",
        Data = new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", "listing-001" },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" }, { "cropType", "teff" },
            { "quantity", 50 }, { "pricePerUnit", 45.5 }
        }
    };

    private static SyncOperationRequest Payment() => new()
    {
        OperationId = "op-pay",
        IdempotencyKey = "buyer-001:order:order-pay:update:12345",
        EntityType = "order",
        EntityId = "order-pay",
        Action = "update",
        Data = new Dictionary<string, object>
        {
            { "status", "payment_submitted" }, { "paymentMethod", "telebirr" }, { "version", 1 }
        }
    };

    private void PaymentOrderOf(decimal orderTotal) => _orders.GetByIdAsync("order-pay", true).Returns(
        new Dictionary<string, object>
        {
            { "orderId", "order-pay" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", "created" },
            { "orderTotal", orderTotal }, { "paymentMethod", "cbe_transfer" }, { "version", "1" }
        });

    private async Task<string> SyncAsync(SyncFunction function, APIGatewayProxyRequest request)
    {
        var response = await function.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    private async Task NothingStoredAsync()
        => await _meta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

    // ===== Creates: farmer crop, trucker trip, buyer order =====

    [Fact]
    public async Task Farmer_CropCreate_Critical_IsRefusedBeforeTheListingChecks_NothingWritten()
    {
        CallerIs(FarmerId, Scored(RiskTier.Critical));

        var body = await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        Assert.Contains(AccountHeld, body);
        await _crops.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
        // No velocity read, no duplicate query, no fraud signal for a held account.
        await _fraud.DidNotReceiveWithAnyArgs().CheckListingCreationAsync(default!, default!, default);
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
        await _fraud.DidNotReceiveWithAnyArgs().RecordListingCreatedAsync(default!);
        await NothingStoredAsync();
    }

    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    public async Task Farmer_CropCreate_BelowCritical_IsCreatedAsBefore(RiskTier tier)
    {
        CallerIs(FarmerId, Scored(tier));

        var body = await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _crops.Received(1).CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Trucker_TripCreate_Critical_IsRefused_NothingWritten()
    {
        CallerIs(TruckerId, Scored(RiskTier.Critical));

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains(AccountHeld, body);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
        await NothingStoredAsync();
    }

    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    public async Task Trucker_TripCreate_BelowCritical_IsCreatedAsBefore(RiskTier tier)
    {
        CallerIs(TruckerId, Scored(tier));

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _ddb.Received(1).PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task Buyer_OrderCreate_Critical_IsRefusedBeforeTheListingRead_NothingWritten()
    {
        CallerIs(BuyerId, Scored(RiskTier.Critical));

        var body = await SyncAsync(Function(), Request(BuyerId, "buyer", null, OrderCreate()));

        Assert.Contains(AccountHeld, body);
        await _crops.DidNotReceive().GetByIdAsync("listing-001");
        await _orders.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
        await NothingStoredAsync();
    }

    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    public async Task Buyer_OrderCreate_BelowCritical_IsCreatedAsBefore(RiskTier tier)
    {
        CallerIs(BuyerId, Scored(tier));

        var body = await SyncAsync(Function(), Request(BuyerId, "buyer", null, OrderCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _orders.Received(1).CreateAsync("order-001", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ATruncatedRead_IsCritical()
    {
        CallerIs(FarmerId, Scored(RiskTier.Critical, truncated: true) with { Score = 15, EnforcementScore = 15 });

        var body = await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        Assert.Contains(AccountHeld, body);
    }

    [Theory]
    [InlineData(RiskScoreSource.Unavailable)]
    [InlineData(RiskScoreSource.TableNotConfigured)]
    public async Task AnUnscoredCaller_IsLowAtTheGate_AndCreatesAsBefore(RiskScoreSource source)
    {
        // Section 14.4: at a user gate a failed read or a missing table is LOW, not a refusal.
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(RiskScoreResult.Unscored(source));

        var crop = await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));
        var trip = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));
        var order = await SyncAsync(Function(), Request(BuyerId, "buyer", null, OrderCreate()));

        Assert.All(new[] { crop, trip, order }, body => Assert.Contains("\"status\":\"success\"", body));
    }

    [Fact]
    public async Task WithoutAFraudService_CreatesAsBefore()
    {
        var body = await SyncAsync(Function(withFraudService: false), Request(BuyerId, "buyer", null, OrderCreate()));

        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task TheScoreIsReadOncePerBatch_AndAgainForTheNextBatch()
    {
        CallerIs(FarmerId, Scored(RiskTier.Critical));
        var function = Function();

        var body = await SyncAsync(function, Request(FarmerId, "farmer", null, CropCreate("crop-1"), CropCreate("crop-2")));

        Assert.Equal(2, body.Split("FRAUD_ACCOUNT_HELD").Length - 1);
        await _fraud.Received(1).GetUserRiskScoreAsync(FarmerId);

        await SyncAsync(function, Request(FarmerId, "farmer", null, CropCreate("crop-3")));
        await _fraud.Received(2).GetUserRiskScoreAsync(FarmerId);
    }

    [Fact]
    public async Task OnlyTheCallersTierIsRead_NotTheCounterpartys()
    {
        // Section 17: a CRITICAL farmer does not stop a buyer's order (the settlement hold covers the money).
        CallerIs(FarmerId, Scored(RiskTier.Critical));

        var body = await SyncAsync(Function(), Request(BuyerId, "buyer", null, OrderCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _fraud.DidNotReceive().GetUserRiskScoreAsync(FarmerId);
    }

    // ===== payment_submitted =====

    [Theory]
    [InlineData(2_000)]
    [InlineData(25_000)]
    public async Task Payment_Critical_IsRefusedBeforeKyc_AtAnyAmount_NothingWritten(int total)
    {
        PaymentOrderOf(total);
        CallerIs(BuyerId, Scored(RiskTier.Critical));
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, FreshFayda));

        var body = await SyncAsync(Function(), Request(BuyerId, "buyer", FreshPassword, Payment()));

        Assert.Contains(AccountHeld, body);
        Assert.DoesNotContain("AUTH_04", body);
        // The CRITICAL check comes before KYC: no profile is read for the payment.
        await _users.DidNotReceiveWithAnyArgs().GetUserByIdAsync(default!);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await NothingStoredAsync();
    }

    [Fact]
    public async Task Payment_Low_IsTheAmountTierAsBefore()
    {
        PaymentOrderOf(2_000);
        var function = Function();

        var stale = await SyncAsync(function, Request(BuyerId, "buyer", StalePassword, Payment()));
        var fresh = await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment()));

        Assert.Contains("AUTH_040", stale);
        Assert.Contains("\"status\":\"success\"", fresh);

        // Above 10,000 ETB a fresh Fayda login alone pays a LOW buyer, as today.
        PaymentOrderOf(25_000);
        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, FreshFayda));
        var above = await SyncAsync(function, Request(BuyerId, "buyer", null, Payment()));
        Assert.Contains("\"status\":\"success\"", above);
    }

    [Fact]
    public async Task Payment_Medium_UpTo10K_IsThePasswordAsForLow()
    {
        PaymentOrderOf(2_000);
        CallerIs(BuyerId, Scored(RiskTier.Medium));
        var function = Function();

        Assert.Contains("AUTH_040", await SyncAsync(function, Request(BuyerId, "buyer", StalePassword, Payment())));
        Assert.Contains("\"status\":\"success\"", await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment())));
    }

    [Fact]
    public async Task Payment_Medium_Above10K_NeedsTheFaydaLoginAndThenThePassword()
    {
        PaymentOrderOf(25_000);
        CallerIs(BuyerId, Scored(RiskTier.Medium));
        var function = Function();

        Assert.Contains("AUTH_041", await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment())));

        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, FreshFayda));
        Assert.Contains("AUTH_040", await SyncAsync(function, Request(BuyerId, "buyer", null, Payment())));
        Assert.Contains("\"status\":\"success\"", await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment())));
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(25_000)]
    public async Task Payment_High_AtAnyAmount_NeedsTheFaydaLoginFirst_ThenThePassword(int total)
    {
        PaymentOrderOf(total);
        CallerIs(BuyerId, Scored(RiskTier.High));
        var function = Function();

        // A fresh password is not enough: Fayda first.
        var first = await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment()));
        Assert.Contains("AUTH_041", first);
        // The amount-neutral text (the JSON escapes the apostrophe).
        Assert.Contains("you with Fayda to continue with this payment.", first);

        _users.GetUserByIdAsync(BuyerId).Returns(Profile(BuyerId, FreshFayda));
        Assert.Contains("AUTH_040", await SyncAsync(function, Request(BuyerId, "buyer", StalePassword, Payment())));
        Assert.Contains("\"status\":\"success\"", await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment())));
    }

    [Fact]
    public async Task Payment_AnUnavailableScore_IsLow_TheBaselineStepUpStillApplies()
    {
        PaymentOrderOf(2_000);
        CallerIs(BuyerId, RiskScoreResult.Unscored(RiskScoreSource.Unavailable));
        var function = Function();

        Assert.Contains("AUTH_040", await SyncAsync(function, Request(BuyerId, "buyer", StalePassword, Payment())));
        Assert.Contains("\"status\":\"success\"", await SyncAsync(function, Request(BuyerId, "buyer", FreshPassword, Payment())));
    }

    [Fact]
    public async Task Payment_TheTierIsNeverInTheResponse()
    {
        PaymentOrderOf(2_000);
        CallerIs(BuyerId, Scored(RiskTier.High));

        var body = await SyncAsync(Function(), Request(BuyerId, "buyer", FreshPassword, Payment()));

        Assert.DoesNotContain("HIGH", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tier", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", body, StringComparison.OrdinalIgnoreCase);
    }
}

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
        _fraud.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<ListingLot?>())
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
            { "woreda", "Jimma" }, { "locationName", "Jimma" }, { "description", "White teff" },
            { "photoIds", CropPhotoFixtures.ValidPhotoIds() }
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
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

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

    // ===== The duplicate lot and the trip rules at the create (fraud spec Rules 1 and 6) =====

    private const string DuplicateTrip =
        "FRAUD_DUPLICATE_LISTING: You already have a trip on this route on that day. Please update your existing trip instead.";

    [Fact]
    public async Task Farmer_CropCreate_GivesTheListingChecksItsLot()
    {
        var create = CropCreate();
        create.Data!["latitude"] = 9.6797;
        create.Data["longitude"] = 39.5308;

        await SyncAsync(Function(), Request(FarmerId, "farmer", null, create));

        await _fraud.Received(1).CheckListingCreationAsync(FarmerId, "teff", 45.5m,
            Arg.Is<ListingLot?>(lot => lot != null && lot.CropId == "crop-001" && lot.Quantity == 50m
                && lot.Woreda == "Jimma" && lot.Latitude == 9.6797 && lot.Longitude == 39.5308));
    }

    [Fact]
    public async Task Farmer_CropCreate_WithoutAPoint_GivesTheLotByItsWoreda()
    {
        await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        await _fraud.Received(1).CheckListingCreationAsync(FarmerId, "teff", 45.5m,
            Arg.Is<ListingLot?>(lot => lot != null && lot.Quantity == 50m && lot.Woreda == "Jimma"
                && lot.Latitude == null && lot.Longitude == null));
    }

    [Fact]
    public async Task Farmer_CropCreate_OfALotAlreadyListed_IsRefused_RecordedUnderTheTypeThatNeverScores()
    {
        _fraud.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<ListingLot?>())
            .Returns(FraudCheckResult.Rejected(
                "FRAUD_DUPLICATE_LISTING", "A similar listing already exists. Please update your existing listing instead.")
                with
            { FlagReason = FraudDetectionService.DuplicateLotSignal });

        var body = await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        // The wire answer is the code and sentence clients know.
        Assert.Contains(
            "FRAUD_DUPLICATE_LISTING: A similar listing already exists. Please update your existing listing instead.", body);
        Assert.DoesNotContain("DUPLICATE_LOT", body);
        await _crops.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
        await _fraud.Received(1).RecordFraudSignalAsync(FarmerId, "DUPLICATE_LOT", "crop-001", "crop", Arg.Any<string?>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), "FRAUD_DUPLICATE_LISTING", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Farmer_CropCreate_RefusedByThePriceBand_KeepsItsScoringSignal()
    {
        _fraud.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<ListingLot?>())
            .Returns(FraudCheckResult.Rejected(
                "FRAUD_DUPLICATE_LISTING", "A similar listing already exists. Please update your existing listing instead."));

        await SyncAsync(Function(), Request(FarmerId, "farmer", null, CropCreate()));

        await _fraud.Received(1).RecordFraudSignalAsync(
            FarmerId, "FRAUD_DUPLICATE_LISTING", "crop-001", "crop", Arg.Any<string?>());
    }

    [Fact]
    public async Task Trucker_TripCreate_GivesTheTripCheckItsPlan_TheDepartureAsUtc()
    {
        // Sent with the Ethiopia offset: the plan carries the same instant in UTC.
        var departure = DateTimeOffset.UtcNow.AddDays(3);
        departure = departure.AddTicks(-(departure.Ticks % TimeSpan.TicksPerSecond));
        var create = TripCreate();
        create.Data!["departureDate"] = departure.ToOffset(TimeSpan.FromHours(3))
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        create.Data["vehiclePlate"] = "3-AA-12345";
        TripPlan? seen = null;
        _fraud.CheckTripCreationAsync(Arg.Do<TripPlan>(plan => seen = plan)).Returns(FraudCheckResult.Allowed());

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, create));

        Assert.Contains("\"status\":\"success\"", body);
        Assert.Equal(
            new TripPlan(TruckerId, "trip-001", "Bishoftu", "Addis Ababa", departure.UtcDateTime, "3-AA-12345"), seen);
        Assert.Equal(DateTimeKind.Utc, seen!.DepartureUtc.Kind);
    }

    [Fact]
    public async Task Trucker_TripCreate_ADuplicateTrip_IsRefused_RecordedUnderTheTypeThatNeverScores()
    {
        _fraud.CheckTripCreationAsync(Arg.Any<TripPlan>()).Returns(FraudCheckResult.Rejected(
            "FRAUD_DUPLICATE_LISTING", "You already have a trip on this route on that day. Please update your existing trip instead.")
            with
        { FlagReason = FraudDetectionService.DuplicateTripSignal });

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains("\"status\":\"error\"", body);
        Assert.Contains(DuplicateTrip, body);
        Assert.DoesNotContain("DUPLICATE_TRIP", body);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "DUPLICATE_TRIP", "trip-001", "trip", Arg.Any<string?>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), "FRAUD_DUPLICATE_LISTING", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Trucker_TripCreate_AnotherTripOfTheDay_IsWrittenAndFlagged()
    {
        _fraud.CheckTripCreationAsync(Arg.Any<TripPlan>()).Returns(FraudCheckResult.Flagged(
            FraudDetectionService.TripSameDaySignal, "Trip Bishoftu to Addis Ababa departs the same day as another open trip."));

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _ddb.Received(1).PutItemAsync(Arg.Any<PutItemRequest>());
        await _fraud.Received(1).RecordFraudSignalAsync(
            TruckerId, FraudDetectionService.TripSameDaySignal, "trip-001", "trip",
            "Trip Bishoftu to Addis Ababa departs the same day as another open trip.");
    }

    [Fact]
    public async Task Trucker_TripCreate_AFlaggedTripThatIsNotWritten_RecordsNoFlag()
    {
        // The id already holds a trip (a replay): the conflict answers, and no same-day row is written.
        _fraud.CheckTripCreationAsync(Arg.Any<TripPlan>()).Returns(FraudCheckResult.Flagged(
            FraudDetectionService.TripSameDaySignal, "flag"));
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "trips"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "tripId", new AttributeValue { S = "trip-001" } },
                        { "createdAt", new AttributeValue { S = "2026-10-01T08:00:00Z" } },
                        { "truckerId", new AttributeValue { S = TruckerId } },
                        { "status", new AttributeValue { S = "open" } },
                        { "version", new AttributeValue { N = "1" } }
                    }
                }
            });

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains("\"status\":\"conflict\"", body);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), FraudDetectionService.TripSameDaySignal, Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Trucker_TripCreate_ATripCheckThatFails_NeverBlocksTheTrip()
    {
        _fraud.CheckTripCreationAsync(Arg.Any<TripPlan>())
            .Returns<FraudCheckResult>(_ => throw new InternalServerErrorException("index unavailable"));

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains("\"status\":\"success\"", body);
        await _ddb.Received(1).PutItemAsync(Arg.Any<PutItemRequest>());
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task Trucker_TripCreate_Critical_IsRefusedBeforeTheTripCheck()
    {
        CallerIs(TruckerId, Scored(RiskTier.Critical));

        var body = await SyncAsync(Function(), Request(TruckerId, "trucker", null, TripCreate()));

        Assert.Contains(AccountHeld, body);
        await _fraud.DidNotReceiveWithAnyArgs().CheckTripCreationAsync(default!);
    }

    [Fact]
    public async Task Trucker_TripCreate_WithTheRealRule_RefusesTheSameRouteAndDayAtAnotherPrice()
    {
        // BUG-4 end to end: the trucker's stored open trip on the route and day of the new one, read
        // through the trucker index by the real service. The new trip asks another price.
        var create = TripCreate();
        var sent = (string)create.Data!["departureDate"];
        var earlier = new QueryResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                new()
                {
                    { "tripId", new AttributeValue { S = "trip-earlier" } },
                    { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
                    { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
                    { "departureDate", new AttributeValue { S = sent } },
                    { "status", new AttributeValue { S = "open" } },
                    { "pricePerQuintal", new AttributeValue { N = "100" } }
                }
            }
        };
        // The trucker index, and the strongly consistent read of the trip row that confirms the hit.
        _ddb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.TableName == "trips" && r.IndexName == "TruckerIdCreatedAtIndex"),
                Arg.Any<CancellationToken>())
            .Returns(earlier);
        _ddb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.TableName == "trips" && r.IndexName == null && r.ConsistentRead == true
                    && r.ExpressionAttributeValues[":id"].S == "trip-earlier"),
                Arg.Any<CancellationToken>())
            .Returns(earlier);
        var service = new FraudDetectionService(
            _ddb, "crops", "", Substitute.For<WeredaConnect.Server.RateLimiting.IRateLimitRepository>())
        {
            TripsTable = "trips"
        };
        var function = new SyncFunction(_meta, _crops, _ddb, "trips", "deliveries",
            fraudService: service, userRepo: _users, orderRepo: _orders);

        var body = await SyncAsync(function, Request(TruckerId, "trucker", null, create));

        Assert.Contains(DuplicateTrip, body);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }
}

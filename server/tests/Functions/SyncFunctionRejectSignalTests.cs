using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-08 (fraud-detection-architecture.md section 15): the <c>/sync</c> refusals that write a typed
/// fraud signal on the caller. Each source writes exactly one signal of its type against the
/// authenticated caller and never onto the user the request names; the answer is the same whether the
/// write works or fails; a batch writes each type once; the refusals an honest offline client also
/// produces (one version conflict, a missing role claim, the owner's create retry) write nothing.
/// </summary>
public class SyncFunctionRejectSignalTests
{
    private const string FarmerId = "farmer-001";
    private const string OtherFarmerId = "farmer-002";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string DeliveriesTable = "test-deliveries";
    private const string DeliveryId = "delivery-001";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryQrSecretRepository _qrSecrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionRejectSignalTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        foreach (var id in new[] { FarmerId, OtherFarmerId, TruckerId, BuyerId })
        {
            _users.GetUserByIdAsync(id).Returns(Profile(id));
        }

        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        _fraud.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(FraudCheckResult.Allowed());

        // A crop listing of the caller and one of another farmer.
        _crops.GetByIdAsync("crop-own").Returns(_ => Crop("crop-own", FarmerId));
        _crops.GetByIdAsync("crop-other").Returns(_ => Crop("crop-other", OtherFarmerId));
    }

    private SyncFunction Function(IFraudDetectionService? fraud = null) => new(
        _meta, _crops, _ddb, "trips", DeliveriesTable,
        fraudService: fraud ?? _fraud, userRepo: _users, orderRepo: _orders, qrSecretRepo: _qrSecrets);

    private static UserDto Profile(string id) => new()
    {
        UserId = id,
        PhoneNumber = "+251933333333",
        DisplayName = "Abebe Kebede",
        Role = "farmer",
        Woreda = "Jimma",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    private static Dictionary<string, object> Crop(string id, string farmerId) => new()
    {
        { "cropId", id }, { "farmerId", farmerId }, { "farmerName", "Abebe Kebede" },
        { "farmerPhone", "+251933333333" }, { "cropType", "teff" }, { "quantity", 10.0 },
        { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "3" }
    };

    private static APIGatewayProxyRequest Request(string userId, string? role, params SyncOperationRequest[] ops)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-frd08",
                ClientId = "device-1",
                Operations = ops.ToList()
            }, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static SyncOperationRequest Op(
        string entityType, string entityId, string action, Dictionary<string, object> data, string? opId = null) => new()
    {
        OperationId = opId ?? $"op-{entityId}-{action}",
        IdempotencyKey = $"caller:{entityType}:{entityId}:{action}:{opId ?? "1"}",
        EntityType = entityType,
        EntityId = entityId,
        Action = action,
        Data = data
    };

    private static SyncOperationRequest CropCreate(string entityId, string farmerId, string? opId = null) =>
        Op("crop_listing", entityId, "create", new Dictionary<string, object>
        {
            { "farmerId", farmerId }, { "cropType", "teff" }, { "quantity", 50 }, { "pricePerKg", 45.5 },
            { "woreda", "Jimma" }, { "locationName", "Jimma" }, { "description", "White teff" }
        }, opId);

    private static SyncOperationRequest CropUpdate(string entityId, Dictionary<string, object> data, string? opId = null) =>
        Op("crop_listing", entityId, "update", data, opId);

    private async Task<string> SyncAsync(APIGatewayProxyRequest request, SyncFunction? function = null)
    {
        var response = await (function ?? Function()).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200);
        return response.Body;
    }

    private static JsonElement Result(string body, int index = 0) =>
        JsonDocument.Parse(body).RootElement.GetProperty("results")[index];

    private async Task OnlySignalIsAsync(string userId, string signalType, string entityId, string entityType)
    {
        await _fraud.Received(1).RecordFraudSignalAsync(userId, signalType, entityId, entityType, Arg.Any<string?>());
        _fraud.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFraudDetectionService.RecordFraudSignalAsync))
            .Should().Be(1);
    }

    private async Task NoSignalAsync() =>
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());

    // ── Role gates and caller binding ────────────────────────────────────

    public static IEnumerable<object[]> WrongRoleCreates() => new[]
    {
        new object[] { TruckerId, "trucker", CropCreate("crop-new", TruckerId), "crop", "CROP_UNAUTHORIZED: Only farmers can create crop listings." },
        new object[]
        {
            FarmerId, "farmer",
            Op("trip", "trip-new", "create", new Dictionary<string, object> { { "truckerId", FarmerId } }),
            "trip", "TRIP_UNAUTHORIZED: Only truckers can create trip listings."
        },
        new object[]
        {
            TruckerId, "trucker",
            Op("order", "order-new", "create", new Dictionary<string, object> { { "buyerId", TruckerId } }),
            "order", "ORDER_UNAUTHORIZED: Only buyers can create orders."
        },
    };

    [Theory]
    [MemberData(nameof(WrongRoleCreates))]
    public async Task WrongRoleCreate_WritesOneRoleForbiddenOnTheCaller(
        string caller, string role, SyncOperationRequest op, string entityType, string message)
    {
        var body = await SyncAsync(Request(caller, role, op));

        Result(body).GetProperty("status").GetString().Should().Be("error");
        Result(body).GetProperty("message").GetString().Should().Be(message);
        await OnlySignalIsAsync(caller, FraudDetectionService.RoleForbiddenSignal, op.EntityId, entityType);
    }

    [Fact]
    public async Task RoleGate_WithoutTheRoleClaim_RefusesAsBeforeAndWritesNothing()
    {
        // An access token instead of the ID token carries no custom:role: a client set-up fault, not a probe.
        var body = await SyncAsync(Request(FarmerId, role: null, CropCreate("crop-new", FarmerId)));

        Result(body).GetProperty("message").GetString().Should().Be("CROP_UNAUTHORIZED: Only farmers can create crop listings.");
        await NoSignalAsync();
    }

    [Fact]
    public async Task CreateForAnotherFarmer_SignalsTheCallerNotTheNamedFarmer()
    {
        var body = await SyncAsync(Request(FarmerId, "farmer", CropCreate("crop-new", OtherFarmerId)));

        Result(body).GetProperty("message").GetString().Should().Be("CROP_UNAUTHORIZED: farmerId must match authenticated user.");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.RoleForbiddenSignal, "crop-new", "crop");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            OtherFarmerId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UpdateOfAnotherFarmersListing_WritesRoleForbiddenOnTheCaller()
    {
        var body = await SyncAsync(Request(FarmerId, "farmer",
            CropUpdate("crop-other", new Dictionary<string, object> { { "description", "Red teff" }, { "version", 3 } })));

        Result(body).GetProperty("message").GetString().Should().Be("CROP_UNAUTHORIZED: You can only update your own crop listings.");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.RoleForbiddenSignal, "crop-other", "crop");
        await _crops.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ── Create conflict on another user's id (IDOR probe) ────────────────

    [Fact]
    public async Task CreateOnAnotherFarmersId_WritesIdorOnTheCallerAndEchoesNothing()
    {
        _crops.CreateAsync("crop-other", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "CONFLICT", "exists"));

        var body = await SyncAsync(Request(FarmerId, "farmer", CropCreate("crop-other", FarmerId)));

        Result(body).GetProperty("status").GetString().Should().Be("conflict");
        (!Result(body).TryGetProperty("serverData", out var echoed) || echoed.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("another farmer's listing is never echoed");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.IdorCreateConflictSignal, "crop-other", "crop");
    }

    [Fact]
    public async Task OwnersCreateRetry_IsAConflictWithoutASignal()
    {
        // A lost response makes an honest client send its create again: its own row comes back.
        _crops.CreateAsync("crop-own", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "CONFLICT", "exists"));

        var body = await SyncAsync(Request(FarmerId, "farmer", CropCreate("crop-own", FarmerId)));

        Result(body).GetProperty("status").GetString().Should().Be("conflict");
        Result(body).GetProperty("serverData").GetProperty("farmerId").GetString().Should().Be(FarmerId);
        await NoSignalAsync();
    }

    // ── Identity and money field changes (SIM-02, SIM-06) ────────────────

    [Fact]
    public async Task CropUpdateThatChangesTheFarmerName_WritesImmutableFieldChange()
    {
        var body = await SyncAsync(Request(FarmerId, "farmer",
            CropUpdate("crop-own", new Dictionary<string, object> { { "farmerName", "Someone Else" }, { "version", 3 } })));

        Result(body).GetProperty("message").GetString().Should()
            .Be("CROP_VALIDATION: farmerName is immutable and cannot be changed on update.");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.ImmutableFieldChangeSignal, "crop-own", "crop");
    }

    [Fact]
    public async Task CropUpdateThatEchoesTheStoredIdentity_WritesNothing()
    {
        // Web clients resend identity fields unchanged; only a change is a probe.
        _crops.UpdateAsync("crop-own", Arg.Any<Dictionary<string, object>>(), 3)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Crop("crop-own", FarmerId)));

        var body = await SyncAsync(Request(FarmerId, "farmer",
            CropUpdate("crop-own", new Dictionary<string, object>
            {
                { "farmerId", FarmerId }, { "farmerPhone", "0933333333" }, { "description", "Red teff" }, { "version", 3 }
            })));

        Result(body).GetProperty("status").GetString().Should().Be("success");
        await NoSignalAsync();
    }

    private void Order(string status = "funded") =>
        _orders.GetByIdAsync("order-001", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", status },
            { "quantity", "500" }, { "orderTotal", "15000" }, { "version", "4" }
        });

    private static SyncOperationRequest OrderUpdate(Dictionary<string, object> data) => Op("order", "order-001", "update", data);

    [Fact]
    public async Task OrderUpdateByANonParty_WritesRoleForbiddenOnTheCaller()
    {
        Order();

        var body = await SyncAsync(Request(OtherFarmerId, "farmer",
            OrderUpdate(new Dictionary<string, object> { { "status", "accepted" }, { "version", 4 } })));

        Result(body).GetProperty("message").GetString().Should()
            .Be("ORDER_UNAUTHORIZED: You can only update orders you are a participant in.");
        await OnlySignalIsAsync(OtherFarmerId, FraudDetectionService.RoleForbiddenSignal, "order-001", "order");
    }

    [Fact]
    public async Task OrderTransitionOfTheOtherParty_WritesRoleForbidden()
    {
        // Only the buyer cancels; the farmer's app offers Accept and Reject only.
        Order();

        var body = await SyncAsync(Request(FarmerId, "farmer",
            OrderUpdate(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } })));

        Result(body).GetProperty("message").GetString().Should()
            .Be("ORDER_UNAUTHORIZED: Only the buyer can transition to 'cancelled'.");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.RoleForbiddenSignal, "order-001", "order");
    }

    [Fact]
    public async Task OrderUpdateThatChangesTheQuantity_WritesImmutableFieldChange()
    {
        Order();

        var body = await SyncAsync(Request(BuyerId, "buyer",
            OrderUpdate(new Dictionary<string, object> { { "quantity", 1 }, { "status", "cancelled" }, { "version", 4 } })));

        Result(body).GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: quantity is immutable and cannot be changed on update.");
        await OnlySignalIsAsync(BuyerId, FraudDetectionService.ImmutableFieldChangeSignal, "order-001", "order");
    }

    // ── QR handoff failures ──────────────────────────────────────────────

    private void PendingDeliveryWithNonce(string? consumedPickupOp = null)
    {
        var created = DateTime.UtcNow.AddDays(-1).ToString("o");
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "orderId", new AttributeValue { S = "order-001" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = "pending" } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = "1" } }
        };
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _qrSecrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret
        {
            DeliveryId = DeliveryId,
            Nonce = "farmer-nonce",
            IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o"),
            ConsumedPickedUpOp = consumedPickupOp
        });
    }

    private static SyncOperationRequest Pickup(string? scannedNonce)
    {
        var data = new Dictionary<string, object> { { "status", "pickedUp" }, { "version", 1 } };
        if (scannedNonce != null)
        {
            data["scannedQrNonce"] = scannedNonce;
        }

        return Op("delivery", DeliveryId, "update", data, "op-pickup");
    }

    [Theory]
    [InlineData("stale-nonce", null, "DELIVERY_NONCE_MISMATCH:")]
    [InlineData(null, null, "DELIVERY_NONCE_MISSING:")]
    [InlineData("farmer-nonce", "op-earlier-pickup", "DELIVERY_NONCE_REPLAY:")]
    public async Task QrHandoffFailure_WritesOneSoftSignalOnTheScanner(
        string? scanned, string? consumedBy, string code)
    {
        PendingDeliveryWithNonce(consumedBy);

        var body = await SyncAsync(Request(TruckerId, "trucker", Pickup(scanned)));

        Result(body).GetProperty("status").GetString().Should().Be("error");
        Result(body).GetProperty("message").GetString().Should().StartWith(code);
        await OnlySignalIsAsync(TruckerId, FraudDetectionService.QrHandoffFailureSignal, DeliveryId, "delivery");
        FraudDetectionService.IsSoftSignal(FraudDetectionService.QrHandoffFailureSignal).Should().BeTrue();
        FraudDetectionService.SignalWeight(FraudDetectionService.QrHandoffFailureSignal).Should().Be(5);
    }

    // ── Stale-version replays ────────────────────────────────────────────

    private void CropUpdatesConflict() =>
        _crops.UpdateAsync("crop-own", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "CONFLICT", "Version conflict."));

    private static SyncOperationRequest[] StaleCropUpdates(int count) => Enumerable.Range(1, count)
        .Select(i => CropUpdate("crop-own",
            new Dictionary<string, object> { { "description", $"Edit {i}" }, { "version", 1 } }, $"op-stale-{i}"))
        .ToArray();

    [Fact]
    public async Task OneStaleVersionReplayAfterOffline_IsAConflictWithoutASignal()
    {
        CropUpdatesConflict();

        var body = await SyncAsync(Request(FarmerId, "farmer", StaleCropUpdates(1)));

        Result(body).GetProperty("status").GetString().Should().Be("conflict");
        await NoSignalAsync();
    }

    [Fact]
    public async Task ConflictsBelowTheBurstThreshold_WriteNothing()
    {
        CropUpdatesConflict();

        await SyncAsync(Request(FarmerId, "farmer", StaleCropUpdates(SyncFunction.StaleVersionBurstThreshold - 1)));

        await NoSignalAsync();
    }

    [Fact]
    public async Task ABurstOfStaleVersionConflicts_WritesOneWeightZeroRowOnTheCaller()
    {
        CropUpdatesConflict();

        var body = await SyncAsync(Request(FarmerId, "farmer", StaleCropUpdates(SyncFunction.StaleVersionBurstThreshold + 5)));

        Enumerable.Range(0, SyncFunction.StaleVersionBurstThreshold + 5)
            .Select(i => Result(body, i).GetProperty("status").GetString())
            .Should().AllBe("conflict");
        await OnlySignalIsAsync(FarmerId, FraudDetectionService.StaleVersionBurstSignal, "crop-own", "crop_listing");
        FraudDetectionService.SignalWeight(FraudDetectionService.StaleVersionBurstSignal).Should().Be(0);
    }

    // ── Write limit and best-effort ──────────────────────────────────────

    [Fact]
    public async Task ABurstOfOneRefusalType_IsOneWritePerBatch()
    {
        var ops = Enumerable.Range(1, 5).Select(i => CropCreate($"crop-new-{i}", TruckerId, $"op-{i}")).ToArray();

        var body = await SyncAsync(Request(TruckerId, "trucker", ops));

        Enumerable.Range(0, 5).Select(i => Result(body, i).GetProperty("status").GetString()).Should().AllBe("error");
        await _fraud.Received(1).RecordFraudSignalAsync(
            TruckerId, FraudDetectionService.RoleForbiddenSignal, "crop-new-1", "crop", Arg.Any<string?>());
        _fraud.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFraudDetectionService.RecordFraudSignalAsync))
            .Should().Be(1);
    }

    [Fact]
    public async Task EachBatchTriesAgain_TheHourlyLimitIsTheServicesJob()
    {
        var function = Function();

        await SyncAsync(Request(TruckerId, "trucker", CropCreate("crop-new-1", TruckerId)), function);
        await SyncAsync(Request(TruckerId, "trucker", CropCreate("crop-new-2", TruckerId)), function);

        await _fraud.Received(2).RecordFraudSignalAsync(
            TruckerId, FraudDetectionService.RoleForbiddenSignal, Arg.Any<string?>(), "crop", Arg.Any<string?>());
    }

    private SyncOperationRequest[] MixedRefusals()
    {
        _crops.CreateAsync("crop-other", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "CONFLICT", "exists"));
        return new[]
        {
            CropCreate("crop-other", FarmerId, "op-idor"),
            CropUpdate("crop-other", new Dictionary<string, object> { { "description", "x" }, { "version", 3 } }, "op-role"),
            CropUpdate("crop-own", new Dictionary<string, object> { { "farmerId", OtherFarmerId }, { "version", 3 } }, "op-field"),
        };
    }

    [Fact]
    public async Task DifferentRefusalTypes_EachWriteTheirOwnSignal()
    {
        await SyncAsync(Request(FarmerId, "farmer", MixedRefusals()));

        await _fraud.Received(1).RecordFraudSignalAsync(
            FarmerId, FraudDetectionService.IdorCreateConflictSignal, "crop-other", "crop", Arg.Any<string?>());
        await _fraud.Received(1).RecordFraudSignalAsync(
            FarmerId, FraudDetectionService.RoleForbiddenSignal, "crop-other", "crop", Arg.Any<string?>());
        await _fraud.Received(1).RecordFraudSignalAsync(
            FarmerId, FraudDetectionService.ImmutableFieldChangeSignal, "crop-own", "crop", Arg.Any<string?>());
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            OtherFarmerId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AFailingSignalWrite_LeavesTheResponseByteIdentical()
    {
        var working = await SyncAsync(Request(FarmerId, "farmer", MixedRefusals()));

        var failing = Substitute.For<IFraudDetectionService>();
        failing.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        failing.CheckListingCreationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(FraudCheckResult.Allowed());
        failing.RecordFraudSignalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        var broken = await SyncAsync(Request(FarmerId, "farmer", MixedRefusals()), Function(failing));

        broken.Should().Be(working);
        await failing.Received(3).RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("Failed to record fraud signal");
    }

    // ── One test per refusal site, through the real service: the row written, its type and weight ──

    private readonly IAmazonDynamoDB _signalsDb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<Dictionary<string, AttributeValue>> _written = new();
    private readonly IIntentRepository _intents = Substitute.For<IIntentRepository>();
    private readonly IRatingRepository _ratings = Substitute.For<IRatingRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();

    /// <summary>A function whose signals go through a real <see cref="FraudDetectionService"/>.</summary>
    private SyncFunction Recording()
    {
        _signalsDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _signalsDb.PutItemAsync(Arg.Do<PutItemRequest>(r => _written.Add(r.Item)), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());
        var service = new FraudDetectionService(_signalsDb, "crops", "fraud-signals", Substitute.For<IRateLimitRepository>());
        return new SyncFunction(_meta, _crops, _ddb, "trips", DeliveriesTable,
            fraudService: service, userRepo: _users, orderRepo: _orders, intentRepo: _intents,
            ratingRepo: _ratings, qrSecretRepo: _qrSecrets, messageRepo: _messages);
    }

    private async Task<JsonElement> RefusedAsync(string caller, string? role, SyncOperationRequest op, string messageStart)
    {
        var body = await SyncAsync(Request(caller, role, op), Recording());
        var result = Result(body);
        result.GetProperty("message").GetString().Should().StartWith(messageStart);
        return result;
    }

    private void WroteOne(string userId, string signalType, int weight, string entityId, string entityType)
    {
        var row = _written.Should().ContainSingle().Subject;
        row["userId"].S.Should().Be(userId);
        row["signalType"].S.Should().Be(signalType);
        row["weight"].N.Should().Be(weight.ToString());
        row["entityId"].S.Should().Be(entityId);
        row["entityType"].S.Should().Be(entityType);
    }

    private void TripRow(string truckerId) =>
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "trips"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "tripId", new AttributeValue { S = "trip-001" } },
                        { "createdAt", new AttributeValue { S = "2026-10-01T08:00:00Z" } },
                        { "truckerId", new AttributeValue { S = truckerId } },
                        { "truckerName", new AttributeValue { S = "Chaltu Tolosa" } },
                        { "truckerPhone", new AttributeValue { S = "+251933333333" } },
                        { "status", new AttributeValue { S = "open" } },
                        { "version", new AttributeValue { N = "2" } }
                    }
                }
            });

    [Fact]
    public async Task TripCreateOnAnotherTruckersId_WritesIdor40()
    {
        TripRow("trucker-002");

        var result = await RefusedAsync(TruckerId, "trucker", Op("trip", "trip-001", "create", new Dictionary<string, object>
        {
            { "truckerId", TruckerId }, { "fromWoreda", "Bishoftu" }, { "toWoreda", "Adama" },
            { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") }, { "capacity", 150.0 }, { "pricePerQuintal", 120.0 }
        }), "Trip already exists on server.");

        result.GetProperty("status").GetString().Should().Be("conflict");
        WroteOne(TruckerId, FraudDetectionService.IdorCreateConflictSignal, 40, "trip-001", "trip");
    }

    [Fact]
    public async Task TripUpdateThatChangesTheTruckerName_WritesImmutableFieldChange25()
    {
        TripRow(TruckerId);

        await RefusedAsync(TruckerId, "trucker", Op("trip", "trip-001", "update",
                new Dictionary<string, object> { { "truckerName", "Someone Else" }, { "version", 2 } }),
            "TRIP_VALIDATION: truckerName is immutable");

        WroteOne(TruckerId, FraudDetectionService.ImmutableFieldChangeSignal, 25, "trip-001", "trip");
    }

    [Fact]
    public async Task OrderCreateOnAnotherBuyersId_WritesIdor40()
    {
        _crops.GetByIdAsync("listing-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "listingId", "listing-001" }, { "farmerId", FarmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" }, { "quantity", 10.0 },
            { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _orders.CreateAsync("order-taken", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "CONFLICT", "exists"));
        _orders.GetByIdAsync("order-taken").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-taken" }, { "buyerId", "buyer-002" }, { "farmerId", OtherFarmerId }, { "version", "1" }
        });

        var result = await RefusedAsync(BuyerId, "buyer", Op("order", "order-taken", "create", new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", "listing-001" },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" }, { "cropType", "teff" },
            { "quantity", 50 }, { "pricePerUnit", 45.5 }
        }), "Order already exists on server.");

        result.GetProperty("status").GetString().Should().Be("conflict");
        WroteOne(BuyerId, FraudDetectionService.IdorCreateConflictSignal, 40, "order-taken", "order");
    }

    [Fact]
    public async Task OrderUpdateEchoingAnotherOrderId_WritesImmutableFieldChange25()
    {
        Order();

        await RefusedAsync(BuyerId, "buyer", OrderUpdate(new Dictionary<string, object>
        {
            { "orderId", "order-999" }, { "status", "cancelled" }, { "version", 4 }
        }), "ORDER_VALIDATION: orderId does not match the operation entityId.");

        WroteOne(BuyerId, FraudDetectionService.ImmutableFieldChangeSignal, 25, "order-001", "order");
    }

    [Fact]
    public async Task TruckerSettingANonLogisticsOrderStatus_WritesRoleForbidden25()
    {
        _orders.GetByIdAsync("order-001", true).Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId },
            { "status", "funded" }, { "version", "4" }
        });

        await RefusedAsync(TruckerId, "trucker", OrderUpdate(new Dictionary<string, object>
        {
            { "status", "accepted" }, { "version", 4 }
        }), "ORDER_UNAUTHORIZED: The trucker may only set pickup_confirmed, in_transit or delivered.");

        WroteOne(TruckerId, FraudDetectionService.RoleForbiddenSignal, 25, "order-001", "order");
    }

    [Theory]
    [InlineData("farmer", "INTENT_UNAUTHORIZED: Only buyers can create purchase intents.")]
    [InlineData("buyer", "INTENT_UNAUTHORIZED: buyerId must match authenticated user.")]
    public async Task IntentRefusals_WriteRoleForbidden25(string role, string message)
    {
        await RefusedAsync(BuyerId, role, Op("buyer_intent", "intent-001", "create",
            new Dictionary<string, object> { { "buyerId", "buyer-002" }, { "cropType", "teff" } }), message);

        WroteOne(BuyerId, FraudDetectionService.RoleForbiddenSignal, 25, "intent-001", "buyer_intent");
    }

    public static IEnumerable<object[]> RatingRefusals() => new[]
    {
        new object[] { "farmer", BuyerId, "RATING_UNAUTHORIZED: Only buyers can rate counterparties." },
        new object[] { "buyer", "buyer-002", "RATING_UNAUTHORIZED: raterId must match authenticated user." },
        new object[] { "buyer", BuyerId, "RATING_FORBIDDEN: You can only rate your own orders." },
    };

    [Theory]
    [MemberData(nameof(RatingRefusals))]
    public async Task RatingRefusals_WriteRoleForbidden25(string role, string raterId, string message)
    {
        _orders.GetByIdAsync("order-other").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-other" }, { "buyerId", "buyer-002" }, { "farmerId", FarmerId }, { "status", "delivered" }
        });

        await RefusedAsync(BuyerId, role, Op("rating", "rating-001", "create", new Dictionary<string, object>
        {
            { "ratingId", "rating-001" }, { "orderId", "order-other" }, { "raterId", raterId },
            { "rateeId", FarmerId }, { "rateeRole", "farmer" }, { "stars", 5 }
        }), message);

        WroteOne(BuyerId, FraudDetectionService.RoleForbiddenSignal, 25, "rating-001", "rating");
    }

    [Fact]
    public async Task MessageFromSomeoneElse_WritesRoleForbidden25()
    {
        await RefusedAsync(BuyerId, "buyer", Op("message", "msg-001", "create", new Dictionary<string, object>
        {
            { "messageId", "msg-001" }, { "conversationId", $"order:order-001:{BuyerId}:{FarmerId}" },
            { "contextType", "order" }, { "contextId", "order-001" }, { "senderId", FarmerId },
            { "recipientId", BuyerId }, { "body", "Hello" }
        }), "MESSAGE_UNAUTHORIZED: senderId must match authenticated user.");

        WroteOne(BuyerId, FraudDetectionService.RoleForbiddenSignal, 25, "msg-001", "message");
    }

    private void DeliveryRow(string status, string? deliveryType = null, string? confirmedAt = null)
    {
        var created = DateTime.UtcNow.AddDays(-1).ToString("o");
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "orderId", new AttributeValue { S = "order-001" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = "1" } }
        };
        if (deliveryType != null)
        {
            item["deliveryType"] = new AttributeValue { S = deliveryType };
        }

        if (confirmedAt != null)
        {
            item["confirmedAt"] = new AttributeValue { S = confirmedAt };
        }

        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
    }

    private static SyncOperationRequest DeliveryUpdate(Dictionary<string, object> data) =>
        Op("delivery", DeliveryId, "update", data, "op-delivery");

    public static IEnumerable<object[]> DeliveryRoleRefusals() => new[]
    {
        new object[] { "pending", BuyerId, "buyer", "pickedUp", "DELIVERY_UNAUTHORIZED: Only the trucker can mark as pickedUp." },
        new object[] { "inTransit", TruckerId, "trucker", "delivered", "DELIVERY_UNAUTHORIZED: Only the buyer can confirm delivery." },
        new object[] { "pending", OtherFarmerId, "farmer", "cancelled", "DELIVERY_UNAUTHORIZED: Only participants can cancel a delivery." },
        new object[] { "disputed", OtherFarmerId, "farmer", "disputed", "DELIVERY_UNAUTHORIZED: Only participants can dispute a delivery." },
    };

    [Theory]
    [MemberData(nameof(DeliveryRoleRefusals))]
    public async Task DeliveryStatusRefusals_WriteRoleForbidden25OnTheCaller(
        string current, string caller, string role, string target, string message)
    {
        DeliveryRow(current);

        await RefusedAsync(caller, role, DeliveryUpdate(new Dictionary<string, object> { { "status", target }, { "version", 1 } }), message);

        WroteOne(caller, FraudDetectionService.RoleForbiddenSignal, 25, DeliveryId, "delivery");
    }

    [Fact]
    public async Task DisputeOfADeliveredDeliveryByANonParty_WritesRoleForbidden25()
    {
        DeliveryRow("delivered", confirmedAt: DateTime.UtcNow.AddHours(-1).ToString("o"));

        await RefusedAsync(OtherFarmerId, "farmer", DeliveryUpdate(new Dictionary<string, object>
        {
            { "status", "disputed" }, { "disputeReason", "Not mine" }, { "version", 1 }
        }), "DELIVERY_UNAUTHORIZED: Only participants can dispute a delivery.");

        WroteOne(OtherFarmerId, FraudDetectionService.RoleForbiddenSignal, 25, DeliveryId, "delivery");
    }

    [Fact]
    public async Task NonStatusDeliveryUpdateByTheBuyer_WritesRoleForbidden25()
    {
        DeliveryRow("pending");

        await RefusedAsync(BuyerId, "buyer", DeliveryUpdate(new Dictionary<string, object>
        {
            { "disputeReason", "late" }, { "version", 1 }
        }), "DELIVERY_UNAUTHORIZED: You can only update your own deliveries.");

        WroteOne(BuyerId, FraudDetectionService.RoleForbiddenSignal, 25, DeliveryId, "delivery");
    }

    [Fact]
    public async Task DeliveryUpdateThatRepointsTheBuyer_WritesImmutableFieldChange25()
    {
        DeliveryRow("pending");

        await RefusedAsync(TruckerId, "trucker", DeliveryUpdate(new Dictionary<string, object>
        {
            { "buyerId", "buyer-002" }, { "version", 1 }
        }), "DELIVERY_VALIDATION: buyerId is immutable");

        WroteOne(TruckerId, FraudDetectionService.ImmutableFieldChangeSignal, 25, DeliveryId, "delivery");
    }

    [Theory]
    [InlineData("self_pickup", "DELIVERY_UNAUTHORIZED: buyerId must match authenticated user for self-pickup.")]
    [InlineData("truck", "DELIVERY_UNAUTHORIZED: truckerId must match authenticated user.")]
    public async Task DeliveryCreateForSomeoneElse_WritesRoleForbidden25(string deliveryType, string message)
    {
        var data = new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "listingId", "listing-001" }, { "farmerId", FarmerId },
            { "buyerId", "buyer-002" }, { "truckerId", "trucker-002" }
        };
        if (deliveryType == "self_pickup")
        {
            data["deliveryType"] = "self_pickup";
        }

        await RefusedAsync(BuyerId, "buyer", Op("delivery", "delivery-new", "create", data), message);

        WroteOne(BuyerId, FraudDetectionService.RoleForbiddenSignal, 25, "delivery-new", "delivery");
    }
}

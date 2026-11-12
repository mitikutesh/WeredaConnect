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
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-29 through <c>/sync</c> (reputation-api.md sections 7 and 8): a suspended caller's whole
/// batch is refused (not flagged); with <c>reputationPenalties</c> on, a restricted farmer lists
/// nothing new, a restricted trucker posts no trip, a buyer cannot order from a restricted or
/// suspended owner, and a new crop or trip row carries the owner's reputation. With the flag off
/// none of the gates or stamps exist. The accept gate is in <see cref="SyncFunctionCommitmentTests"/>.
/// </summary>
public class SyncFunctionReputationGateTests
{
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string ListingId = "listing-001";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly TestLambdaContext _ctx = new();
    private readonly Dictionary<string, UserDto> _rows = new();
    private Dictionary<string, object>? _createdCrop;
    private PutItemRequest? _putTrip;

    public SyncFunctionReputationGateTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _rows[FarmerId] = User(FarmerId, "farmer");
        _rows[TruckerId] = User(TruckerId, "trucker");
        _rows[BuyerId] = User(BuyerId, "buyer");
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(call => _rows.GetValueOrDefault(call.Arg<string>()));
        _users.GetUserByIdAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(call => _rows.GetValueOrDefault(call.ArgAt<string>(0)));

        _crops.CreateAsync(Arg.Any<string>(), Arg.Do<Dictionary<string, object>>(d => _createdCrop = d))
            .Returns(call =>
            {
                // As the repository answers: every stored value as its string (DynamoDbHelper.FromItem).
                var row = call.ArgAt<Dictionary<string, object>>(1).ToDictionary(
                    kv => kv.Key,
                    kv => (object)(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                row["cropId"] = call.ArgAt<string>(0);
                row["version"] = "1";
                return AuthResult<Dictionary<string, object>>.Success(row);
            });
        _crops.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "2" } }));
        _crops.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" }, { "farmerId", FarmerId }, { "cropType", "teff" }, { "woreda", "Jimma" },
            { "quantity", 6.0 }, { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _crops.GetByIdAsync(ListingId).Returns(new Dictionary<string, object>
        {
            { "cropId", ListingId }, { "listingId", ListingId }, { "farmerId", FarmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" },
            { "quantity", 10.0 }, { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _orders.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", call.ArgAt<string>(0) }, { "status", "created" }, { "version", "1" }
            }));
        _ddb.QueryAsync(Arg.Any<QueryRequest>()).Returns(new QueryResponse { Items = new() });
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => _putTrip = r)).Returns(new PutItemResponse());
    }

    // ===== 8.4 suspension: the whole batch, flag or no flag =====

    [Theory]
    [InlineData(FarmerId, "farmer", false)]
    [InlineData(TruckerId, "trucker", false)]
    [InlineData(BuyerId, "buyer", true)]
    public async Task ASuspendedCaller_IsRefusedForTheWholeBatch_WhateverTheFlag(string userId, string role, bool gates)
    {
        _rows[userId] = _rows[userId] with { Suspended = true };

        var response = await Function(gates).FunctionHandler(Request(CropCreate(), userId, role), _ctx);

        response.StatusCode.Should().Be(403);
        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("AUTH_025");
        error.GetProperty("message").GetString().Should().Be("Account suspended.");
        // No operation applied, no idempotency key read or recorded.
        await _meta.DidNotReceive().IdempotencyKeyExistsAsync(Arg.Any<string>());
        _meta.ReceivedCalls().Should().BeEmpty();
        await _crops.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ADeletingAccount_StillGetsTheDeletionAnswer_BeforeTheSuspension()
    {
        _rows[FarmerId] = _rows[FarmerId] with { Suspended = true, DeletionState = "in_progress" };

        var response = await Function(false).FunctionHandler(Request(CropCreate(), FarmerId, "farmer"), _ctx);

        response.StatusCode.Should().Be(410);
        response.Body.Should().Contain("ACCOUNT_004");
    }

    [Fact]
    public async Task TheCallersRow_IsReadOncePerBatch_StronglyConsistent()
    {
        await SendAsync(CropCreate(), FarmerId, "farmer", gates: true);

        await _users.Received(1).GetUserByIdAsync(FarmerId, true);
    }

    [Fact]
    public async Task ACallerRowThatCannotBeRead_RefusesTheBatch_AsBefore()
    {
        _users.GetUserByIdAsync(FarmerId, true).ThrowsAsync(new InvalidOperationException("down"));

        var response = await Function(false).FunctionHandler(Request(CropCreate(), FarmerId, "farmer"), _ctx);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("SYNC_SERVER_ERROR");
    }

    // ===== 8.1 own gate: farmer =====

    [Fact]
    public async Task Farmer_Restricted_CropCreate_IsRefused_WithTheEndDate_NothingWritten()
    {
        var until = DateTime.UtcNow.AddDays(9);
        Restrict(FarmerId, until);

        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: true);

        result.GetProperty("status").GetString().Should().Be("error");
        var message = result.GetProperty("message").GetString()!;
        message.Should().StartWith($"REPUTATION_RESTRICTED: until={ReputationPolicy.Iso(until)}: Your account can't take new orders until ");
        message.Should().EndWith("Your current orders continue.");
        await _crops.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Farmer_UnderReview_CropCreate_SaysReview()
    {
        Restrict(FarmerId, until: null);

        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: true);

        result.GetProperty("message").GetString().Should().StartWith("REPUTATION_RESTRICTED: until=review: ");
    }

    [Fact]
    public async Task Farmer_Restricted_FlagOff_CropCreate_GoesThrough_Unstamped()
    {
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));

        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: false);

        result.GetProperty("status").GetString().Should().Be("success");
        _createdCrop.Should().NotBeNull();
        _createdCrop!.Keys.Should().NotContain(OwnerReputationFields.Names);
    }

    [Fact]
    public async Task Farmer_WhoseRestrictionEnded_CropCreate_GoesThrough_EvenBeforeTheDailyJob()
    {
        // The stored map still says restricted: the daily job has not run since the end.
        Restrict(FarmerId, DateTime.UtcNow.AddMinutes(-5));

        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: true);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Farmer_Restricted_MayStillUpdateAListing()
    {
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));
        var update = Op("crop_listing", "crop-001", "update", new Dictionary<string, object> { { "pricePerKg", 50.0 }, { "version", 1 } });

        var result = await SendAsync(update, FarmerId, "farmer", gates: true);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Farmer_Restricted_OldAppWithoutABuildHeader_GetsAPermanentCodeItKnows()
    {
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));

        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: true, appBuild: null);

        var message = result.GetProperty("message").GetString()!;
        message.Should().StartWith("SYNC_VALIDATION: Your account can't take new orders until ");
        message.Should().NotContain("REPUTATION_RESTRICTED");
    }

    // ===== 8.1 own gate: trucker =====

    [Fact]
    public async Task Trucker_Restricted_TripCreate_IsRefused_NothingWritten()
    {
        Restrict(TruckerId, DateTime.UtcNow.AddDays(3));

        var result = await SendAsync(TripCreate(), TruckerId, "trucker", gates: true);

        result.GetProperty("message").GetString().Should().StartWith("REPUTATION_RESTRICTED: until=");
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task Trucker_Restricted_FlagOff_TripCreate_GoesThrough_Unstamped()
    {
        Restrict(TruckerId, DateTime.UtcNow.AddDays(3));

        var result = await SendAsync(TripCreate(), TruckerId, "trucker", gates: false);

        result.GetProperty("status").GetString().Should().Be("success");
        _putTrip!.Item.Keys.Should().NotContain(OwnerReputationFields.Names);
    }

    // ===== 8.2 counterparty gate: buyer =====

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Buyer_OrderFromARestrictedOrSuspendedFarmer_IsRefused_WithoutDates(bool suspended)
    {
        if (suspended)
        {
            _rows[FarmerId] = _rows[FarmerId] with { Suspended = true };
        }
        else
        {
            Restrict(FarmerId, DateTime.UtcNow.AddDays(9));
        }

        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: true);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .Be("REPUTATION_RESTRICTED: counterparty: This farmer or trucker can't take new orders right now.");
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _users.Received(1).GetUserByIdAsync(FarmerId, true);
    }

    [Fact]
    public async Task Buyer_OrderFromAFarmerInGoodStanding_GoesThrough()
    {
        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: true);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Buyer_OrderFromARestrictedFarmer_FlagOff_GoesThrough_WithNoOwnerRead()
    {
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));

        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: false);

        result.GetProperty("status").GetString().Should().Be("success");
        await _users.DidNotReceive().GetUserByIdAsync(FarmerId, true);
    }

    [Fact]
    public async Task Buyer_OwnerRowCannotBeRead_RefusedForNow_WithTheRetryableText()
    {
        _users.GetUserByIdAsync(FarmerId, true).ThrowsAsync(new InvalidOperationException("down"));

        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: true);

        result.GetProperty("message").GetString().Should().Be("The seller's account could not be checked. Please try again later.");
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Buyer_ReplayOfAnOrderTheServerHolds_GetsTheConflict_NotTheRefusal()
    {
        // The farmer was restricted after the order was created: the client must not drop it.
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));
        _orders.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", "funded" }, { "version", "3" }
        });
        _orders.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Order already exists.", "orderId"));

        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: true);

        result.GetProperty("status").GetString().Should().Be("conflict");
    }

    [Fact]
    public async Task Buyer_OldAppWithoutABuildHeader_GetsOrderValidation()
    {
        Restrict(FarmerId, DateTime.UtcNow.AddDays(9));

        var result = await SendAsync(OrderCreate(), BuyerId, "buyer", gates: true, appBuild: null);

        result.GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: This farmer or trucker can't take new orders right now.");
    }

    // ===== section 7: owner stamps at create =====

    [Fact]
    public async Task CropCreate_FlagOn_StampsTheOwnersReputation_AndTheAnswerShowsThePublicTier()
    {
        _rows[FarmerId] = _rows[FarmerId] with
        {
            RatingSum = 44,
            RatingCount = 10,
            RatingsAggregatedAt = "2026-11-01T00:00:00.000Z",
            Reputation = new ReputationSnapshot
            {
                Tier = ReputationTiers.Warning, ActivePoints = 4, CompletedOrders90d = 9, FaultCancels90d = 1
            }
        };

        var result = await SendAsync(
            CropCreate((OwnerReputationFields.TierField, "good")), FarmerId, "farmer", gates: true, publicTags: true);

        result.GetProperty("status").GetString().Should().Be("success");
        _createdCrop![OwnerReputationFields.TierField].Should().Be("warning", "the row stores the internal tier, never the client's value");
        _createdCrop[OwnerReputationFields.EffectiveRatingField].Should().Be(4.0m);
        _createdCrop[OwnerReputationFields.RatingCountField].Should().Be(10L);
        _createdCrop[OwnerReputationFields.ReliabilityPctField].Should().Be(90);
        _createdCrop.Should().ContainKey(OwnerReputationFields.AtField);
        // Buyers and the echo read warning as good.
        result.GetProperty("serverData").GetProperty(OwnerReputationFields.TierField).GetString().Should().Be("good");
    }

    [Fact]
    public async Task CropCreate_PublicTagsOff_TheAnswerLeavesTheOwnerFieldsOut()
    {
        var result = await SendAsync(CropCreate(), FarmerId, "farmer", gates: true, publicTags: false);

        _createdCrop![OwnerReputationFields.TierField].Should().Be("good");
        _createdCrop.Should().NotContainKey(OwnerReputationFields.EffectiveRatingField);
        var data = result.GetProperty("serverData");
        foreach (var name in OwnerReputationFields.Names)
        {
            data.TryGetProperty(name, out _).Should().BeFalse(name);
        }
    }

    [Fact]
    public async Task TripCreate_FlagOn_StampsTheOwnersReputation()
    {
        var result = await SendAsync(TripCreate(), TruckerId, "trucker", gates: true);

        result.GetProperty("status").GetString().Should().Be("success");
        _putTrip!.Item[OwnerReputationFields.TierField].S.Should().Be("good");
        _putTrip.Item[OwnerReputationFields.RatingCountField].N.Should().Be("0");
        _putTrip.Item.Should().ContainKey(OwnerReputationFields.AtField);
    }

    [Fact]
    public async Task AnUpdate_NeverCarriesTheOwnerFields()
    {
        var update = Op("crop_listing", "crop-001", "update", new Dictionary<string, object>
        {
            { "pricePerKg", 50.0 }, { OwnerReputationFields.TierField, "good" }, { OwnerReputationFields.RatingCountField, 99 }, { "version", 1 }
        });

        var result = await SendAsync(update, FarmerId, "farmer", gates: true);

        result.GetProperty("status").GetString().Should().Be("success");
        await _crops.Received(1).UpdateAsync("crop-001",
            Arg.Is<Dictionary<string, object>>(d => !d.Keys.Any(k => k.StartsWith("owner", StringComparison.Ordinal))), Arg.Any<int>());
    }

    // ===== helpers =====

    private void Restrict(string userId, DateTime? until) => _rows[userId] = _rows[userId] with
    {
        Reputation = new ReputationSnapshot
        {
            Tier = ReputationTiers.Restricted,
            ActivePoints = 10,
            HasRestriction = true,
            RestrictionCause = until == null ? ReputationCauses.DeliveryOverdue : ReputationCauses.Points,
            RestrictionSince = DateTime.UtcNow.AddDays(-5),
            RestrictionUntil = until
        }
    };

    private static UserDto User(string userId, string role) => new()
    {
        UserId = userId,
        PhoneNumber = "+251933333333",
        DisplayName = "Abebe Kebede",
        Role = role,
        Woreda = "Jimma",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    private SyncFunction Function(bool gates, bool publicTags = false)
        => new(_meta, _crops, _ddb, "trips", "deliveries", userRepo: _users, orderRepo: _orders)
        {
            ReputationGatesEnabled = gates,
            ReputationPublicTags = publicTags
        };

    private async Task<JsonElement> SendAsync(
        SyncOperationRequest op, string userId, string role, bool gates, bool publicTags = false, int? appBuild = 3)
    {
        var response = await Function(gates, publicTags).FunctionHandler(Request(op, userId, role, appBuild), _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static APIGatewayProxyRequest Request(SyncOperationRequest op, string userId, string role, int? appBuild = 3) => new()
    {
        Body = JsonSerializer.Serialize(new SyncRequest
        {
            SyncBatchId = "batch-esc-29",
            ClientId = "device-1",
            Operations = new List<SyncOperationRequest> { op }
        }, Json),
        Headers = appBuild == null ? null : new Dictionary<string, string> { { ClientAppBuild.Header, appBuild.Value.ToString() } },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } }
        }
    };

    private static SyncOperationRequest Op(string entityType, string entityId, string action, Dictionary<string, object> data) => new()
    {
        OperationId = "op-1",
        IdempotencyKey = $"user:{entityType}:{entityId}:{action}:{Guid.NewGuid():N}",
        EntityType = entityType,
        EntityId = entityId,
        Action = action,
        Data = data
    };

    private static SyncOperationRequest CropCreate(params (string Key, object Value)[] extra)
    {
        var data = new Dictionary<string, object>
        {
            { "farmerId", FarmerId }, { "cropType", "teff" }, { "quantity", 50 }, { "pricePerKg", 45.5 },
            { "woreda", "Jimma" }, { "locationName", "Jimma" }, { "description", "White teff" },
            { "photoIds", CropPhotoFixtures.ValidPhotoIds() }
        };
        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        return Op("crop_listing", "crop-new", "create", data);
    }

    private static SyncOperationRequest TripCreate() => Op("trip", "trip-new", "create", new Dictionary<string, object>
    {
        { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" }, { "truckerPhone", "+251912345678" },
        { "fromWoreda", "Bishoftu" }, { "toWoreda", "Addis Ababa" },
        { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") }, { "capacity", 150.0 }, { "pricePerQuintal", 120.0 }
    });

    private static SyncOperationRequest OrderCreate() => Op("order", "order-001", "create", new Dictionary<string, object>
    {
        { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", ListingId },
        { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
        { "cropType", "teff" }, { "quantity", 50 }, { "pricePerUnit", 45.5 }
    });
}

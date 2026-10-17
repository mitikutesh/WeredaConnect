using System.Linq.Expressions;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class SyncFunctionTests
{
    private readonly ISyncMetadataRepository _mockSyncMeta;
    private readonly ICropRepository _mockCropRepo;
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly IUserRepository _mockUserRepo;
    private readonly IOrderRepository _mockOrderRepo;
    private readonly SyncFunction _function;
    private readonly TestLambdaContext _context;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SyncFunctionTests()
    {
        _mockSyncMeta = Substitute.For<ISyncMetadataRepository>();
        _mockCropRepo = Substitute.For<ICropRepository>();
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _mockUserRepo = Substitute.For<IUserRepository>();
        _mockOrderRepo = Substitute.For<IOrderRepository>();
        _function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo);
        _context = new TestLambdaContext();

        // Default: delivery creates reference a live order with matching participants
        // (no trucker: the self-pickup tests rely on that; SetupTruckOrder adds one).
        _mockOrderRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId },
            { "buyerId", TestBuyerId },
            { "farmerId", TestFarmerId },
            { "status", "accepted" },
            { "version", "1" }
        });

        // Default: the order's single delivery slot is free (SIM-09), so a create writes the
        // slot claim and the delivery row together (ESC-08).
        _mockOrderRepo.TryCreateDeliveryWithSlotAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, AttributeValue>>(),
                Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.Written);

        // Default: no duplicate idempotency keys
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        // Default: listing exists with matching farmerId for delivery validation
        _mockCropRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "cropId", "00000000-0000-0000-0000-000000000004" },
            { "farmerId", "00000000-0000-0000-0000-000000000002" },
            { "status", "available" },
            { "version", "1" }
        });

        // Default: buyer exists with correct role for delivery validation
        _mockUserRepo.GetUserByIdAsync(Arg.Any<string>()).Returns(new UserDto
        {
            UserId = "00000000-0000-0000-0000-000000000003",
            PhoneNumber = "+251933333333",
            DisplayName = "Test User",
            Role = "buyer",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });
    }

    private static APIGatewayProxyRequest MakeRequest(object body) => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    private static SyncRequest MakeSyncRequest(params SyncOperationRequest[] operations) => new()
    {
        SyncBatchId = "batch-001",
        ClientId = "device-001",
        Operations = operations.ToList()
    };

    private static SyncOperationRequest MakeCropCreateOp(
        string opId = "op-001",
        string entityId = "crop-001",
        string farmerId = "farmer-001") => new()
        {
            OperationId = opId,
            IdempotencyKey = $"user:crop_listing:{entityId}:create:12345",
            EntityType = "crop_listing",
            EntityId = entityId,
            Action = "create",
            Data = new Dictionary<string, object>
        {
            { "cropType", "teff" },
            { "quantity", 50 },
            { "pricePerKg", 45.5 },
            { "farmerId", farmerId },
            { "woreda", "Jimma" },
            { "status", "active" }
        },
            Timestamp = "2026-03-15T10:00:00Z"
        };

    [Fact]
    public async Task FunctionHandler_ValidCreateOperation_Returns200WithSuccess()
    {
        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "teff" },
            { "version", "1" }
        };
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(serverData));

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.Contains("op-001", response.Body);
        Assert.Contains("batch-001", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_Create_InjectsFarmerReliability_FromListingHistory()
    {
        // Farmer history: 3 sold + 1 expired = 4 resolved → reliability 0.75.
        Dictionary<string, object> Crop(string status) =>
            new() { { "cropId", "c" }, { "status", status } };
        _mockCropRepo.QueryCropsAsync(
                farmerId: "farmer-001",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<double?>())
            .Returns((new List<Dictionary<string, object>>
            {
                Crop("sold"), Crop("sold"), Crop("sold"), Crop("expired"), Crop("available"),
            }, (Dictionary<string, AttributeValue>?)null));

        Dictionary<string, object>? capturedData = null;
        _mockCropRepo.CreateAsync("crop-001", Arg.Do<Dictionary<string, object>>(d => capturedData = d))
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "1" } }));

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(capturedData);
        Assert.True(capturedData!.ContainsKey("farmerReliability"));
        Assert.Equal(0.75, Convert.ToDouble(capturedData["farmerReliability"]), 6);
    }

    [Fact]
    public async Task FunctionHandler_Create_NoReliability_WhenTooFewResolvedListings()
    {
        // Only 2 resolved listings (< MinListingsForReliability=3) → unset (neutral).
        _mockCropRepo.QueryCropsAsync(
                farmerId: "farmer-001",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<double?>())
            .Returns((new List<Dictionary<string, object>>
            {
                new() { { "cropId", "c" }, { "status", "sold" } },
                new() { { "cropId", "c" }, { "status", "available" } },
            }, (Dictionary<string, AttributeValue>?)null));

        Dictionary<string, object>? capturedData = null;
        _mockCropRepo.CreateAsync("crop-001", Arg.Do<Dictionary<string, object>>(d => capturedData = d))
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "1" } }));

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        await _function.FunctionHandler(request, _context);

        Assert.NotNull(capturedData);
        Assert.False(capturedData!.ContainsKey("farmerReliability"));
    }

    [Fact]
    public async Task FunctionHandler_Create_ReliabilityFailure_DoesNotBlockCreate()
    {
        // A reliability read failure must never fail the crop creation.
        _mockCropRepo.QueryCropsAsync(
                farmerId: "farmer-001",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<double?>())
            .ThrowsAsync(new Exception("dynamo down"));
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "1" } }));

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_CreateSuccess_StoresIdempotencyKey()
    {
        var serverData = new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "1" } };
        _mockCropRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(serverData));

        var op = MakeCropCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        await _function.FunctionHandler(request, _context);

        // The fixture key does not start with the caller's id, so it is stored
        // scoped to the caller (SIM-48).
        await _mockSyncMeta.Received(1).StoreIdempotencyKeyAsync(
            "farmer-001:" + op.IdempotencyKey,
            op.OperationId,
            "crop_listing",
            "success");
    }

    [Fact]
    public async Task FunctionHandler_DuplicateIdempotencyKey_ReturnsDuplicate()
    {
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("duplicate", response.Body);

        // Should NOT call crop repo
        await _mockCropRepo.DidNotReceive()
            .CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    // ===== Caller-scoped idempotency keys (SIM-48, ESC-11, SEC-03) =====

    /// <summary>
    /// Makes the idempotency store behave like the real table: a key exists once
    /// it was stored. Returns the set of stored keys.
    /// </summary>
    private HashSet<string> UseStatefulIdempotencyStore()
    {
        var stored = new HashSet<string>(StringComparer.Ordinal);
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>())
            .Returns(ci => Task.FromResult(stored.Contains(ci.ArgAt<string>(0))));
        _mockSyncMeta
            .When(x => x.StoreIdempotencyKeyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => stored.Add(ci.ArgAt<string>(0)));
        return stored;
    }

    private void StubCropCreateSuccess() =>
        _mockCropRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", ci.ArgAt<string>(0) }, { "version", "1" } }));

    [Fact]
    public async Task Idempotency_KeyPreClaimedByAnotherFarmer_StillProcessesCallersCreate()
    {
        var stored = UseStatefulIdempotencyStore();
        StubCropCreateSuccess();
        const string victimKey = "farmer-001:crop_listing:crop-001:create:12345";

        // farmer-002 creates its own crop but sends farmer-001's key.
        var attackerOp = MakeCropCreateOp("op-attacker", "crop-attacker", "farmer-002") with
        {
            IdempotencyKey = victimKey
        };
        var attackerResponse = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(attackerOp), "farmer-002", "farmer"), _context);
        Assert.Contains("\"status\":\"success\"", attackerResponse.Body);

        // farmer-001 then syncs its own create with that key: it must be processed.
        var victimOp = MakeCropCreateOp() with { IdempotencyKey = victimKey };
        var victimResponse = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(victimOp), "farmer-001", "farmer"), _context);

        Assert.Equal(200, victimResponse.StatusCode);
        Assert.Contains("\"status\":\"success\"", victimResponse.Body);
        Assert.DoesNotContain("duplicate", victimResponse.Body);
        await _mockCropRepo.Received(1).CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>());
        Assert.Contains(victimKey, stored);
        Assert.Contains("farmer-002:" + victimKey, stored);
    }

    [Fact]
    public async Task Idempotency_KeyPreClaimedByAnotherTrucker_StillProcessesCallersTripCreate()
    {
        UseStatefulIdempotencyStore();
        const string victimKey = "trucker-001:trip:trip-001:create:12345";

        var attackerOp = MakeTripCreateOp("trucker-002", "op-attacker", "trip-attacker") with
        {
            IdempotencyKey = victimKey
        };
        var attackerResponse = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(attackerOp), "trucker-002", "trucker"), _context);
        Assert.Contains("\"status\":\"success\"", attackerResponse.Body);

        var victimOp = MakeTripCreateOp() with { IdempotencyKey = victimKey };
        var victimResponse = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(victimOp), "trucker-001", "trucker"), _context);

        Assert.Contains("\"status\":\"success\"", victimResponse.Body);
        Assert.DoesNotContain("duplicate", victimResponse.Body);
        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.Item.ContainsKey("tripId") && r.Item["tripId"].S == "trip-001"));
    }

    [Theory]
    [InlineData("farmer-001:crop_listing:crop-001:create:12345")] // mobile sync queue format
    [InlineData("web_create_crop-001")] // web repository format
    public async Task Idempotency_SameCallerRetry_ReturnsDuplicateAndDoesNotReprocess(string key)
    {
        UseStatefulIdempotencyStore();
        StubCropCreateSuccess();
        var op = MakeCropCreateOp() with { IdempotencyKey = key };

        var first = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);
        var retry = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        Assert.Contains("\"status\":\"success\"", first.Body);
        Assert.Contains("\"status\":\"duplicate\"", retry.Body);
        await _mockCropRepo.Received(1).CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Idempotency_DocumentedFormatKey_LookedUpAndStoredUnchanged()
    {
        // Records written before the fix used the raw key; a documented-format key
        // must map to the same stored value so a straddling retry stays a duplicate.
        StubCropCreateSuccess();
        const string key = "farmer-001:crop_listing:crop-001:create:12345";
        var op = MakeCropCreateOp() with { IdempotencyKey = key };

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        await _mockSyncMeta.Received(1).IdempotencyKeyExistsAsync(key);
        await _mockSyncMeta.Received(1).StoreIdempotencyKeyAsync(key, "op-001", "crop_listing", "success");
    }

    [Fact]
    public async Task Idempotency_UnprefixedKey_LookedUpAndStoredWithCallerPrefix()
    {
        StubCropCreateSuccess();
        var op = MakeCropCreateOp() with { IdempotencyKey = "web_create_crop-001" };

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        await _mockSyncMeta.Received(1).IdempotencyKeyExistsAsync("farmer-001:web_create_crop-001");
        await _mockSyncMeta.Received(1).StoreIdempotencyKeyAsync(
            "farmer-001:web_create_crop-001", "op-001", "crop_listing", "success");
        await _mockSyncMeta.DidNotReceive().IdempotencyKeyExistsAsync("web_create_crop-001");
    }

    [Fact]
    public async Task Idempotency_CallerIdWithColon_ReturnsErrorWithoutProcessing()
    {
        StubCropCreateSuccess();

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "bad:id", "farmer"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"status\":\"error\"", response.Body);
        Assert.Contains("SYNC_VALIDATION", response.Body);
        await _mockSyncMeta.DidNotReceive().IdempotencyKeyExistsAsync(Arg.Any<string>());
        await _mockSyncMeta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockCropRepo.DidNotReceive()
            .CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FunctionHandler_CreateConflict_ReturnsConflictWithServerData()
    {
        _mockCropRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Already exists."));

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "wheat" },
            { "farmerId", "farmer-001" },
            { "version", "3" }
        };
        _mockCropRepo.GetByIdAsync("crop-001").Returns(serverData);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
        Assert.Contains("wheat", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_CreateConflict_OtherFarmersListing_IsNotEchoed()
    {
        // Role-visibility rule: a farmer who replays another farmer's cropId must
        // get the conflict status but never that farmer's listing data.
        _mockCropRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Already exists."));
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "wheat" },
            { "farmerId", "farmer-999" },
            { "farmerPhone", "+251999999999" },
            { "version", "3" }
        });

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
        Assert.DoesNotContain("wheat", response.Body);
        Assert.DoesNotContain("+251999999999", response.Body);
        Assert.DoesNotContain("farmer-999", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UpdateOperation_ReturnsSuccess()
    {
        // Setup existing item for ownership check
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "version", "1" }
        });

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "teff" },
            { "quantity", "100" },
            { "version", "2" }
        };
        _mockCropRepo.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(serverData));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-update",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "quantity", 100 },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UpdateConflict_ReturnsConflictWithServerData()
    {
        // GetByIdAsync is called twice: once for ownership check, once after conflict
        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "version", "5" }
        };
        _mockCropRepo.GetByIdAsync("crop-001").Returns(serverData);

        _mockCropRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version mismatch."));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-conflict",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "quantity", 100 }, { "version", 2 } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DeleteOperation_ReturnsSuccess()
    {
        // Setup existing item for ownership check
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "version", "1" }
        });

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "status", "deleted" },
            { "version", "2" }
        };
        _mockCropRepo.DeleteAsync("crop-001", 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(serverData));

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-delete",
            IdempotencyKey = "user:crop_listing:crop-001:delete:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_MultipleMixedOperations_ReturnsResultsForEach()
    {
        // Op 1: create success
        _mockCropRepo.CreateAsync("crop-A", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-A" }, { "version", "1" } }));

        // Op 2: duplicate (the server looks the key up scoped to the caller)
        _mockSyncMeta.IdempotencyKeyExistsAsync("farmer-001:dup-key").Returns(true);

        var op1 = MakeCropCreateOp("op-A", "crop-A");
        var op2 = new SyncOperationRequest
        {
            OperationId = "op-B",
            IdempotencyKey = "dup-key",
            EntityType = "crop_listing",
            EntityId = "crop-B",
            Action = "create",
            Data = new Dictionary<string, object> { { "cropType", "wheat" }, { "farmerId", "farmer-001" } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op1, op2), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("op-A", response.Body);
        Assert.Contains("op-B", response.Body);
        Assert.Contains("success", response.Body);
        Assert.Contains("duplicate", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UnsupportedEntityType_ReturnsError()
    {
        var badOp = new SyncOperationRequest
        {
            OperationId = "op-bad",
            IdempotencyKey = "user:unknown:x:create:12345",
            EntityType = "unknown_entity",
            EntityId = "x-001",
            Action = "create",
            Data = new Dictionary<string, object> { { "foo", "bar" } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(badOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("Unsupported entity type", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "{{bad" };
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("SYNC_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_EmptyOperations_Returns400()
    {
        var syncRequest = new
        {
            syncBatchId = "batch-empty",
            clientId = "device-001",
            operations = Array.Empty<object>()
        };

        var request = MakeRequest(syncRequest);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("SYNC_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidAction_ReturnsError()
    {
        var badOp = new SyncOperationRequest
        {
            OperationId = "op-bad-action",
            IdempotencyKey = "user:crop_listing:x:patch:12345",
            EntityType = "crop_listing",
            EntityId = "x-001",
            Action = "patch",
            Data = new Dictionary<string, object> { { "foo", "bar" } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(badOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("Invalid action", response.Body);
    }

    [Fact]
    public async Task HostileOperationFields_CannotFireAMoneyAlarm()
    {
        // ESC-13: the sync log group carries the ORDER_ESCROW_* alarm filter, and every op is
        // logged with its client-chosen id and its result message (which echoes the action).
        var hostile = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.Hostile;
        SyncOperationRequest Op(string tag, string action) => new()
        {
            OperationId = $"{tag}-{hostile}",
            IdempotencyKey = $"{tag}:{hostile}",
            EntityType = "crop_listing",
            EntityId = hostile,
            Action = action,
            Data = new Dictionary<string, object> { { "foo", hostile } }
        };
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Is<string>(k => k.Contains("dup:"))).Returns(true);
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Is<string>(k => k.Contains("boom:")))
            .ThrowsAsync(new FormatException($"The input string \x27{hostile}\x27 was not in a correct format."));
        var request = MakeAuthenticatedRequest(
            MakeSyncRequest(Op("bad", hostile), Op("dup", "create"), Op("boom", "create")), "farmer-001");
        request.Headers = new Dictionary<string, string> { { "X-Correlation-Id", "ORDER_ESCROW_LINK_FAILED" } };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        var logs = ((TestLambdaLogger)_context.Logger).Buffer.ToString();
        Assert.Contains("Invalid action", logs);
        Assert.Contains("Duplicate idempotency key", logs);
        Assert.Contains("Error processing operation boom-", logs);
        Assert.DoesNotContain(WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens, logs.Contains);
    }

    [Fact]
    public async Task FunctionHandler_CreateWithNoData_ReturnsError()
    {
        var noDataOp = new SyncOperationRequest
        {
            OperationId = "op-no-data",
            IdempotencyKey = "user:crop_listing:x:create:12345",
            EntityType = "crop_listing",
            EntityId = "x-001",
            Action = "create"
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(noDataOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("requires data", response.Body);
    }

    // ===== Crop Listing Security Tests =====

    [Fact]
    public async Task FunctionHandler_Unauthenticated_Returns401()
    {
        var request = MakeRequest(MakeSyncRequest(MakeCropCreateOp()));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("UNAUTHORIZED", response.Body);
    }

    // SIM-40: buyers see the listing's farmerName/farmerPhone, so a create takes
    // them from the farmer's profile and ignores what the client sent.
    [Fact]
    public async Task CropCreate_SpoofedFarmerContact_IsReplacedFromProfile()
    {
        _mockUserRepo.GetUserByIdAsync("farmer-001").Returns(new UserDto
        {
            UserId = "farmer-001",
            PhoneNumber = "+251900100001",
            DisplayName = "Abebe Kebede",
            Role = "farmer",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "1" } }));
        var op = MakeCropCreateOp();
        op.Data!["farmerName"] = "Seeded Abebe (fake)";
        op.Data["farmerPhone"] = "+251911000001";

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        await _function.FunctionHandler(request, _context);

        await _mockCropRepo.Received(1).CreateAsync("crop-001", Arg.Is<Dictionary<string, object>>(d =>
            (string)d["farmerName"] == "Abebe Kebede"
            && (string)d["farmerPhone"] == "+251900100001"));
    }

    [Fact]
    public async Task CropCreate_FarmerIdMismatch_ReturnsUnauthorized()
    {
        var op = MakeCropCreateOp(farmerId: "farmer-999");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task CropCreate_UnknownFields_ReturnsValidationError()
    {
        var op = new SyncOperationRequest
        {
            OperationId = "op-unknown-fields",
            IdempotencyKey = "user:crop_listing:crop-001:create:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "farmerId", "farmer-001" },
                { "cropType", "teff" },
                { "isAdmin", true },
                { "secretField", "malicious" }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_VALIDATION", response.Body);
        Assert.Contains("Unknown fields", response.Body);
    }

    [Fact]
    public async Task CropUpdate_FarmerIdMismatch_ReturnsUnauthorized()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-999" },
            { "version", "1" }
        });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-update-unauth",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "quantity", 100 }, { "version", 1 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
    }

    [Theory]
    [InlineData("farmer-999")]
    [InlineData("trucker-002")]
    [InlineData("buyer-003")]
    public async Task CropUpdate_ChangedFarmerId_ReturnsValidationErrorAndDoesNotWrite(string newFarmerId)
    {
        // SIM-02: the owner rewrote farmerId to move the listing into another account.
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "version", "1" }
        });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-update-reassign",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "farmerId", newFarmerId },
                { "quantity", 100 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_VALIDATION: farmerId is immutable", response.Body);
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CropUpdate_ChangedFarmerPhone_ReturnsValidationErrorAndDoesNotWrite()
    {
        // SIM-02: spoofing the contact details buyers see on the listing.
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "farmerName", "Abebe Kebede" },
            { "farmerPhone", "+251911000001" },
            { "version", "1" }
        });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-update-spoof",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "farmerPhone", "+251922000002" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("CROP_VALIDATION: farmerPhone is immutable", response.Body);
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ===== SIM-17: geo/date rules on update as on create =====

    private static SyncOperationRequest MakeCropUpdateOp(Dictionary<string, object> data) => new()
    {
        OperationId = "op-update-geo",
        IdempotencyKey = "user:crop_listing:crop-001:update:12345",
        EntityType = "crop_listing",
        EntityId = "crop-001",
        Action = "update",
        Data = data
    };

    private void StubOwnedCrop() =>
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "version", "1" }
        });

    [Theory]
    [InlineData("latitude", 20.0)]
    [InlineData("latitude", 95.0)]
    [InlineData("latitude", -1.0)]
    [InlineData("longitude", 10.0)]
    [InlineData("longitude", 50.5)]
    public async Task CropUpdate_CoordinateOutsideEthiopia_ReturnsValidationErrorAndDoesNotWrite(
        string field, double value)
    {
        StubOwnedCrop();
        var op = MakeCropUpdateOp(new Dictionary<string, object>
        {
            { field, value }, { "quantity", 100 }, { "version", 1 }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains($"CROP_VALIDATION: {field} must be between", response.Body);
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("2027-01-01T00:00:00Z")]
    [InlineData("2099-06-30T12:00:00.000")]
    public async Task CropUpdate_FutureCreatedAt_ReturnsValidationErrorAndDoesNotWrite(string createdAt)
    {
        StubOwnedCrop();
        var op = MakeCropUpdateOp(new Dictionary<string, object>
        {
            { "createdAt", createdAt }, { "quantity", 100 }, { "version", 1 }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("CROP_VALIDATION: createdAt cannot be in the future.", response.Body);
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CropUpdate_MalformedCreatedAt_ReturnsValidationError()
    {
        StubOwnedCrop();
        var op = MakeCropUpdateOp(new Dictionary<string, object>
        {
            { "createdAt", "yesterday" }, { "version", 1 }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("CROP_VALIDATION: createdAt must be an ISO 8601 timestamp.", response.Body);
    }

    [Fact]
    public async Task CropUpdate_ValidCoordinatesAndPastCreatedAt_Succeeds()
    {
        StubOwnedCrop();
        _mockCropRepo.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "2" }
            }));
        var op = MakeCropUpdateOp(new Dictionary<string, object>
        {
            { "latitude", 9.03 }, { "longitude", 38.74 },
            { "createdAt", "2026-03-01T10:00:00Z" }, { "quantity", 100 }, { "version", 1 }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _mockCropRepo.Received(1).UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1);
    }

    [Fact]
    public async Task CropUpdate_NullCoordinates_AreTreatedAsAbsent()
    {
        // The clients send `latitude: null` when the GPS fix is missing (remote areas).
        StubOwnedCrop();
        _mockCropRepo.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "2" }
            }));
        var op = MakeCropUpdateOp(new Dictionary<string, object?>
        {
            { "latitude", null }, { "longitude", null }, { "quantity", 100 }, { "version", 1 }
        }!);
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("success", response.Body);
    }

    [Theory]
    [InlineData("latitude", 20.0)]
    [InlineData("longitude", 10.0)]
    public async Task CropCreate_CoordinateOutsideEthiopia_ReturnsValidationError(string field, double value)
    {
        var op = MakeCropCreateOp();
        op.Data![field] = value;
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains($"CROP_VALIDATION: {field} must be between", response.Body);
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_FutureCreatedAt_ReturnsValidationError()
    {
        var op = MakeCropCreateOp();
        op.Data!["createdAt"] = "2027-01-01T00:00:00Z";
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("CROP_VALIDATION: createdAt cannot be in the future.", response.Body);
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_LocalTimeCreatedAtWithinSkew_IsAccepted()
    {
        // Mobile sends local time without an offset (Ethiopia is UTC+3); parsed as
        // UTC that sits hours "ahead". The skew allowance must not reject it.
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "1" }
            }));
        var op = MakeCropCreateOp();
        op.Data!["createdAt"] = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-ddTHH:mm:ss.fff");
        op.Data["latitude"] = 9.03;
        op.Data["longitude"] = 38.74;
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task CropUpdate_EchoedIdentityFields_AreStrippedAndUpdateSucceeds()
    {
        // A client may resend identity fields unchanged (phone in the local 09
        // spelling). The update must go through without rewriting them.
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "farmerName", "Abebe Kebede" },
            { "farmerPhone", "+251911000001" },
            { "version", "1" }
        });
        Dictionary<string, object>? written = null;
        _mockCropRepo.UpdateAsync("crop-001", Arg.Do<Dictionary<string, object>>(d => written = d), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" },
                { "farmerId", "farmer-001" },
                { "version", "2" }
            }));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-update-echo",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "farmerId", "farmer-001" },
                { "farmerName", "Abebe Kebede" },
                { "farmerPhone", "0911000001" },
                { "quantity", 100 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.NotNull(written);
        Assert.False(written!.ContainsKey("farmerId"));
        Assert.False(written.ContainsKey("farmerName"));
        Assert.False(written.ContainsKey("farmerPhone"));
        Assert.True(written.ContainsKey("quantity"));
    }

    [Fact]
    public async Task CropDelete_FarmerIdMismatch_ReturnsUnauthorized()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-999" },
            { "version", "1" }
        });

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-delete-unauth",
            IdempotencyKey = "user:crop_listing:crop-001:delete:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp), "farmer-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
    }

    // ===== Trip Sync Operation Tests =====

    private static APIGatewayProxyRequest MakeAuthenticatedRequest(
        object body, string userId = "trucker-001", string? role = null, long? authTime = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }
        if (authTime != null)
        {
            authorizer["auth_time"] = authTime.Value.ToString();
        }
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = authorizer
            }
        };
    }

    private static SyncOperationRequest MakeTripCreateOp(
        string truckerId = "trucker-001",
        string opId = "op-trip-create",
        string entityId = "trip-001",
        Dictionary<string, object>? dataOverrides = null)
    {
        var data = new Dictionary<string, object>
        {
            { "truckerId", truckerId },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251912345678" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") },
            { "capacity", 150.0 },
            { "pricePerQuintal", 120.0 }
        };
        if (dataOverrides != null)
        {
            foreach (var kvp in dataOverrides)
            {
                data[kvp.Key] = kvp.Value;
            }
        }
        return new SyncOperationRequest
        {
            OperationId = opId,
            IdempotencyKey = $"user:trip:{entityId}:create:12345",
            EntityType = "trip",
            EntityId = entityId,
            Action = "create",
            Data = data
        };
    }

    private void SetupExistingTrip(string tripId, string truckerId, string status, int version,
        string? deletedAt = null, string? truckerPhone = null, double? pricePerQuintal = null,
        string? departureDate = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = tripId } },
            { "truckerId", new AttributeValue { S = truckerId } },
            { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:00:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:00:00Z" } },
            { "status", new AttributeValue { S = status } },
            { "version", new AttributeValue { N = version.ToString() } }
        };
        if (deletedAt != null)
        {
            item["deletedAt"] = new AttributeValue { S = deletedAt };
        }
        if (truckerPhone != null)
        {
            item["truckerPhone"] = new AttributeValue { S = truckerPhone };
        }
        if (pricePerQuintal != null)
        {
            item["pricePerQuintal"] = new AttributeValue
            {
                N = pricePerQuintal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        if (departureDate != null)
        {
            item["departureDate"] = new AttributeValue { S = departureDate };
        }
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { item }
            });
    }

    private static Dictionary<string, AttributeValue> MakeUpdatedTripAttributes(
        string tripId, int version)
    {
        return new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = tripId } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:00:00Z" } },
            { "updatedAt", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
            { "status", new AttributeValue { S = "open" } },
            { "version", new AttributeValue { N = version.ToString() } }
        };
    }

    // --- Trip Create Tests ---

    [Fact]
    public async Task TripCreate_ValidData_ReturnsSuccessWithVersion1()
    {
        var op = MakeTripCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.Contains("\"serverVersion\":1", response.Body);
        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Any<PutItemRequest>());
    }

    // SIM-39: the key is (tripId, createdAt), so a replayed create with a new
    // idempotency key used to add a second row for the same tripId.
    [Fact]
    public async Task TripCreate_ExistingTripId_ReturnsConflictWithoutWriting()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);

        var op = MakeTripCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"status\":\"conflict\"", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task TripCreate_AnotherTruckersTripId_ConflictsAndEchoesNothing()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);

        var op = MakeTripCreateOp(truckerId: "trucker-002");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-002", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("\"status\":\"conflict\"", response.Body);
        Assert.DoesNotContain("Tadesse Worku", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task TripCreate_DuplicateIdempotencyKey_ReturnsDuplicate()
    {
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(true);

        var op = MakeTripCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("duplicate", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task TripCreate_InvalidCapacity_ReturnsValidationError()
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object> { { "capacity", -1 } });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("capacity", response.Body);
    }

    [Fact]
    public async Task TripCreate_SameFromToWoreda_ReturnsValidationError()
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Bishoftu" }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("fromWoreda and toWoreda must be different", response.Body);
    }

    [Fact]
    public async Task TripCreate_ForcesStatusOpen()
    {
        var op = MakeTripCreateOp(); // no status in data
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.Item.ContainsKey("status") && r.Item["status"].S == "open"));
    }

    [Fact]
    public async Task TripCreate_MissingRequiredFromWoreda_ReturnsValidationError()
    {
        var data = new Dictionary<string, object>
        {
            { "truckerId", "trucker-001" },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251912345678" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") },
            { "capacity", 150.0 },
            { "pricePerQuintal", 120.0 }
        };
        var op = new SyncOperationRequest
        {
            OperationId = "op-trip-create",
            IdempotencyKey = "user:trip:trip-001:create:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "create",
            Data = data
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("fromWoreda", response.Body);
    }

    // --- F-SEC-01: Departure Date Validation Tests ---

    [Fact]
    public async Task TripCreate_PastDepartureDate_ReturnsValidationError()
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "departureDate", DateTime.UtcNow.AddDays(-1).ToString("o") }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("departureDate must be in the future", response.Body);
    }

    [Fact]
    public async Task TripCreate_DepartureDateOver30Days_ReturnsValidationError()
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "departureDate", DateTime.UtcNow.AddDays(31).ToString("o") }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("departureDate must be within 30 days", response.Body);
    }

    // --- F-SEC-02: Phone Validation Tests ---

    // SIM-40: the trip's contact comes from the trucker's profile, so a spoofed
    // or malformed client phone and name never reach buyers.
    [Theory]
    [InlineData("+251abc")]
    [InlineData("+251911000000")]
    public async Task TripCreate_ClientPhoneAndName_AreReplacedFromProfile(string phone)
    {
        _mockUserRepo.GetUserByIdAsync("trucker-001").Returns(new UserDto
        {
            UserId = "trucker-001",
            PhoneNumber = "+251900100002",
            DisplayName = "Chaltu Tolosa",
            Role = "trucker",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "truckerPhone", phone },
            { "truckerName", "Someone Else" }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.Item["truckerPhone"].S == "+251900100002"
            && r.Item["truckerName"].S == "Chaltu Tolosa"));
    }

    [Fact]
    public async Task TripCreate_OwnerProfileMissing_IsRejected()
    {
        _mockUserRepo.GetUserByIdAsync("trucker-001").Returns((UserDto?)null);

        var op = MakeTripCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("TRIP_VALIDATION", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    private static UserDto ProfileWithNames(string userId, string? displayName, string? fullName) => new()
    {
        UserId = userId,
        PhoneNumber = "+251900100002",
        DisplayName = displayName,
        FullName = fullName,
        Role = "trucker",
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1
    };

    // SIM-40: with no display name the legal name received from Fayda is shown.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TripCreate_NoDisplayName_UsesFaydaName(string? displayName)
    {
        _mockUserRepo.GetUserByIdAsync("trucker-001")
            .Returns(ProfileWithNames("trucker-001", displayName, "Chaltu Tolosa Gemechu"));
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "truckerName", "Ethiopian Commodity Exchange" }
        });

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        await _function.FunctionHandler(request, _context);

        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.Item["truckerName"].S == "Chaltu Tolosa Gemechu"));
    }

    // SIM-40: a profile with no name at all must not fall back to the client value.
    [Fact]
    public async Task TripCreate_ProfileWithoutAnyName_IsRejected()
    {
        _mockUserRepo.GetUserByIdAsync("trucker-001").Returns(ProfileWithNames("trucker-001", null, null));
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "truckerName", "Ethiopian Commodity Exchange" }
        });

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("Add your name to your profile", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task CropCreate_OwnerProfileMissing_IsRejected()
    {
        _mockUserRepo.GetUserByIdAsync("farmer-001").Returns((UserDto?)null);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("CROP_VALIDATION", response.Body);
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    // --- F-SEC-06: Delivered Terminal State Tests ---

    [Fact]
    public async Task TripUpdate_DeliveredTrip_ReturnsTerminalStateError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "delivered", 3);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "capacity", 200.0 },
                { "version", 3 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_TERMINAL_STATE", response.Body);
    }

    [Fact]
    public async Task TripDelete_DeliveredTrip_ReturnsTerminalStateError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "delivered", 3);

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = "user:trip:trip-001:delete:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 3 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_TERMINAL_STATE", response.Body);
    }

    [Fact]
    public async Task TripCreate_TruckerIdMismatch_ReturnsUnauthorized()
    {
        var op = MakeTripCreateOp(truckerId: "trucker-999");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_UNAUTHORIZED", response.Body);
    }

    // --- SIM-60: departureDate on update ---

    private static SyncOperationRequest MakeTripUpdateOp(Dictionary<string, object> data) => new()
    {
        OperationId = "op-trip-update",
        IdempotencyKey = "user:trip:trip-001:update:12345",
        EntityType = "trip",
        EntityId = "trip-001",
        Action = "update",
        Data = data
    };

    [Fact]
    public async Task TripUpdate_PastDepartureDate_ReturnsValidationErrorAndDoesNotWrite()
    {
        // An open trip could be moved into the past: the update path only parsed the date.
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("o"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", DateTime.UtcNow.AddDays(-1).ToString("o") }, { "version", 1 }
            }))), _context);

        Assert.Contains("TRIP_VALIDATION: departureDate must be in the future when changed.", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_DepartureDateOver30Days_ReturnsValidationError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("o"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", DateTime.UtcNow.AddDays(31).ToString("o") }, { "version", 1 }
            }))), _context);

        Assert.Contains("TRIP_VALIDATION: departureDate must be within 30 days.", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_FutureDepartureDate_Succeeds()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("o"));
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = MakeUpdatedTripAttributes("trip-001", 2) });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", DateTime.UtcNow.AddDays(5).ToString("o") }, { "version", 1 }
            }))), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("TRIP_VALIDATION", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_UnchangedPastDepartureEcho_IsAccepted()
    {
        // A departed trip still moves matched -> inTransit; a client that echoes the
        // stored (past) date must not be refused.
        var departed = DateTime.UtcNow.AddDays(-1).ToString("o");
        SetupExistingTrip("trip-001", "trucker-001", "matched", 2, departureDate: departed);
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = MakeUpdatedTripAttributes("trip-001", 3) });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", departed }, { "status", "inTransit" }, { "version", 2 }
            }))), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("departureDate must", response.Body);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("us-date")]
    public async Task TripUpdate_BlankOrNonIsoDepartureDate_IsRefusedAndNotWritten(string kind)
    {
        // departureDate is the browse index sort key: " " or "10/15/2026" sort ahead of
        // every real date, and the update path used to write them as sent.
        var value = kind == "blank"
            ? " "
            : DateTime.UtcNow.AddDays(5).ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("o"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", value }, { "version", 1 }
            }))), _context);

        Assert.Contains("TRIP_VALIDATION: departureDate must be a valid ISO 8601 date.", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripCreate_NonIsoDepartureDate_IsRefusedAndNotWritten()
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "departureDate", DateTime.UtcNow.AddDays(5).ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture) }
        });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker"), _context);

        Assert.Contains("TRIP_VALIDATION: departureDate must be a valid ISO 8601 date.", response.Body);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    /// <summary>
    /// A departure 5 days out at 06:00 UTC, sent as mobile and web do ("Z"), with an
    /// offset ("+03:00"), or without a zone like builds before 2026-06-18 ("none"),
    /// and the single form the server stores for all three.
    /// </summary>
    private static (string Sent, string Stored) DepartureDateIn(string zone)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var departure = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(5).AddHours(6), TimeSpan.Zero);
        var stored = departure.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", inv);
        var sent = zone switch
        {
            "Z" => stored,
            "+03:00" => departure.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", inv),
            _ => departure.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", inv),
        };
        return (sent, stored);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("+03:00")]
    [InlineData("none")]
    public async Task TripUpdate_IsoDepartureDate_IsAcceptedAndStoredAsUtc(string zone)
    {
        // An offset must not move the trip within StatusDepartureDateIndex, so every
        // accepted value is written in the one UTC form shipped clients send.
        var (sent, stored) = DepartureDateIn(zone);
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("o"));
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = MakeUpdatedTripAttributes("trip-001", 2) });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", sent }, { "version", 1 }
            }))), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("TRIP_VALIDATION", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeValues.Values.Any(v => v.S == stored)));
    }

    [Theory]
    [InlineData("+03:00")]
    [InlineData("none")]
    public async Task TripCreate_IsoDepartureDate_IsStoredAsUtc(string zone)
    {
        // A zone-less value is read as UTC, so a create queued by an old build still replays.
        var (sent, stored) = DepartureDateIn(zone);
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object> { { "departureDate", sent } });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker"), _context);

        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.TableName == "test-trips-table" && r.Item["departureDate"].S == stored));
    }

    [Fact]
    public async Task TripUpdate_UnchangedDepartureEchoedWithAnOffset_IsStoredAsUtc()
    {
        // The same-instant echo rule accepts another spelling of the stored date, but the
        // row keeps the UTC form, so the echo cannot shift the trip's browse rank.
        var departedUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(6), TimeSpan.Zero);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        SetupExistingTrip("trip-001", "trucker-001", "matched", 2,
            departureDate: departedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv));
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = MakeUpdatedTripAttributes("trip-001", 3) });

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripUpdateOp(new Dictionary<string, object>
            {
                { "departureDate", departedUtc.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", inv) },
                { "status", "inTransit" }, { "version", 2 }
            }))), _context);

        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeValues.Values.Any(v =>
                v.S == departedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", inv))
            && !r.ExpressionAttributeValues.Values.Any(v => v.S != null && v.S.EndsWith("+03:00"))));
    }

    // --- Trip Update Tests ---

    [Fact]
    public async Task TripUpdate_ValidData_ReturnsSuccess()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = MakeUpdatedTripAttributes("trip-001", 2)
            });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "capacity", 200.0 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.Contains("\"serverVersion\":2", response.Body);
    }

    [Fact]
    public async Task TripUpdate_VersionConflict_ReturnsConflictWithServerData()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 3);
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-conflict",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "capacity", 200.0 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
        Assert.Contains("\"serverVersion\":3", response.Body);
    }

    [Fact]
    public async Task TripUpdate_InvalidTransition_OpenToDelivered_ReturnsError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-transition",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_INVALID_TRANSITION", response.Body);
    }

    [Fact]
    public async Task TripUpdate_ValidTransition_OpenToMatched_Succeeds()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = MakeUpdatedTripAttributes("trip-001", 2)
            });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-matched",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "matched" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task TripUpdate_CancelledTrip_ReturnsAlreadyCancelled()
    {
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 2);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "capacity", 200.0 },
                { "version", 2 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_TERMINAL_STATE", response.Body);
    }

    [Fact]
    public async Task TripUpdate_TruckerIdMismatch_ReturnsUnauthorized()
    {
        SetupExistingTrip("trip-001", "trucker-999", "open", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "capacity", 200.0 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task TripUpdate_ChangedTruckerId_ReturnsValidationErrorAndDoesNotWrite()
    {
        // SIM-03: the owner rewrote truckerId to hand the trip to another account.
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-reassign",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "truckerId", "trucker-999" },
                { "capacity", 200.0 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION: truckerId is immutable", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_ChangedTruckerPhone_ReturnsValidationErrorAndDoesNotWrite()
    {
        // SIM-03: spoofing the contact details buyers see on the trip.
        SetupExistingTrip("trip-001", "trucker-001", "open", 1, truckerPhone: "+251912345678");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-spoof",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "truckerPhone", "+251922000002" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("TRIP_VALIDATION: truckerPhone is immutable", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_EchoedOwnIdentityFields_AreStrippedAndUpdateSucceeds()
    {
        // The web client resends its own truckerId on status transitions. An echo
        // (including the local 09 phone spelling) must neither fail nor be rewritten.
        SetupExistingTrip("trip-001", "trucker-001", "open", 1, truckerPhone: "+251912345678");
        UpdateItemRequest? sent = null;
        _mockDynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r))
            .Returns(new UpdateItemResponse
            {
                Attributes = MakeUpdatedTripAttributes("trip-001", 2)
            });

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-trip-echo",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "truckerId", "trucker-001" },
                { "truckerName", "Tadesse Worku" },
                { "truckerPhone", "0912345678" },
                { "capacity", 200.0 },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.NotNull(sent);
        Assert.DoesNotContain("truckerId", sent!.ExpressionAttributeNames.Values);
        Assert.DoesNotContain("truckerName", sent.ExpressionAttributeNames.Values);
        Assert.DoesNotContain("truckerPhone", sent.ExpressionAttributeNames.Values);
        Assert.Contains("capacity", sent.ExpressionAttributeNames.Values);
    }

    // --- Trip Delete Tests ---

    [Fact]
    public async Task TripDelete_SoftDelete_Succeeds()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 1);

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = "user:trip:trip-001:delete:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.Contains("\"serverVersion\":2", response.Body);
    }

    [Fact]
    public async Task TripDelete_VersionConflict_ReturnsConflict()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 3);
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = "user:trip:trip-001:delete:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
        Assert.Contains("\"serverVersion\":3", response.Body);
    }

    [Fact]
    public async Task TripDelete_AlreadyCancelled_ReturnsError()
    {
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 2);

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = "user:trip:trip-001:delete:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 2 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(deleteOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_TERMINAL_STATE", response.Body);
    }

    // ===== Per-User Rate Limiting Tests (F-11 Finding 3) =====

    [Fact]
    public async Task FunctionHandler_RateLimitExceeded_Returns429()
    {
        var mockRateLimiter = Substitute.For<IRateLimitService>();
        mockRateLimiter.CheckSyncAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(429, "SYNC_RATE_LIMITED",
                "Too many sync requests. Please try again later."));

        var rateLimitedFunction = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table", mockRateLimiter);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001");
        var response = await rateLimitedFunction.FunctionHandler(request, _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("SYNC_RATE_LIMITED", response.Body);
        Assert.Contains("Too many sync requests", response.Body);
        Assert.True(response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task FunctionHandler_RateLimitAllowed_ProcessesNormally()
    {
        var mockRateLimiter = Substitute.For<IRateLimitService>();
        mockRateLimiter.CheckSyncAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "teff" },
            { "version", "1" }
        };
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(serverData));

        var rateLimitedFunction = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table", mockRateLimiter,
            userRepo: _mockUserRepo);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer");
        var response = await rateLimitedFunction.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await mockRateLimiter.Received(1).RecordSyncAsync("farmer-001");
    }

    [Fact]
    public async Task FunctionHandler_RateLimitExceeded_DoesNotProcessOperations()
    {
        var mockRateLimiter = Substitute.For<IRateLimitService>();
        mockRateLimiter.CheckSyncAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(429, "SYNC_RATE_LIMITED",
                "Too many sync requests. Please try again later."));

        var rateLimitedFunction = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table", mockRateLimiter);

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001");
        await rateLimitedFunction.FunctionHandler(request, _context);

        // Should NOT process any operations
        await _mockCropRepo.DidNotReceive()
            .CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await mockRateLimiter.DidNotReceive().RecordSyncAsync(Arg.Any<string>());
    }

    // ===== Delivery disputeReason Validation Tests (F12-SEC-06: 500 char limit) =====

    [Fact]
    public async Task DeliveryUpdate_DisputeReasonTooLong_ReturnsError()
    {
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-1).ToString("o"));

        var longReason = new string('x', 501);
        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute",
            IdempotencyKey = "user:delivery:del-001:update:12345",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", longReason },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("disputeReason must be 1-500 characters", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_DisputeReasonEmpty_ReturnsError()
    {
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-1).ToString("o"));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute",
            IdempotencyKey = "user:delivery:del-001:update:12345",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", "" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("disputeReason must be 1-500 characters", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_DisputeReasonWithin500Chars_Accepted()
    {
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-1).ToString("o"));

        var validReason = new string('x', 500);
        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute",
            IdempotencyKey = "user:delivery:del-001:update:12345",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", validReason },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // Should not contain the validation error
        Assert.DoesNotContain("disputeReason must be", response.Body);
    }

    // ===== F12-SEC-02: 48-Hour Dispute Window Tests =====

    [Fact]
    public async Task DeliveryUpdate_DisputeWithin48Hours_Accepted()
    {
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-24).ToString("o"));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute-ok",
            IdempotencyKey = "user:delivery:del-001:update:dispute-ok",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", "Quality issue with delivery" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("DELIVERY_DISPUTE_EXPIRED", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_DisputeAfter48Hours_ReturnsExpiredError()
    {
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-49).ToString("o"));

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute-expired",
            IdempotencyKey = "user:delivery:del-001:update:dispute-expired",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", "Late dispute" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("DELIVERY_DISPUTE_EXPIRED", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_DisputeWithMissingConfirmedAt_ReturnsValidationError()
    {
        // Use standard setup which doesn't include confirmedAt
        SetupExistingDelivery("del-001", "trucker-001", "delivered", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-dispute-no-confirmed",
            IdempotencyKey = "user:delivery:del-001:update:dispute-no-confirmed",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "disputed" },
                { "disputeReason", "Missing confirmedAt" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("confirmedAt is missing", response.Body);
    }

    // ===== QR Nonce Cross-Validation Tests =====

    [Fact]
    public async Task DeliveryUpdate_PickedUpWithMatchingNonce_Succeeds()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-pickup-nonce",
            IdempotencyKey = "user:delivery:del-001:update:pickup-nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "scannedQrNonce", "nonce-abc-123" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_PickedUpWithMismatchedNonce_ReturnsError()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-pickup-bad-nonce",
            IdempotencyKey = "user:delivery:del-001:update:pickup-bad-nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "scannedQrNonce", "wrong-nonce-xyz" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("DELIVERY_NONCE_MISMATCH", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_PickedUpWithMissingNonce_WhenGeneratorSynced_ReturnsError()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-pickup-no-nonce",
            IdempotencyKey = "user:delivery:del-001:update:pickup-no-nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("DELIVERY_NONCE_MISSING", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_PickedUpWithoutGeneratorNonce_AllowsTransition()
    {
        // Generator hasn't synced qrNonce yet — offline-first backward compat
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-pickup-no-gen-nonce",
            IdempotencyKey = "user:delivery:del-001:update:pickup-no-gen-nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "trucker-001");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_FarmerPersistsQrNonce_Succeeds()
    {
        // Regression (review A): the FARMER generates the pickup QR and persists
        // its nonce via a status-less update. Previously the non-status update
        // branch required the trucker, so the farmer's persist was rejected and
        // the nonce was never stored (verification silently failed open).
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);

        var persistOp = new SyncOperationRequest
        {
            OperationId = "op-nonce-persist",
            IdempotencyKey = "farmer-001:delivery:del-001:update:nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "qrNonce", "farmer-generated-nonce-xyz" },
                { "version", 1 }
            }
        };

        // farmer-001 is the delivery's farmer (see SetupExistingDelivery).
        var request = MakeAuthenticatedRequest(MakeSyncRequest(persistOp), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_BuyerPersistsQrNonce_IsRejected()
    {
        // The nonce-persist exception is farmer-only; a buyer (non-generator)
        // status-less qrNonce write must still be rejected.
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);

        var persistOp = new SyncOperationRequest
        {
            OperationId = "op-nonce-buyer",
            IdempotencyKey = "buyer-001:delivery:del-001:update:nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "qrNonce", "buyer-tries-to-set-nonce" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(persistOp), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
    }

    // SIM-41: the trucker could set his own nonce and then confirm pickup with it.
    [Fact]
    public async Task DeliveryUpdate_TruckerSentQrNonce_IsNeverWritten()
    {
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);

        var op = new SyncOperationRequest
        {
            OperationId = "op-nonce-trucker",
            IdempotencyKey = "trucker-001:delivery:del-001:update:nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "qrNonce", "trucker-chosen-nonce" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001");
        await _function.FunctionHandler(request, _context);

        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeValues.Values.Any(v => v.S == "trucker-chosen-nonce")));
    }

    [Fact]
    public async Task DeliveryCreate_ClientQrNonce_IsNotStored()
    {
        SetupTruckOrder();
        var op = MakeDeliveryCreateOp();
        op.Data!["qrNonce"] = "trucker-chosen-nonce";

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("success", response.Body);
        await ReceivedDeliveryWrite(item => !item.ContainsKey("qrNonce"));
    }

    [Fact]
    public async Task PickedUp_WithSecretStore_IgnoresOnRecordNonce()
    {
        // A row written before SIM-41 may carry a trucker-authored nonce. With the
        // secret store in place only the farmer's stored nonce counts, so this is
        // treated as "no nonce yet" and a money-bearing pickup is deferred.
        var mockSecret = Substitute.For<IDeliveryQrSecretRepository>();
        mockSecret.GetAsync("del-001").Returns((DeliveryQrSecret?)null);
        var mockEscrow = Substitute.For<IEscrowRepository>();
        mockEscrow.GetByDeliveryIdAsync("del-001").Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-001" },
            { "status", "escrowed" }
        });
        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: mockEscrow, qrSecretRepo: mockSecret);
        // Recent row (inside the defer window) carrying a trucker-authored nonce.
        var item = DeliveryItemForOrder();
        item["qrNonce"] = new AttributeValue { S = "trucker-chosen-nonce" };
        item["createdAt"] = new AttributeValue { S = DateTime.UtcNow.AddDays(-1).ToString("o") };
        SetupDeliveryQuery(item);

        var op = new SyncOperationRequest
        {
            OperationId = "op-pickup-own-nonce",
            IdempotencyKey = "trucker-001:delivery:del-001:update:pickup",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "pickedUp" },
                { "scannedQrNonce", "trucker-chosen-nonce" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001");
        var response = await function.FunctionHandler(request, _context);

        Assert.Contains("\"status\":\"deferred\"", response.Body);
    }

    // SIM-39: a create reusing an existing deliveryId conflicts, writes nothing and
    // does not claim the order's delivery slot (it would stay stuck).
    [Fact]
    public async Task DeliveryCreate_ExistingDeliveryId_ConflictsWithoutClaimOrWrite()
    {
        SetupTruckOrder();
        SetupDeliveryQuery(DeliveryItemForOrder());

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("\"status\":\"conflict\"", response.Body);
        await DidNotWriteDelivery();
    }

    // ===== Delivery confirmation → escrow + transition regression tests =====

    [Fact]
    public async Task DeliveryUpdate_PickedUpToDelivered_ByBuyer_WithCleanPayload_Succeeds()
    {
        // Regression: the buyer confirms delivery straight from pickedUp (no
        // separate in-transit step), sending the exact payload the mobile client
        // now emits — status + version only (deliveryId/timestamps stripped).
        // Previously this failed both as DELIVERY_INVALID_TRANSITION and, when the
        // client still sent deliveryId/confirmedAt/updatedAt, as "Unknown fields".
        SetupExistingDelivery("del-001", "trucker-001", "pickedUp", 1);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-deliver",
            IdempotencyKey = "buyer-001:delivery:del-001:update:deliver",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("Unknown fields", response.Body);
        Assert.DoesNotContain("INVALID_TRANSITION", response.Body);
    }

    [Fact]
    public async Task DeliveryConfirmed_StampsLinkedEscrowForAutoRelease()
    {
        // Regression: confirming delivery must stamp the linked escrow's
        // deliveryConfirmedAt + disputeWindowEndsAt so the auto-release cron pays
        // the farmer (and the auto-refund cron skips it). Previously nothing wrote
        // deliveryConfirmedAt, so a delivered order was refunded after 7 days.
        var mockEscrow = Substitute.For<IEscrowRepository>();
        mockEscrow.GetByDeliveryIdAsync("del-001").Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-001" },
            { "createdAt", "2026-02-16T10:00:00Z" },
            { "status", "escrowed" }
        });

        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: mockEscrow);

        // A money-bearing delivery now requires QR proof to confirm (fail-closed): the farmer's
        // nonce is on the record (legacy path, no secret table wired here) and the buyer echoes it.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-escrow");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-deliver-escrow",
            IdempotencyKey = "buyer-001:delivery:del-001:update:deliver-escrow",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "nonce-escrow" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await mockEscrow.Received(1).UpdateStatusAsync(
            "esc-001",
            Arg.Any<string>(),
            "escrowed",
            "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                f.ContainsKey("deliveryConfirmedAt") && f.ContainsKey("disputeWindowEndsAt")));
    }

    [Fact]
    public async Task DeliveryConfirmed_AlreadyStampedEscrow_DoesNotReStamp()
    {
        // Idempotent replay: an escrow already carrying deliveryConfirmedAt must
        // not be stamped again (avoids resetting the dispute window).
        var mockEscrow = Substitute.For<IEscrowRepository>();
        mockEscrow.GetByDeliveryIdAsync("del-001").Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-001" },
            { "createdAt", "2026-02-16T10:00:00Z" },
            { "status", "escrowed" },
            { "deliveryConfirmedAt", "2026-02-18T09:00:00Z" }
        });

        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: mockEscrow);

        // Give the delivery a nonce + echo it so the confirmation actually SUCCEEDS and reaches
        // the stamp path — proving the skip is due to idempotency, not a failed/ deferred op.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-replay");

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-deliver-replay",
            IdempotencyKey = "buyer-001:delivery:del-001:update:deliver-replay",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "nonce-replay" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(updateOp), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await mockEscrow.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    private void SetupExistingDeliveryWithQrNonce(
        string deliveryId, string truckerId, string status, int version, string qrNonce)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = deliveryId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "farmerName", new AttributeValue { S = "Abebe" } },
            { "farmerPhone", new AttributeValue { S = "+251911111111" } },
            { "truckerId", new AttributeValue { S = truckerId } },
            { "truckerName", new AttributeValue { S = "Tadesse" } },
            { "truckerPhone", new AttributeValue { S = "+251922222222" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "buyerName", new AttributeValue { S = "Mekonnen" } },
            { "buyerPhone", new AttributeValue { S = "+251933333333" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "qrNonce", new AttributeValue { S = qrNonce } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } }
        };

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r =>
                r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { item }
            });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = item
            });
    }

    private void SetupExistingDelivery(string deliveryId, string truckerId, string status, int version)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = deliveryId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "farmerName", new AttributeValue { S = "Abebe" } },
            { "farmerPhone", new AttributeValue { S = "+251911111111" } },
            { "truckerId", new AttributeValue { S = truckerId } },
            { "truckerName", new AttributeValue { S = "Tadesse" } },
            { "truckerPhone", new AttributeValue { S = "+251922222222" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "buyerName", new AttributeValue { S = "Mekonnen" } },
            { "buyerPhone", new AttributeValue { S = "+251933333333" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } }
        };

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r =>
                r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { item }
            });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = item
            });
    }

    private void SetupExistingDeliveryWithConfirmedAt(
        string deliveryId, string truckerId, string status, int version, string confirmedAt)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = deliveryId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "farmerName", new AttributeValue { S = "Abebe" } },
            { "farmerPhone", new AttributeValue { S = "+251911111111" } },
            { "truckerId", new AttributeValue { S = truckerId } },
            { "truckerName", new AttributeValue { S = "Tadesse" } },
            { "truckerPhone", new AttributeValue { S = "+251922222222" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "buyerName", new AttributeValue { S = "Mekonnen" } },
            { "buyerPhone", new AttributeValue { S = "+251933333333" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "status", new AttributeValue { S = status } },
            { "confirmedAt", new AttributeValue { S = confirmedAt } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } }
        };

        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r =>
                r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { item }
            });

        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = item
            });
    }

    // ===== Delivery Participant Validation Tests (SEC-VULN-002) =====

    private const string TestTruckerId = "00000000-0000-0000-0000-000000000001";
    private const string TestFarmerId = "00000000-0000-0000-0000-000000000002";
    private const string TestBuyerId = "00000000-0000-0000-0000-000000000003";
    private const string TestListingId = "00000000-0000-0000-0000-000000000004";
    private const string TestTripId = "00000000-0000-0000-0000-000000000005";
    private const string TestOrderId = "00000000-0000-0000-0000-000000000006";

    private static SyncOperationRequest MakeDeliveryCreateOp(
        string? truckerId = null,
        string? farmerId = null,
        string? buyerId = null,
        string? listingId = null,
        string opId = "op-del-create",
        string entityId = "del-001")
    {
        return new SyncOperationRequest
        {
            OperationId = opId,
            IdempotencyKey = $"user:delivery:{entityId}:create:12345",
            EntityType = "delivery",
            EntityId = entityId,
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "tripId", TestTripId },
                { "orderId", TestOrderId },
                { "listingId", listingId ?? TestListingId },
                { "farmerId", farmerId ?? TestFarmerId },
                { "farmerName", "Abebe" },
                { "farmerPhone", "+251911111111" },
                { "truckerId", truckerId ?? TestTruckerId },
                { "truckerName", "Tadesse" },
                { "truckerPhone", "+251922222222" },
                { "buyerId", buyerId ?? TestBuyerId },
                { "buyerName", "Mekonnen" },
                { "buyerPhone", "+251933333333" },
                { "qrToken", "qr-token-abc123" }
            }
        };
    }

    [Fact]
    public async Task DeliveryCreate_ValidParticipants_ReturnsSuccess()
    {
        SetupTruckOrder();
        var op = MakeDeliveryCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_FarmerIdMismatchListingOwner_ReturnsError()
    {
        // Listing owner is TestFarmerId (from default mock), but delivery says different farmer
        var mismatchedFarmer = "00000000-0000-0000-0000-000000000099";
        var op = MakeDeliveryCreateOp(farmerId: mismatchedFarmer);
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_PARTICIPANT_MISMATCH", response.Body);
        Assert.Contains("farmerId does not match listing owner", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_ListingNotFound_ReturnsError()
    {
        var missingListingId = "00000000-0000-0000-0000-000000000088";
        _mockCropRepo.GetByIdAsync(missingListingId).Returns((Dictionary<string, object>?)null);

        var op = MakeDeliveryCreateOp(listingId: missingListingId);
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("Referenced listing not found", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_DeletedListing_ReturnsError()
    {
        _mockCropRepo.GetByIdAsync(TestListingId).Returns(new Dictionary<string, object>
        {
            { "cropId", TestListingId },
            { "farmerId", TestFarmerId },
            { "status", "deleted" },
            { "deletedAt", "2026-02-15T10:00:00Z" },
            { "version", "1" }
        });

        var op = MakeDeliveryCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("listing has been deleted", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_BuyerNotFound_ReturnsError()
    {
        var unknownBuyer = "00000000-0000-0000-0000-000000000077";
        _mockUserRepo.GetUserByIdAsync(unknownBuyer).Returns((UserDto?)null);

        var op = MakeDeliveryCreateOp(buyerId: unknownBuyer);
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("buyerId does not reference a valid user", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_BuyerHasWrongRole_ReturnsError()
    {
        var farmerActingAsBuyer = "00000000-0000-0000-0000-000000000066";
        _mockUserRepo.GetUserByIdAsync(farmerActingAsBuyer).Returns(new UserDto
        {
            UserId = farmerActingAsBuyer,
            PhoneNumber = "+251944444444",
            Role = "farmer",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });

        var op = MakeDeliveryCreateOp(buyerId: farmerActingAsBuyer);
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_PARTICIPANT_MISMATCH", response.Body);
        Assert.Contains("buyerId must reference a buyer", response.Body);
    }

    // ===== Role-Based Create Authorization Tests =====
    // Verifies that only the correct role can create each entity type,
    // closing the bypass where any authenticated user could set their own
    // userId as the owner field and create any entity type.

    private static SyncOperationRequest MakeOrderCreateOp(
        string buyerId = "buyer-001",
        string opId = "op-order-create",
        string entityId = "order-001",
        Dictionary<string, object>? dataOverrides = null)
    {
        // 50 kg of farmer-001's teff at 45.5 ETB/kg (see SetupListing).
        var data = new Dictionary<string, object>
        {
            { "buyerId", buyerId },
            { "farmerId", "farmer-001" },
            { "listingId", "listing-001" },
            { "buyerName", "Meron Alemu" },
            { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" },
            { "quantity", 50 },
            { "pricePerUnit", 45.5 }
        };
        if (dataOverrides != null)
        {
            foreach (var kvp in dataOverrides)
            {
                data[kvp.Key] = kvp.Value;
            }
        }

        return new SyncOperationRequest
        {
            OperationId = opId,
            IdempotencyKey = $"user:order:{entityId}:create:12345",
            EntityType = "order",
            EntityId = entityId,
            Action = "create",
            Data = data
        };
    }

    /// <summary>
    /// The listing MakeOrderCreateOp references: farmer-001's teff, 10 quintals at 45.5 ETB/kg.
    /// </summary>
    private void SetupListing(
        string listingId = "listing-001",
        string farmerId = "farmer-001",
        string cropType = "teff",
        double quantityQuintals = 10,
        double pricePerKg = 45.5,
        string status = "available")
    {
        _mockCropRepo.GetByIdAsync(listingId).Returns(new Dictionary<string, object>
        {
            { "cropId", listingId }, { "listingId", listingId }, { "farmerId", farmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", cropType },
            { "quantity", quantityQuintals }, { "pricePerKg", pricePerKg },
            { "status", status }, { "version", "1" }
        });
    }

    private Dictionary<string, object>? _createdOrder;

    /// <summary>Let order creates succeed and capture the record handed to the repository.</summary>
    private void SetupOrderCreateSucceeds()
    {
        // Return a plain record (as the DynamoDB repository does) rather than echoing the
        // request dictionary, whose JsonElement values the response serializer rejects.
        _mockOrderRepo.CreateAsync(Arg.Any<string>(), Arg.Do<Dictionary<string, object>>(d => _createdOrder = d))
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", ci.Arg<string>() }, { "status", "created" }, { "version", "1" }
            }));
    }

    private async Task<string> CreateOrderAsBuyer(SyncOperationRequest op)
    {
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    [Fact]
    public async Task CropCreate_BuyerRole_ReturnsUnauthorized()
    {
        // Buyer sets buyerId = their own userId — server must reject on role, not just ownership
        var op = MakeCropCreateOp(farmerId: "buyer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only farmers can create crop listings", response.Body);
    }

    [Fact]
    public async Task CropCreate_TruckerRole_ReturnsUnauthorized()
    {
        var op = MakeCropCreateOp(farmerId: "trucker-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only farmers can create crop listings", response.Body);
    }

    [Fact]
    public async Task CropCreate_NoRole_ReturnsUnauthorized()
    {
        // Missing role claim (unauthenticated / stripped JWT) must also be rejected
        var op = MakeCropCreateOp(farmerId: "farmer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001"); // no role
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("CROP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only farmers can create crop listings", response.Body);
    }

    [Fact]
    public async Task TripCreate_FarmerRole_ReturnsUnauthorized()
    {
        // Farmer sets truckerId = their own userId — must be rejected on role
        var op = MakeTripCreateOp(truckerId: "farmer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only truckers can create trip listings", response.Body);
    }

    [Fact]
    public async Task TripCreate_BuyerRole_ReturnsUnauthorized()
    {
        var op = MakeTripCreateOp(truckerId: "buyer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only truckers can create trip listings", response.Body);
    }

    [Fact]
    public async Task TripCreate_NoRole_ReturnsUnauthorized()
    {
        var op = MakeTripCreateOp(truckerId: "trucker-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001"); // no role
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_UNAUTHORIZED", response.Body);
        Assert.Contains("Only truckers can create trip listings", response.Body);
    }

    [Fact]
    public async Task OrderCreate_FarmerRole_ReturnsUnauthorized()
    {
        // Farmer sets buyerId = their own userId — must be rejected on role
        var op = MakeOrderCreateOp(buyerId: "farmer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        Assert.Contains("Only buyers can create orders", response.Body);
    }

    [Fact]
    public async Task OrderCreate_TruckerRole_ReturnsUnauthorized()
    {
        var op = MakeOrderCreateOp(buyerId: "trucker-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        Assert.Contains("Only buyers can create orders", response.Body);
    }

    [Fact]
    public async Task OrderCreate_NoRole_ReturnsUnauthorized()
    {
        var op = MakeOrderCreateOp(buyerId: "buyer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001"); // no role
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        Assert.Contains("Only buyers can create orders", response.Body);
    }

    [Fact]
    public async Task OrderCreate_ValidBuyerRole_DoesNotReturnRoleError()
    {
        // Sanity check: a proper buyer role should NOT get a role error
        // (will fail later at buyerId ownership or validation, but NOT role check)
        var op = MakeOrderCreateOp(buyerId: "buyer-001");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("Only buyers can create orders", response.Body);
    }

    // --- SIM-05: order create is bound to the server's listing and trip records ---

    [Fact]
    public async Task OrderCreate_Valid_ComputesMoneyFromListingAndTrip()
    {
        SetupListing();
        SetupExistingTrip("trip-001", "trucker-001", "open", 1, pricePerQuintal: 120);
        SetupOrderCreateSucceeds();
        var op = MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }, { "truckerName", "spoofed" },
            { "farmerName", "spoofed" }, { "transportCost", 60.0 }
        });

        var body = await CreateOrderAsBuyer(op);

        Assert.Contains("success", body);
        Assert.NotNull(_createdOrder);
        var order = _createdOrder!;
        Assert.Equal(2275.0, (double)order["cropCost"]);    // 50 kg x 45.5 ETB/kg
        Assert.Equal(60.0, (double)order["transportCost"]); // 0.5 quintal x 120 ETB/quintal
        var rate = (double)order["commissionRate"];
        Assert.Equal(2275.0 * rate, (double)order["commissionAmount"], 6);
        Assert.Equal(2275.0 + 60.0 + 2275.0 * rate, (double)order["orderTotal"], 6);
        Assert.Equal("Abebe Kebede", order["farmerName"]);
        Assert.Equal("Tadesse Worku", order["truckerName"]);
        Assert.Equal("created", order["status"]);
    }

    [Fact]
    public async Task OrderCreate_WithoutTrip_HasZeroTransportCost()
    {
        SetupListing();
        SetupOrderCreateSucceeds();

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("success", body);
        Assert.Equal(0.0, (double)_createdOrder!["transportCost"]);
        Assert.Equal(2275.0 + 2275.0 * (double)_createdOrder["commissionRate"],
            (double)_createdOrder["orderTotal"], 6);
    }

    [Fact]
    public async Task OrderCreate_ListingNotFound_ReturnsValidationError()
    {
        _mockCropRepo.GetByIdAsync("listing-001").Returns((Dictionary<string, object>?)null);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("ORDER_VALIDATION: Listing not found", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_ListingNotAvailable_ReturnsValidationError()
    {
        SetupListing(status: "reserved");

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("ORDER_VALIDATION: Listing is not available", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_FarmerIdNotListingOwner_ReturnsValidationError()
    {
        // The fleet run priced orders against a farmer who did not own the listing.
        SetupListing(farmerId: "farmer-999");

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("ORDER_VALIDATION: farmerId must be the farmer on the listing", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_CropTypeMismatch_ReturnsValidationError()
    {
        SetupListing(cropType: "wheat");

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("ORDER_VALIDATION: cropType must match the listing", body);
    }

    [Fact]
    public async Task OrderCreate_PriceBelowListing_ReturnsValidationError()
    {
        // 1 ETB/kg on a 120 ETB/kg listing was accepted and priced before SIM-05.
        SetupListing(pricePerKg: 120);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "pricePerUnit", 1.0 }
        }));

        Assert.Contains("ORDER_VALIDATION: pricePerUnit must equal the listing price (120.00 ETB/kg)", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_QuantityExceedsStock_ReturnsValidationError()
    {
        // 0.4 quintal = 40 kg in stock, order asks for 50 kg.
        SetupListing(quantityQuintals: 0.4);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp());

        Assert.Contains("ORDER_VALIDATION: quantity (50 kg) exceeds the listing stock (40 kg)", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_WholeListingAtMaxStock_Succeeds()
    {
        // A listing at its 10,000-quintal maximum is 1,000,000 kg; the buyer can order all of it.
        SetupListing(quantityQuintals: 10000, pricePerKg: 1000);
        SetupOrderCreateSucceeds();

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "quantity", 1000000 }, { "pricePerUnit", 1000 }
        }));

        Assert.Contains("\"status\":\"success\"", body);
        Assert.NotNull(_createdOrder);
        Assert.Equal(1_000_000_000.0, (double)_createdOrder!["cropCost"], 2);
    }

    [Fact]
    public async Task OrderCreate_QuantityAboveMaxOrderKg_ReturnsValidationError()
    {
        SetupListing(quantityQuintals: 10000);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "quantity", 1000001 }
        }));

        Assert.Contains("ORDER_VALIDATION: quantity must be between 0.01 and 1000000 kg.", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData("tripId", "trip-001")]
    [InlineData("truckerId", "trucker-001")]
    public async Task OrderCreate_TripAndTruckerNotTogether_ReturnsValidationError(string key, string value)
    {
        SetupListing();

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { key, value }
        }));

        Assert.Contains("ORDER_VALIDATION: tripId and truckerId must be provided together", body);
    }

    [Fact]
    public async Task OrderCreate_TripNotFound_ReturnsValidationError()
    {
        SetupListing();
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-missing" }, { "truckerId", "trucker-001" }
        }));

        Assert.Contains("ORDER_VALIDATION: Trip not found", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_TruckerIdNotTripOwner_ReturnsValidationError()
    {
        SetupListing();
        SetupExistingTrip("trip-001", "trucker-999", "open", 1, pricePerQuintal: 120);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }
        }));

        Assert.Contains("ORDER_VALIDATION: truckerId must be the trucker on the trip", body);
    }

    [Fact]
    public async Task OrderCreate_TripNotOpen_ReturnsValidationError()
    {
        SetupListing();
        SetupExistingTrip("trip-001", "trucker-001", "delivered", 3, pricePerQuintal: 120);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }
        }));

        Assert.Contains("ORDER_VALIDATION: Trip is not open for orders (status: delivered)", body);
    }

    [Theory]
    [InlineData(240.0)]
    [InlineData(-470.6)]
    public async Task OrderCreate_TransportCostNotDerivedFromTrip_ReturnsValidationError(double sent)
    {
        SetupListing();
        SetupExistingTrip("trip-001", "trucker-001", "open", 1, pricePerQuintal: 120);

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }, { "transportCost", sent }
        }));

        Assert.Contains("ORDER_VALIDATION: transportCost must equal quantity/100 x the trip pricePerQuintal (60.00 ETB)", body);
        await _mockOrderRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_TransportCostWithoutTrip_ReturnsValidationError()
    {
        SetupListing();

        var body = await CreateOrderAsBuyer(MakeOrderCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "transportCost", 240.0 }
        }));

        Assert.Contains("ORDER_VALIDATION: transportCost requires a tripId", body);
    }

    [Fact]
    public async Task OrderFunding_DecreasesListingStockInQuintals()
    {
        // SIM-07: a 500 kg order on a 60-quintal listing must leave 55 quintals, not 0.
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        try
        {
            SetupListing(quantityQuintals: 60);
            var orderRepo = Substitute.For<IOrderRepository>();
            orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
            {
                { "orderId", "order-001" }, { "buyerId", "buyer-001" }, { "farmerId", "farmer-001" },
                { "listingId", "listing-001" }, { "quantity", 500.0 },
                { "status", "created" }, { "version", "1" }
            });
            orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
                .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
                {
                    { "orderId", "order-001" }, { "buyerId", "buyer-001" }, { "farmerId", "farmer-001" },
                    { "listingId", "listing-001" }, { "quantity", 500.0 }, { "status", "funded" },
                    { "orderTotal", "23887.50" }, { "cropCost", "22750.00" },
                    { "paymentVerifiedAt", "2026-03-01T10:00:00.0000000Z" }, { "version", "3" }
                }));
            var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
                "test-trips-table", "test-deliveries-table",
                userRepo: _mockUserRepo, orderRepo: orderRepo);

            var op = new SyncOperationRequest
            {
                OperationId = "op-pay-stock",
                IdempotencyKey = "user:order:order-001:update:12345",
                EntityType = "order",
                EntityId = "order-001",
                Action = "update",
                Data = new Dictionary<string, object>
                {
                    { "status", "payment_submitted" },
                    { "paymentMethod", "telebirr" },
                    { "version", 1 }
                }
            };
            var response = await function.FunctionHandler(
                MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer", authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("success", response.Body);
            // ESC-01: the stock step is the idempotent per-order decrement (the 60 -> 55
            // quintal arithmetic is covered in CropRepositoryTests).
            await _mockCropRepo.Received(1).TryDecrementStockAsync("listing-001", "order-001", 5.0);
            await _mockCropRepo.DidNotReceive().UpdateAsync(
                "listing-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        }
    }

    [Theory]
    [InlineData("buyer-001")]
    [InlineData("farmer-001")]
    public async Task OrderUpdate_PaymentFailed_IsSystemOnly(string caller)
    {
        // ESC-01: a participant marking payment_failed while a Telebirr payment is in flight
        // let the buyer resubmit and pay twice; only the system may set it now.
        SetupExistingOrder(status: "payment_submitted");
        var op = new SyncOperationRequest
        {
            OperationId = "op-payment-failed",
            IdempotencyKey = $"{caller}:order:order-001:update:12345",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "status", "payment_failed" }, { "version", 1 } }
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), caller, caller == "buyer-001" ? "buyer" : "farmer"), _context);

        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        Assert.Contains("system-managed", response.Body);
    }

    [Fact]
    public async Task OrderUpdate_TelebirrPaymentFields_AreStrippedFromClientData()
    {
        // ESC-01: the Telebirr references behind a funding are server-only; a client cannot
        // plant one to make a later payment look like a redelivery.
        SetupExistingOrder(status: "created");
        SetupOrderUpdateSucceeds();
        var op = new SyncOperationRequest
        {
            OperationId = "op-plant-ref",
            IdempotencyKey = "buyer-001:order:order-001:update:12345",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "cancelReason", "changed my mind" },
                { "telebirrPaymentOrderId", "TB-PLANTED" },
                { "telebirrMerchOrderId", "WCO-PLANTED" },
                { "telebirrTransId", "TXN-PLANTED" },
                // ESC-45: the webhook's payment hold is server-only too.
                { "paymentHoldUntil", "2099-01-01T00:00:00.0000000Z" },
                { "version", 1 }
            }
        };

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.NotNull(_updatedOrderData);
        Assert.Equal("changed my mind", _updatedOrderData!["cancelReason"].ToString());
        Assert.False(_updatedOrderData.ContainsKey("telebirrPaymentOrderId"));
        Assert.False(_updatedOrderData.ContainsKey("telebirrMerchOrderId"));
        Assert.False(_updatedOrderData.ContainsKey("telebirrTransId"));
        Assert.False(_updatedOrderData.ContainsKey("paymentHoldUntil"));
    }

    // --- SIM-06: order update allow-list and immutability ---

    /// <summary>A stored order for buyer-001 / farmer-001 with server-computed money fields.</summary>
    private Dictionary<string, object> SetupExistingOrder(string status = "created", int version = 1)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "listingId", "listing-001" },
            { "buyerId", "buyer-001" }, { "farmerId", "farmer-001" },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 50.0 }, { "pricePerUnit", 45.5 },
            { "tripId", "trip-001" }, { "truckerId", "trucker-001" }, { "truckerName", "Tadesse Worku" },
            { "transportCost", 60.0 }, { "cropCost", 2275.0 }, { "commissionRate", 0.05 },
            { "commissionAmount", 113.75 }, { "orderTotal", 2448.75 },
            { "status", status }, { "version", version.ToString() }
        };
        _mockOrderRepo.GetByIdAsync("order-001").Returns(order);
        return order;
    }

    private Dictionary<string, object>? _updatedOrderData;

    private void SetupOrderUpdateSucceeds()
    {
        _mockOrderRepo.UpdateAsync("order-001", Arg.Do<Dictionary<string, object>>(d => _updatedOrderData = d), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" }, { "status", "cancelled" }, { "version", "2" }
            }));
    }

    private static SyncOperationRequest MakeOrderUpdateOp(Dictionary<string, object> data) => new()
    {
        OperationId = "op-order-update",
        IdempotencyKey = "user:order:order-001:update:12345",
        EntityType = "order",
        EntityId = "order-001",
        Action = "update",
        Data = data
    };

    private async Task<string> UpdateOrderAs(string userId, string role, Dictionary<string, object> data)
    {
        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderUpdateOp(data)), userId, role);
        var response = await _function.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    [Fact]
    public async Task OrderUpdate_UnknownField_ReturnsValidationError()
    {
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "cancelled" }, { "discountCode", "FREE" }
        });

        Assert.Contains("ORDER_VALIDATION: Unknown fields: discountCode", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("pricePerUnit", 1.0)]
    [InlineData("quantity", 9999.0)]
    [InlineData("transportCost", -470.6)]
    public async Task OrderUpdate_ChangedPriceInput_ReturnsValidationError(string field, double value)
    {
        // The fleet run rewrote these after create; orderTotal then no longer matched.
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "cancelled" }, { field, value }
        });

        Assert.Contains($"ORDER_VALIDATION: {field} is immutable and cannot be changed on update", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("buyerId", "buyer-999")]
    [InlineData("farmerId", "farmer-999")]
    [InlineData("truckerId", "trucker-999")]
    [InlineData("tripId", "trip-999")]
    [InlineData("listingId", "listing-999")]
    [InlineData("farmerName", "Someone Else")]
    public async Task OrderUpdate_ChangedParty_ReturnsValidationError(string field, string value)
    {
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { field, value }
        });

        Assert.Contains($"ORDER_VALIDATION: {field} is immutable and cannot be changed on update", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_AfterFunding_PriceInputsStillImmutable()
    {
        // Post-funding mutation was untested by the fleet run: the farmer accepts and
        // tries to raise the price in the same operation.
        SetupExistingOrder(status: "funded", version: 3);

        var body = await UpdateOrderAs("farmer-001", "farmer", new Dictionary<string, object>
        {
            { "status", "accepted" }, { "pricePerUnit", 500.0 }, { "version", 3 }
        });

        Assert.Contains("ORDER_VALIDATION: pricePerUnit is immutable and cannot be changed on update", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_AfterFunding_MoneyFieldsSentByClientAreDropped()
    {
        SetupExistingOrder(status: "funded", version: 3);
        SetupOrderUpdateSucceeds();
        SetupExistingTrip("trip-001", "trucker-001", "open", 1,
            departureDate: DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));

        var body = await UpdateOrderAs("farmer-001", "farmer", new Dictionary<string, object>
        {
            { "status", "accepted" }, { "orderTotal", 1.0 }, { "commissionAmount", 0.0 }, { "version", 3 }
        });

        Assert.Contains("success", body);
        Assert.NotNull(_updatedOrderData);
        Assert.False(_updatedOrderData!.ContainsKey("orderTotal"));
        Assert.False(_updatedOrderData.ContainsKey("commissionAmount"));
        Assert.Equal("accepted", _updatedOrderData["status"].ToString());
        // ESC-06: the accept stamps the pickup deadline in the same write.
        Assert.True(_updatedOrderData.ContainsKey("pickupDeadlineAt"));
    }

    [Fact]
    public async Task OrderUpdate_EchoedBookkeepingAndUnchangedFields_AreStrippedAndApplied()
    {
        // Exactly what the mobile client sends on cancel, plus unchanged identity echoes.
        SetupExistingOrder();
        SetupOrderUpdateSucceeds();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "orderId", "order-001" }, { "status", "cancelled" }, { "cancelReason", "changed my mind" },
            { "cancelledAt", "2026-09-25T08:00:00Z" }, { "updatedAt", "2026-09-25T08:00:00Z" },
            { "buyerId", "buyer-001" }, { "quantity", 50 }, { "transportCost", 60 }, { "version", 1 }
        });

        Assert.Contains("success", body);
        var written = _updatedOrderData!;
        Assert.Equal("cancelled", written["status"].ToString());
        Assert.Equal("changed my mind", written["cancelReason"].ToString());
        Assert.True(written.ContainsKey("cancelledAt"));           // server-stamped, not the client's
        Assert.NotEqual("2026-09-25T08:00:00Z", written["cancelledAt"].ToString());
        foreach (var dropped in new[] { "orderId", "updatedAt", "buyerId", "quantity", "transportCost", "version" })
        {
            Assert.False(written.ContainsKey(dropped), $"{dropped} should not be written");
        }
    }

    [Fact]
    public async Task OrderUpdate_OrderIdMismatch_ReturnsValidationError()
    {
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "orderId", "order-999" }, { "status", "cancelled" }
        });

        Assert.Contains("ORDER_VALIDATION: orderId does not match the operation entityId", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_ReasonTooLong_ReturnsValidationError()
    {
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "cancelled" }, { "cancelReason", new string('x', 501) }
        });

        Assert.Contains("ORDER_VALIDATION: cancelReason must not exceed 500 characters", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // --- SIM-08: client version resolution ---

    [Fact]
    public async Task OrderUpdate_WithoutAnyVersion_ReturnsValidationError()
    {
        // The old path fell back to the stored version and applied the update with no
        // concurrency check at all.
        SetupExistingOrder();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "cancelled" }
        });

        Assert.Contains("ORDER_VALIDATION: version is required for order updates", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_OpLevelClientVersion_IsHonoured()
    {
        // Contract-conformant clients send clientVersion on the operation, not in data.
        SetupExistingOrder(version: 4);
        var usedVersion = -1;
        _mockOrderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Do<int>(v => usedVersion = v))
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" }, { "status", "cancelled" }, { "version", "5" }
            }));
        var op = MakeOrderUpdateOp(new Dictionary<string, object> { { "status", "cancelled" } })
            with
        { ClientVersion = 4 };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        Assert.Equal(4, usedVersion);
    }

    [Fact]
    public async Task OrderUpdate_OpLevelClientVersion_WinsOverDataVersion()
    {
        SetupExistingOrder(version: 4);
        var usedVersion = -1;
        _mockOrderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Do<int>(v => usedVersion = v))
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" }, { "status", "cancelled" }, { "version", "5" }
            }));
        var op = MakeOrderUpdateOp(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 1 } })
            with
        { ClientVersion = 4 };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        Assert.Equal(4, usedVersion);
    }

    [Fact]
    public async Task CropUpdate_OpLevelClientVersion_IsHonoured()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" }, { "farmerId", "farmer-001" }, { "version", "3" }
        });
        _mockCropRepo.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 3)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "farmerId", "farmer-001" }, { "version", "4" }
            }));
        var op = new SyncOperationRequest
        {
            OperationId = "op-crop-cv",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            ClientVersion = 3,
            Data = new Dictionary<string, object> { { "quantity", 100 } }
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001"), _context);

        Assert.Contains("success", response.Body);
        Assert.Contains("\"serverVersion\":4", response.Body);
        await _mockCropRepo.Received(1).UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 3);
    }

    [Fact]
    public async Task TripUpdate_OpLevelClientVersion_IsHonoured()
    {
        SetupExistingTrip("trip-001", "trucker-001", "open", 3);
        UpdateItemRequest? sent = null;
        _mockDynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r))
            .Returns(new UpdateItemResponse { Attributes = MakeUpdatedTripAttributes("trip-001", 4) });
        var op = new SyncOperationRequest
        {
            OperationId = "op-trip-cv",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            ClientVersion = 3,
            Data = new Dictionary<string, object> { { "capacity", 200.0 } }
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        Assert.Equal("3", sent!.ExpressionAttributeValues[":expectedVersion"].N);
    }

    [Fact]
    public async Task OrderUpdate_CreatedToPaymentSubmitted_IsAllowed()
    {
        // Buyers pay immediately after placing an order, so the direct
        // created -> payment_submitted hop must be accepted (not rejected as an
        // invalid transition). Regression for the "An error occurred" bug.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", "created" },
            { "orderTotal", "500.00" },
            { "version", "1" }
        });
        orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" },
                { "status", "payment_submitted" },
                { "version", "2" }
            }));

        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: orderRepo);

        var op = new SyncOperationRequest
        {
            OperationId = "op-pay",
            IdempotencyKey = "user:order:order-001:update:12345",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "payment_submitted" },
                { "paymentMethod", "telebirr" },
                { "cbeTransactionId", "MOCK-TBR-1" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer", authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("Invalid status transition", response.Body);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task OrderUpdate_OnAnOrderTheMoneyMovedOn_FailsTheTransitionCheck_AndWritesNothing()
    {
        // ESC-09 (V8): a device still showing in_transit queues the delivered catch-up after the
        // escrow release moved the order to disbursing. The transition check runs before the version
        // check, so the answer is a permanent ORDER_VALIDATION (the client reverts to the server row).
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", "disbursing" },
            { "version", "7" }
        });

        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: orderRepo);

        var op = new SyncOperationRequest
        {
            OperationId = "op-late-delivered",
            IdempotencyKey = "user:order:order-001:update:12346",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "status", "delivered" }, { "version", 5 } }
        };
        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        // The body is JSON, which escapes the quotes around the statuses.
        Assert.Contains("ORDER_VALIDATION: Invalid status transition from \\u0027disbursing\\u0027 to \\u0027delivered\\u0027.", response.Body);
        await orderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await orderRepo.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task OrderUpdate_FundedToRejected_IsAllowedForFarmer()
    {
        // The farmer responds to a funded order with accept OR reject; the
        // funded -> rejected entry was missing from the transition map, so a
        // farmer's rejection bounced as an invalid transition. Pin it, including
        // the server-stamped respondedAt side-effect.
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", "funded" },
            { "version", "1" }
        });
        orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" },
                { "status", "rejected" },
                { "version", "2" }
            }));

        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: orderRepo);

        var op = new SyncOperationRequest
        {
            OperationId = "op-reject",
            IdempotencyKey = "farmer-001:order:order-001:update:12345",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "rejected" },
                { "version", 1 }
            }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("Invalid status transition", response.Body);
        Assert.Contains("success", response.Body);
        // Reject is a farmer response: the server stamps respondedAt.
        await orderRepo.Received(1).UpdateAsync(
            "order-001",
            Arg.Is<Dictionary<string, object>>(d =>
                d["status"].ToString() == "rejected" && d.ContainsKey("respondedAt")),
            Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_PaymentSubmitted_MockMode_CreatesOrderEscrow()
    {
        // Funding is the moment money becomes escrow-held: the order-funded escrow
        // record must be materialized so /escrow/history and the Payments screen
        // reflect it (escrow-protocol.md §Order-funded escrow).
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        try
        {
            var orderRepo = Substitute.For<IOrderRepository>();
            orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
            {
                { "orderId", "order-001" },
                { "buyerId", "buyer-001" },
                { "farmerId", "farmer-001" },
                { "status", "created" },
                { "version", "1" }
            });
            orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
                .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
                {
                    { "orderId", "order-001" },
                    { "buyerId", "buyer-001" },
                    { "farmerId", "farmer-001" },
                    { "status", "funded" },
                    { "orderTotal", "1000.00" },
                    { "cropCost", "950.00" },
                    { "paymentVerifiedAt", "2026-03-01T10:00:00.0000000Z" },
                    { "version", "3" }
                }));

            var escrowRepo = Substitute.For<IEscrowRepository>();
            escrowRepo.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
                .Returns(new List<Dictionary<string, object>>());
            var orderEscrowService = new EscrowService(
                escrowRepo, Substitute.For<IEscrowAuditRepository>(),
                idempotencyRepo: null, telebirrGateway: null);

            var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
                "test-trips-table", "test-deliveries-table",
                userRepo: _mockUserRepo, orderRepo: orderRepo,
                orderEscrowService: orderEscrowService);

            var op = new SyncOperationRequest
            {
                OperationId = "op-pay-escrow",
                IdempotencyKey = "user:order:order-001:update:12345",
                EntityType = "order",
                EntityId = "order-001",
                Action = "update",
                Data = new Dictionary<string, object>
                {
                    { "status", "payment_submitted" },
                    { "paymentMethod", "telebirr" },
                    { "version", 1 }
                }
            };
            var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer", authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var response = await function.FunctionHandler(request, _context);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("success", response.Body);
            await escrowRepo.Received(1).CreateAsync(Arg.Is<Dictionary<string, object>>(r =>
                (string)r["escrowId"] == "escrow-order-order-001"
                && (string)r["status"] == "escrowed"
                && (string)r["source"] == "order"
                && !r.ContainsKey("deliveryId")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        }
    }

    [Fact]
    public async Task OrderUpdate_FundedToRejected_SchedulesEscrowRefund()
    {
        // Rejecting a funded order must send the buyer's money back: the escrow is
        // expired NOW so the hourly auto-refund cron pays it out (order-lifecycle.md
        // invariant 2).
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", "funded" },
            { "version", "1" }
        });
        orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" },
                { "status", "rejected" },
                { "version", "2" }
            }));

        var escrowRepo = Substitute.For<IEscrowRepository>();
        escrowRepo.QueryByOrderIdAsync("order-001", Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>
            {
                new()
                {
                    { "escrowId", "escrow-order-order-001" },
                    { "orderId", "order-001" },
                    { "status", "escrowed" },
                    { "source", "order" },
                    { "createdAt", "2026-03-01T10:00:00.0000000Z" }
                }
            });
        var orderEscrowService = new EscrowService(
            escrowRepo, Substitute.For<IEscrowAuditRepository>(),
            idempotencyRepo: null, telebirrGateway: null);

        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: orderRepo,
            orderEscrowService: orderEscrowService);

        var op = new SyncOperationRequest
        {
            OperationId = "op-reject-refund",
            IdempotencyKey = "farmer-001:order:order-001:update:12345",
            EntityType = "order",
            EntityId = "order-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "status", "rejected" }, { "version", 1 } }
        };
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await escrowRepo.Received(1).UpdateStatusAsync(
            "escrow-order-order-001", "2026-03-01T10:00:00.0000000Z", "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f["refundReason"] == "order_rejected" && f.ContainsKey("expiresAt")));
    }

    [Fact]
    public async Task DeliveryCreate_MissingOrderId_ReturnsError()
    {
        var op = MakeDeliveryCreateOp();
        op.Data!.Remove("orderId");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("DELIVERY_ORDER_REQUIRED", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_WrongTrucker_ReturnsError()
    {
        // The order was matched to a different trucker: an arbitrary authenticated
        // trucker must not be able to attach a delivery (escrow-linkage hijack).
        _mockOrderRepo.GetByIdAsync(TestOrderId).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId },
            { "buyerId", TestBuyerId },
            { "farmerId", TestFarmerId },
            { "truckerId", "00000000-0000-0000-0000-000000000077" },
            { "status", "accepted" },
            { "version", "1" }
        });

        var op = MakeDeliveryCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("matched trucker", response.Body);
    }

    [Fact]
    public async Task DeliveryCreate_TerminalOrder_ReturnsError()
    {
        _mockOrderRepo.GetByIdAsync(TestOrderId).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId },
            { "buyerId", TestBuyerId },
            { "farmerId", TestFarmerId },
            { "status", "cancelled" },
            { "version", "2" }
        });

        var op = MakeDeliveryCreateOp();
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId);
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("no delivery can be created", response.Body);
    }

    // --- SIM-09: delivery create and update guards ---

    /// <summary>An order matched to the test trucker (the trucker-flow happy path).</summary>
    private void SetupTruckOrder(string status = "accepted", string truckerId = TestTruckerId)
    {
        _mockOrderRepo.GetByIdAsync(TestOrderId).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId }, { "buyerId", TestBuyerId }, { "farmerId", TestFarmerId },
            { "listingId", TestListingId }, { "tripId", TestTripId },
            { "truckerId", truckerId }, { "status", status }, { "version", "1" }
        });
        SetupOrderTrip();
    }

    /// <summary>ESC-08: the order's trip, owned by <paramref name="truckerId"/>.</summary>
    private void SetupOrderTrip(string truckerId = TestTruckerId)
    {
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r =>
                r.TableName == "test-trips-table" && r.ExpressionAttributeValues[":id"].S == TestTripId))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "tripId", new AttributeValue { S = TestTripId } },
                        { "truckerId", new AttributeValue { S = truckerId } },
                        { "status", new AttributeValue { S = "open" } }
                    }
                }
            });
    }

    /// <summary>ESC-08: a create wrote one delivery row (with its slot claim) matching <paramref name="item"/>.</summary>
    private Task<DeliverySlotWriteResult> ReceivedDeliveryWrite(
        Expression<Predicate<Dictionary<string, AttributeValue>>> item) =>
        _mockOrderRepo.Received(1).TryCreateDeliveryWithSlotAsync(
            Arg.Any<string>(), Arg.Is("test-deliveries-table"), Arg.Is(item), Arg.Any<DeliverySlotHolder?>());

    private Task<DeliverySlotWriteResult> DidNotWriteDelivery() =>
        _mockOrderRepo.DidNotReceive().TryCreateDeliveryWithSlotAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, AttributeValue>>(),
            Arg.Any<DeliverySlotHolder?>());

    /// <summary>A pending trucker delivery for order-001 with the standard test parties.</summary>
    private static Dictionary<string, AttributeValue> DeliveryItemForOrder(
        string status = "pending", int version = 1, string? confirmedAt = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "orderId", new AttributeValue { S = "order-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "farmerName", new AttributeValue { S = "Abebe" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "truckerName", new AttributeValue { S = "Tadesse" } },
            { "truckerPhone", new AttributeValue { S = "+251922222222" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "buyerName", new AttributeValue { S = "Mekonnen" } },
            { "qrToken", new AttributeValue { S = "qr-token-abc123" } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = version.ToString() } }
        };
        if (confirmedAt != null)
        {
            item["confirmedAt"] = new AttributeValue { S = confirmedAt };
        }

        return item;
    }

    private static SyncOperationRequest MakeDeliveryUpdateOp(Dictionary<string, object> data, string action = "update") => new()
    {
        OperationId = "op-del-update",
        IdempotencyKey = $"user:delivery:del-001:{action}:12345",
        EntityType = "delivery",
        EntityId = "del-001",
        Action = action,
        Data = data
    };

    [Fact]
    public async Task DeliveryCreate_OrderHasNoTrucker_TruckerFlow_ReturnsError()
    {
        // The fleet run: any trucker could attach a delivery to a truckerless order.
        // The fixture's default order has no trucker.
        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        Assert.Contains("The order has no trucker", response.Body);
        await DidNotWriteDelivery();
    }

    [Theory]
    [InlineData("created")]
    [InlineData("payment_submitted")]
    [InlineData("funded")]
    public async Task DeliveryCreate_OrderNotYetAccepted_ReturnsError(string status)
    {
        SetupTruckOrder(status: status);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        Assert.Contains($"Order is {status}; no delivery can be created until the farmer has accepted it", response.Body);
        await DidNotWriteDelivery();
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    public async Task DeliveryCreate_AcceptedOrder_ClaimsSlotAndSucceeds(string status)
    {
        SetupTruckOrder(status: status);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).TryCreateDeliveryWithSlotAsync(
            TestOrderId, "test-deliveries-table",
            Arg.Is<Dictionary<string, AttributeValue>>(item => item["deliveryId"].S == "del-001"), null);
    }

    private static UserDto PartyProfile(
        string userId, string role, string phone, string? displayName, string? fullName = null) => new()
        {
            UserId = userId,
            PhoneNumber = phone,
            DisplayName = displayName,
            FullName = fullName,
            Role = role,
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        };

    private const string TestSelfPickupDeliveryId = "00000000-0000-0000-0000-000000000046";

    /// <summary>SIM-46: the three delivery parties as the users table knows them.</summary>
    private void SetupDeliveryPartyProfiles(string? farmerDisplayName = "Abebe Kebede")
    {
        _mockUserRepo.GetUserByIdAsync(TestBuyerId)
            .Returns(PartyProfile(TestBuyerId, "buyer", "+251900100003", "Mekonnen Haile"));
        _mockUserRepo.GetUserByIdAsync(TestFarmerId)
            .Returns(PartyProfile(TestFarmerId, "farmer", "+251900100001", farmerDisplayName));
        _mockUserRepo.GetUserByIdAsync(TestTruckerId)
            .Returns(PartyProfile(TestTruckerId, "trucker", "+251900100002", "Chaltu Tolosa"));
    }

    // SIM-46: the delivery shows each party the others' contact, so a create takes
    // every name and phone from the users table and ignores what the client sent.
    [Fact]
    public async Task DeliveryCreate_ClientPartyContact_IsReplacedFromProfiles()
    {
        SetupTruckOrder();
        SetupDeliveryPartyProfiles();
        var op = MakeDeliveryCreateOp();
        op.Data!["buyerName"] = "Spoofed Buyer";
        op.Data["buyerPhone"] = "+251911000001";
        op.Data["farmerName"] = "Spoofed Farmer";
        op.Data["farmerPhone"] = "+251911000002";
        op.Data["truckerName"] = "Spoofed Trucker";
        op.Data["truckerPhone"] = "+251911000003";

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await ReceivedDeliveryWrite(item =>
            item["buyerName"].S == "Mekonnen Haile"
            && item["buyerPhone"].S == "+251900100003"
            && item["farmerName"].S == "Abebe Kebede"
            && item["farmerPhone"].S == "+251900100001"
            && item["truckerName"].S == "Chaltu Tolosa"
            && item["truckerPhone"].S == "+251900100002");
    }

    // SIM-46: a client that sends no phones at all (the shipped mobile and web
    // clients) still gets every phone stamped.
    [Fact]
    public async Task DeliveryCreate_NoClientPhones_StampsAllPhones()
    {
        SetupTruckOrder();
        SetupDeliveryPartyProfiles();
        var op = MakeDeliveryCreateOp();
        op.Data!.Remove("buyerPhone");
        op.Data.Remove("farmerPhone");
        op.Data.Remove("truckerPhone");

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        await ReceivedDeliveryWrite(item =>
            item["buyerPhone"].S == "+251900100003"
            && item["farmerPhone"].S == "+251900100001"
            && item["truckerPhone"].S == "+251900100002");
    }

    // SIM-46: the trucker cannot fix the farmer's profile, so a party with no name on
    // file keeps the order row's (server-stamped) name; the phone is still the profile's.
    [Fact]
    public async Task DeliveryCreate_PartyWithoutProfileName_UsesOrderName()
    {
        _mockOrderRepo.GetByIdAsync(TestOrderId).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId }, { "buyerId", TestBuyerId }, { "farmerId", TestFarmerId },
            { "farmerName", "Abebe From Order" }, { "truckerId", TestTruckerId },
            { "listingId", TestListingId }, { "tripId", TestTripId },
            { "status", "accepted" }, { "version", "1" }
        });
        SetupOrderTrip();
        SetupDeliveryPartyProfiles(farmerDisplayName: null);
        var op = MakeDeliveryCreateOp();
        op.Data!["farmerName"] = "Spoofed Farmer";

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        await ReceivedDeliveryWrite(item =>
            item["farmerName"].S == "Abebe From Order"
            && item["farmerPhone"].S == "+251900100001");
    }

    // SIM-46: with no display name the legal name received from Fayda is used
    // before the order row's name.
    [Fact]
    public async Task DeliveryCreate_PartyWithoutDisplayName_UsesFaydaName()
    {
        _mockOrderRepo.GetByIdAsync(TestOrderId).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId }, { "buyerId", TestBuyerId }, { "farmerId", TestFarmerId },
            { "farmerName", "Abebe From Order" }, { "truckerId", TestTruckerId },
            { "listingId", TestListingId }, { "tripId", TestTripId },
            { "status", "accepted" }, { "version", "1" }
        });
        SetupOrderTrip();
        SetupDeliveryPartyProfiles(farmerDisplayName: null);
        _mockUserRepo.GetUserByIdAsync(TestFarmerId).Returns(
            PartyProfile(TestFarmerId, "farmer", "+251900100001", "  ", "Abebe Kebede Lemma"));

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        await ReceivedDeliveryWrite(item =>
            item["farmerName"].S == "Abebe Kebede Lemma");
    }

    // SIM-46: no name anywhere must not fall back to the client value.
    [Fact]
    public async Task DeliveryCreate_PartyWithoutAnyName_IsRejected()
    {
        SetupTruckOrder();
        SetupDeliveryPartyProfiles(farmerDisplayName: null);
        var op = MakeDeliveryCreateOp();
        op.Data!["farmerName"] = "Spoofed Farmer";

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("farmer has no name", response.Body);
        await DidNotWriteDelivery();
    }

    // SIM-46: an unreadable party profile fails closed (as SIM-40 does for owners).
    [Fact]
    public async Task DeliveryCreate_FarmerProfileMissing_IsRejected()
    {
        SetupTruckOrder();
        SetupDeliveryPartyProfiles();
        _mockUserRepo.GetUserByIdAsync(TestFarmerId).Returns((UserDto?)null);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        Assert.Contains("DELIVERY_VALIDATION", response.Body);
        Assert.Contains("farmer profile could not be read", response.Body);
        await DidNotWriteDelivery();
    }

    // SIM-46: a self-pickup stamps buyer and farmer from their profiles and stores no
    // trucker contact at all, whatever the client sent.
    [Fact]
    public async Task SelfPickupCreate_PartyContact_IsStampedWithoutTrucker()
    {
        SetupDeliveryPartyProfiles();
        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-create-contact",
            IdempotencyKey = $"{TestBuyerId}:delivery:{TestSelfPickupDeliveryId}:create:1",
            EntityType = "delivery",
            // A UUID: the server synthesizes the tripId sort key from it.
            EntityId = TestSelfPickupDeliveryId,
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "deliveryType", "self_pickup" },
                { "orderId", TestOrderId },
                { "listingId", TestListingId },
                { "farmerId", TestFarmerId },
                { "farmerName", "Spoofed Farmer" },
                { "farmerPhone", "+251911000002" },
                { "truckerName", "Ghost Trucker" },
                { "truckerPhone", "+251911000003" },
                { "buyerId", TestBuyerId },
                { "buyerName", "Spoofed Buyer" },
                { "status", "pending" }
            }
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestBuyerId, "buyer"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await ReceivedDeliveryWrite(item =>
            item["buyerName"].S == "Mekonnen Haile"
            && item["buyerPhone"].S == "+251900100003"
            && item["farmerName"].S == "Abebe Kebede"
            && item["farmerPhone"].S == "+251900100001"
            && !item.ContainsKey("truckerName")
            && !item.ContainsKey("truckerPhone"));
    }

    [Fact]
    public async Task DeliveryCreate_SecondActiveDelivery_ReturnsError()
    {
        SetupTruckOrder();
        _mockOrderRepo.TryCreateDeliveryWithSlotAsync(
                TestOrderId, Arg.Any<string>(), Arg.Any<Dictionary<string, AttributeValue>>(), Arg.Any<DeliverySlotHolder?>())
            .Returns(DeliverySlotWriteResult.SlotTaken);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryCreateOp()), TestTruckerId), _context);

        Assert.Contains("DELIVERY_VALIDATION: The order already has an active delivery", response.Body);
        // One refused attempt (nothing written), no takeover: no stale holder was found.
        await _mockOrderRepo.Received(1).TryCreateDeliveryWithSlotAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, AttributeValue>>(),
            Arg.Any<DeliverySlotHolder?>());
    }

    [Theory]
    [InlineData("farmerId", "farmer-999")]
    [InlineData("buyerId", "buyer-999")]
    [InlineData("truckerId", "trucker-999")]
    [InlineData("listingId", "listing-999")]
    [InlineData("orderId", "order-999")]
    public async Task DeliveryUpdate_ChangedParticipant_ReturnsError(string field, string value)
    {
        // The fleet run re-pointed farmerId on a delivery update.
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { field, value }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains($"DELIVERY_VALIDATION: {field} is immutable and cannot be changed on update", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task DeliveryUpdate_EchoedParticipants_AreStripped()
    {
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "truckerName", "Tadesse" }, { "truckerPhone", "0922222222" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            !r.ExpressionAttributeNames.Values.Contains("truckerName")
            && !r.ExpressionAttributeNames.Values.Contains("truckerPhone")));
    }

    [Fact]
    public async Task DeliveryUpdate_Cancelled_ReleasesOrderSlot()
    {
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "cancelled" }, { "version", 1 }
            })), "buyer-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).ReleaseDeliverySlotAsync("order-001", "del-001");
    }

    [Fact]
    public async Task DeliveryDelete_ReleasesOrderSlot()
    {
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "version", 1 }
            }, action: "delete")), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).ReleaseDeliverySlotAsync("order-001", "del-001");
    }

    // --- SIM-10: the contract's example payloads and the mobile dispute payload ---

    [Fact]
    public async Task DeliveryUpdate_ContractPickupPayload_Succeeds()
    {
        // delivery-verification-api.md "Confirm Pickup" example: deliveryId + pickedUpAt
        // were rejected as unknown fields.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "deliveryId", "del-001" }, { "status", "pickedUp" },
                { "pickedUpAt", "2026-02-18T08:15:00Z" }, { "scannedQrNonce", "nonce-abc-123" },
                { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("Unknown fields", response.Body);
        // The server stamps pickedUpAt itself; the client's value is not written.
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeNames.Values.Contains("pickedUpAt")
            && !r.ExpressionAttributeNames.Values.Contains("deliveryId")
            && r.ExpressionAttributeValues.Values.All(v => v.S != "2026-02-18T08:15:00Z")));
    }

    [Fact]
    public async Task DeliveryUpdate_ContractConfirmPayload_Succeeds_WithServerConfirmedAt()
    {
        // "Confirm Delivery" example: deliveryId + confirmedAt. confirmedAt anchors the
        // 48-hour dispute window, so the client's value must be replaced by the server's.
        SetupExistingDelivery("del-001", "trucker-001", "pickedUp", 1);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "deliveryId", "del-001" }, { "status", "delivered" },
                { "confirmedAt", "2020-01-01T00:00:00Z" }, { "version", 1 }
            })), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeNames.Values.Contains("confirmedAt")
            && r.ExpressionAttributeValues.Values.All(v => v.S != "2020-01-01T00:00:00Z")));
    }

    [Fact]
    public async Task DeliveryUpdate_MobileDisputePayload_Succeeds()
    {
        // Exactly what the mobile client sends on dispute (deliveryId + updatedAt echoed).
        SetupExistingDeliveryWithConfirmedAt("del-001", "trucker-001", "delivered", 1,
            DateTime.UtcNow.AddHours(-24).ToString("o"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "deliveryId", "del-001" }, { "status", "disputed" },
                { "disputeReason", "Quality issue with delivery" },
                { "updatedAt", "2026-09-25T08:00:00Z" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("Unknown fields", response.Body);
    }

    [Fact]
    public async Task DeliveryUpdate_DeliveryIdMismatch_ReturnsError()
    {
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "deliveryId", "del-999" }, { "status", "cancelled" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("DELIVERY_VALIDATION: deliveryId does not match the operation entityId", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task DeliveryCreate_ContractPayloadWithDeliveryIdEcho_Succeeds()
    {
        SetupTruckOrder();
        var op = MakeDeliveryCreateOp();
        op.Data!["deliveryId"] = "del-001";
        op.Data["updatedAt"] = "2026-02-18T08:00:00Z";

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), TestTruckerId), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("Unknown fields", response.Body);
    }

    // --- SIM-11: the order follows its delivery ---

    [Fact]
    public async Task DeliveryUpdate_PickedUp_AdvancesOrderToPickupConfirmed()
    {
        SetupExistingOrder(status: "accepted");
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "pickedUp" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "pickup_confirmed"), 1);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    public async Task DeliveryUpdate_Delivered_AdvancesOrderToDelivered(string orderStatus)
    {
        // The fleet run had to hand-step pickup_confirmed -> in_transit -> delivered;
        // a confirmed handoff now moves the order to delivered from any logistics state.
        SetupExistingOrder(status: orderStatus);
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "delivered" }, { "version", 1 }
            })), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "delivered"), 1);
    }

    [Fact]
    public async Task DeliveryUpdate_InTransit_AdvancesOrderToInTransit()
    {
        SetupExistingOrder(status: "pickup_confirmed");
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "inTransit" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "in_transit"), 1);
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    public async Task DeliveryUpdate_Delivered_NeverMovesOrderBackwardsOrOutOfTerminal(string orderStatus)
    {
        SetupExistingOrder(status: orderStatus);
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "delivered" }, { "version", 1 }
            })), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task DeliveryUpdate_OrderAdvanceFailure_DoesNotFailTheDelivery()
    {
        SetupExistingOrder(status: "accepted");
        _mockOrderRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .ThrowsAsync(new InvalidOperationException("orders table unavailable"));
        SetupDeliveryQuery(DeliveryItemForOrder());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "pickedUp" }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task OrderUpdate_TruckerMaySetLogisticsStatus()
    {
        // The fleet run: the order's own trucker was refused pickup_confirmed / in_transit.
        // Since SIM-60 the catch-up is honoured only once the delivery is picked up.
        var order = SetupExistingOrder(status: "accepted");
        order["activeDeliveryId"] = "del-001";
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));
        SetupOrderUpdateSucceeds();

        var body = await UpdateOrderAs("trucker-001", "trucker", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 1 }
        });

        Assert.Contains("success", body);
        Assert.DoesNotContain("ORDER_UNAUTHORIZED", body);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("accepted")]
    public async Task OrderUpdate_TruckerMayNotSetNonLogisticsStatus(string status)
    {
        SetupExistingOrder(status: "funded");

        var body = await UpdateOrderAs("trucker-001", "trucker", new Dictionary<string, object>
        {
            { "status", status }, { "version", 1 }
        });

        Assert.Contains("ORDER_UNAUTHORIZED: The trucker may only set pickup_confirmed, in_transit or delivered", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_DeliveredRefusedUntilTheDeliveryIsConfirmed()
    {
        // The fleet run marked an order delivered while its delivery was still inTransit.
        var order = SetupExistingOrder(status: "in_transit");
        order["activeDeliveryId"] = "del-001";
        SetupDeliveryQuery(DeliveryItemForOrder(status: "inTransit"));

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }
        });

        Assert.Contains("ORDER_VALIDATION: The delivery has not been confirmed yet (delivery status: inTransit)", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_DeliveredAllowedOnceTheDeliveryIsConfirmed()
    {
        var order = SetupExistingOrder(status: "in_transit");
        order["activeDeliveryId"] = "del-001";
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "delivered"));

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 1 }
        });

        Assert.Contains("success", body);
    }

    // --- SIM-60: pickup_confirmed / in_transit follow the delivery ---

    [Fact]
    public async Task OrderUpdate_BuyerPickupConfirmedWithoutDelivery_IsRefused()
    {
        // The buyer could mark an order picked up with no delivery at all.
        SetupExistingOrder(status: "accepted");

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 1 }
        });

        Assert.Contains("ORDER_VALIDATION: The order has no active delivery", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("farmer-001", "farmer")]
    [InlineData("trucker-001", "trucker")]
    public async Task OrderUpdate_PickupConfirmedBeforeDeliveryPickedUp_IsRefused(string userId, string role)
    {
        var order = SetupExistingOrder(status: "accepted");
        order["activeDeliveryId"] = "del-001";
        SetupDeliveryQuery(DeliveryItemForOrder());

        var body = await UpdateOrderAs(userId, role, new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 1 }
        });

        Assert.Contains("ORDER_VALIDATION: The delivery is not picked up yet (delivery status: pending)", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_InTransitBeforeDeliveryInTransit_IsRefused()
    {
        var order = SetupExistingOrder(status: "pickup_confirmed");
        order["activeDeliveryId"] = "del-001";
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "in_transit" }, { "version", 1 }
        });

        Assert.Contains("ORDER_VALIDATION: The delivery is not in transit yet (delivery status: pickedUp)", body);
        await _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OrderUpdate_PickupConfirmedAllowedOnceDeliveryPickedUp()
    {
        // Catch-up path: the pickup scan already advanced the delivery.
        var order = SetupExistingOrder(status: "accepted");
        order["activeDeliveryId"] = "del-001";
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "pickedUp"));

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 1 }
        });

        Assert.Contains("success", body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "pickup_confirmed"), 1);
    }

    [Fact]
    public async Task OrderUpdate_PickupCatchUp_ReadsTheDeliveryStronglyConsistent()
    {
        // Right after the pickup scan an eventually consistent read may still say pending
        // and would refuse the catch-up; only the consistent read sees pickedUp here.
        var order = SetupExistingOrder(status: "accepted");
        order["activeDeliveryId"] = "del-001";
        SetupOrderUpdateSucceeds();
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table" && !r.ConsistentRead))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { DeliveryItemForOrder() } });
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table" && r.ConsistentRead))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { DeliveryItemForOrder(status: "pickedUp") }
            });

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 1 }
        });

        Assert.Contains("success", body);
        await _mockDynamoDb.Received().QueryAsync(Arg.Is<QueryRequest>(r =>
            r.TableName == "test-deliveries-table" && r.ConsistentRead));
    }

    // --- SIM-60: a photo ref on a delivery update is a server upload key of this delivery ---

    [Fact]
    public async Task DeliveryUpdate_PhotoRefInUploadKeyShape_IsAccepted()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");
        const string key = "uploads/photos/del-001/pickup-1700000000000.webp";

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "pickedUp" }, { "scannedQrNonce", "nonce-abc-123" },
                { "pickupPhotoRef", key }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("success", response.Body);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeValues.Values.Any(v => v.S == key)));
    }

    [Theory]
    [InlineData("photos/del-001/pickup-1700000000000.webp")]
    [InlineData("uploads/photos/del-999/pickup-1700000000000.webp")]
    [InlineData("/data/user/0/app/files/photos/del-001/pickup-1700000000000.webp")]
    [InlineData("uploads/photos/del-001/pickup-1700000000000-fb.webp")]
    public async Task DeliveryUpdate_PhotoRefNotAnUploadKeyOfThisDelivery_IsRejected(string photoRef)
    {
        // In order: the pre-1.7.0 contract example shape, another delivery's key, the
        // local file path the camera screen produces, and the fallback-quality file name.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pending", 1, "nonce-abc-123");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "pickedUp" }, { "scannedQrNonce", "nonce-abc-123" },
                { "pickupPhotoRef", photoRef }, { "version", 1 }
            })), "trucker-001"), _context);

        Assert.Contains("DELIVERY_INVALID_FIELD: pickupPhotoRef has invalid format.", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    // --- SIM-12, ESC-07: a delivery dispute freezes the escrow first, then the delivery and order follow ---

    private const string OrderEscrowId = "escrow-order-order-001";

    private static Dictionary<string, object> OrderEscrowRow(string status = "escrowed")
        => new()
        {
            { "escrowId", OrderEscrowId }, { "orderId", "order-001" }, { "deliveryId", "del-001" },
            { "createdAt", "2026-02-17T10:00:00.0000000Z" }, { "status", status },
            { "deliveryConfirmedAt", "2026-02-18T10:00:00.0000000Z" }, { "version", 1 }
        };

    private sealed record DisputeRig(
        SyncFunction Function, IEscrowRepository EscrowRepo, IEscrowAuditRepository AuditRepo,
        IDeliveryRepository DeliveryRepo, ITransactionAuditRepository TxRepo);

    /// <summary>
    /// A sync function with the escrow service wired as in the Lambda (same order repository,
    /// transaction audit), a delivered delivery of order-001 and the order at <paramref name="orderStatus"/>.
    /// </summary>
    private DisputeRig DisputeFunction(Dictionary<string, object>? escrow, string orderStatus = "delivered")
    {
        var escrowRepo = Substitute.For<IEscrowRepository>();
        if (escrow != null)
        {
            escrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(escrow);
        }

        var auditRepo = Substitute.For<IEscrowAuditRepository>();
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        deliveryRepo.GetByIdAsync("del-001", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-001" }, { "orderId", "order-001" }, { "status", "disputed" }
        });
        var txRepo = Substitute.For<ITransactionAuditRepository>();
        var service = new EscrowService(escrowRepo, auditRepo, idempotencyRepo: null, telebirrGateway: null,
            orderRepo: _mockOrderRepo, deliveryRepo: deliveryRepo, transactionAuditRepo: txRepo);
        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, escrowRepo: escrowRepo,
            transactionAuditRepo: txRepo, orderEscrowService: service);
        var order = SetupExistingOrder(status: orderStatus);
        _mockOrderRepo.GetByIdAsync("order-001", true).Returns(order);
        SetupOrderUpdateSucceeds();
        SetupDeliveryQuery(DeliveryItemForOrder(status: "delivered",
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));
        return new DisputeRig(function, escrowRepo, auditRepo, deliveryRepo, txRepo);
    }

    private Task<APIGatewayProxyResponse> SendDeliveryDisputeAsync(
        SyncFunction function, int version = 1, string userId = "buyer-001", string role = "buyer",
        long? authTime = null)
        => function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeDeliveryUpdateOp(new Dictionary<string, object>
            {
                { "status", "disputed" }, { "disputeReason", "wet bags" }, { "version", version }
            })), userId, role, authTime), _context);

    private Task<UpdateItemResponse> DidNotWriteTheDelivery()
        => _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));

    private Task<AuthResult<Dictionary<string, object>>> DidNotWriteTheOrder()
        => _mockOrderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());

    private static Task DidNotWriteTheEscrow(IEscrowRepository escrowRepo)
        => escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());

    [Fact]
    public async Task DeliveryUpdate_Disputed_FreezesTheEscrowFirst_ThenWritesTheDeliveryAndTheOrder()
    {
        // Without the freeze the 48-hour auto-release still paid the farmer for a disputed handoff.
        var rig = DisputeFunction(OrderEscrowRow());
        var writes = new List<string>();
        rig.EscrowRepo.When(r => r.UpdateStatusAsync(OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed",
                Arg.Any<Dictionary<string, object>?>()))
            .Do(_ => writes.Add("escrow"));
        _mockDynamoDb.When(d => d.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table")))
            .Do(_ => writes.Add("delivery"));
        _mockOrderRepo.When(o => o.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>()))
            .Do(_ => writes.Add("order"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        Assert.Equal(new[] { "escrow", "delivery", "order" }, writes);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, "2026-02-17T10:00:00.0000000Z", "disputed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                f["disputedBy"].ToString() == "buyer-001" && f["disputeDescription"].ToString() == "wet bags"));
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
        await rig.AuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(d => (string)d["action"] == "dispute"));
        // The order hop is audited as a system move, the delivery hop as the buyer's.
        await rig.TxRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityType"] == "order" && (string)e["action"] == "dispute"
            && (string)e["fromState"] == "delivered" && (string)e["toState"] == "disputed" && (string)e["actor"] == "system"));
        await rig.TxRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityType"] == "delivery" && (string)e["action"] == "dispute" && (string)e["actor"] == "buyer-001"));
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_OrderLeftBehindItsDelivery_IsAdvancedThenDisputed()
    {
        // ESC-07: the order lagged at in_transit (its advance failed); the dispute brings it to
        // delivered first, then disputed, like the verdict mirror.
        var rig = DisputeFunction(OrderEscrowRow(), orderStatus: "in_transit");
        _mockOrderRepo.UpdateAsync("order-001", Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "delivered"), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-001" }, { "status", "delivered" }, { "version", "2" }
            }));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "delivered"), 1);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_EscrowAlreadyDisputed_RepairsTheDeliveryAndOrder()
    {
        // POST /escrow/dispute froze the money but its delivery leg failed: the delivery dispute
        // writes the delivery and order, and the escrow is left as it is.
        var rig = DisputeFunction(OrderEscrowRow(status: "disputed"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
        await rig.AuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(d => (string)d["action"] == "dispute"));
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_QueuedManualPayout_IsRefused_AndNothingIsWritten()
    {
        // ESC-02: the settlement waits for an admin in the manual payout queue. ESC-07: the
        // delivery is not marked either (before, it was disputed over money being paid out).
        var escrow = OrderEscrowRow();
        escrow["manualPayout"] = "open";
        escrow["manualPayoutOp"] = "release";
        var rig = DisputeFunction(escrow);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Contains("paid out by an admin", response.Body);
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
        await rig.AuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(d =>
            (string)d["action"] == "dispute_refused_manual_payout" && (string)d["escrowId"] == OrderEscrowId));
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_WhileASettlementHoldsTheLock_IsRefused_AndNothingIsWritten()
    {
        // ESC-03: the release took the settlement lock between the read and the disputed write, so
        // the write fails its lock condition. The settlement wins and nothing else moves.
        var rig = DisputeFunction(null);
        var locked = OrderEscrowRow();
        locked["settlementLock"] = "release";
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow(), locked);
        rig.EscrowRepo.UpdateStatusAsync(OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Contains("being settled", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
        await rig.AuditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(d =>
            (string)d["action"] == "dispute_refused_settlement_in_flight" && (string)d["escrowId"] == OrderEscrowId
            && (string)d["metadata"] == "deliveryId=del-001"));
        await rig.AuditRepo.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(d => (string)d["action"] == "dispute"));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("refunded")]
    [InlineData("initiated")]
    public async Task DeliveryUpdate_Disputed_EscrowHoldingNoMoney_IsRefused(string status)
    {
        var rig = DisputeFunction(OrderEscrowRow(status: status));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Contains($"status: {status}", response.Body);
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_FundedOrderWithoutEscrow_IsRefused_AndAlarmed()
    {
        var rig = DisputeFunction(null);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Contains("No payment is on record", response.Body);
        Assert.Contains("DISPUTE_NO_ESCROW orderId=order-001 deliveryId=del-001",
            ((TestLambdaLogger)_context.Logger).Buffer.ToString());
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_FreezeUndecided_AsksForARetry_AndWritesNothing()
    {
        var rig = DisputeFunction(null);
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_RETRY", response.Body);
        Assert.DoesNotContain("DELIVERY_DISPUTE_REFUSED", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_OrderBearingDelivery_WithoutEscrowService_AsksForARetry()
    {
        // Fail closed: a funded order's delivery is never disputed without its escrow frozen.
        SetupExistingOrder(status: "delivered");
        SetupDeliveryQuery(DeliveryItemForOrder(status: "delivered",
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(_function);

        Assert.Contains("DELIVERY_DISPUTE_RETRY", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_StaleVersion_IsAConflict_AndFreezesNothing()
    {
        var rig = DisputeFunction(OrderEscrowRow());
        SetupDeliveryQuery(DeliveryItemForOrder(status: "delivered", version: 2,
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("conflict", response.Body);
        await rig.EscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_DeliveryWriteConflictsAfterTheFreeze_TheTradeStillFollows()
    {
        // A concurrent update landed between the version check and the write. The money is
        // frozen, so the delivery and order follow through the status-conditioned mirror.
        var rig = DisputeFunction(OrderEscrowRow());
        _mockDynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"))
            .ThrowsAsync(new ConditionalCheckFailedException("version moved"));
        rig.DeliveryRepo.TryMarkDisputedAsync("del-001", "buyer-001", "wet bags").Returns(true);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("conflict", response.Body);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await rig.DeliveryRepo.Received(1).TryMarkDisputedAsync("del-001", "buyer-001", "wet bags");
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_DeliveryWriteThrowsAfterTheFreeze_TheRetryFinishesWithOneDispute()
    {
        // ESC-07 partial write: the freeze landed, then the delivery write was throttled. The first
        // attempt errors (escrow disputed, delivery and order still delivered); the retry finds the
        // escrow already disputed and finishes the delivery and the order. One dispute row in all.
        var rig = DisputeFunction(null);
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow(), OrderEscrowRow(status: "disputed"));
        var deliveredItem = DeliveryItemForOrder(status: "delivered", confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o"));
        var deliveryWrites = 0;
        _mockDynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"))
            .Returns(_ => ++deliveryWrites == 1
                ? throw new ProvisionedThroughputExceededException("throttled")
                : new UpdateItemResponse { Attributes = deliveredItem });

        var first = await SendDeliveryDisputeAsync(rig.Function);
        await DidNotWriteTheOrder();
        var retry = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("error", first.Body);
        Assert.Contains("success", retry.Body);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await rig.AuditRepo.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(d => (string)d["action"] == "dispute"));
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_LegacyDeliveryWithoutOrderOrEscrow_IsWrittenAsBefore()
    {
        // A delivery from before orderId was required, with no escrow anywhere: nothing to freeze,
        // so the dispute is written as it always was (and no order is touched).
        var rig = DisputeFunction(null);
        var legacy = DeliveryItemForOrder(status: "delivered", confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o"));
        legacy.Remove("orderId");
        SetupDeliveryQuery(legacy);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        // Read twice: the step-up sizes the dispute by it (FUP-26), then the freeze looks for it.
        await rig.EscrowRepo.Received(2).GetByDeliveryIdAsync("del-001");
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_AgainOnADisputedDelivery_FinishesTheOrder_AndWritesNoDelivery()
    {
        // The Lambda stopped after the delivery write and before the order mirror: the client's
        // retry finds the delivery disputed. The order follows now; the delivery is left as it is.
        var rig = DisputeFunction(OrderEscrowRow(status: "disputed"));
        SetupDeliveryQuery(DeliveryItemForOrder(status: "disputed", version: 2,
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("conflict", response.Body);
        Assert.Contains("already disputed", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
        await rig.TxRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["entityType"] == "delivery"));
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_AgainButTheFreezeIsUndecided_AsksForARetry()
    {
        var rig = DisputeFunction(null);
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));
        SetupDeliveryQuery(DeliveryItemForOrder(status: "disputed", version: 2,
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("DELIVERY_DISPUTE_RETRY", response.Body);
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_AgainByAnOutsider_IsUnauthorized()
    {
        var rig = DisputeFunction(OrderEscrowRow(status: "disputed"));
        SetupDeliveryQuery(DeliveryItemForOrder(status: "disputed", version: 2,
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(rig.Function, userId: "stranger-9", role: "buyer");

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await rig.EscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_ConsistentReadAlreadyDisputed_WritesNoSecondDispute()
    {
        // The eventually consistent read still says delivered (replication window) but the strongly
        // consistent one says disputed: no second delivery write (it would overwrite disputeReason
        // and audit a second dispute). The order is still brought along.
        var rig = DisputeFunction(OrderEscrowRow(status: "disputed"));
        var confirmedAt = DateTime.UtcNow.AddHours(-2).ToString("o");
        var stale = DeliveryItemForOrder(status: "delivered", version: 1, confirmedAt: confirmedAt);
        var fresh = DeliveryItemForOrder(status: "disputed", version: 2, confirmedAt: confirmedAt);
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table" && r.ConsistentRead != true))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { stale } });
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table" && r.ConsistentRead == true))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { fresh } });

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("conflict", response.Body);
        await DidNotWriteTheDelivery();
        await rig.TxRepo.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["entityType"] == "delivery"));
        await _mockOrderRepo.Received(1).UpdateAsync("order-001",
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 1);
    }

    [Fact]
    public async Task DeliveryUpdate_Disputed_ConsistentReadNoLongerDelivered_IsAConflict_AndFreezesNothing()
    {
        var rig = DisputeFunction(OrderEscrowRow());
        var confirmedAt = DateTime.UtcNow.AddHours(-2).ToString("o");
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table" && r.ConsistentRead == true))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    DeliveryItemForOrder(status: "cancelled", version: 1, confirmedAt: confirmedAt)
                }
            });

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("conflict", response.Body);
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        await DidNotWriteTheDelivery();
    }

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("farmer-001", "farmer")]
    public async Task OrderUpdate_DeliveredToDisputed_IsRefused_AsSystemManaged(string userId, string role)
    {
        // ESC-07: a client order dispute froze no money and the 48-hour release still paid out.
        SetupExistingOrder(status: "delivered");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderUpdateOp(new Dictionary<string, object>
            {
                { "status", "disputed" }, { "version", 1 }
            })), userId, role), _context);

        // System.Text.Json escapes the quotes around the status.
        Assert.Contains("ORDER_UNAUTHORIZED: \\u0027disputed\\u0027 is a system-managed transition.", response.Body);
        await DidNotWriteTheOrder();
    }

    [Fact]
    public async Task OrderUpdate_TruckerSendsDisputed_IsRefused()
    {
        SetupExistingOrder(status: "delivered");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderUpdateOp(new Dictionary<string, object>
            {
                { "status", "disputed" }, { "version", 1 }
            })), "trucker-001", "trucker"), _context);

        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        await DidNotWriteTheOrder();
    }

    // --- SIM-13: a funded order that dies gives its listing stock back ---

    [Theory]
    [InlineData("rejected", "farmer-001", "farmer")]
    [InlineData("cancelled", "buyer-001", "buyer")]
    public async Task OrderUpdate_FundedOrderDies_RestoresListingStock(string status, string userId, string role)
    {
        // The fleet run: wheat 30 -> 22 with 5 sold; the cancelled orders' quintals never came back.
        SetupExistingOrder(status: "funded");
        SetupOrderUpdateSucceeds();

        var body = await UpdateOrderAs(userId, role, new Dictionary<string, object>
        {
            { "status", status }, { "version", 1 }
        });

        Assert.Contains("success", body);
        // 50 kg on the order = 0.5 quintal back on listing-001.
        await _mockCropRepo.Received(1).TryRestoreStockAsync("listing-001", "order-001", 0.5);
    }

    [Fact]
    public async Task OrderUpdate_UnfundedOrderCancelled_DoesNotTouchStock()
    {
        // Nothing was taken from the listing before funding, so nothing is given back.
        SetupExistingOrder(status: "created");
        SetupOrderUpdateSucceeds();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "cancelled" }, { "version", 1 }
        });

        Assert.Contains("success", body);
        await _mockCropRepo.DidNotReceive().TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OrderUpdate_StockRestoreFailure_DoesNotFailTheOrderUpdate()
    {
        SetupExistingOrder(status: "funded");
        SetupOrderUpdateSucceeds();
        _mockCropRepo.TryRestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .ThrowsAsync(new InvalidOperationException("crops table unavailable"));

        var body = await UpdateOrderAs("farmer-001", "farmer", new Dictionary<string, object>
        {
            { "status", "rejected" }, { "version", 1 }
        });

        Assert.Contains("success", body);
    }

    [Fact]
    public async Task OrderUpdate_PaymentSubmitted_MockMode_AutoFundsOrder()
    {
        // In mock mode there is no real gateway to verify the transfer, so a
        // submitted payment must be auto-verified: the server advances the order
        // to the system-managed 'funded' state so the farmer can act on it.
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        try
        {
            var orderRepo = Substitute.For<IOrderRepository>();
            orderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
            {
                { "orderId", "order-001" },
                { "buyerId", "buyer-001" },
                { "farmerId", "farmer-001" },
                { "listingId", "listing-001" },
                { "quantity", "10" },
                { "status", "created" },
                { "version", "1" }
            });
            orderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
                .Returns(call =>
                {
                    var data = call.ArgAt<Dictionary<string, object>>(1);
                    var status = data.TryGetValue("status", out var s) ? s?.ToString() ?? "" : "";
                    return AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
                    {
                        { "orderId", "order-001" },
                        { "status", status },
                        { "version", "2" }
                    });
                });

            var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
                "test-trips-table", "test-deliveries-table",
                userRepo: _mockUserRepo, orderRepo: orderRepo);

            var op = new SyncOperationRequest
            {
                OperationId = "op-pay",
                IdempotencyKey = "user:order:order-001:update:12345",
                EntityType = "order",
                EntityId = "order-001",
                Action = "update",
                Data = new Dictionary<string, object>
                {
                    { "status", "payment_submitted" },
                    { "paymentMethod", "telebirr" },
                    { "cbeTransactionId", "MOCK-TBR-1" },
                    { "version", 1 }
                }
            };
            var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer", authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var response = await function.FunctionHandler(request, _context);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("success", response.Body);
            await orderRepo.Received().UpdateAsync(
                "order-001",
                Arg.Is<Dictionary<string, object>>(d =>
                    d.ContainsKey("status") && d["status"].ToString() == "funded"),
                Arg.Any<int>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        }
    }

    // ===== Self-pickup + QR handoff hardening =====

    private void SetupDeliveryQuery(Dictionary<string, AttributeValue> item)
    {
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r =>
                r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { item }
            });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = item });
    }

    private static Dictionary<string, AttributeValue> SelfPickupDeliveryItem(
        string status, int version, string? qrNonce, string createdAt) =>
        new()
        {
            { "deliveryId", new AttributeValue { S = "del-sp" } },
            { "tripId", new AttributeValue { S = "del-sp" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "farmerName", new AttributeValue { S = "Abebe" } },
            { "truckerId", new AttributeValue { S = "" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "buyerName", new AttributeValue { S = "Mekonnen" } },
            { "qrToken", new AttributeValue { S = "" } },
            { "deliveryType", new AttributeValue { S = "self_pickup" } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = createdAt } },
            { "updatedAt", new AttributeValue { S = createdAt } },
            { "version", new AttributeValue { N = version.ToString() } },
            { "qrNonce", qrNonce != null ? new AttributeValue { S = qrNonce } : new AttributeValue { NULL = true } }
        };

    [Fact]
    public async Task SelfPickupCreate_ByNonBuyer_Rejected()
    {
        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-create",
            IdempotencyKey = "trucker-001:delivery:del-sp:create:1",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "deliveryType", "self_pickup" },
                { "orderId", "order-001" },
                { "listingId", "listing-001" },
                { "farmerId", "farmer-001" },
                { "farmerName", "Abebe" },
                { "buyerId", "buyer-001" },
                { "buyerName", "Mekonnen" },
                { "status", "pending" }
            }
        };

        // Authenticated as a trucker, not the buyer named in buyerId.
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task SelfPickupCreate_WithTrucker_Rejected()
    {
        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-create2",
            IdempotencyKey = "buyer-001:delivery:del-sp:create:1",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "create",
            Data = new Dictionary<string, object>
            {
                { "deliveryType", "self_pickup" },
                { "orderId", "order-001" },
                { "listingId", "listing-001" },
                { "farmerId", "farmer-001" },
                { "farmerName", "Abebe" },
                { "truckerId", "trucker-001" },
                { "buyerId", "buyer-001" },
                { "buyerName", "Mekonnen" },
                { "status", "pending" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("must not have a trucker", response.Body);
    }

    [Fact]
    public async Task SelfPickupDelivered_WithNonceAndListingGps_Succeeds()
    {
        // Listing has a registered location; the buyer confirms at that location.
        // Use a FRESH crop repo (no shared constructor default to shadow this stub).
        var cropRepo = Substitute.For<ICropRepository>();
        cropRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" },
            { "farmerId", "farmer-001" },
            { "latitude", "9.0" },
            { "longitude", "38.7" },
            { "status", "available" },
            { "version", "1" }
        });

        var mockEscrow = Substitute.For<IEscrowRepository>();
        mockEscrow.GetByDeliveryIdAsync("del-sp").Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-sp" },
            { "createdAt", "2026-07-08T10:00:00Z" },
            { "status", "escrowed" }
        });

        var function = new SyncFunction(
            _mockSyncMeta, cropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: mockEscrow);

        SetupDeliveryQuery(SelfPickupDeliveryItem(
            "pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));

        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-deliver",
            IdempotencyKey = "buyer-001:delivery:del-sp:update:deliver",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "sp-nonce" },
                { "gpsDropoff", new Dictionary<string, object> { { "latitude", 9.0 }, { "longitude", 38.7 } } }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await mockEscrow.Received(1).UpdateStatusAsync(
            "esc-sp", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => f.ContainsKey("deliveryConfirmedAt")));
    }

    [Fact]
    public async Task SelfPickupDelivered_WithoutGps_Rejected()
    {
        SetupDeliveryQuery(SelfPickupDeliveryItem(
            "pending", 1, "sp-nonce", DateTime.UtcNow.AddDays(-1).ToString("o")));

        var op = new SyncOperationRequest
        {
            OperationId = "op-sp-nogps",
            IdempotencyKey = "buyer-001:delivery:del-sp:update:nogps",
            EntityType = "delivery",
            EntityId = "del-sp",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "sp-nonce" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Contains("DELIVERY_GPS_PROXIMITY", response.Body);
    }

    [Fact]
    public async Task MoneyBearingDelivered_NoNonce_Deferred()
    {
        var mockEscrow = Substitute.For<IEscrowRepository>();
        mockEscrow.GetByDeliveryIdAsync("del-001").Returns(new Dictionary<string, object>
        {
            { "escrowId", "esc-001" },
            { "status", "escrowed" }
        });

        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: mockEscrow);

        // Recent trucker delivery with NO nonce anywhere → must DEFER, not fail open.
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "status", new AttributeValue { S = "pickedUp" } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.AddDays(-1).ToString("o") } },
            { "updatedAt", new AttributeValue { S = DateTime.UtcNow.AddDays(-1).ToString("o") } },
            { "version", new AttributeValue { N = "1" } }
        };
        SetupDeliveryQuery(item);

        var op = new SyncOperationRequest
        {
            OperationId = "op-defer",
            IdempotencyKey = "buyer-001:delivery:del-001:update:defer",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "status", "delivered" }, { "version", 1 } }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Contains("deferred", response.Body);
        Assert.DoesNotContain("\"success\"", response.Body);
    }

    [Fact]
    public async Task Delivered_NonceReplayByDifferentOp_Rejected()
    {
        var mockSecret = Substitute.For<IDeliveryQrSecretRepository>();
        mockSecret.GetAsync("del-001").Returns(new DeliveryQrSecret
        {
            DeliveryId = "del-001",
            Nonce = "the-nonce",
            IssuedAt = DateTime.UtcNow.AddHours(-1).ToString("o"),
            ConsumedDeliveredOp = "some-other-op" // already consumed for delivered
        });

        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, qrSecretRepo: mockSecret);

        SetupExistingDelivery("del-001", "trucker-001", "pickedUp", 1);

        var op = new SyncOperationRequest
        {
            OperationId = "op-replay",
            IdempotencyKey = "buyer-001:delivery:del-001:update:replay",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "the-nonce" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Contains("DELIVERY_NONCE_REPLAY", response.Body);
    }

    [Fact]
    public async Task FarmerNonceOnlyUpdate_StoresSecret_WithoutWritingOrBumpingTheVersion()
    {
        var mockSecret = Substitute.For<IDeliveryQrSecretRepository>();
        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, qrSecretRepo: mockSecret);
        SetupExistingDelivery("del-001", "", "pending", 3);

        var op = new SyncOperationRequest
        {
            OperationId = "op-nonce",
            IdempotencyKey = "farmer-001:delivery:del-001:update:nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "qrNonce", "nonce-1" }, { "version", 3 } }
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.Contains("\"serverVersion\":3", response.Body);
        Assert.DoesNotContain("nonce-1", response.Body);
        await mockSecret.Received(1).PutNonceAsync("del-001", "nonce-1");
        // The scanner's queued confirm at version 3 must still match.
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }

    [Fact]
    public async Task NonceOnlyUpdate_FromTheBuyer_StoresNothing()
    {
        var mockSecret = Substitute.For<IDeliveryQrSecretRepository>();
        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, qrSecretRepo: mockSecret);
        SetupExistingDelivery("del-001", "", "pending", 3);

        var op = new SyncOperationRequest
        {
            OperationId = "op-nonce-buyer",
            IdempotencyKey = "buyer-001:delivery:del-001:update:nonce",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "qrNonce", "nonce-x" }, { "version", 3 } }
        };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

        Assert.Contains("DELIVERY_UNAUTHORIZED", response.Body);
        await mockSecret.DidNotReceive().PutNonceAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DeliveryConflict_DoesNotLeakQrNonce()
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "secret-nonce-999");
        // Force the version CAS to fail so the conflict path echoes the server item.
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("conflict"));

        var op = new SyncOperationRequest
        {
            OperationId = "op-conflict",
            IdempotencyKey = "buyer-001:delivery:del-001:update:conflict",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "secret-nonce-999" }
            }
        };

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await _function.FunctionHandler(request, _context);

        // The scanner triggered a conflict; the echoed server item must NOT carry the secret.
        Assert.DoesNotContain("secret-nonce-999", response.Body);
        Assert.DoesNotContain("qrNonce", response.Body);
        // ESC-48: nor any party's phone number.
        AssertNoDeliveryPhones(response.Body);
    }

    private static void AssertNoDeliveryPhones(string body)
    {
        Assert.DoesNotContain("farmerPhone", body);
        Assert.DoesNotContain("truckerPhone", body);
        Assert.DoesNotContain("buyerPhone", body);
        Assert.DoesNotContain("251911111111", body);
        Assert.DoesNotContain("251922222222", body);
        Assert.DoesNotContain("251933333333", body);
    }

    [Fact]
    public async Task DeliveryUpdate_Success_ServerDataCarriesNoPhones()
    {
        // ESC-48: the parties reach each other from the order, never from the delivery.
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "secret-nonce-1");

        var op = new SyncOperationRequest
        {
            OperationId = "op-transit",
            IdempotencyKey = "trucker-001:delivery:del-001:update:transit",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "status", "inTransit" }, { "version", 1 } }
        };

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker"), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.Contains("del-001", response.Body);
        AssertNoDeliveryPhones(response.Body);
    }

    [Fact]
    public async Task TripConflict_OwnerKeepsOwnTruckerPhone()
    {
        // ESC-48 strips phones from delivery rows only: shipped clients read an owner trip's
        // truckerPhone from conflict data as a required field.
        SetupExistingTrip("trip-001", "trucker-001", "open", 3, truckerPhone: "+251912345678");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));

        var deleteOp = new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = "user:trip:trip-001:delete:esc48",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        };
        var response = await _function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(deleteOp)), _context);

        Assert.Contains("conflict", response.Body);
        Assert.Contains("251912345678", response.Body);
    }

    // ===== Payment gates: KYC (ORDER_KYC_REQUIRED) and step-up (AUTH_040 password / AUTH_041 Fayda) =====

    private static UserDto PaymentParty(string id, bool phoneVerified = true, bool faydaVerified = false) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000000",
        Role = "buyer",
        Woreda = "Adama",
        IsPhoneVerified = phoneVerified,
        FaydaVerified = faydaVerified,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    private SyncFunction PaymentFunction(
        decimal orderTotal, UserDto buyer, UserDto farmer, string fromStatus = "created")
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-pay").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-pay" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", fromStatus },
            { "orderTotal", orderTotal },
            { "version", "1" }
        });
        orderRepo.UpdateAsync("order-pay", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "order-pay" },
                { "status", "payment_submitted" },
                { "version", "2" }
            }));

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetUserByIdAsync("buyer-001").Returns(buyer);
        userRepo.GetUserByIdAsync("farmer-001").Returns(farmer);

        return new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: userRepo, orderRepo: orderRepo);
    }

    private static SyncOperationRequest PaymentOp() => new()
    {
        OperationId = "op-pay-gate",
        IdempotencyKey = "buyer-001:order:order-pay:update:12345",
        EntityType = "order",
        EntityId = "order-pay",
        Action = "update",
        Data = new Dictionary<string, object>
        {
            { "status", "payment_submitted" },
            { "paymentMethod", "telebirr" },
            { "version", 1 }
        }
    };

    [Fact]
    public async Task PaymentSubmitted_UnverifiedBuyerPhone_IsRejectedWithKycError()
    {
        var function = PaymentFunction(500m,
            PaymentParty("buyer-001", phoneVerified: false), PaymentParty("farmer-001"));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("ORDER_KYC_REQUIRED", response.Body);
        Assert.Contains("phone", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_UnverifiedFarmerPhone_IsRejectedWithKycError()
    {
        var function = PaymentFunction(500m,
            PaymentParty("buyer-001"), PaymentParty("farmer-001", phoneVerified: false));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("ORDER_KYC_REQUIRED", response.Body);
        Assert.Contains("farmer", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_Above10K_WithoutFayda_IsRejectedWithKycError()
    {
        var function = PaymentFunction(15_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("ORDER_KYC_REQUIRED", response.Body);
        Assert.Contains("Fayda", response.Body);
    }

    private static UserDto WithFaydaLogin(UserDto user, DateTime at)
        => user with { FaydaAuthenticatedAt = at.ToString("o") };

    [Fact]
    public async Task PaymentSubmitted_Above10K_FreshPasswordOnly_RequiresFaydaStepUp()
    {
        var function = PaymentFunction(15_000m,
            PaymentParty("buyer-001", faydaVerified: true), PaymentParty("farmer-001", faydaVerified: true));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_041", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_Above10K_StaleFaydaLogin_RequiresFaydaStepUp()
    {
        var buyer = WithFaydaLogin(PaymentParty("buyer-001", faydaVerified: true), DateTime.UtcNow.AddMinutes(-30));
        var function = PaymentFunction(15_000m, buyer, PaymentParty("farmer-001", faydaVerified: true));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_041", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_Above10K_RecentFaydaLogin_SucceedsWithoutPassword()
    {
        var buyer = WithFaydaLogin(PaymentParty("buyer-001", faydaVerified: true), DateTime.UtcNow.AddMinutes(-1));
        var function = PaymentFunction(15_000m, buyer, PaymentParty("farmer-001", faydaVerified: true));

        // No auth_time claim: above 10,000 ETB the Fayda login replaces the password.
        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.DoesNotContain("AUTH_04", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_Exactly10K_NeedsPasswordNotFayda()
    {
        var function = PaymentFunction(10_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

        var stale = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), _context);
        var fresh = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_040", stale.Body);
        Assert.Contains("\"status\":\"success\"", fresh.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_SmallAmount_WithoutFreshPassword_RequiresStepUp()
    {
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

        var noClaim = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);
        var stale = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_040", noClaim.Body);
        Assert.Contains("AUTH_040", stale.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_SmallAmount_FreshPassword_Succeeds()
    {
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_LiveTotalBelowTelebirrMinimum_IsRefused()
    {
        var function = PaymentFunction(99.99m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("ORDER_VALIDATION", response.Body);
        Assert.Contains("100 to 500000 ETB", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_LiveTotalAboveTelebirrMaximum_IsRefused()
    {
        var buyer = WithFaydaLogin(PaymentParty("buyer-001", faydaVerified: true), DateTime.UtcNow.AddMinutes(-1));
        var function = PaymentFunction(500_000.01m, buyer, PaymentParty("farmer-001", faydaVerified: true));

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("ORDER_VALIDATION", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentSubmitted_MockPayments_SkipTheTelebirrLimits()
    {
        Environment.SetEnvironmentVariable("TELEBIRR_MOCK", "true");
        try
        {
            var function = PaymentFunction(50m, PaymentParty("buyer-001"), PaymentParty("farmer-001"));

            var response = await function.FunctionHandler(
                MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                    authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

            Assert.DoesNotContain("ORDER_VALIDATION", response.Body);
            Assert.Contains("\"status\":\"success\"", response.Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        }
    }

    [Fact]
    public async Task PaymentSubmitted_StampsThePaymentClockOnTheServer()
    {
        // ESC-01: paymentSubmittedAt drives the 20h checkout cut-off and the 24h payment_failed
        // sweep, so the client's own value is dropped and the server's time is stored.
        SetupExistingOrder(status: "created");
        SetupOrderUpdateSucceeds();
        var op = MakeOrderUpdateOp(new Dictionary<string, object>
        {
            { "status", "payment_submitted" },
            { "paymentMethod", "telebirr" },
            { "paymentSubmittedAt", "2020-01-01T00:00:00.0000000Z" },
            { "version", 1 }
        });
        var before = DateTime.UtcNow;

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.NotNull(_updatedOrderData);
        var stamped = DateTime.Parse(_updatedOrderData!["paymentSubmittedAt"].ToString()!, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(stamped >= before && stamped <= DateTime.UtcNow);
    }

    [Fact]
    public async Task PaymentFailed_RetryStraightToPaymentSubmitted_IsAccepted()
    {
        // ESC-47 (1), done in ESC-44: the app retries a failed payment with one change, so a
        // stale read between two queued changes can never refuse the second one.
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"),
            fromStatus: "payment_failed");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.DoesNotContain("Invalid status transition", response.Body);
    }

    [Fact]
    public async Task PaymentFailed_RetryToPaymentSubmitted_RunsTheStepUpGate()
    {
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"),
            fromStatus: "payment_failed");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_040", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentFailed_RetryToPaymentSubmitted_RunsTheKycGate()
    {
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001", phoneVerified: false),
            PaymentParty("farmer-001"), fromStatus: "payment_failed");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("ORDER_KYC_REQUIRED", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentFailed_RetryToPaymentSubmitted_RunsTheLiveTotalGate()
    {
        var function = PaymentFunction(99.99m, PaymentParty("buyer-001"), PaymentParty("farmer-001"),
            fromStatus: "payment_failed");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("ORDER_VALIDATION", response.Body);
        Assert.Contains("100 to 500000 ETB", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentFailed_RetryToPaymentSubmitted_IsBuyerOnly()
    {
        var function = PaymentFunction(2_000m, PaymentParty("buyer-001"), PaymentParty("farmer-001"),
            fromStatus: "payment_failed");
        var op = PaymentOp() with { IdempotencyKey = "farmer-001:order:order-pay:update:12345" };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("ORDER_UNAUTHORIZED", response.Body);
        Assert.DoesNotContain("\"status\":\"success\"", response.Body);
    }

    [Fact]
    public async Task PaymentFailed_RetryToPaymentSubmitted_StartsANewPaymentClock()
    {
        // The retry is a new payment attempt: a checkout from the failed attempt can never
        // fund it, because the mapping records the earlier paymentSubmittedAt.
        var order = SetupExistingOrder(status: "payment_failed");
        order["paymentSubmittedAt"] = "2026-01-01T00:00:00.0000000Z";
        SetupOrderUpdateSucceeds();
        var op = MakeOrderUpdateOp(new Dictionary<string, object>
        {
            { "status", "payment_submitted" },
            { "paymentMethod", "telebirr" },
            { "version", 1 }
        });
        var before = DateTime.UtcNow;

        await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.NotNull(_updatedOrderData);
        var stamped = DateTime.Parse(_updatedOrderData!["paymentSubmittedAt"].ToString()!, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(stamped >= before && stamped <= DateTime.UtcNow);
    }

    [Fact]
    public async Task PaymentFailed_PendingPaymentStep_IsStillAccepted()
    {
        SetupExistingOrder(status: "payment_failed");
        SetupOrderUpdateSucceeds();

        var body = await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "status", "pending_payment" },
            { "version", 1 }
        });

        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task OrderUpdate_PaymentSubmittedAt_IsStrippedOutsideAPaymentSubmit()
    {
        SetupExistingOrder(status: "created");
        SetupOrderUpdateSucceeds();

        await UpdateOrderAs("buyer-001", "buyer", new Dictionary<string, object>
        {
            { "cancelReason", "changed my mind" },
            { "paymentSubmittedAt", "2020-01-01T00:00:00.0000000Z" },
            { "version", 1 }
        });

        Assert.NotNull(_updatedOrderData);
        Assert.False(_updatedOrderData!.ContainsKey("paymentSubmittedAt"));
    }

    [Fact]
    public async Task PaymentSubmitted_NoUserRepository_FailsClosed()
    {
        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("order-pay").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-pay" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "status", "created" },
            { "version", "1" }
        });
        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table", orderRepo: orderRepo);

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(PaymentOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("ORDER_KYC_REQUIRED", response.Body);
        await orderRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    // ===== Create-conflict echo: trip and order only for the owner / a party =====

    private void TripCreateConflicts(string existingTruckerId)
    {
        // The conditional put fails because the trip exists; the conflict path then
        // reads the stored trip (a Query, see SetupExistingTrip) to echo it.
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>())
            .Returns<PutItemResponse>(_ => throw new ConditionalCheckFailedException("exists"));
        SetupExistingTrip("trip-001", existingTruckerId, "open", 2);
    }

    [Fact]
    public async Task TripCreateConflict_OwnerGetsServerData()
    {
        TripCreateConflicts("trucker-001");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripCreateOp(truckerId: "trucker-001")), "trucker-001", "trucker"),
            _context);

        Assert.True(response.Body.Contains("conflict"), response.Body);
        Assert.Contains("Bishoftu", response.Body);
    }

    [Fact]
    public async Task TripCreateConflict_OtherTruckersTrip_IsNotEchoed()
    {
        TripCreateConflicts("trucker-999");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeTripCreateOp(truckerId: "trucker-001")), "trucker-001", "trucker"),
            _context);

        Assert.Contains("conflict", response.Body);
        Assert.DoesNotContain("Bishoftu", response.Body);
        Assert.DoesNotContain("Tadesse Worku", response.Body);
        Assert.DoesNotContain("trucker-999", response.Body);
    }

    // ===== Order create-conflict echo and delivery dispute step-up =====

    private void OrderCreateConflicts(string existingBuyerId)
    {
        SetupListing();
        _mockOrderRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Already exists."));
        _mockOrderRepo.GetByIdAsync("order-001").Returns(new Dictionary<string, object>
        {
            { "orderId", "order-001" },
            { "buyerId", existingBuyerId },
            { "buyerPhone", "+251999999999" },
            { "farmerId", "farmer-777" },
            { "orderTotal", 2275m },
            { "status", "created" },
            { "version", "2" }
        });
    }

    [Fact]
    public async Task OrderCreateConflict_PartyGetsServerData()
    {
        OrderCreateConflicts("buyer-001");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderCreateOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("conflict", response.Body);
        Assert.Contains("251999999999", response.Body);
    }

    [Fact]
    public async Task OrderCreateConflict_AnotherBuyersOrder_IsNotEchoed()
    {
        OrderCreateConflicts("buyer-999");

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeOrderCreateOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("conflict", response.Body);
        Assert.DoesNotContain("251999999999", response.Body);
        Assert.DoesNotContain("farmer-777", response.Body);
    }

    private void SetupDeliveredDeliveryWithOrder(string orderId, decimal orderTotal)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = "del-001" } },
            { "orderId", new AttributeValue { S = orderId } },
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "listingId", new AttributeValue { S = "listing-001" } },
            { "farmerId", new AttributeValue { S = "farmer-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "buyerId", new AttributeValue { S = "buyer-001" } },
            { "status", new AttributeValue { S = "delivered" } },
            { "confirmedAt", new AttributeValue { S = DateTime.UtcNow.AddHours(-1).ToString("o") } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "updatedAt", new AttributeValue { S = "2026-02-16T10:30:00Z" } },
            { "version", new AttributeValue { N = "1" } }
        };
        _mockDynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-deliveries-table"))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse { Attributes = item });
        _mockOrderRepo.GetByIdAsync(orderId).Returns(new Dictionary<string, object>
        {
            { "orderId", orderId },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "orderTotal", orderTotal },
            { "status", "funded" },
            { "version", "1" }
        });
    }

    private static SyncOperationRequest DisputeOp() => new()
    {
        OperationId = "op-dispute-stepup",
        IdempotencyKey = "buyer-001:delivery:del-001:update:99999",
        EntityType = "delivery",
        EntityId = "del-001",
        Action = "update",
        Data = new Dictionary<string, object>
        {
            { "status", "disputed" },
            { "disputeReason", "Half the sacks were damaged." },
            { "version", 1 }
        }
    };

    /// <summary>A sync function whose escrow service finds <paramref name="orderId"/>'s escrow held (ESC-07).</summary>
    private (SyncFunction Function, IEscrowRepository EscrowRepo) StepUpDisputeFunction(string orderId)
    {
        var escrowRepo = Substitute.For<IEscrowRepository>();
        escrowRepo.GetByIdAsync($"escrow-order-{orderId}", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", $"escrow-order-{orderId}" }, { "orderId", orderId }, { "deliveryId", "del-001" },
            { "createdAt", "2026-02-17T10:00:00.0000000Z" }, { "status", "escrowed" }, { "version", 1 }
        });
        var service = new EscrowService(escrowRepo, Substitute.For<IEscrowAuditRepository>(),
            idempotencyRepo: null, telebirrGateway: null, orderRepo: _mockOrderRepo,
            deliveryRepo: Substitute.For<IDeliveryRepository>());
        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, escrowRepo: escrowRepo, orderEscrowService: service);
        return (function, escrowRepo);
    }

    [Fact]
    public async Task DeliveryDispute_LargeOrder_StaleAuth_RequiresStepUp()
    {
        SetupDeliveredDeliveryWithOrder("order-big", 15_000m);
        var (function, escrowRepo) = StepUpDisputeFunction("order-big");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DisputeOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()), _context);

        Assert.Contains("AUTH_040", response.Body);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
        await escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task DeliveryDispute_LargeOrder_FreshAuth_PassesStepUp()
    {
        SetupDeliveredDeliveryWithOrder("order-big", 15_000m);
        var (function, escrowRepo) = StepUpDisputeFunction("order-big");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DisputeOp()), "buyer-001", "buyer",
                authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("AUTH_040", response.Body);
        await escrowRepo.Received(1).UpdateStatusAsync(
            "escrow-order-order-big", Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }

    [Fact]
    public async Task DeliveryDispute_SmallOrder_NeedsNoStepUp()
    {
        SetupDeliveredDeliveryWithOrder("order-small", 800m);
        var (function, escrowRepo) = StepUpDisputeFunction("order-small");

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(DisputeOp()), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        Assert.DoesNotContain("AUTH_040", response.Body);
        await escrowRepo.Received(1).UpdateStatusAsync(
            "escrow-order-order-small", Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }

    // --- FUP-26: the dispute step-up fails closed when its amount cannot be read ---

    private static long StaleAuth() => DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();

    private static long FreshAuth() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static (string Status, string Message) SingleResult(APIGatewayProxyResponse response)
    {
        var result = JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0];
        return (result.GetProperty("status").GetString()!,
            result.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
    }

    /// <summary>Order-001's total as the step-up reads it; null leaves the field out.</summary>
    private void SetupStepUpOrderTotal(object? total)
    {
        var order = new Dictionary<string, object>(SetupExistingOrder(status: "delivered"));
        if (total == null)
        {
            order.Remove("orderTotal");
        }
        else
        {
            order["orderTotal"] = total;
        }

        _mockOrderRepo.GetByIdAsync("order-001").Returns(order);
    }

    /// <summary>A delivered delivery from before orderId was required, paid through /escrow/initiate.</summary>
    private void SetupLegacyDeliveryWithIndexedEscrow(DisputeRig rig, decimal amount)
    {
        var legacy = DeliveryItemForOrder(status: "delivered", confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o"));
        legacy.Remove("orderId");
        SetupDeliveryQuery(legacy);
        Dictionary<string, object> Row() => new()
        {
            { "escrowId", "escrow-legacy" }, { "deliveryId", "del-001" }, { "amount", amount },
            { "createdAt", "2026-02-17T10:00:00.0000000Z" }, { "status", "escrowed" }, { "version", 1 }
        };
        rig.EscrowRepo.GetByDeliveryIdAsync("del-001").Returns(Row());
        rig.EscrowRepo.GetByIdAsync("escrow-legacy", true).Returns(Row());
    }

    /// <summary>The freeze never started and no table was written, the idempotency key included.</summary>
    private async Task AssertTheDisputeWroteNothing(DisputeRig rig)
    {
        await rig.EscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await DidNotWriteTheEscrow(rig.EscrowRepo);
        Assert.Empty(rig.AuditRepo.ReceivedCalls());
        Assert.Empty(rig.TxRepo.ReceivedCalls());
        await DidNotWriteTheDelivery();
        await DidNotWriteTheOrder();
        await _mockSyncMeta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("buyer-001", "buyer")]
    [InlineData("farmer-001", "farmer")]
    [InlineData("trucker-001", "trucker")]
    public async Task DeliveryDispute_OrderReadFails_AsksForARetry_AndWritesNothing(string userId, string role)
    {
        // Before FUP-26 the failed read counted as "no order", so a stale session froze a large
        // order's escrow without the step-up.
        var rig = DisputeFunction(OrderEscrowRow());
        SetupStepUpOrderTotal(15_000m);
        _mockOrderRepo.GetByIdAsync("order-001").ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await SendDeliveryDisputeAsync(rig.Function, userId: userId, role: role, authTime: StaleAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("DELIVERY_DISPUTE_RETRY: ", message);
        await AssertTheDisputeWroteNothing(rig);
    }

    [Fact]
    public async Task DeliveryDispute_OrderReadFails_WithFreshAuth_StillAsksForARetry()
    {
        var rig = DisputeFunction(OrderEscrowRow());
        _mockOrderRepo.GetByIdAsync("order-001").ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: FreshAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("DELIVERY_DISPUTE_RETRY: ", message);
        await AssertTheDisputeWroteNothing(rig);
    }

    [Fact]
    public async Task DeliveryDispute_OrderMissing_StaleAuth_RequiresStepUp()
    {
        var rig = DisputeFunction(OrderEscrowRow());
        _mockOrderRepo.GetByIdAsync("order-001").Returns((Dictionary<string, object>?)null);

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: StaleAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("AUTH_040: ", message);
        await AssertTheDisputeWroteNothing(rig);
    }

    [Fact]
    public async Task DeliveryDispute_OrderMissing_FreshAuth_GoesOnToTheFreeze()
    {
        // The gate is not a dead end: with a fresh password the freeze decides as before.
        var rig = DisputeFunction(OrderEscrowRow());
        _mockOrderRepo.GetByIdAsync("order-001").Returns((Dictionary<string, object>?)null);

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: FreshAuth());

        Assert.Equal("success", SingleResult(response).Status);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("n/a")]
    [InlineData("0")]
    [InlineData("-15000")]
    [InlineData("9999.995")]
    public async Task DeliveryDispute_OrderTotalUnreadableOrAtTheThreshold_StaleAuth_RequiresStepUp(string? total)
    {
        // Missing, unparseable, zero and negative totals are sized at the threshold; 9,999.995
        // rounds to 10,000.00, as the escrow amount /escrow/dispute checks does.
        var rig = DisputeFunction(OrderEscrowRow());
        SetupStepUpOrderTotal(total);

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: StaleAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("AUTH_040: ", message);
        await AssertTheDisputeWroteNothing(rig);
    }

    [Fact]
    public async Task DeliveryDispute_LegacyDeliveryWithLargeIndexedEscrow_StaleAuth_RequiresStepUp()
    {
        // POST /escrow/dispute asks the step-up for this escrow; sync no longer skips it.
        var rig = DisputeFunction(null);
        SetupLegacyDeliveryWithIndexedEscrow(rig, 15_000m);

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: StaleAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("AUTH_040: ", message);
        await rig.EscrowRepo.Received(1).GetByDeliveryIdAsync("del-001");
        await AssertTheDisputeWroteNothing(rig);
    }

    [Fact]
    public async Task DeliveryDispute_LegacyDeliveryEscrowReadFails_AsksForARetry_AndWritesNothing()
    {
        var rig = DisputeFunction(null);
        SetupLegacyDeliveryWithIndexedEscrow(rig, 15_000m);
        rig.EscrowRepo.GetByDeliveryIdAsync("del-001").ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: FreshAuth());

        var (status, message) = SingleResult(response);
        Assert.Equal("error", status);
        Assert.StartsWith("DELIVERY_DISPUTE_RETRY: ", message);
        // Only the step-up's read: the freeze, which would read the index again, never started.
        await rig.EscrowRepo.Received(1).GetByDeliveryIdAsync("del-001");
        await AssertTheDisputeWroteNothing(rig);
    }

    [Theory]
    [InlineData(15_000, true)]
    [InlineData(800, false)]
    public async Task DeliveryDispute_LegacyDeliveryWithIndexedEscrow_PassingTheStepUp_IsFrozenAndWritten(
        int amount, bool fresh)
    {
        // Sized by the escrow's own amount: a large one with a fresh password, a small one without.
        var rig = DisputeFunction(null);
        SetupLegacyDeliveryWithIndexedEscrow(rig, amount);

        var response = await SendDeliveryDisputeAsync(rig.Function, authTime: fresh ? FreshAuth() : StaleAuth());

        Assert.Equal("success", SingleResult(response).Status);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-legacy", Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await _mockDynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"));
    }
}

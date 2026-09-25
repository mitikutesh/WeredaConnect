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

        // Default: delivery creates reference a live order with matching participants.
        _mockOrderRepo.GetByIdAsync(Arg.Any<string>()).Returns(new Dictionary<string, object>
        {
            { "orderId", TestOrderId },
            { "buyerId", TestBuyerId },
            { "farmerId", TestFarmerId },
            { "status", "accepted" },
            { "version", "1" }
        });

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

        await _mockSyncMeta.Received(1).StoreIdempotencyKeyAsync(
            op.IdempotencyKey,
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

    [Fact]
    public async Task FunctionHandler_CreateConflict_ReturnsConflictWithServerData()
    {
        _mockCropRepo.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Already exists."));

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "cropType", "wheat" },
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

        // Op 2: duplicate
        _mockSyncMeta.IdempotencyKeyExistsAsync("dup-key").Returns(true);

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
        object body, string userId = "trucker-001", string? role = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
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
        string? deletedAt = null, string? truckerPhone = null, double? pricePerQuintal = null)
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

    [Theory]
    [InlineData("+251abc")]
    [InlineData("+25191234")]
    [InlineData("+2519123456789")]
    [InlineData("+252912345678")]
    public async Task TripCreate_InvalidPhoneFormat_ReturnsValidationError(string phone)
    {
        var op = MakeTripCreateOp(dataOverrides: new Dictionary<string, object>
        {
            { "truckerPhone", phone }
        });
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001", "trucker");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("truckerPhone", response.Body);
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
            "test-trips-table", "test-deliveries-table", mockRateLimiter);

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
                MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer"), _context);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("success", response.Body);
            await _mockCropRepo.Received(1).UpdateAsync(
                "listing-001",
                Arg.Is<Dictionary<string, object>>(d => d["quantity"].ToString() == "55.00" && !d.ContainsKey("status")),
                1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEBIRR_MOCK", null);
        }
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

        var body = await UpdateOrderAs("farmer-001", "farmer", new Dictionary<string, object>
        {
            { "status", "accepted" }, { "orderTotal", 1.0 }, { "commissionAmount", 0.0 }, { "version", 3 }
        });

        Assert.Contains("success", body);
        Assert.NotNull(_updatedOrderData);
        Assert.False(_updatedOrderData!.ContainsKey("orderTotal"));
        Assert.False(_updatedOrderData.ContainsKey("commissionAmount"));
        Assert.Equal("accepted", _updatedOrderData["status"].ToString());
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("Invalid status transition", response.Body);
        Assert.Contains("success", response.Body);
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
            var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
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
            var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "buyer-001", "buyer");
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
    }
}

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
        _function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo);
        _context = new TestLambdaContext();

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
        string? deletedAt = null)
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
        string entityId = "order-001") => new()
        {
            OperationId = opId,
            IdempotencyKey = $"user:order:{entityId}:create:12345",
            EntityType = "order",
            EntityId = entityId,
            Action = "create",
            Data = new Dictionary<string, object>
        {
            { "buyerId", buyerId },
            { "farmerId", "farmer-001" },
            { "listingId", "listing-001" },
            { "buyerName", "Meron Alemu" },
            { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" },
            { "quantity", 50 },
            { "pricePerUnit", 45.5 }
        }
        };

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
}

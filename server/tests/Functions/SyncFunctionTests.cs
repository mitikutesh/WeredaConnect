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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class SyncFunctionTests
{
    private readonly ISyncMetadataRepository _mockSyncMeta;
    private readonly ICropRepository _mockCropRepo;
    private readonly IAmazonDynamoDB _mockDynamoDb;
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
        _function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table");
        _context = new TestLambdaContext();

        // Default: no duplicate idempotency keys
        _mockSyncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
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

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001");
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001");
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

        var request = MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001");
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

        var request = MakeAuthenticatedRequest(MakeSyncRequest(op1, op2), "farmer-001");
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001");
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001");
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
        object body, string userId = "trucker-001") => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                { "userId", userId }
            }
        }
    };

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
            { "departureDate", "2026-02-18T06:00:00Z" },
            { "capacity", 150.0 },
            { "pricePerQuintal", 120.0 }
        };
        if (dataOverrides != null)
        {
            foreach (var kvp in dataOverrides) data[kvp.Key] = kvp.Value;
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
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
            { "departureDate", "2026-02-18T06:00:00Z" },
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
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("TRIP_VALIDATION", response.Body);
        Assert.Contains("fromWoreda", response.Body);
    }

    [Fact]
    public async Task TripCreate_TruckerIdMismatch_ReturnsUnauthorized()
    {
        var op = MakeTripCreateOp(truckerId: "trucker-999");
        var request = MakeAuthenticatedRequest(MakeSyncRequest(op), "trucker-001");
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
        Assert.Contains("TRIP_ALREADY_CANCELLED", response.Body);
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
        Assert.Contains("TRIP_ALREADY_CANCELLED", response.Body);
    }
}

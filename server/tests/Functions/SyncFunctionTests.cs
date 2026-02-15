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

public class SyncFunctionTests
{
    private readonly ISyncMetadataRepository _mockSyncMeta;
    private readonly ICropRepository _mockCropRepo;
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
        _function = new SyncFunction(_mockSyncMeta, _mockCropRepo);
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
        string entityId = "crop-001") => new()
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
            { "farmerId", "user-001" },
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

        var request = MakeRequest(MakeSyncRequest(MakeCropCreateOp()));
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
        var request = MakeRequest(MakeSyncRequest(op));
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

        var request = MakeRequest(MakeSyncRequest(MakeCropCreateOp()));
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

        var request = MakeRequest(MakeSyncRequest(MakeCropCreateOp()));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
        Assert.Contains("wheat", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UpdateOperation_ReturnsSuccess()
    {
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

        var request = MakeRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UpdateConflict_ReturnsConflictWithServerData()
    {
        _mockCropRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version mismatch."));

        var serverData = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "version", "5" }
        };
        _mockCropRepo.GetByIdAsync("crop-001").Returns(serverData);

        var updateOp = new SyncOperationRequest
        {
            OperationId = "op-conflict",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "quantity", 100 }, { "version", 2 } }
        };

        var request = MakeRequest(MakeSyncRequest(updateOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("conflict", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DeleteOperation_ReturnsSuccess()
    {
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

        var request = MakeRequest(MakeSyncRequest(deleteOp));
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
            Data = new Dictionary<string, object> { { "cropType", "wheat" } }
        };

        var request = MakeRequest(MakeSyncRequest(op1, op2));
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

        var request = MakeRequest(MakeSyncRequest(badOp));
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

        var request = MakeRequest(MakeSyncRequest(badOp));
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

        var request = MakeRequest(MakeSyncRequest(noDataOp));
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("error", response.Body);
        Assert.Contains("requires data", response.Body);
    }
}

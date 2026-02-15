using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /sync Lambda handler.
/// Processes a batch of offline-first sync operations from the mobile client.
///
/// Flow per operation:
/// 1. Check idempotency key — if exists, return duplicate
/// 2. Route to entity handler based on entityType (crop_listing, etc.)
/// 3. Apply operation (create/update/delete) with version checking
/// 4. Store idempotency key with 7-day TTL
/// 5. Return per-operation result (success/conflict/error)
///
/// Error codes:
/// - SYNC_SUCCESS: Operation applied successfully
/// - SYNC_CONFLICT: Client version behind server version
/// - SYNC_DUPLICATE: Idempotency key already processed
/// - SYNC_VALIDATION: Invalid operation data
/// - SYNC_SERVER_ERROR: Unexpected server failure
///
/// See: specs/contracts/sync-protocol.md
/// </summary>
public class SyncFunction
{
    private readonly ISyncMetadataRepository _syncMetadataRepo;
    private readonly ICropRepository _cropRepo;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public SyncFunction()
    {
        var syncMetadataTable = Environment.GetEnvironmentVariable("SYNC_METADATA_TABLE")
                                ?? throw new InvalidOperationException("SYNC_METADATA_TABLE is not set.");
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE")
                         ?? throw new InvalidOperationException("CROPS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();

        _syncMetadataRepo = new SyncMetadataRepository(dynamoDb, syncMetadataTable);
        _cropRepo = new CropRepository(dynamoDb, cropsTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo)
    {
        _syncMetadataRepo = syncMetadataRepo ?? throw new ArgumentNullException(nameof(syncMetadataRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Sync invoked");

        // Parse request
        SyncRequest? syncRequest;
        try
        {
            syncRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.SyncRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Invalid JSON in request body.");
        }

        if (syncRequest == null || syncRequest.Operations == null || syncRequest.Operations.Count == 0)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Request must contain at least one operation.");
        }

        // Validate batch size limit (prevent abuse)
        if (syncRequest.Operations.Count > 50)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Maximum 50 operations per batch.");
        }

        // Process each operation independently
        var results = new List<SyncOperationResult>();

        foreach (var operation in syncRequest.Operations)
        {
            var result = await ProcessOperationAsync(operation, context);
            results.Add(result);
        }

        var response = new SyncResponse
        {
            SyncBatchId = syncRequest.SyncBatchId,
            Results = results
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.SyncResponse);
    }

    /// <summary>
    /// Process a single sync operation with idempotency checking and entity routing.
    /// </summary>
    private async Task<SyncOperationResult> ProcessOperationAsync(
        SyncOperationRequest operation,
        ILambdaContext context)
    {
        // Validate required fields
        if (string.IsNullOrEmpty(operation.OperationId) ||
            string.IsNullOrEmpty(operation.IdempotencyKey) ||
            string.IsNullOrEmpty(operation.EntityType) ||
            string.IsNullOrEmpty(operation.EntityId) ||
            string.IsNullOrEmpty(operation.Action))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId ?? "unknown",
                Status = "error",
                Message = "Missing required fields: operationId, idempotencyKey, entityType, entityId, action."
            };
        }

        // Validate action
        if (operation.Action is not ("create" or "update" or "delete"))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"Invalid action: '{operation.Action}'. Must be create, update, or delete."
            };
        }

        try
        {
            // 1. Check idempotency — already processed?
            var isDuplicate = await _syncMetadataRepo.IdempotencyKeyExistsAsync(operation.IdempotencyKey);
            if (isDuplicate)
            {
                context.Logger.LogInformation(
                    $"Duplicate idempotency key: {operation.IdempotencyKey} for operation {operation.OperationId}");

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "duplicate",
                    Message = "Operation already processed."
                };
            }

            // 2. Route to entity handler
            var result = operation.EntityType switch
            {
                "crop_listing" => await ProcessCropListingAsync(operation, context),
                _ => new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Unsupported entity type: '{operation.EntityType}'."
                }
            };

            // 3. Store idempotency key (for successful or conflict results)
            if (result.Status is "success" or "conflict" or "duplicate")
            {
                await _syncMetadataRepo.StoreIdempotencyKeyAsync(
                    operation.IdempotencyKey,
                    operation.OperationId,
                    operation.EntityType,
                    result.Status);
            }

            return result;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Error processing operation {operation.OperationId}: {ex.Message}");

            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Internal server error."
            };
        }
    }

    /// <summary>
    /// Process a crop_listing sync operation (create, update, delete).
    /// Uses version-based conflict detection per sync-protocol.md.
    /// </summary>
    private async Task<SyncOperationResult> ProcessCropListingAsync(
        SyncOperationRequest operation,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
            {
                if (operation.Data == null || operation.Data.Count == 0)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = "Create operation requires data payload."
                    };
                }

                var createResult = await _cropRepo.CreateAsync(operation.EntityId, operation.Data);

                if (createResult.IsSuccess)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        ServerVersion = 1,
                        ServerData = createResult.Value
                    };
                }

                // Already exists — return conflict with server data
                var existing = await _cropRepo.GetByIdAsync(operation.EntityId);
                var serverVersion = existing != null && existing.TryGetValue("version", out var v)
                    ? int.TryParse(v.ToString(), out var sv) ? sv : 1
                    : 1;

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "conflict",
                    ServerVersion = serverVersion,
                    ServerData = existing,
                    Message = "Crop listing already exists on server."
                };
            }

            case "update":
            {
                if (operation.Data == null || operation.Data.Count == 0)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = "Update operation requires data payload."
                    };
                }

                // Extract client version from data
                var clientVersion = 0;
                if (operation.Data.TryGetValue("version", out var vObj))
                {
                    _ = int.TryParse(vObj.ToString(), out clientVersion);
                }

                var updateResult = await _cropRepo.UpdateAsync(
                    operation.EntityId, operation.Data, clientVersion);

                if (updateResult.IsSuccess)
                {
                    var newVersion = updateResult.Value != null &&
                                     updateResult.Value.TryGetValue("version", out var nv)
                        ? int.TryParse(nv.ToString(), out var snv) ? snv : clientVersion + 1
                        : clientVersion + 1;

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        ServerVersion = newVersion,
                        ServerData = updateResult.Value
                    };
                }

                // Conflict — return server's current data
                var serverData = await _cropRepo.GetByIdAsync(operation.EntityId);
                var serverVer = serverData != null && serverData.TryGetValue("version", out var sv2)
                    ? int.TryParse(sv2.ToString(), out var svn) ? svn : 0
                    : 0;

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "conflict",
                    ServerVersion = serverVer,
                    ServerData = serverData,
                    Message = updateResult.Error?.Message ?? "Version conflict."
                };
            }

            case "delete":
            {
                // Extract version for optimistic concurrency
                var clientVersion = 0;
                if (operation.Data != null && operation.Data.TryGetValue("version", out var vObj))
                {
                    _ = int.TryParse(vObj.ToString(), out clientVersion);
                }

                var deleteResult = await _cropRepo.DeleteAsync(operation.EntityId, clientVersion);

                if (deleteResult.IsSuccess)
                {
                    var newVersion = deleteResult.Value != null &&
                                     deleteResult.Value.TryGetValue("version", out var nv)
                        ? int.TryParse(nv.ToString(), out var snv) ? snv : clientVersion + 1
                        : clientVersion + 1;

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        ServerVersion = newVersion
                    };
                }

                if (deleteResult.Error?.HttpStatus == 404)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = "Crop listing not found."
                    };
                }

                // Conflict
                var serverData = await _cropRepo.GetByIdAsync(operation.EntityId);
                var serverVer = serverData != null && serverData.TryGetValue("version", out var sv3)
                    ? int.TryParse(sv3.ToString(), out var svn) ? svn : 0
                    : 0;

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "conflict",
                    ServerVersion = serverVer,
                    ServerData = serverData,
                    Message = deleteResult.Error?.Message ?? "Version conflict on delete."
                };
            }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Invalid action: '{operation.Action}'."
                };
        }
    }
}

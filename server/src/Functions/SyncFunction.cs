using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
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
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tripsTable;
    private readonly string _deliveriesTable;
    private readonly IRateLimitService? _rateLimitService;

    private static readonly HashSet<string> AllowedCropFields = new()
    {
        "farmerId", "cropType", "quantity", "pricePerKg",
        "latitude", "longitude", "description", "status", "woreda"
    };

    private static readonly HashSet<string> AllowedTripFields = new()
    {
        "truckerId", "truckerName", "truckerPhone", "fromWoreda", "toWoreda",
        "departureDate", "capacity", "pricePerQuintal", "status", "matchedListings"
    };

    private static readonly HashSet<string> AllowedDeliveryFields = new()
    {
        "tripId", "listingId", "farmerId", "farmerName", "farmerPhone",
        "truckerId", "truckerName", "truckerPhone", "buyerId", "buyerName", "buyerPhone",
        "qrToken", "status", "gpsPickup", "gpsDropoff",
        "pickupPhotoRef", "dropoffPhotoRef", "pickedUpAt", "confirmedAt", "disputeReason"
    };

    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new()
    {
        { "open", new HashSet<string> { "matched", "cancelled" } },
        { "matched", new HashSet<string> { "inTransit", "cancelled" } },
        { "inTransit", new HashSet<string> { "delivered" } },
    };

    private static readonly Dictionary<string, HashSet<string>> DeliveryTransitions = new()
    {
        { "pending", new HashSet<string> { "pickedUp", "cancelled" } },
        { "pickedUp", new HashSet<string> { "inTransit", "cancelled" } },
        { "inTransit", new HashSet<string> { "delivered" } },
        { "delivered", new HashSet<string> { "disputed" } },
    };

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
        _tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE")
                      ?? throw new InvalidOperationException("TRIPS_TABLE is not set.");
        _deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE")
                           ?? throw new InvalidOperationException("DELIVERIES_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        _dynamoDb = new AmazonDynamoDBClient();

        _syncMetadataRepo = new SyncMetadataRepository(_dynamoDb, syncMetadataTable);
        _cropRepo = new CropRepository(_dynamoDb, cropsTable);
        _rateLimitService = new RateLimitService(new RateLimitRepository(_dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo,
        IAmazonDynamoDB dynamoDb, string tripsTable, string deliveriesTable,
        IRateLimitService? rateLimitService = null)
    {
        _syncMetadataRepo = syncMetadataRepo ?? throw new ArgumentNullException(nameof(syncMetadataRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tripsTable = tripsTable ?? throw new ArgumentNullException(nameof(tripsTable));
        _deliveriesTable = deliveriesTable ?? throw new ArgumentNullException(nameof(deliveriesTable));
        _rateLimitService = rateLimitService;
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

        // Extract authenticated user ID from API Gateway authorizer context
        // JWT authorizer: Cognito `sub` claim is spread into authorizer dict
        string? authenticatedUserId = null;
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer != null)
        {
            if (authorizer.ContainsKey("sub"))
            {
                authenticatedUserId = authorizer["sub"]?.ToString();
            }
            else if (authorizer.ContainsKey("userId"))
            {
                authenticatedUserId = authorizer["userId"]?.ToString();
            }
        }

        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        // Per-user rate limiting (F-11 Finding 3)
        if (_rateLimitService != null)
        {
            var rateLimitCheck = await _rateLimitService.CheckSyncAllowedAsync(authenticatedUserId);
            if (!rateLimitCheck.IsSuccess)
            {
                var rlError = rateLimitCheck.Error!;
                return LambdaResponse.RateLimited(rlError.Code, rlError.Message, 60);
            }
            await _rateLimitService.RecordSyncAsync(authenticatedUserId);
        }

        // Process each operation independently
        var results = new List<SyncOperationResult>();

        foreach (var operation in syncRequest.Operations)
        {
            var result = await ProcessOperationAsync(operation, authenticatedUserId, context);
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
        string? authenticatedUserId,
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
                "crop_listing" => await ProcessCropListingAsync(operation, authenticatedUserId, context),
                "trip" => await ProcessTripAsync(operation, authenticatedUserId, context),
                "delivery" => await ProcessDeliveryAsync(operation, authenticatedUserId, context),
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
        string? authenticatedUserId,
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

                    // Validate farmerId matches authenticated user
                    if (!operation.Data.TryGetValue("farmerId", out var farmerIdObj)
                        || farmerIdObj?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: farmerId must match authenticated user."
                        };
                    }

                    // Field whitelist — reject unknown fields
                    var unknownCreateFields = operation.Data.Keys
                        .Where(k => !AllowedCropFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownCreateFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: Unknown fields: {string.Join(", ", unknownCreateFields)}"
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

                    // Validate ownership — fetch existing item
                    var existingForUpdate = await _cropRepo.GetByIdAsync(operation.EntityId);
                    if (existingForUpdate == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Crop listing not found."
                        };
                    }

                    if (!existingForUpdate.TryGetValue("farmerId", out var updateFarmerId)
                        || updateFarmerId?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: You can only update your own crop listings."
                        };
                    }

                    // Field whitelist — reject unknown fields
                    var unknownUpdateFields = operation.Data.Keys
                        .Where(k => !AllowedCropFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownUpdateFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: Unknown fields: {string.Join(", ", unknownUpdateFields)}"
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

                    // Validate ownership
                    var existingForDelete = await _cropRepo.GetByIdAsync(operation.EntityId);
                    if (existingForDelete == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Crop listing not found."
                        };
                    }

                    if (!existingForDelete.TryGetValue("farmerId", out var deleteFarmerId)
                        || deleteFarmerId?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: You can only delete your own crop listings."
                        };
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

    /// <summary>
    /// Process a trip sync operation (create, update, delete).
    /// Uses version-based conflict detection per trip-listing-api.md.
    /// Operates directly on DynamoDB (trips don't use the full CRUD repo pattern).
    /// </summary>
    private async Task<SyncOperationResult> ProcessTripAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
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

                    // C2: Validate truckerId matches authenticated user
                    if (!operation.Data.TryGetValue("truckerId", out var truckerIdObj)
                        || truckerIdObj?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: truckerId must match authenticated user."
                        };
                    }

                    // C3: Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedTripFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // C3: Validate required fields
                    var validationErrors = ValidateTripFields(operation.Data, isCreate: true);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    var now = DateTime.UtcNow.ToString("o");

                    var item = new Dictionary<string, AttributeValue>
                {
                    { "tripId", new AttributeValue { S = operation.EntityId } },
                    { "createdAt", new AttributeValue { S = now } },
                    { "updatedAt", new AttributeValue { S = now } },
                    { "version", new AttributeValue { N = "1" } }
                };

                    // Copy only whitelisted data fields (C3)
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "tripId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedTripFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        if (kvp.Key == "matchedListings")
                        {
                            continue; // handled separately
                        }

                        item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
                    }

                    // Force status to "open" for create (C3)
                    item["status"] = new AttributeValue { S = "open" };

                    // Handle matchedListings
                    if (operation.Data.TryGetValue("matchedListings", out var ml))
                    {
                        var mlAttr = CropRepository.ToAttributeValue(ml);
                        item["matchedListings"] = mlAttr.S != null
                            ? mlAttr
                            : new AttributeValue { S = "[]" };
                    }
                    else
                    {
                        item["matchedListings"] = new AttributeValue { S = "[]" };
                    }

                    var request = new PutItemRequest
                    {
                        TableName = _tripsTable,
                        Item = item,
                        ConditionExpression = "attribute_not_exists(tripId)"
                    };

                    try
                    {
                        await _dynamoDb.PutItemAsync(request);

                        var result = CropRepository.FromItem(item);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = 1,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        // Already exists — return conflict with server data
                        var existing = await GetTripItemAsync(operation.EntityId);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = existing != null ? CropRepository.FromItem(existing) : null,
                            Message = "Trip already exists on server."
                        };
                    }
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

                    // Get existing item to find sort key
                    var existing = await GetTripItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Trip not found."
                        };
                    }

                    // C2: Validate authenticated user owns this trip
                    if (!existing.TryGetValue("truckerId", out var existingTruckerId)
                        || existingTruckerId.S != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: You can only update your own trips."
                        };
                    }

                    // H3: Block updates to cancelled/deleted trips
                    var currentStatus = existing.TryGetValue("status", out var existingStatusAttr)
                        ? existingStatusAttr.S : "open";
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_ALREADY_CANCELLED: Cannot update a deleted trip."
                        };
                    }
                    if (currentStatus == "cancelled")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_ALREADY_CANCELLED: Cannot update a cancelled trip."
                        };
                    }

                    // C3: Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedTripFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // C3: Validate present fields
                    var validationErrors = ValidateTripFields(operation.Data, isCreate: false);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    // H1: Validate status transition if status is being changed
                    if (operation.Data.TryGetValue("status", out var newStatusObj))
                    {
                        var newStatus = newStatusObj?.ToString();
                        if (!ValidTransitions.TryGetValue(currentStatus, out var allowed)
                            || newStatus == null || !allowed.Contains(newStatus))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"TRIP_INVALID_TRANSITION: Cannot transition from '{currentStatus}' to '{newStatus}'."
                            };
                        }
                    }

                    // Extract client version from data
                    var clientVersion = 0;
                    if (operation.Data.TryGetValue("version", out var vObj))
                    {
                        _ = int.TryParse(vObj.ToString(), out clientVersion);
                    }

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Build update expression
                    var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
                    var attrNames = new Dictionary<string, string>
                {
                    { "#updated", "updatedAt" },
                    { "#v", "version" }
                };
                    var attrValues = new Dictionary<string, AttributeValue>
                {
                    { ":updated", new AttributeValue { S = now } },
                    { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                    { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                };

                    var i = 0;
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "tripId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedTripFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        var nameAlias = $"#f{i}";
                        var valueAlias = $":f{i}";
                        attrNames[nameAlias] = kvp.Key;
                        attrValues[valueAlias] = CropRepository.ToAttributeValue(kvp.Value);
                        updateParts.Add($"{nameAlias} = {valueAlias}");
                        i++;
                    }

                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = _tripsTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "tripId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET " + string.Join(", ", updateParts),
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = attrNames,
                        ExpressionAttributeValues = attrValues,
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(updateRequest);
                        var result = CropRepository.FromItem(response.Attributes);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = CropRepository.FromItem(existing);
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = $"Version conflict: server has version {serverVer}, client expected {clientVersion}."
                        };
                    }
                }

            case "delete":
                {
                    // Extract version for optimistic concurrency
                    var clientVersion = 0;
                    if (operation.Data != null && operation.Data.TryGetValue("version", out var vObj))
                    {
                        _ = int.TryParse(vObj.ToString(), out clientVersion);
                    }

                    var existing = await GetTripItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Trip not found."
                        };
                    }

                    // C2: Validate authenticated user owns this trip
                    if (!existing.TryGetValue("truckerId", out var existingTruckerId)
                        || existingTruckerId.S != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: You can only delete your own trips."
                        };
                    }

                    // H3: Block deletion of cancelled/deleted trips
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_ALREADY_CANCELLED: Trip is already deleted."
                        };
                    }
                    if (existing.TryGetValue("status", out var existingStatusAttr)
                        && existingStatusAttr.S == "cancelled")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_ALREADY_CANCELLED: Trip is already cancelled."
                        };
                    }

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Soft delete: set deletedAt and status=cancelled
                    var deleteRequest = new UpdateItemRequest
                    {
                        TableName = _tripsTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "tripId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET #deleted = :deleted, #status = :status, #updated = :updated, #v = :newVersion",
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#deleted", "deletedAt" },
                        { "#status", "status" },
                        { "#updated", "updatedAt" },
                        { "#v", "version" }
                    },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":deleted", new AttributeValue { S = now } },
                        { ":status", new AttributeValue { S = "cancelled" } },
                        { ":updated", new AttributeValue { S = now } },
                        { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                        { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                    },
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(deleteRequest);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = CropRepository.FromItem(existing);
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = "Version conflict on delete."
                        };
                    }
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

    /// <summary>
    /// Validate trip data fields. For create: all required fields checked.
    /// For update: only validates fields that are present in the data.
    /// </summary>
    private static List<string> ValidateTripFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        // capacity: required on create, 0.1-1000
        if (isCreate || data.ContainsKey("capacity"))
        {
            if (!data.TryGetValue("capacity", out var capObj)
                || !double.TryParse(capObj?.ToString(), out var capacity))
            {
                if (isCreate)
                {
                    errors.Add("capacity is required and must be a number.");
                }
            }
            else if (capacity < 0.1 || capacity > 1000)
            {
                errors.Add("capacity must be between 0.1 and 1000.");
            }
        }

        // pricePerQuintal: required on create, 0.01-10000
        if (isCreate || data.ContainsKey("pricePerQuintal"))
        {
            if (!data.TryGetValue("pricePerQuintal", out var priceObj)
                || !double.TryParse(priceObj?.ToString(), out var price))
            {
                if (isCreate)
                {
                    errors.Add("pricePerQuintal is required and must be a number.");
                }
            }
            else if (price < 0.01 || price > 10000)
            {
                errors.Add("pricePerQuintal must be between 0.01 and 10000.");
            }
        }

        // fromWoreda: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("fromWoreda"))
        {
            if (!data.TryGetValue("fromWoreda", out var fromObj)
                || string.IsNullOrWhiteSpace(fromObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("fromWoreda is required.");
                }
            }
            else if (fromObj.ToString()!.Length > 100)
            {
                errors.Add("fromWoreda must be 1-100 characters.");
            }
        }

        // toWoreda: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("toWoreda"))
        {
            if (!data.TryGetValue("toWoreda", out var toObj)
                || string.IsNullOrWhiteSpace(toObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("toWoreda is required.");
                }
            }
            else if (toObj.ToString()!.Length > 100)
            {
                errors.Add("toWoreda must be 1-100 characters.");
            }
        }

        // fromWoreda and toWoreda must differ
        if (data.TryGetValue("fromWoreda", out var from) && data.TryGetValue("toWoreda", out var to)
            && from?.ToString() == to?.ToString())
        {
            errors.Add("fromWoreda and toWoreda must be different.");
        }

        // departureDate: required on create, valid ISO 8601
        if (isCreate || data.ContainsKey("departureDate"))
        {
            if (!data.TryGetValue("departureDate", out var dateObj)
                || string.IsNullOrWhiteSpace(dateObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("departureDate is required.");
                }
            }
            else if (!DateTime.TryParse(dateObj.ToString(), null,
                         System.Globalization.DateTimeStyles.RoundtripKind, out _))
            {
                errors.Add("departureDate must be a valid ISO 8601 date.");
            }
        }

        // status: must be "open" for create
        if (isCreate && data.TryGetValue("status", out var statusObj)
            && statusObj?.ToString() != "open")
        {
            errors.Add("status must be 'open' for create.");
        }

        // truckerPhone: required on create, must start with "+251"
        if (isCreate || data.ContainsKey("truckerPhone"))
        {
            if (!data.TryGetValue("truckerPhone", out var phoneObj)
                || string.IsNullOrWhiteSpace(phoneObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("truckerPhone is required.");
                }
            }
            else if (!phoneObj.ToString()!.StartsWith("+251"))
            {
                errors.Add("truckerPhone must start with '+251'.");
            }
        }

        // truckerName: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("truckerName"))
        {
            if (!data.TryGetValue("truckerName", out var nameObj)
                || string.IsNullOrWhiteSpace(nameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("truckerName is required.");
                }
            }
            else if (nameObj.ToString()!.Length > 100)
            {
                errors.Add("truckerName must be 1-100 characters.");
            }
        }

        // truckerId: required on create (auth check done separately)
        if (isCreate && !data.ContainsKey("truckerId"))
        {
            errors.Add("truckerId is required.");
        }

        return errors;
    }

    /// <summary>
    /// Validate delivery fields for create/update operations.
    /// Follows the same pattern as ValidateTripFields.
    /// </summary>
    private static List<string> ValidateDeliveryFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        // tripId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("tripId"))
        {
            if (!data.TryGetValue("tripId", out var tripIdObj)
                || string.IsNullOrWhiteSpace(tripIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("tripId is required.");
                }
            }
            else if (!Guid.TryParse(tripIdObj.ToString(), out _))
            {
                errors.Add("tripId must be a valid UUID.");
            }
        }

        // listingId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("listingId"))
        {
            if (!data.TryGetValue("listingId", out var listingIdObj)
                || string.IsNullOrWhiteSpace(listingIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("listingId is required.");
                }
            }
            else if (!Guid.TryParse(listingIdObj.ToString(), out _))
            {
                errors.Add("listingId must be a valid UUID.");
            }
        }

        // farmerId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("farmerId"))
        {
            if (!data.TryGetValue("farmerId", out var farmerIdObj)
                || string.IsNullOrWhiteSpace(farmerIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("farmerId is required.");
                }
            }
            else if (!Guid.TryParse(farmerIdObj.ToString(), out _))
            {
                errors.Add("farmerId must be a valid UUID.");
            }
        }

        // buyerId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("buyerId"))
        {
            if (!data.TryGetValue("buyerId", out var buyerIdObj)
                || string.IsNullOrWhiteSpace(buyerIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("buyerId is required.");
                }
            }
            else if (!Guid.TryParse(buyerIdObj.ToString(), out _))
            {
                errors.Add("buyerId must be a valid UUID.");
            }
        }

        // qrToken: required on create, 1-500 chars
        if (isCreate || data.ContainsKey("qrToken"))
        {
            if (!data.TryGetValue("qrToken", out var qrTokenObj)
                || string.IsNullOrWhiteSpace(qrTokenObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("qrToken is required.");
                }
            }
            else if (qrTokenObj.ToString()!.Length > 500)
            {
                errors.Add("qrToken must be 1-500 characters.");
            }
        }

        // farmerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("farmerName"))
        {
            if (!data.TryGetValue("farmerName", out var farmerNameObj)
                || string.IsNullOrWhiteSpace(farmerNameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("farmerName is required.");
                }
            }
            else if (farmerNameObj.ToString()!.Length > 200)
            {
                errors.Add("farmerName must be 1-200 characters.");
            }
        }

        // truckerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("truckerName"))
        {
            if (!data.TryGetValue("truckerName", out var truckerNameObj)
                || string.IsNullOrWhiteSpace(truckerNameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("truckerName is required.");
                }
            }
            else if (truckerNameObj.ToString()!.Length > 200)
            {
                errors.Add("truckerName must be 1-200 characters.");
            }
        }

        // buyerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("buyerName"))
        {
            if (!data.TryGetValue("buyerName", out var buyerNameObj)
                || string.IsNullOrWhiteSpace(buyerNameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("buyerName is required.");
                }
            }
            else if (buyerNameObj.ToString()!.Length > 200)
            {
                errors.Add("buyerName must be 1-200 characters.");
            }
        }

        // Phone format validation for optional phone fields (+251 prefix)
        foreach (var phoneField in new[] { "farmerPhone", "truckerPhone", "buyerPhone" })
        {
            if (data.TryGetValue(phoneField, out var phoneObj)
                && phoneObj != null && !string.IsNullOrWhiteSpace(phoneObj.ToString()))
            {
                if (!phoneObj.ToString()!.StartsWith("+251"))
                {
                    errors.Add($"{phoneField} must start with '+251'.");
                }
            }
        }

        // disputeReason: optional, 1-2000 chars when present (F-12 Finding 3)
        if (data.TryGetValue("disputeReason", out var disputeObj) && disputeObj != null)
        {
            var disputeText = disputeObj.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(disputeText) || disputeText.Length > 2000)
            {
                errors.Add("disputeReason must be 1-2000 characters.");
            }
        }

        // truckerId: required on create (auth check done separately)
        if (isCreate && !data.ContainsKey("truckerId"))
        {
            errors.Add("truckerId is required.");
        }

        return errors;
    }

    /// <summary>
    /// Process a delivery sync operation (create, update, delete).
    /// Uses version-based conflict detection.
    /// Operates directly on DynamoDB (deliveries don't use the full CRUD repo pattern).
    /// </summary>
    private async Task<SyncOperationResult> ProcessDeliveryAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
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

                    // Validate truckerId matches authenticated user
                    if (!operation.Data.TryGetValue("truckerId", out var truckerIdObj)
                        || truckerIdObj?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_UNAUTHORIZED: truckerId must match authenticated user."
                        };
                    }

                    // Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedDeliveryFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // Validate required fields
                    var validationErrors = ValidateDeliveryFields(operation.Data, isCreate: true);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    var now = DateTime.UtcNow.ToString("o");

                    var item = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = operation.EntityId } },
                    { "createdAt", new AttributeValue { S = now } },
                    { "updatedAt", new AttributeValue { S = now } },
                    { "version", new AttributeValue { N = "1" } }
                };

                    // Copy only whitelisted data fields
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // Handle GPS fields as nested DynamoDB Map attributes
                        if (kvp.Key is "gpsPickup" or "gpsDropoff")
                        {
                            if (kvp.Value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                var lat = je.GetProperty("latitude").GetDouble();
                                var lng = je.GetProperty("longitude").GetDouble();

                                if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90
                                    || double.IsNaN(lng) || double.IsInfinity(lng) || lng < -180 || lng > 180)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: Invalid GPS coordinates for {kvp.Key}. Latitude must be -90 to 90, longitude -180 to 180."
                                    };
                                }

                                var mapAttr = new AttributeValue
                                {
                                    M = new Dictionary<string, AttributeValue>
                                {
                                    { "latitude", new AttributeValue { N = lat.ToString(CultureInfo.InvariantCulture) } },
                                    { "longitude", new AttributeValue { N = lng.ToString(CultureInfo.InvariantCulture) } }
                                }
                                };
                                item[kvp.Key] = mapAttr;
                            }
                            continue;
                        }

                        item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
                    }

                    // Force status to "pending" for create
                    item["status"] = new AttributeValue { S = "pending" };

                    var request = new PutItemRequest
                    {
                        TableName = _deliveriesTable,
                        Item = item,
                        ConditionExpression = "attribute_not_exists(deliveryId)"
                    };

                    try
                    {
                        await _dynamoDb.PutItemAsync(request);

                        var result = CropRepository.FromItem(item);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = 1,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var existing = await GetDeliveryItemAsync(operation.EntityId);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = existing != null ? CropRepository.FromItem(existing) : null,
                            Message = "Delivery already exists on server."
                        };
                    }
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

                    // Get existing item to find sort key
                    var existing = await GetDeliveryItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Delivery not found."
                        };
                    }

                    // Block updates to cancelled/deleted deliveries
                    var currentStatus = existing.TryGetValue("status", out var existingStatusAttr)
                        ? existingStatusAttr.S : "pending";
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ALREADY_CANCELLED: Cannot update a deleted delivery."
                        };
                    }
                    if (currentStatus == "cancelled")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ALREADY_CANCELLED: Cannot update a cancelled delivery."
                        };
                    }

                    // Role-based authorization for status transitions
                    if (operation.Data.TryGetValue("status", out var newStatusObj))
                    {
                        var newStatus = newStatusObj?.ToString();

                        // Validate status transition
                        if (!DeliveryTransitions.TryGetValue(currentStatus, out var allowed)
                            || newStatus == null || !allowed.Contains(newStatus))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"DELIVERY_INVALID_TRANSITION: Cannot transition from '{currentStatus}' to '{newStatus}'."
                            };
                        }

                        // Role-based access for specific transitions
                        var existingTruckerId = existing.TryGetValue("truckerId", out var tid) ? tid.S : null;
                        var existingBuyerId = existing.TryGetValue("buyerId", out var bid) ? bid.S : null;
                        var existingFarmerId = existing.TryGetValue("farmerId", out var fid) ? fid.S : null;

                        switch (newStatus)
                        {
                            case "pickedUp":
                            case "inTransit":
                                if (authenticatedUserId != existingTruckerId)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_UNAUTHORIZED: Only the trucker can mark as {newStatus}."
                                    };
                                }
                                break;
                            case "delivered":
                                if (authenticatedUserId != existingBuyerId)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_UNAUTHORIZED: Only the buyer can confirm delivery."
                                    };
                                }
                                break;
                            case "disputed":
                            case "cancelled":
                                if (authenticatedUserId != existingFarmerId
                                    && authenticatedUserId != existingTruckerId
                                    && authenticatedUserId != existingBuyerId)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_UNAUTHORIZED: Only participants can {(newStatus == "disputed" ? "dispute" : "cancel")} a delivery."
                                    };
                                }
                                break;
                            default:
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_INVALID_TRANSITION: Unknown status '{newStatus}'."
                                };
                        }
                    }
                    else
                    {
                        // Non-status updates: trucker must own the delivery
                        var existingTruckerId = existing.TryGetValue("truckerId", out var tid2) ? tid2.S : null;
                        if (authenticatedUserId != existingTruckerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_UNAUTHORIZED: You can only update your own deliveries."
                            };
                        }
                    }

                    // Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedDeliveryFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // Validate disputeReason length (F-12 Finding 3)
                    if (operation.Data.TryGetValue("disputeReason", out var drObj) && drObj != null)
                    {
                        var drText = drObj.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(drText) || drText.Length > 2000)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_INVALID_FIELD: disputeReason must be 1-2000 characters."
                            };
                        }
                    }

                    // Extract client version from data
                    var clientVersion = 0;
                    if (operation.Data.TryGetValue("version", out var vObj))
                    {
                        _ = int.TryParse(vObj.ToString(), out clientVersion);
                    }

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Build update expression
                    var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
                    var attrNames = new Dictionary<string, string>
                {
                    { "#updated", "updatedAt" },
                    { "#v", "version" }
                };
                    var attrValues = new Dictionary<string, AttributeValue>
                {
                    { ":updated", new AttributeValue { S = now } },
                    { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                    { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                };

                    var i = 0;
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // Handle GPS fields as nested DynamoDB Map attributes
                        if (kvp.Key is "gpsPickup" or "gpsDropoff")
                        {
                            if (kvp.Value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                var lat = je.GetProperty("latitude").GetDouble();
                                var lng = je.GetProperty("longitude").GetDouble();

                                if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90
                                    || double.IsNaN(lng) || double.IsInfinity(lng) || lng < -180 || lng > 180)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: Invalid GPS coordinates for {kvp.Key}. Latitude must be -90 to 90, longitude -180 to 180."
                                    };
                                }

                                var nameAlias = $"#f{i}";
                                var valueAlias = $":f{i}";
                                attrNames[nameAlias] = kvp.Key;
                                attrValues[valueAlias] = new AttributeValue
                                {
                                    M = new Dictionary<string, AttributeValue>
                                {
                                    { "latitude", new AttributeValue { N = lat.ToString(CultureInfo.InvariantCulture) } },
                                    { "longitude", new AttributeValue { N = lng.ToString(CultureInfo.InvariantCulture) } }
                                }
                                };
                                updateParts.Add($"{nameAlias} = {valueAlias}");
                                i++;
                            }
                            continue;
                        }

                        var na = $"#f{i}";
                        var va = $":f{i}";
                        attrNames[na] = kvp.Key;
                        attrValues[va] = CropRepository.ToAttributeValue(kvp.Value);
                        updateParts.Add($"{na} = {va}");
                        i++;
                    }

                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = _deliveriesTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET " + string.Join(", ", updateParts),
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = attrNames,
                        ExpressionAttributeValues = attrValues,
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(updateRequest);
                        var result = CropRepository.FromItem(response.Attributes);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = CropRepository.FromItem(existing);
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = $"Version conflict: server has version {serverVer}, client expected {clientVersion}."
                        };
                    }
                }

            case "delete":
                {
                    // Extract version for optimistic concurrency
                    var clientVersion = 0;
                    if (operation.Data != null && operation.Data.TryGetValue("version", out var vObj))
                    {
                        _ = int.TryParse(vObj.ToString(), out clientVersion);
                    }

                    var existing = await GetDeliveryItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Delivery not found."
                        };
                    }

                    // Validate authenticated user is the trucker
                    if (!existing.TryGetValue("truckerId", out var existingTruckerId)
                        || existingTruckerId.S != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_UNAUTHORIZED: You can only delete your own deliveries."
                        };
                    }

                    // Block deletion of cancelled/deleted deliveries
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ALREADY_CANCELLED: Delivery is already deleted."
                        };
                    }
                    if (existing.TryGetValue("status", out var existingStatusAttr)
                        && existingStatusAttr.S == "cancelled")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ALREADY_CANCELLED: Delivery is already cancelled."
                        };
                    }

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Soft delete: set deletedAt and status=cancelled
                    var deleteRequest = new UpdateItemRequest
                    {
                        TableName = _deliveriesTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET #deleted = :deleted, #status = :status, #updated = :updated, #v = :newVersion",
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#deleted", "deletedAt" },
                        { "#status", "status" },
                        { "#updated", "updatedAt" },
                        { "#v", "version" }
                    },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":deleted", new AttributeValue { S = now } },
                        { ":status", new AttributeValue { S = "cancelled" } },
                        { ":updated", new AttributeValue { S = now } },
                        { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                        { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                    },
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        await _dynamoDb.UpdateItemAsync(deleteRequest);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = CropRepository.FromItem(existing);
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = "Version conflict on delete."
                        };
                    }
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

    /// <summary>
    /// Query for a trip item by tripId (partition key).
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetTripItemAsync(string tripId)
    {
        var request = new QueryRequest
        {
            TableName = _tripsTable,
            KeyConditionExpression = "tripId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = tripId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Query for a delivery item by deliveryId (partition key).
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetDeliveryItemAsync(string deliveryId)
    {
        var request = new QueryRequest
        {
            TableName = _deliveriesTable,
            KeyConditionExpression = "deliveryId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = deliveryId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}

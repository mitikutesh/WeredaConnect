using System.Globalization;
using System.Text.RegularExpressions;
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
public partial class SyncFunction
{
    private readonly ISyncMetadataRepository _syncMetadataRepo;
    private readonly ICropRepository _cropRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tripsTable;
    private readonly string _deliveriesTable;
    private readonly IRateLimitService? _rateLimitService;
    private readonly IFraudDetectionService? _fraudService;
    private readonly IUserRepository? _userRepo;

    // SSM commission rate cache (15-min TTL)
    private static double _cachedCommissionRate;
    private static DateTime _commissionRateFetchedAt = DateTime.MinValue;
    private static readonly TimeSpan CommissionRateCacheTtl = TimeSpan.FromMinutes(15);
    private const double DefaultCommissionRate = 0.05;

    private static readonly HashSet<string> AllowedCropFields = new()
    {
        "farmerId", "cropType", "quantity", "pricePerKg",
        "latitude", "longitude", "locationName", "description",
        "status", "woreda", "listingId", "audioNoteRef", "createdAt"
    };

    private static readonly HashSet<string> AllowedTripFields = new()
    {
        "truckerId", "truckerName", "truckerPhone", "vehiclePlate", "vehicleDescription",
        "fromWoreda", "toWoreda", "departureDate", "capacity", "pricePerQuintal", "status", "matchedListings"
    };

    private static readonly HashSet<string> AllowedDeliveryFields = new()
    {
        "tripId", "listingId", "orderId", "farmerId", "farmerName", "farmerPhone",
        "truckerId", "truckerName", "truckerPhone", "buyerId", "buyerName", "buyerPhone",
        "status", "gpsPickup", "gpsDropoff",
        "pickupPhotoRef", "dropoffPhotoRef", "disputeReason",
        "qrToken", "gpsAnomalyFlag"
    };

    private static readonly HashSet<string> AllowedOrderFields = new()
    {
        "listingId", "buyerId", "farmerId", "buyerName", "farmerName",
        "cropType", "quantity", "pricePerUnit",
        "tripId", "truckerId", "truckerName", "transportCost",
        "paymentMethod", "cbeTransactionId",
        "status", "rejectionReason", "cancelReason"
    };

    // Server-calculated fields that clients must not set directly
    private static readonly HashSet<string> OrderServerOnlyFields = new()
    {
        "cropCost", "commissionRate", "commissionAmount", "orderTotal",
        "paymentVerifiedAt", "respondedAt", "cancelledAt"
    };

    private static readonly Dictionary<string, HashSet<string>> OrderTransitions = new()
    {
        { "created", new HashSet<string> { "pending_payment", "cancelled" } },
        { "pending_payment", new HashSet<string> { "payment_submitted", "cancelled" } },
        { "payment_submitted", new HashSet<string> { "funded", "payment_failed" } },
        { "funded", new HashSet<string> { "accepted", "cancelled" } },
        { "accepted", new HashSet<string> { "pickup_confirmed" } },
        { "pickup_confirmed", new HashSet<string> { "in_transit" } },
        { "in_transit", new HashSet<string> { "delivered" } },
        { "delivered", new HashSet<string> { "disbursing", "disputed" } },
        { "disbursing", new HashSet<string> { "settled" } },
        { "disputed", new HashSet<string> { "dispute_resolved" } },
        { "payment_failed", new HashSet<string> { "pending_payment", "cancelled" } },
    };

    // Server-controlled fields that must not be set by clients.
    // Stripped from client data early in processing; server injects them after fraud checks.
    private static readonly HashSet<string> ServerOnlyFields = new()
    {
        "isFlagged", "flagReason", "gpsAnomalyFlag"
    };

    // FIX-007: Identity fields are immutable after delivery creation
    private static readonly HashSet<string> ImmutableDeliveryFields = new()
    {
        "farmerId", "farmerName", "farmerPhone",
        "buyerId", "buyerName", "buyerPhone",
        "truckerId", "truckerName", "truckerPhone",
        "tripId", "listingId"
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

    private static readonly HashSet<string> AllowedCropTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "teff", "wheat", "barley", "maize", "chickpea", "lentils", "sorghum", "coffee", "sesame"
    };

    private static readonly Dictionary<string, HashSet<string>> CropTransitions = new()
    {
        { "available", new HashSet<string> { "reserved", "sold", "deleted" } },
        { "reserved", new HashSet<string> { "available", "sold", "deleted" } },
        // sold and deleted are terminal states
    };

    [GeneratedRegex(@"^\+251\d{9}$")]
    private static partial Regex EthiopianPhoneRegex();

    [GeneratedRegex(@"^uploads/photos/[a-zA-Z0-9\-]+/(pickup|dropoff)-\d+\.webp$")]
    private static partial Regex PhotoRefRegex();

    // FIX-011: Validate photo ref format and delivery ID ownership to prevent path traversal
    private static bool IsValidPhotoRef(string photoRef, string deliveryId)
    {
        if (!PhotoRefRegex().IsMatch(photoRef))
        {
            return false;
        }

        return photoRef.StartsWith($"uploads/photos/{deliveryId}/", StringComparison.Ordinal);
    }

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

        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        _dynamoDb = new AmazonDynamoDBClient();

        _syncMetadataRepo = new SyncMetadataRepository(_dynamoDb, syncMetadataTable);
        _cropRepo = new CropRepository(_dynamoDb, cropsTable);
        _orderRepo = new OrderRepository(_dynamoDb, ordersTable);
        _rateLimitService = new RateLimitService(new RateLimitRepository(_dynamoDb, rateLimitTable));
        _fraudService = new FraudDetectionService(
            _dynamoDb,
            cropsTable,
            new RateLimitRepository(_dynamoDb, rateLimitTable));
        _userRepo = new UserRepository(_dynamoDb, usersTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo,
        IAmazonDynamoDB dynamoDb, string tripsTable, string deliveriesTable,
        IRateLimitService? rateLimitService = null,
        IFraudDetectionService? fraudService = null,
        IUserRepository? userRepo = null,
        IOrderRepository? orderRepo = null)
    {
        _syncMetadataRepo = syncMetadataRepo ?? throw new ArgumentNullException(nameof(syncMetadataRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _orderRepo = orderRepo ?? new OrderRepository(dynamoDb, "orders-placeholder");
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tripsTable = tripsTable ?? throw new ArgumentNullException(nameof(tripsTable));
        _deliveriesTable = deliveriesTable ?? throw new ArgumentNullException(nameof(deliveriesTable));
        _rateLimitService = rateLimitService;
        _fraudService = fraudService;
        _userRepo = userRepo;
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

        // Extract authenticated user ID from API Gateway authorizer context.
        // Uses shared AuthHelper to handle both HTTP API v2 JWT authorizer
        // (claims nested under "claims" key) and flat test dictionaries.
        var authenticatedUserId = AuthHelper.GetUserId(request);
        context.Logger.LogInformation($"Authenticated user: {authenticatedUserId ?? "NULL"}");
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        // Per-user rate limiting (F-11 Finding 3)
        // Record unconditionally to prevent request flooding via invalid operations
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
            context.Logger.LogInformation(
                $"Op {result.OperationId}: status={result.Status}, msg={result.Message ?? "none"}");
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

        // Strip server-controlled fields from client data to prevent spoofing
        if (operation.Data != null)
        {
            foreach (var field in ServerOnlyFields)
            {
                operation.Data.Remove(field);
            }
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
                "order" => await ProcessOrderAsync(operation, authenticatedUserId, context),
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

    private static List<string> ValidateCropFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        // Validate pricePerKg
        if (data.TryGetValue("pricePerKg", out var ppkObj))
        {
            if (!decimal.TryParse(ppkObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
                || price < 0.01m || price > 1000)
            {
                errors.Add("pricePerKg must be between 0.01 and 1000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("pricePerKg is required.");
        }

        // Validate quantity
        if (data.TryGetValue("quantity", out var qObj))
        {
            if (!decimal.TryParse(qObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
                || qty < 0.01m || qty > 10000)
            {
                errors.Add("quantity must be between 0.01 and 10000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("quantity is required.");
        }

        // Validate cropType against allowed enum
        if (data.TryGetValue("cropType", out var ctObj))
        {
            var cropType = ctObj?.ToString() ?? "";
            if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }
        else if (isCreate)
        {
            errors.Add("cropType is required.");
        }

        // Validate description length
        if (data.TryGetValue("description", out var descObj))
        {
            var desc = descObj?.ToString() ?? "";
            if (desc.Length > 500)
            {
                errors.Add("description must not exceed 500 characters.");
            }
        }

        // Validate woreda
        if (data.TryGetValue("woreda", out var wObj))
        {
            var woreda = wObj?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(woreda) || woreda.Length > 100)
            {
                errors.Add("woreda must be 1-100 characters.");
            }
        }
        else if (isCreate)
        {
            errors.Add("woreda is required.");
        }

        return errors;
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

                    // FIX-009: Validate crop field values
                    var createValidationErrors = ValidateCropFields(operation.Data, isCreate: true);
                    if (createValidationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {string.Join(" ", createValidationErrors)}"
                        };
                    }

                    // ── Fraud detection (F-14) ──
                    if (_fraudService != null)
                    {
                        var cropType = operation.Data.GetValueOrDefault("cropType")?.ToString() ?? "";
                        var pricePerKg = 0m;
                        if (operation.Data.TryGetValue("pricePerKg", out var ppkObj))
                        {
                            _ = decimal.TryParse(ppkObj?.ToString(), out pricePerKg);
                        }

                        var fraudCheck = await _fraudService.CheckListingCreationAsync(
                            authenticatedUserId!, cropType, pricePerKg);

                        if (!fraudCheck.IsAllowed)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"{fraudCheck.ErrorCode}: {fraudCheck.Message}"
                            };
                        }

                        if (fraudCheck.IsFlagged)
                        {
                            operation.Data["isFlagged"] = true;
                            operation.Data["flagReason"] = fraudCheck.FlagReason ?? "UNKNOWN";
                            context.Logger.LogWarning(
                                $"Fraud flag on listing {operation.EntityId}: {fraudCheck.FlagReason} — {fraudCheck.Message}");
                        }
                    }

                    // FIX-010: Force initial status to "available" on create
                    operation.Data["status"] = "available";

                    var createResult = await _cropRepo.CreateAsync(operation.EntityId, operation.Data);

                    if (createResult.IsSuccess)
                    {
                        // Record for velocity tracking (F-14)
                        if (_fraudService != null)
                        {
                            await _fraudService.RecordListingCreatedAsync(authenticatedUserId!);
                        }

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

                    // FIX-009: Validate crop field values
                    var updateValidationErrors = ValidateCropFields(operation.Data, isCreate: false);
                    if (updateValidationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {string.Join(" ", updateValidationErrors)}"
                        };
                    }

                    // FIX-010: Validate crop status transitions
                    if (operation.Data.TryGetValue("status", out var newCropStatusObj))
                    {
                        var newCropStatus = newCropStatusObj?.ToString();
                        var currentCropStatus = existingForUpdate.TryGetValue("status", out var curStatusObj)
                            ? curStatusObj?.ToString() ?? "available"
                            : "available";

                        if (!CropTransitions.TryGetValue(currentCropStatus, out var allowedCropStatuses)
                            || newCropStatus == null || !allowedCropStatuses.Contains(newCropStatus))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"CROP_INVALID_TRANSITION: Cannot transition from '{currentCropStatus}' to '{newCropStatus}'."
                            };
                        }
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

                    // H3: Block updates to terminal-state or deleted trips
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
                    // F-SEC-06: Block updates to both cancelled and delivered (terminal states)
                    if (currentStatus is "cancelled" or "delivered")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_TERMINAL_STATE: Cannot update a terminal-state trip."
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

                    // H3: Block deletion of terminal-state or deleted trips
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
                        && existingStatusAttr.S is "cancelled" or "delivered")
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_TERMINAL_STATE: Cannot delete a terminal-state trip."
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

        // departureDate: required on create, valid ISO 8601, future-only on create, max 30 days
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
                         System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
            {
                errors.Add("departureDate must be a valid ISO 8601 date.");
            }
            else if (isCreate)
            {
                // F-SEC-01: Enforce future date and 30-day max for create operations
                if (parsedDate <= DateTime.UtcNow)
                {
                    errors.Add("departureDate must be in the future for create.");
                }
                else if (parsedDate > DateTime.UtcNow.AddDays(30))
                {
                    errors.Add("departureDate must be within 30 days.");
                }
            }
        }

        // status: must be "open" for create
        if (isCreate && data.TryGetValue("status", out var statusObj)
            && statusObj?.ToString() != "open")
        {
            errors.Add("status must be 'open' for create.");
        }

        // truckerPhone: required on create, must be valid Ethiopian E.164 (+251XXXXXXXXX)
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
            else if (!EthiopianPhoneRegex().IsMatch(phoneObj.ToString()!))
            {
                // F-SEC-02: Strict E.164 validation (exactly +251 + 9 digits = 13 chars)
                errors.Add("truckerPhone must be valid Ethiopian E.164 format (+251XXXXXXXXX).");
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

        // F12-SEC-04: Phone format validation using strict E.164 regex
        foreach (var phoneField in new[] { "farmerPhone", "truckerPhone", "buyerPhone" })
        {
            if (data.TryGetValue(phoneField, out var phoneObj)
                && phoneObj != null && !string.IsNullOrWhiteSpace(phoneObj.ToString()))
            {
                if (!EthiopianPhoneRegex().IsMatch(phoneObj.ToString()!))
                {
                    errors.Add($"{phoneField} must be valid Ethiopian E.164 format (+251XXXXXXXXX).");
                }
            }
        }

        // F12-SEC-06: disputeReason — 1-500 chars (aligned with contract)
        if (data.TryGetValue("disputeReason", out var disputeObj) && disputeObj != null)
        {
            var disputeText = disputeObj.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(disputeText) || disputeText.Length > 500)
            {
                errors.Add("disputeReason must be 1-500 characters.");
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

                    // SEC-VULN-002: Validate farmerId matches listing owner
                    if (operation.Data.TryGetValue("listingId", out var lidVal))
                    {
                        var listingId = lidVal?.ToString();
                        if (!string.IsNullOrEmpty(listingId))
                        {
                            var listing = await _cropRepo.GetByIdAsync(listingId);
                            if (listing == null)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: Referenced listing not found."
                                };
                            }

                            // Check if listing is deleted
                            if (listing.TryGetValue("deletedAt", out var deletedAt)
                                && deletedAt != null
                                && !string.IsNullOrEmpty(deletedAt.ToString()))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: Referenced listing has been deleted."
                                };
                            }

                            // Check farmerId matches listing owner
                            var requestedFarmerId = operation.Data.TryGetValue("farmerId", out var fid)
                                ? fid?.ToString() : null;
                            var listingFarmerId = listing.TryGetValue("farmerId", out var lfid)
                                ? lfid?.ToString() : null;
                            if (requestedFarmerId != listingFarmerId)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match listing owner."
                                };
                            }
                        }
                    }

                    // SEC-VULN-002: Validate buyerId references a real buyer
                    if (operation.Data.TryGetValue("buyerId", out var bidVal))
                    {
                        var buyerId = bidVal?.ToString();
                        if (!string.IsNullOrEmpty(buyerId) && _userRepo != null)
                        {
                            var buyer = await _userRepo.GetUserByIdAsync(buyerId);
                            if (buyer == null)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: buyerId does not reference a valid user."
                                };
                            }

                            if (!string.Equals(buyer.Role, "buyer", StringComparison.OrdinalIgnoreCase))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_PARTICIPANT_MISMATCH: buyerId must reference a buyer."
                                };
                            }
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

                    // FIX-011: Validate photo reference format and delivery ownership (prevent S3 path traversal)
                    foreach (var photoField in new[] { "pickupPhotoRef", "dropoffPhotoRef" })
                    {
                        if (operation.Data.TryGetValue(photoField, out var photoObj) && photoObj != null)
                        {
                            var photoRef = photoObj.ToString() ?? "";
                            if (!string.IsNullOrEmpty(photoRef) && !IsValidPhotoRef(photoRef, operation.EntityId))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_INVALID_FIELD: {photoField} has invalid format."
                                };
                            }
                        }
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

                    // FIX-005: Generate qrNonce server-side (not client-writable)
                    item["qrNonce"] = new AttributeValue { S = Guid.NewGuid().ToString("N") };

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
                                // F12-SEC-02: Enforce 48-hour dispute window (server-side)
                                if (existing.TryGetValue("confirmedAt", out var confirmedAtAttr2)
                                    && !string.IsNullOrEmpty(confirmedAtAttr2.S)
                                    && DateTime.TryParse(confirmedAtAttr2.S, null,
                                        DateTimeStyles.RoundtripKind, out var confirmedDate))
                                {
                                    if (DateTime.UtcNow - confirmedDate > TimeSpan.FromHours(48))
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has expired."
                                        };
                                    }
                                }
                                else if (currentStatus == "delivered")
                                {
                                    // confirmedAt is missing for a delivered delivery — data integrity issue
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_VALIDATION: confirmedAt is missing for delivered delivery."
                                    };
                                }
                                if (authenticatedUserId != existingFarmerId
                                    && authenticatedUserId != existingTruckerId
                                    && authenticatedUserId != existingBuyerId)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_UNAUTHORIZED: Only participants can dispute a delivery."
                                    };
                                }
                                break;
                            case "cancelled":
                                if (authenticatedUserId != existingFarmerId
                                    && authenticatedUserId != existingTruckerId
                                    && authenticatedUserId != existingBuyerId)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_UNAUTHORIZED: Only participants can cancel a delivery."
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

                    // QR nonce cross-validation for pickedUp/delivered transitions.
                    // If the generator has synced a qrNonce, the scanner must provide
                    // a matching scannedQrNonce. If qrNonce is null (generator hasn't
                    // synced yet), allow the transition (offline-first backward compat).
                    if (operation.Data.TryGetValue("status", out var statusForNonce))
                    {
                        var nStatus = statusForNonce?.ToString();
                        if (nStatus is "pickedUp" or "delivered")
                        {
                            var existingQrNonce = existing.TryGetValue("qrNonce", out var qnAttr)
                                ? qnAttr.S : null;

                            if (!string.IsNullOrEmpty(existingQrNonce))
                            {
                                var scannedNonce = operation.Data.TryGetValue("scannedQrNonce", out var snObj)
                                    ? snObj?.ToString() : null;

                                if (string.IsNullOrEmpty(scannedNonce))
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_NONCE_MISSING: QR nonce verification required for this transition."
                                    };
                                }

                                if (scannedNonce != existingQrNonce)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_NONCE_MISMATCH: Scanned QR nonce does not match generator's nonce."
                                    };
                                }
                            }
                        }
                    }

                    // Field whitelist — reject unknown fields
                    // FIX-005: scannedQrNonce is allowed in data for validation but not persisted
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedDeliveryFields.Contains(k) && k != "version" && k != "scannedQrNonce")
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

                    // F12-SEC-06: Validate disputeReason length (aligned with contract: 500 chars)
                    if (operation.Data.TryGetValue("disputeReason", out var drObj) && drObj != null)
                    {
                        var drText = drObj.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(drText) || drText.Length > 500)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_INVALID_FIELD: disputeReason must be 1-500 characters."
                            };
                        }
                    }

                    // FIX-011: Validate photo reference format and delivery ownership (prevent S3 path traversal)
                    foreach (var photoField in new[] { "pickupPhotoRef", "dropoffPhotoRef" })
                    {
                        if (operation.Data.TryGetValue(photoField, out var photoObj) && photoObj != null)
                        {
                            var photoRef = photoObj.ToString() ?? "";
                            if (!string.IsNullOrEmpty(photoRef) && !IsValidPhotoRef(photoRef, operation.EntityId))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_INVALID_FIELD: {photoField} has invalid format."
                                };
                            }
                        }
                    }

                    // F12-SEC-05: GPS proximity validation for pickup/delivery
                    var resolvedNewStatus = operation.Data.TryGetValue("status", out var statusForGps)
                        ? statusForGps?.ToString() : null;

                    if (resolvedNewStatus == "pickedUp"
                        && operation.Data.TryGetValue("gpsPickup", out var gpsPickupObj)
                        && gpsPickupObj is System.Text.Json.JsonElement pickupJe
                        && pickupJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var pickupLat = pickupJe.GetProperty("latitude").GetDouble();
                        var pickupLng = pickupJe.GetProperty("longitude").GetDouble();

                        var listingId = existing.TryGetValue("listingId", out var lid) ? lid.S : null;
                        if (listingId != null)
                        {
                            var listing = await _cropRepo.GetByIdAsync(listingId);
                            if (listing != null
                                && listing.TryGetValue("latitude", out var listingLatObj)
                                && listing.TryGetValue("longitude", out var listingLonObj)
                                && double.TryParse(listingLatObj.ToString(), out var listingLat)
                                && double.TryParse(listingLonObj.ToString(), out var listingLon))
                            {
                                var distance = GpsValidator.HaversineDistance(
                                    pickupLat, pickupLng, listingLat, listingLon);
                                if (distance > 5.0)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_GPS_PROXIMITY: Pickup GPS is {distance:F1}km from listing location (max 5km)."
                                    };
                                }
                            }
                        }
                    }

                    if (resolvedNewStatus == "delivered"
                        && operation.Data.TryGetValue("gpsDropoff", out var gpsDropoffObj)
                        && gpsDropoffObj is System.Text.Json.JsonElement dropoffJe
                        && dropoffJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var dropoffLat = dropoffJe.GetProperty("latitude").GetDouble();
                        var dropoffLng = dropoffJe.GetProperty("longitude").GetDouble();

                        var tripId = existing.TryGetValue("tripId", out var tripIdAttr) ? tripIdAttr.S : null;
                        if (tripId != null)
                        {
                            var trip = await GetTripItemAsync(tripId);
                            if (trip != null
                                && trip.TryGetValue("destinationLatitude", out var destLatAttr)
                                && trip.TryGetValue("destinationLongitude", out var destLonAttr))
                            {
                                if (double.TryParse(destLatAttr.N, out var destLat)
                                    && double.TryParse(destLonAttr.N, out var destLon))
                                {
                                    var distance = GpsValidator.HaversineDistance(
                                        dropoffLat, dropoffLng, destLat, destLon);
                                    if (distance > 5.0)
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = $"DELIVERY_GPS_PROXIMITY: Dropoff GPS is {distance:F1}km from trip destination (max 5km)."
                                        };
                                    }
                                }
                            }
                        }
                    }

                    // ── GPS fraud detection (F-14) ──
                    if (_fraudService != null && resolvedNewStatus == "delivered" && operation.Data != null)
                    {
                        double? fraudPickupLat = null, fraudPickupLng = null, fraudDropoffLat = null, fraudDropoffLng = null;
                        string fraudFromWoreda = "", fraudToWoreda = "";

                        // Extract pickup GPS from existing delivery (recorded at pickedUp transition)
                        if (existing.TryGetValue("gpsPickup", out var gpAttr) && gpAttr.M != null)
                        {
                            if (gpAttr.M.TryGetValue("latitude", out var lat) && double.TryParse(lat.N, out var pLat))
                            {
                                fraudPickupLat = pLat;
                            }

                            if (gpAttr.M.TryGetValue("longitude", out var lng) && double.TryParse(lng.N, out var pLng))
                            {
                                fraudPickupLng = pLng;
                            }
                        }

                        // Extract dropoff GPS from incoming data
                        if (operation.Data.TryGetValue("gpsDropoff", out var fraudGdObj) && fraudGdObj is System.Text.Json.JsonElement fraudGdJe
                            && fraudGdJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (fraudGdJe.TryGetProperty("latitude", out var lat))
                            {
                                fraudDropoffLat = lat.GetDouble();
                            }

                            if (fraudGdJe.TryGetProperty("longitude", out var lng))
                            {
                                fraudDropoffLng = lng.GetDouble();
                            }
                        }

                        // Get woredas from the associated trip
                        var fraudTripId = existing.TryGetValue("tripId", out var ftAttr) ? ftAttr.S : null;
                        if (fraudTripId != null)
                        {
                            var fraudTrip = await GetTripItemAsync(fraudTripId);
                            if (fraudTrip != null)
                            {
                                fraudFromWoreda = fraudTrip.TryGetValue("fromWoreda", out var fw) ? fw.S ?? "" : "";
                                fraudToWoreda = fraudTrip.TryGetValue("toWoreda", out var tw) ? tw.S ?? "" : "";
                            }
                        }

                        var gpsCheck = _fraudService.CheckGpsConsistency(
                            fraudPickupLat, fraudPickupLng, fraudDropoffLat, fraudDropoffLng, fraudFromWoreda, fraudToWoreda);

                        if (gpsCheck.IsFlagged)
                        {
                            operation.Data["gpsAnomalyFlag"] = true;
                            context.Logger.LogWarning(
                                $"GPS anomaly on delivery {operation.EntityId}: {gpsCheck.Message}");
                        }
                    }

                    // Extract client version from data
                    var clientVersion = 0;
                    if (operation.Data?.TryGetValue("version", out var vObj) == true && vObj is not null)
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
                    foreach (var kvp in operation.Data ?? new Dictionary<string, object>())
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt" or "qrToken")
                        {
                            continue;
                        }

                        if (!AllowedDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // FIX-007: Identity fields are immutable after creation
                        if (ImmutableDeliveryFields.Contains(kvp.Key))
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

                    // FIX-006: Server-side timestamps for status transitions
                    if (operation.Data != null && operation.Data.TryGetValue("status", out var tsStatusObj))
                    {
                        var tsStatus = tsStatusObj?.ToString();
                        if (tsStatus == "pickedUp")
                        {
                            var tsAlias = $"#f{i}";
                            var tsValAlias = $":f{i}";
                            attrNames[tsAlias] = "pickedUpAt";
                            attrValues[tsValAlias] = new AttributeValue { S = now };
                            updateParts.Add($"{tsAlias} = {tsValAlias}");
                            i++;
                        }
                        else if (tsStatus == "delivered")
                        {
                            var tsAlias = $"#f{i}";
                            var tsValAlias = $":f{i}";
                            attrNames[tsAlias] = "confirmedAt";
                            attrValues[tsValAlias] = new AttributeValue { S = now };
                            updateParts.Add($"{tsAlias} = {tsValAlias}");
                            i++;
                        }
                    }

                    // Sort key is tripId (not createdAt) per database-stack.ts
                    var existingTripIdForKey = existing.TryGetValue("tripId", out var tripIdForKey)
                        ? tripIdForKey.S : "";
                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = _deliveriesTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = operation.EntityId } },
                        { "tripId", new AttributeValue { S = existingTripIdForKey } }
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
                    var existingTripIdForDeleteKey = existing.TryGetValue("tripId", out var deleteTripId)
                        ? deleteTripId.S : "";
                    var deleteRequest = new UpdateItemRequest
                    {
                        TableName = _deliveriesTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = operation.EntityId } },
                        { "tripId", new AttributeValue { S = existingTripIdForDeleteKey } }
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

    // ─── Order Processing ─────────────────────────────────────────────────

    /// <summary>
    /// Process an order sync operation (create, update).
    /// Uses server-authoritative state machine and commission calculation.
    /// </summary>
    private async Task<SyncOperationResult> ProcessOrderAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
                return await ProcessOrderCreateAsync(operation, authenticatedUserId, context);
            case "update":
                return await ProcessOrderUpdateAsync(operation, authenticatedUserId, context);
            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"ORDER_VALIDATION: Invalid action '{operation.Action}' for orders. Allowed: create, update."
                };
        }
    }

    private async Task<SyncOperationResult> ProcessOrderCreateAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
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

        // Validate buyerId matches authenticated user
        if (!operation.Data.TryGetValue("buyerId", out var buyerIdObj)
            || buyerIdObj?.ToString() != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: buyerId must match authenticated user."
            };
        }

        // Strip server-controlled fields
        foreach (var field in OrderServerOnlyFields)
        {
            operation.Data.Remove(field);
        }

        // Field whitelist
        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedOrderFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"ORDER_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        // Validate required fields
        var validationErrors = ValidateOrderFields(operation.Data, isCreate: true);
        if (validationErrors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"ORDER_VALIDATION: {string.Join(" ", validationErrors)}"
            };
        }

        // Calculate server-side fields
        var quantity = ParseDouble(operation.Data, "quantity");
        var pricePerUnit = ParseDouble(operation.Data, "pricePerUnit");
        var cropCost = quantity * pricePerUnit;
        var commissionRate = GetCommissionRate(context);
        var commissionAmount = cropCost * commissionRate;
        var transportCost = ParseDoubleOrNull(operation.Data, "transportCost") ?? 0.0;
        var orderTotal = cropCost + transportCost + commissionAmount;

        operation.Data["cropCost"] = cropCost;
        operation.Data["commissionRate"] = commissionRate;
        operation.Data["commissionAmount"] = commissionAmount;
        operation.Data["orderTotal"] = orderTotal;

        // Force initial status
        operation.Data["status"] = "created";

        var createResult = await _orderRepo.CreateAsync(operation.EntityId, operation.Data);

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

        // Already exists — return conflict
        var existing = await _orderRepo.GetByIdAsync(operation.EntityId);
        var serverVersion = existing != null && existing.TryGetValue("version", out var v)
            ? int.TryParse(v.ToString(), out var sv) ? sv : 1
            : 1;

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = serverVersion,
            ServerData = existing,
            Message = "Order already exists on server."
        };
    }

    private async Task<SyncOperationResult> ProcessOrderUpdateAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
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

        // Fetch existing order
        var existingOrder = await _orderRepo.GetByIdAsync(operation.EntityId);
        if (existingOrder == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Order not found."
            };
        }

        // Authorization: only buyer or farmer of this order can update
        var orderBuyerId = existingOrder.TryGetValue("buyerId", out var bid) ? bid?.ToString() : null;
        var orderFarmerId = existingOrder.TryGetValue("farmerId", out var fid) ? fid?.ToString() : null;

        if (authenticatedUserId != orderBuyerId && authenticatedUserId != orderFarmerId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: You can only update orders you are a participant in."
            };
        }

        // Strip server-controlled fields
        foreach (var field in OrderServerOnlyFields)
        {
            operation.Data.Remove(field);
        }

        // Validate status transition
        if (operation.Data.TryGetValue("status", out var newStatusObj))
        {
            var newStatus = newStatusObj?.ToString() ?? "";
            var currentStatus = existingOrder.TryGetValue("status", out var cs) ? cs?.ToString() ?? "" : "";

            if (!OrderTransitions.TryGetValue(currentStatus, out var allowedTransitions)
                || !allowedTransitions.Contains(newStatus))
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"ORDER_VALIDATION: Invalid status transition from '{currentStatus}' to '{newStatus}'."
                };
            }

            // Authorization for specific transitions
            var transitionError = ValidateOrderTransitionAuth(
                currentStatus, newStatus, authenticatedUserId, orderBuyerId, orderFarmerId);
            if (transitionError != null)
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = transitionError
                };
            }

            // Add server-side timestamps for specific transitions
            var now = DateTime.UtcNow.ToString("o");
            switch (newStatus)
            {
                case "cancelled":
                    operation.Data["cancelledAt"] = now;
                    break;
                case "accepted":
                case "rejected":
                    operation.Data["respondedAt"] = now;
                    break;
                case "funded":
                    operation.Data["paymentVerifiedAt"] = now;
                    break;
            }
        }

        // Get version for optimistic concurrency
        var version = 1;
        if (operation.Data.TryGetValue("version", out var vObj))
        {
            int.TryParse(vObj?.ToString(), out version);
            operation.Data.Remove("version"); // version handled by repo
        }
        else if (existingOrder.TryGetValue("version", out var evObj))
        {
            int.TryParse(evObj?.ToString(), out version);
        }

        var updateResult = await _orderRepo.UpdateAsync(operation.EntityId, operation.Data, version);

        if (updateResult.IsSuccess)
        {
            // 6D: When order transitions to "funded", decrease listing quantity
            if (operation.Data.TryGetValue("status", out var statusVal) && statusVal?.ToString() == "funded")
            {
                await TryDecreaseListingQuantityAsync(existingOrder, context);
            }

            var newVersion = version + 1;
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = newVersion,
                ServerData = updateResult.Value
            };
        }

        // Conflict
        var serverData = await _orderRepo.GetByIdAsync(operation.EntityId);
        var srvVersion = serverData != null && serverData.TryGetValue("version", out var svObj)
            ? int.TryParse(svObj.ToString(), out var parsed) ? parsed : version
            : version;

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = srvVersion,
            ServerData = serverData,
            Message = updateResult.Error?.Message ?? "Version conflict."
        };
    }

    /// <summary>
    /// When an order becomes "funded", decrease the crop listing's available quantity
    /// by the order quantity. If quantity reaches 0, transition listing to "reserved".
    /// Best-effort — failures are logged but do not block the order update.
    /// </summary>
    private async Task TryDecreaseListingQuantityAsync(
        Dictionary<string, object> order, ILambdaContext context)
    {
        try
        {
            var listingId = order.TryGetValue("listingId", out var lid) ? lid?.ToString() : null;
            if (string.IsNullOrEmpty(listingId)) return;

            var orderQuantity = ParseDouble(order, "quantity");
            if (orderQuantity <= 0) return;

            var listing = await _cropRepo.GetByIdAsync(listingId);
            if (listing == null) return;

            var currentQuantity = ParseDouble(listing, "quantity");
            var listingVersion = 1;
            if (listing.TryGetValue("version", out var lvObj))
                int.TryParse(lvObj?.ToString(), out listingVersion);

            var newQuantity = Math.Max(0, currentQuantity - orderQuantity);
            var updateData = new Dictionary<string, object>
            {
                { "quantity", newQuantity.ToString("F2") }
            };

            if (newQuantity <= 0)
            {
                updateData["status"] = "reserved";
            }

            await _cropRepo.UpdateAsync(listingId, updateData, listingVersion);
            context.Logger.LogInformation(
                $"Listing {listingId}: quantity {currentQuantity} -> {newQuantity} (order funded)");
        }
        catch (Exception ex)
        {
            // Best-effort: don't fail the order update if listing update fails
            context.Logger.LogWarning(
                $"Failed to decrease listing quantity: [{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>
    /// Validate authorization for specific order state transitions.
    /// Returns null if authorized, or an error message string if not.
    /// </summary>
    private static string? ValidateOrderTransitionAuth(
        string currentStatus, string newStatus,
        string? authenticatedUserId, string? buyerId, string? farmerId)
    {
        // System-only transitions: block from client sync
        if (newStatus is "funded" or "disbursing" or "settled" or "dispute_resolved")
        {
            return $"ORDER_UNAUTHORIZED: '{newStatus}' is a system-managed transition.";
        }

        // Buyer actions: cancel, submit payment
        if (newStatus is "cancelled" or "pending_payment" or "payment_submitted")
        {
            if (authenticatedUserId != buyerId)
            {
                return $"ORDER_UNAUTHORIZED: Only the buyer can transition to '{newStatus}'.";
            }
        }

        // Farmer actions: accept, reject
        if (newStatus is "accepted" or "rejected")
        {
            if (authenticatedUserId != farmerId)
            {
                return $"ORDER_UNAUTHORIZED: Only the farmer can transition to '{newStatus}'.";
            }
        }

        // Logistics transitions (pickup_confirmed, in_transit, delivered):
        // either participant allowed — already gated by participant check

        return null;
    }

    private static List<string> ValidateOrderFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        if (isCreate)
        {
            if (!data.ContainsKey("listingId"))
                errors.Add("listingId is required.");
            if (!data.ContainsKey("buyerId"))
                errors.Add("buyerId is required.");
            if (!data.ContainsKey("farmerId"))
                errors.Add("farmerId is required.");
            if (!data.ContainsKey("buyerName"))
                errors.Add("buyerName is required.");
            if (!data.ContainsKey("farmerName"))
                errors.Add("farmerName is required.");
            if (!data.ContainsKey("cropType"))
                errors.Add("cropType is required.");
        }

        // Validate quantity
        if (data.TryGetValue("quantity", out var qObj))
        {
            if (!decimal.TryParse(qObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
                || qty <= 0 || qty > 100000)
            {
                errors.Add("quantity must be between 0.01 and 100000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("quantity is required.");
        }

        // Validate pricePerUnit
        if (data.TryGetValue("pricePerUnit", out var ppuObj))
        {
            if (!decimal.TryParse(ppuObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ppu)
                || ppu <= 0 || ppu > 10000)
            {
                errors.Add("pricePerUnit must be between 0.01 and 10000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("pricePerUnit is required.");
        }

        // Validate cropType against allowed enum
        if (data.TryGetValue("cropType", out var ctObj))
        {
            var cropType = ctObj?.ToString() ?? "";
            if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }

        // Validate string lengths
        if (data.TryGetValue("buyerName", out var bnObj))
        {
            var name = bnObj?.ToString() ?? "";
            if (name.Length > 100)
                errors.Add("buyerName must not exceed 100 characters.");
        }

        if (data.TryGetValue("farmerName", out var fnObj))
        {
            var name = fnObj?.ToString() ?? "";
            if (name.Length > 100)
                errors.Add("farmerName must not exceed 100 characters.");
        }

        if (data.TryGetValue("rejectionReason", out var rrObj))
        {
            var reason = rrObj?.ToString() ?? "";
            if (reason.Length > 500)
                errors.Add("rejectionReason must not exceed 500 characters.");
        }

        if (data.TryGetValue("cancelReason", out var crObj))
        {
            var reason = crObj?.ToString() ?? "";
            if (reason.Length > 500)
                errors.Add("cancelReason must not exceed 500 characters.");
        }

        if (data.TryGetValue("cbeTransactionId", out var cbeObj))
        {
            var txId = cbeObj?.ToString() ?? "";
            if (txId.Length > 100)
                errors.Add("cbeTransactionId must not exceed 100 characters.");
        }

        return errors;
    }

    /// <summary>
    /// Get the platform commission rate from SSM with 15-minute cache.
    /// Falls back to DefaultCommissionRate if SSM is unavailable.
    /// </summary>
    private static double GetCommissionRate(ILambdaContext context)
    {
        if (DateTime.UtcNow - _commissionRateFetchedAt < CommissionRateCacheTtl)
        {
            return _cachedCommissionRate;
        }

        try
        {
            var paramName = Environment.GetEnvironmentVariable("COMMISSION_RATE_PARAM");
            if (string.IsNullOrEmpty(paramName))
            {
                _cachedCommissionRate = DefaultCommissionRate;
                _commissionRateFetchedAt = DateTime.UtcNow;
                return _cachedCommissionRate;
            }

            var value = AwsParameterResolver.GetSsmParameterValue(paramName);
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) && rate > 0 && rate < 1)
            {
                _cachedCommissionRate = rate;
            }
            else
            {
                context.Logger.LogWarning($"Invalid commission rate from SSM: '{value}'. Using default {DefaultCommissionRate}.");
                _cachedCommissionRate = DefaultCommissionRate;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to fetch commission rate from SSM: [{ex.GetType().Name}]. Using default.");
            _cachedCommissionRate = DefaultCommissionRate;
        }

        _commissionRateFetchedAt = DateTime.UtcNow;
        return _cachedCommissionRate;
    }

    private static double ParseDouble(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value)) return 0.0;
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0.0,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r2) ? r2 : 0.0,
            _ => 0.0
        };
    }

    private static double? ParseDoubleOrNull(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value)) return null;
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r2) ? r2 : null,
            _ => null
        };
    }
}

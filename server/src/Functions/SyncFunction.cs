using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    // auth_time of the current request's token (Lambda handles one request at a
    // time per instance, so a field is safe); consumed by the payment step-up gate.
    private long? _requestAuthTime;
    private readonly IIntentRepository? _intentRepo;
    private readonly IMatchEventRepository? _matchEventRepo;
    // Optional: when the escrows table is provisioned, confirming a delivery
    // stamps the linked escrow so the auto-release cron can pay the farmer.
    private readonly IEscrowRepository? _escrowRepo;
    // Optional: the order-funded escrow leg (escrow record at order funding,
    // delivery linkage, refund scheduling). Constructed WITHOUT Telebirr wiring —
    // these paths never move money themselves (escrow-protocol.md §Order-funded
    // escrow). Needs both ESCROWS_TABLE and ESCROW_AUDIT_TABLE; absent → null
    // (orders still fund; the escrow record simply isn't materialized here).
    private readonly EscrowService? _orderEscrowService;
    // ESC-01: the funding side effects (stock + order escrow) shared with the live
    // Telebirr webhook, so mock and live funding can never drift apart.
    private readonly OrderFundingService _orderFunding;
    // Optional: when the ratings table is provisioned, buyers can rate
    // counterparties after a delivered order. Absent env → null (rating sync
    // simply returns RATING_UNAVAILABLE).
    private readonly IRatingRepository? _ratingRepo;
    // Optional: when the messages table is provisioned, participants of a
    // marketplace context (crop/order/trip/delivery) can chat. Absent env → null
    // (message sync simply returns MESSAGE_UNAVAILABLE).
    private readonly IMessageRepository? _messageRepo;
    // Optional: unified transaction-audit trail for order/delivery lifecycle
    // transitions. Absent env → null (transitions still process; simply unlogged).
    private readonly ITransactionAuditRepository? _transactionAuditRepo;
    // Optional: per-delivery QR handoff secret store, kept SEPARATE from the delivery
    // record so the pickup nonce can never leak via any sync-down/GSI/DTO/conflict path.
    // Absent env → null (falls back to the legacy on-record qrNonce for in-flight deliveries).
    private readonly IDeliveryQrSecretRepository? _qrSecretRepo;

    // Who/where for the current sync batch (same user/IP/correlation across all
    // operations in one request). Set at the top of FunctionHandler. Lambda runs
    // one invocation per instance at a time, so a per-batch field is safe.
    private AuditContext? _batchAudit;

    // SSM commission rate cache (15-min TTL)
    private static double _cachedCommissionRate;
    private static DateTime _commissionRateFetchedAt = DateTime.MinValue;
    private static readonly TimeSpan CommissionRateCacheTtl = TimeSpan.FromMinutes(15);
    private const double DefaultCommissionRate = 0.05;

    /// <summary>
    /// SIM-05/SIM-07 units: order.quantity is KILOGRAMS on the wire; listing stock and
    /// trip capacity are quintals and trip prices are per quintal (order-lifecycle.md).
    /// </summary>
    private const double KgPerQuintal = 100.0;

    /// <summary>ESC-01: what one live Telebirr payment can take (escrow-protocol.md §Amount Validation).</summary>
    private const decimal LiveMinPaymentEtb = 100m;
    private const decimal LiveMaxPaymentEtb = 500_000m;

    /// <summary>Client-echoed money must match the server's computation to the santim.</summary>
    private const double MoneyTolerance = 0.01;

    /// <summary>Slack for floating-point kg comparisons against quintal stock.</summary>
    private const double QuantityTolerance = 0.001;

    /// <summary>Largest crop listing stock, in quintals (crop-listing-api.md).</summary>
    private const decimal MaxListingQuintals = 10000m;

    /// <summary>
    /// Largest order, in kilograms: a whole listing at its maximum stock (MaxListingQuintals x
    /// KgPerQuintal). The real bound is the listing's stock, checked in BindOrderToListing.
    /// </summary>
    private const decimal MaxOrderKg = MaxListingQuintals * 100m;

    private static readonly HashSet<string> AllowedCropFields = new()
    {
        "farmerId", "farmerName", "farmerPhone", "cropType", "quantity", "pricePerKg",
        "latitude", "longitude", "locationName", "description",
        "status", "woreda", "listingId", "audioNoteRef", "createdAt"
    };

    private static readonly HashSet<string> AllowedIntentFields = new()
    {
        "intentId", "buyerId", "cropType", "quantityNeeded", "maxPricePerKg",
        "deliveryWoreda", "deliveryLatitude", "deliveryLongitude", "neededByDate",
        "status", "createdAt"
    };

    private static readonly HashSet<string> AllowedRatingFields = new()
    {
        "ratingId", "orderId", "raterId", "rateeId", "rateeRole", "stars",
        "comment", "createdAt"
    };

    private static readonly HashSet<string> AllowedRateeRoles = new(StringComparer.Ordinal)
    {
        "farmer", "trucker"
    };

    // Client-supplied message fields. Sender/recipient name+phone are NOT here:
    // the server denormalizes them authoritatively from the users table, so a
    // client cannot spoof the counterpart's identity or phone number.
    private static readonly HashSet<string> AllowedMessageFields = new()
    {
        "messageId", "conversationId", "contextType", "contextId",
        "senderId", "recipientId", "body", "createdAt"
    };

    // The four context kinds a conversation may be scoped to.
    private static readonly HashSet<string> AllowedMessageContextTypes = new(StringComparer.Ordinal)
    {
        "crop", "order", "trip", "delivery"
    };

    private static readonly HashSet<string> AllowedIntentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "fulfilled", "expired"
    };

    private static readonly HashSet<string> AllowedMatchEventFields = new()
    {
        "eventId", "intentId", "buyerId", "eventType", "listingId",
        "score", "rank", "isSubstitute", "matchCount", "impressions",
        "occurredAt", "policyTag"
    };

    private static readonly HashSet<string> AllowedMatchEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "shown", "opened", "order_created"
    };

    /// <summary>Upper bound for the JSON-string `impressions` payload (bytes-ish).</summary>
    private const int MaxImpressionsLength = 8192;

    /// <summary>
    /// SIM-02: identity fields a crop update may echo unchanged (web clients resend
    /// them) but may never CHANGE. Rewriting farmerId moved listings into other
    /// users' accounts; farmerName/farmerPhone spoofed the contact shown to buyers.
    /// </summary>
    private static readonly HashSet<string> ImmutableCropIdentityFields = new()
    {
        "farmerId", "farmerName", "farmerPhone"
    };

    /// <summary>SIM-03: same guard for trips (truckerId/Name/Phone).</summary>
    private static readonly HashSet<string> ImmutableTripIdentityFields = new()
    {
        "truckerId", "truckerName", "truckerPhone"
    };

    /// <summary>
    /// SIM-09: delivery fields fixed at create (parties, order, listing, trip, type). An
    /// update may echo them unchanged; re-pointing any of them is rejected.
    /// </summary>
    private static readonly HashSet<string> ImmutableDeliveryIdentityFields = new()
    {
        "orderId", "tripId", "listingId", "deliveryType",
        "buyerId", "buyerName", "buyerPhone",
        "farmerId", "farmerName", "farmerPhone",
        "truckerId", "truckerName", "truckerPhone"
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
        "qrToken", "gpsAnomalyFlag", "qrNonce", "deliveryType"
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
        "paymentVerifiedAt", "respondedAt", "cancelledAt",
        // ESC-01: the live Telebirr payment behind a funding; only the webhook writes them.
        // paymentSubmittedAt starts the payment clock and is stamped by the server.
        OrderFundingService.MerchOrderIdField, OrderFundingService.PaymentOrderIdField,
        OrderFundingService.TransIdField, "paymentSubmittedAt"
    };

    /// <summary>
    /// SIM-06: the only order fields a client may change after create. Everything else in
    /// AllowedOrderFields identifies or prices the order and is immutable; the money
    /// fields are server-owned (OrderServerOnlyFields) so orderTotal can never drift.
    /// </summary>
    private static readonly HashSet<string> UpdatableOrderFields = new()
    {
        "status", "rejectionReason", "cancelReason", "paymentMethod", "cbeTransactionId"
    };

    /// <summary>
    /// Bookkeeping keys the shipped clients echo on every order update. They are checked
    /// (orderId) or dropped (updatedAt is stamped by the repository); version is consumed
    /// by the optimistic-concurrency check.
    /// </summary>
    private static readonly HashSet<string> OrderUpdateEchoFields = new()
    {
        "orderId", "updatedAt", "version"
    };

    private static readonly Dictionary<string, HashSet<string>> OrderTransitions = new()
    {
        // Buyers pay immediately after placing an order, so allow the direct
        // created -> payment_submitted hop as well as the pending_payment step.
        { "created", new HashSet<string> { "pending_payment", "payment_submitted", "cancelled" } },
        { "pending_payment", new HashSet<string> { "payment_submitted", "cancelled" } },
        { "payment_submitted", new HashSet<string> { "funded", "payment_failed" } },
        // Farmer responds to a funded order with accept or reject; reject is a
        // terminal state (authorization + respondedAt/audit side-effects already
        // handle "rejected" — only this transition entry was missing).
        { "funded", new HashSet<string> { "accepted", "rejected", "cancelled" } },
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
        "tripId", "listingId", "deliveryType"
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
        // "delivered" allowed directly from pickedUp: the buyer confirms handoff
        // right after pickup in the capture flow; marking in-transit is optional.
        { "pickedUp", new HashSet<string> { "inTransit", "delivered", "cancelled" } },
        { "inTransit", new HashSet<string> { "delivered" } },
        { "delivered", new HashSet<string> { "disputed" } },
    };

    // Self-pickup deliveries have no trucker/transit leg: the buyer collects at the farm,
    // so a single scan of the farmer's QR takes pending → delivered in one hop. This
    // transition is permitted ONLY for deliveryType == self_pickup (see the transition
    // authorization below), never for a normal trucker delivery.
    private const string SelfPickupDeliveryType = "self_pickup";

    // QR handoff freshness / offline-defer windows (days). Generous, because rural
    // handoffs sync late; the point is to stop indefinitely-stale codes and to give up
    // deferring a money-bearing transition whose nonce is clearly never going to arrive.
    private const int QrNonceFreshnessDays = 14;
    private const int QrNonceMaxDeferDays = 14;

    /// <summary>
    /// Scrub secret fields from ANY entity item before it is echoed to a client as sync
    /// <c>ServerData</c> (conflict/success). Currently the only such field is the delivery
    /// QR handoff nonce: new deliveries never store it on the record (it lives in the separate
    /// secret store), but this also cleans legacy in-flight items so the pickup nonce can never
    /// ride back down to a scanner. A no-op for entities that have no secret field.
    /// </summary>
    private static Dictionary<string, object> ScrubSyncSecrets(Dictionary<string, object> item)
    {
        item.Remove("qrNonce");
        return item;
    }

    /// <summary>
    /// Constant-time comparison of a scanned QR nonce against the stored one. The nonce is a
    /// high-entropy bearer secret, so compare in fixed time to avoid leaking a match prefix
    /// via timing. Differing lengths short-circuit to false (length is not sensitive here).
    /// </summary>
    private static bool NonceEquals(string scanned, string expected)
    {
        var a = Encoding.UTF8.GetBytes(scanned);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

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
        // Fraud signals feed the admin review queue; absent env → writes no-op.
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        _fraudService = new FraudDetectionService(
            _dynamoDb,
            cropsTable,
            fraudSignalsTable,
            new RateLimitRepository(_dynamoDb, rateLimitTable));
        _userRepo = new UserRepository(_dynamoDb, usersTable);

        // Intents are optional: the buyer_intent sync handler is only reachable once
        // the intents table is provisioned (push-alerts feature). Absent env → null.
        var intentsTable = Environment.GetEnvironmentVariable("INTENTS_TABLE");
        _intentRepo = intentsTable != null
            ? new IntentRepository(_dynamoDb, intentsTable)
            : null;

        // Match-event logging is optional in the same way: the match_event sync
        // handler is only reachable once the match-events table is provisioned.
        var matchEventsTable = Environment.GetEnvironmentVariable("MATCH_EVENTS_TABLE");
        _matchEventRepo = matchEventsTable != null
            ? new MatchEventRepository(_dynamoDb, matchEventsTable)
            : null;

        // Escrow bridge is optional: confirming a delivery stamps deliveryConfirmedAt
        // on the linked escrow so the auto-release cron can settle. Absent env → null
        // (delivery still transitions; escrow simply isn't stamped from here).
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE");
        _escrowRepo = escrowsTable != null
            ? new EscrowRepository(_dynamoDb, escrowsTable)
            : null;

        // Order-funded escrow leg. Reuses _escrowRepo; the audit table is required
        // because every escrow record mutation must land in the immutable ledger.
        var escrowAuditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE");
        _orderEscrowService = _escrowRepo != null && escrowAuditTable != null
            ? new EscrowService(
                _escrowRepo,
                new EscrowAuditRepository(_dynamoDb, escrowAuditTable),
                idempotencyRepo: null,
                telebirrGateway: null,
                userRepo: _userRepo,
                orderRepo: _orderRepo,
                deliveryRepo: new DeliveryRepository(_dynamoDb, _deliveriesTable))
            : null;
        _orderFunding = new OrderFundingService(_orderRepo, _cropRepo, _orderEscrowService);

        // Ratings are optional in the same way: the rating sync handler is only
        // reachable once the ratings table is provisioned.
        var ratingsTable = Environment.GetEnvironmentVariable("RATINGS_TABLE");
        _ratingRepo = ratingsTable != null
            ? new RatingRepository(_dynamoDb, ratingsTable)
            : null;

        // Messages are optional in the same way: the message sync handler is only
        // reachable once the messages table is provisioned.
        var messagesTable = Environment.GetEnvironmentVariable("MESSAGES_TABLE");
        _messageRepo = messagesTable != null
            ? new MessageRepository(_dynamoDb, messagesTable)
            : null;

        // Optional: unified transaction-audit trail (order/delivery transitions).
        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _transactionAuditRepo = txAuditTable != null
            ? new TransactionAuditRepository(_dynamoDb, txAuditTable)
            : null;

        // Optional: per-delivery QR handoff secret store. Absent env → null (legacy
        // on-record qrNonce fallback keeps in-flight deliveries verifiable).
        var qrSecretsTable = Environment.GetEnvironmentVariable("DELIVERY_QR_SECRETS_TABLE");
        _qrSecretRepo = qrSecretsTable != null
            ? new DeliveryQrSecretRepository(_dynamoDb, qrSecretsTable)
            : null;
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo,
        IAmazonDynamoDB dynamoDb, string tripsTable, string deliveriesTable,
        IRateLimitService? rateLimitService = null,
        IFraudDetectionService? fraudService = null,
        IUserRepository? userRepo = null,
        IOrderRepository? orderRepo = null,
        IIntentRepository? intentRepo = null,
        IMatchEventRepository? matchEventRepo = null,
        IEscrowRepository? escrowRepo = null,
        IRatingRepository? ratingRepo = null,
        ITransactionAuditRepository? transactionAuditRepo = null,
        IDeliveryQrSecretRepository? qrSecretRepo = null,
        IMessageRepository? messageRepo = null,
        EscrowService? orderEscrowService = null)
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
        _intentRepo = intentRepo;
        _matchEventRepo = matchEventRepo;
        _escrowRepo = escrowRepo;
        _ratingRepo = ratingRepo;
        _transactionAuditRepo = transactionAuditRepo;
        _qrSecretRepo = qrSecretRepo;
        _messageRepo = messageRepo;
        _orderEscrowService = orderEscrowService;
        _orderFunding = new OrderFundingService(_orderRepo, _cropRepo, _orderEscrowService);
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

        var authenticatedRole = AuthHelper.GetClaim(request, "custom:role");
        _requestAuthTime = AuthHelper.GetAuthTime(request);

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

        var correlationId = AuditLogger.GetCorrelationId(request);

        // Who/where for every audit event emitted while processing this batch.
        _batchAudit = AuditContext.From(request, authenticatedUserId, authenticatedRole);

        AuditLogger.LogAudit("SYNC", "BATCH_START",
            userId: authenticatedUserId, correlationId: correlationId,
            detail: $"{syncRequest.Operations.Count} operations");

        // Process each operation independently
        var results = new List<SyncOperationResult>();

        foreach (var operation in syncRequest.Operations)
        {
            var result = await ProcessOperationAsync(operation, authenticatedUserId, authenticatedRole, context);
            context.Logger.LogInformation(
                $"Op {result.OperationId}: status={result.Status}, msg={result.Message ?? "none"}");
            results.Add(result);
        }

        var successCount = results.Count(r => r.Status == "SYNC_SUCCESS" || r.Status == "SYNC_DUPLICATE");
        var failCount = results.Count - successCount;

        AuditLogger.LogAudit("SYNC", "BATCH_COMPLETE",
            userId: authenticatedUserId, correlationId: correlationId,
            detail: $"{successCount} ok, {failCount} failed");

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
        string? authenticatedRole,
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
                "crop_listing" => await ProcessCropListingAsync(operation, authenticatedUserId, authenticatedRole, context),
                "buyer_intent" => await ProcessBuyerIntentAsync(operation, authenticatedUserId, authenticatedRole, context),
                "match_event" => await ProcessMatchEventAsync(operation, authenticatedUserId, authenticatedRole),
                "trip" => await ProcessTripAsync(operation, authenticatedUserId, authenticatedRole, context),
                "delivery" => await ProcessDeliveryAsync(operation, authenticatedUserId, context),
                "order" => await ProcessOrderAsync(operation, authenticatedUserId, authenticatedRole, context),
                "rating" => await ProcessRatingAsync(operation, authenticatedUserId, authenticatedRole, context),
                "message" => await ProcessMessageAsync(operation, authenticatedUserId, authenticatedRole, context),
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
    /// Reject an update that changes an immutable identity field; strip identity
    /// fields that merely echo the stored value so they are not rewritten.
    /// Phone fields are compared in normalized (+251...) form because a client may
    /// echo the local 09... spelling of a number the server stored normalized, and
    /// numeric fields are compared as numbers (an echoed 50 must match a stored 50.0).
    /// Returns an error message (without the code prefix) or null when clean.
    /// </summary>
    private static string? RejectImmutableFieldChanges(
        Dictionary<string, object> data,
        HashSet<string> immutableFields,
        Func<string, string?> storedValue)
    {
        foreach (var field in immutableFields)
        {
            if (!data.TryGetValue(field, out var incoming))
            {
                continue;
            }

            var incomingText = (incoming?.ToString() ?? "").Trim();
            var stored = (storedValue(field) ?? "").Trim();
            if (field.EndsWith("Phone", StringComparison.Ordinal))
            {
                incomingText = Validation.RequestValidator.NormalizePhoneNumber(incomingText) ?? incomingText;
                stored = Validation.RequestValidator.NormalizePhoneNumber(stored) ?? stored;
            }

            var unchanged = string.Equals(incomingText, stored, StringComparison.Ordinal)
                || (double.TryParse(incomingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var incomingNumber)
                    && double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var storedNumber)
                    && Math.Abs(incomingNumber - storedNumber) <= QuantityTolerance);
            if (!unchanged)
            {
                return $"{field} is immutable and cannot be changed on update.";
            }

            data.Remove(field);
        }

        return null;
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
                || qty < 0.01m || qty > MaxListingQuintals)
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

        // SIM-17: geo and date rules run on create AND update (the contract's
        // server-side rules; they used to be enforced nowhere on the server).
        // Coordinates are optional (GPS may not work in remote areas): absent,
        // null or empty is fine, a present value must be a number inside the
        // Ethiopia bounding box.
        if (TryReadOptionalDouble(data, "latitude", out var latitude, out var latitudeInvalid))
        {
            if (latitudeInvalid || latitude < EthiopiaMinLatitude || latitude > EthiopiaMaxLatitude)
            {
                errors.Add($"latitude must be between {EthiopiaMinLatitude} and {EthiopiaMaxLatitude} (Ethiopia).");
            }
        }

        if (TryReadOptionalDouble(data, "longitude", out var longitude, out var longitudeInvalid))
        {
            if (longitudeInvalid || longitude < EthiopiaMinLongitude || longitude > EthiopiaMaxLongitude)
            {
                errors.Add($"longitude must be between {EthiopiaMinLongitude} and {EthiopiaMaxLongitude} (Ethiopia).");
            }
        }

        // createdAt is server-stamped (the repository ignores the client value),
        // but a nonsense value must not be accepted silently. Clients send local
        // time without an offset and device clocks drift, so allow a day of skew.
        if (data.TryGetValue("createdAt", out var createdAtObj))
        {
            var createdAtText = createdAtObj?.ToString();
            if (!string.IsNullOrWhiteSpace(createdAtText))
            {
                if (!DateTime.TryParse(createdAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAtUtc))
                {
                    errors.Add("createdAt must be an ISO 8601 timestamp.");
                }
                else if (createdAtUtc > DateTime.UtcNow.Add(CreatedAtFutureSkew))
                {
                    errors.Add("createdAt cannot be in the future.");
                }
            }
        }

        return errors;
    }

    /// <summary>Ethiopia bounding box (contract: 3-15°N, 33-48°E).</summary>
    private const double EthiopiaMinLatitude = 3.0;
    private const double EthiopiaMaxLatitude = 15.0;
    private const double EthiopiaMinLongitude = 33.0;
    private const double EthiopiaMaxLongitude = 48.0;

    /// <summary>
    /// How far ahead of server time a client createdAt may sit before it is a
    /// future date. Covers local-time strings sent without an offset (UTC+3)
    /// plus device clock drift, without accepting a listing dated next year.
    /// </summary>
    private static readonly TimeSpan CreatedAtFutureSkew = TimeSpan.FromHours(24);

    /// <summary>
    /// Read an optional numeric field. Returns false when the key is absent or
    /// its value is null/empty (treated as not provided). When present,
    /// <paramref name="invalid"/> is true if the value is not a finite number.
    /// </summary>
    private static bool TryReadOptionalDouble(
        Dictionary<string, object> data, string key, out double value, out bool invalid)
    {
        value = 0;
        invalid = false;
        if (!data.TryGetValue(key, out var obj))
        {
            return false;
        }

        var text = obj?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        invalid = !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value) || double.IsInfinity(value);
        return true;
    }

    /// <summary>
    /// Validate buyer-intent field values. Mirrors the crop validator's style.
    /// </summary>
    private static List<string> ValidateIntentFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        if (isCreate)
        {
            var cropType = data.GetValueOrDefault("cropType")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(cropType))
            {
                errors.Add("cropType is required.");
            }
            else if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }

        // Parse invariantly: client numbers are JSON (period decimal), so a
        // fractional quantity/price must not be rejected on a comma-decimal
        // server locale.
        if (data.TryGetValue("quantityNeeded", out var qObj)
            && (!double.TryParse(qObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var q) || q <= 0))
        {
            errors.Add("quantityNeeded must be a positive number.");
        }

        // 0 is a valid value: it means "no budget set" (the max-price field is
        // optional on the client) and scores as unconstrained in MatchScoring.
        if (data.TryGetValue("maxPricePerKg", out var pObj)
            && (!double.TryParse(pObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var p) || p < 0))
        {
            errors.Add("maxPricePerKg must be a non-negative number.");
        }

        if (data.TryGetValue("status", out var sObj))
        {
            var status = sObj?.ToString() ?? "";
            if (!AllowedIntentStatuses.Contains(status))
            {
                errors.Add("Invalid status.");
            }
        }

        // Optional delivery deadline — must be a parseable ISO 8601 date when present.
        if (data.TryGetValue("neededByDate", out var nObj)
            && !string.IsNullOrWhiteSpace(nObj?.ToString())
            && !DateTime.TryParse(nObj!.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
        {
            errors.Add("neededByDate must be an ISO 8601 date.");
        }

        return errors;
    }

    /// <summary>
    /// Process a buyer_intent sync operation (create, update, delete).
    ///
    /// Buyer intents are owned by the authenticated buyer; only role "buyer" may
    /// create/modify them, and the data's buyerId must match the caller. New crops
    /// are matched against these intents for push alerts (see
    /// specs/architecture/push-notifications-architecture.md).
    /// </summary>
    private async Task<SyncOperationResult> ProcessBuyerIntentAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_intentRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "INTENT_UNAVAILABLE: Intent sync is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "INTENT_UNAUTHORIZED: Only buyers can create purchase intents."
            };
        }

        switch (operation.Action)
        {
            case "create":
            case "update":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Operation requires data payload."
                        };
                    }

                    // buyerId in the payload must match the authenticated user.
                    if (!operation.Data.TryGetValue("buyerId", out var buyerIdObj)
                        || buyerIdObj?.ToString() != authenticatedUserId)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "INTENT_UNAUTHORIZED: buyerId must match authenticated user."
                        };
                    }

                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedIntentFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"INTENT_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    var validationErrors = ValidateIntentFields(operation.Data, operation.Action == "create");
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"INTENT_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    if (operation.Action == "create")
                    {
                        operation.Data["status"] = "active";
                        var createResult = await _intentRepo.CreateAsync(
                            operation.EntityId, authenticatedUserId!, operation.Data);
                        return createResult.IsSuccess
                            ? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "success",
                                ServerVersion = 1,
                                ServerData = createResult.Value
                            }
                            : new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "conflict",
                                Message = createResult.Error?.Message ?? "Intent already exists."
                            };
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    var updateResult = await _intentRepo.UpdateAsync(
                        operation.EntityId, authenticatedUserId!, operation.Data, clientVersion);
                    return updateResult.IsSuccess
                        ? new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = clientVersion + 1,
                            ServerData = updateResult.Value
                        }
                        : new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            Message = updateResult.Error?.Message ?? "Intent conflict."
                        };
                }

            case "delete":
                {
                    await _intentRepo.DeleteAsync(operation.EntityId, authenticatedUserId!);
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        Message = "Intent deleted."
                    };
                }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Unsupported action: '{operation.Action}'."
                };
        }
    }

    /// <summary>
    /// Process a rating sync operation. Create-only and immutable: a buyer rates
    /// a counterparty (farmer or trucker) for a delivered/settled order they own.
    /// The deterministic ratingId ("{orderId}:{rateeId}") + attribute_not_exists
    /// create condition guarantee one rating per (order, ratee).
    /// </summary>
    private async Task<SyncOperationResult> ProcessRatingAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_ratingRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAVAILABLE: Rating sync is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAUTHORIZED: Only buyers can rate counterparties."
            };
        }

        // Ratings are immutable: only `create` is supported.
        if (!string.Equals(operation.Action, "create", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: Unsupported action '{operation.Action}'. Ratings are immutable."
            };
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Operation requires data payload."
            };
        }

        // raterId in the payload must match the authenticated user.
        if (!operation.Data.TryGetValue("raterId", out var raterIdObj)
            || raterIdObj?.ToString() != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAUTHORIZED: raterId must match authenticated user."
            };
        }

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedRatingFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        var validationErrors = ValidateRatingFields(operation.Data);
        if (validationErrors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: {string.Join(" ", validationErrors)}"
            };
        }

        var orderId = operation.Data["orderId"].ToString()!;
        var rateeId = operation.Data["rateeId"].ToString()!;
        var rateeRole = operation.Data["rateeRole"].ToString()!;

        // The rating must be about a delivered/settled order the buyer owns, and
        // the ratee must be that order's farmer or trucker.
        var order = await _orderRepo.GetByIdAsync(orderId);
        if (order == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: Order not found."
            };
        }

        var orderBuyerId = order.TryGetValue("buyerId", out var bObj) ? bObj?.ToString() : null;
        if (orderBuyerId != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_FORBIDDEN: You can only rate your own orders."
            };
        }

        var orderStatus = order.TryGetValue("status", out var sObj) ? sObj?.ToString() : null;
        if (orderStatus != "delivered" && orderStatus != "settled")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_FORBIDDEN: You can only rate after the order is delivered."
            };
        }

        var orderFarmerId = order.TryGetValue("farmerId", out var fObj) ? fObj?.ToString() : null;
        var orderTruckerId = order.TryGetValue("truckerId", out var tObj) ? tObj?.ToString() : null;
        var validRatee = (rateeRole == "farmer" && rateeId == orderFarmerId)
            || (rateeRole == "trucker" && rateeId == orderTruckerId
                && !string.IsNullOrEmpty(orderTruckerId));
        if (!validRatee)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: ratee must be the order's farmer or trucker."
            };
        }

        // Deterministic id enforces one rating per (order, ratee).
        if (operation.EntityId != $"{orderId}:{rateeId}")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: ratingId must equal '{orderId}:{rateeId}'."
            };
        }

        var createResult = await _ratingRepo.CreateAsync(operation.EntityId, operation.Data);
        return createResult.IsSuccess
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            }
            : new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "conflict",
                Message = createResult.Error?.Message ?? "Rating already exists."
            };
    }

    /// <summary>
    /// Validate rating fields: orderId/rateeId required, rateeRole in
    /// {farmer, trucker}, stars an integer 1-5, comment ≤ 500 chars.
    /// </summary>
    private static List<string> ValidateRatingFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        if (!data.TryGetValue("orderId", out var orderIdObj)
            || string.IsNullOrWhiteSpace(orderIdObj?.ToString()))
        {
            errors.Add("orderId is required.");
        }

        if (!data.TryGetValue("rateeId", out var rateeIdObj)
            || string.IsNullOrWhiteSpace(rateeIdObj?.ToString()))
        {
            errors.Add("rateeId is required.");
        }

        if (!data.TryGetValue("rateeRole", out var roleObj)
            || !AllowedRateeRoles.Contains(roleObj?.ToString() ?? ""))
        {
            errors.Add("rateeRole must be 'farmer' or 'trucker'.");
        }

        if (!data.TryGetValue("stars", out var starsObj)
            || !int.TryParse(starsObj?.ToString(), out var stars) || stars < 1 || stars > 5)
        {
            errors.Add("stars must be an integer between 1 and 5.");
        }

        if (data.TryGetValue("comment", out var commentObj) && commentObj != null
            && commentObj.ToString()!.Length > 500)
        {
            errors.Add("comment must be 500 characters or fewer.");
        }

        return errors;
    }

    /// <summary>
    /// Process a message sync operation (context-scoped in-app chat). Messages
    /// are create-only and immutable. The CRITICAL role/visibility gate lives
    /// here: the sender must be the authenticated user, and both the sender and
    /// recipient must be legitimate participants of the referenced context
    /// (crop/order/trip/delivery). Sender/recipient name+phone are denormalized
    /// authoritatively from the users table so neither side can spoof them and
    /// either party can Call/Copy the other's number.
    /// </summary>
    private async Task<SyncOperationResult> ProcessMessageAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_messageRepo == null || _userRepo == null)
        {
            return MessageError(operation, "error",
                "MESSAGE_UNAVAILABLE: Messaging is not enabled on this environment.");
        }

        // Messages are immutable: only `create` is supported.
        if (!string.Equals(operation.Action, "create", StringComparison.OrdinalIgnoreCase))
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: Unsupported action '{operation.Action}'. Messages are immutable.");
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return MessageError(operation, "error", "Operation requires data payload.");
        }

        // Strip server-denormalized identity fields so a client can neither spoof
        // them nor be rejected for echoing them back on a resend.
        operation.Data.Remove("senderName");
        operation.Data.Remove("senderPhone");
        operation.Data.Remove("recipientName");
        operation.Data.Remove("recipientPhone");

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedMessageFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}");
        }

        var validationErrors = ValidateMessageFields(operation.Data);
        if (validationErrors.Count > 0)
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: {string.Join(" ", validationErrors)}");
        }

        var messageId = operation.Data["messageId"].ToString()!;
        var conversationId = operation.Data["conversationId"].ToString()!;
        var contextType = operation.Data["contextType"].ToString()!;
        var contextId = operation.Data["contextId"].ToString()!;
        var senderId = operation.Data["senderId"].ToString()!;
        var recipientId = operation.Data["recipientId"].ToString()!;

        // The message id (entityId) must equal the client-generated messageId.
        if (operation.EntityId != messageId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: messageId must equal the operation entityId.");
        }

        // The sender must be the authenticated user (the hard rule).
        if (senderId != authenticatedUserId)
        {
            return MessageError(operation, "error",
                "MESSAGE_UNAUTHORIZED: senderId must match authenticated user.");
        }

        if (senderId == recipientId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: sender and recipient must differ.");
        }

        // conversationId must be the deterministic "{contextType}:{contextId}:{lo}:{hi}"
        // over the sorted participant ids — ties the grouping key to this exact
        // context + pair so a client cannot mis-group or hijack a thread.
        var sortedIds = new[] { senderId, recipientId };
        Array.Sort(sortedIds, StringComparer.Ordinal);
        var expectedConversationId = $"{contextType}:{contextId}:{sortedIds[0]}:{sortedIds[1]}";
        if (conversationId != expectedConversationId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: conversationId does not match the context and participants.");
        }

        // Verify BOTH parties are legitimate participants of the context. Never
        // trust a client-supplied role — derive legitimacy from the record.
        var contextError = await VerifyMessageContextAsync(
            contextType, contextId, senderId, recipientId);
        if (contextError != null)
        {
            return MessageError(operation, "error", contextError);
        }

        // Denormalize both parties' name + phone authoritatively from the users
        // table (overwrites anything the client might have sent).
        var sender = await _userRepo.GetUserByIdAsync(senderId);
        var recipient = await _userRepo.GetUserByIdAsync(recipientId);
        if (sender == null || recipient == null)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: sender or recipient user not found.");
        }

        operation.Data["senderName"] = sender.DisplayName ?? sender.FullName ?? "";
        operation.Data["senderPhone"] = sender.PhoneNumber;
        operation.Data["recipientName"] = recipient.DisplayName ?? recipient.FullName ?? "";
        operation.Data["recipientPhone"] = recipient.PhoneNumber;

        var createResult = await _messageRepo.CreateAsync(operation.EntityId, operation.Data);
        return createResult.IsSuccess
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            }
            : new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "duplicate",
                Message = createResult.Error?.Message ?? "Message already exists."
            };
    }

    /// <summary>
    /// Verify sender and recipient are the two legitimate parties for the
    /// referenced context. Returns null when valid, or an error message string.
    /// </summary>
    private async Task<string?> VerifyMessageContextAsync(
        string contextType, string contextId, string senderId, string recipientId)
    {
        switch (contextType)
        {
            case "crop":
                {
                    var crop = await _cropRepo.GetByIdAsync(contextId);
                    if (crop == null)
                    {
                        return "MESSAGE_VALIDATION: Crop not found.";
                    }

                    var farmerId = ItemString(crop, "farmerId");
                    // Exactly one party owns the crop (the farmer); the other must be
                    // a buyer (buyers may message about ANY crop).
                    return await VerifyOwnerBuyerPairAsync(farmerId, "buyer", senderId, recipientId);
                }

            case "trip":
                {
                    var trip = await GetTripItemAsync(contextId);
                    if (trip == null)
                    {
                        return "MESSAGE_VALIDATION: Trip not found.";
                    }

                    var truckerId = AttrString(trip, "truckerId");
                    // Exactly one party owns the trip (the trucker); the other buyer.
                    return await VerifyOwnerBuyerPairAsync(truckerId, "buyer", senderId, recipientId);
                }

            case "order":
                {
                    var order = await _orderRepo.GetByIdAsync(contextId);
                    if (order == null)
                    {
                        return "MESSAGE_VALIDATION: Order not found.";
                    }

                    var participants = new HashSet<string>(StringComparer.Ordinal);
                    AddIfPresent(participants, ItemString(order, "buyerId"));
                    AddIfPresent(participants, ItemString(order, "farmerId"));
                    AddIfPresent(participants, ItemString(order, "truckerId"));
                    return VerifyDistinctParticipants(participants, senderId, recipientId);
                }

            case "delivery":
                {
                    var delivery = await GetDeliveryItemAsync(contextId);
                    if (delivery == null)
                    {
                        return "MESSAGE_VALIDATION: Delivery not found.";
                    }

                    var participants = new HashSet<string>(StringComparer.Ordinal);
                    AddIfPresent(participants, AttrString(delivery, "buyerId"));
                    AddIfPresent(participants, AttrString(delivery, "farmerId"));
                    AddIfPresent(participants, AttrString(delivery, "truckerId"));
                    return VerifyDistinctParticipants(participants, senderId, recipientId);
                }

            default:
                return "MESSAGE_VALIDATION: Unknown contextType.";
        }
    }

    /// <summary>
    /// For crop/trip: exactly one of the two parties must be the record owner,
    /// and the other party must have <paramref name="otherRole"/> (buyer).
    /// </summary>
    private async Task<string?> VerifyOwnerBuyerPairAsync(
        string ownerId, string otherRole, string senderId, string recipientId)
    {
        if (string.IsNullOrEmpty(ownerId))
        {
            return "MESSAGE_FORBIDDEN: Context has no owner to message.";
        }

        var senderIsOwner = senderId == ownerId;
        var recipientIsOwner = recipientId == ownerId;
        if (senderIsOwner == recipientIsOwner)
        {
            // Neither is the owner, or (impossible: both) — not a valid pair.
            return "MESSAGE_FORBIDDEN: You are not a participant in this conversation.";
        }

        var otherPartyId = senderIsOwner ? recipientId : senderId;
        var otherParty = await _userRepo!.GetUserByIdAsync(otherPartyId);
        if (otherParty == null
            || !string.Equals(otherParty.Role, otherRole, StringComparison.OrdinalIgnoreCase))
        {
            return "MESSAGE_FORBIDDEN: You are not a participant in this conversation.";
        }

        return null;
    }

    /// <summary>Both parties must be distinct members of the participant set.</summary>
    private static string? VerifyDistinctParticipants(
        HashSet<string> participants, string senderId, string recipientId)
    {
        if (!participants.Contains(senderId) || !participants.Contains(recipientId))
        {
            return "MESSAGE_FORBIDDEN: You are not a participant in this conversation.";
        }

        return null;
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            set.Add(value);
        }
    }

    private static string ItemString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string AttrString(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) ? v.S ?? "" : "";

    private static SyncOperationResult MessageError(
        SyncOperationRequest operation, string status, string message)
        => new()
        {
            OperationId = operation.OperationId,
            Status = status,
            Message = message
        };

    /// <summary>
    /// Validate message fields: ids required, contextType in the allowed set,
    /// body non-empty and ≤ 1000 chars, createdAt (if present) a valid timestamp.
    /// </summary>
    private static List<string> ValidateMessageFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        foreach (var required in new[] { "messageId", "conversationId", "contextId", "senderId", "recipientId" })
        {
            if (!data.TryGetValue(required, out var v) || string.IsNullOrWhiteSpace(v?.ToString()))
            {
                errors.Add($"{required} is required.");
            }
        }

        if (!data.TryGetValue("contextType", out var ctObj)
            || !AllowedMessageContextTypes.Contains(ctObj?.ToString() ?? ""))
        {
            errors.Add("contextType must be one of crop, order, trip, delivery.");
        }

        if (!data.TryGetValue("body", out var bodyObj) || string.IsNullOrWhiteSpace(bodyObj?.ToString()))
        {
            errors.Add("body is required.");
        }
        else if (bodyObj!.ToString()!.Length > 1000)
        {
            errors.Add("body must be 1000 characters or fewer.");
        }

        if (data.TryGetValue("createdAt", out var caObj) && caObj != null
            && !DateTime.TryParse(caObj.ToString(), out _))
        {
            errors.Add("createdAt must be a valid ISO-8601 timestamp.");
        }

        return errors;
    }

    /// <summary>
    /// Process a match_event sync operation. Append-only telemetry: only
    /// `create` is supported, only by the buyer whose intent the event belongs
    /// to. Events feed the future learned ranker (specs/contracts/match-events.md);
    /// a failure to log must never block the marketplace flow, so clients treat
    /// errors here as non-fatal.
    /// </summary>
    private async Task<SyncOperationResult> ProcessMatchEventAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole)
    {
        if (_matchEventRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAVAILABLE: Match-event logging is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAUTHORIZED: Only buyers can log match events."
            };
        }

        if (operation.Action != "create")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_VALIDATION: Match events are append-only (create only)."
            };
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Operation requires data payload."
            };
        }

        if (!operation.Data.TryGetValue("buyerId", out var buyerIdObj)
            || buyerIdObj?.ToString() != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAUTHORIZED: buyerId must match authenticated user."
            };
        }

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedMatchEventFields.Contains(k))
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"MATCH_EVENT_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        var errors = ValidateMatchEventFields(operation.Data);
        if (errors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"MATCH_EVENT_VALIDATION: {string.Join(" ", errors)}"
            };
        }

        var intentId = operation.Data.GetValueOrDefault("intentId")?.ToString() ?? "";
        await _matchEventRepo.CreateAsync(
            operation.EntityId, intentId, authenticatedUserId!, operation.Data);

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = 1,
        };
    }

    /// <summary>Validate match-event field values (see AllowedMatchEventFields).</summary>
    private static List<string> ValidateMatchEventFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        var eventType = data.GetValueOrDefault("eventType")?.ToString() ?? "";
        if (!AllowedMatchEventTypes.Contains(eventType))
        {
            errors.Add("eventType must be one of: shown, opened, order_created.");
        }

        if (string.IsNullOrWhiteSpace(data.GetValueOrDefault("intentId")?.ToString()))
        {
            errors.Add("intentId is required.");
        }

        // Interaction events must say WHICH listing was interacted with.
        if (eventType is "opened" or "order_created"
            && string.IsNullOrWhiteSpace(data.GetValueOrDefault("listingId")?.ToString()))
        {
            errors.Add("listingId is required for opened/order_created events.");
        }

        // Parse invariantly: client numbers are JSON (period decimal), so a
        // comma-decimal server locale must not reject a fractional score.
        if (data.TryGetValue("score", out var sObj)
            && (!double.TryParse(sObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s)
                || s < 0 || s > 1))
        {
            errors.Add("score must be a number in [0, 1].");
        }

        if (data.TryGetValue("rank", out var rObj)
            && (!int.TryParse(rObj?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var r)
                || r < 0))
        {
            errors.Add("rank must be a non-negative integer.");
        }

        if (data.TryGetValue("impressions", out var iObj))
        {
            var impressions = iObj?.ToString() ?? "";
            if (impressions.Length > MaxImpressionsLength)
            {
                errors.Add($"impressions must not exceed {MaxImpressionsLength} characters.");
            }
            else if (impressions.Length > 0 && !IsValidJson(impressions))
            {
                // Contract: impressions is a JSON string. Guard it so the
                // training corpus stays parseable.
                errors.Add("impressions must be a valid JSON string.");
            }
        }

        if (data.TryGetValue("matchCount", out var mcObj)
            && (!int.TryParse(mcObj?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var mc)
                || mc < 0))
        {
            errors.Add("matchCount must be a non-negative integer.");
        }

        if (data.TryGetValue("occurredAt", out var oObj)
            && !string.IsNullOrWhiteSpace(oObj?.ToString())
            && !DateTime.TryParse(oObj!.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
        {
            errors.Add("occurredAt must be an ISO 8601 date.");
        }

        return errors;
    }

    /// <summary>Whether <paramref name="value"/> parses as a JSON document.</summary>
    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Persist a fraud signal for admin review. Best-effort: signals feed the
    /// admin review queue, so a write failure must never block or fail the
    /// underlying sync operation. Skips silently when the user id is unknown
    /// (the table's partition key cannot be empty).
    /// </summary>
    private async Task TryRecordFraudSignalAsync(
        string userId, string signalType, string? entityId, string entityType,
        string? description, ILambdaContext context)
    {
        if (_fraudService == null || string.IsNullOrEmpty(userId))
        {
            return;
        }

        try
        {
            await _fraudService.RecordFraudSignalAsync(
                userId, signalType, entityId, entityType, description);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to record fraud signal {signalType} for user {userId}: {ex.Message}");
        }
    }

    /// <summary>Minimum resolved listings before a farmer gets a computed reliability.</summary>
    private const int MinListingsForReliability = 3;

    /// <summary>Candidate pages walked when computing farmer reliability.</summary>
    private const int ReliabilityMaxPages = 3;

    /// <summary>
    /// Farmer reliability = fraction of the farmer's prior listings that reached
    /// <c>sold</c>, over all resolved listings (sold or expired). Returns null
    /// when the farmer has fewer than <see cref="MinListingsForReliability"/>
    /// resolved listings (→ neutral default in scoring) or on any read failure
    /// (best-effort: reliability must never block crop creation).
    ///
    /// Escrow-free and cheap: one bounded query over the farmer's own crops at
    /// create time, no per-match reads and no order/escrow coupling.
    /// </summary>
    private async Task<double?> ComputeFarmerReliabilityAsync(
        string farmerId, ILambdaContext context)
    {
        try
        {
            var sold = 0;
            var resolved = 0;
            Dictionary<string, AttributeValue>? startKey = null;
            var pages = 0;

            do
            {
                var (items, lastKey) = await _cropRepo.QueryCropsAsync(
                    farmerId: farmerId, limit: 100, exclusiveStartKey: startKey);

                foreach (var item in items)
                {
                    var status = (item.TryGetValue("status", out var s) ? s?.ToString() : null)
                        ?? "";
                    if (string.Equals(status, "sold", StringComparison.OrdinalIgnoreCase))
                    {
                        sold++;
                        resolved++;
                    }
                    else if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
                    {
                        resolved++;
                    }
                }

                startKey = lastKey;
            }
            while (startKey != null && ++pages < ReliabilityMaxPages);

            if (resolved < MinListingsForReliability)
            {
                return null;
            }

            return Math.Clamp((double)sold / resolved, 0.0, 1.0);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Reliability compute skipped: [{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>
    /// Process a crop_listing sync operation (create, update, delete).
    /// Uses version-based conflict detection per sync-protocol.md.
    /// </summary>
    private async Task<SyncOperationResult> ProcessCropListingAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
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

                    // Role check: only farmers can create crop listings
                    if (!string.Equals(authenticatedRole, "farmer", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: Only farmers can create crop listings."
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

                    var cropContactError = await StampOwnerContactAsync(
                        operation.Data, authenticatedUserId!, "farmerName", "farmerPhone", context);
                    if (cropContactError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {cropContactError}"
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
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId!, fraudCheck.ErrorCode ?? "FRAUD_REJECTED",
                                operation.EntityId, "crop", fraudCheck.Message, context);

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
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId!, fraudCheck.FlagReason ?? "UNKNOWN",
                                operation.EntityId, "crop", fraudCheck.Message, context);
                        }
                    }

                    // FIX-010: Force initial status to "available" on create
                    operation.Data["status"] = "available";

                    // Denormalize the farmer's reliability (listing-conversion
                    // track record) onto the new listing so the matcher can
                    // fold it into ranking without a per-candidate read.
                    // Best-effort: a failure just leaves it unset (neutral).
                    var reliability = await ComputeFarmerReliabilityAsync(
                        authenticatedUserId!, context);
                    if (reliability is double rel)
                    {
                        operation.Data["farmerReliability"] = rel;
                    }

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

                    // Already exists: return conflict with server data, but only to the
                    // owner: a create that names another farmer's id must not read that
                    // farmer's listing (role-visibility rule).
                    var existing = await _cropRepo.GetByIdAsync(operation.EntityId);
                    var serverVersion = existing != null && existing.TryGetValue("version", out var v)
                        ? int.TryParse(v.ToString(), out var sv) ? sv : 1
                        : 1;
                    var isOwner = existing != null && IsParty(existing, authenticatedUserId, "farmerId");

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "conflict",
                        ServerVersion = serverVersion,
                        ServerData = isOwner ? existing : null,
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

                    // SIM-02: ownership/contact fields are immutable after create.
                    var cropIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableCropIdentityFields,
                        key => existingForUpdate.TryGetValue(key, out var stored) ? stored?.ToString() : null);
                    if (cropIdentityError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {cropIdentityError}"
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

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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
                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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
        string? authenticatedRole,
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

                    // Role check: only truckers can create trip listings
                    if (!string.Equals(authenticatedRole, "trucker", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: Only truckers can create trip listings."
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

                    var tripContactError = await StampOwnerContactAsync(
                        operation.Data, authenticatedUserId!, "truckerName", "truckerPhone", context);
                    if (tripContactError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {tripContactError}"
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
                        // The key is (tripId, createdAt), so the condition alone lets a
                        // replayed create add a second row (SIM-39).
                        if (await DynamoDbHelper.PartitionExistsAsync(
                                _dynamoDb, _tripsTable, "tripId", operation.EntityId))
                        {
                            throw new ConditionalCheckFailedException("Trip already exists.");
                        }

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
                        // Already exists: return conflict with server data, only to the
                        // owning trucker (role-visibility rule).
                        var existing = await GetTripItemAsync(operation.EntityId);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }
                        var isOwner = existing != null && IsParty(existing, authenticatedUserId, "truckerId");

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = isOwner ? CropRepository.FromItem(existing!) : null,
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

                    // SIM-03: ownership/contact fields are immutable after create.
                    var tripIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableTripIdentityFields,
                        key => existing.TryGetValue(key, out var stored) ? stored.S : null);
                    if (tripIdentityError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {tripIdentityError}"
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

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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
                        var result = ScrubSyncSecrets(CropRepository.FromItem(response.Attributes));
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
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
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
                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
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

        // truckerPhone: required on create, must be valid Ethiopian phone
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
            else
            {
                // F-SEC-02: Normalize then validate (accepts +251, 00251, 0 prefix)
                var normalized = Validation.RequestValidator.NormalizePhoneNumber(phoneObj.ToString()!);
                if (normalized == null)
                {
                    errors.Add("truckerPhone must be valid Ethiopian format (+251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX).");
                }
                else
                {
                    data["truckerPhone"] = normalized;
                }
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
    private static List<string> ValidateDeliveryFields(
        Dictionary<string, object> data, bool isCreate, bool isSelfPickup = false)
    {
        var errors = new List<string>();

        // Self-pickup deliveries have no trucker and the farmer's QR is generated later,
        // so truckerId/truckerName/qrToken are NOT required at create for them.
        var requireTruckerAndQr = isCreate && !isSelfPickup;

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
                if (requireTruckerAndQr)
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
                if (requireTruckerAndQr)
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

        // F12-SEC-04: Phone format validation — normalize then validate
        foreach (var phoneField in new[] { "farmerPhone", "truckerPhone", "buyerPhone" })
        {
            if (data.TryGetValue(phoneField, out var phoneObj)
                && phoneObj != null && !string.IsNullOrWhiteSpace(phoneObj.ToString()))
            {
                var normalized = Validation.RequestValidator.NormalizePhoneNumber(phoneObj.ToString()!);
                if (normalized == null)
                {
                    errors.Add($"{phoneField} must be valid Ethiopian format (+251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX).");
                }
                else
                {
                    data[phoneField] = normalized;
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

        // truckerId: required on create for trucker deliveries only (auth check done
        // separately). Self-pickup deliveries legitimately have no trucker.
        if (requireTruckerAndQr && !data.ContainsKey("truckerId"))
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

                    var createEchoError = StripDeliveryEchoFields(operation);
                    if (createEchoError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {createEchoError}"
                        };
                    }

                    // Self-pickup deliveries are created by the BUYER (there is no trucker);
                    // normal deliveries are created by the trucker. Gate authorization on the
                    // party that owns each kind.
                    var createDeliveryType = operation.Data.TryGetValue("deliveryType", out var cdtObj)
                        ? cdtObj?.ToString() : null;
                    var isSelfPickupCreate = string.Equals(
                        createDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal);

                    if (isSelfPickupCreate)
                    {
                        var spBuyerId = operation.Data.TryGetValue("buyerId", out var spB)
                            ? spB?.ToString() : null;
                        if (spBuyerId != authenticatedUserId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_UNAUTHORIZED: buyerId must match authenticated user for self-pickup."
                            };
                        }

                        var spTruckerId = operation.Data.TryGetValue("truckerId", out var spT)
                            ? spT?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(spTruckerId))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: A self-pickup delivery must not have a trucker."
                            };
                        }

                        // Synthesize the required non-empty tripId sort key from the deliveryId
                        // (self-pickup has no trip). GetByIdAsync queries the PK only, so this
                        // synthetic value is inert for retrieval and the TripIdIndex.
                        operation.Data["tripId"] = operation.EntityId;
                    }
                    else if (!operation.Data.TryGetValue("truckerId", out var truckerIdObj)
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

                    // The order backing this delivery, loaded once and reused for the
                    // escrow-linkage step after the write.
                    Dictionary<string, object>? createOrder = null;

                    // Self-pickup integrity: no self-trade, and the linked order must carry no
                    // transport cost / trucker — otherwise escrow would try to settle a trucker
                    // leg with no trucker, or block on a split that can't reconcile 2-way.
                    if (isSelfPickupCreate)
                    {
                        var spFarmerId = operation.Data.TryGetValue("farmerId", out var spF)
                            ? spF?.ToString() : null;
                        var spBuyerId2 = operation.Data.TryGetValue("buyerId", out var spB2)
                            ? spB2?.ToString() : null;
                        if (!string.IsNullOrEmpty(spFarmerId) && spFarmerId == spBuyerId2)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Buyer and farmer must be different users."
                            };
                        }

                        var spOrderId = operation.Data.TryGetValue("orderId", out var spO)
                            ? spO?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(spOrderId))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: A self-pickup delivery requires an orderId."
                            };
                        }

                        var spOrder = await _orderRepo.GetByIdAsync(spOrderId);
                        if (spOrder == null)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Referenced order not found."
                            };
                        }

                        if (spOrder.TryGetValue("buyerId", out var soB)
                            && soB?.ToString() != spBuyerId2)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: The order does not belong to this buyer."
                            };
                        }

                        // Bind the delivery's listing/farmer to the order's, so the buyer
                        // can't point the GPS geofence at an attacker-chosen listing while
                        // paying against a different order (anti-fraud: "physically at the farm").
                        var spListingId = operation.Data.TryGetValue("listingId", out var spL)
                            ? spL?.ToString() : null;
                        if (spOrder.TryGetValue("listingId", out var soL)
                            && soL?.ToString() != spListingId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: listingId does not match the order."
                            };
                        }
                        if (spOrder.TryGetValue("farmerId", out var soF)
                            && soF?.ToString() != spFarmerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match the order."
                            };
                        }

                        var soTransport = spOrder.TryGetValue("transportCost", out var soT)
                            && decimal.TryParse(soT?.ToString(), NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var soTv)
                            ? soTv : 0m;
                        var soTrucker = spOrder.TryGetValue("truckerId", out var soTr)
                            ? soTr?.ToString() : null;
                        if (soTransport > 0m || !string.IsNullOrWhiteSpace(soTrucker))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Self-pickup is not allowed for an order that already has transport or a trucker."
                            };
                        }

                        createOrder = spOrder;
                    }

                    // Every delivery must reference its order so the order-funded escrow
                    // can be linked to it (previously only self-pickup required orderId —
                    // an orderId-less trucker delivery would silently skip escrow linkage
                    // and the QR gate would fail open as "cash").
                    var createOrderId = operation.Data.TryGetValue("orderId", out var coObj)
                        ? coObj?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(createOrderId))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ORDER_REQUIRED: A delivery must reference its orderId."
                        };
                    }

                    createOrder ??= await _orderRepo.GetByIdAsync(createOrderId);
                    if (createOrder == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_VALIDATION: Referenced order not found."
                        };
                    }

                    // SIM-09: a delivery moves goods the farmer has agreed to sell, so the
                    // order must be accepted (or already in a pre-delivery logistics state).
                    // Earlier statuses carry no farmer consent (created, funded), and a dead
                    // order would re-point escrow money at a finished trade.
                    var createOrderStatus = createOrder.TryGetValue("status", out var cosObj)
                        ? cosObj?.ToString() : null;
                    if (createOrderStatus is not ("accepted" or "pickup_confirmed" or "in_transit"))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Order is {createOrderStatus ?? "unknown"}; "
                                + "no delivery can be created until the farmer has accepted it "
                                + "(accepted, pickup_confirmed or in_transit)."
                        };
                    }

                    // Participant binding for the trucker flow (self-pickup already binds
                    // buyer/farmer/listing to the order above).
                    if (!isSelfPickupCreate)
                    {
                        var dBuyerId = operation.Data.TryGetValue("buyerId", out var dB)
                            ? dB?.ToString() : null;
                        var dFarmerId = operation.Data.TryGetValue("farmerId", out var dF)
                            ? dF?.ToString() : null;
                        if (createOrder.TryGetValue("buyerId", out var coB)
                            && coB?.ToString() != dBuyerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: buyerId does not match the order."
                            };
                        }

                        if (createOrder.TryGetValue("farmerId", out var coF)
                            && coF?.ToString() != dFarmerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match the order."
                            };
                        }

                        // When the order was matched to a trucker, only THAT trucker
                        // may create its delivery — otherwise any authenticated
                        // trucker who learns an orderId could attach a bogus delivery
                        // and hijack the escrow linkage.
                        var coTrucker = createOrder.TryGetValue("truckerId", out var coT)
                            ? coT?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(coTrucker))
                        {
                            // SIM-09: an order without a trucker can only be fulfilled by the
                            // buyer as a self-pickup; any trucker could otherwise attach itself.
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: The order has no trucker; only the buyer can create a self-pickup delivery for it."
                            };
                        }

                        if (coTrucker != authenticatedUserId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: Only the order's matched trucker can create this delivery."
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

                    // Validate required fields
                    var validationErrors = ValidateDeliveryFields(
                        operation.Data, isCreate: true, isSelfPickup: isSelfPickupCreate);
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

                    // Copy only whitelisted data fields. qrNonce is never taken at create:
                    // only the farmer may author it, later (SIM-41).
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt" or "qrNonce")
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
                                // Guard property presence/type: GetProperty throws on a present-but-
                                // malformed GPS object (missing keys), which the outer catch would mask
                                // as a generic 500 and burn a sync retry. Fail with a clean validation error.
                                if (!je.TryGetProperty("latitude", out var latEl) || latEl.ValueKind != System.Text.Json.JsonValueKind.Number
                                    || !je.TryGetProperty("longitude", out var lngEl) || lngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: {kvp.Key} requires numeric latitude and longitude."
                                    };
                                }
                                var lat = latEl.GetDouble();
                                var lng = lngEl.GetDouble();

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

                    // Participant index key: the buyer is the party that must receive
                    // status updates (pull-based), so ParticipantIndex is keyed on buyerId.
                    if (item.TryGetValue("buyerId", out var buyerIdForIndex)
                        && !string.IsNullOrEmpty(buyerIdForIndex.S))
                    {
                        item["participantId"] = new AttributeValue { S = buyerIdForIndex.S };
                    }

                    // QR nonce is NOT set at create. The delivery generator (farmer)
                    // persists a nonce via an update after generating the QR the trucker
                    // scans; that farmer-authored nonce is authoritative and is what the
                    // scanner must echo back. Until it is persisted, qrNonce is empty and
                    // the pickup/delivery nonce check is skipped (offline-first).

                    // The key is (deliveryId, tripId), so a replayed create with another
                    // tripId would pass the put's condition and add a second row (SIM-39).
                    // Checked before the slot claim so a reused deliveryId never claims
                    // (and strands) another order's slot.
                    var deliveryExists = await DynamoDbHelper.PartitionExistsAsync(
                        _dynamoDb, _deliveriesTable, "deliveryId", operation.EntityId);

                    // SIM-09: at most one active delivery per order. The claim is an atomic
                    // conditional write on the order row (deliveries have no order index); a
                    // retry of the same deliveryId re-claims its own slot, and a cancelled
                    // delivery releases it.
                    if (!deliveryExists
                        && !await _orderRepo.TryClaimDeliverySlotAsync(createOrderId!, operation.EntityId))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_VALIDATION: The order already has an active delivery."
                        };
                    }

                    var request = new PutItemRequest
                    {
                        TableName = _deliveriesTable,
                        Item = item,
                        ConditionExpression = "attribute_not_exists(deliveryId)"
                    };

                    try
                    {
                        if (deliveryExists)
                        {
                            throw new ConditionalCheckFailedException("Delivery already exists.");
                        }

                        await _dynamoDb.PutItemAsync(request);

                        var deliveryTxId = item.TryGetValue("orderId", out var dCreateOid)
                            && !string.IsNullOrEmpty(dCreateOid.S)
                            ? dCreateOid.S : operation.EntityId;
                        await EmitTxAuditAsync(context, deliveryTxId, "delivery", operation.EntityId,
                            "delivery_created", null, "pending");

                        // Attach the order's escrow to this delivery so confirm-stamping,
                        // auto-release, and the self-pickup QR gate all see the money.
                        await TryLinkOrderEscrowAsync(
                            createOrderId!, operation.EntityId, createOrder, context);

                        var result = ScrubSyncSecrets(CropRepository.FromItem(item));
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
                        // Echo the existing delivery only to one of its parties.
                        var existing = await GetDeliveryItemAsync(operation.EntityId);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }
                        var isParty = existing != null
                            && IsParty(existing, authenticatedUserId, "buyerId", "farmerId", "truckerId");

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = isParty ? ScrubSyncSecrets(CropRepository.FromItem(existing!)) : null,
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

                    var updateEchoError = StripDeliveryEchoFields(operation);
                    if (updateEchoError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {updateEchoError}"
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

                    // SIM-41: the pickup nonce proves the farmer handed over the goods, so
                    // only the farmer may author it. A trucker-sent nonce let the trucker
                    // confirm pickup with a QR code he made himself.
                    var deliveryFarmerId = existing.TryGetValue("farmerId", out var nonceFarmerAttr)
                        ? nonceFarmerAttr.S : null;
                    if (authenticatedUserId != deliveryFarmerId)
                    {
                        operation.Data.Remove("qrNonce");
                    }

                    // Role-based authorization for status transitions
                    if (operation.Data.TryGetValue("status", out var newStatusObj))
                    {
                        var newStatus = newStatusObj?.ToString();

                        var existingDeliveryType = existing.TryGetValue("deliveryType", out var edt)
                            ? edt.S : null;
                        var isSelfPickupDelivery = string.Equals(
                            existingDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal);

                        // Self-pickup: the buyer collects at the farm, so a single scan takes
                        // pending → delivered directly (there is no trucker pickup/transit leg).
                        // Permitted ONLY for self-pickup; a normal delivery must still go through
                        // the trucker's pickedUp step. The "delivered" auth below keeps it buyer-only.
                        var selfPickupDirectDeliver = isSelfPickupDelivery
                            && currentStatus == "pending" && newStatus == "delivered";

                        // Validate status transition
                        if (!selfPickupDirectDeliver
                            && (!DeliveryTransitions.TryGetValue(currentStatus, out var allowed)
                                || newStatus == null || !allowed.Contains(newStatus)))
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

                                // A dispute freezes the order's money: above the step-up
                                // threshold it needs a fresh password (AUTH_040), like the
                                // escrow dispute endpoint.
                                var disputeStepUp = await CheckDeliveryDisputeStepUpAsync(existing);
                                if (disputeStepUp != null)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = disputeStepUp
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
                        // Non-status updates: the trucker owns generic updates.
                        // Additionally the FARMER — who generates the pickup QR —
                        // may persist the qrNonce so the scanner's nonce can be
                        // verified against it. (The farmer cannot drive any status
                        // transition, so this grants no extra transition power.)
                        var existingTruckerId = existing.TryGetValue("truckerId", out var tid2) ? tid2.S : null;
                        var existingFarmerIdForNonce = existing.TryGetValue("farmerId", out var fid2) ? fid2.S : null;
                        var isFarmerNoncePersist = operation.Data.ContainsKey("qrNonce")
                            && authenticatedUserId == existingFarmerIdForNonce;
                        if (authenticatedUserId != existingTruckerId && !isFarmerNoncePersist)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_UNAUTHORIZED: You can only update your own deliveries."
                            };
                        }

                        // Route the farmer-authored pickup nonce to the SEPARATE secret
                        // store (never onto the delivery item, so it can't leak via any
                        // sync-down/GSI/conflict-echo path). When no table is provisioned
                        // (_qrSecretRepo == null) we leave the legacy on-record qrNonce so
                        // in-flight deliveries stay verifiable.
                        if (isFarmerNoncePersist && _qrSecretRepo != null)
                        {
                            var nonceValue = operation.Data.TryGetValue("qrNonce", out var qnPersist)
                                ? qnPersist?.ToString()
                                : null;
                            if (!string.IsNullOrEmpty(nonceValue))
                            {
                                await _qrSecretRepo.PutNonceAsync(operation.EntityId, nonceValue);
                            }

                            operation.Data.Remove("qrNonce");

                            // Nonce only: nothing on the delivery item changes, so skip the
                            // write and keep the version. Bumping it made the scanner's
                            // queued confirm (sent at the version it last pulled) conflict,
                            // and server-wins silently undid the pickup or collection.
                            if (operation.Data.Keys.All(k => k is "version" or "updatedAt"))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "success",
                                    ServerVersion = existing.TryGetValue("version", out var nonceVer)
                                        && int.TryParse(nonceVer.N, out var nonceVerN) ? nonceVerN : 0,
                                    ServerData = ScrubSyncSecrets(CropRepository.FromItem(existing))
                                };
                            }
                        }
                    }

                    // QR nonce cross-validation for pickedUp/delivered transitions.
                    // The nonce lives in a SEPARATE secret store (never on the delivery item);
                    // in-flight deliveries created before that store fall back to the legacy
                    // on-record qrNonce. When set after a successful CAS, this holds the status
                    // whose single-use marker should be recorded (consume-after-commit).
                    string? nonceToConsumeStatus = null;
                    if (operation.Data.TryGetValue("status", out var statusForNonce))
                    {
                        var nStatus = statusForNonce?.ToString();
                        if (nStatus is "pickedUp" or "delivered")
                        {
                            var secret = _qrSecretRepo != null
                                ? await _qrSecretRepo.GetAsync(operation.EntityId)
                                : null;
                            // The on-record nonce is trusted only without a secret store: rows
                            // written before SIM-41 may carry a trucker-authored value.
                            var legacyNonce = _qrSecretRepo == null
                                && existing.TryGetValue("qrNonce", out var qnAttr)
                                ? qnAttr.S : null;
                            var effectiveNonce = secret?.Nonce ?? legacyNonce;

                            if (!string.IsNullOrEmpty(effectiveNonce))
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

                                if (!NonceEquals(scannedNonce, effectiveNonce))
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_NONCE_MISMATCH: Scanned QR nonce does not match generator's nonce."
                                    };
                                }

                                // Secret-store path adds freshness + single-use replay protection
                                // (legacy on-record nonces lack issuance/consumption metadata).
                                if (secret != null)
                                {
                                    if (!string.IsNullOrEmpty(secret.IssuedAt)
                                        && DateTime.TryParse(secret.IssuedAt, CultureInfo.InvariantCulture,
                                            DateTimeStyles.RoundtripKind, out var issuedAt)
                                        && DateTime.UtcNow - issuedAt.ToUniversalTime()
                                            > TimeSpan.FromDays(QrNonceFreshnessDays))
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_NONCE_EXPIRED: QR code has expired. Ask the farmer to show a fresh code."
                                        };
                                    }

                                    var consumedOp = secret.ConsumedOpFor(nStatus);
                                    if (consumedOp != null && consumedOp != operation.OperationId)
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_NONCE_REPLAY: This QR code was already used for this step."
                                        };
                                    }

                                    // Consume only AFTER the version CAS succeeds (below), so a
                                    // rejected transition never burns the nonce. A same-op retry
                                    // (consumedOp == this op) is idempotent — do not re-consume.
                                    if (consumedOp == null)
                                    {
                                        nonceToConsumeStatus = nStatus;
                                    }
                                }
                            }
                            else
                            {
                                // No nonce on either source. Money-bearing deliveries must NOT
                                // fail open: DEFER (offline-first — the op stays queued and retries)
                                // until the farmer's nonce syncs, then hard-fail once it's clearly
                                // never coming. Cash (no-escrow) deliveries keep the legacy
                                // allow-through so a missing QR doesn't block them.
                                var hasEscrow = _escrowRepo != null
                                    && await _escrowRepo.GetByDeliveryIdAsync(operation.EntityId) != null;
                                if (hasEscrow)
                                {
                                    var createdAtStr = existing.TryGetValue("createdAt", out var caAttr)
                                        ? caAttr.S : null;
                                    var tooOld = createdAtStr != null
                                        && DateTime.TryParse(createdAtStr, CultureInfo.InvariantCulture,
                                            DateTimeStyles.RoundtripKind, out var deliveryCreatedAt)
                                        && DateTime.UtcNow - deliveryCreatedAt.ToUniversalTime()
                                            > TimeSpan.FromDays(QrNonceMaxDeferDays);
                                    if (tooOld)
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_NONCE_MISSING: QR verification could not be completed for this payment. Contact support."
                                        };
                                    }

                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "deferred",
                                        Message = "DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically."
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

                    // SIM-09: the parties, order, listing, trip and type of a delivery are fixed
                    // at create; an update may only echo them unchanged.
                    var deliveryIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableDeliveryIdentityFields,
                        key => existing.TryGetValue(key, out var storedAttr) ? storedAttr.S : null);
                    if (deliveryIdentityError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {deliveryIdentityError}"
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
                        // Guard presence/type — a present-but-malformed gpsPickup would
                        // otherwise throw KeyNotFoundException → masked as a generic 500.
                        if (!pickupJe.TryGetProperty("latitude", out var pickupLatEl) || pickupLatEl.ValueKind != System.Text.Json.JsonValueKind.Number
                            || !pickupJe.TryGetProperty("longitude", out var pickupLngEl) || pickupLngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: gpsPickup requires numeric latitude and longitude."
                            };
                        }
                        var pickupLat = pickupLatEl.GetDouble();
                        var pickupLng = pickupLngEl.GetDouble();

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

                    // Self-pickup confirmation releases money, so GPS at the farm is MANDATORY
                    // (a normal trucker delivery's GPS is best-effort). Require gpsDropoff up front
                    // for self-pickup so the check below cannot be skipped by simply omitting it.
                    if (resolvedNewStatus == "delivered"
                        && string.Equals(
                            existing.TryGetValue("deliveryType", out var spDt) ? spDt.S : null,
                            SelfPickupDeliveryType, StringComparison.Ordinal)
                        && !(operation.Data.TryGetValue("gpsDropoff", out var spGpsObj)
                            && spGpsObj is System.Text.Json.JsonElement spGpsJe
                            && spGpsJe.ValueKind == System.Text.Json.JsonValueKind.Object))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_GPS_PROXIMITY: Location is required to confirm self-pickup at the farm."
                        };
                    }

                    if (resolvedNewStatus == "delivered"
                        && operation.Data.TryGetValue("gpsDropoff", out var gpsDropoffObj)
                        && gpsDropoffObj is System.Text.Json.JsonElement dropoffJe
                        && dropoffJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // Guard presence/type — a present-but-malformed gpsDropoff would
                        // otherwise throw KeyNotFoundException → masked as a generic 500.
                        if (!dropoffJe.TryGetProperty("latitude", out var dropoffLatEl) || dropoffLatEl.ValueKind != System.Text.Json.JsonValueKind.Number
                            || !dropoffJe.TryGetProperty("longitude", out var dropoffLngEl) || dropoffLngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: gpsDropoff requires numeric latitude and longitude."
                            };
                        }
                        var dropoffLat = dropoffLatEl.GetDouble();
                        var dropoffLng = dropoffLngEl.GetDouble();

                        var deliveredDeliveryType = existing.TryGetValue("deliveryType", out var ddt)
                            ? ddt.S : null;
                        if (string.Equals(deliveredDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal))
                        {
                            // Self-pickup: the handoff happens AT THE FARM, so validate the buyer's
                            // GPS against the LISTING location (there is no trip destination). Listing
                            // GPS is MANDATORY — otherwise this money-releasing confirmation could be
                            // made from anywhere.
                            var listingId = existing.TryGetValue("listingId", out var lidD) ? lidD.S : null;
                            var listing = listingId != null ? await _cropRepo.GetByIdAsync(listingId) : null;
                            if (listing == null
                                || !listing.TryGetValue("latitude", out var slLatObj)
                                || !listing.TryGetValue("longitude", out var slLonObj)
                                || !double.TryParse(slLatObj?.ToString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var listingLat)
                                || !double.TryParse(slLonObj?.ToString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var listingLon))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_GPS_PROXIMITY: This listing has no registered location, so self-pickup cannot be verified."
                                };
                            }

                            var spDistance = GpsValidator.HaversineDistance(
                                dropoffLat, dropoffLng, listingLat, listingLon);
                            if (spDistance > 5.0)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_GPS_PROXIMITY: You are {spDistance:F1}km from the farm location (max 5km)."
                                };
                            }
                        }
                        else
                        {
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
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId ?? "", gpsCheck.FlagReason ?? "GPS_ANOMALY",
                                operation.EntityId, "delivery", gpsCheck.Message, context);
                        }
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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
                                // Guard property presence/type: GetProperty throws on a present-but-
                                // malformed GPS object (missing keys), which the outer catch would mask
                                // as a generic 500 and burn a sync retry. Fail with a clean validation error.
                                if (!je.TryGetProperty("latitude", out var latEl) || latEl.ValueKind != System.Text.Json.JsonValueKind.Number
                                    || !je.TryGetProperty("longitude", out var lngEl) || lngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: {kvp.Key} requires numeric latitude and longitude."
                                    };
                                }
                                var lat = latEl.GetDouble();
                                var lng = lngEl.GetDouble();

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
                        var result = ScrubSyncSecrets(CropRepository.FromItem(response.Attributes));

                        // Audit the delivery transition (who/when; GPS is joined at
                        // read time from the delivery record) for the monitor.
                        if (operation.Data != null
                            && operation.Data.TryGetValue("status", out var newDelivStatusObj))
                        {
                            var toStatus = newDelivStatusObj?.ToString() ?? "";
                            var fromStatus = existing.TryGetValue("status", out var exStatus)
                                ? exStatus.S : null;
                            var deliveryTxId = existing.TryGetValue("orderId", out var exOid)
                                && !string.IsNullOrEmpty(exOid.S)
                                ? exOid.S : operation.EntityId;
                            await EmitTxAuditAsync(context, deliveryTxId, "delivery", operation.EntityId,
                                DeliveryActionFor(toStatus), fromStatus, toStatus);
                        }

                        // SIM-09: a cancelled delivery frees the order's single delivery slot
                        // so a replacement can be created.
                        if (operation.Data != null
                            && operation.Data.TryGetValue("status", out var cancelStatusObj)
                            && cancelStatusObj?.ToString() == "cancelled"
                            && existing.TryGetValue("orderId", out var cancelOid)
                            && !string.IsNullOrEmpty(cancelOid.S))
                        {
                            await _orderRepo.ReleaseDeliverySlotAsync(cancelOid.S, operation.EntityId);
                        }

                        // SIM-11: the order follows its delivery (pickedUp -> pickup_confirmed,
                        // inTransit -> in_transit, delivered -> delivered; a self-pickup jumps
                        // from accepted straight to delivered). Best-effort, system actor.
                        if (operation.Data != null
                            && operation.Data.TryGetValue("status", out var followStatusObj)
                            && followStatusObj?.ToString() is string followStatus)
                        {
                            if (existing.TryGetValue("orderId", out var followOid) && !string.IsNullOrEmpty(followOid.S))
                            {
                                await TryAdvanceOrderForDeliveryAsync(followOid.S, followStatus, context);
                            }

                            // SIM-12: a disputed handoff must freeze the escrowed money too, or the
                            // 48-hour auto-release would still pay the farmer.
                            if (followStatus == "disputed")
                            {
                                var disputeText = operation.Data.TryGetValue("disputeReason", out var disputeTextObj)
                                    ? disputeTextObj?.ToString() : null;
                                await TryFreezeEscrowForDisputedDeliveryAsync(
                                    operation.EntityId, authenticatedUserId, disputeText, context);
                            }
                        }

                        // Bridge delivery confirmation -> escrow release. When the buyer
                        // confirms delivery, stamp the linked escrow's deliveryConfirmedAt +
                        // disputeWindowEndsAt so the auto-release cron settles to the farmer
                        // (and the auto-refund cron skips it). Without this, a delivered order
                        // was never paid out and the buyer was refunded after 7 days.
                        // Non-fatal: a confirmed delivery must still succeed even if the
                        // escrow can't be stamped here (reconciliation cron is the backstop).
                        // Single-use: burn the nonce for this transition now that the CAS
                        // committed. Ordered after the write so a failed CAS never consumes it.
                        if (nonceToConsumeStatus != null && _qrSecretRepo != null)
                        {
                            await _qrSecretRepo.MarkConsumedAsync(
                                operation.EntityId, nonceToConsumeStatus, operation.OperationId);
                        }

                        if (_escrowRepo != null
                            && operation.Data != null
                            && operation.Data.TryGetValue("status", out var confirmedStatusObj)
                            && confirmedStatusObj?.ToString() == "delivered")
                        {
                            await StampEscrowDeliveryConfirmedAsync(operation.EntityId, now, context);
                        }

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
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
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
                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

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

                        // SIM-09: soft delete is a cancellation; free the order's delivery slot.
                        if (existing.TryGetValue("orderId", out var deleteOid) && !string.IsNullOrEmpty(deleteOid.S))
                        {
                            await _orderRepo.ReleaseDeliverySlotAsync(deleteOid.S, operation.EntityId);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
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

    /// <summary>
    /// Stamp the escrow linked to a confirmed delivery with deliveryConfirmedAt +
    /// disputeWindowEndsAt (now + 48h) so the auto-release cron settles to the
    /// farmer and the auto-refund cron skips it. Idempotent: only stamps an
    /// "escrowed" escrow that has not already been confirmed. Best-effort — errors
    /// are logged, never thrown, so a confirmed delivery is never rolled back.
    /// </summary>
    private async Task StampEscrowDeliveryConfirmedAsync(
        string deliveryId, string confirmedAt, ILambdaContext context)
    {
        if (_escrowRepo == null)
        {
            return;
        }

        try
        {
            var escrow = await _escrowRepo.GetByDeliveryIdAsync(deliveryId);
            if (escrow == null)
            {
                return; // no escrow linked to this delivery (e.g. cash order)
            }

            var status = escrow.TryGetValue("status", out var st) ? st?.ToString() : null;
            var alreadyConfirmed = escrow.TryGetValue("deliveryConfirmedAt", out var dc)
                && !string.IsNullOrEmpty(dc?.ToString());
            if (status != "escrowed" || alreadyConfirmed)
            {
                return; // not settleable, or already stamped (idempotent replay)
            }

            var escrowId = escrow.TryGetValue("escrowId", out var eid) ? eid?.ToString() ?? "" : "";
            var createdAt = escrow.TryGetValue("createdAt", out var ca) ? ca?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(escrowId) || string.IsNullOrEmpty(createdAt))
            {
                return;
            }

            // Base the dispute window on the same confirmation timestamp so the two
            // fields are consistent (not skewed by a fresh UtcNow read).
            var windowBase = DateTime.TryParse(confirmedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var confirmedDt)
                ? confirmedDt
                : DateTime.UtcNow;
            var disputeWindowEndsAt = windowBase.AddHours(48).ToString("o");

            await _escrowRepo.UpdateStatusAsync(
                escrowId,
                createdAt,
                newStatus: "escrowed",
                expectedCurrentStatus: "escrowed",
                additionalFields: new Dictionary<string, object>
                {
                    { "deliveryConfirmedAt", confirmedAt },
                    { "disputeWindowEndsAt", disputeWindowEndsAt }
                });

            context.Logger.LogInformation(
                $"Escrow {escrowId} stamped for delivery {deliveryId}; auto-release scheduled after 48h dispute window.");
        }
        catch (Exception ex)
        {
            // A confirmed delivery must not be rolled back. Log loudly: until the
            // escrow is stamped, the 7-day auto-refund is NOT blocked, so this needs
            // an ops alert / reconciliation follow-up.
            context.Logger.LogError(
                $"Delivery {deliveryId} confirmed but escrow stamp FAILED: [{ex.GetType().Name}]. "
                + "Escrow not yet marked delivery-confirmed — requires reconciliation.");
        }
    }

    // ─── Order Processing ─────────────────────────────────────────────────

    /// <summary>
    /// Process an order sync operation (create, update).
    /// Uses server-authoritative state machine and commission calculation.
    /// </summary>
    private async Task<SyncOperationResult> ProcessOrderAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
                return await ProcessOrderCreateAsync(operation, authenticatedUserId, authenticatedRole, context);
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

    /// <summary>
    /// Best-effort emit to the unified transaction-audit trail using the current
    /// batch's who/where context (or a system override for follow-on transitions).
    /// No-op when audit is unconfigured or no batch context is set.
    /// </summary>
    private Task EmitTxAuditAsync(
        ILambdaContext context,
        string transactionId,
        string entityType,
        string entityId,
        string action,
        string? fromState,
        string? toState,
        AuditContext? ctxOverride = null,
        string? metadata = null)
    {
        var ctx = ctxOverride ?? _batchAudit;
        if (ctx == null)
        {
            return Task.CompletedTask;
        }
        return TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, context, transactionId, entityType, entityId,
            action, fromState, toState, ctx, metadata);
    }

    /// <summary>
    /// Map an order status to a timeline action key. Reuses the vocabulary the
    /// admin app already localises (payment_verified / farmer_responded /
    /// order_cancelled / order_created); everything else is a generic
    /// order_updated with the from→to states carrying the detail.
    /// </summary>
    private static string OrderActionFor(string toStatus) => toStatus switch
    {
        "funded" => "payment_verified",
        "accepted" or "rejected" => "farmer_responded",
        "cancelled" => "order_cancelled",
        _ => "order_updated",
    };

    /// <summary>Delivery status → timeline action key (reuses picked_up / delivered).</summary>
    private static string DeliveryActionFor(string toStatus) => toStatus switch
    {
        "pickedUp" => "picked_up",
        "delivered" => "delivered",
        _ => "delivery_updated",
    };

    private async Task<SyncOperationResult> ProcessOrderCreateAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
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

        // Role check: only buyers can create orders
        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: Only buyers can create orders."
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

        // SIM-05: the client's copy of the listing and trip is not trusted. Load both,
        // bind every party, price and quantity to the server's records, and compute the
        // money fields from those records only.
        var listingId = operation.Data["listingId"]?.ToString() ?? "";
        var listing = await _cropRepo.GetByIdAsync(listingId);
        if (listing == null)
        {
            return OrderValidationError(operation, "Listing not found.");
        }

        var listingError = BindOrderToListing(operation.Data, listing, authenticatedUserId);
        if (listingError != null)
        {
            return OrderValidationError(operation, listingError);
        }

        var quantityKg = ParseDouble(operation.Data, "quantity");
        var pricePerUnit = ParseDouble(operation.Data, "pricePerUnit");
        var cropCost = quantityKg * pricePerUnit;
        var commissionRate = GetCommissionRate(context);
        var commissionAmount = cropCost * commissionRate;

        var tripId = operation.Data.TryGetValue("tripId", out var tripIdObj) ? tripIdObj?.ToString() : null;
        var truckerId = operation.Data.TryGetValue("truckerId", out var truckerIdObj) ? truckerIdObj?.ToString() : null;
        if (string.IsNullOrEmpty(tripId) != string.IsNullOrEmpty(truckerId))
        {
            return OrderValidationError(operation, "tripId and truckerId must be provided together.");
        }

        var clientTransportCost = ParseDoubleOrNull(operation.Data, "transportCost");
        var transportCost = 0.0;
        if (!string.IsNullOrEmpty(tripId))
        {
            var trip = await GetTripItemAsync(tripId);
            if (trip == null)
            {
                return OrderValidationError(operation, "Trip not found.");
            }

            var tripError = BindOrderToTrip(
                operation.Data, trip, truckerId!, quantityKg, clientTransportCost, out transportCost);
            if (tripError != null)
            {
                return OrderValidationError(operation, tripError);
            }
        }
        else if (clientTransportCost is double sentWithoutTrip && sentWithoutTrip != 0.0)
        {
            return OrderValidationError(operation, "transportCost requires a tripId.");
        }

        operation.Data["transportCost"] = transportCost;
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
            await EmitTxAuditAsync(context, operation.EntityId, "order", operation.EntityId,
                "order_created", null, "created");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            };
        }

        // Already exists: return conflict; the order (phones, amounts) is echoed only
        // to one of its parties.
        var existing = await _orderRepo.GetByIdAsync(operation.EntityId);
        var serverVersion = existing != null && existing.TryGetValue("version", out var v)
            ? int.TryParse(v.ToString(), out var sv) ? sv : 1
            : 1;
        var isParty = existing != null
            && IsParty(existing, authenticatedUserId, "buyerId", "farmerId", "truckerId");

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = serverVersion,
            ServerData = isParty ? existing : null,
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

        // Authorization: the buyer or farmer of this order can update it; the order's
        // matched trucker may only drive the logistics statuses (SIM-11; the contract
        // says "either participant" for pickup_confirmed / in_transit / delivered).
        var orderBuyerId = existingOrder.TryGetValue("buyerId", out var bid) ? bid?.ToString() : null;
        var orderFarmerId = existingOrder.TryGetValue("farmerId", out var fid) ? fid?.ToString() : null;
        var orderTruckerId = existingOrder.TryGetValue("truckerId", out var otid) ? otid?.ToString() : null;
        var isBuyerOrFarmer = authenticatedUserId == orderBuyerId || authenticatedUserId == orderFarmerId;
        var isTruckerOnly = !isBuyerOrFarmer
            && !string.IsNullOrEmpty(orderTruckerId)
            && authenticatedUserId == orderTruckerId;

        if (!isBuyerOrFarmer && !isTruckerOnly)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: You can only update orders you are a participant in."
            };
        }

        if (isTruckerOnly)
        {
            var truckerStatus = operation.Data.TryGetValue("status", out var tsObj) ? tsObj?.ToString() : null;
            if (truckerStatus is not ("pickup_confirmed" or "in_transit" or "delivered"))
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = "ORDER_UNAUTHORIZED: The trucker may only set pickup_confirmed, in_transit or delivered."
                };
            }
        }

        // Strip server-controlled fields
        foreach (var field in OrderServerOnlyFields)
        {
            operation.Data.Remove(field);
        }

        // SIM-06: an update may only carry status and its companions. The echoed orderId
        // must be this order; updatedAt is server-stamped; unknown keys are rejected; every
        // identity or pricing field is immutable (an unchanged echo is dropped, a changed
        // value is rejected) so the stored money fields always match their inputs.
        if (operation.Data.TryGetValue("orderId", out var echoedOrderId))
        {
            if (!string.Equals(echoedOrderId?.ToString(), operation.EntityId, StringComparison.Ordinal))
            {
                return OrderValidationError(operation, "orderId does not match the operation entityId.");
            }

            operation.Data.Remove("orderId");
        }

        operation.Data.Remove("updatedAt");

        var unknownUpdateFields = operation.Data.Keys
            .Where(k => !AllowedOrderFields.Contains(k) && !OrderUpdateEchoFields.Contains(k))
            .ToList();
        if (unknownUpdateFields.Count > 0)
        {
            return OrderValidationError(operation, $"Unknown fields: {string.Join(", ", unknownUpdateFields)}");
        }

        var immutableOrderFields = AllowedOrderFields.Where(f => !UpdatableOrderFields.Contains(f)).ToHashSet();
        var immutableError = RejectImmutableFieldChanges(
            operation.Data,
            immutableOrderFields,
            key => existingOrder.TryGetValue(key, out var stored) ? stored?.ToString() : null);
        if (immutableError != null)
        {
            return OrderValidationError(operation, immutableError);
        }

        var updateValidationErrors = ValidateOrderFields(operation.Data, isCreate: false);
        if (updateValidationErrors.Count > 0)
        {
            return OrderValidationError(operation, string.Join(" ", updateValidationErrors));
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

            // KYC and step-up gates on the buyer's payment. The order-funded escrow leg is
            // the real money path, so the checks /escrow/initiate makes (both parties
            // phone-verified, Fayda above 10,000 ETB) and the payment step-up (a fresh
            // password up to 10,000 ETB, AUTH_040; a fresh Fayda login above, AUTH_041)
            // run here, before any money moves.
            if (newStatus == "payment_submitted")
            {
                var orderTotal = ReadDecimal(existingOrder, "orderTotal");
                var (kycError, buyer) = await CheckPaymentKycAsync(authenticatedUserId!, orderFarmerId, orderTotal, context);
                if (kycError != null)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = kycError
                    };
                }

                var stepUpError = StepUpPolicy.CheckPayment(_requestAuthTime, buyer?.FaydaAuthenticatedAt, orderTotal);
                if (stepUpError != null)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = stepUpError
                    };
                }

                // ESC-01: with the live gateway an order is paid by one Telebirr checkout, which
                // takes 100 to 500,000 ETB. Refuse up front rather than leave the order waiting
                // for a payment that can never be made (bank transfers are ESC-46).
                var roundedTotal = Math.Round(orderTotal, 2, MidpointRounding.AwayFromZero);
                if (!IsMockPaymentsEnabled()
                    && (roundedTotal < LiveMinPaymentEtb || roundedTotal > LiveMaxPaymentEtb))
                {
                    return OrderValidationError(operation,
                        $"This order total ({roundedTotal} ETB) is outside what one Telebirr payment can take "
                        + $"({LiveMinPaymentEtb} to {LiveMaxPaymentEtb} ETB).");
                }
            }

            // SIM-11: the order becomes "delivered" only once its delivery is confirmed.
            // The confirmation itself advances the order server-side, so a client request
            // is honoured only when the active delivery already says so. Orders with no
            // active delivery on record (created before 1.4.0) keep the old behaviour.
            if (newStatus == "delivered"
                && existingOrder.TryGetValue("activeDeliveryId", out var adObj)
                && adObj?.ToString() is string activeDeliveryId
                && !string.IsNullOrEmpty(activeDeliveryId))
            {
                var activeDelivery = await GetDeliveryItemAsync(activeDeliveryId);
                var activeDeliveryStatus = activeDelivery != null
                    && activeDelivery.TryGetValue("status", out var adsAttr) ? adsAttr.S : null;
                if (activeDeliveryStatus != "delivered")
                {
                    return OrderValidationError(operation,
                        $"The delivery has not been confirmed yet (delivery status: {activeDeliveryStatus ?? "unknown"}); "
                        + "confirm the delivery instead of marking the order delivered.");
                }
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
                case "payment_submitted":
                    // ESC-01: starts the payment clock. The checkout refuses new attempts
                    // after 20 hours and the order cron marks the order payment_failed after 24.
                    operation.Data["paymentSubmittedAt"] = now;
                    break;
            }
        }

        // SIM-08: an order update is never applied without the client's version. The old
        // fallback to the stored version skipped optimistic concurrency entirely, so a
        // stale client could overwrite a newer state (even a cancelled order).
        if (!TryGetClientVersion(operation, out var version))
        {
            return OrderValidationError(operation,
                "version is required for order updates (send clientVersion or data.version).");
        }

        operation.Data.Remove("version"); // consumed here; the repository handles the check

        var updateResult = await _orderRepo.UpdateAsync(operation.EntityId, operation.Data, version);

        if (updateResult.IsSuccess)
        {
            var appliedStatus = operation.Data.TryGetValue("status", out var statusVal)
                ? statusVal?.ToString()
                : null;

            // Audit the order transition (who/when/where) for the monitor.
            if (!string.IsNullOrEmpty(appliedStatus))
            {
                var priorStatus = existingOrder.TryGetValue("status", out var psObj)
                    ? psObj?.ToString()
                    : null;
                await EmitTxAuditAsync(context, operation.EntityId, "order", operation.EntityId,
                    OrderActionFor(appliedStatus), priorStatus, appliedStatus);

                // A funded order that gets rejected/cancelled holds the buyer's money
                // in escrow — schedule the refund (expiresAt=now) so the hourly
                // auto-refund cron pays it back through the standard gateway path
                // (order-lifecycle.md invariant 2). Only the funded→terminal hop
                // carries unreleased money; later statuses are governed by the
                // delivery-confirm/dispute machinery.
                if (priorStatus == "funded" && appliedStatus is "rejected" or "cancelled")
                {
                    await TryScheduleOrderEscrowRefundAsync(
                        operation.EntityId, $"order_{appliedStatus}", context);

                    // SIM-13: the goods go back on the market with the money going back
                    // to the buyer.
                    await TryRestoreListingStockAsync(existingOrder, context);
                }
            }

            // 6D: When order transitions to "funded", decrease listing quantity
            if (appliedStatus == "funded")
            {
                await TryDecreaseListingQuantityAsync(existingOrder, context);
            }

            var newVersion = version + 1;

            // Mock/demo payments have no real gateway to verify the transfer, so a
            // submitted payment is auto-verified server-side: advance
            // payment_submitted -> funded (clients are blocked from the
            // system-managed 'funded' state) so the farmer can act on the order.
            // Live/prod payments (TELEBIRR_MOCK unset) are funded by the real
            // gateway callback, never here.
            if (appliedStatus == "payment_submitted" && IsMockPaymentsEnabled())
            {
                var fundedData = await TryAutoFundMockOrderAsync(
                    operation.EntityId, newVersion, existingOrder, context);
                if (fundedData != null)
                {
                    // System-driven follow-on transition — record it as a system actor.
                    await EmitTxAuditAsync(context, operation.EntityId, "order", operation.EntityId,
                        "payment_verified", "payment_submitted", "funded",
                        ctxOverride: AuditContext.ForSystem());
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        ServerVersion = newVersion + 1,
                        ServerData = fundedData
                    };
                }
            }

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
    /// SIM-12: freeze the escrow behind a delivery disputed through sync, via the escrow
    /// service's status write and audit (no idempotency record: the sync op is already
    /// idempotent). Best-effort; a failure is logged and never fails the delivery update.
    /// </summary>
    private async Task TryFreezeEscrowForDisputedDeliveryAsync(
        string deliveryId, string? disputedBy, string? reason, ILambdaContext context)
    {
        if (_orderEscrowService == null || string.IsNullOrEmpty(disputedBy))
        {
            return;
        }

        try
        {
            var frozen = await _orderEscrowService.MarkDisputedFromDeliveryAsync(deliveryId, disputedBy, reason);
            context.Logger.LogInformation(frozen
                ? $"Escrow for delivery {deliveryId} disputed by {disputedBy}"
                : $"No escrowed money to freeze for disputed delivery {deliveryId}");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to freeze escrow for disputed delivery {deliveryId}: [{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>Order logistics statuses in progression order (SIM-11).</summary>
    private static readonly string[] OrderLogisticsProgression =
    {
        "accepted", "pickup_confirmed", "in_transit", "delivered"
    };

    /// <summary>
    /// SIM-11: advance the linked order to mirror a delivery transition. The order only
    /// ever moves forward along accepted -> pickup_confirmed -> in_transit -> delivered;
    /// a self-pickup handoff or a pickedUp -> delivered confirmation jumps straight to
    /// delivered. Best-effort: a failure is logged and never fails the delivery update.
    /// Audited as a system actor so the monitor shows who really moved the order.
    /// </summary>
    private async Task TryAdvanceOrderForDeliveryAsync(
        string orderId, string deliveryStatus, ILambdaContext context)
    {
        var target = deliveryStatus switch
        {
            "pickedUp" => "pickup_confirmed",
            "inTransit" => "in_transit",
            "delivered" => "delivered",
            "disputed" => "disputed",
            _ => null
        };
        if (target == null)
        {
            return;
        }

        try
        {
            var order = await _orderRepo.GetByIdAsync(orderId);
            if (order == null)
            {
                return;
            }

            var current = order.TryGetValue("status", out var cs) ? cs?.ToString() ?? "" : "";
            if (target == "disputed")
            {
                // SIM-12: only a delivered order can be disputed (contract: delivered -> disputed).
                if (current != "delivered")
                {
                    return;
                }
            }
            else
            {
                var currentRank = Array.IndexOf(OrderLogisticsProgression, current);
                var targetRank = Array.IndexOf(OrderLogisticsProgression, target);
                if (currentRank < 0 || currentRank >= targetRank)
                {
                    // Outside the logistics run (disputed, cancelled, ...) or already at/after
                    // the target: never move an order backwards or out of a terminal state.
                    return;
                }
            }

            var version = 1;
            if (order.TryGetValue("version", out var v))
            {
                int.TryParse(v?.ToString(), out version);
            }

            var result = await _orderRepo.UpdateAsync(
                orderId, new Dictionary<string, object> { { "status", target } }, version);
            if (!result.IsSuccess)
            {
                context.Logger.LogWarning(
                    $"Order {orderId} not advanced to {target} from delivery {deliveryStatus}: {result.Error?.Message}");
                return;
            }

            await EmitTxAuditAsync(context, orderId, "order", orderId,
                OrderActionFor(target), current, target, ctxOverride: AuditContext.ForSystem());
            context.Logger.LogInformation($"Order {orderId}: {current} -> {target} (delivery {deliveryStatus})");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to advance order {orderId} for delivery {deliveryStatus}: [{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>
    /// SIM-13: give the listing its stock back when a funded order dies (rejected or
    /// cancelled). Idempotency per order and the race fence live in the repository
    /// (version CAS plus the listing's set of restored order ids). Best-effort, like
    /// the decrement: a failure is logged and never fails the order update.
    /// </summary>
    private async Task TryRestoreListingStockAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        try
        {
            var listingId = order.TryGetValue("listingId", out var lid) ? lid?.ToString() : null;
            var orderId = order.TryGetValue("orderId", out var oid) ? oid?.ToString() : null;
            var quintals = ParseDouble(order, "quantity") / KgPerQuintal;
            if (string.IsNullOrEmpty(listingId) || string.IsNullOrEmpty(orderId) || quintals <= 0)
            {
                return;
            }

            var restored = await _cropRepo.TryRestoreStockAsync(listingId, orderId, quintals);
            context.Logger.LogInformation(restored
                ? $"Listing {listingId}: +{quintals} quintals restored (order {orderId} died)"
                : $"Listing {listingId}: nothing to restore for order {orderId} (already restored or listing gone)");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to restore listing stock for order: [{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>
    /// When an order becomes "funded", take the ordered quantity off the crop listing
    /// (the listing becomes "reserved" at 0). Idempotent per order and best-effort: a
    /// failure logs the alarmable ORDER_STOCK_DECREMENT_FAILED token and never blocks
    /// the order update (ESC-01, <see cref="OrderFundingService.DecrementStockAsync"/>).
    /// </summary>
    private Task TryDecreaseListingQuantityAsync(
        Dictionary<string, object> order, ILambdaContext context)
        => _orderFunding.DecrementStockAsync(order, context);

    /// <summary>
    /// Whether the environment runs mocked payments (no real Telebirr gateway).
    /// Set by infra for dev/staging (TELEBIRR_MOCK=true); never set in prod or
    /// when the live gateway is enabled.
    /// </summary>
    private static bool IsMockPaymentsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("TELEBIRR_MOCK"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Auto-verify a mocked payment by advancing the order payment_submitted ->
    /// funded server-side (the client cannot set the system-managed 'funded'
    /// state). Mirrors the real funded path: stamps paymentVerifiedAt and
    /// decreases the listing quantity. Best-effort — on failure the caller keeps
    /// the successful payment_submitted result rather than erroring the payment.
    /// </summary>
    private async Task<Dictionary<string, object>?> TryAutoFundMockOrderAsync(
        string orderId, int expectedVersion,
        Dictionary<string, object> existingOrder, ILambdaContext context)
    {
        try
        {
            var fundData = new Dictionary<string, object>
            {
                { "status", "funded" },
                { "paymentVerifiedAt", DateTime.UtcNow.ToString("o") }
            };

            var result = await _orderRepo.UpdateAsync(orderId, fundData, expectedVersion);
            if (!result.IsSuccess)
            {
                context.Logger.LogWarning(
                    $"Mock auto-fund not applied for order {orderId}: {result.Error?.Message}");
                return null;
            }

            // Same side effects as a live Telebirr payment (ESC-01): stock off the listing
            // and the order escrow, both idempotent per order.
            await _orderFunding.EnsureFundedSideEffectsAsync(result.Value!, "system", _batchAudit, context);
            context.Logger.LogInformation($"Order {orderId} auto-funded (mock payments).");
            return result.Value;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Mock auto-fund failed for order {orderId}: [{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>
    /// Best-effort refund scheduling for a rejected/cancelled funded order: expires
    /// the order's escrow so the hourly auto-refund cron pays the buyer back. A
    /// failure here is money stuck in escrow — alarmable token, needs reconciliation.
    /// </summary>
    private async Task TryScheduleOrderEscrowRefundAsync(
        string orderId, string reason, ILambdaContext context)
    {
        if (_orderEscrowService == null)
        {
            return;
        }

        try
        {
            var scheduled = await _orderEscrowService.ScheduleRefundForOrderAsync(
                orderId, reason, "system", _batchAudit);
            context.Logger.LogInformation(scheduled
                ? $"Order escrow refund scheduled: orderId={orderId} reason={reason}"
                : $"No refundable order escrow for orderId={orderId} ({reason}) — "
                  + "either none exists or delivery already confirmed.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={orderId} reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// Best-effort escrow↔delivery linkage after a delivery create. Self-heals a
    /// missing order escrow while the order still holds money; "not linked" is a
    /// normal outcome for pre-escrow legacy orders.
    /// </summary>
    private async Task TryLinkOrderEscrowAsync(
        string orderId, string deliveryId, Dictionary<string, object> order, ILambdaContext context)
    {
        if (_orderEscrowService == null)
        {
            return;
        }

        try
        {
            var linked = await _orderEscrowService.LinkDeliveryToOrderEscrowAsync(
                orderId, deliveryId, order, "system", _batchAudit);
            if (!linked)
            {
                context.Logger.LogInformation(
                    $"No order escrow linked for delivery {deliveryId} (orderId={orderId}).");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_LINK_FAILED orderId={orderId} deliveryId={deliveryId} "
                + $"reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// SIM-40: a new listing shows buyers the owner's name and phone, so they come
    /// from the users table, never from the client (a spoofed phone redirected buyers).
    /// Returns an error message when the profile cannot be read or has no name.
    /// </summary>
    private async Task<string?> StampOwnerContactAsync(
        Dictionary<string, object> data, string ownerId, string nameKey, string phoneKey,
        ILambdaContext context)
    {
        if (_userRepo == null)
        {
            context.Logger.LogError($"Cannot stamp {phoneKey}: user repository not configured.");
            return "Your profile could not be read. Please try again later.";
        }

        var owner = await _userRepo.GetUserByIdAsync(ownerId);
        if (owner == null)
        {
            return "Your profile could not be read. Please try again later.";
        }

        // Display name first, else the legal name received from Fayda. The client
        // value is never kept: with no name on the profile the create is refused.
        var name = !string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.DisplayName
            : !string.IsNullOrWhiteSpace(owner.FullName) ? owner.FullName
            : null;
        if (name == null)
        {
            return "Add your name to your profile before creating a listing.";
        }

        data[nameKey] = name.Trim();
        data[phoneKey] = owner.PhoneNumber;
        return null;
    }

    /// <summary>
    /// Both parties must be phone-verified, and Fayda-verified above the NBE
    /// threshold, before the buyer pays. Fails closed when the profiles cannot be read.
    /// Also returns the buyer's row so the step-up check can read the last Fayda login.
    /// </summary>
    private async Task<(string? Error, UserDto? Buyer)> CheckPaymentKycAsync(
        string buyerId, string? farmerId, decimal orderTotal, ILambdaContext context)
    {
        if (_userRepo == null)
        {
            context.Logger.LogError("Payment KYC gate unavailable: user repository not configured.");
            return (KycPolicy.Message(KycPolicy.ReasonUnavailable, "Identity verification is unavailable right now. Please try again later."), null);
        }

        var buyer = await _userRepo.GetUserByIdAsync(buyerId);
        var farmer = string.IsNullOrEmpty(farmerId) ? null : await _userRepo.GetUserByIdAsync(farmerId);
        return (KycPolicy.CheckPayment(buyer, farmer, orderTotal), buyer);
    }

    /// <summary>
    /// Step-up check for a delivery dispute, sized by the linked order's total. A
    /// delivery without a resolvable order carries no amount and needs no step-up.
    /// </summary>
    private async Task<string?> CheckDeliveryDisputeStepUpAsync(Dictionary<string, AttributeValue> delivery)
    {
        var orderId = delivery.TryGetValue("orderId", out var oid) ? oid.S : null;
        if (string.IsNullOrEmpty(orderId))
        {
            return null;
        }

        Dictionary<string, object>? order;
        try
        {
            order = await _orderRepo.GetByIdAsync(orderId);
        }
        catch (Exception)
        {
            order = null;
        }

        return order == null ? null : StepUpPolicy.Check(_requestAuthTime, ReadDecimal(order, "orderTotal"));
    }

    /// <summary>True when the caller's id equals any of the named id fields.</summary>
    private static bool IsParty(Dictionary<string, object> item, string? userId, params string[] idFields)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        foreach (var field in idFields)
        {
            if (item.TryGetValue(field, out var raw) && string.Equals(raw?.ToString(), userId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsParty(Dictionary<string, AttributeValue> item, string? userId, params string[] idFields)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        foreach (var field in idFields)
        {
            if (item.TryGetValue(field, out var attr) && string.Equals(attr.S, userId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static decimal ReadDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var raw) || raw == null)
        {
            return 0m;
        }

        return raw switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            int i => i,
            long l => l,
            _ => decimal.TryParse(raw.ToString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m
        };
    }

    /// <summary>
    /// Validate authorization for specific order state transitions.
    /// Returns null if authorized, or an error message string if not.
    /// </summary>
    private static string? ValidateOrderTransitionAuth(
        string currentStatus, string newStatus,
        string? authenticatedUserId, string? buyerId, string? farmerId)
    {
        // System-only transitions: block from client sync. payment_failed joined them in
        // ESC-01: a participant setting it while a Telebirr payment was in flight let the
        // buyer resubmit and pay twice (order-lifecycle.md says only the system sets it).
        if (newStatus is "funded" or "disbursing" or "settled" or "dispute_resolved" or "payment_failed")
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

    /// <summary>
    /// SIM-10: the contract's example payloads and the mobile dispute call echo
    /// bookkeeping keys the server owns. deliveryId may be sent but must name this
    /// operation's entity; updatedAt, pickedUpAt and confirmedAt are always stamped by
    /// the server (confirmedAt anchors the 48-hour dispute window, so a client value
    /// must never be trusted) and are dropped here. Returns an error message without
    /// the code prefix, or null when clean.
    /// </summary>
    private static string? StripDeliveryEchoFields(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (data.TryGetValue("deliveryId", out var echoedDeliveryId))
        {
            if (!string.Equals(echoedDeliveryId?.ToString(), operation.EntityId, StringComparison.Ordinal))
            {
                return "deliveryId does not match the operation entityId.";
            }

            data.Remove("deliveryId");
        }

        data.Remove("updatedAt");
        data.Remove("pickedUpAt");
        data.Remove("confirmedAt");
        return null;
    }

    /// <summary>
    /// SIM-08: the client's optimistic-concurrency version. The contract carries it as
    /// the op-level clientVersion; the shipped clients send data.version. Both are
    /// honoured, op-level first. Returns false (version 0) when neither is present.
    /// </summary>
    private static bool TryGetClientVersion(SyncOperationRequest operation, out int version)
    {
        if (operation.ClientVersion is int opLevel)
        {
            version = opLevel;
            return true;
        }

        if (operation.Data != null
            && operation.Data.TryGetValue("version", out var raw)
            && raw != null
            && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
        {
            return true;
        }

        version = 0;
        return false;
    }

    private static SyncOperationResult OrderValidationError(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = $"ORDER_VALIDATION: {message}"
    };

    /// <summary>
    /// SIM-05: bind an order create to the listing it references (existence is checked
    /// by the caller). Returns an error message without the code prefix, or null when
    /// clean. Overwrites farmerName from the listing so the client cannot spoof it.
    /// </summary>
    private static string? BindOrderToListing(
        Dictionary<string, object> data, Dictionary<string, object> listing, string? buyerId)
    {
        var listingStatus = listing.TryGetValue("status", out var ls)
            ? ls?.ToString() ?? "available"
            : "available";
        if (!string.Equals(listingStatus, "available", StringComparison.OrdinalIgnoreCase))
        {
            return $"Listing is not available for orders (status: {listingStatus}).";
        }

        var listingFarmerId = listing.TryGetValue("farmerId", out var lf) ? lf?.ToString() : null;
        if (string.IsNullOrEmpty(listingFarmerId)
            || !string.Equals(data["farmerId"]?.ToString(), listingFarmerId, StringComparison.Ordinal))
        {
            return "farmerId must be the farmer on the listing.";
        }

        if (string.Equals(buyerId, listingFarmerId, StringComparison.Ordinal))
        {
            return "Self-trading not allowed. Buyer and farmer must be different users.";
        }

        var listingCropType = listing.TryGetValue("cropType", out var lc) ? lc?.ToString() : null;
        if (!string.Equals(data["cropType"]?.ToString(), listingCropType, StringComparison.OrdinalIgnoreCase))
        {
            return "cropType must match the listing.";
        }

        var listingPricePerKg = ParseDouble(listing, "pricePerKg");
        var pricePerUnit = ParseDouble(data, "pricePerUnit");
        if (listingPricePerKg <= 0 || Math.Abs(pricePerUnit - listingPricePerKg) > MoneyTolerance)
        {
            return "pricePerUnit must equal the listing price "
                + $"({listingPricePerKg.ToString("0.00", CultureInfo.InvariantCulture)} ETB/kg).";
        }

        var stockKg = ParseDouble(listing, "quantity") * KgPerQuintal;
        var quantityKg = ParseDouble(data, "quantity");
        if (quantityKg > stockKg + QuantityTolerance)
        {
            return $"quantity ({quantityKg.ToString("0.##", CultureInfo.InvariantCulture)} kg) exceeds "
                + $"the listing stock ({stockKg.ToString("0.##", CultureInfo.InvariantCulture)} kg).";
        }

        if (listing.TryGetValue("farmerName", out var lfn) && !string.IsNullOrWhiteSpace(lfn?.ToString()))
        {
            data["farmerName"] = lfn.ToString()!;
        }

        return null;
    }

    /// <summary>
    /// SIM-05: bind an order create to the trip it references and derive the transport
    /// cost (kg / 100 x pricePerQuintal). A client-sent transportCost may only echo it.
    /// Overwrites truckerName from the trip.
    /// </summary>
    private static string? BindOrderToTrip(
        Dictionary<string, object> data,
        Dictionary<string, AttributeValue> trip,
        string truckerId,
        double quantityKg,
        double? clientTransportCost,
        out double transportCost)
    {
        transportCost = 0.0;

        var tripStatus = trip.TryGetValue("status", out var ts) ? ts.S ?? "open" : "open";
        if (tripStatus is not ("open" or "matched"))
        {
            return $"Trip is not open for orders (status: {tripStatus}).";
        }

        var tripTruckerId = trip.TryGetValue("truckerId", out var tt) ? tt.S : null;
        if (string.IsNullOrEmpty(tripTruckerId)
            || !string.Equals(tripTruckerId, truckerId, StringComparison.Ordinal))
        {
            return "truckerId must be the trucker on the trip.";
        }

        var pricePerQuintal = trip.TryGetValue("pricePerQuintal", out var pq)
            && double.TryParse(pq.N ?? pq.S, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
        if (pricePerQuintal <= 0)
        {
            return "Trip has no valid pricePerQuintal.";
        }

        transportCost = quantityKg / KgPerQuintal * pricePerQuintal;
        if (clientTransportCost is double sent && Math.Abs(sent - transportCost) > MoneyTolerance)
        {
            return "transportCost must equal quantity/100 x the trip pricePerQuintal "
                + $"({transportCost.ToString("0.00", CultureInfo.InvariantCulture)} ETB).";
        }

        if (trip.TryGetValue("truckerName", out var tn) && !string.IsNullOrEmpty(tn.S))
        {
            data["truckerName"] = tn.S;
        }

        return null;
    }

    private static List<string> ValidateOrderFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        if (isCreate)
        {
            if (!data.ContainsKey("listingId"))
            {
                errors.Add("listingId is required.");
            }

            if (!data.ContainsKey("buyerId"))
            {
                errors.Add("buyerId is required.");
            }

            if (!data.ContainsKey("farmerId"))
            {
                errors.Add("farmerId is required.");
            }

            if (!data.ContainsKey("buyerName"))
            {
                errors.Add("buyerName is required.");
            }

            if (!data.ContainsKey("farmerName"))
            {
                errors.Add("farmerName is required.");
            }

            if (!data.ContainsKey("cropType"))
            {
                errors.Add("cropType is required.");
            }
        }

        // Validate quantity
        if (data.TryGetValue("quantity", out var qObj))
        {
            if (!decimal.TryParse(qObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
                || qty <= 0 || qty > MaxOrderKg)
            {
                errors.Add("quantity must be between 0.01 and 1000000 kg.");
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
            {
                errors.Add("buyerName must not exceed 100 characters.");
            }
        }

        if (data.TryGetValue("farmerName", out var fnObj))
        {
            var name = fnObj?.ToString() ?? "";
            if (name.Length > 100)
            {
                errors.Add("farmerName must not exceed 100 characters.");
            }
        }

        if (data.TryGetValue("rejectionReason", out var rrObj))
        {
            var reason = rrObj?.ToString() ?? "";
            if (reason.Length > 500)
            {
                errors.Add("rejectionReason must not exceed 500 characters.");
            }
        }

        if (data.TryGetValue("cancelReason", out var crObj))
        {
            var reason = crObj?.ToString() ?? "";
            if (reason.Length > 500)
            {
                errors.Add("cancelReason must not exceed 500 characters.");
            }
        }

        if (data.TryGetValue("cbeTransactionId", out var cbeObj))
        {
            var txId = cbeObj?.ToString() ?? "";
            if (txId.Length > 100)
            {
                errors.Add("cbeTransactionId must not exceed 100 characters.");
            }
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
        if (!data.TryGetValue(key, out var value))
        {
            return 0.0;
        }

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
        if (!data.TryGetValue(key, out var value))
        {
            return null;
        }

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

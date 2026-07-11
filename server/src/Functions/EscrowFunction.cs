using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Escrow Lambda handler for all /escrow/* endpoints.
/// Routes based on HTTP method and path segment.
///
/// Endpoints:
/// - POST /escrow/initiate  → Buyer initiates escrow payment
/// - POST /escrow/release   → System/admin releases funds to farmer
/// - POST /escrow/dispute   → Buyer/farmer raises a dispute
/// - POST /escrow/refund    → System/admin refunds buyer
/// - GET  /escrow/history   → Authenticated user's payment history
/// - GET  /escrow/{id}      → Get single escrow by ID
/// - GET  /escrow/by-delivery/{deliveryId} → Get the escrow for a delivery
///
/// Error codes: ESCROW_001 through ESCROW_023 (see escrow-protocol.md)
/// </summary>
public class EscrowFunction
{
    private readonly EscrowService _escrowService;
    private readonly IFraudDetectionService? _fraudService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public EscrowFunction()
    {
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE")
                           ?? throw new InvalidOperationException("ESCROWS_TABLE is not set.");
        var auditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE")
                         ?? throw new InvalidOperationException("ESCROW_AUDIT_TABLE is not set.");
        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE")
                               ?? throw new InvalidOperationException("ESCROW_IDEMPOTENCY_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();

        var escrowRepo = new EscrowRepository(dynamoDb, escrowsTable);
        var auditRepo = new EscrowAuditRepository(dynamoDb, auditTable);
        var idempotencyRepo = new EscrowIdempotencyRepository(dynamoDb, idempotencyTable);
        var telebirrGateway = CreateTelebirrGateway();

        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        IUserRepository? userRepo = null;
        if (!string.IsNullOrEmpty(usersTable))
        {
            userRepo = new UserRepository(dynamoDb, usersTable);
        }

        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE");
        IOrderRepository? orderRepo = null;
        if (!string.IsNullOrEmpty(ordersTable))
        {
            orderRepo = new OrderRepository(dynamoDb, ordersTable);
        }

        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE");
        IDeliveryRepository? deliveryRepo = null;
        if (!string.IsNullOrEmpty(deliveriesTable))
        {
            deliveryRepo = new DeliveryRepository(dynamoDb, deliveriesTable);
        }

        _escrowService = new EscrowService(
            escrowRepo, auditRepo, idempotencyRepo, telebirrGateway, userRepo, orderRepo, deliveryRepo);

        // Fraud detection (F-14)
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE") ?? "";
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        if (!string.IsNullOrEmpty(rateLimitTable))
        {
            _fraudService = new FraudDetectionService(
                dynamoDb, cropsTable, fraudSignalsTable,
                new RateLimitRepository(dynamoDb, rateLimitTable));
        }
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public EscrowFunction(EscrowService escrowService, IFraudDetectionService? fraudService = null)
    {
        _escrowService = escrowService ?? throw new ArgumentNullException(nameof(escrowService));
        _fraudService = fraudService;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"EscrowFunction invoked: {request.HttpMethod} {request.Path}");

        var action = ExtractAction(request);

        return (request.HttpMethod, action) switch
        {
            ("POST", "initiate") => await HandleInitiateAsync(request, context),
            ("POST", "release") => await HandleReleaseAsync(request, context),
            ("POST", "dispute") => await HandleDisputeAsync(request, context),
            ("POST", "refund") => await HandleRefundAsync(request, context),
            ("GET", "history") => await HandleHistoryAsync(request, context),
            // /escrow/by-delivery/{deliveryId} — routed on the path param, since
            // ExtractAction returns the last segment (the deliveryId value), not
            // "by-delivery". Distinct from /escrow/{id} (path param "id"). F-33.
            ("GET", _) when request.PathParameters?.ContainsKey("deliveryId") == true
                => await HandleGetByDeliveryAsync(request, context),
            ("GET", _) when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetByIdAsync(request, context),
            _ => LambdaResponse.Error(405, "ESCROW_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// POST /escrow/initiate — Buyer initiates escrow payment for a delivery.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleInitiateAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        InitiateEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.InitiateEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        // ── Fraud velocity check (F-14) ──
        if (_fraudService != null)
        {
            var fraudCheck = await _fraudService.CheckPaymentVelocityAsync(userId);
            if (!fraudCheck.IsAllowed)
            {
                // Best-effort: the signal feeds the admin review queue; a write
                // failure must not mask the rate-limit response.
                try
                {
                    await _fraudService.RecordFraudSignalAsync(
                        userId, fraudCheck.ErrorCode ?? "FRAUD_VELOCITY_EXCEEDED",
                        body.DeliveryId, "escrow", fraudCheck.Message);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(
                        $"Failed to record fraud signal for user {userId}: {ex.Message}");
                }

                // Contract maps rate-limit rejection to ESCROW_011 (429); the mobile client keys
                // on this code. The fraud service's internal code is preserved in the log context.
                return LambdaResponse.Error(429, "ESCROW_011",
                    fraudCheck.Message ?? "Payment rate limit exceeded. Please try again later.");
            }
        }

        var correlationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.InitiateAsync(
                body, userId, AuditContext.From(request, userId));

            // Record for velocity tracking (F-14)
            if (_fraudService != null && result.IsSuccess)
            {
                await _fraudService.RecordPaymentInitiatedAsync(userId);
            }

            if (result.IsSuccess)
            {
                AuditLogger.LogAudit("ESCROW", "INITIATE",
                    userId: userId, correlationId: correlationId,
                    entityType: "escrow", entityId: result.Value?.EscrowId,
                    outcome: "success");
            }
            else
            {
                AuditLogger.LogAudit("ESCROW", "INITIATE",
                    userId: userId, correlationId: correlationId,
                    entityType: "escrow",
                    outcome: "failure", errorCode: result.Error?.Code);
            }

            return MapResult(result,
                value => value.IsDuplicate
                    ? LambdaResponse.Ok(value, AppJsonContext.Default.InitiateEscrowResponse)
                    : LambdaResponse.Created(value, AppJsonContext.Default.InitiateEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error initiating escrow: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "INITIATE",
                userId: userId, correlationId: correlationId,
                entityType: "escrow",
                outcome: "error", errorCode: "ESCROW_012");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/release — Release escrowed funds to the farmer.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleReleaseAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        ReleaseEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.ReleaseEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var role = GetAuthenticatedUserRole(request);
        var releaseCorrelationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.ReleaseAsync(
                body, userId, role, AuditContext.From(request, userId, role));

            AuditLogger.LogAudit("ESCROW", "RELEASE",
                userId: userId, correlationId: releaseCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: result.IsSuccess ? "success" : "failure",
                errorCode: result.Error?.Code);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.ReleaseEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error releasing escrow: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "RELEASE",
                userId: userId, correlationId: releaseCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: "error", errorCode: "ESCROW_012");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/dispute — Buyer or farmer raises a dispute.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDisputeAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        DisputeEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.DisputeEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var disputeCorrelationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.DisputeAsync(
                body, userId, AuditContext.From(request, userId));

            AuditLogger.LogAudit("ESCROW", "DISPUTE",
                userId: userId, correlationId: disputeCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: result.IsSuccess ? "success" : "failure",
                errorCode: result.Error?.Code);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.DisputeEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error disputing escrow: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "DISPUTE",
                userId: userId, correlationId: disputeCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: "error", errorCode: "ESCROW_012");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// POST /escrow/refund — Refund escrowed funds to the buyer.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleRefundAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        RefundEscrowRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.RefundEscrowRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Request body is required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var role = GetAuthenticatedUserRole(request);
        var refundCorrelationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.RefundAsync(
                body, userId, role, AuditContext.From(request, userId, role));

            AuditLogger.LogAudit("ESCROW", "REFUND",
                userId: userId, correlationId: refundCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: result.IsSuccess ? "success" : "failure",
                errorCode: result.Error?.Code);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.RefundEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error refunding escrow: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "REFUND",
                userId: userId, correlationId: refundCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: "error", errorCode: "ESCROW_012");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// GET /escrow/history — Authenticated user's payment history.
    /// Query params: status, role, fromDate, toDate, limit, nextToken.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleHistoryAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("role", out var role);
        queryParams.TryGetValue("fromDate", out var fromDate);
        queryParams.TryGetValue("toDate", out var toDate);
        queryParams.TryGetValue("nextToken", out var nextToken);

        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "ESCROW_023",
                    "Invalid limit parameter. Must be between 1 and 50.");
            }
        }

        try
        {
            var result = await _escrowService.GetHistoryAsync(
                userId, status, role, fromDate, toDate, limit, nextToken);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.GetEscrowHistoryResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying escrow history: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// GET /escrow/{id} — Get a single escrow by ID.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        if (!request.PathParameters.TryGetValue("id", out var escrowId) || string.IsNullOrEmpty(escrowId))
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Missing escrow ID in path.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        try
        {
            var role = GetAuthenticatedUserRole(request);
            var result = await _escrowService.GetByIdAsync(escrowId, userId, role);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.EscrowDto));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting escrow {escrowId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    /// <summary>
    /// GET /escrow/by-delivery/{deliveryId} — Get the escrow for a delivery.
    /// Only accessible by the buyer, farmer, or an admin party to the escrow. F-33.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetByDeliveryAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");
        }

        if (!request.PathParameters.TryGetValue("deliveryId", out var deliveryId)
            || string.IsNullOrEmpty(deliveryId))
        {
            return LambdaResponse.Error(400, "ESCROW_001", "Missing delivery ID in path.");
        }

        var userId = GetAuthenticatedUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "ESCROW_005", "Unable to determine authenticated user.");
        }

        try
        {
            var role = GetAuthenticatedUserRole(request);
            var result = await _escrowService.GetByDeliveryAsync(deliveryId, userId, role);
            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.EscrowDto));
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Error getting escrow for delivery {deliveryId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_012", "Internal server error.");
        }
    }

    // ──────────────────────────────────────────────
    // Routing helper
    // ──────────────────────────────────────────────

    /// <summary>
    /// Extract the action segment from the request path (e.g. "initiate" from "/escrow/initiate").
    /// Falls back to empty string for paths like "/escrow/{id}".
    /// </summary>
    private static string ExtractAction(APIGatewayProxyRequest request)
    {
        var path = request.Path ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expected: ["escrow", "initiate"] or ["escrow", "{id}"]
        if (segments.Length >= 2)
        {
            return segments[^1];
        }

        return "";
    }

    // ──────────────────────────────────────────────
    // Auth helpers (same pattern as DeliveryFunction)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Check if the request is authenticated by verifying JWT authorizer claims
    /// in the request context (set by API Gateway after JWT validation).
    /// SECURITY: Raw Authorization headers are NOT trusted — they can be spoofed.
    /// </summary>
    private static bool IsAuthenticated(APIGatewayProxyRequest request)
        => AuthHelper.IsAuthenticated(request);

    private static string? GetAuthenticatedUserId(APIGatewayProxyRequest request)
        => AuthHelper.GetUserId(request);

    private static string? GetAuthenticatedUserRole(APIGatewayProxyRequest request)
        => AuthHelper.GetClaim(request, "role") ?? AuthHelper.GetClaim(request, "custom:role");

    // ──────────────────────────────────────────────
    // Gateway factory
    // ──────────────────────────────────────────────

    /// <summary>
    /// Create the Telebirr gateway based on the TELEBIRR_MOCK environment variable.
    /// Set TELEBIRR_MOCK=true in development/staging to use the mock gateway.
    /// In production (TELEBIRR_MOCK absent or false), this throws to prevent
    /// accidental mock usage with real user funds.
    /// </summary>
    private static ITelebirrGateway CreateTelebirrGateway()
    {
        var useMock = string.Equals(
            Environment.GetEnvironmentVariable("TELEBIRR_MOCK"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (useMock)
        {
            return new MockTelebirrGateway();
        }

        // Real gateway: local sandbox simulator or the live Telebirr sandbox,
        // selected purely by config (TELEBIRR_LOCAL_CREDENTIALS_FILE / TELEBIRR_SECRET_ARN).
        return TelebirrGatewayFactory.CreateGateway(TelebirrGatewayFactory.LoadConfigOrThrow());
    }

    // ──────────────────────────────────────────────
    // Result mapping
    // ──────────────────────────────────────────────

    /// <summary>
    /// Map an EscrowResult to an APIGatewayProxyResponse.
    /// On success/duplicate: use the provided success mapper.
    /// On failure: map EscrowResultError to LambdaResponse.Error.
    /// </summary>
    private static APIGatewayProxyResponse MapResult<T>(
        EscrowResult<T> result,
        Func<T, APIGatewayProxyResponse> onSuccess)
    {
        if (result.IsSuccess)
        {
            return onSuccess(result.Value!);
        }

        var error = result.Error!;
        return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
    }
}

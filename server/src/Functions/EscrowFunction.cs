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
    private readonly IUserRepository? _userRepo;

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

        // ESC-01: live order checkouts share the idempotency table (slot + merch_order_id rows).
        var orderCheckoutRepo = new OrderCheckoutRepository(dynamoDb, idempotencyTable);

        // ESC-03: an admin settlement of a dispute verdict first moves its order and delivery to
        // the verdict (a buyer_wins refund before pickup cancels the order and restores its
        // listing stock), audited in transaction-audit.
        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        var txAuditRepo = !string.IsNullOrEmpty(txAuditTable)
            ? new TransactionAuditRepository(dynamoDb, txAuditTable)
            : null;
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
        var cropRepo = !string.IsNullOrEmpty(cropsTable) ? new CropRepository(dynamoDb, cropsTable) : null;

        _escrowService = new EscrowService(
            escrowRepo, auditRepo, idempotencyRepo, telebirrGateway, userRepo, orderRepo, deliveryRepo,
            orderCheckoutRepo, txAuditRepo, cropRepo);
        _userRepo = userRepo;

        // Fraud detection (F-14)
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE") ?? "";
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        if (!string.IsNullOrEmpty(rateLimitTable))
        {
            _fraudService = new FraudDetectionService(
                dynamoDb, cropsTable, fraudSignalsTable,
                new RateLimitRepository(dynamoDb, rateLimitTable));
        }
    }

    /// <summary>ESC-45 (D6): run time kept back from Telebirr calls for the writes and the response after them.</summary>
    internal static readonly TimeSpan GatewayCallReserve = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public EscrowFunction(
        EscrowService escrowService, IFraudDetectionService? fraudService = null, IUserRepository? userRepo = null)
    {
        _escrowService = escrowService ?? throw new ArgumentNullException(nameof(escrowService));
        _fraudService = fraudService;
        _userRepo = userRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // The route template, never the raw path: a path segment is client text and this log
        // group carries the settlement-failure alarm filter (ESC-13). Defanged as well, because
        // AWS does not promise that a payload 1.0 resource is always the template.
        context.Logger.LogInformation(
            $"EscrowFunction invoked: {MoneyAlarmLog.LogId(request.HttpMethod)} {MoneyAlarmLog.LogText(request.Resource)}");

        // ESC-45 (D6): every Telebirr call ends before the writes after it (slot outcome, audit,
        // settlement status) and the response need the last seconds of the 30 s run.
        using var gatewayDeadline = GatewayDeadline.Begin(context, GatewayCallReserve);

        var action = ExtractAction(request);

        // ACC-02: an account that is being or has been deleted starts no payment and no dispute.
        if (request.HttpMethod == "POST" && (action is "initiate" or "order-checkout" or "dispute")
            && _userRepo != null && GetAuthenticatedUserId(request) is { Length: > 0 } callerId)
        {
            APIGatewayProxyResponse? gone;
            try
            {
                gone = await AccountDeletionGuard.RefuseIfDeletingAsync(_userRepo, callerId);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Account state read failed: [{ex.GetType().Name}]");
                return LambdaResponse.Error(503, "ESCROW_012", "Payment is temporarily unavailable. Please try again.");
            }

            if (gone != null)
            {
                return gone;
            }
        }

        return (request.HttpMethod, action) switch
        {
            ("POST", "initiate") => await HandleInitiateAsync(request, context),
            ("POST", "order-checkout") => await HandleOrderCheckoutAsync(request, context),
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

        // SIM-04: buyer role only (contract, Authorization Matrix). Fail closed when the
        // claim is missing: clients must present the ID token that carries it.
        var role = GetAuthenticatedUserRole(request);
        if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            AuditLogger.LogAudit("ESCROW", "INITIATE",
                userId: userId, correlationId: AuditLogger.GetCorrelationId(request),
                entityType: "escrow", outcome: "failure", errorCode: "ESCROW_025");
            await TryRecordRoleForbiddenAsync(userId, role, body.DeliveryId, "escrow", context);
            return LambdaResponse.Error(403, "ESCROW_025", "Only buyers can initiate escrow payments.");
        }

        // FRD-04 (fraud-detection-architecture.md 17.1): the buyer's risk tier, after the role check.
        // CRITICAL pays nothing; MEDIUM and HIGH add proofs to the step-up below.
        var riskTier = await CallerRiskTierAsync(userId);
        if (riskTier == RiskTier.Critical)
        {
            return AccountHeld(request, userId, "INITIATE", "escrow");
        }

        // Step-up: a payment needs a recent password (up to 10,000 ETB) or a recent
        // Fayda login (above it), so a long-lived session on a shared or stolen phone
        // cannot pay on its own. Without the user table the Fayda tier fails closed.
        string? buyerFaydaAuthAt = null;
        if (StepUpPolicy.PaymentNeedsFayda(body.Amount, riskTier) && _userRepo != null)
        {
            try
            {
                buyerFaydaAuthAt = (await _userRepo.GetUserByIdAsync(userId))?.FaydaAuthenticatedAt;
            }
            catch (Exception ex)
            {
                // Temporary: nothing was held, the client may retry.
                context.Logger.LogError($"Step-up profile read failed: [{ex.GetType().Name}]");
                AuditLogger.LogAudit("ESCROW", "INITIATE",
                    userId: userId, correlationId: AuditLogger.GetCorrelationId(request),
                    entityType: "escrow", outcome: "failure", errorCode: "ESCROW_012");
                return LambdaResponse.Error(503, "ESCROW_012",
                    "Payment is temporarily unavailable. Please try again.");
            }
        }
        var stepUpError = StepUpPolicy.CheckPayment(
            AuthHelper.GetAuthTime(request), buyerFaydaAuthAt, body.Amount, tier: riskTier);
        if (stepUpError != null)
        {
            var stepUpCode = StepUpPolicy.CodeOf(stepUpError);
            AuditLogger.LogAudit("ESCROW", "INITIATE",
                userId: userId, correlationId: AuditLogger.GetCorrelationId(request),
                entityType: "escrow", outcome: "failure", errorCode: stepUpCode);
            return LambdaResponse.Error(403, stepUpCode,
                stepUpCode == StepUpPolicy.FaydaErrorCode ? StepUpPolicy.FaydaErrorMessage : StepUpPolicy.ErrorMessage);
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
                        $"Failed to record fraud signal for user {userId}: [{ex.GetType().Name}]");
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
    /// ESC-01: POST /escrow/order-checkout. The order's buyer opens (or gets back) the live
    /// Telebirr checkout for a payment_submitted order. The body carries only the orderId;
    /// the amount and the Telebirr reference come from the server.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleOrderCheckoutAsync(
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

        var correlationId = AuditLogger.GetCorrelationId(request);
        var role = GetAuthenticatedUserRole(request);
        if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            AuditLogger.LogAudit("ESCROW", "ORDER_CHECKOUT",
                userId: userId, correlationId: correlationId,
                entityType: "order", outcome: "failure", errorCode: "ESCROW_025");
            await TryRecordRoleForbiddenAsync(userId, role, entityId: null, "order", context);
            return LambdaResponse.Error(403, "ESCROW_025", "Only buyers can pay for orders.");
        }

        OrderCheckoutRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.OrderCheckoutRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ESCROW_033", "Invalid JSON in request body.");
        }

        if (body == null || string.IsNullOrWhiteSpace(body.OrderId))
        {
            return LambdaResponse.Error(400, "ESCROW_033", "orderId is required.", "orderId");
        }

        // FRD-04 (fraud-detection-architecture.md 17.1): CRITICAL is refused before the velocity count
        // and before any Telebirr call. MEDIUM and HIGH are not asked again: the step-up ran at
        // payment_submitted.
        if (await CallerRiskTierAsync(userId) == RiskTier.Critical)
        {
            return AccountHeld(request, userId, "ORDER_CHECKOUT", "order", body.OrderId);
        }

        // Same payment velocity limit as /escrow/initiate: each new checkout is a Telebirr
        // preOrder, so a flood of them must not reach the merchant account.
        if (_fraudService != null)
        {
            var fraudCheck = await _fraudService.CheckPaymentVelocityAsync(userId);
            if (!fraudCheck.IsAllowed)
            {
                return LambdaResponse.Error(429, "ESCROW_011",
                    fraudCheck.Message ?? "Payment rate limit exceeded. Please try again later.");
            }
        }

        try
        {
            var result = await _escrowService.CreateOrderCheckoutAsync(
                body.OrderId, userId, AuditContext.From(request, userId));

            // Every attempt that reached Telebirr counts toward the velocity limit, including
            // refused ones; a returned open checkout does not.
            var reachedTelebirr = (result.IsSuccess && result.Value?.IsExisting == false)
                || result.Error?.Code == "ESCROW_013";
            if (_fraudService != null && reachedTelebirr)
            {
                await _fraudService.RecordPaymentInitiatedAsync(userId);
            }

            AuditLogger.LogAudit("ESCROW", "ORDER_CHECKOUT",
                userId: userId, correlationId: correlationId,
                entityType: "order", entityId: body.OrderId,
                outcome: result.IsSuccess ? "success" : "failure", errorCode: result.Error?.Code);

            return MapResult(result, value => LambdaResponse.Ok(value, AppJsonContext.Default.OrderCheckoutResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error opening order checkout: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "ORDER_CHECKOUT",
                userId: userId, correlationId: correlationId,
                entityType: "order", entityId: body.OrderId,
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

        // The route sits on the admin authorizer: an admin-pool token carries
        // cognito:groups, not custom:role. Verified here, honoured by the service.
        var isAdmin = AdminAuthHelper.IsInAdminGroup(request);
        var role = isAdmin ? "admin" : GetAuthenticatedUserRole(request);
        var releaseCorrelationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.ReleaseAsync(
                body, userId, role, AuditContext.From(request, userId, role), adminGroupVerified: isAdmin);

            AuditLogger.LogAudit("ESCROW", "RELEASE",
                userId: userId, correlationId: releaseCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: SettlementOutcome(result.IsSuccess, result.Error?.Code),
                errorCode: result.Error?.Code);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.ReleaseEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error releasing escrow: [{ex.GetType().Name}]");
            // ESC-13: unknown only when the exception escaped after the settlement lock (the
            // gateway may have been called); before the lock nothing can have moved (ESC-05).
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                body.EscrowId, "release_exception", MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
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

        // Step-up for disputes on large escrows (the party check happens in the
        // service; a non-party gets the service's own error below).
        EscrowResult<EscrowDto> existingEscrow;
        try
        {
            existingEscrow = await _escrowService.GetByIdAsync(
                body.EscrowId, userId, GetAuthenticatedUserRole(request));
        }
        catch (Exception ex)
        {
            // FUP-44: fail closed with a typed answer. The step-up cannot be decided, so the
            // dispute does not run: nothing was written and the client may retry.
            context.Logger.LogError(
                $"Dispute step-up read failed for escrow {MoneyAlarmLog.LogId(body.EscrowId)}: [{ex.GetType().Name}]");
            AuditLogger.LogAudit("ESCROW", "DISPUTE",
                userId: userId, correlationId: disputeCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: "failure", errorCode: "ESCROW_012");
            return LambdaResponse.Error(503, "ESCROW_012",
                "Disputes are temporarily unavailable. Nothing changed; please try again.");
        }

        if (existingEscrow.IsSuccess
            && StepUpPolicy.Check(request, existingEscrow.Value!.Amount) != null)
        {
            AuditLogger.LogAudit("ESCROW", "DISPUTE",
                userId: userId, correlationId: disputeCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: "failure", errorCode: StepUpPolicy.ErrorCode);
            return LambdaResponse.Error(403, StepUpPolicy.ErrorCode, StepUpPolicy.ErrorMessage);
        }

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

        var isAdmin = AdminAuthHelper.IsInAdminGroup(request);
        var role = isAdmin ? "admin" : GetAuthenticatedUserRole(request);
        var refundCorrelationId = AuditLogger.GetCorrelationId(request);

        try
        {
            var result = await _escrowService.RefundAsync(
                body, userId, role, AuditContext.From(request, userId, role), adminGroupVerified: isAdmin);

            AuditLogger.LogAudit("ESCROW", "REFUND",
                userId: userId, correlationId: refundCorrelationId,
                entityType: "escrow", entityId: body.EscrowId,
                outcome: SettlementOutcome(result.IsSuccess, result.Error?.Code),
                errorCode: result.Error?.Code);

            return MapResult(result,
                value => LambdaResponse.Ok(value, AppJsonContext.Default.RefundEscrowResponse));
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error refunding escrow: [{ex.GetType().Name}]");
            // ESC-13: unknown only when the exception escaped after the settlement lock (the
            // gateway may have been called); before the lock nothing can have moved (ESC-05).
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                body.EscrowId, "refund_exception", MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
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
        // SIM-16: without ?role= the query used to default to the buyer GSI, so a
        // farmer or trucker (including the farmer of a self-pickup, whose escrow is
        // order-funded and has no delivery) read []. Scope by the token's role.
        if (string.IsNullOrWhiteSpace(role))
        {
            role = AuthHelper.GetRole(request);
        }

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
            context.Logger.LogError($"Error getting escrow {MoneyAlarmLog.LogId(escrowId)}: [{ex.GetType().Name}]");
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
                $"Error getting escrow for delivery {MoneyAlarmLog.LogId(deliveryId)}: [{ex.GetType().Name}]");
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
        => AuthHelper.GetRole(request);

    // ──────────────────────────────────────────────
    // Risk tier gate (FRD-04)
    // ──────────────────────────────────────────────

    /// <summary>
    /// The caller's risk tier (fraud-detection-architecture.md 17.1), one read per request. Without a fraud
    /// service (no <c>RATE_LIMITS_TABLE</c>) there is no tier: LOW, as for an unconfigured signals table at
    /// a user gate (section 14.4), so today's checks and the baseline step-up still apply.
    /// </summary>
    private async Task<RiskTier> CallerRiskTierAsync(string userId)
        => _fraudService == null
            ? RiskTier.Low
            : RiskGatePolicy.TierAtUserGate(await _fraudService.GetUserRiskScoreAsync(userId));

    /// <summary>FRD-04: <c>403 FRAUD_ACCOUNT_HELD</c> (section 17.3); nothing held, nothing written.</summary>
    private static APIGatewayProxyResponse AccountHeld(
        APIGatewayProxyRequest request, string userId, string action, string entityType, string? entityId = null)
    {
        AuditLogger.LogAudit("ESCROW", action,
            userId: userId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: entityType, entityId: entityId, outcome: "failure", errorCode: RiskGatePolicy.AccountHeldCode);
        return LambdaResponse.Error(403, RiskGatePolicy.AccountHeldCode, RiskGatePolicy.AccountHeldMessage);
    }

    /// <summary>
    /// FRD-08: <c>ESCROW_025</c> records <c>ROLE_FORBIDDEN</c> on the caller, since the app shows the
    /// pay buttons to buyers only. A token without the role claim fails the check too (an access
    /// token instead of the ID token), which is not a probe, so it writes nothing. Best-effort: the
    /// 403 is the same whether the write works or not.
    /// </summary>
    private async Task TryRecordRoleForbiddenAsync(
        string userId, string? role, string? entityId, string entityType, ILambdaContext context)
    {
        if (_fraudService == null || string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        try
        {
            await _fraudService.RecordFraudSignalAsync(
                userId, FraudDetectionService.RoleForbiddenSignal, entityId, entityType,
                "ESCROW_025: a non-buyer role asked to pay.");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to record fraud signal {FraudDetectionService.RoleForbiddenSignal} for user {userId}: [{ex.GetType().Name}]");
        }
    }

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

    /// <summary>ESC-02: a settlement parked in the manual payout queue (202 ESCROW_034) is "queued", not a failure.</summary>
    private static string SettlementOutcome(bool isSuccess, string? errorCode)
        => isSuccess ? "success" : errorCode == "ESCROW_034" ? "queued" : "failure";

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

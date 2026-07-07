using System.Globalization;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// EventBridge scheduled event handler for escrow background jobs.
/// Routes based on "action" field in the request body:
///   - auto_release_and_refund (hourly): release confirmed + refund expired escrows
///   - reconciliation (every 6h): reconcile stuck "initiated" escrows with Telebirr
///
/// Accepts APIGatewayProxyRequest for compatibility with the existing Lambda bootstrap.
/// EventBridge rule passes the action payload in the request body as JSON.
///
/// Error codes:
/// - 400: ESCROW_CRON_VALIDATION - Invalid or missing action
/// - 500: ESCROW_CRON_ERROR - Unexpected failure during processing
/// </summary>
public class EscrowCronFunction
{
    private readonly EscrowService _escrowService;
    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly ITelebirrGateway _telebirrGateway;
    // Optional: lets the auto-refund sweep verify a delivery's real status so a
    // confirmed-but-unstamped escrow is never refunded to the buyer.
    private readonly IDeliveryRepository? _deliveryRepo;

    private const int ReconciliationStaleMinutes = 30;

    /// <summary>
    /// Grace period before a still-"initiated" escrow with NO Telebirr reference is
    /// cancelled. In the async H5/C2B flow such an escrow is normally just awaiting the
    /// buyer's payment — the webhook stamps the ref and flips it to "escrowed". The buyer
    /// has up to the preOrder timeout (timeout_express = 120m) to pay, so cancelling
    /// earlier could strand a real payment: the webhook's initiated→escrowed CAS would
    /// fail against a "cancelled" record. 150m = the 120m window + a 30m safety buffer.
    /// </summary>
    private const int NoRefCancelGraceMinutes = 150;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public EscrowCronFunction()
    {
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE")
                           ?? throw new InvalidOperationException("ESCROWS_TABLE is not set.");
        var auditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE")
                         ?? throw new InvalidOperationException("ESCROW_AUDIT_TABLE is not set.");
        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE")
                               ?? throw new InvalidOperationException("ESCROW_IDEMPOTENCY_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _escrowRepo = new EscrowRepository(dynamoDb, escrowsTable);
        _auditRepo = new EscrowAuditRepository(dynamoDb, auditTable);
        var idempotencyRepo = new EscrowIdempotencyRepository(dynamoDb, idempotencyTable);
        _telebirrGateway = CreateTelebirrGateway();

        // Optional: only wired when the deliveries table is provisioned for this
        // Lambda. Enables the refund-safety check below.
        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE");
        _deliveryRepo = deliveriesTable != null
            ? new DeliveryRepository(dynamoDb, deliveriesTable)
            : null;

        _escrowService = new EscrowService(_escrowRepo, _auditRepo, idempotencyRepo, _telebirrGateway);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public EscrowCronFunction(
        EscrowService escrowService,
        IEscrowRepository escrowRepo,
        IEscrowAuditRepository auditRepo,
        ITelebirrGateway telebirrGateway,
        IDeliveryRepository? deliveryRepo = null)
    {
        _escrowService = escrowService ?? throw new ArgumentNullException(nameof(escrowService));
        _escrowRepo = escrowRepo ?? throw new ArgumentNullException(nameof(escrowRepo));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _telebirrGateway = telebirrGateway ?? throw new ArgumentNullException(nameof(telebirrGateway));
        _deliveryRepo = deliveryRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("EscrowCronFunction invoked.");

        // Parse action from request body
        string? action = null;
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                var payload = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.DictionaryStringString);
                payload?.TryGetValue("action", out action);
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION",
                    "Invalid JSON in request body.");
            }
        }

        // Fallback: check query string parameters
        if (string.IsNullOrEmpty(action))
        {
            request.QueryStringParameters?.TryGetValue("action", out action);
        }

        if (string.IsNullOrEmpty(action))
        {
            return LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION",
                "Missing 'action' field. Expected: auto_release_and_refund, reconciliation.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_release_and_refund" => await HandleAutoReleaseAndRefundAsync(context),
                "reconciliation" => await HandleReconciliationAsync(context),
                _ => LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: auto_release_and_refund, reconciliation.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"EscrowCronFunction error for action '{safeAction}': [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ESCROW_CRON_ERROR",
                $"Cron job '{safeAction}' failed unexpectedly.");
        }
    }

    // ──────────────────────────────────────────────
    // Auto-Release & Auto-Refund
    // ──────────────────────────────────────────────

    /// <summary>
    /// Runs auto-release for confirmed deliveries past the dispute window,
    /// then auto-refund for expired escrows.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAutoReleaseAndRefundAsync(ILambdaContext context)
    {
        var releaseCount = await AutoReleaseAsync(context);
        var refundCount = await AutoRefundAsync(context);
        var completedCount = await CompleteReleasedAsync(context);

        var summary = new Dictionary<string, string>
        {
            { "action", "auto_release_and_refund" },
            { "released", releaseCount.ToString() },
            { "refunded", refundCount.ToString() },
            { "completed", completedCount.ToString() },
            { "completedAt", DateTime.UtcNow.ToString("o") }
        };

        context.Logger.LogInformation(
            $"Auto-release/refund complete: {releaseCount} released, {refundCount} refunded, {completedCount} crash-recovered to completed.");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    /// <summary>
    /// Auto-release: query "escrowed" items where delivery is confirmed and dispute window has passed.
    /// </summary>
    private async Task<int> AutoReleaseAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var escrows = await _escrowRepo.QueryByStatusForCronAsync("escrowed");

        var releaseCount = 0;
        foreach (var escrow in escrows)
        {
            // Per-item isolation: a single poison escrow (throttle, transient error) must not
            // abort the whole batch and starve every other releasable escrow.
            try
            {
                var deliveryConfirmedAt = GetStringOrNull(escrow, "deliveryConfirmedAt");
                if (string.IsNullOrEmpty(deliveryConfirmedAt))
                {
                    continue; // delivery not yet confirmed
                }

                var disputeWindowEndsAt = GetStringOrNull(escrow, "disputeWindowEndsAt");
                if (string.IsNullOrEmpty(disputeWindowEndsAt))
                {
                    continue; // no dispute window set
                }

                // Check if dispute window has passed
                if (!DateTime.TryParse(disputeWindowEndsAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var windowEnd) || now <= windowEnd)
                {
                    continue; // dispute window still open
                }

                var escrowId = GetString(escrow, "escrowId");
                var deliveryId = GetString(escrow, "deliveryId");
                // Hourly-bucketed idempotency key allows a crashed run to safely re-drive the next
                // hour; Telebirr dedupes on the stable per-escrow transactionRef (release:{id}), so
                // re-driving never double-pays. (Review C1/C2: the rotating key no longer risks a
                // double payout now that the Telebirr ref is stable.)
                var hourBucket = now.ToString("yyyyMMddHH");

                context.Logger.LogInformation($"Auto-releasing escrow {escrowId} (dispute window ended).");

                var result = await _escrowService.ReleaseAsync(
                    new ReleaseEscrowRequest
                    {
                        EscrowId = escrowId,
                        Trigger = "delivery_confirmed",
                        DeliveryId = deliveryId,
                        IdempotencyKey = $"system:{escrowId}:auto_release:{hourBucket}"
                    },
                    authenticatedUserId: "system");

                if (result.IsSuccess)
                {
                    releaseCount++;
                }
                else
                {
                    context.Logger.LogError(
                        $"Auto-release failed for {escrowId}: {result.Error?.Code} - {result.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Auto-release: unexpected error processing an escrow: [{ex.GetType().Name}]. Continuing batch.");
            }
        }

        return releaseCount;
    }

    /// <summary>
    /// Auto-refund: query "escrowed" items where expiresAt has passed (7-day timeout).
    /// </summary>
    private async Task<int> AutoRefundAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var nowIso = now.ToString("o");

        // Query escrowed items with expiresAt <= now
        var escrows = await _escrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: nowIso);

        var refundCount = 0;
        foreach (var escrow in escrows)
        {
            // Per-item isolation: one failing refund must not abort the whole batch.
            try
            {
                // Skip items that have a confirmed delivery (those should be auto-released, not refunded)
                var deliveryConfirmedAt = GetStringOrNull(escrow, "deliveryConfirmedAt");
                if (!string.IsNullOrEmpty(deliveryConfirmedAt))
                {
                    continue;
                }

                // Safety net: never refund a buyer whose delivery was actually
                // confirmed. If deliveryConfirmedAt was never stamped (e.g. the
                // inline stamp during confirmation failed transiently), verify the
                // delivery's real status. If it is "delivered", stamp the escrow now
                // so the auto-release cron pays the farmer, and skip the refund.
                if (_deliveryRepo != null
                    && await IsDeliveryConfirmedAndStampAsync(escrow, context))
                {
                    continue;
                }

                var escrowId = GetString(escrow, "escrowId");
                // Hourly-bucketed key allows safe re-drive after a crash; Telebirr dedupes on the
                // stable per-escrow ref (refund:{id}), so re-driving never double-refunds.
                var hourBucket = now.ToString("yyyyMMddHH");

                context.Logger.LogInformation($"Auto-refunding expired escrow {escrowId}.");

                var result = await _escrowService.RefundAsync(
                    new RefundEscrowRequest
                    {
                        EscrowId = escrowId,
                        Trigger = "delivery_timeout",
                        Reason = "Escrow expired: delivery not completed within 7 days.",
                        IdempotencyKey = $"system:{escrowId}:auto_refund:{hourBucket}"
                    },
                    authenticatedUserId: "system");

                if (result.IsSuccess)
                {
                    refundCount++;
                }
                else
                {
                    context.Logger.LogError(
                        $"Auto-refund failed for {escrowId}: {result.Error?.Code} - {result.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Auto-refund: unexpected error processing an escrow: [{ex.GetType().Name}]. Continuing batch.");
            }
        }

        return refundCount;
    }

    /// <summary>
    /// Crash-recovery sweep: query escrows stuck in "released" state and complete them.
    ///
    /// EscrowService.ReleaseAsync performs two sequential DynamoDB writes:
    ///   1. escrowed/disputed → released  (Telebirr ReleaseFunds succeeded)
    ///   2. released → completed
    ///
    /// If Lambda crashes between the two writes the escrow is permanently stuck in "released".
    /// Money has already moved at this point so no Telebirr call is required — we only need
    /// to write the final status and append an audit entry.
    /// </summary>
    private async Task<int> CompleteReleasedAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow.ToString("o");
        var escrows = await _escrowRepo.QueryByStatusForCronAsync("released")
                      ?? new List<Dictionary<string, object>>();

        var completedCount = 0;
        foreach (var escrow in escrows)
        {
            var escrowId = GetString(escrow, "escrowId");
            var createdAt = GetString(escrow, "createdAt");

            context.Logger.LogInformation(
                $"Crash-recovery: completing stuck 'released' escrow {escrowId}.");

            try
            {
                await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "completed", "released",
                    new Dictionary<string, object> { { "completedAt", now } });

                await AppendAuditAsync(escrowId, "complete", "released", "completed",
                    "system", metadata: "crash_recovery");

                completedCount++;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Crash-recovery: failed to complete 'released' escrow {escrowId}: [{ex.GetType().Name}]");
            }
        }

        return completedCount;
    }

    // ──────────────────────────────────────────────
    // Reconciliation
    // ──────────────────────────────────────────────

    /// <summary>
    /// Reconcile stuck "initiated" escrows by checking Telebirr transaction status.
    /// Items older than 30 minutes in "initiated" state are considered stale.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleReconciliationAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var escrows = await _escrowRepo.QueryByStatusForCronAsync("initiated");

        var reconciledToEscrowed = 0;
        var reconciledToCancelled = 0;
        var skipped = 0;

        foreach (var escrow in escrows)
        {
            var createdAtStr = GetString(escrow, "createdAt");
            if (!DateTime.TryParse(createdAtStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var createdAt))
            {
                continue;
            }

            // Only reconcile items older than 30 minutes
            if (now - createdAt < TimeSpan.FromMinutes(ReconciliationStaleMinutes))
            {
                skipped++;
                continue;
            }

            var escrowId = GetString(escrow, "escrowId");
            var telebirrRef = GetStringOrNull(escrow, "telebirrRef");

            // No Telebirr ref: either the async buyer hasn't paid yet (normal — the
            // webhook will stamp the ref + flip to escrowed) or the hold never returned.
            // Do NOT cancel inside the payment window: cancelling and then receiving the
            // webhook would strand a real payment in "cancelled" (the webhook's
            // initiated→escrowed CAS would fail). Only cancel once the window has closed.
            if (string.IsNullOrEmpty(telebirrRef))
            {
                if (now - createdAt < TimeSpan.FromMinutes(NoRefCancelGraceMinutes))
                {
                    skipped++;
                    continue;
                }

                context.Logger.LogInformation(
                    $"Reconciliation: cancelling {escrowId} (no Telebirr ref, payment window elapsed).");

                await _escrowRepo.UpdateStatusAsync(escrowId, createdAtStr, "cancelled", "initiated",
                    new Dictionary<string, object> { { "cancelledAt", now.ToString("o") } });

                await AppendAuditAsync(escrowId, "reconcile_cancel", "initiated", "cancelled",
                    "system", metadata: "No Telebirr reference; payment window elapsed with no completion.");

                reconciledToCancelled++;
                continue;
            }

            // Query Telebirr for the transaction status
            context.Logger.LogInformation($"Reconciliation: checking Telebirr status for {escrowId}.");

            try
            {
                var txStatus = await _telebirrGateway.GetTransactionStatusAsync(telebirrRef);

                if (txStatus.Status is "completed" or "held" or "success")
                {
                    // Telebirr confirms the hold — transition to escrowed
                    await _escrowRepo.UpdateStatusAsync(escrowId, createdAtStr, "escrowed", "initiated",
                        new Dictionary<string, object>
                        {
                            { "telebirrHoldRef", txStatus.TelebirrRef ?? telebirrRef }
                        });

                    await AppendAuditAsync(escrowId, "reconcile_confirm", "initiated", "escrowed",
                        "system", txStatus.TelebirrRef);

                    reconciledToEscrowed++;
                }
                else if (txStatus.Status is "failed" or "not_found" or "expired")
                {
                    // Telebirr has no valid record — cancel
                    await _escrowRepo.UpdateStatusAsync(escrowId, createdAtStr, "cancelled", "initiated",
                        new Dictionary<string, object> { { "cancelledAt", now.ToString("o") } });

                    await AppendAuditAsync(escrowId, "reconcile_cancel", "initiated", "cancelled",
                        "system", metadata: $"Telebirr status: {txStatus.Status}");

                    reconciledToCancelled++;
                }
                else
                {
                    // Unknown/pending status — skip for now
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Reconciliation: Telebirr query failed for {escrowId}: [{ex.GetType().Name}]");
                skipped++;
            }
        }

        var summary = new Dictionary<string, string>
        {
            { "action", "reconciliation" },
            { "reconciledToEscrowed", reconciledToEscrowed.ToString() },
            { "reconciledToCancelled", reconciledToCancelled.ToString() },
            { "skipped", skipped.ToString() },
            { "total", escrows.Count.ToString() },
            { "completedAt", DateTime.UtcNow.ToString("o") }
        };

        context.Logger.LogInformation(
            $"Reconciliation complete: {reconciledToEscrowed} escrowed, {reconciledToCancelled} cancelled, {skipped} skipped out of {escrows.Count}.");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private async Task AppendAuditAsync(
        string escrowId, string action, string fromState, string toState,
        string actor, string? telebirrRef = null, string? metadata = null)
    {
        var auditEntry = new Dictionary<string, object>
        {
            { "auditId", $"audit-{Guid.NewGuid():N}" },
            { "escrowId", escrowId },
            { "action", action },
            { "fromState", fromState },
            { "toState", toState },
            { "actor", actor },
            { "timestamp", DateTime.UtcNow.ToString("o") }
        };

        if (telebirrRef != null)
        {
            auditEntry["telebirrRef"] = telebirrRef;
        }

        if (metadata != null)
        {
            auditEntry["metadata"] = metadata;
        }

        await _auditRepo.AppendAsync(auditEntry);
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

    /// <summary>
    /// Money-safety backstop for auto-refund: if this escrow's linked delivery is
    /// actually "delivered" but the escrow was never stamped (e.g. the inline stamp
    /// at confirmation time failed transiently), stamp deliveryConfirmedAt +
    /// disputeWindowEndsAt now so the auto-release cron pays the farmer, and return
    /// true so the caller SKIPS the refund. Returns false when the delivery is not
    /// confirmed (a genuine timeout → refund proceeds). Best-effort: a stamp failure
    /// still returns true so we never refund a delivered order.
    /// </summary>
    private async Task<bool> IsDeliveryConfirmedAndStampAsync(
        Dictionary<string, object> escrow, ILambdaContext context)
    {
        var deliveryId = GetStringOrNull(escrow, "deliveryId");
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var delivery = await _deliveryRepo.GetByIdAsync(deliveryId);
        var deliveryStatus = delivery != null && delivery.TryGetValue("status", out var ds)
            ? ds?.ToString()
            : null;
        if (deliveryStatus != "delivered")
        {
            return false; // not confirmed — a real timeout, let the refund proceed
        }

        var escrowId = GetString(escrow, "escrowId");
        try
        {
            var confirmedAt = (delivery!.TryGetValue("confirmedAt", out var ca) ? ca?.ToString() : null)
                ?? DateTime.UtcNow.ToString("o");
            var windowEnd = (DateTime.TryParse(confirmedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var cdt) ? cdt : DateTime.UtcNow).AddHours(48).ToString("o");
            await _escrowRepo.UpdateStatusAsync(
                escrowId,
                GetString(escrow, "createdAt"),
                newStatus: "escrowed",
                expectedCurrentStatus: "escrowed",
                additionalFields: new Dictionary<string, object>
                {
                    { "deliveryConfirmedAt", confirmedAt },
                    { "disputeWindowEndsAt", windowEnd }
                });
            context.Logger.LogWarning(
                $"Escrow {escrowId}: delivery {deliveryId} was delivered but escrow was unstamped; "
                + "stamped now to trigger auto-release (refund averted).");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Escrow {escrowId}: failed to stamp a delivered-but-unstamped escrow: [{ex.GetType().Name}]. "
                + "Skipping refund anyway to avoid refunding a delivered order.");
        }
        return true; // delivery was confirmed → never refund
    }

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
}

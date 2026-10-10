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
///   - park_legacy_trucker_legs (no schedule; the server deploy invokes it, ESC-04): queue the
///     trucker legs of escrows completed before ESC-04 without them
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
    // Optional: lets the auto-refund sweep check an ORDER-funded escrow's order
    // before refunding — a 7-day expiry can pass while the trade is legitimately
    // still in flight. Absent env → order-sourced escrows are skipped (fail-safe),
    // never refunded blind.
    private readonly IOrderRepository? _orderRepo;
    // Optional: lets a cancelled stale-funded order give its listing stock back
    // (SIM-13). Absent env → the cancel and refund still happen, stock is not restored.
    private readonly ICropRepository? _cropRepo;
    // Optional: holds the ESC-04 legacy action's done marker. Absent → the action always runs.
    private readonly IEscrowIdempotencyRepository? _idempotencyRepo;
    // Optional: ESC-06 audits an order entering or leaving the admin stuck-order queue.
    private readonly ITransactionAuditRepository? _transactionAuditRepo;

    /// <summary>
    /// Order statuses that mean the trade is actively progressing toward delivery —
    /// its escrow must NOT be refunded by the 7-day sweep; push the expiry instead,
    /// never past the 21-day hold cap (ESC-06).
    /// </summary>
    private static readonly HashSet<string> ProgressingOrderStatuses = new()
    {
        "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing"
    };

    /// <summary>
    /// Order statuses whose escrow may be refunded: stale-funded (farmer never
    /// responded within the expiry window) or terminated by a party.
    /// </summary>
    private static readonly HashSet<string> RefundableOrderStatuses = new()
    {
        "funded", "rejected", "cancelled"
    };

    private const int OrderEscrowExpiryExtensionHours = 48;

    /// <summary>
    /// ESC-02: settlements this run found waiting in the manual payout queue (202
    /// ESCROW_034). Not a failure: the gateway cannot pay out and an admin settles them.
    /// Reset at the start of each auto_release_and_refund run; Lambda runs one invocation
    /// per instance at a time.
    /// </summary>
    private int _manualPayoutsWaiting;

    /// <summary>ESC-06: orders this run put in the admin queue, and overdue lines logged for missing orders.</summary>
    private int _adminQueueEntered;
    private int _adminQueueOverdue;

    /// <summary>ESC-02: page size and page cap of the hourly overdue-payout report.</summary>
    private const int ManualPayoutReportPageSize = 50;
    private const int ManualPayoutReportMaxPages = 20;

    /// <summary>ESC-05: stale settlement locks parked per status per run.</summary>
    private const int StaleLockBatchSize = 50;

    /// <summary>
    /// ESC-04: pending trucker legs read per run (the released partition holds little else), and
    /// gateway calls per run. Rows past the read cap log a truncation alarm line.
    /// </summary>
    private const int TruckerRetryMaxRows = 500;
    private const int MaxTruckerGatewayCallsPerRun = 25;

    /// <summary>
    /// ESC-04: a trucker lock is taken only with more time left than this: the gateway's 30 second
    /// timeout plus a margin for the writes after it, so the 5 minute run is never killed under a lock.
    /// </summary>
    internal static readonly TimeSpan TruckerRetryTimeReserve = TimeSpan.FromSeconds(90);

    /// <summary>ESC-04: legacy rows the one-off action reads in one invocation.</summary>
    private const int LegacyTruckerMaxRows = 5000;

    /// <summary>
    /// ESC-04: the legacy action's marker in the escrow idempotency table (30-day TTL, so the action
    /// reads the completed partition again at most a few times a month). <c>clean</c> after the first
    /// run that found nothing, <c>done</c> after a second clean run at least
    /// <see cref="LegacyTruckerSettleTime"/> later; a done marker skips the read. Two clean runs apart
    /// catch a release that an instance still running the old code completed after the first one.
    /// </summary>
    internal const string LegacyTruckerMarkerKey = "migration:esc-04:park_legacy_trucker_legs";
    internal static readonly TimeSpan LegacyTruckerSettleTime = TimeSpan.FromMinutes(15);

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
        _idempotencyRepo = idempotencyRepo;
        _telebirrGateway = CreateTelebirrGateway();

        // Optional: only wired when the deliveries table is provisioned for this
        // Lambda. Enables the refund-safety check below.
        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE");
        _deliveryRepo = deliveriesTable != null
            ? new DeliveryRepository(dynamoDb, deliveriesTable)
            : null;

        // Optional: only wired when the orders table is provisioned for this Lambda.
        // Enables the order-aware refund gate for order-funded escrows.
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE");
        _orderRepo = ordersTable != null
            ? new OrderRepository(dynamoDb, ordersTable)
            : null;

        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE");
        _cropRepo = cropsTable != null
            ? new CropRepository(dynamoDb, cropsTable)
            : null;

        // Payout destinations and the release-time identity gate need the profiles.
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        var userRepo = usersTable != null ? new UserRepository(dynamoDb, usersTable) : null;

        // ESC-03: settling a dispute verdict first moves its order and delivery to the verdict
        // (repairing a leg the admin verdict could not write; a buyer_wins refund before pickup
        // cancels the order and restores its stock), audited in transaction-audit.
        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        var txAuditRepo = !string.IsNullOrEmpty(txAuditTable)
            ? new TransactionAuditRepository(dynamoDb, txAuditTable)
            : null;

        _transactionAuditRepo = txAuditRepo;
        _escrowService = new EscrowService(_escrowRepo, _auditRepo, idempotencyRepo, _telebirrGateway, userRepo,
            _orderRepo, _deliveryRepo, transactionAuditRepo: txAuditRepo, cropRepo: _cropRepo);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public EscrowCronFunction(
        EscrowService escrowService,
        IEscrowRepository escrowRepo,
        IEscrowAuditRepository auditRepo,
        ITelebirrGateway telebirrGateway,
        IDeliveryRepository? deliveryRepo = null,
        IOrderRepository? orderRepo = null,
        ICropRepository? cropRepo = null,
        IEscrowIdempotencyRepository? idempotencyRepo = null,
        ITransactionAuditRepository? transactionAuditRepo = null)
    {
        _escrowService = escrowService ?? throw new ArgumentNullException(nameof(escrowService));
        _escrowRepo = escrowRepo ?? throw new ArgumentNullException(nameof(escrowRepo));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _telebirrGateway = telebirrGateway ?? throw new ArgumentNullException(nameof(telebirrGateway));
        _deliveryRepo = deliveryRepo;
        _orderRepo = orderRepo;
        _cropRepo = cropRepo;
        _idempotencyRepo = idempotencyRepo;
        _transactionAuditRepo = transactionAuditRepo;
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
                "Missing 'action' field. Expected: auto_release_and_refund, reconciliation, park_legacy_trucker_legs.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_release_and_refund" => await HandleAutoReleaseAndRefundAsync(context),
                "reconciliation" => await HandleReconciliationAsync(context),
                "park_legacy_trucker_legs" => await HandleParkLegacyTruckerLegsAsync(context),
                _ => LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: auto_release_and_refund, reconciliation, park_legacy_trucker_legs.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"EscrowCronFunction error for action '{safeAction}': [{ex.GetType().Name}]");
            // ESC-13: a sweep that died mid-run. ESC-05: the outcome is unknown only when the
            // exception escaped a settlement after its lock (the sweeps catch those per escrow).
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_failed", MoneyAlarmLog.ExceptionOutcome(ex), $"{safeAction}:{ex.GetType().Name}"));
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
        var runStart = DateTime.UtcNow;
        _manualPayoutsWaiting = 0;
        _adminQueueEntered = 0;
        _adminQueueOverdue = 0;
        // First and self-contained, so a failing sweep below can never skip the overdue alarm lines.
        var overdueCount = await ReportOverdueManualPayoutsAsync(context);
        // ESC-06: the admin queue owns the capped order escrows the sweeps no longer see.
        var (queueOverdue, queueCleared) = await ReportAdminQueueAsync(context);
        // ESC-05: before the sweeps, so a parked escrow is already out of their way this run.
        var parkedCount = await ParkStaleSettlementLocksAsync(context);
        var releaseCount = await AutoReleaseAsync(context);
        var refundCount = await AutoRefundAsync(context);
        var (settledReleases, settledRefunds) = await SettleResolvedDisputesAsync(context);
        var completedCount = await CompleteReleasedAsync(context);
        // ESC-04: last, so the money sweeps above keep their share of the 5 minute run.
        var trucker = await RetryPendingTruckerLegsAsync(context, runStart);

        var summary = new Dictionary<string, string>
        {
            { "action", "auto_release_and_refund" },
            { "released", (releaseCount + settledReleases).ToString() },
            { "refunded", (refundCount + settledRefunds).ToString() },
            { "disputesSettled", (settledReleases + settledRefunds).ToString() },
            { "completed", completedCount.ToString() },
            { "manualPayoutsWaiting", _manualPayoutsWaiting.ToString() },
            { "manualPayoutsOverdue", overdueCount.ToString() },
            { "staleLocksParked", parkedCount.ToString() },
            { "truckerLegsPaid", trucker.Paid.ToString() },
            { "truckerLegsQueued", trucker.Queued.ToString() },
            { "truckerLegsBlocked", trucker.Blocked.ToString() },
            { "truckerLegsFailed", trucker.Failed.ToString() },
            { "truckerLegsDeferred", trucker.Deferred.ToString() },
            { "truckerLegsOverdue", trucker.Overdue.ToString() },
            { "adminQueueEntered", _adminQueueEntered.ToString() },
            { "adminQueueOverdue", (queueOverdue + _adminQueueOverdue).ToString() },
            { "adminQueueCleared", queueCleared.ToString() },
            { "completedAt", DateTime.UtcNow.ToString("o") }
        };

        context.Logger.LogInformation(
            $"Auto-release/refund complete: {releaseCount} released, {refundCount} refunded, "
            + $"{settledReleases + settledRefunds} resolved disputes settled, {completedCount} crash-recovered to completed, "
            + $"{_manualPayoutsWaiting} waiting for a manual payout, {overdueCount} manual payout legs overdue, "
            + $"{parkedCount} stale settlement locks parked, trucker legs: {trucker.Paid} paid, {trucker.Queued} queued, "
            + $"{trucker.Blocked} blocked, {trucker.Failed} failed, {trucker.Deferred} deferred, {trucker.Overdue} overdue, "
            + $"{_adminQueueEntered} orders entered the admin queue, "
            + $"{queueOverdue + _adminQueueOverdue} admin queue entries overdue, {queueCleared} cleared.");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    /// <summary>
    /// An admin verdict (POST /admin/disputes/{id}/resolve) records disputeResolution
    /// on the escrow and leaves it in dispute_resolved; this sweep moves the money:
    /// farmer_wins releases, buyer_wins refunds (ESC-03: the lock now accepts
    /// dispute_resolved, and the service moves the order and delivery to the verdict
    /// first). The verdict endpoint refuses anything else since ESC-03; a legacy row with
    /// another value (partial_refund) is left for a manual admin release/refund. Money
    /// moves only here and on the admin settlement routes, never in the admin Lambda itself.
    /// </summary>
    private async Task<(int released, int refunded)> SettleResolvedDisputesAsync(ILambdaContext context)
    {
        List<Dictionary<string, object>> escrows;
        try
        {
            escrows = await _escrowRepo.QueryByStatusForCronAsync("dispute_resolved") ?? [];
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Dispute settlement: query failed [{ex.GetType().Name}]; skipping this run.");
            // ESC-13: no gateway call happened, so the outcome is a definite failure (verdicts wait).
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_failed", MoneyAlarmLog.OutcomeFailed, $"dispute_query:{ex.GetType().Name}"));
            return (0, 0);
        }

        var released = 0;
        var refunded = 0;
        var hourBucket = DateTime.UtcNow.ToString("yyyyMMddHH");

        foreach (var escrow in escrows)
        {
            try
            {
                var escrowId = GetString(escrow, "escrowId");
                var deliveryId = GetStringOrNull(escrow, "deliveryId") ?? "";
                var resolution = GetStringOrNull(escrow, "disputeResolution");

                switch (resolution)
                {
                    case "farmer_wins":
                        {
                            var result = await _escrowService.ReleaseAsync(
                                new ReleaseEscrowRequest
                                {
                                    EscrowId = escrowId,
                                    Trigger = "dispute_resolved",
                                    DeliveryId = deliveryId,
                                    IdempotencyKey = $"system:{escrowId}:dispute_release:{hourBucket}"
                                },
                                authenticatedUserId: "system");
                            if (result.IsSuccess)
                            {
                                released++;
                            }
                            else if (IsManualPayoutWaiting(result.Error))
                            {
                                NoteManualPayoutWaiting(context, escrowId, "dispute release");
                            }
                            else
                            {
                                context.Logger.LogError(
                                    $"Dispute settlement (release) failed for {MoneyAlarmLog.LogId(escrowId)}: {result.Error?.Code} - {MoneyAlarmLog.LogText(result.Error?.Message)}");
                            }
                            break;
                        }
                    case "buyer_wins":
                        {
                            var result = await _escrowService.RefundAsync(
                                new RefundEscrowRequest
                                {
                                    EscrowId = escrowId,
                                    Trigger = "dispute_resolved",
                                    Reason = "Dispute resolved in the buyer's favour.",
                                    IdempotencyKey = $"system:{escrowId}:dispute_refund:{hourBucket}"
                                },
                                authenticatedUserId: "system");
                            if (result.IsSuccess)
                            {
                                refunded++;
                            }
                            else if (IsManualPayoutWaiting(result.Error))
                            {
                                NoteManualPayoutWaiting(context, escrowId, "dispute refund");
                            }
                            else
                            {
                                context.Logger.LogError(
                                    $"Dispute settlement (refund) failed for {MoneyAlarmLog.LogId(escrowId)}: {result.Error?.Code} - {MoneyAlarmLog.LogText(result.Error?.Message)}");
                            }
                            break;
                        }
                    default:
                        context.Logger.LogWarning(
                            $"Dispute {MoneyAlarmLog.LogId(escrowId)} resolved as '{MoneyAlarmLog.LogId(resolution ?? "unset")}': no automatic settlement, admin must release or refund.");
                        break;
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Dispute settlement: unexpected error processing an escrow: [{ex.GetType().Name}]. Continuing batch.");
                // ESC-13: unknown when the exception escaped after the settlement lock (ESC-05).
                context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                    GetStringOrNull(escrow, "escrowId"), "dispute_settlement_exception",
                    MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
            }
        }

        return (released, refunded);
    }

    /// <summary>
    /// Auto-release: query "escrowed" items where delivery is confirmed and dispute window has passed.
    /// </summary>
    private async Task<int> AutoReleaseAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var escrows = await QuerySweepAsync(context, "release_query", () => _escrowRepo.QueryByStatusForCronAsync("escrowed"));

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

                context.Logger.LogInformation($"Auto-releasing escrow {MoneyAlarmLog.LogId(escrowId)} (dispute window ended).");

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
                else if (IsManualPayoutWaiting(result.Error))
                {
                    NoteManualPayoutWaiting(context, escrowId, "auto-release");
                }
                else
                {
                    context.Logger.LogError(
                        $"Auto-release failed for {MoneyAlarmLog.LogId(escrowId)}: {result.Error?.Code} - {MoneyAlarmLog.LogText(result.Error?.Message)}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Auto-release: unexpected error processing an escrow: [{ex.GetType().Name}]. Continuing batch.");
                // ESC-13: unknown when the exception escaped after the settlement lock (ESC-05).
                context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                    GetStringOrNull(escrow, "escrowId"), "auto_release_exception",
                    MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
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
        var escrows = await QuerySweepAsync(context, "refund_query",
            () => _escrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: nowIso));

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

                // ESC-02: a refund already waiting in the manual payout queue is left to the admin.
                // Skipped before the delivery stamp and the order cancel and restock below, which
                // must not run again.
                if (EscrowService.IsManualPayoutOpen(escrow))
                {
                    NoteManualPayoutWaiting(context, GetString(escrow, "escrowId"), "auto-refund");
                    continue;
                }

                // ESC-05: a settlement holds this escrow: one in flight, or one the recovery step
                // just parked while this index copy still lacks the queue marker. Stamping the
                // delivery, cancelling the order or giving its stock back now would act on money
                // another settlement owns, and the refund would only 409 on the lock afterwards.
                if (GetStringOrNull(escrow, SettlementLockFields.Lock) != null)
                {
                    context.Logger.LogInformation(
                        $"ESCROW_SETTLEMENT_IN_FLIGHT_SKIPPED escrowId={MoneyAlarmLog.LogId(GetString(escrow, "escrowId"))} sweep=auto-refund");
                    continue;
                }

                // ESC-05: the index copy can lag the base row (an admin release that just took the
                // lock, a queue write, a status move, a delivery confirmation). One strongly
                // consistent read decides before any side effect below.
                var baseRow = await _escrowRepo.GetByIdAsync(GetString(escrow, "escrowId"), consistentRead: true);
                var skipReason = AutoRefundSkipReason(baseRow);
                if (skipReason != null)
                {
                    context.Logger.LogInformation(
                        $"ESCROW_AUTO_REFUND_SKIPPED escrowId={MoneyAlarmLog.LogId(GetString(escrow, "escrowId"))} reason={skipReason}");
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
                var escrowCreatedAt = GetString(escrow, "createdAt");

                // Order-funded escrows: the 7-day expiry started at FUNDING, so it can
                // lapse while the trade is legitimately still in flight (rural
                // logistics). Gate the refund on the order's real state; fail-safe
                // (skip, never refund blind) when the order can't be checked.
                var isOrderSourced = string.Equals(
                    GetStringOrNull(escrow, "source"), "order", StringComparison.Ordinal);
                var escrowOrderId = GetStringOrNull(escrow, "orderId");
                Dictionary<string, object>? fundedOrder = null;
                var refundedOrderWasFunded = false;
                if (isOrderSourced && !string.IsNullOrEmpty(escrowOrderId))
                {
                    if (_orderRepo == null)
                    {
                        context.Logger.LogWarning(
                            $"Order-funded escrow {MoneyAlarmLog.LogId(escrowId)} expired but ORDERS_TABLE is not "
                            + "wired — skipping refund (fail-safe).");
                        continue;
                    }

                    fundedOrder = await _orderRepo.GetByIdAsync(escrowOrderId);
                    var orderStatus = fundedOrder != null
                        ? GetStringOrNull(fundedOrder, "status") : null;

                    if (orderStatus == null || !RefundableOrderStatuses.Contains(orderStatus))
                    {
                        // ESC-06: never held past funding + 21 days. Before the cap a live trade gets
                        // its expiry pushed (never past the cap); at the cap an admin decides.
                        var capAt = HoldCapAt(escrowCreatedAt);
                        if (capAt == null || now >= capAt.Value)
                        {
                            await EnterHoldCapQueueAsync(baseRow!, fundedOrder, escrowOrderId, now, capAt, context);
                            continue;
                        }

                        if (orderStatus != null && ProgressingOrderStatuses.Contains(orderStatus))
                        {
                            // Trade is live: push the expiry forward and re-check next sweep. Conditional,
                            // so a refund scheduled since the read above keeps its expiry.
                            var extendTo = now.AddHours(OrderEscrowExpiryExtensionHours);
                            await _escrowRepo.TryExtendExpiryAsync(
                                escrowId, escrowCreatedAt, (extendTo < capAt.Value ? extendTo : capAt.Value).ToString("o"));
                            continue;
                        }

                        context.Logger.LogWarning(
                            $"Order-funded escrow {MoneyAlarmLog.LogId(escrowId)} expired but its order "
                            + $"{MoneyAlarmLog.LogId(escrowOrderId)} is '{MoneyAlarmLog.LogId(orderStatus ?? "missing")}', skipping "
                            + "refund; needs reconciliation.");
                        continue;
                    }

                    // ESC-06: a buyer's transport-failure cancel gives the stock back before the
                    // money (the sync cancel restores it best-effort; this repairs a restore that
                    // did not land). Fails closed like the dispute verdict's cancel-then-refund.
                    if (orderStatus == "cancelled"
                        && GetStringOrNull(fundedOrder!, "cancelReason") == PickupDeadline.CancelReason
                        && !await EnsureListingStockRestoredAsync(fundedOrder!, context))
                    {
                        continue;
                    }

                    refundedOrderWasFunded = orderStatus == "funded";

                    // Cancel-then-refund: the version-CAS cancel fences out a
                    // farmer accepting in the read→refund window. On conflict,
                    // skip — the next sweep re-reads the order and (now
                    // 'accepted') pushes the expiry instead of refunding. If the
                    // refund below fails transiently, the order is already
                    // cancelled and the next sweep retries the refund (terminal
                    // orders stay refundable).
                    if (refundedOrderWasFunded && fundedOrder != null)
                    {
                        if (!await TryCancelRefundedOrderAsync(fundedOrder, context))
                        {
                            continue;
                        }

                        // SIM-13: the cancelled order's goods go back on the market.
                        await TryRestoreListingStockAsync(fundedOrder, context);
                    }
                }

                // Hourly-bucketed key allows safe re-drive after a crash; Telebirr dedupes on the
                // stable per-escrow ref (refund:{id}), so re-driving never double-refunds.
                var hourBucket = now.ToString("yyyyMMddHH");

                context.Logger.LogInformation($"Auto-refunding expired escrow {MoneyAlarmLog.LogId(escrowId)}.");

                var scheduledReason = GetStringOrNull(escrow, "refundReason");
                var result = await _escrowService.RefundAsync(
                    new RefundEscrowRequest
                    {
                        EscrowId = escrowId,
                        Trigger = "delivery_timeout",
                        Reason = scheduledReason != null
                            ? $"Order escrow refund: {scheduledReason}."
                            : "Escrow expired: delivery not completed within 7 days.",
                        IdempotencyKey = $"system:{escrowId}:auto_refund:{hourBucket}"
                    },
                    authenticatedUserId: "system");

                if (result.IsSuccess)
                {
                    refundCount++;
                }
                else if (IsManualPayoutWaiting(result.Error))
                {
                    NoteManualPayoutWaiting(context, escrowId, "auto-refund");
                }
                else
                {
                    context.Logger.LogError(
                        $"Auto-refund failed for {MoneyAlarmLog.LogId(escrowId)}: {result.Error?.Code} - {MoneyAlarmLog.LogText(result.Error?.Message)}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Auto-refund: unexpected error processing an escrow: [{ex.GetType().Name}]. Continuing batch.");
                // ESC-13: unknown when the exception escaped after the settlement lock (ESC-05).
                context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                    GetStringOrNull(escrow, "escrowId"), "auto_refund_exception",
                    MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
            }
        }

        return refundCount;
    }

    /// <summary>
    /// ESC-05 (O5): a sweep's status query. A failure skips that sweep for this run with a definite
    /// failure line (no gateway call happened), like the dispute query, so the sweeps after it
    /// still run.
    /// </summary>
    private static async Task<List<Dictionary<string, object>>> QuerySweepAsync(
        ILambdaContext context, string code, Func<Task<List<Dictionary<string, object>>>> query)
    {
        try
        {
            return await query() ?? new List<Dictionary<string, object>>();
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Sweep query {code} failed [{ex.GetType().Name}]; skipping this sweep for this run.");
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_failed", MoneyAlarmLog.OutcomeFailed, $"{code}:{ex.GetType().Name}"));
            return new List<Dictionary<string, object>>();
        }
    }

    /// <summary>
    /// ESC-05 recovery step: a settlement lock older than 15 minutes on an escrowed,
    /// dispute_resolved or (pre-1.9.0) disputed escrow that is not queued has no living owner,
    /// and the gateway may have been called under it. Each one is parked in the manual payout
    /// queue as <c>GATEWAY_OUTCOME_UNKNOWN</c> (<see cref="EscrowService.ParkStaleSettlementAsync"/>).
    /// Never calls the gateway and never releases a lock. A failed query logs a definite failure
    /// line; a failed park logs an unknown one (the lock stays and the next run tries again).
    /// </summary>
    private async Task<int> ParkStaleSettlementLocksAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var lockedBefore = (now - SettlementLockFields.StaleAfter).ToString("o");
        var parked = 0;
        foreach (var status in EscrowService.StaleLockStatuses)
        {
            var rows = await QuerySweepAsync(context, $"lock_recovery_query:{status}",
                () => _escrowRepo.QueryStaleSettlementLocksAsync(status, lockedBefore, StaleLockBatchSize));
            foreach (var row in rows)
            {
                try
                {
                    if (await _escrowService.ParkStaleSettlementAsync(row, now) == EscrowService.StaleSettlementOutcome.Parked)
                    {
                        parked++;
                        context.Logger.LogInformation(
                            $"ESCROW_STALE_LOCK_PARKED escrowId={MoneyAlarmLog.LogId(GetStringOrNull(row, "escrowId"))} status={status}");
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                        GetStringOrNull(row, "escrowId"), "stale_lock_recovery_failed",
                        MoneyAlarmLog.OutcomeUnknown, ex.GetType().Name));
                }
            }
        }

        return parked;
    }

    /// <summary>
    /// ESC-05: why the auto-refund sweep must leave this escrow alone, read from the base row (not
    /// the index copy), or null when it may go on: gone, no longer escrowed, an open manual payout,
    /// a settlement lock, or a delivery confirmed meanwhile (the release sweep pays it).
    /// </summary>
    private static string? AutoRefundSkipReason(Dictionary<string, object>? current)
    {
        if (current == null)
        {
            return "missing";
        }

        if (GetString(current, "status") != "escrowed")
        {
            return "status";
        }

        if (EscrowService.IsManualPayoutOpen(current))
        {
            return "queued";
        }

        if (GetStringOrNull(current, SettlementLockFields.Lock) != null)
        {
            return "locked";
        }

        return string.IsNullOrEmpty(GetStringOrNull(current, "deliveryConfirmedAt")) ? null : "confirmed";
    }

    /// <summary>ESC-02: 202 ESCROW_034 means the settlement sits in the manual payout queue.</summary>
    private static bool IsManualPayoutWaiting(EscrowResultError? error)
        => error != null && error.HttpStatus == 202 && error.Code == "ESCROW_034";

    /// <summary>
    /// Logged at Information with a stable token, never as an error: the gateway cannot pay
    /// out and the admin queue owns the settlement. No alarm counts it; the overdue report
    /// below feeds the alarm.
    /// </summary>
    private void NoteManualPayoutWaiting(ILambdaContext context, string escrowId, string sweep)
    {
        _manualPayoutsWaiting++;
        context.Logger.LogInformation($"ESCROW_MANUAL_PAYOUT_WAITING escrowId={MoneyAlarmLog.LogId(escrowId)} sweep={sweep}");
    }

    /// <summary>
    /// ESC-02 producer of the ESC-13 <c>MANUAL_PAYOUT_OVERDUE</c> alarm: one line per pending
    /// leg of every manual payout queued more than 24 hours ago, every hourly run (the
    /// metric filter sits on this Lambda's log group). The index is oldest first, so paging
    /// stops at the first younger row. An open row without a pending leg cannot close from
    /// the admin page, so it gets a line too. Never throws: a failed query logs a
    /// settlement-failure line instead, so the operator still hears about it.
    /// </summary>
    private async Task<int> ReportOverdueManualPayoutsAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var lines = 0;
        try
        {
            Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
            for (var page = 1; page <= ManualPayoutReportMaxPages; page++)
            {
                var (items, lastKey) = await _escrowRepo.QueryOpenManualPayoutsAsync(ManualPayoutReportPageSize, startKey);
                foreach (var item in items)
                {
                    var queuedAt = GetStringOrNull(item, ManualPayoutFields.QueuedAt);
                    var age = DateTime.TryParse(queuedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var queued)
                        ? now - queued.ToUniversalTime()
                        : MoneyAlarmLog.QueueAlarmAfter; // unreadable time: treat as overdue
                    if (age < MoneyAlarmLog.QueueAlarmAfter)
                    {
                        return lines;
                    }

                    var escrowId = GetStringOrNull(item, "escrowId");
                    var op = GetStringOrNull(item, ManualPayoutFields.Op) ?? "unknown";
                    var ageHours = (int)age.TotalHours;
                    var pending = ManualPayoutFields.AllLegs
                        .Where(leg => GetStringOrNull(item, ManualPayoutFields.StatusAttr(leg)) == ManualPayoutFields.LegPending)
                        .ToList();
                    foreach (var leg in pending.Count > 0 ? pending : ["no_pending_leg"])
                    {
                        context.Logger.LogWarning(MoneyAlarmLog.ManualPayoutOverdue(escrowId, $"{op}:{leg}", ageHours));
                        lines++;
                    }
                }

                if (lastKey == null)
                {
                    return lines;
                }

                startKey = lastKey;
            }

            context.Logger.LogWarning(
                $"Manual payout report stopped after {ManualPayoutReportMaxPages} pages; older entries may be unreported.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_failed", MoneyAlarmLog.OutcomeFailed, $"manual_payout_query:{ex.GetType().Name}"));
        }

        return lines;
    }

    /// <summary>
    /// SIM-13: a cancelled stale-funded order gives its listing stock back. Idempotency
    /// per order and the race fence live in the repository. Best-effort: the refund
    /// proceeds regardless, and a failure is logged for the next sweep to notice.
    /// </summary>
    private async Task TryRestoreListingStockAsync(
        Dictionary<string, object> order, ILambdaContext context)
    {
        if (_cropRepo == null)
        {
            return;
        }

        var orderId = GetString(order, "orderId");
        var listingId = GetStringOrNull(order, "listingId");
        var quintals = double.TryParse(GetStringOrNull(order, "quantity"),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture,
            out var kg) ? kg / 100.0 : 0.0;
        if (string.IsNullOrEmpty(listingId) || quintals <= 0)
        {
            return;
        }

        try
        {
            var restored = await _cropRepo.TryRestoreStockAsync(listingId, orderId, quintals);
            context.Logger.LogInformation(restored
                ? $"Listing {MoneyAlarmLog.LogId(listingId)}: +{quintals} quintals restored (order {MoneyAlarmLog.LogId(orderId)} cancelled by escrow refund)"
                : $"Listing {MoneyAlarmLog.LogId(listingId)}: nothing to restore for order {MoneyAlarmLog.LogId(orderId)}");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"ORDER_STOCK_RESTORE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// Cancel a stale-funded order BEFORE refunding its escrow. The version-CAS
    /// write doubles as the race fence: if the farmer accepted in the meantime,
    /// the version moved, the cancel conflicts, and the caller skips the refund
    /// (the next sweep re-reads the order and pushes the expiry instead).
    /// Returns true only when the order is now cancelled.
    /// </summary>
    private async Task<bool> TryCancelRefundedOrderAsync(
        Dictionary<string, object> order, ILambdaContext context)
    {
        var orderId = GetString(order, "orderId");
        try
        {
            var version = 1;
            if (order.TryGetValue("version", out var vObj))
            {
                _ = int.TryParse(vObj?.ToString(), out version);
            }

            var result = await _orderRepo!.UpdateAsync(orderId, new Dictionary<string, object>
            {
                { "status", "cancelled" },
                { "cancelledAt", DateTime.UtcNow.ToString("o") },
                { "cancelReason", "escrow_refunded" }
            }, version);

            if (!result.IsSuccess)
            {
                context.Logger.LogWarning(
                    $"ORDER_ESCROW_REFUND_ORDER_CANCEL_FAILED orderId={MoneyAlarmLog.LogId(orderId)}: "
                    + $"{MoneyAlarmLog.LogText(result.Error?.Message)}; refund deferred to the next sweep.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"ORDER_ESCROW_REFUND_ORDER_CANCEL_FAILED orderId={MoneyAlarmLog.LogId(orderId)}: "
                + $"[{ex.GetType().Name}] — refund deferred to the next sweep.");
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // ESC-06: 21-day hold cap and the admin stuck-order queue
    // ──────────────────────────────────────────────

    /// <summary>ESC-06: an order escrow is never extended past funding + 21 days.</summary>
    private static readonly TimeSpan HoldCap = TimeSpan.FromDays(21);

    /// <summary>ESC-06: page size and page cap of the hourly admin queue report.</summary>
    private const int AdminQueueReportPageSize = 50;
    private const int AdminQueueReportMaxPages = 20;

    /// <summary>
    /// ESC-06: the hold cap of an order escrow, whose <c>createdAt</c> is the funding time. Null when
    /// that cannot be read, which the caller treats as capped.
    /// </summary>
    private static DateTime? HoldCapAt(string createdAt)
        => ParseUtc(createdAt) is { } fundedAt ? fundedAt + HoldCap : null;

    /// <summary>
    /// ESC-06: an order escrow reached its hold cap while its order is not refundable (still moving,
    /// disputed, or anything else the sweep cannot settle), so an admin decides. The order enters the
    /// admin queue (<see cref="IOrderRepository.TryEnterAdminQueueAsync"/>, not versioned), with a
    /// transaction-audit row <c>admin_queue_entered</c> and an escrow-audit row <c>hold_cap_reached</c>
    /// keyed on the queue time (a repeat writes nothing). The escrow is stamped
    /// <c>holdCapReachedAt</c> LAST: the stamp takes it out of the sweep windows, so it is written only
    /// once the queue owns the order. A missing order has no row to queue: its escrow is not stamped,
    /// stays in the windows and logs <c>ADMIN_QUEUE_OVERDUE ... reason=order_missing</c> every run
    /// once the cap is 24 hours old.
    /// </summary>
    private async Task EnterHoldCapQueueAsync(
        Dictionary<string, object> escrow, Dictionary<string, object>? order, string orderId,
        DateTime now, DateTime? capAt, ILambdaContext context)
    {
        var escrowId = GetString(escrow, "escrowId");
        if (order == null)
        {
            var overdueFor = capAt == null ? MoneyAlarmLog.QueueAlarmAfter : now - capAt.Value;
            if (overdueFor >= MoneyAlarmLog.QueueAlarmAfter)
            {
                context.Logger.LogWarning(MoneyAlarmLog.AdminQueueOverdue(
                    orderId, AdminQueueFields.ReasonOrderMissing, (int)overdueFor.TotalHours));
                _adminQueueOverdue++;
            }

            return;
        }

        var orderStatus = GetString(order, "status");
        string? queuedAt = now.ToString("o");
        if (await _orderRepo!.TryEnterAdminQueueAsync(
                orderId, AdminQueueFields.ReasonHoldCap, escrowId, queuedAt, orderStatus))
        {
            _adminQueueEntered++;
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, context, orderId, "order", orderId,
                "admin_queue_entered", orderStatus, orderStatus, AuditContext.ForSystem(),
                metadata: $"reason={AdminQueueFields.ReasonHoldCap};escrowId={escrowId}");
            context.Logger.LogInformation(
                $"ORDER_ESCROW_HOLD_CAPPED orderId={MoneyAlarmLog.LogId(orderId)} escrowId={MoneyAlarmLog.LogId(escrowId)}");
        }
        else
        {
            // Queued by an earlier run (whose stamp below may not have landed), or the order moved on
            // since it was read: then the next run decides again.
            var current = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            queuedAt = current == null ? null : GetStringOrNull(current, AdminQueueFields.QueuedAt);
            if (queuedAt == null)
            {
                return;
            }
        }

        await _auditRepo.AppendIfAbsentAsync(new Dictionary<string, object>
        {
            { "auditId", EscrowAuditRepository.DeterministicAuditId("hold_cap_reached", queuedAt) },
            { "escrowId", escrowId },
            { "action", "hold_cap_reached" },
            { "fromState", "escrowed" },
            { "toState", "escrowed" },
            { "actor", "system" },
            { "timestamp", queuedAt },
            { "metadata", $"orderId={orderId};reason={AdminQueueFields.ReasonHoldCap}" }
        });

        if (GetStringOrNull(escrow, AdminQueueFields.HoldCapReachedAt) == null)
        {
            await _escrowRepo.UpdateStatusAsync(escrowId, GetString(escrow, "createdAt"), "escrowed", "escrowed",
                new Dictionary<string, object> { { AdminQueueFields.HoldCapReachedAt, queuedAt } });
        }
    }

    /// <summary>
    /// ESC-06: before the refund of a buyer's transport-failure cancel, make sure the cancelled order's
    /// quintals are back on its listing (idempotent per order). The dispute verdict's rules: nothing to
    /// give back (no listing, a quantity that is not positive) or a listing that is gone counts as
    /// done; a restore that keeps conflicting or throws logs <c>ORDER_STOCK_RESTORE_FAILED</c> and
    /// blocks this run's refund, and the next run retries. Without a crops table there is nothing to
    /// check, and the refund goes ahead.
    /// </summary>
    private async Task<bool> EnsureListingStockRestoredAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        var orderId = GetString(order, "orderId");
        var listingId = GetStringOrNull(order, "listingId");
        var quintals = double.TryParse(GetStringOrNull(order, "quantity"), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var kg) ? kg / 100.0 : 0.0;
        if (_cropRepo == null || string.IsNullOrEmpty(listingId) || quintals <= 0)
        {
            return true;
        }

        string reason;
        try
        {
            var result = await _cropRepo.RestoreStockAsync(listingId, orderId, quintals);
            if (result is StockRestoreResult.Restored or StockRestoreResult.AlreadyRestored or StockRestoreResult.ListingNotFound)
            {
                return true;
            }

            reason = "restock:conflict";
        }
        catch (Exception ex)
        {
            reason = $"restock:[{ex.GetType().Name}]";
        }

        context.Logger.LogWarning(
            $"ORDER_STOCK_RESTORE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} "
            + $"reason={MoneyAlarmLog.LogText(reason)}; refund deferred to the next sweep.");
        return false;
    }

    /// <summary>What <see cref="ReviewAdminQueueEntryAsync"/> did with one admin queue entry.</summary>
    private enum AdminQueueReview
    {
        Waiting,
        Overdue,
        Cleared
    }

    /// <summary>
    /// ESC-06 producer of the ESC-13 <c>ADMIN_QUEUE_OVERDUE</c> alarm, and the owner of the queued
    /// orders: their capped escrows are out of the sweep windows. Every hourly run reads each entry
    /// (oldest first, pages of 50, at most 20 pages) and decides it with
    /// <see cref="ReviewAdminQueueEntryAsync"/>. Never throws: a failed query logs a definite
    /// settlement-failure line (<c>admin_queue_query</c>); an entry that cannot be reviewed is never
    /// cleared, and logs the overdue line once it is 24 hours old.
    /// </summary>
    private async Task<(int Overdue, int Cleared)> ReportAdminQueueAsync(ILambdaContext context)
    {
        if (_orderRepo == null)
        {
            return (0, 0);
        }

        var now = DateTime.UtcNow;
        var overdue = 0;
        var cleared = 0;
        try
        {
            Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
            for (var page = 1; page <= AdminQueueReportMaxPages; page++)
            {
                var (items, lastKey) = await _orderRepo.QueryAdminQueueAsync(AdminQueueReportPageSize, startKey);
                foreach (var item in items)
                {
                    AdminQueueReview review;
                    try
                    {
                        review = await ReviewAdminQueueEntryAsync(item, now, context);
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogWarning(
                            $"Admin queue entry {MoneyAlarmLog.LogId(GetStringOrNull(item, "orderId"))} could not be reviewed: [{ex.GetType().Name}]");
                        review = LogIfOverdue(item, ParseUtc(GetStringOrNull(item, AdminQueueFields.QueuedAt)), now, context);
                    }

                    if (review == AdminQueueReview.Overdue)
                    {
                        overdue++;
                    }
                    else if (review == AdminQueueReview.Cleared)
                    {
                        cleared++;
                    }
                }

                if (lastKey == null)
                {
                    return (overdue, cleared);
                }

                startKey = lastKey;
            }

            context.Logger.LogWarning(
                $"Admin queue report stopped after {AdminQueueReportMaxPages} pages; newer entries may be unreported.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_failed", MoneyAlarmLog.OutcomeFailed, $"admin_queue_query:{ex.GetType().Name}"));
        }

        return (overdue, cleared);
    }

    /// <summary>
    /// ESC-06: one admin queue entry, read with its escrow, both strongly consistent.
    /// - Cleared only on escrow evidence: the escrow is gone or no longer <c>escrowed</c>, waits in
    ///   the manual payout queue, or has a confirmed delivery (the release sweep pays it). Never on
    ///   the order status alone.
    /// - Re-armed when the order ended without its exit: a <c>cancelled</c> or <c>rejected</c> order
    ///   whose escrow has no refund scheduled gets one (the refund sweep then sees the escrow again);
    ///   otherwise an escrow linked to a delivery that is <c>delivered</c> gets the stamp repair,
    ///   whatever the order status says (it can lag the delivery).
    /// - Otherwise, with no refund on its way, the entry logs one <c>ADMIN_QUEUE_OVERDUE</c> line once
    ///   it has been due for 24 hours. It is due from its queue time, or from the order's
    ///   <c>pickupDeadlineAt</c> when the order is still <c>accepted</c> and that is later (a trip that
    ///   departs after day 21 is not overdue yet).
    /// </summary>
    private async Task<AdminQueueReview> ReviewAdminQueueEntryAsync(
        Dictionary<string, object> item, DateTime now, ILambdaContext context)
    {
        var orderId = GetString(item, "orderId");
        var queuedAt = GetStringOrNull(item, AdminQueueFields.QueuedAt);
        var escrowId = GetStringOrNull(item, AdminQueueFields.EscrowId) ?? $"escrow-order-{orderId}";
        var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);

        if (escrow == null
            || GetString(escrow, "status") != "escrowed"
            || EscrowService.IsManualPayoutOpen(escrow)
            || !string.IsNullOrEmpty(GetStringOrNull(escrow, "deliveryConfirmedAt")))
        {
            if (queuedAt == null || !await _orderRepo!.TryLeaveAdminQueueAsync(orderId, queuedAt))
            {
                return AdminQueueReview.Waiting;
            }

            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, context, orderId, "order", orderId,
                "admin_queue_cleared", null, null, AuditContext.ForSystem(),
                metadata: $"reason={GetStringOrNull(item, AdminQueueFields.Reason)};escrowStatus={(escrow == null ? "missing" : GetString(escrow, "status"))}");
            context.Logger.LogInformation(
                $"Admin queue entry for order {MoneyAlarmLog.LogId(orderId)} cleared: escrow {MoneyAlarmLog.LogId(escrowId)} moved on.");
            return AdminQueueReview.Cleared;
        }

        var order = await _orderRepo!.GetByIdAsync(orderId, consistentRead: true);
        var orderStatus = order == null ? null : GetStringOrNull(order, "status");
        var exitArmed = !string.IsNullOrEmpty(GetStringOrNull(escrow, "refundReason"));
        if (!exitArmed && orderStatus is "cancelled" or "rejected")
        {
            exitArmed = await _escrowService.ScheduleRefundForOrderAsync(orderId, $"order_{orderStatus}", "system");
            context.Logger.LogInformation(
                $"Admin queue entry for order {MoneyAlarmLog.LogId(orderId)}: refund {(exitArmed ? "scheduled" : "not scheduled")} "
                + $"for its {orderStatus} order.");
        }
        else if (!exitArmed && !string.IsNullOrEmpty(GetStringOrNull(escrow, "deliveryId")))
        {
            // The order status can lag its delivery: a linked delivery that is delivered gets the
            // escrow stamped, so the release sweep sees it again.
            exitArmed = await IsDeliveryConfirmedAndStampAsync(escrow, context);
        }

        if (exitArmed)
        {
            return AdminQueueReview.Waiting;
        }

        var dueAt = ParseUtc(queuedAt);
        if (dueAt != null && orderStatus == "accepted"
            && PickupDeadline.TryParse(GetStringOrNull(order!, PickupDeadline.Field), out var pickupBy)
            && pickupBy > dueAt.Value)
        {
            dueAt = pickupBy;
        }

        return LogIfOverdue(item, dueAt, now, context);
    }

    /// <summary>
    /// ESC-06: log <c>ADMIN_QUEUE_OVERDUE</c> for an entry due since <paramref name="dueAt"/> once that is
    /// 24 hours ago; <c>ageHours</c> counts whole hours since it became due. An unreadable time counts
    /// as overdue.
    /// </summary>
    private static AdminQueueReview LogIfOverdue(
        Dictionary<string, object> item, DateTime? dueAt, DateTime now, ILambdaContext context)
    {
        var dueFor = dueAt == null ? MoneyAlarmLog.QueueAlarmAfter : now - dueAt.Value;
        if (dueFor < MoneyAlarmLog.QueueAlarmAfter)
        {
            return AdminQueueReview.Waiting;
        }

        context.Logger.LogWarning(MoneyAlarmLog.AdminQueueOverdue(
            GetStringOrNull(item, "orderId"), GetStringOrNull(item, AdminQueueFields.Reason), (int)dueFor.TotalHours));
        return AdminQueueReview.Overdue;
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
    /// to write the final status and append an audit entry. ESC-04: a released escrow whose
    /// trucker leg is still owed is not stuck: the query leaves it to the trucker retry, and the
    /// repository refuses to complete it.
    /// </summary>
    private async Task<int> CompleteReleasedAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow.ToString("o");
        var escrows = await QuerySweepAsync(context, "complete_query",
            () => _escrowRepo.QueryByStatusForCronAsync("released"));

        var completedCount = 0;
        foreach (var escrow in escrows)
        {
            var escrowId = GetString(escrow, "escrowId");
            var createdAt = GetString(escrow, "createdAt");

            context.Logger.LogInformation(
                $"Crash-recovery: completing stuck 'released' escrow {MoneyAlarmLog.LogId(escrowId)}.");

            try
            {
                await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "completed", "released",
                    new Dictionary<string, object> { { "completedAt", now } });

                // Keyed on completedAt like the escrow service's complete row (ESC-02), so a
                // manual payout replay that re-ensures it never writes a second one.
                await _auditRepo.AppendIfAbsentAsync(new Dictionary<string, object>
                {
                    { "auditId", EscrowAuditRepository.DeterministicAuditId("complete", now) },
                    { "escrowId", escrowId },
                    { "action", "complete" },
                    { "fromState", "released" },
                    { "toState", "completed" },
                    { "actor", "system" },
                    { "timestamp", now },
                    { "metadata", "crash_recovery" }
                });

                completedCount++;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Crash-recovery: failed to complete 'released' escrow {MoneyAlarmLog.LogId(escrowId)}: [{ex.GetType().Name}]");
            }
        }

        return completedCount;
    }

    /// <summary>What the trucker retry did this run (ESC-04).</summary>
    private sealed class TruckerRetryCounts
    {
        public int Paid { get; set; }
        public int Queued { get; set; }
        public int Blocked { get; set; }
        public int Failed { get; set; }
        public int Deferred { get; set; }
        public int Overdue { get; set; }
    }

    /// <summary>
    /// ESC-04 trucker retry, last in the run: every released escrow whose trucker leg is still owed
    /// and not queued, oldest first (<see cref="EscrowService.RetryTruckerLegAsync"/>). A leg that
    /// went pending in this run waits for the next one. A trucker lock (and so a gateway call) is
    /// taken only while the run has more than <see cref="TruckerRetryTimeReserve"/> left and fewer
    /// than <see cref="MaxTruckerGatewayCallsPerRun"/> calls were made. Every leg still owed more
    /// than 24 hours after it went pending logs <c>TRUCKER_PAYOUT_OVERDUE</c>, so none waits silently.
    /// A failed query logs <c>sweep_failed</c>; rows past the read cap log <c>sweep_truncated</c>.
    /// </summary>
    private async Task<TruckerRetryCounts> RetryPendingTruckerLegsAsync(ILambdaContext context, DateTime runStart)
    {
        var counts = new TruckerRetryCounts();
        var rows = await QuerySweepAsync(context, "trucker_retry_query",
            () => _escrowRepo.QueryPendingTruckerLegsAsync(TruckerRetryMaxRows + 1));
        if (rows.Count > TruckerRetryMaxRows)
        {
            // O5: the rows past the cap are neither retried nor reported overdue this run.
            context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                null, "sweep_truncated", MoneyAlarmLog.OutcomeFailed, $"trucker_retry_query:{TruckerRetryMaxRows}"));
            context.Logger.LogWarning(
                $"Trucker retry read stopped after {TruckerRetryMaxRows} rows; the newer pending trucker legs wait for a later run.");
            rows = rows.Take(TruckerRetryMaxRows).ToList();
        }

        var now = DateTime.UtcNow;
        var gatewayCalls = 0;
        foreach (var row in rows)
        {
            var escrowId = GetStringOrNull(row, "escrowId");
            EscrowService.TruckerRetryResult result;
            if (ParseUtc(GetStringOrNull(row, TruckerPayoutFields.PendingSince)) is { } since && since >= runStart)
            {
                result = new(EscrowService.TruckerRetryOutcome.Deferred, "pending_this_run");
            }
            else
            {
                var mayCallGateway = gatewayCalls < MaxTruckerGatewayCallsPerRun
                                     && context.RemainingTime > TruckerRetryTimeReserve;
                try
                {
                    result = await _escrowService.RetryTruckerLegAsync(row, now, mayCallGateway);
                }
                catch (Exception ex)
                {
                    // Unknown when the exception escaped after the trucker lock (ESC-05 rule).
                    context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                        escrowId, "trucker_retry_exception", MoneyAlarmLog.ExceptionOutcome(ex), ex.GetType().Name));
                    result = new(EscrowService.TruckerRetryOutcome.Failed, "error");
                }

                if (result.Outcome is EscrowService.TruckerRetryOutcome.Paid or EscrowService.TruckerRetryOutcome.Failed
                    || (result.Outcome == EscrowService.TruckerRetryOutcome.Queued && result.Reason is not ("stale_lock" or "legacy")))
                {
                    gatewayCalls++;
                }
            }

            switch (result.Outcome)
            {
                case EscrowService.TruckerRetryOutcome.Paid:
                    counts.Paid++;
                    break;
                case EscrowService.TruckerRetryOutcome.Queued:
                    counts.Queued++;
                    break;
                case EscrowService.TruckerRetryOutcome.Blocked:
                    counts.Blocked++;
                    break;
                case EscrowService.TruckerRetryOutcome.Failed:
                    counts.Failed++;
                    break;
                case EscrowService.TruckerRetryOutcome.Deferred:
                case EscrowService.TruckerRetryOutcome.InFlight:
                    counts.Deferred++;
                    break;
            }

            if (result.Outcome is EscrowService.TruckerRetryOutcome.Skipped or EscrowService.TruckerRetryOutcome.Paid
                or EscrowService.TruckerRetryOutcome.Queued)
            {
                continue;
            }

            var pendingSince = ParseUtc(GetStringOrNull(row, TruckerPayoutFields.PendingSince))
                               ?? ParseUtc(GetStringOrNull(row, "releasedAt"))
                               ?? ParseUtc(GetStringOrNull(row, "updatedAt"));
            var age = pendingSince is { } at ? now - at : MoneyAlarmLog.QueueAlarmAfter; // unreadable time: overdue
            if (age >= MoneyAlarmLog.QueueAlarmAfter)
            {
                context.Logger.LogWarning(MoneyAlarmLog.TruckerPayoutOverdue(escrowId, result.Reason ?? "pending", (int)age.TotalHours));
                counts.Overdue++;
            }
        }

        return counts;
    }

    /// <summary>
    /// ESC-04 legacy action (<c>park_legacy_trucker_legs</c>, invoked by the server deploy, never
    /// scheduled): a <c>completed</c> escrow still flagged <c>truckerPayoutPending</c> was completed
    /// before ESC-04 without its trucker leg, which the old runbook said to pay by hand. Each one goes
    /// back to <c>released</c> with the leg in the manual payout queue as
    /// <c>GATEWAY_OUTCOME_UNKNOWN</c> (<see cref="EscrowService.ParkLegacyTruckerLegAsync"/>), never
    /// retried. Idempotent: a parked row is no longer in the query. Answers 500 when a row could not
    /// be parked, so the deploy step fails visibly; a re-run picks it up. Bounded: once the marker
    /// (<see cref="LegacyTruckerMarkerKey"/>) is done, a run reads nothing. A release completed by an
    /// instance still running the old code after a run waits for the next deploy's run. A released
    /// legacy row (no pending time) is parked by the hourly trucker retry instead.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleParkLegacyTruckerLegsAsync(ILambdaContext context)
    {
        var marker = await ReadLegacyMarkerAsync(context);
        var markerStatus = marker != null ? GetStringOrNull(marker, "status") : null;
        if (markerStatus == "done")
        {
            context.Logger.LogInformation("Legacy trucker legs: done marker found, nothing read.");
            return LegacySummary(200, found: 0, parked: 0, failed: 0, truncated: false, "done", skipped: true);
        }

        var rows = await _escrowRepo.QueryLegacyTruckerLegsAsync(LegacyTruckerMaxRows);
        var parked = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            try
            {
                if (await _escrowService.ParkLegacyTruckerLegAsync(row))
                {
                    parked++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                context.Logger.LogError(MoneyAlarmLog.SettlementFailed(
                    GetStringOrNull(row, "escrowId"), "legacy_trucker_park_failed", MoneyAlarmLog.OutcomeFailed, ex.GetType().Name));
            }
        }

        var truncated = rows.Count >= LegacyTruckerMaxRows;
        if (rows.Count == 0 && failed == 0)
        {
            markerStatus = await AdvanceLegacyMarkerAsync(marker, context) ?? markerStatus;
        }

        context.Logger.LogInformation(
            $"Legacy trucker legs: {rows.Count} found, {parked} parked, {failed} failed, marker {markerStatus ?? "none"}.");
        return LegacySummary(failed > 0 ? 500 : 200, rows.Count, parked, failed, truncated, markerStatus, skipped: false);
    }

    /// <summary>The legacy action's marker, or null (none, no idempotency table, or a failed read: the action runs).</summary>
    private async Task<Dictionary<string, object>?> ReadLegacyMarkerAsync(ILambdaContext context)
    {
        if (_idempotencyRepo == null)
        {
            return null;
        }

        try
        {
            return await _idempotencyRepo.GetAsync(LegacyTruckerMarkerKey);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Legacy trucker legs: marker read failed [{ex.GetType().Name}]; running the read.");
            return null;
        }
    }

    /// <summary>
    /// A clean run (nothing found) moves the marker from none to <c>clean</c>, and from <c>clean</c> to
    /// <c>done</c> once <see cref="LegacyTruckerSettleTime"/> passed since it was written. Returns the
    /// new status, or null when nothing was written. A failed write only logs: the next run tries again.
    /// </summary>
    private async Task<string?> AdvanceLegacyMarkerAsync(Dictionary<string, object>? marker, ILambdaContext context)
    {
        if (_idempotencyRepo == null)
        {
            return null;
        }

        try
        {
            if (marker == null)
            {
                await _idempotencyRepo.StoreAsync(LegacyTruckerMarkerKey, "-", "clean");
                return "clean";
            }

            var cleanAt = ParseUtc(GetStringOrNull(marker, "createdAt"));
            if (GetStringOrNull(marker, "status") == "clean" && cleanAt is { } at
                && DateTime.UtcNow - at >= LegacyTruckerSettleTime)
            {
                await _idempotencyRepo.UpdateAsync(LegacyTruckerMarkerKey, "done");
                return "done";
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Legacy trucker legs: marker write failed [{ex.GetType().Name}].");
        }

        return null;
    }

    private static APIGatewayProxyResponse LegacySummary(
        int statusCode, int found, int parked, int failed, bool truncated, string? marker, bool skipped) => new()
    {
        StatusCode = statusCode,
        Body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "action", "park_legacy_trucker_legs" },
            { "found", found.ToString() },
            { "parked", parked.ToString() },
            { "failed", failed.ToString() },
            { "truncated", truncated ? "true" : "false" },
            { "marker", marker ?? "none" },
            { "skipped", skipped ? "true" : "false" },
            { "completedAt", DateTime.UtcNow.ToString("o") }
        }, AppJsonContext.Default.DictionaryStringString),
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
    };

    private static DateTime? ParseUtc(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

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
                    $"Reconciliation: cancelling {MoneyAlarmLog.LogId(escrowId)} (no Telebirr ref, payment window elapsed).");

                await _escrowRepo.UpdateStatusAsync(escrowId, createdAtStr, "cancelled", "initiated",
                    new Dictionary<string, object> { { "cancelledAt", now.ToString("o") } });

                await AppendAuditAsync(escrowId, "reconcile_cancel", "initiated", "cancelled",
                    "system", metadata: "No Telebirr reference; payment window elapsed with no completion.");

                reconciledToCancelled++;
                continue;
            }

            // Query Telebirr for the transaction status
            context.Logger.LogInformation($"Reconciliation: checking Telebirr status for {MoneyAlarmLog.LogId(escrowId)}.");

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
                    $"Reconciliation: Telebirr query failed for {MoneyAlarmLog.LogId(escrowId)}: [{ex.GetType().Name}]");
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
                $"Escrow {MoneyAlarmLog.LogId(escrowId)}: delivery {MoneyAlarmLog.LogId(deliveryId)} was delivered but escrow was unstamped; "
                + "stamped now to trigger auto-release (refund averted).");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Escrow {MoneyAlarmLog.LogId(escrowId)}: failed to stamp a delivered-but-unstamped escrow: [{ex.GetType().Name}]. "
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

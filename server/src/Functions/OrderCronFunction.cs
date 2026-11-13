using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using DynamoValue = Amazon.DynamoDBv2.Model.AttributeValue;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// EventBridge scheduled event handler for order background jobs.
/// Routes based on "action" field in the request body:
///   - auto_cancel_expired (hourly): cancel unpaid orders older than 24h (T1) and fail payments
///     not verified in 24h (T2); both windows from SSM since ESC-16 (<see cref="OrderTimings"/>)
///   - commitment_deadlines (every 15 minutes since ESC-16; ESC-15): settle the response windows
///     of paid orders (the farmer's, the trucker's and the buyer's), notice cancelled trips, and
///     stamp the reminder and pickup-overdue markers the notification layer reads. ESC-16: the due
///     orders come from the sparse DeadlineIndex (<see cref="OrderDeadlines"/>), not from walking
///     the status partitions. Then close the open trips that left with no booking and no live
///     order (ESC-17: cancelled, cancelReason expired); once a day per container, a repair pass
///     (below) too
///   - deadline_repair (daily, ESC-16): stamp the next deadline on paid orders that lack it or
///     carry a wrong one (orders from before ESC-16, or a stamp that failed); decides nothing
///   - relationship_patterns (hourly, FRD-15): flag self-trading, shared devices and repeated
///     buyer and farmer pairs on recently funded orders as fraud signals
///   - reputation_daily (daily, ESC-28): expire reputation points, end restrictions and admin
///     overrides, consume heal markers (reputation-architecture.md 8.2); <c>{"catchUpDays": n}</c>
///     reads up to 100 days back after an outage
///   - reputation_unpark (hourly, ESC-28): replay reputation stream records parked in SQS (7.5)
///   - reputation_recompute_all (by hand, ESC-28): recompute every user with an active row after a
///     policy change (8.3)
///   - reputation_backfill_ratings (by hand, once per environment, ESC-28): the rating aggregate
///     backfill (12.2); not flagged. FUP-162: then a one-off users Scan for the rows without a woreda;
///     <c>{"scanFrom": "..."}</c> resumes it from the <c>scanCursor</c> of a run that stopped
///   - reputation_stamp_owner_copies (by hand, once per environment, FUP-160): copy the owners'
///     reputation onto the active crop and trip rows created before <c>reputationPenalties</c> went on
///     (12.3); <c>{"startAt": next}</c> goes on after a partial run
///
/// Error codes:
/// - 400: ORDER_CRON_VALIDATION - Invalid or missing action
/// - 500: ORDER_CRON_ERROR - Unexpected failure during processing
/// </summary>
public partial class OrderCronFunction
{
    private readonly IOrderRepository _orderRepo;
    // Optional: unified transaction-audit trail. Auto-cancel is a system-driven
    // order transition that would otherwise be invisible in the monitor.
    private readonly ITransactionAuditRepository? _transactionAuditRepo;
    // ESC-45: the Telebirr webhook inbox; an order whose verified payment is still pending there is
    // never failed. Absent env: only the order's payment hold protects it.
    private readonly IWebhookInboxRepository? _inboxRepo;

    // ESC-15: what the commitment walk needs. Each is built only when its table env is set. The
    // walk refuses to run at all (ORDER_CRON_COMMITMENTS_UNCONFIGURED) unless the escrow service,
    // the crop repository and the trip repository are all there: a cancel that cannot schedule
    // its refund, give the stock back or free the truck would strand money or inventory.
    private readonly EscrowService? _escrowService;
    private readonly ICropRepository? _cropRepo;
    private readonly ITripRepository? _tripRepo;
    // Optional even for the walk: without it a cancelled trip still parks an accepted order with
    // the buyer, only its pending delivery is not cancelled from here.
    private readonly IDeliveryRepository? _deliveryRepo;

    /// <summary>
    /// ESC-15: the <c>COMMITMENTS_ENABLED</c> gate of the commitment walk. Defaults to the
    /// process-wide flag; a test sets it in the object initializer instead of toggling the static.
    /// </summary>
    public bool CommitmentsEnabled { get; init; } = OrderCommitment.IsEnabled;

    /// <summary>
    /// ESC-34: <c>pickupClassification</c>. On, an order stamped <c>pickupJudgement: pending</c> is judged
    /// by its check-ins at its pickup deadline; off, such an order gets the P1 marker and its stamp is
    /// cleared (<see cref="EndPickupJudgementAsync"/>). Orders without the stamp never notice the flag.
    /// </summary>
    public bool PickupClassificationEnabled { get; init; } = PickupJudgement.IsEnabled;

    /// <summary>
    /// FRD-15 (fraud-detection-architecture.md section 15.2): the relationship scan. The Lambda wires it when
    /// <c>FRAUD_SIGNALS_TABLE</c> and <c>USERS_TABLE</c> are set; without it (the test constructor) the action
    /// logs <c>FRAUD_RELATIONSHIP_UNCONFIGURED</c> and writes nothing.
    /// </summary>
    internal RelationshipPatternService? RelationshipPatterns { get; init; }

    /// <summary>FRD-15: the relationship scan's share of the 5 minute Lambda, with a 30 second reserve for the summary.</summary>
    internal static readonly TimeSpan RelationshipScanTime = TimeSpan.FromMinutes(4);

    internal const string RelationshipUnconfiguredToken = "FRAUD_RELATIONSHIP_UNCONFIGURED";

    /// <summary>ESC-45: pending inbox rows read per run (pages of 100); past it the payment-failed step waits a run.</summary>
    private const int MaxPendingInboxPages = 20;

    private const string CommitmentsAction = "commitment_deadlines";

    private const string RelationshipAction = "relationship_patterns";

    private const string ReputationDailyAction = "reputation_daily";
    private const string ReputationUnparkAction = "reputation_unpark";
    private const string ReputationRecomputeAction = "reputation_recompute_all";
    private const string RatingBackfillAction = "reputation_backfill_ratings";
    private const string OwnerStampAction = OwnerCopyStampJob.Action;

    private const string ExpectedActions =
        "auto_cancel_expired, commitment_deadlines, deadline_repair, relationship_patterns, reputation_daily, "
        + "reputation_unpark, reputation_recompute_all, reputation_backfill_ratings or reputation_stamp_owner_copies";

    /// <summary>ESC-28: each reputation action's share of the 5 minute Lambda (8.2: 4 minutes, the rest for the summary).</summary>
    internal static readonly TimeSpan ReputationRunTime = TimeSpan.FromMinutes(4);

    /// <summary>
    /// ESC-28: the <c>reputationPenalties</c> flag. Off, the daily, unpark and recompute actions and
    /// FUP-160's owner stamp log and return (the backfill is not flagged). Defaults to the env flag;
    /// tests set it in the initializer.
    /// </summary>
    public bool ReputationPenaltiesEnabled { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary>FUP-160: the one-off owner stamp; wired with the daily job when <c>CROPS_TABLE</c> and <c>TRIPS_TABLE</c> are set too.</summary>
    internal OwnerCopyStampJob? OwnerCopyStamp { get; init; }

    /// <summary>ESC-28: the daily job; wired when <c>REPUTATION_EVENTS_TABLE</c> and <c>USERS_TABLE</c> are set.</summary>
    internal ReputationDailyJob? ReputationDaily { get; init; }

    /// <summary>ESC-28: the parked record replay; wired when the parked queue URL is set too.</summary>
    internal ReputationUnparkJob? ReputationUnpark { get; init; }

    /// <summary>ESC-28: the rating aggregate backfill; wired when <c>USERS_TABLE</c> and <c>RATINGS_TABLE</c> are set.</summary>
    internal RatingAggregateBackfill? RatingBackfill { get; init; }

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public OrderCronFunction()
    {
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _orderRepo = new OrderRepository(dynamoDb, ordersTable);

        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _transactionAuditRepo = txAuditTable != null
            ? new TransactionAuditRepository(dynamoDb, txAuditTable)
            : null;

        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE");
        _inboxRepo = !string.IsNullOrEmpty(idempotencyTable)
            ? new WebhookInboxRepository(dynamoDb, idempotencyTable)
            : null;

        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE");
        _deliveryRepo = !string.IsNullOrEmpty(deliveriesTable)
            ? new DeliveryRepository(dynamoDb, deliveriesTable)
            : null;

        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE");
        _cropRepo = !string.IsNullOrEmpty(cropsTable)
            ? new CropRepository(dynamoDb, cropsTable)
            : null;

        var tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE");
        _tripRepo = !string.IsNullOrEmpty(tripsTable)
            ? new TripRepository(dynamoDb, tripsTable)
            : null;

        // Built like SyncFunction's order escrow leg: the audit table is required because every
        // escrow mutation must land in the immutable ledger. No gateway: the walk only schedules
        // refunds (expires the escrow); the escrow cron pays them out.
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE");
        var escrowAuditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        var escrowRepo = !string.IsNullOrEmpty(escrowsTable) ? new EscrowRepository(dynamoDb, escrowsTable) : null;
        var userRepo = !string.IsNullOrEmpty(usersTable) ? new UserRepository(dynamoDb, usersTable) : null;
        _escrowService = escrowRepo != null && !string.IsNullOrEmpty(escrowAuditTable)
            ? new EscrowService(
                escrowRepo,
                new EscrowAuditRepository(dynamoDb, escrowAuditTable),
                idempotencyRepo: null,
                telebirrGateway: null,
                userRepo: userRepo,
                orderRepo: _orderRepo,
                deliveryRepo: _deliveryRepo,
                transactionAuditRepo: _transactionAuditRepo)
            : null;

        // ESC-36: T9 reads the trucker's reputation and stamps the escrow of a silent buyer's delivery as a
        // buyer's confirm does, with FRD-13's payout delay (the risk score from the fraud signals table).
        EscrowRepo = escrowRepo;
        UserRepo = userRepo;

        // FRD-15: the relationship scan reads users rows and writes signals; the fraud service needs no
        // crops or rate-limit table here (the cron runs no listing or velocity check).
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE");
        if (!string.IsNullOrEmpty(fraudSignalsTable) && !string.IsNullOrEmpty(usersTable))
        {
            RelationshipPatterns = new RelationshipPatternService(
                _orderRepo,
                new UserRepository(dynamoDb, usersTable),
                new FraudDetectionService(dynamoDb, cropsTable ?? "", fraudSignalsTable, new RateLimitRepository(dynamoDb, "")));
        }

        // ESC-36: the payout delay of T9's escrow stamp, as the sync and escrow-cron Lambdas build it. Without
        // the fraud signals table every payee is unscored (the longest wait), never trusted.
        PayoutDelay = new PayoutDelayPolicy(
            string.IsNullOrEmpty(fraudSignalsTable)
                ? null
                : new FraudDetectionService(dynamoDb, cropsTable ?? "", fraudSignalsTable, new RateLimitRepository(dynamoDb, "")),
            userRepo, escrowRepo);

        // ESC-16: commitment_deadlines repairs the order deadlines itself once a day per container.
        AutoRepair = DeadlineRepairMarker.Process;

        // ESC-28: the rating backfill runs with every flag off (rollout step 2).
        var ratingsTable = Environment.GetEnvironmentVariable("RATINGS_TABLE");
        if (!string.IsNullOrEmpty(usersTable) && !string.IsNullOrEmpty(ratingsTable))
        {
            RatingBackfill = new RatingAggregateBackfill(dynamoDb, usersTable, ratingsTable);
        }

        // ESC-28: the reputation jobs; nothing is built (no policy read) while the flag is off.
        var reputationTable = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE");
        if (ReputationPenaltiesEnabled && !string.IsNullOrEmpty(reputationTable) && !string.IsNullOrEmpty(usersTable))
        {
            var settings = ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line));
            var repo = new ReputationEventRepository(dynamoDb, reputationTable, usersTable);
            var fraud = string.IsNullOrEmpty(fraudSignalsTable)
                ? null
                : new FraudDetectionService(dynamoDb, cropsTable ?? "", fraudSignalsTable, new RateLimitRepository(dynamoDb, ""));
            var service = new ReputationService(
                repo, settings, fraud, copies: ReputationCopyRepository.FromEnvironment(dynamoDb))
            {
                // ESC-33: a parked decline replayed here checks the buyer decline pattern like a live one.
                BuyerPatterns = new TransactionPatternService(_orderRepo, escrows: null)
            };
            ReputationDaily = new ReputationDailyJob(service, repo);

            // FUP-160: the owner stamp walks the listing status indexes and writes through the copy writer.
            if (service.CopiesConfigured && !string.IsNullOrEmpty(cropsTable) && !string.IsNullOrEmpty(tripsTable))
            {
                OwnerCopyStamp = new OwnerCopyStampJob(service, dynamoDb, cropsTable, tripsTable);
            }

            var parkedQueueUrl = Environment.GetEnvironmentVariable("REPUTATION_PARKED_QUEUE_URL");
            var deviceTokensTable = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE");
            if (!string.IsNullOrEmpty(parkedQueueUrl) && !string.IsNullOrEmpty(deviceTokensTable))
            {
                ReputationUnpark = new ReputationUnparkJob(
                    new ReputationRecordProcessor(
                        service, new DeviceReachability(dynamoDb, deviceTokensTable).HasDeviceAsync, ReputationFlags.TimeoutPenaltiesEnabled),
                    new AmazonSQSClient(),
                    parkedQueueUrl);
            }
        }
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies. The commitment walk's gate is
    /// <see cref="CommitmentsEnabled"/>, set in the object initializer.
    /// </summary>
    public OrderCronFunction(
        IOrderRepository orderRepo,
        ITransactionAuditRepository? transactionAuditRepo = null,
        IWebhookInboxRepository? inboxRepo = null,
        EscrowService? escrowService = null,
        ICropRepository? cropRepo = null,
        ITripRepository? tripRepo = null,
        IDeliveryRepository? deliveryRepo = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _transactionAuditRepo = transactionAuditRepo;
        _inboxRepo = inboxRepo;
        _escrowService = escrowService;
        _cropRepo = cropRepo;
        _tripRepo = tripRepo;
        _deliveryRepo = deliveryRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("OrderCronFunction invoked.");

        // Parse action (and ESC-28's optional catchUpDays, a number or a numeric string, FUP-160's
        // optional startAt and FUP-162's optional scanFrom, each a user id) from the body.
        string? action = null;
        int? catchUpDays = null;
        string? scanFrom = null;
        string? startAt = null;
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                using var payload = JsonDocument.Parse(request.Body);
                if (payload.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION", "Invalid JSON in request body.");
                }

                if (payload.RootElement.TryGetProperty("action", out var actionValue))
                {
                    if (actionValue.ValueKind != JsonValueKind.String)
                    {
                        return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION", "Invalid JSON in request body.");
                    }

                    action = actionValue.GetString();
                }

                if (payload.RootElement.TryGetProperty("catchUpDays", out var days))
                {
                    if (!TryReadDays(days, out var parsed))
                    {
                        return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                            $"catchUpDays must be a whole number from 1 to {ReputationDailyJob.MaxCatchUpDays}.");
                    }

                    catchUpDays = parsed;
                }

                // FUP-162: reputation_backfill_ratings resumes its users Scan from the scanCursor of a run
                // that stopped (a user id, so FUP-160's startAt check).
                if (payload.RootElement.TryGetProperty("scanFrom", out var from))
                {
                    if (from.ValueKind != JsonValueKind.String || !OwnerCopyStampJob.IsValidStartAt(from.GetString()))
                    {
                        return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                            $"scanFrom must be the scanCursor of an earlier run, at most {OwnerCopyStampJob.MaxStartAtLength} characters.");
                    }

                    scanFrom = from.GetString();
                }

                if (payload.RootElement.TryGetProperty("startAt", out var start))
                {
                    if (start.ValueKind != JsonValueKind.String || !OwnerCopyStampJob.IsValidStartAt(start.GetString()))
                    {
                        return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                            $"startAt must be the 'next' user id of an earlier run, at most {OwnerCopyStampJob.MaxStartAtLength} characters.");
                    }

                    startAt = start.GetString();
                }
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
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
            return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                $"Missing 'action' field. Expected: {ExpectedActions}.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Order cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_cancel_expired" => await HandleAutoCancelExpiredAsync(context),
                CommitmentsAction => await HandleCommitmentDeadlinesAsync(context),
                RepairAction => await HandleDeadlineRepairAsync(context),
                RelationshipAction => await HandleRelationshipPatternsAsync(context),
                ReputationDailyAction => await HandleReputationDailyAsync(catchUpDays, context),
                ReputationUnparkAction => await HandleReputationUnparkAsync(context),
                ReputationRecomputeAction => await HandleReputationRecomputeAsync(context),
                RatingBackfillAction => await HandleRatingBackfillAsync(scanFrom, context),
                OwnerStampAction => await HandleOwnerStampAsync(startAt, context),
                _ => LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: {ExpectedActions}.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"OrderCronFunction error for action '{safeAction}': [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ORDER_CRON_ERROR",
                $"Order cron job '{safeAction}' failed unexpectedly.");
        }
    }

    /// <summary>
    /// Auto-cancel orders left in "created" or "pending_payment" status for 24 hours (T1; ESC-16:
    /// <see cref="OrderTimings.UnpaidExpiry"/>, from SSM).
    /// These are orders where the buyer never completed payment. A "created" order counts from
    /// createdAt, a "pending_payment" order from when it last entered that status (ESC-47).
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAutoCancelExpiredAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var cutoff = (now - OrderTimings.Current.UnpaidExpiry).ToString("o");

        var cancelledCount = 0;

        // Cancel expired "created" orders
        cancelledCount += await CancelExpiredByStatusAsync("created", cutoff, now, context);

        // Cancel expired "pending_payment" orders
        cancelledCount += await CancelExpiredByStatusAsync("pending_payment", cutoff, now, context);

        // ESC-01: a submitted payment that was never confirmed (abandoned or refused checkout,
        // or a mock auto-fund that failed) fails after 24 hours, so the buyer can pay again or
        // cancel. No checkout is still open then: they stop opening 20 hours after submission.
        var paymentFailedCount = await FailStalePaymentsAsync(now, context);

        var summary = new Dictionary<string, string>
        {
            { "action", "auto_cancel_expired" },
            { "cancelled", cancelledCount.ToString() },
            { "paymentFailed", paymentFailedCount.ToString() },
            { "completedAt", now.ToString("o") }
        };

        context.Logger.LogInformation(
            $"Order auto-cancel complete: {cancelledCount} orders cancelled, {paymentFailedCount} payments failed.");

        return JsonResponse(summary);
    }

    private static APIGatewayProxyResponse JsonResponse(Dictionary<string, string> summary) => new()
    {
        StatusCode = 200,
        Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
    };

    private async Task<int> CancelExpiredByStatusAsync(
        string status, string createdBefore, DateTime now, ILambdaContext context)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync(status, createdBefore);
        var expiry = OrderTimings.Current.UnpaidExpiry;
        var threshold = now - expiry;
        var cancelledCount = 0;

        foreach (var order in orders)
        {
            // ESC-47: a pending_payment order gets its 24 hours from when it entered that status
            // (pendingPaymentAt, stamped by sync), so a buyer who retries a failed payment on an old
            // order through pending_payment is not cancelled at the next run. createdAt is never
            // later, so the query's createdAt filter stays a safe pre-filter; orders from before
            // the stamp keep counting from createdAt.
            if (status == "pending_payment"
                && DateTime.TryParse(GetString(order, "pendingPaymentAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var enteredAt)
                && enteredAt > threshold)
            {
                continue;
            }

            var orderId = GetString(order, "orderId");
            var version = GetInt(order, "version");

            context.Logger.LogInformation($"Auto-cancelling expired order {orderId} (was {status}).");

            var updateData = new Dictionary<string, object>
            {
                { "status", "cancelled" },
                { "cancelledAt", now.ToString("o") },
                { "cancelReason", $"Auto-cancelled: no payment within {OrderTimings.HoursText(expiry)} hours." }
            };

            var result = await _orderRepo.UpdateAsync(orderId, updateData, version);

            if (result.IsSuccess)
            {
                cancelledCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_cancelled", status, "cancelled",
                    AuditContext.ForSystem(),
                    metadata: $"Auto-cancelled: no payment within {OrderTimings.HoursText(expiry)}h.");
            }
            else
            {
                context.Logger.LogError(
                    $"Auto-cancel failed for order {orderId}: {result.Error?.Code} - {result.Error?.Message}");
            }
        }

        return cancelledCount;
    }

    /// <summary>
    /// ESC-01: move <c>payment_submitted</c> orders whose payment clock (<c>paymentSubmittedAt</c>,
    /// or <c>createdAt</c> for orders from before it was stamped) passed 24 hours (T2; ESC-16:
    /// <see cref="OrderTimings.PaymentVerification"/>, from SSM) to
    /// <c>payment_failed</c>, version-checked so a concurrent funding wins.
    /// ESC-45 (D8): never while a verified Telebirr payment for the order is pending in the webhook
    /// inbox (its replay may still fund it): such orders are skipped, both by the inbox's pending
    /// partition and by the order's own <c>paymentHoldUntil</c>, and the write itself is
    /// conditional on the hold. If the inbox cannot be read, the step waits for the next run.
    /// </summary>
    private async Task<int> FailStalePaymentsAsync(DateTime now, ILambdaContext context)
    {
        var verification = OrderTimings.Current.PaymentVerification;
        var threshold = now - verification;
        var nowIso = WebhookInbox.Iso(now);

        HashSet<string>? pendingPaymentOrders = null;
        if (_inboxRepo != null)
        {
            try
            {
                pendingPaymentOrders = await PendingPaymentOrdersAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Payment-failed sweep skipped this run: the webhook inbox could not be read [{ex.GetType().Name}].");
                return 0;
            }

            if (pendingPaymentOrders == null)
            {
                context.Logger.LogError(
                    $"Payment-failed sweep skipped this run: more than {MaxPendingInboxPages * 100} pending inbox rows.");
                return 0;
            }
        }

        // createdAt is never later than paymentSubmittedAt, so it is a safe pre-filter.
        var orders = await _orderRepo.QueryByStatusForCronAsync("payment_submitted", threshold.ToString("o"))
            ?? new List<Dictionary<string, object>>();
        var failedCount = 0;

        foreach (var order in orders)
        {
            if (DateTime.TryParse(GetString(order, "paymentSubmittedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var submittedAt)
                && submittedAt > threshold)
            {
                continue;
            }

            var orderId = GetString(order, "orderId");
            if (pendingPaymentOrders?.Contains(orderId) == true)
            {
                context.Logger.LogInformation(
                    $"Payment-failed sweep: order {MoneyAlarmLog.LogId(orderId)} has a verified payment pending in the webhook inbox.");
                continue;
            }

            if (DateTime.TryParse(GetString(order, PaymentHoldFields.HoldUntil), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var holdUntil)
                && holdUntil > now)
            {
                context.Logger.LogInformation(
                    $"Payment-failed sweep: order {MoneyAlarmLog.LogId(orderId)} held for a pending payment until {WebhookInbox.Iso(holdUntil)}.");
                continue;
            }

            if (await _orderRepo.TryMarkPaymentFailedAsync(orderId, GetInt(order, "version"), nowIso))
            {
                failedCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_updated", "payment_submitted", "payment_failed",
                    AuditContext.ForSystem(),
                    metadata: $"No confirmed payment within {OrderTimings.HoursText(verification)}h.");
            }
            else
            {
                // Most often a funding that landed in the meantime (version moved) or a payment hold
                // written after the read: nothing to do.
                context.Logger.LogWarning(
                    $"Payment-failed sweep skipped order {MoneyAlarmLog.LogId(orderId)}: changed, funded or held since it was read.");
            }
        }

        return failedCount;
    }

    /// <summary>
    /// ESC-45: the orders of every pending row in the webhook inbox, or null when there are more
    /// rows than one run reads (then nothing is failed this run).
    /// </summary>
    private async Task<HashSet<string>?> PendingPaymentOrdersAsync()
    {
        var orders = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
        for (var page = 0; page < MaxPendingInboxPages; page++)
        {
            var (rows, next) = await _inboxRepo!.QueryPendingAsync(100, startKey);
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.OrderId))
                {
                    orders.Add(row.OrderId);
                }
            }

            if (next == null)
            {
                return orders;
            }

            startKey = next;
        }

        return null;
    }

    // ──────────────────────────────────────────────
    // ESC-15: commitment deadlines
    // ──────────────────────────────────────────────

    /// <summary>
    /// The state of one <c>commitment_deadlines</c> run: the clock every decision uses, the
    /// counters of the response body, and the trip rows read so far (one read per trip per run,
    /// however many orders ride on it).
    /// </summary>
    private sealed class CommitmentRun
    {
        public CommitmentRun(ILambdaContext context, DateTime now)
        {
            Context = context;
            Now = now;
            NowIso = OrderCommitment.Iso(now);
        }

        public ILambdaContext Context { get; }
        public DateTime Now { get; }
        public string NowIso { get; }
        public Dictionary<string, (bool Readable, Dictionary<string, object>? Trip)> Trips { get; } = new(StringComparer.Ordinal);

        public int FarmerExpired;
        public int TruckerExpired;
        public int TripCancelled;
        public int BuyerChoiceExpired;
        public int FarmerReminders;
        public int TruckerReminders;
        public int BuyerReminders;
        public int PickupOverdue;

        /// <summary>ESC-34: pickups judged by their check-ins (any outcome).</summary>
        public int PickupJudged;

        /// <summary>ESC-36: drop-offs judged at T8 or T9 (any outcome).</summary>
        public int DropoffJudged;

        /// <summary>FUP-286: check-in reminders stamped before a pickup judgement.</summary>
        public int PickupReminders;

        /// <summary>FUP-338: silent buyer reminders stamped before T9.</summary>
        public int DropoffReminders;

        public int Conflicts;
        public int TripReads;
        public int DepartedTripsClosed;
        public int DepartedTripsKept;

        /// <summary>ESC-16: rows the deadline index returned as due (each read again before acting).</summary>
        public int Due;

        /// <summary>ESC-16: due rows with nothing to do, whose deadline was corrected.</summary>
        public int Restamped;

        /// <summary>ESC-16: rows that must wait (an unreadable delivery, goods on the move), looked at again later.</summary>
        public int Retried;

        /// <summary>ESC-16: rows whose decision threw; the rest of the run went on.</summary>
        public int RowErrors;

        /// <summary>ESC-16: why the sweep stopped before the index ran out (<c>time</c>, <c>lambda</c>, <c>pages</c>), or null.</summary>
        public string? StopReason;

        /// <summary>ESC-16: rows read and deadlines stamped by this run's repair pass (<see cref="RunAutoRepairAsync"/>); 0 when none ran.</summary>
        public int RepairRead;
        public int RepairStamped;

        public Dictionary<string, string> Summary(string outcome) => new()
        {
            { "action", CommitmentsAction },
            { "outcome", outcome },
            { "farmerExpired", FarmerExpired.ToString() },
            { "truckerExpired", TruckerExpired.ToString() },
            { "tripCancelled", TripCancelled.ToString() },
            { "buyerChoiceExpired", BuyerChoiceExpired.ToString() },
            { "farmerReminders", FarmerReminders.ToString() },
            { "truckerReminders", TruckerReminders.ToString() },
            { "buyerReminders", BuyerReminders.ToString() },
            { "pickupOverdue", PickupOverdue.ToString() },
            { "pickupJudged", PickupJudged.ToString() },
            { "dropoffJudged", DropoffJudged.ToString() },
            { "pickupReminders", PickupReminders.ToString() },
            { "dropoffReminders", DropoffReminders.ToString() },
            { "conflicts", Conflicts.ToString() },
            { "tripReads", TripReads.ToString() },
            { "departedTripsClosed", DepartedTripsClosed.ToString() },
            { "departedTripsKept", DepartedTripsKept.ToString() },
            { "due", Due.ToString() },
            { "restamped", Restamped.ToString() },
            { "retried", Retried.ToString() },
            { "rowErrors", RowErrors.ToString() },
            { "stopReason", StopReason ?? "" },
            { "repairRead", RepairRead.ToString() },
            { "repairStamped", RepairStamped.ToString() },
            { "completedAt", NowIso }
        };
    }

    /// <summary>ESC-16: what deciding one due order did.</summary>
    private enum DeadlineOutcome
    {
        /// <summary>Nothing was due on the row as read again.</summary>
        None,

        /// <summary>A transition or a marker was written (the write stamped the next deadline).</summary>
        Applied,

        /// <summary>The row changed since it was read (the write that changed it stamped its deadline).</summary>
        Conflict,

        /// <summary>Something must happen but cannot yet (an unreadable delivery, goods on the move).</summary>
        Waiting
    }

    /// <summary>
    /// FRD-15 (fraud-detection-architecture.md section 15.2): flag self-trading, shared devices and repeated
    /// buyer and farmer pairs on the orders funded in the lookback. Writes nothing and logs
    /// <c>FRAUD_RELATIONSHIP_UNCONFIGURED</c> when the Lambda has no signals or users table. Outcome
    /// <c>partial</c> when the run's time slice ended first; the next hourly run sees the same orders.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleRelationshipPatternsAsync(ILambdaContext context)
    {
        if (RelationshipPatterns == null)
        {
            context.Logger.LogError(
                $"{RelationshipUnconfiguredToken} missing=fraud_signals_or_users: no relationship signal was written this run.");
            return JsonResponse(new RelationshipScanSummary().ToDictionary("unconfigured"));
        }

        var slice = CronRunBudget.StartNew().Slice(RelationshipScanTime);
        var summary = await RelationshipPatterns.RunAsync(DateTime.UtcNow, slice, context);
        return JsonResponse(summary.ToDictionary(summary.StopReason == null ? "ok" : "partial"));
    }

    // ===== ESC-28: reputation (reputation-architecture.md sections 7.5, 8 and 12.2) =====

    private static bool TryReadDays(JsonElement value, out int days)
    {
        days = 0;
        var ok = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out days),
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out days),
            _ => false
        };
        return ok && days >= 1 && days <= ReputationDailyJob.MaxCatchUpDays;
    }

    private APIGatewayProxyResponse ReputationOff(string action, ILambdaContext context)
    {
        context.Logger.LogInformation($"{ReputationDailyJob.SummaryToken} skipped action={action}: reputationPenalties is off.");
        return JsonResponse(new Dictionary<string, string> { ["action"] = action, ["outcome"] = "disabled" });
    }

    private static APIGatewayProxyResponse ReputationUnconfigured(string action, ILambdaContext context)
    {
        context.Logger.LogError($"REPUTATION_UNCONFIGURED action={action}: nothing was done this run.");
        return JsonResponse(new Dictionary<string, string> { ["action"] = action, ["outcome"] = "unconfigured" });
    }

    /// <summary>ESC-28: the daily reputation job (8.2), with an optional catch-up of up to 100 days (8.3).</summary>
    private async Task<APIGatewayProxyResponse> HandleReputationDailyAsync(int? catchUpDays, ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled)
        {
            return ReputationOff(ReputationDailyAction, context);
        }

        if (ReputationDaily == null)
        {
            return ReputationUnconfigured(ReputationDailyAction, context);
        }

        var slice = CronRunBudget.StartNew().Slice(ReputationRunTime);
        return JsonResponse(await ReputationDaily.RunDailyAsync(
            DateTime.UtcNow, catchUpDays ?? ReputationDailyJob.DefaultLookbackDays, slice, context));
    }

    /// <summary>ESC-28: replay the parked reputation records (7.5).</summary>
    private async Task<APIGatewayProxyResponse> HandleReputationUnparkAsync(ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled)
        {
            return ReputationOff(ReputationUnparkAction, context);
        }

        if (ReputationUnpark == null)
        {
            return ReputationUnconfigured(ReputationUnparkAction, context);
        }

        return JsonResponse(await ReputationUnpark.RunAsync(CronRunBudget.StartNew().Slice(ReputationRunTime), context));
    }

    /// <summary>ESC-28: recompute every user with an active row after a policy change (8.3).</summary>
    private async Task<APIGatewayProxyResponse> HandleReputationRecomputeAsync(ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled)
        {
            return ReputationOff(ReputationRecomputeAction, context);
        }

        if (ReputationDaily == null)
        {
            return ReputationUnconfigured(ReputationRecomputeAction, context);
        }

        return JsonResponse(await ReputationDaily.RecomputeAllAsync(
            DateTime.UtcNow, CronRunBudget.StartNew().Slice(ReputationRunTime), context));
    }

    /// <summary>
    /// ESC-28: the one-time rating aggregate backfill (12.2). Not flagged; a rerun adds nothing twice.
    /// FUP-162: <paramref name="scanFrom"/> resumes the users Scan where a stopped run left it.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleRatingBackfillAsync(string? scanFrom, ILambdaContext context)
    {
        if (RatingBackfill == null)
        {
            return ReputationUnconfigured(RatingBackfillAction, context);
        }

        return JsonResponse(await RatingBackfill.RunAsync(
            DateTime.UtcNow, CronRunBudget.StartNew().Slice(ReputationRunTime), context, scanFrom));
    }

    /// <summary>
    /// FUP-160: the one-off stamp of the owner fields (12.3). Behind <c>reputationPenalties</c> like
    /// every copy: off, nothing is read or written. A rerun writes nothing.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleOwnerStampAsync(string? startAt, ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled)
        {
            return ReputationOff(OwnerStampAction, context);
        }

        if (OwnerCopyStamp == null)
        {
            return ReputationUnconfigured(OwnerStampAction, context);
        }

        return JsonResponse(await OwnerCopyStamp.RunAsync(startAt, CronRunBudget.StartNew().Slice(ReputationRunTime), context));
    }

    /// <summary>
    /// ESC-15: settle the commitment deadlines of paid orders. ESC-16: every 15 minutes, over the
    /// orders the sparse <c>DeadlineIndex</c> says are due (<see cref="SweepDueDeadlinesAsync"/>),
    /// each read again before it is decided. Every status transition is a version CAS on the
    /// row as read (<see cref="IOrderRepository.UpdateAsync"/>; FUP-210: a cancel that refunds goes in
    /// one transaction with its refund arm, <see cref="CancelWithRefundAsync"/>), so a party's answer that lands
    /// first wins and the cron only logs the conflict. Markers (reminders, pickup overdue) are
    /// stamped without a version bump, conditioned on the status the row was read in.
    /// Gated on <c>COMMITMENTS_ENABLED</c>; refuses to run when a dependency is unconfigured.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleCommitmentDeadlinesAsync(ILambdaContext context)
    {
        var run = new CommitmentRun(context, DateTime.UtcNow);

        if (!CommitmentsEnabled)
        {
            context.Logger.LogInformation("Commitment deadlines skipped: COMMITMENTS_ENABLED is off.");
            return JsonResponse(run.Summary("disabled"));
        }

        if (_escrowService == null || _cropRepo == null || _tripRepo == null)
        {
            var missing = string.Join(",", new[]
            {
                _escrowService == null ? "escrow" : null,
                _cropRepo == null ? "crops" : null,
                _tripRepo == null ? "trips" : null
            }.Where(m => m != null));
            context.Logger.LogError(
                $"ORDER_CRON_COMMITMENTS_UNCONFIGURED missing={missing}: no deadline was settled this run.");
            return JsonResponse(run.Summary("unconfigured"));
        }

        await SweepDueDeadlinesAsync(run, _escrowService, _cropRepo, _tripRepo);
        await CloseDepartedTripsAsync(run, _tripRepo);
        await RunAutoRepairAsync(run);

        context.Logger.LogInformation(
            $"Commitment deadlines complete: {run.Due} due orders read, "
            + $"{run.FarmerExpired} farmer windows expired, {run.TruckerExpired} trucker windows expired, "
            + $"{run.TripCancelled} trip cancellations applied, {run.BuyerChoiceExpired} buyer choices expired, "
            + $"{run.FarmerReminders}/{run.TruckerReminders}/{run.BuyerReminders} reminders stamped, "
            + $"{run.PickupOverdue} pickups overdue, {run.Conflicts} conflicts, {run.TripReads} trip reads, "
            + $"{run.Restamped} deadlines corrected, {run.Retried} retried later, {run.RowErrors} row errors, "
            + $"{run.DepartedTripsClosed} departed trips closed, {run.DepartedTripsKept} departed trips kept, "
            + $"{run.RepairStamped} deadlines repaired"
            + (run.StopReason != null ? $", the deadline sweep stopped early ({run.StopReason})." : "."));

        return JsonResponse(run.Summary(run.StopReason == null ? "ok" : "partial"));
    }

    /// <summary>
    /// A due <c>funded</c> order: (a) a farmer who never answered cancels the order (refund, stock
    /// back, truck freed); (b) a trucker who never answered fails the transport; (c) a trip that is
    /// gone fails the transport, or is only noted when the trucker was not asked yet; (e) reminders.
    /// One decision per row per run: after a transition the version has moved on.
    /// </summary>
    private async Task<DeadlineOutcome> DecideFundedAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run,
        EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        var farmer = OrderCommitment.FarmerResponseOf(order);
        var trucker = OrderCommitment.TruckerResponseOf(order);

        // Only a stamped deadline expires the farmer's request. A funded row from before the
        // stamps (no farmerDeadlineAt) is left to the 7-day escrow sweep as before, so the
        // first run after the flag turns on never cancels and refunds every old order at once.
        if (farmer == OrderCommitment.Response.Pending
            && GetString(order, OrderCommitment.FarmerDeadlineAtField).Length > 0
            && OrderCommitment.FarmerDeadlineOf(order) is { } farmerDeadline
            && run.Now > farmerDeadline)
        {
            return await ExpireFarmerAsync(order, orderId, trucker, run, escrow, crops, trips);
        }

        if (trucker == OrderCommitment.Response.Pending
            && OrderCommitment.TruckerDeadlineOf(order) is { } truckerDeadline
            && run.Now > truckerDeadline)
        {
            return await ExpireTruckerAsync(order, orderId, run, trips);
        }

        if (await TripIsGoneAsync(order, trucker, run, trips))
        {
            return await FailFundedForCancelledTripAsync(order, orderId, farmer, run);
        }

        var farmerReminder = await RemindFarmerAsync(order, orderId, farmer, run);
        var truckerReminder = await RemindTruckerAsync(order, orderId, trucker, run);
        return farmerReminder == DeadlineOutcome.Applied || truckerReminder == DeadlineOutcome.Applied
            ? DeadlineOutcome.Applied
            : farmerReminder == DeadlineOutcome.Conflict || truckerReminder == DeadlineOutcome.Conflict
                ? DeadlineOutcome.Conflict
                : DeadlineOutcome.None;
    }

    /// <summary>
    /// A due <c>accepted</c> order: (c) a trip that is gone parks the order with the buyer and
    /// cancels its pending delivery; (f) a pickup past its deadline is marked overdue once, or (ESC-34)
    /// judged by the arrival check-ins when the order was accepted with <c>pickupClassification</c> on.
    /// </summary>
    private async Task<DeadlineOutcome> DecideAcceptedAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run,
        EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        if (await TripIsGoneAsync(order, OrderCommitment.TruckerResponseOf(order), run, trips))
        {
            return await FailAcceptedForCancelledTripAsync(order, orderId, run);
        }

        if (PickupJudgement.IsPending(order))
        {
            return PickupClassificationEnabled
                ? await JudgePickupAsync(order, orderId, run, escrow, crops, trips)
                : await EndPickupJudgementAsync(order, orderId, run);
        }

        return await MarkPickupOverdueAsync(order, orderId, run);
    }

    /// <summary>
    /// A due <c>awaiting_transport</c> order: (d) a buyer who did not decide in 48 hours is
    /// refunded (nobody is at fault); (e) a reminder before that.
    /// </summary>
    private async Task<DeadlineOutcome> DecideAwaitingTransportAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, EscrowService escrow, ICropRepository crops)
    {
        var deadline = OrderCommitment.BuyerChoiceDeadlineOf(order);
        if (deadline is { } due && run.Now > due)
        {
            return await ExpireBuyerChoiceAsync(order, orderId, run, escrow, crops);
        }

        return await RemindBuyerAsync(order, orderId, deadline, run);
    }

    // ── (a) farmer window expired ──

    private async Task<DeadlineOutcome> ExpireFarmerAsync(
        Dictionary<string, object> order, string orderId, string trucker, CommitmentRun run,
        EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        var fields = new Dictionary<string, object>
        {
            { "status", "cancelled" },
            { "cancelledAt", run.NowIso },
            { "cancelReason", "farmer_response_expired" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Expired },
            { OrderCommitment.CancelledByField, OrderCommitment.Party.System },
            { OrderCommitment.FaultPartyField, OrderCommitment.Party.Farmer }
        };

        var cancel = await CancelWithRefundAsync(order, orderId, fields, "funded", "farmer_response_expired", run, escrow);
        if (cancel != DeadlineOutcome.Applied)
        {
            return cancel;
        }

        run.FarmerExpired++;
        await RestoreStockAsync(order, orderId, run, crops);

        // The farmer never agreed, so no booking was confirmed; the money hold still took the
        // capacity when it reserved the trip (unless the trip was full or there is none).
        if (OrderCommitment.HasTrip(order)
            && trucker is not (OrderCommitment.Response.None or OrderCommitment.Response.FailedCapacity))
        {
            await ReleaseTripCapacityAsync(order, orderId, run, trips);
        }

        return DeadlineOutcome.Applied;
    }

    // ── (b) trucker window expired ──

    private async Task<DeadlineOutcome> ExpireTruckerAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, ITripRepository trips)
    {
        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TruckerExpired, OrderCommitment.Response.Expired, run.Now);
        fields[OrderCommitment.TruckerRespondedAtField] = run.NowIso;

        if (!await ApplyTransitionAsync(order, orderId, fields, "funded", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trucker_expired", run))
        {
            return DeadlineOutcome.Conflict;
        }

        run.TruckerExpired++;
        await ReleaseTripCapacityAsync(order, orderId, run, trips);
        return DeadlineOutcome.Applied;
    }

    // ── (c) the trip is gone ──

    /// <summary>
    /// Whether the order's trip no longer exists, was soft-deleted or is <c>cancelled</c>. False
    /// for self-pickup, for a transport that already failed (nothing more to notice), and for a
    /// trip that could not be read (the walk never fails a transport on a read error).
    /// </summary>
    private async Task<bool> TripIsGoneAsync(
        Dictionary<string, object> order, string trucker, CommitmentRun run, ITripRepository trips)
    {
        if (!OrderCommitment.HasTrip(order)
            || trucker is OrderCommitment.Response.Cancelled or OrderCommitment.Response.Declined or OrderCommitment.Response.Expired)
        {
            return false;
        }

        var tripId = GetString(order, "tripId");
        if (!run.Trips.TryGetValue(tripId, out var cached))
        {
            run.TripReads++;
            try
            {
                cached = (true, await trips.GetByIdAsync(tripId));
            }
            catch (Exception ex)
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: trip {MoneyAlarmLog.LogId(tripId)} could not be read [{ex.GetType().Name}]; its orders wait a run.");
                cached = (false, null);
            }

            run.Trips[tripId] = cached;
        }

        if (!cached.Readable)
        {
            return false;
        }

        var trip = cached.Trip;
        return trip == null
               || GetString(trip, "deletedAt").Length > 0
               || GetString(trip, "status") == "cancelled";
    }

    private async Task<DeadlineOutcome> FailFundedForCancelledTripAsync(
        Dictionary<string, object> order, string orderId, string farmer, CommitmentRun run)
    {
        if (farmer != OrderCommitment.Response.Accepted)
        {
            // The trucker was never asked. Only record that the trip is gone: the farmer's answer
            // still decides the order, and their accept then lands it with the buyer (DecideFarmer).
            var stamped = await _orderRepo.TryStampAsync(orderId,
                new Dictionary<string, object> { { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Cancelled } },
                new Dictionary<string, string> { { "status", "funded" } });
            if (!stamped)
            {
                run.Conflicts++;
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} left funded before its trip cancellation was noted.");
                return DeadlineOutcome.Conflict;
            }

            run.TripCancelled++;
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, run.Context, orderId, "order", orderId,
                "order_updated", "funded", "funded", AuditContext.ForSystem(),
                metadata: "trip_cancelled: trucker response set to cancelled before the farmer answered");
            return DeadlineOutcome.Applied;
        }

        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, run.Now);
        if (!await ApplyTransitionAsync(order, orderId, fields, "funded", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trip_cancelled", run))
        {
            return DeadlineOutcome.Conflict;
        }

        run.TripCancelled++;
        return DeadlineOutcome.Applied;
    }

    private async Task<DeadlineOutcome> FailAcceptedForCancelledTripAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        var deliveryId = GetString(order, "activeDeliveryId");
        if (deliveryId.Length > 0 && _deliveryRepo != null)
        {
            // Goods already on the move are not a transport failure the buyer can cancel for a
            // refund: a trip cancelled after pickup is a dispute matter. An unreadable delivery
            // waits a run rather than risk that.
            Dictionary<string, object>? delivery;
            try
            {
                delivery = await _deliveryRepo.GetByIdAsync(deliveryId);
            }
            catch (Exception ex)
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} could not be read [{ex.GetType().Name}]; the trip cancellation waits a run.");
                return DeadlineOutcome.Waiting;
            }

            if (delivery != null && GetString(delivery, "status") is not ("pending" or "cancelled"))
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: trip of order {MoneyAlarmLog.LogId(orderId)} is cancelled but its delivery {MoneyAlarmLog.LogId(deliveryId)} is {GetString(delivery, "status")}; left for the parties.");
                return DeadlineOutcome.Waiting;
            }
        }

        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, run.Now);
        if (!await ApplyTransitionAsync(order, orderId, fields, "accepted", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trip_cancelled", run))
        {
            return DeadlineOutcome.Conflict;
        }

        run.TripCancelled++;
        if (deliveryId.Length == 0 || _deliveryRepo == null)
        {
            return DeadlineOutcome.Applied;
        }

        try
        {
            if (await _deliveryRepo.TryCancelPendingAsync(deliveryId))
            {
                await _orderRepo.ReleaseDeliverySlotAsync(orderId, deliveryId);
                run.Context.Logger.LogInformation(
                    $"Commitment deadlines: pending delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} cancelled with its trip.");
            }
            else
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} was no longer pending; not cancelled.");
            }
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: cancelling delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} failed [{ex.GetType().Name}].");
        }

        return DeadlineOutcome.Applied;
    }

    // ── (d) buyer did not decide ──

    private async Task<DeadlineOutcome> ExpireBuyerChoiceAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, EscrowService escrow, ICropRepository crops)
    {
        var fields = new Dictionary<string, object>
        {
            { "status", "cancelled" },
            { "cancelledAt", run.NowIso },
            { "cancelReason", "transport_not_arranged" },
            { OrderCommitment.CancelledByField, OrderCommitment.Party.System },
            { OrderCommitment.FaultPartyField, OrderCommitment.Party.None }
        };

        var cancel = await CancelWithRefundAsync(
            order, orderId, fields, OrderCommitment.AwaitingTransportStatus, "transport_not_arranged", run, escrow);
        if (cancel != DeadlineOutcome.Applied)
        {
            return cancel;
        }

        run.BuyerChoiceExpired++;
        await RestoreStockAsync(order, orderId, run, crops);
        return DeadlineOutcome.Applied;
    }

    // ── (e) reminders ──

    /// <summary>
    /// A reminder goes out once, <see cref="OrderCommitment.ReminderLead"/> before the deadline,
    /// and only for a window of at least <see cref="OrderCommitment.MinReminderWindow"/> (a short
    /// window near departure would be reminded the moment it opened).
    /// </summary>
    private static bool ReminderDue(DateTime deadline, DateTime askedAt, DateTime now)
        => deadline - now <= OrderCommitment.ReminderLead
           && deadline - askedAt >= OrderCommitment.MinReminderWindow;

    private async Task<DeadlineOutcome> RemindFarmerAsync(Dictionary<string, object> order, string orderId, string farmer, CommitmentRun run)
    {
        // A funded row from before the feature has no farmerResponse attribute: the conditional
        // stamp below could never match it, so it is not attempted.
        if (farmer != OrderCommitment.Response.Pending
            || GetString(order, OrderCommitment.FarmerResponseField).Length == 0
            || GetString(order, OrderCommitment.FarmerReminderAtField).Length > 0
            || OrderCommitment.FarmerDeadlineOf(order) is not { } deadline
            || !OrderCommitment.TryParseUtc(GetString(order, "paymentVerifiedAt"), out var askedAt)
            || !ReminderDue(deadline, askedAt, run.Now))
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.FarmerReminderAtField, run.NowIso } },
            new Dictionary<string, string>
            {
                { "status", "funded" }, { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending }
            },
            "farmer reminder", run, () => run.FarmerReminders++);
    }

    private async Task<DeadlineOutcome> RemindTruckerAsync(Dictionary<string, object> order, string orderId, string trucker, CommitmentRun run)
    {
        if (trucker != OrderCommitment.Response.Pending
            || GetString(order, OrderCommitment.TruckerResponseField).Length == 0
            || GetString(order, OrderCommitment.TruckerReminderAtField).Length > 0
            || OrderCommitment.TruckerDeadlineOf(order) is not { } deadline
            || !OrderCommitment.TryParseUtc(GetString(order, OrderCommitment.TruckerAskedAtField), out var askedAt)
            || !ReminderDue(deadline, askedAt, run.Now))
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.TruckerReminderAtField, run.NowIso } },
            new Dictionary<string, string>
            {
                { "status", "funded" }, { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Pending }
            },
            "trucker reminder", run, () => run.TruckerReminders++);
    }

    private async Task<DeadlineOutcome> RemindBuyerAsync(Dictionary<string, object> order, string orderId, DateTime? deadline, CommitmentRun run)
    {
        // The buyer's window is always the full 48 hours (BuyerChoiceWindow), so only the lead matters.
        if (GetString(order, OrderCommitment.BuyerChoiceReminderAtField).Length > 0
            || deadline is not { } due
            || due - run.Now > OrderCommitment.ReminderLead)
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.BuyerChoiceReminderAtField, run.NowIso } },
            new Dictionary<string, string> { { "status", OrderCommitment.AwaitingTransportStatus } },
            "buyer choice reminder", run, () => run.BuyerReminders++);
    }

    // ── (f) pickup overdue ──

    private async Task<DeadlineOutcome> MarkPickupOverdueAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (GetString(order, OrderCommitment.PickupOverdueNotifiedAtField).Length > 0
            || !PickupDeadline.TryParse(GetString(order, PickupDeadline.Field), out var pickupDeadline)
            || pickupDeadline >= run.Now)
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.PickupOverdueNotifiedAtField, run.NowIso } },
            new Dictionary<string, string> { { "status", "accepted" } },
            "pickup overdue marker", run, () => run.PickupOverdue++);
    }

    // ── (g) ESC-17: trips that left unbooked ──

    /// <summary>Departed open trips read per page of the StatusDepartureDateIndex.</summary>
    internal const int DepartedTripPageSize = 100;

    /// <summary>
    /// Pages read per run, newest departures first. A trip is closable from the first run after its
    /// Ethiopia day ends, when it sits at the head of the read; the trips kept open after they left
    /// (booked, or named by a live order) only pile up behind it, so they never hide a newer one.
    /// </summary>
    internal const int MaxDepartedTripPages = 10;

    /// <summary>Trips closed per run at most.</summary>
    internal const int MaxDepartedTripCloses = 200;

    /// <summary>
    /// ESC-17 (design 4.10, edge case G4): close the <c>open</c> trips whose Ethiopia day is over
    /// (the browse stopped listing them, L-7), that hold no booking and that no live order still
    /// names: <c>cancelled</c> with <c>cancelReason: expired</c>. Read through the
    /// StatusDepartureDateIndex below the browse cutoff (no Scan), newest departures first, bounded
    /// per run by <see cref="MaxDepartedTripPages"/> and <see cref="MaxDepartedTripCloses"/>. Each close is one
    /// write conditioned on the version read, the status and no booking
    /// (<see cref="ITripRepository.TryExpireUnbookedAsync"/>), so a re-run, a trucker's edit or a
    /// funding that lands in between changes nothing more. A trip that left with bookings stays for
    /// its trucker to start or cancel.
    /// </summary>
    private async Task CloseDepartedTripsAsync(CommitmentRun run, ITripRepository trips)
    {
        var cutoff = TripDeparture.CutoffIso(run.Now);
        Dictionary<string, DynamoValue>? startKey = null;
        for (var page = 0; page < MaxDepartedTripPages; page++)
        {
            List<Dictionary<string, DynamoValue>>? rows;
            Dictionary<string, DynamoValue>? next;
            try
            {
                (rows, next) = await trips.QueryDepartedOpenAsync(cutoff, DepartedTripPageSize, startKey);
            }
            catch (Exception ex)
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: departed trips could not be read [{ex.GetType().Name}]; they wait a run.");
                return;
            }

            foreach (var row in rows ?? new List<Dictionary<string, DynamoValue>>())
            {
                if (run.DepartedTripsClosed >= MaxDepartedTripCloses)
                {
                    run.Context.Logger.LogInformation(
                        $"Commitment deadlines: {MaxDepartedTripCloses} departed trips closed; the rest wait a run.");
                    return;
                }

                await TryCloseDepartedTripAsync(row, cutoff, run, trips);
            }

            if (next == null)
            {
                return;
            }

            startKey = next;
        }

        run.Context.Logger.LogInformation(
            $"Commitment deadlines: {MaxDepartedTripPages} pages of departed trips read; the rest wait a run.");
    }

    private async Task TryCloseDepartedTripAsync(
        Dictionary<string, DynamoValue> trip, string cutoff, CommitmentRun run, ITripRepository trips)
    {
        var tripId = RawString(trip, "tripId");
        var createdAt = RawString(trip, "createdAt");
        if (tripId.Length == 0 || createdAt.Length == 0)
        {
            return;
        }

        if (HasBookings(trip))
        {
            run.DepartedTripsKept++;
            return;
        }

        // The index compares strings; a row stored in another form before 1.3.0 is checked as a date.
        if (!DateTime.TryParse(RawString(trip, "departureDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var departure)
            || !TripDeparture.HasDeparted(departure, run.Now))
        {
            return;
        }

        // Bookings exist only since ESC-15 and only while the flag is on: an order from before
        // (accepted on a trip that never stored a booking) or one still being paid must keep its truck.
        List<Dictionary<string, object>> orders;
        try
        {
            orders = await _orderRepo.QueryByTripAsync(tripId) ?? new List<Dictionary<string, object>>();
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: orders of departed trip {MoneyAlarmLog.LogId(tripId)} could not be read [{ex.GetType().Name}]; it waits a run.");
            return;
        }

        if (orders.Any(o => TripLifecycle.OrderStillOnTrip(GetString(o, "status"))))
        {
            run.DepartedTripsKept++;
            return;
        }

        var version = trip.TryGetValue("version", out var versionAttr)
            && int.TryParse(versionAttr.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        bool closed;
        try
        {
            closed = await trips.TryExpireUnbookedAsync(tripId, createdAt, version, cutoff, run.NowIso);
        }
        catch (Exception ex)
        {
            // A throttle or a network error on one trip must not end the walk for the others.
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: departed trip {MoneyAlarmLog.LogId(tripId)} could not be closed [{ex.GetType().Name}]; it waits a run.");
            return;
        }

        if (!closed)
        {
            run.Conflicts++;
            run.Context.Logger.LogInformation(
                $"Commitment deadlines: departed trip {MoneyAlarmLog.LogId(tripId)} changed since it was read; not closed this run.");
            return;
        }

        run.DepartedTripsClosed++;
        run.Context.Logger.LogInformation(
            $"Commitment deadlines: trip {MoneyAlarmLog.LogId(tripId)} open -> cancelled ({TripLifecycle.ExpiredCancelReason}: left unbooked).");
    }

    /// <summary>Whether the trip row holds a booking (ESC-15 attributes; absent on older rows).</summary>
    private static bool HasBookings(Dictionary<string, DynamoValue> trip)
        => (trip.TryGetValue("bookedOrderIds", out var ids) && ids.SS is { Count: > 0 })
           || (trip.TryGetValue("acceptedOrderIds", out var accepted) && accepted.SS is { Count: > 0 })
           || (trip.TryGetValue("bookedQuintals", out var booked)
               && double.TryParse(booked.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var quintals)
               && quintals > 0);

    private static string RawString(Dictionary<string, DynamoValue> item, string key)
        => item.TryGetValue(key, out var value) ? value.S ?? "" : "";

    // ── shared writes and side effects ──

    /// <summary>
    /// The version CAS every transition goes through. A conflict (the row changed since the query
    /// read it, most often a party answering) is counted and logged, never thrown; the next run
    /// reads the row again. An applied transition is audited as the system actor.
    /// </summary>
    private async Task<bool> ApplyTransitionAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> fields,
        string fromStatus, string toStatus, string auditAction, string reason, CommitmentRun run)
    {
        var result = await _orderRepo.UpdateAsync(orderId, fields, GetInt(order, "version"));
        if (!result.IsSuccess)
        {
            run.Conflicts++;
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} changed since it was read ({result.Error?.Code}); {reason} not applied this run.");
            return false;
        }

        await NoteTransitionAsync(orderId, fromStatus, toStatus, auditAction, reason, run);
        return true;
    }

    /// <summary>An applied transition: logged, and audited as the system actor.</summary>
    private async Task NoteTransitionAsync(
        string orderId, string fromStatus, string toStatus, string auditAction, string reason, CommitmentRun run)
    {
        run.Context.Logger.LogInformation(
            $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} {fromStatus} -> {toStatus} ({reason}).");
        await TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, run.Context, orderId, "order", orderId,
            auditAction, fromStatus, toStatus, AuditContext.ForSystem(), metadata: reason);
    }

    /// <summary>FUP-210: plans per deadline cancel that refunds (an escrow can change between the plan and the write).</summary>
    private const int RefundCancelAttempts = 3;

    /// <summary>FUP-210: the <c>refundReason</c> a deadline cancel arms (the one <see cref="ScheduleRefundAsync"/> arms too).</summary>
    private const string DeadlineRefundReason = "order_cancelled";

    /// <summary>
    /// FUP-210: a deadline cancel left for a later run because its refund could not be armed with it
    /// (the money is frozen or being settled, or kept changing). Nothing was written.
    /// </summary>
    internal const string DeadlineRefundWaitingToken = "ORDER_DEADLINE_REFUND_WAITING";

    /// <summary>
    /// FUP-210: the deadline cancel of a paid order (<c>farmer_response_expired</c>,
    /// <c>transport_not_arranged</c>) and the refund arm on its money in one DynamoDB transaction
    /// (<see cref="EscrowService.PlanOrderRefundAsync"/>, <see cref="EscrowService.CloseOrderWithRefundAsync"/>),
    /// as the funded reject in sync does since FUP-53, so a hold or a settlement can no longer reach the
    /// escrow between the cancel and the arm and leave the cancelled order's refund unarmed.
    /// <list type="bullet">
    /// <item>Plain holds: the cancel (the version and status as read) and the arm on each
    /// (<c>refundReason: order_cancelled</c>, <c>expiresAt = now</c>) land together, then one
    /// <c>refund_scheduled</c> row each. An escrow held for review is not armed (its admin decides it)
    /// and the cancel lands only while it is still held.</item>
    /// <item>Money frozen or moving (disputed, under a settlement lock, in the manual payout queue):
    /// <see cref="DeadlineOutcome.Waiting"/> with <see cref="DeadlineRefundWaitingToken"/>, nothing
    /// written, and the row comes back later. Never a cancel without its arm.</item>
    /// <item>No escrow record holds the money: the version CAS cancel and the best-effort refund
    /// schedule after it, as before.</item>
    /// </list>
    /// An escrow that changed under the transaction is planned again, at most
    /// <see cref="RefundCancelAttempts"/> times, then the row waits too. The order having moved is a
    /// conflict, as before. A read or a transaction that fails throws with nothing written, and the
    /// row is tried again next run (<see cref="DeadlineRowFailedToken"/>), after the alarmed line of
    /// <see cref="LogRefundNotArmed"/>.
    /// </summary>
    private async Task<DeadlineOutcome> CancelWithRefundAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> fields,
        string fromStatus, string reason, CommitmentRun run, EscrowService escrow)
    {
        for (var attempt = 1; attempt <= RefundCancelAttempts; attempt++)
        {
            OrderRefundPlan plan;
            try
            {
                plan = await escrow.PlanOrderRefundAsync(orderId);
            }
            catch (Exception ex)
            {
                LogRefundNotArmed(orderId, reason, "escrow_unreadable", ex, run);
                throw;
            }

            if (plan.Readiness == OrderRefundReadiness.Busy)
            {
                return RefundWaiting(orderId, reason, "escrow_busy", run);
            }

            if (plan.Readiness == OrderRefundReadiness.Nothing)
            {
                if (!await ApplyTransitionAsync(order, orderId, fields, fromStatus, "cancelled", "order_cancelled", reason, run))
                {
                    return DeadlineOutcome.Conflict;
                }

                await ScheduleRefundAsync(orderId, run, escrow);
                return DeadlineOutcome.Applied;
            }

            OrderLinkedWriteResult write;
            try
            {
                write = await escrow.CloseOrderWithRefundAsync(
                    orderId, fields, GetInt(order, "version"), fromStatus, plan, DeadlineRefundReason, "system", AuditContext.ForSystem());
            }
            catch (Exception ex)
            {
                LogRefundNotArmed(orderId, reason, "cancel_not_written", ex, run);
                throw;
            }

            switch (write.Outcome)
            {
                case OrderLinkedWriteOutcome.Written:
                    await NoteTransitionAsync(orderId, fromStatus, "cancelled", "order_cancelled", reason, run);
                    run.Context.Logger.LogInformation(plan.Held.Count > 0
                        ? $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason={DeadlineRefundReason}"
                        : $"No refundable order escrow for orderId={MoneyAlarmLog.LogId(orderId)} ({DeadlineRefundReason}): held for review.");
                    return DeadlineOutcome.Applied;

                case OrderLinkedWriteOutcome.OrderChanged:
                case OrderLinkedWriteOutcome.OrderNotFound:
                    run.Conflicts++;
                    run.Context.Logger.LogWarning(
                        $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} changed since it was read ({write.Outcome}); {reason} not applied this run.");
                    return DeadlineOutcome.Conflict;
            }

            // LinkedItemRefused: an escrow changed after the plan (a hold, a settlement, a released hold). Plan again.
        }

        return RefundWaiting(orderId, reason, "escrow_changed", run);
    }

    /// <summary>
    /// FUP-210: the money could not be read, or the cancel with its arm could not be written, for a
    /// reason other than a refusal: nothing was written, and the row is tried again next run. Logged
    /// with the alarmed <c>ORDER_ESCROW_REFUND_SCHEDULE_FAILED</c> token (the order-cron filter counts
    /// it into <c>OrderEscrowProblems</c>), as the failed separate arm was before, so a read that keeps
    /// failing (permissions, a table missing from the env) does not hold the buyer's refund unseen.
    /// </summary>
    private static void LogRefundNotArmed(string orderId, string reason, string why, Exception ex, CommitmentRun run)
        => run.Context.Logger.LogError(
            $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason={why} cancelReason={reason} "
            + $"error=[{ex.GetType().Name}]: nothing written, the cancel and its refund wait for the next run.");

    /// <summary>FUP-210: log why a deadline cancel waits (<see cref="DeadlineRefundWaitingToken"/>).</summary>
    private static DeadlineOutcome RefundWaiting(string orderId, string reason, string why, CommitmentRun run)
    {
        run.Context.Logger.LogWarning(
            $"{DeadlineRefundWaitingToken} orderId={MoneyAlarmLog.LogId(orderId)} cancelReason={reason} reason={why}: "
            + $"the refund could not be armed with the cancel, nothing written; looked at again in {OrderTimings.HoursText(DeadlineRetryAfter)}h.");
        return DeadlineOutcome.Waiting;
    }

    /// <summary>
    /// A marker write: no version bump, conditioned on the state the row was read in. ESC-16: the
    /// same write moves the order's next deadline past the marker (<see cref="IOrderRepository.TryStampAsync"/>).
    /// </summary>
    private async Task<DeadlineOutcome> StampAsync(
        string orderId, Dictionary<string, object> fields, IReadOnlyDictionary<string, string> expected,
        string what, CommitmentRun run, Action onStamped)
    {
        if (await _orderRepo.TryStampAsync(orderId, fields, expected))
        {
            onStamped();
            return DeadlineOutcome.Applied;
        }

        run.Conflicts++;
        run.Context.Logger.LogInformation(
            $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} moved on before its {what} was stamped.");
        return DeadlineOutcome.Conflict;
    }

    /// <summary>
    /// Expire the order's escrow so the hourly refund sweep pays the buyer back. The same alarm
    /// token as SyncFunction's cancel path: a failure here leaves the buyer's money held. FUP-210:
    /// only after a cancel whose plan found no escrow record holding the money; otherwise
    /// <see cref="CancelWithRefundAsync"/> arms in the cancel's own transaction.
    /// </summary>
    private static async Task ScheduleRefundAsync(string orderId, CommitmentRun run, EscrowService escrow)
    {
        try
        {
            var scheduled = await escrow.ScheduleRefundForOrderAsync(
                orderId, "order_cancelled", "system", AuditContext.ForSystem());
            run.Context.Logger.LogInformation(scheduled
                ? $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason=order_cancelled"
                : $"No refundable order escrow for orderId={MoneyAlarmLog.LogId(orderId)} (order_cancelled): "
                  + "either none exists or delivery already confirmed.");
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogError(
                $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>Give the listing back the stock the funded order took (SIM-13; idempotent per order).</summary>
    private static async Task RestoreStockAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, ICropRepository crops)
    {
        var listingId = GetString(order, "listingId");
        var quintals = QuintalsOf(order);
        if (listingId.Length == 0 || quintals <= 0)
        {
            return;
        }

        try
        {
            var restored = await crops.TryRestoreStockAsync(listingId, orderId, quintals);
            run.Context.Logger.LogInformation(restored
                ? $"Listing {MoneyAlarmLog.LogId(listingId)}: +{quintals} quintals restored (order {MoneyAlarmLog.LogId(orderId)} cancelled by the commitment cron)"
                : $"Listing {MoneyAlarmLog.LogId(listingId)}: nothing to restore for order {MoneyAlarmLog.LogId(orderId)}");
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: stock restore for order {MoneyAlarmLog.LogId(orderId)} failed [{ex.GetType().Name}]; the listing keeps the reservation.");
        }
    }

    /// <summary>Free the capacity the order holds on its trip (idempotent per order).</summary>
    private static async Task ReleaseTripCapacityAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, ITripRepository trips)
    {
        var tripId = GetString(order, "tripId");
        var quintals = QuintalsOf(order);
        if (tripId.Length == 0 || quintals <= 0)
        {
            return;
        }

        try
        {
            var released = await trips.TryReleaseCapacityAsync(tripId, orderId, quintals);
            run.Context.Logger.LogInformation(released
                ? $"Trip {MoneyAlarmLog.LogId(tripId)}: +{quintals} quintals released (order {MoneyAlarmLog.LogId(orderId)})"
                : $"Trip {MoneyAlarmLog.LogId(tripId)}: nothing to release for order {MoneyAlarmLog.LogId(orderId)}");
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Commitment deadlines: capacity release for order {MoneyAlarmLog.LogId(orderId)} failed [{ex.GetType().Name}]; the trip keeps the booking.");
        }
    }

    /// <summary>The order's quantity (kilograms) as quintals, 0 when unreadable.</summary>
    private static double QuintalsOf(Dictionary<string, object> order)
        => double.TryParse(GetString(order, "quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var kg)
            ? kg / 100.0
            : 0.0;

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}

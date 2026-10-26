using System.Globalization;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// EventBridge scheduled event handler for order background jobs.
/// Routes based on "action" field in the request body:
///   - auto_cancel_expired (hourly): cancel unpaid orders older than 24h
///   - commitment_deadlines (hourly, ESC-15): settle the response windows of paid orders (the
///     farmer's, the trucker's and the buyer's), notice cancelled trips, and stamp the reminder
///     and pickup-overdue markers the notification layer reads
///
/// Error codes:
/// - 400: ORDER_CRON_VALIDATION - Invalid or missing action
/// - 500: ORDER_CRON_ERROR - Unexpected failure during processing
/// </summary>
public class OrderCronFunction
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

    private const int ExpirationHours = 24;

    /// <summary>ESC-45: pending inbox rows read per run (pages of 100); past it the payment-failed step waits a run.</summary>
    private const int MaxPendingInboxPages = 20;

    private const string CommitmentsAction = "commitment_deadlines";

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
        _escrowService = !string.IsNullOrEmpty(escrowsTable) && !string.IsNullOrEmpty(escrowAuditTable)
            ? new EscrowService(
                new EscrowRepository(dynamoDb, escrowsTable),
                new EscrowAuditRepository(dynamoDb, escrowAuditTable),
                idempotencyRepo: null,
                telebirrGateway: null,
                userRepo: !string.IsNullOrEmpty(usersTable) ? new UserRepository(dynamoDb, usersTable) : null,
                orderRepo: _orderRepo,
                deliveryRepo: _deliveryRepo,
                transactionAuditRepo: _transactionAuditRepo)
            : null;
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
                $"Missing 'action' field. Expected: auto_cancel_expired or {CommitmentsAction}.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Order cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_cancel_expired" => await HandleAutoCancelExpiredAsync(context),
                CommitmentsAction => await HandleCommitmentDeadlinesAsync(context),
                _ => LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: auto_cancel_expired or {CommitmentsAction}.")
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
    /// Auto-cancel orders left in "created" or "pending_payment" status for 24 hours.
    /// These are orders where the buyer never completed payment. A "created" order counts from
    /// createdAt, a "pending_payment" order from when it last entered that status (ESC-47).
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAutoCancelExpiredAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-ExpirationHours).ToString("o");

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
        var threshold = now.AddHours(-ExpirationHours);
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
                { "cancelReason", $"Auto-cancelled: no payment within {ExpirationHours} hours." }
            };

            var result = await _orderRepo.UpdateAsync(orderId, updateData, version);

            if (result.IsSuccess)
            {
                cancelledCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_cancelled", status, "cancelled",
                    AuditContext.ForSystem(),
                    metadata: $"Auto-cancelled: no payment within {ExpirationHours}h.");
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
    /// or <c>createdAt</c> for orders from before it was stamped) passed 24 hours to
    /// <c>payment_failed</c>, version-checked so a concurrent funding wins.
    /// ESC-45 (D8): never while a verified Telebirr payment for the order is pending in the webhook
    /// inbox (its replay may still fund it): such orders are skipped, both by the inbox's pending
    /// partition and by the order's own <c>paymentHoldUntil</c>, and the write itself is
    /// conditional on the hold. If the inbox cannot be read, the step waits for the next run.
    /// </summary>
    private async Task<int> FailStalePaymentsAsync(DateTime now, ILambdaContext context)
    {
        var threshold = now.AddHours(-ExpirationHours);
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
                    metadata: $"No confirmed payment within {ExpirationHours}h.");
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
        public int Conflicts;
        public int TripReads;

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
            { "conflicts", Conflicts.ToString() },
            { "tripReads", TripReads.ToString() },
            { "completedAt", NowIso }
        };
    }

    /// <summary>
    /// ESC-15: the hourly walk over paid orders. Every status transition is a version CAS on the
    /// row as read (<see cref="IOrderRepository.UpdateAsync"/>), so a party's answer that lands
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

        await WalkFundedAsync(run, _escrowService, _cropRepo, _tripRepo);
        await WalkAcceptedAsync(run, _tripRepo);
        await WalkAwaitingTransportAsync(run, _escrowService, _cropRepo);

        context.Logger.LogInformation(
            $"Commitment deadlines complete: {run.FarmerExpired} farmer windows expired, {run.TruckerExpired} trucker windows expired, "
            + $"{run.TripCancelled} trip cancellations applied, {run.BuyerChoiceExpired} buyer choices expired, "
            + $"{run.FarmerReminders}/{run.TruckerReminders}/{run.BuyerReminders} reminders stamped, "
            + $"{run.PickupOverdue} pickups overdue, {run.Conflicts} conflicts, {run.TripReads} trip reads.");

        return JsonResponse(run.Summary("ok"));
    }

    /// <summary>
    /// <c>funded</c> orders: (a) a farmer who never answered cancels the order (refund, stock back,
    /// truck freed); (b) a trucker who never answered fails the transport; (c) a trip that is gone
    /// fails the transport, or is only noted when the trucker was not asked yet; (e) reminders.
    /// One decision per row per run: after a transition the version has moved on.
    /// </summary>
    private async Task WalkFundedAsync(CommitmentRun run, EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync("funded") ?? new List<Dictionary<string, object>>();
        foreach (var order in orders)
        {
            var orderId = GetString(order, "orderId");
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
                await ExpireFarmerAsync(order, orderId, trucker, run, escrow, crops, trips);
                continue;
            }

            if (trucker == OrderCommitment.Response.Pending
                && OrderCommitment.TruckerDeadlineOf(order) is { } truckerDeadline
                && run.Now > truckerDeadline)
            {
                await ExpireTruckerAsync(order, orderId, run, trips);
                continue;
            }

            if (await TripIsGoneAsync(order, trucker, run, trips))
            {
                await FailFundedForCancelledTripAsync(order, orderId, farmer, run);
                continue;
            }

            await RemindFarmerAsync(order, orderId, farmer, run);
            await RemindTruckerAsync(order, orderId, trucker, run);
        }
    }

    /// <summary>
    /// <c>accepted</c> orders: (c) a trip that is gone parks the order with the buyer and cancels
    /// its pending delivery; (f) a pickup past its deadline is marked overdue once.
    /// </summary>
    private async Task WalkAcceptedAsync(CommitmentRun run, ITripRepository trips)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync("accepted") ?? new List<Dictionary<string, object>>();
        foreach (var order in orders)
        {
            var orderId = GetString(order, "orderId");
            if (await TripIsGoneAsync(order, OrderCommitment.TruckerResponseOf(order), run, trips))
            {
                await FailAcceptedForCancelledTripAsync(order, orderId, run);
                continue;
            }

            await MarkPickupOverdueAsync(order, orderId, run);
        }
    }

    /// <summary>
    /// <c>awaiting_transport</c> orders: (d) a buyer who did not decide in 48 hours is refunded
    /// (nobody is at fault); (e) a reminder before that.
    /// </summary>
    private async Task WalkAwaitingTransportAsync(CommitmentRun run, EscrowService escrow, ICropRepository crops)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync(OrderCommitment.AwaitingTransportStatus)
            ?? new List<Dictionary<string, object>>();
        foreach (var order in orders)
        {
            var orderId = GetString(order, "orderId");
            var deadline = OrderCommitment.BuyerChoiceDeadlineOf(order);
            if (deadline is { } due && run.Now > due)
            {
                await ExpireBuyerChoiceAsync(order, orderId, run, escrow, crops);
                continue;
            }

            await RemindBuyerAsync(order, orderId, deadline, run);
        }
    }

    // ── (a) farmer window expired ──

    private async Task ExpireFarmerAsync(
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

        if (!await ApplyTransitionAsync(order, orderId, fields, "funded", "cancelled",
                "order_cancelled", "farmer_response_expired", run))
        {
            return;
        }

        run.FarmerExpired++;
        await ScheduleRefundAsync(orderId, run, escrow);
        await RestoreStockAsync(order, orderId, run, crops);

        // The farmer never agreed, so no booking was confirmed; the money hold still took the
        // capacity when it reserved the trip (unless the trip was full or there is none).
        if (OrderCommitment.HasTrip(order)
            && trucker is not (OrderCommitment.Response.None or OrderCommitment.Response.FailedCapacity))
        {
            await ReleaseTripCapacityAsync(order, orderId, run, trips);
        }
    }

    // ── (b) trucker window expired ──

    private async Task ExpireTruckerAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, ITripRepository trips)
    {
        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TruckerExpired, OrderCommitment.Response.Expired, run.Now);
        fields[OrderCommitment.TruckerRespondedAtField] = run.NowIso;

        if (!await ApplyTransitionAsync(order, orderId, fields, "funded", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trucker_expired", run))
        {
            return;
        }

        run.TruckerExpired++;
        await ReleaseTripCapacityAsync(order, orderId, run, trips);
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

    private async Task FailFundedForCancelledTripAsync(
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
                return;
            }

            run.TripCancelled++;
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, run.Context, orderId, "order", orderId,
                "order_updated", "funded", "funded", AuditContext.ForSystem(),
                metadata: "trip_cancelled: trucker response set to cancelled before the farmer answered");
            return;
        }

        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, run.Now);
        if (await ApplyTransitionAsync(order, orderId, fields, "funded", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trip_cancelled", run))
        {
            run.TripCancelled++;
        }
    }

    private async Task FailAcceptedForCancelledTripAsync(
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
                return;
            }

            if (delivery != null && GetString(delivery, "status") is not ("pending" or "cancelled"))
            {
                run.Context.Logger.LogWarning(
                    $"Commitment deadlines: trip of order {MoneyAlarmLog.LogId(orderId)} is cancelled but its delivery {MoneyAlarmLog.LogId(deliveryId)} is {GetString(delivery, "status")}; left for the parties.");
                return;
            }
        }

        var fields = OrderCommitment.TransportFailedFields(
            OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, run.Now);
        if (!await ApplyTransitionAsync(order, orderId, fields, "accepted", OrderCommitment.AwaitingTransportStatus,
                "order_updated", "trip_cancelled", run))
        {
            return;
        }

        run.TripCancelled++;
        if (deliveryId.Length == 0 || _deliveryRepo == null)
        {
            return;
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
    }

    // ── (d) buyer did not decide ──

    private async Task ExpireBuyerChoiceAsync(
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

        if (!await ApplyTransitionAsync(order, orderId, fields, OrderCommitment.AwaitingTransportStatus, "cancelled",
                "order_cancelled", "transport_not_arranged", run))
        {
            return;
        }

        run.BuyerChoiceExpired++;
        await ScheduleRefundAsync(orderId, run, escrow);
        await RestoreStockAsync(order, orderId, run, crops);
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

    private async Task RemindFarmerAsync(Dictionary<string, object> order, string orderId, string farmer, CommitmentRun run)
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
            return;
        }

        await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.FarmerReminderAtField, run.NowIso } },
            new Dictionary<string, string>
            {
                { "status", "funded" }, { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending }
            },
            "farmer reminder", run, () => run.FarmerReminders++);
    }

    private async Task RemindTruckerAsync(Dictionary<string, object> order, string orderId, string trucker, CommitmentRun run)
    {
        if (trucker != OrderCommitment.Response.Pending
            || GetString(order, OrderCommitment.TruckerResponseField).Length == 0
            || GetString(order, OrderCommitment.TruckerReminderAtField).Length > 0
            || OrderCommitment.TruckerDeadlineOf(order) is not { } deadline
            || !OrderCommitment.TryParseUtc(GetString(order, OrderCommitment.TruckerAskedAtField), out var askedAt)
            || !ReminderDue(deadline, askedAt, run.Now))
        {
            return;
        }

        await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.TruckerReminderAtField, run.NowIso } },
            new Dictionary<string, string>
            {
                { "status", "funded" }, { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Pending }
            },
            "trucker reminder", run, () => run.TruckerReminders++);
    }

    private async Task RemindBuyerAsync(Dictionary<string, object> order, string orderId, DateTime? deadline, CommitmentRun run)
    {
        // The buyer's window is always the full 48 hours (BuyerChoiceWindow), so only the lead matters.
        if (GetString(order, OrderCommitment.BuyerChoiceReminderAtField).Length > 0
            || deadline is not { } due
            || due - run.Now > OrderCommitment.ReminderLead)
        {
            return;
        }

        await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.BuyerChoiceReminderAtField, run.NowIso } },
            new Dictionary<string, string> { { "status", OrderCommitment.AwaitingTransportStatus } },
            "buyer choice reminder", run, () => run.BuyerReminders++);
    }

    // ── (f) pickup overdue ──

    private async Task MarkPickupOverdueAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (GetString(order, OrderCommitment.PickupOverdueNotifiedAtField).Length > 0
            || !PickupDeadline.TryParse(GetString(order, PickupDeadline.Field), out var pickupDeadline)
            || pickupDeadline >= run.Now)
        {
            return;
        }

        await StampAsync(orderId,
            new Dictionary<string, object> { { OrderCommitment.PickupOverdueNotifiedAtField, run.NowIso } },
            new Dictionary<string, string> { { "status", "accepted" } },
            "pickup overdue marker", run, () => run.PickupOverdue++);
    }

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

        run.Context.Logger.LogInformation(
            $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} {fromStatus} -> {toStatus} ({reason}).");
        await TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, run.Context, orderId, "order", orderId,
            auditAction, fromStatus, toStatus, AuditContext.ForSystem(), metadata: reason);
        return true;
    }

    /// <summary>A marker write: no version bump, conditioned on the state the row was read in.</summary>
    private async Task StampAsync(
        string orderId, Dictionary<string, object> fields, IReadOnlyDictionary<string, string> expected,
        string what, CommitmentRun run, Action onStamped)
    {
        if (await _orderRepo.TryStampAsync(orderId, fields, expected))
        {
            onStamped();
            return;
        }

        run.Conflicts++;
        run.Context.Logger.LogInformation(
            $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} moved on before its {what} was stamped.");
    }

    /// <summary>
    /// Expire the order's escrow so the hourly refund sweep pays the buyer back. The same alarm
    /// token as SyncFunction's cancel path: a failure here leaves the buyer's money held.
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

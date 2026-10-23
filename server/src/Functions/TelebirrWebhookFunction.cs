using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Handles asynchronous Telebirr payment notifications: POST /webhooks/telebirr
/// (no JWT: Telebirr can't send one). Security layers: signature verification over the one
/// field map that is read (primary), notify_time freshness, merchant match, replay rejection,
/// amount match. On a verified "Completed" it transitions the escrow initiated → escrowed; on a
/// documented failure it cancels. ESC-01: a notification for an order checkout
/// (POST /escrow/order-checkout) funds the order instead, exactly once.
/// ESC-45 answers: 200 for everything before verification and for every final outcome; 500
/// (no detail) when a verified notification is not yet durably recorded or applied for a reason a
/// retry can fix, so Telebirr sends it again. Order payments are written to an inbox row first;
/// the same Lambda, invoked by an EventBridge schedule, replays inbox rows left pending.
/// AOT-safe: System.Text.Json source-gen + System.Security.Cryptography only.
/// </summary>
public class TelebirrWebhookFunction
{
    private const int MaxTimestampSkewSeconds = 300;

    /// <summary>
    /// ESC-45 (D3): a verified order notification older than 300 s is still processed up to this
    /// age, because Telebirr may resend the original notify_time; the inbox key, the conditional
    /// funding write and the attempt binding make a replay harmless. Below the 30-day inbox TTL.
    /// </summary>
    internal static readonly TimeSpan LateWindow = TimeSpan.FromHours(48);

    /// <summary>ESC-45 (D6): no apply starts with less than this left; the 500 makes Telebirr (or the sweep) retry.</summary>
    internal static readonly TimeSpan ApplyReserve = TimeSpan.FromSeconds(4);

    /// <summary>ESC-45: the sweep claims no row with less than this left.</summary>
    internal static readonly TimeSpan SweepClaimReserve = TimeSpan.FromSeconds(5);

    /// <summary>ESC-45: the sweep's budget when the remaining time is unknown (test contexts).</summary>
    private static readonly TimeSpan SweepUnknownBudget = TimeSpan.FromSeconds(10);

    private const int SweepPageSize = 25;
    private const int SweepMaxRows = 100;

    /// <summary>The action the EventBridge schedule sends; only a direct invocation can carry it.</summary>
    internal const string ReplayAction = "webhook_inbox_replay";

    private const string TelebirrActor = "telebirr";
    private const string OrderEscrowPrefix = "escrow-order-";
    private const string UnmatchedToken = "ORDER_PAYMENT_UNMATCHED";
    private const string DuplicateToken = "ORDER_PAYMENT_DUPLICATE";
    private const string UnprocessedToken = "ORDER_PAYMENT_UNPROCESSED";

    /// <summary>
    /// Follow-up bookkeeping failed after a payment was applied (not a refund case, so it is
    /// deliberately outside the order-payment-problems alarm).
    /// </summary>
    private const string BookkeepingToken = "ORDER_PAYMENT_BOOKKEEPING_FAILED";

    /// <summary>
    /// ESC-45 (D5): a verified order payment that is recorded in the inbox but not applied yet.
    /// Not alarmed: the sweep (or a redelivery) applies it; the page comes only at give-up.
    /// </summary>
    private const string ReplayPendingToken = "ORDER_PAYMENT_REPLAY_PENDING";

    /// <summary>ESC-45 (D8): the order hold could not be written (the sweep writes it again).</summary>
    private const string HoldFailedToken = "ORDER_PAYMENT_HOLD_FAILED";

    /// <summary>ESC-45 (D5): one line per sweep run, the heartbeat the stalled alarm watches.</summary>
    internal const string ReplayRunToken = "TELEBIRR_INBOX_REPLAY_RUN";

    private const string SuccessBody = "{\"code\":\"0\",\"msg\":\"success\"}";
    private const string ErrorBody = "{\"code\":\"1\",\"msg\":\"error\"}";

    /// <summary>
    /// Order statuses in which a redelivered payment must not repair side effects: the order is
    /// over (or paying out), so a new escrow or another stock decrement would be wrong.
    /// </summary>
    private static readonly HashSet<string> NoRepairOrderStatuses = new(StringComparer.Ordinal)
    {
        "rejected", "cancelled", "payment_failed", "disbursing", "settled", "dispute_resolved"
    };

    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IEscrowIdempotencyRepository _idempotencyRepo;
    private readonly ITelebirrSignatureService _signer;
    private readonly HashSet<string> _allowedSourceIps;
    private readonly string? _merchantCode;
    private readonly IUserRepository? _userRepo;
    private readonly bool _requirePayerMatch;

    // ESC-01: order payments. Null order wiring leaves them unprocessed (alarmed), never funded blind.
    private readonly IOrderCheckoutRepository? _orderCheckoutRepo;
    private readonly IOrderRepository? _orderRepo;
    private readonly OrderFundingService? _orderFunding;
    private readonly ITransactionAuditRepository? _txAuditRepo;

    // ESC-45: the inbox of verified order notifications, and the clock the freshness and replay
    // rules read (a test clock in tests).
    private readonly IWebhookInboxRepository? _inboxRepo;
    private readonly TimeProvider _clock;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public TelebirrWebhookFunction()
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
        _idempotencyRepo = new EscrowIdempotencyRepository(dynamoDb, idempotencyTable);

        var config = TelebirrGatewayFactory.LoadConfigOrThrow();
        _signer = new TelebirrSignatureService(null, config.TelebirrPublicKeyPem, config.Padding);
        _allowedSourceIps = new HashSet<string>(config.AllowedSourceIps, StringComparer.Ordinal);
        _merchantCode = config.MerchantCode;

        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        _userRepo = string.IsNullOrEmpty(usersTable) ? null : new UserRepository(dynamoDb, usersTable);
        _requirePayerMatch = string.Equals(
            Environment.GetEnvironmentVariable("TELEBIRR_REQUIRE_PAYER_MATCH"), "true", StringComparison.OrdinalIgnoreCase);

        // ESC-01: order checkouts live in the idempotency table; funding needs the orders
        // and crops tables (stock) and records a transaction-audit row.
        _orderCheckoutRepo = new OrderCheckoutRepository(dynamoDb, idempotencyTable);
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE");
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE");
        if (!string.IsNullOrEmpty(ordersTable) && !string.IsNullOrEmpty(cropsTable))
        {
            var orderRepo = new OrderRepository(dynamoDb, ordersTable);
            var orderEscrowService = new EscrowService(
                _escrowRepo, _auditRepo, idempotencyRepo: null, telebirrGateway: null,
                userRepo: _userRepo, orderRepo: orderRepo);
            _orderRepo = orderRepo;
            _orderFunding = new OrderFundingService(orderRepo, new CropRepository(dynamoDb, cropsTable), orderEscrowService);
        }

        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _txAuditRepo = string.IsNullOrEmpty(txAuditTable) ? null : new TransactionAuditRepository(dynamoDb, txAuditTable);

        _inboxRepo = new WebhookInboxRepository(dynamoDb, idempotencyTable);
        _clock = TimeProvider.System;
    }

    /// <summary>Constructor for testing.</summary>
    public TelebirrWebhookFunction(
        IEscrowRepository escrowRepo,
        IEscrowAuditRepository auditRepo,
        IEscrowIdempotencyRepository idempotencyRepo,
        ITelebirrSignatureService signer,
        IEnumerable<string>? allowedSourceIps = null,
        string? merchantCode = null,
        IUserRepository? userRepo = null,
        bool requirePayerMatch = false,
        IOrderCheckoutRepository? orderCheckoutRepo = null,
        IOrderRepository? orderRepo = null,
        OrderFundingService? orderFunding = null,
        ITransactionAuditRepository? txAuditRepo = null,
        IWebhookInboxRepository? inboxRepo = null,
        TimeProvider? clock = null)
    {
        _escrowRepo = escrowRepo;
        _auditRepo = auditRepo;
        _idempotencyRepo = idempotencyRepo;
        _signer = signer;
        _allowedSourceIps = new HashSet<string>(allowedSourceIps ?? Array.Empty<string>(), StringComparer.Ordinal);
        _merchantCode = merchantCode;
        _userRepo = userRepo;
        _requirePayerMatch = requirePayerMatch;
        _orderCheckoutRepo = orderCheckoutRepo;
        _orderRepo = orderRepo;
        _orderFunding = orderFunding;
        _txAuditRepo = txAuditRepo;
        _inboxRepo = inboxRepo;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        // ESC-45: the EventBridge schedule invokes this Lambda directly. API Gateway always sets
        // requestContext, so the public route can never reach the sweep (and the sweep takes no
        // input from its event anyway).
        if (IsReplayInvocation(request))
        {
            return await ReplayInboxAsync(context);
        }

        Ack ack;
        try
        {
            ack = await ProcessAsync(request, context);
        }
        catch (Exception ex)
        {
            // Before verification nothing is retried (SEC-07: nothing leaks to the caller).
            context.Logger.LogError($"Telebirr webhook: internal error [{ex.GetType().Name}].");
            ack = Ack.Done;
        }

        return ack == Ack.RetryLater ? Respond(500, ErrorBody) : Respond(200, SuccessBody);
    }

    /// <summary>What the webhook answers: 200, or 500 so Telebirr sends the notification again.</summary>
    private enum Ack
    {
        Done,
        RetryLater
    }

    /// <summary>How old a verified notification is (D3).</summary>
    private enum Timing
    {
        Fresh,
        Late,
        OutOfWindow
    }

    private async Task<Ack> ProcessAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        // Layer 1 (optional): source IP allow-list. Skipped when unconfigured (local).
        if (_allowedSourceIps.Count > 0)
        {
            var ip = request.RequestContext?.Identity?.SourceIp;
            if (ip == null || !_allowedSourceIps.Contains(ip))
            {
                context.Logger.LogWarning("Telebirr webhook: source IP not allow-listed.");
                return Ack.Done;
            }
        }

        if (string.IsNullOrEmpty(request.Body))
        {
            return Ack.Done;
        }

        // ESC-45 (D0): Telebirr's notify is a flat body; one map is both verified and read.
        if (!TelebirrNotifyParser.TryParse(request.Body, out var parsed, out var parseError))
        {
            context.Logger.LogWarning($"Telebirr webhook: body refused ({parseError}).");
            return Ack.Done;
        }

        if (parsed!.Fields.GetValueOrDefault("merch_order_id") is not { Length: > 0 } || string.IsNullOrEmpty(parsed.Sign))
        {
            context.Logger.LogWarning("Telebirr webhook: missing merch_order_id or sign.");
            return Ack.Done;
        }

        // Layer 2 (primary): verify the signature with Telebirr's public key, over exactly the
        // fields read below. Telebirr signs the transaction id as trans_id but has been seen
        // sending transId; that rename is tried only when trans_id itself is absent.
        var fields = parsed.Fields;
        if (!_signer.VerifyFlat(fields, parsed.Sign, out var padding))
        {
            var renamed = TelebirrNotifyParser.WithTransIdAlias(fields);
            if (renamed == null || !_signer.VerifyFlat(renamed, parsed.Sign, out padding))
            {
                context.Logger.LogWarning($"Telebirr webhook: signature verification failed. fields={DescribeFields(fields)}");
                return Ack.Done;
            }

            fields = renamed;
        }

        var n = TelebirrNotifyParser.ToBiz(fields);

        // Layer 3: freshness of notify_time (seconds or milliseconds by digit range). Fail closed:
        // a notification without a parseable time is treated like a stale one (replay defence).
        if (TelebirrNotifyParser.SentAt(fields) is not { } sentAt)
        {
            context.Logger.LogWarning("Telebirr webhook: missing or unparseable notify_time.");
            return Ack.Done;
        }

        var age = _clock.GetUtcNow() - sentAt;
        var timing = Math.Abs(age.TotalSeconds) <= MaxTimestampSkewSeconds ? Timing.Fresh
            : age > TimeSpan.Zero && age <= LateWindow ? Timing.Late
            : Timing.OutOfWindow;

        // Layer 3b: the notification must be for our merchant account.
        if (!string.IsNullOrEmpty(_merchantCode)
            && !string.Equals(n.MerchCode, _merchantCode, StringComparison.Ordinal))
        {
            context.Logger.LogWarning("Telebirr webhook: merch_code does not match this merchant.");
            return Ack.Done;
        }

        context.Logger.LogInformation(
            $"Telebirr webhook: verified (padding={padding.ToString().ToLowerInvariant()}, "
            + $"late={(timing == Timing.Fresh ? "false" : "true")}, age={age.TotalSeconds:F0}s).");

        // Verified from here on: a failure a retry can fix answers 500 (D1).
        try
        {
            return await HandleVerifiedAsync(n, timing, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Telebirr webhook: verified notification not processed, answered 500 for a retry "
                + $"merchOrderId={SafeLogId(n.MerchOrderId)} paymentOrderId={SafeLogId(n.PaymentOrderId)} reason={ex.GetType().Name}");
            return Ack.RetryLater;
        }
    }

    private async Task<Ack> HandleVerifiedAsync(TelebirrWebhookBiz n, Timing timing, ILambdaContext context)
    {
        var merchOrderId = n.MerchOrderId!;

        // ESC-01: an order checkout (server-generated merch_order_id) funds the order itself.
        // A failed lookup must never drop a verified payment silently: it is logged with the
        // alarmable token and the ids needed to reconcile it by hand.
        OrderCheckoutMapping? checkout = null;
        if (_orderCheckoutRepo != null)
        {
            try
            {
                checkout = await _orderCheckoutRepo.GetMappingAsync(merchOrderId);
            }
            catch (InvalidDataException)
            {
                // A malformed mapping row: a retry cannot fix it.
                context.Logger.LogError(
                    $"{UnprocessedToken} merchOrderId={SafeLogId(merchOrderId)} "
                    + $"paymentOrderId={SafeLogId(n.PaymentOrderId)} reason=mapping_invalid");
                return Ack.Done;
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"{UnprocessedToken} merchOrderId={SafeLogId(merchOrderId)} "
                    + $"paymentOrderId={SafeLogId(n.PaymentOrderId)} reason=mapping_lookup_{ex.GetType().Name}");
                return Ack.RetryLater;
            }
        }

        if (checkout != null)
        {
            return await ProcessOrderPaymentAsync(n, checkout, timing, context);
        }

        // The /escrow/initiate path has no inbox: only a fresh notification is processed (300 s).
        if (timing != Timing.Fresh)
        {
            context.Logger.LogWarning("Telebirr webhook: stale notify_time for an escrow payment.");
            return Ack.Done;
        }

        return await ProcessEscrowPaymentAsync(n, context);
    }

    /// <summary>
    /// The <c>/escrow/initiate</c> path (not usable with live Telebirr: its merch_order_id is the
    /// client idempotency key, which breaks the documented <c>^[A-Za-z0-9]+$</c>; ESC-45). Any
    /// exception escapes and answers 500: the path writes its dedupe key only after the escrow CAS
    /// commits, so a retry is safe.
    /// </summary>
    private async Task<Ack> ProcessEscrowPaymentAsync(TelebirrWebhookBiz n, ILambdaContext context)
    {
        var merchOrderId = n.MerchOrderId!;

        // Resolve escrow via the idempotency map (merch_order_id == initiate idempotency key).
        var idem = await _idempotencyRepo.GetAsync(merchOrderId);
        var escrowId = idem != null && idem.TryGetValue("escrowId", out var eid) ? eid?.ToString() : null;
        if (string.IsNullOrEmpty(escrowId))
        {
            context.Logger.LogWarning("Telebirr webhook: no escrow for merch_order_id.");
            return Ack.Done;
        }

        var escrow = await _escrowRepo.GetByIdAsync(escrowId);
        if (escrow == null)
        {
            context.Logger.LogWarning("Telebirr webhook: escrow record not found.");
            return Ack.Done;
        }

        // Layer 4: replay rejection (composite of order + telebirr ref + status).
        // NOTE: the dedupe key is recorded only AFTER the state transition commits
        // (see below). Recording it here would permanently lock out webhook
        // redelivery if the escrow update failed transiently, stranding a paid
        // payment in 'initiated'. The escrow CAS (expectedVersion 'initiated') is
        // the primary idempotency guard against concurrent duplicate deliveries.
        var replayKey = $"webhook:{merchOrderId}:{n.PaymentOrderId}:{n.TradeStatus}";

        var isSuccess = string.Equals(n.TradeStatus, "Completed", StringComparison.OrdinalIgnoreCase);

        // Only a documented failure cancels the escrow. Pending ("payment completed, awaiting order
        // synchronization") or Paying must not: the Completed that follows would then no-op on the
        // CAS and strand a paid escrow (ESC-45).
        if (!isSuccess && !IsFailedStatus(n.TradeStatus))
        {
            context.Logger.LogInformation(
                $"Telebirr webhook: status {SafeLogId(n.TradeStatus)} for an escrow payment; nothing to do.");
            return Ack.Done;
        }
        var createdAt = GetString(escrow, "createdAt");

        // Payer binding: the wallet that paid should be the buyer's registered phone.
        // A shared or borrowed account paying from someone else's wallet is the
        // signal KYC exists to catch. Enforced only when TELEBIRR_REQUIRE_PAYER_MATCH
        // is on (the field must be confirmed in Telebirr's live payload first);
        // otherwise a mismatch is recorded for the fraud review queue.
        if (isSuccess && !await PayerMatchesBuyerAsync(
                GetString(escrow, "buyerId"), escrowId, n.Msisdn, "initiated", context))
        {
            return Ack.Done;
        }

        // Amount integrity: a forged/replayed notification must not carry a different amount.
        if (isSuccess)
        {
            var escrowAmount = GetDecimal(escrow, "amount");
            if (!decimal.TryParse(n.TotalAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var notified)
                || Math.Abs(escrowAmount - notified) > 0.01m)
            {
                context.Logger.LogWarning("Telebirr webhook: amount mismatch — rejecting.");
                await AppendAuditAsync(escrowId, "webhook_amount_mismatch", "initiated", "initiated",
                    metadata: $"escrow={escrowAmount},webhook={n.TotalAmount}");
                return Ack.Done;
            }
        }

        try
        {
            if (isSuccess)
            {
                var telebirrRef = n.PaymentOrderId ?? "";
                await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "escrowed", "initiated",
                    new Dictionary<string, object>
                    {
                        { "telebirrRef", telebirrRef },
                        { "telebirrHoldRef", telebirrRef },
                        { "version", 2 },
                    });
                await AppendAuditAsync(escrowId, "webhook_hold_success", "initiated", "escrowed", telebirrRef);
                await _idempotencyRepo.UpdateAsync(merchOrderId, "completed");
            }
            else
            {
                await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "cancelled", "initiated");
                await AppendAuditAsync(escrowId, "webhook_hold_failed", "initiated", "cancelled",
                    metadata: n.TradeStatus);
            }

            // The transition committed: only now record the replay/dedupe key so any
            // transient failure above leaves the webhook safely retryable.
            var firstDelivery = await _idempotencyRepo.StoreAsync(replayKey, escrowId, "webhook_processed");
            if (!firstDelivery)
            {
                context.Logger.LogInformation("Telebirr webhook: duplicate delivery recorded post-processing.");
            }
        }
        catch (ConditionalCheckFailedException)
        {
            // Escrow already moved on (not 'initiated'): a prior delivery already
            // processed this payment. Treat as an idempotent no-op.
            context.Logger.LogInformation("Telebirr webhook: escrow not in 'initiated'; no-op.");
        }

        return Ack.Done;
    }

    /// <summary>A status Telebirr documents as a failed payment (Failure) or that our earlier simulator sent (Failed), or an expired one.</summary>
    private static bool IsFailedStatus(string? tradeStatus)
        => tradeStatus is not null
           && (tradeStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase)
               || tradeStatus.Equals("Failure", StringComparison.OrdinalIgnoreCase)
               || tradeStatus.Equals("Expired", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The field names of a body that did not verify, for diagnosis: at most 20, each through
    /// <see cref="MoneyAlarmLog.LogId"/>, so an unverified body can never spell an alarm token.
    /// </summary>
    private static string DescribeFields(IReadOnlyDictionary<string, string> fields)
        => string.Join(",", fields.Keys.Order(StringComparer.Ordinal).Take(20).Select(SafeLogId));

    /// <summary>
    /// False when the notification must be rejected. Records a mismatch (or a
    /// missing payer while a match is required) in the escrow audit trail, with
    /// <paramref name="state"/> as the (unchanged) from/to state.
    /// </summary>
    private async Task<bool> PayerMatchesBuyerAsync(
        string buyerId, string escrowId, string? payerMsisdn, string state, ILambdaContext context)
    {
        if (string.IsNullOrWhiteSpace(payerMsisdn))
        {
            if (!_requirePayerMatch)
            {
                return true;
            }
            context.Logger.LogWarning("Telebirr webhook: payer phone required but absent.");
            await AppendAuditAsync(escrowId, "webhook_payer_unbound", state, state);
            return false;
        }

        if (_userRepo == null)
        {
            context.Logger.LogWarning("Telebirr webhook: payer phone present but no user repository to check it.");
            return !_requirePayerMatch;
        }

        var buyer = string.IsNullOrEmpty(buyerId) ? null : await _userRepo.GetUserByIdAsync(buyerId);
        var payer = Validation.RequestValidator.NormalizePhoneNumber(payerMsisdn) ?? payerMsisdn.Trim();
        if (buyer != null && string.Equals(buyer.PhoneNumber, payer, StringComparison.Ordinal))
        {
            return true;
        }

        context.Logger.LogWarning("Telebirr webhook: payer phone does not match the buyer's registered phone.");
        await AppendAuditAsync(escrowId, "webhook_payer_mismatch", state, state,
            metadata: $"buyer={buyerId},payerSuffix={payer[^Math.Min(4, payer.Length)..]}");
        return !_requirePayerMatch;
    }

    // ─── ESC-01: live order payments ─────────────────────────────────────

    /// <summary>A verified notification for an order checkout, with its binding.</summary>
    private sealed record OrderPayment(
        OrderCheckoutMapping Checkout, string PaymentOrderId, string? TransId, string TradeStatus, string EscrowId);

    /// <summary>What applying an order payment came to: the inbox status, and whether a retry can change it.</summary>
    private readonly record struct ApplyOutcome(string Status, bool IsFinal, bool Transient)
    {
        public static ApplyOutcome Final(string status) => new(status, true, false);
    }

    /// <summary>
    /// ESC-01, ESC-45: a verified notification whose merch_order_id is an order checkout. Its inbox
    /// row is read first: an earlier refusal keeps its verdict, and a late redelivery of a
    /// notification already handled changes nothing (no apply, no hold, no alarm). A verified
    /// <c>Completed</c> holds the order out of the 24 h <c>payment_failed</c> (D8), the notification is
    /// recorded as a pending inbox row, then the order is funded exactly once, or the reason it was
    /// not is recorded. Anything a retry can fix answers 500 and stays pending for the sweep.
    /// </summary>
    private async Task<Ack> ProcessOrderPaymentAsync(
        TelebirrWebhookBiz biz, OrderCheckoutMapping checkout, Timing timing, ILambdaContext context)
    {
        var payment = new OrderPayment(
            checkout, biz.PaymentOrderId ?? "", biz.TransId, biz.TradeStatus ?? "",
            OrderEscrowPrefix + checkout.OrderId);
        var inboxKey = WebhookInboxRepository.KeyOf(checkout.MerchOrderId, payment.PaymentOrderId, payment.TradeStatus);

        if (_inboxRepo == null)
        {
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=inbox_not_configured");
            return Ack.Done;
        }

        WebhookInboxRow? existing;
        try
        {
            existing = await _inboxRepo.GetAsync(inboxKey);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=inbox_read_{ex.GetType().Name}");
            return Ack.RetryLater;
        }

        if (KeepsEarlierVerdict(existing, timing, payment, context))
        {
            return Ack.Done;
        }

        if (timing == Timing.OutOfWindow)
        {
            if (existing != null)
            {
                context.Logger.LogInformation(existing.Marker == WebhookInbox.Exhausted
                    ? $"Telebirr out-of-window redelivery of an order payment the inbox gave up on (replay_exhausted already alarmed; requeue it to apply): {Describe(payment)}."
                    : $"Telebirr out-of-window redelivery of an order payment pending in the inbox (the sweep applies it): {Describe(payment)}.");
                return Ack.Done;
            }

            if (!IsCompleted(payment.TradeStatus))
            {
                // No money behind it: nothing to alarm.
                context.Logger.LogInformation(
                    $"Telebirr out-of-window {SafeLogId(payment.TradeStatus)} notification ignored: {Describe(payment)}.");
                return Ack.Done;
            }

            // Too old (or from the future) to process, and never seen: a verified payment is not
            // dropped silently.
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=stale_notify_time");
            await TryRecordUnprocessedAsync(payment, "stale_notify_time", context);
            return Ack.Done;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (IsCompleted(payment.TradeStatus))
        {
            await TryHoldOrderAsync(payment, now + WebhookInbox.ReplayHorizon, context);
        }

        if (existing == null)
        {
            bool recorded;
            try
            {
                recorded = await _inboxRepo.RecordAsync(
                    inboxKey, payment.EscrowId, checkout.OrderId, checkout.MerchOrderId, biz, now);
            }
            catch (Exception ex)
            {
                // Nothing durable holds this payment yet: alarm now, and let Telebirr send it again.
                context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=inbox_write_{ex.GetType().Name}");
                await TryRecordUnprocessedAsync(payment, ex.GetType().Name, context);
                return Ack.RetryLater;
            }

            if (!recorded)
            {
                // A concurrent delivery recorded it first: decide by what it holds now.
                existing = await _inboxRepo.GetAsync(inboxKey);
                if (KeepsEarlierVerdict(existing, timing, payment, context))
                {
                    return Ack.Done;
                }
            }
        }

        // D6: never start applying with too little time left; the row stays pending.
        if (context.RemainingTime > TimeSpan.Zero && context.RemainingTime < ApplyReserve)
        {
            context.Logger.LogWarning($"{ReplayPendingToken} {Describe(payment)} reason=low_time");
            return Ack.RetryLater;
        }

        ApplyOutcome outcome;
        try
        {
            outcome = await ApplyOrderPaymentAsync(biz, payment, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{ReplayPendingToken} {Describe(payment)} reason={ex.GetType().Name}");
            return Ack.RetryLater;
        }

        if (!outcome.IsFinal)
        {
            await TryMarkUnprocessedAsync(inboxKey, payment, context);
            return outcome.Transient ? Ack.RetryLater : Ack.Done;
        }

        await FinalizeInboxAsync(inboxKey, outcome.Status, payment,
            existing?.Marker == WebhookInbox.Exhausted || existing?.ExhaustedAt != null, context);
        return Ack.Done;
    }

    /// <summary>
    /// True when this delivery must change nothing: the payment was refused before (it may be
    /// refunded by hand already), or it is a late redelivery of a notification already handled.
    /// </summary>
    private static bool KeepsEarlierVerdict(
        WebhookInboxRow? existing, Timing timing, OrderPayment payment, ILambdaContext context)
    {
        if (existing is not { IsFinal: true })
        {
            return false;
        }

        if (existing.IsRefusal)
        {
            context.Logger.LogInformation(
                $"Telebirr redelivery of an order payment already judged {SafeLogId(existing.Status)}; verdict kept: {Describe(payment)}.");
            return true;
        }

        if (timing != Timing.Fresh)
        {
            context.Logger.LogInformation(
                $"Telebirr late redelivery of an order payment already handled ({SafeLogId(existing.Status)}); nothing to do: {Describe(payment)}.");
            return true;
        }

        return false;
    }

    private static bool IsCompleted(string? tradeStatus)
        => string.Equals(tradeStatus, "Completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// D8: hold the order out of the order cron's 24 h <c>payment_failed</c> until
    /// <paramref name="holdUntilUtc"/>, for this payment attempt only. Best effort: the sweep writes
    /// it again at its next claim.
    /// </summary>
    private async Task TryHoldOrderAsync(OrderPayment payment, DateTime holdUntilUtc, ILambdaContext context)
    {
        if (_orderRepo == null)
        {
            return;
        }

        try
        {
            await _orderRepo.TryHoldForPaymentAsync(
                payment.Checkout.OrderId, WebhookInbox.Iso(holdUntilUtc), payment.Checkout.PaymentSubmittedAt);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{HoldFailedToken} {Describe(payment)} reason={ex.GetType().Name}");
        }
    }

    private async Task TryMarkUnprocessedAsync(string inboxKey, OrderPayment payment, ILambdaContext context)
    {
        try
        {
            await _inboxRepo!.MarkUnprocessedAsync(inboxKey);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{BookkeepingToken} {Describe(payment)} step=inbox_unprocessed reason={ex.GetType().Name}");
        }
    }

    /// <summary>Write a final outcome to the inbox row; a failure leaves it pending, and the sweep finishes it.</summary>
    private async Task FinalizeInboxAsync(
        string inboxKey, string outcome, OrderPayment payment, bool wasExhausted, ILambdaContext context)
    {
        try
        {
            await _inboxRepo!.FinalizeAsync(inboxKey, outcome);
        }
        catch (Exception ex)
        {
            // The notification was handled; only its inbox row is behind. Not a refund case.
            context.Logger.LogWarning(
                $"{BookkeepingToken} {Describe(payment)} step=inbox outcome={outcome} reason={ex.GetType().Name}");
        }

        if (wasExhausted && outcome is "applied" or "applied_again")
        {
            context.Logger.LogWarning(
                $"Telebirr payment applied after its inbox row was given up; stop any manual refund: {Describe(payment)}.");
        }
    }

    /// <summary>Apply an order payment notification; returns the inbox outcome.</summary>
    private async Task<ApplyOutcome> ApplyOrderPaymentAsync(
        TelebirrWebhookBiz biz, OrderPayment payment, ILambdaContext context)
    {
        if (_orderRepo == null || _orderFunding == null)
        {
            // A deployment fault: alarmed now, and a retry cannot fix it.
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=orders_not_configured");
            return new ApplyOutcome(WebhookInbox.Unprocessed, IsFinal: false, Transient: false);
        }

        if (!IsCompleted(payment.TradeStatus))
        {
            return ApplyOutcome.Final(await RecordNotCompletedAsync(payment));
        }

        if (payment.PaymentOrderId.Length == 0)
        {
            return ApplyOutcome.Final(await RecordUnappliedAsync(payment, UnmatchedToken, "no_payment_order_id", context));
        }

        // Classify against the order's current state. A lost race (the order changed between
        // the read and the conditional write) is read and classified once more; it is never
        // treated as done.
        for (var pass = 0; pass < 2; pass++)
        {
            var order = await _orderRepo.GetByIdAsync(payment.Checkout.OrderId, consistentRead: true);
            var verdict = await TryApplyToOrderAsync(order, biz, payment, context);
            if (verdict != null)
            {
                return ApplyOutcome.Final(verdict);
            }
        }

        // Transient: the sweep or a redelivery classifies it again (D5: no page here).
        context.Logger.LogWarning($"{ReplayPendingToken} {Describe(payment)} reason=funding_conflict");
        return new ApplyOutcome(WebhookInbox.Unprocessed, IsFinal: false, Transient: true);
    }

    /// <summary>
    /// Fund the order with this payment, repair a funding this payment already made, or record
    /// why it cannot apply. Returns the inbox outcome, or null when the funding write lost a
    /// race and the order must be read again.
    /// </summary>
    private async Task<string?> TryApplyToOrderAsync(
        Dictionary<string, object>? order, TelebirrWebhookBiz biz, OrderPayment payment, ILambdaContext context)
    {
        if (order == null)
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "order_missing", context);
        }

        // Decide by the payment reference first, in any status: a redelivery after the farmer
        // has already responded is still the same payment.
        var recordedPayment = GetString(order, OrderFundingService.PaymentOrderIdField);
        if (recordedPayment.Length > 0)
        {
            return string.Equals(recordedPayment, payment.PaymentOrderId, StringComparison.Ordinal)
                ? await RepairRedeliveryAsync(order, payment, context)
                : await RecordUnappliedAsync(payment, DuplicateToken, "already_paid", context);
        }

        var status = GetString(order, "status");
        if (status != "payment_submitted")
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, $"status_{status}", context);
        }

        // Only a payment that is about to fund the order is checked against it; a redelivery of
        // an applied payment is not re-judged (the buyer may have changed phone since).
        var mismatch = await CheckPaymentAgainstOrderAsync(order, biz, payment, context);
        if (mismatch != null)
        {
            return mismatch;
        }

        var funded = await _orderFunding!.TryFundFromPaymentAsync(
            payment.Checkout.OrderId, payment.Checkout.MerchOrderId, payment.PaymentOrderId, payment.TransId);
        if (funded == null)
        {
            return null;
        }

        // The order is funded now. What follows is bookkeeping: a failure there is logged
        // without the refund alarm, because refunding a funded order would be wrong.
        try
        {
            await _orderFunding.EnsureFundedSideEffectsAsync(
                funded, TelebirrActor, AuditContext.ForSystem(TelebirrActor), context);
            await EnsurePaymentVerifiedAuditAsync(funded, payment, lookForEarlierRow: false, context);
            await FinishCheckoutAsync(payment);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{BookkeepingToken} {Describe(payment)} step=after_funding reason={ex.GetType().Name}");
        }

        context.Logger.LogInformation($"Order funded by a Telebirr payment: {Describe(payment)}.");
        return "applied";
    }

    /// <summary>
    /// The currency, amount and payer checks, and that the order still matches its checkout.
    /// Returns the inbox outcome of a mismatch, or null when the payment may fund the order.
    /// </summary>
    private async Task<string?> CheckPaymentAgainstOrderAsync(
        Dictionary<string, object> order, TelebirrWebhookBiz biz, OrderPayment payment, ILambdaContext context)
    {
        if (!string.Equals(biz.TransCurrency, "ETB", StringComparison.OrdinalIgnoreCase))
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "currency", context);
        }

        if (!decimal.TryParse(biz.TotalAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var notified)
            || Math.Abs(notified - payment.Checkout.Amount) > 0.01m)
        {
            await AppendAuditAsync(payment.EscrowId, "webhook_amount_mismatch", "payment_submitted", "payment_submitted",
                metadata: $"checkout={payment.Checkout.Amount},webhook={biz.TotalAmount}");
            return await RecordUnappliedAsync(payment, UnmatchedToken, "amount", context);
        }

        if (!await PayerMatchesBuyerAsync(
                payment.Checkout.BuyerId, payment.EscrowId, biz.Msisdn, "payment_submitted", context))
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "payer", context);
        }

        if (!string.Equals(GetString(order, "buyerId"), payment.Checkout.BuyerId, StringComparison.Ordinal)
            || Math.Abs(RoundMoney(GetDecimal(order, "orderTotal")) - payment.Checkout.Amount) > 0.01m)
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "order_changed", context);
        }

        // The checkout belongs to the payment attempt it was opened in. After payment_failed and
        // a new payment_submitted, a payment from the earlier attempt never funds the order: it
        // may already have been refunded by hand.
        if (!string.Equals(GetString(order, "paymentSubmittedAt"), payment.Checkout.PaymentSubmittedAt ?? "",
                StringComparison.Ordinal))
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "stale_checkout", context);
        }

        return null;
    }

    /// <summary>
    /// A durable escrow-audit row for a payment that could not be processed, next to its log
    /// line. Best effort: the same outage can stop this write too, and then the log line is the
    /// only record.
    /// </summary>
    private async Task TryRecordUnprocessedAsync(OrderPayment payment, string reason, ILambdaContext context)
    {
        try
        {
            await AppendAuditAsync(payment.EscrowId, "order_payment_unprocessed", "", "",
                telebirrRef: payment.PaymentOrderId.Length > 0 ? payment.PaymentOrderId : null,
                metadata: $"orderId={payment.Checkout.OrderId},merchOrderId={payment.Checkout.MerchOrderId},"
                          + $"amount={payment.Checkout.Amount},reason={reason}");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Audit row for an unprocessed order payment not written: {Describe(payment)} reason={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Redelivery of the payment that funded the order: finish what an earlier delivery did not
    /// (stock and escrow, both idempotent per order), but only while the order is live. A
    /// rejected, cancelled, settled or resolved order must not get a new escrow or lose stock
    /// again. The transaction-audit row of the funding is written in every status (ESC-47).
    /// </summary>
    private async Task<string> RepairRedeliveryAsync(
        Dictionary<string, object> order, OrderPayment payment, ILambdaContext context)
    {
        var status = GetString(order, "status");
        if (NoRepairOrderStatuses.Contains(status))
        {
            context.Logger.LogInformation(
                $"Telebirr redelivery for an order that is {SafeLogId(status)}; nothing to repair: {Describe(payment)}.");
            // ESC-47: the payment_verified row records a funding that happened, so it is written
            // whatever the order became since.
            await EnsurePaymentVerifiedAuditAsync(order, payment, lookForEarlierRow: true, context);
            return "applied_again";
        }

        try
        {
            await _orderFunding!.EnsureFundedSideEffectsAsync(
                order, TelebirrActor, AuditContext.ForSystem(TelebirrActor), context);
            await FinishCheckoutAsync(payment);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{BookkeepingToken} {Describe(payment)} step=repair reason={ex.GetType().Name}");
        }

        // ESC-47: after the stock and escrow repair, so a slow audit table never uses up its time.
        await EnsurePaymentVerifiedAuditAsync(order, payment, lookForEarlierRow: true, context);
        return "applied_again";
    }

    /// <summary>
    /// ESC-47: the transaction-audit <c>payment_verified</c> row of a Telebirr funding, written once.
    /// Its key is the order's <c>paymentVerifiedAt</c>, so the funding delivery and every redelivery
    /// that repairs it write the same row, and only the first write lands. A repair first looks for
    /// the row under any timestamp (servers before ESC-47 wrote it at the time of the write) and
    /// writes nothing if it is there or cannot be read. Never throws: a failure (read or write) is
    /// logged with the bookkeeping token and the payment ids, and the next redelivery tries again.
    /// </summary>
    private async Task EnsurePaymentVerifiedAuditAsync(
        Dictionary<string, object> order, OrderPayment payment, bool lookForEarlierRow, ILambdaContext context)
    {
        if (_txAuditRepo == null)
        {
            return;
        }

        var verifiedAt = GetString(order, "paymentVerifiedAt");
        if (verifiedAt.Length == 0)
        {
            // Not expected: the funding write sets it together with the payment reference.
            context.Logger.LogWarning(
                $"{BookkeepingToken} {Describe(payment)} step=payment_verified_audit reason=no_payment_verified_at");
            return;
        }

        if (lookForEarlierRow)
        {
            try
            {
                var trail = await _txAuditRepo.GetByTransactionAsync(payment.Checkout.OrderId);
                if (trail.Any(e => GetString(e, "action") == "payment_verified"))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"{BookkeepingToken} {Describe(payment)} step=payment_verified_audit reason={ex.GetType().Name}");
                return;
            }
        }

        var written = await TransactionAuditWriter.EmitOnceAsync(
            _txAuditRepo, context, payment.Checkout.OrderId, "order", payment.Checkout.OrderId,
            "payment_verified", "payment_submitted", "funded", AuditContext.ForSystem(TelebirrActor), verifiedAt,
            metadata: $"merchOrderId={payment.Checkout.MerchOrderId},paymentOrderId={payment.PaymentOrderId}");
        if (!written)
        {
            // The writer's own line has no order or payment id; this one lets ops find the order.
            context.Logger.LogWarning(
                $"{BookkeepingToken} {Describe(payment)} step=payment_verified_audit reason=write_failed");
        }
    }

    /// <summary>A payment that funded (or already funded) the order closes its checkout.</summary>
    private async Task FinishCheckoutAsync(OrderPayment payment)
    {
        await _orderCheckoutRepo!.CloseSlotForMerchOrderAsync(
            payment.Checkout.OrderId, payment.Checkout.MerchOrderId, OrderCheckoutOutcome.Paid);
        await _idempotencyRepo.UpdateAsync(payment.Checkout.MerchOrderId, "completed");
    }

    /// <summary>
    /// A notification that is not a completed payment: only recorded. A "Failed" try leaves
    /// the checkout open until its window ends, because Telebirr may still accept the same
    /// payment (a wrong PIN or a low balance can be retried); the buyer gets the same URL back
    /// until then. The order stays payment_submitted.
    /// </summary>
    private async Task<string> RecordNotCompletedAsync(OrderPayment payment)
    {
        var failed = string.Equals(payment.TradeStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(payment.TradeStatus, "Failure", StringComparison.OrdinalIgnoreCase);
        await AppendAuditAsync(payment.EscrowId, failed ? "order_payment_failed" : "order_payment_status_ignored",
            "payment_submitted", "payment_submitted",
            metadata: $"orderId={payment.Checkout.OrderId},merchOrderId={payment.Checkout.MerchOrderId},"
                      + $"paymentOrderId={payment.PaymentOrderId},tradeStatus={payment.TradeStatus}");
        return failed ? "payment_failed" : "ignored";
    }

    /// <summary>
    /// A payment that cannot be applied: alarmable log token plus a durable escrow-audit row
    /// (keyed by the order's escrow id), so an admin can refund it by hand.
    /// </summary>
    private async Task<string> RecordUnappliedAsync(
        OrderPayment payment, string token, string reason, ILambdaContext context)
    {
        context.Logger.LogError($"{token} {Describe(payment)} reason={reason}");
        var action = token == DuplicateToken ? "order_payment_duplicate" : "order_payment_unmatched";
        await AppendAuditAsync(payment.EscrowId, action, "", "",
            telebirrRef: payment.PaymentOrderId.Length > 0 ? payment.PaymentOrderId : null,
            metadata: $"orderId={payment.Checkout.OrderId},merchOrderId={payment.Checkout.MerchOrderId},"
                      + $"amount={payment.Checkout.Amount},reason={reason}");
        return token == DuplicateToken ? "duplicate" : $"unmatched:{reason}";
    }

    // ─── ESC-45: inbox replay sweep ──────────────────────────────────────

    /// <summary>
    /// A direct invocation by the EventBridge schedule: no API Gateway request context (API Gateway
    /// always sets one) and exactly the replay action in the body.
    /// </summary>
    private static bool IsReplayInvocation(APIGatewayProxyRequest request)
    {
        if (request.RequestContext != null || string.IsNullOrEmpty(request.Body) || request.Body.Length > 200)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("action", out var action)
                   && action.ValueKind == JsonValueKind.String
                   && action.GetString() == ReplayAction;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What one sweep run did, for its heartbeat line.</summary>
    private sealed class ReplayCounts
    {
        public int Due;
        public int Claimed;
        public int Applied;
        public int Pending;
        public int Exhausted;
    }

    /// <summary>
    /// ESC-45 (D4): replay inbox rows left pending (a failure or a cut-off run after the row was
    /// written). Due rows only, oldest first, at most <see cref="SweepMaxRows"/> per run, each
    /// claimed with a conditional write before it is processed, and no claim with less than
    /// <see cref="SweepClaimReserve"/> left. One heartbeat line per run, unless the index could not
    /// be read (then the stalled alarm fires).
    /// </summary>
    private async Task<APIGatewayProxyResponse> ReplayInboxAsync(ILambdaContext context)
    {
        var counts = new ReplayCounts();
        if (_inboxRepo == null || _orderCheckoutRepo == null)
        {
            context.Logger.LogError("Telebirr inbox replay: inbox or checkouts not configured.");
            return Respond(200, SuccessBody);
        }

        var stopwatch = Stopwatch.StartNew();
        var queryFailed = false;
        try
        {
            Dictionary<string, AttributeValue>? startKey = null;
            var budgetLeft = true;
            do
            {
                var (rows, next) = await _inboxRepo.QueryDueAsync(_clock.GetUtcNow().UtcDateTime, SweepPageSize, startKey);
                foreach (var row in rows)
                {
                    if (counts.Due >= SweepMaxRows || !SweepHasTime(context, stopwatch))
                    {
                        budgetLeft = false;
                        break;
                    }

                    counts.Due++;
                    try
                    {
                        await ReplayRowAsync(row, counts, context);
                    }
                    catch (Exception ex)
                    {
                        // The row keeps its claim time and comes back on a later run.
                        context.Logger.LogWarning(
                            $"Telebirr inbox replay: row not finished {DescribeRow(row, null)} reason={ex.GetType().Name}");
                    }
                }

                startKey = next;
            }
            while (startKey != null && budgetLeft);
        }
        catch (Exception ex)
        {
            queryFailed = true;
            context.Logger.LogError($"Telebirr inbox replay: the inbox index could not be read [{ex.GetType().Name}].");
        }
        finally
        {
            if (!queryFailed)
            {
                context.Logger.LogInformation(
                    $"{ReplayRunToken} due={counts.Due} claimed={counts.Claimed} applied={counts.Applied} "
                    + $"pending={counts.Pending} exhausted={counts.Exhausted}");
            }
        }

        return Respond(200, SuccessBody);
    }

    private static bool SweepHasTime(ILambdaContext context, Stopwatch stopwatch)
        => context.RemainingTime > TimeSpan.Zero
            ? context.RemainingTime > SweepClaimReserve
            : stopwatch.Elapsed < SweepUnknownBudget;

    /// <summary>Claim one due row, replay it like a redelivery, and record what came of it.</summary>
    private async Task ReplayRowAsync(WebhookInboxRow row, ReplayCounts counts, ILambdaContext context)
    {
        var inbox = _inboxRepo!;

        // A run that died during the last attempt left the row due with every attempt used.
        if (row.Attempts >= WebhookInbox.MaxAttempts)
        {
            await GiveUpAsync(row, row.Attempts, "attempts_used", null, counts, context);
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var nextReplay = now + WebhookInbox.Backoff(row.Attempts);
        if (row.ReplayAt == null || !await inbox.TryClaimAsync(row.Key, row.ReplayAt, row.Attempts, nextReplay, now))
        {
            return;
        }

        counts.Claimed++;
        var attempts = row.Attempts + 1;
        if (row.Biz is not { MerchOrderId: { Length: > 0 } merchOrderId } biz)
        {
            await GiveUpAsync(row, attempts, "stored_notification_unreadable", null, counts, context);
            return;
        }

        OrderCheckoutMapping? checkout;
        try
        {
            checkout = await _orderCheckoutRepo!.GetMappingAsync(merchOrderId);
        }
        catch (InvalidDataException)
        {
            await GiveUpAsync(row, attempts, "mapping_invalid", null, counts, context);
            return;
        }
        catch (Exception ex)
        {
            await ReplayLaterAsync(row, attempts, $"mapping_lookup_{ex.GetType().Name}", null, counts, context);
            return;
        }

        if (checkout == null)
        {
            await GiveUpAsync(row, attempts, "mapping_missing", null, counts, context);
            return;
        }

        var payment = new OrderPayment(
            checkout, biz.PaymentOrderId ?? "", biz.TransId, biz.TradeStatus ?? "", OrderEscrowPrefix + checkout.OrderId);
        if (IsCompleted(payment.TradeStatus))
        {
            // D8: keep the order held while replays remain (also after a requeue).
            var receivedAt = ParseUtcOrNull(row.CreatedAt) ?? now;
            var holdUntil = receivedAt + WebhookInbox.ReplayHorizon;
            var afterNext = nextReplay + TimeSpan.FromMinutes(15);
            await TryHoldOrderAsync(payment, holdUntil > afterNext ? holdUntil : afterNext, context);
        }

        ApplyOutcome outcome;
        try
        {
            outcome = await ApplyOrderPaymentAsync(biz, payment, context);
        }
        catch (Exception ex)
        {
            await ReplayLaterAsync(row, attempts, ex.GetType().Name, payment, counts, context);
            return;
        }

        if (!outcome.IsFinal)
        {
            await ReplayLaterAsync(row, attempts, outcome.Status, payment, counts, context);
            return;
        }

        // A requeued row (exhaustedAt kept) that applies now: an admin may have started a refund.
        await FinalizeInboxAsync(row.Key, outcome.Status, payment, wasExhausted: row.ExhaustedAt != null, context);
        if (outcome.Status is "applied" or "applied_again")
        {
            counts.Applied++;
        }

        context.Logger.LogInformation(
            $"Telebirr inbox replay: {SafeLogId(outcome.Status)} on attempt {attempts}: {Describe(payment)}.");
    }

    /// <summary>A replay that did not finish: pending for the next attempt, or given up after the last.</summary>
    private async Task ReplayLaterAsync(
        WebhookInboxRow row, int attempts, string reason, OrderPayment? payment, ReplayCounts counts, ILambdaContext context)
    {
        if (attempts >= WebhookInbox.MaxAttempts)
        {
            await GiveUpAsync(row, attempts, reason, payment, counts, context);
            return;
        }

        if (!await _inboxRepo!.MarkUnprocessedAsync(row.Key))
        {
            // A redelivery wrote a final outcome meanwhile.
            context.Logger.LogInformation($"Telebirr inbox replay: row finished elsewhere: {DescribeRow(row, payment)}.");
            return;
        }

        counts.Pending++;
        context.Logger.LogWarning(
            $"{ReplayPendingToken} {DescribeRow(row, payment)} attempt={attempts}/{WebhookInbox.MaxAttempts} reason={SafeLogId(reason)}");
    }

    /// <summary>
    /// Give a row up: only while it is still pending with <paramref name="seenAttempts"/> (a finalize
    /// that won the race is never undone), and only then the alarm line and the durable audit row
    /// that make it a refund case for an admin.
    /// </summary>
    private async Task GiveUpAsync(
        WebhookInboxRow row, int seenAttempts, string reason, OrderPayment? payment, ReplayCounts counts, ILambdaContext context)
    {
        if (!await _inboxRepo!.ExhaustAsync(row.Key, seenAttempts))
        {
            context.Logger.LogInformation($"Telebirr inbox replay: row moved on before it was given up: {DescribeRow(row, payment)}.");
            return;
        }

        counts.Exhausted++;
        context.Logger.LogError(
            $"{UnprocessedToken} {DescribeRow(row, payment)} reason=replay_exhausted cause={SafeLogId(reason)} attempts={seenAttempts}");
        try
        {
            await AppendAuditAsync(row.EscrowId, "order_payment_unprocessed", "", "",
                telebirrRef: row.Biz?.PaymentOrderId is { Length: > 0 } paymentOrderId ? paymentOrderId : null,
                metadata: $"orderId={row.OrderId},merchOrderId={row.MerchOrderId},reason=replay_exhausted:{reason},attempts={seenAttempts}");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Audit row for an exhausted order payment not written: {DescribeRow(row, payment)} reason={ex.GetType().Name}");
        }
    }

    private static string DescribeRow(WebhookInboxRow row, OrderPayment? payment)
        => payment != null
            ? Describe(payment)
            : $"orderId={SafeLogId(row.OrderId)} merchOrderId={SafeLogId(row.MerchOrderId)} "
              + $"paymentOrderId={SafeLogId(row.Biz?.PaymentOrderId)}";

    private static DateTime? ParseUtcOrNull(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>The ids of an order payment for a log line, sanitized.</summary>
    private static string Describe(OrderPayment payment)
        => $"orderId={SafeLogId(payment.Checkout.OrderId)} merchOrderId={SafeLogId(payment.Checkout.MerchOrderId)} "
           + $"paymentOrderId={SafeLogId(payment.PaymentOrderId)}";

    /// <summary>
    /// A value reduced to ASCII letters, digits and <c>-.:</c> with <c>_</c> turned into <c>-</c>
    /// (at most 64), so an id (the order id is client-chosen) cannot forge an alarm token or a
    /// log line. The shared ESC-13 rule, <see cref="MoneyAlarmLog.LogId"/>.
    /// </summary>
    private static string SafeLogId(string? value) => MoneyAlarmLog.LogId(value);

    private static decimal RoundMoney(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task AppendAuditAsync(
        string escrowId, string action, string fromState, string toState,
        string? telebirrRef = null, string? metadata = null)
    {
        var entry = new Dictionary<string, object>
        {
            { "auditId", $"audit-{Guid.NewGuid():N}" },
            { "escrowId", escrowId },
            { "action", action },
            { "fromState", fromState },
            { "toState", toState },
            { "actor", "telebirr" },
            { "timestamp", DateTime.UtcNow.ToString("o") },
        };
        if (telebirrRef != null)
        {
            entry["telebirrRef"] = telebirrRef;
        }

        if (metadata != null)
        {
            entry["metadata"] = metadata;
        }

        await _auditRepo.AppendAsync(entry);
    }

    /// <summary>
    /// D2: a small valid JSON body (Telebirr's own success envelope); the 500 body says nothing
    /// about the cause (SEC-07).
    /// </summary>
    private static APIGatewayProxyResponse Respond(int status, string body) => new()
    {
        StatusCode = status,
        Body = body,
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
    };

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static decimal GetDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var v))
        {
            return 0m;
        }

        return v switch
        {
            decimal d => d,
            double db => (decimal)db,
            int i => i,
            long l => l,
            string s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : 0m,
            _ => 0m,
        };
    }
}

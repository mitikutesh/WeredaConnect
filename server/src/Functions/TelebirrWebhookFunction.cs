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
/// (no JWT — Telebirr can't send one). Security layers: signature verification
/// (primary), timestamp freshness, nonce/replay rejection, amount match. On a
/// verified "Completed" it transitions the escrow initiated → escrowed; on
/// "Failed" it cancels. ESC-01: a notification for an order checkout
/// (POST /escrow/order-checkout) funds the order instead, exactly once.
/// ALWAYS returns 200 OK so failures leak nothing to the caller; internal
/// failures are logged.
/// AOT-safe: System.Text.Json source-gen + System.Security.Cryptography only.
/// </summary>
public class TelebirrWebhookFunction
{
    private const int MaxTimestampSkewSeconds = 300;
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
        ITransactionAuditRepository? txAuditRepo = null)
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
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            await ProcessAsync(request, context);
        }
        catch (Exception ex)
        {
            // Never surface internal errors to the caller (SEC-07).
            context.Logger.LogError($"Telebirr webhook: internal error [{ex.GetType().Name}].");
        }

        return Ok();
    }

    private async Task ProcessAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        // Layer 1 (optional): source IP allow-list. Skipped when unconfigured (local).
        if (_allowedSourceIps.Count > 0)
        {
            var ip = request.RequestContext?.Identity?.SourceIp;
            if (ip == null || !_allowedSourceIps.Contains(ip))
            {
                context.Logger.LogWarning("Telebirr webhook: source IP not allow-listed.");
                return;
            }
        }

        if (string.IsNullOrEmpty(request.Body))
        {
            return;
        }

        TelebirrWebhookNotification? n;
        try
        {
            n = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.TelebirrWebhookNotification);
        }
        catch (JsonException)
        {
            context.Logger.LogWarning("Telebirr webhook: unparseable body.");
            return;
        }

        if (n?.BizContent?.MerchOrderId is not { Length: > 0 } merchOrderId || string.IsNullOrEmpty(n.Sign))
        {
            context.Logger.LogWarning("Telebirr webhook: missing merch_order_id or sign.");
            return;
        }

        // Layer 2 (primary): verify the signature with Telebirr's public key.
        if (!_signer.VerifyObject(ToSignable(n), n.Sign))
        {
            context.Logger.LogWarning("Telebirr webhook: signature verification failed.");
            return;
        }

        // Layer 3: timestamp freshness. Fail closed: a notification without a
        // parseable timestamp is treated like a stale one (replay defence).
        if (!long.TryParse(n.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tsMs))
        {
            context.Logger.LogWarning("Telebirr webhook: missing or unparseable timestamp.");
            return;
        }
        var skewSec = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - tsMs) / 1000.0;
        if (skewSec > MaxTimestampSkewSeconds)
        {
            context.Logger.LogWarning($"Telebirr webhook: stale timestamp (skew {skewSec:F0}s).");
            return;
        }

        // Layer 3b: the notification must be for our merchant account.
        if (!string.IsNullOrEmpty(_merchantCode)
            && !string.Equals(n.BizContent.MerchCode, _merchantCode, StringComparison.Ordinal))
        {
            context.Logger.LogWarning("Telebirr webhook: merch_code does not match this merchant.");
            return;
        }

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
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"{UnprocessedToken} merchOrderId={SafeLogId(merchOrderId)} "
                    + $"paymentOrderId={SafeLogId(n.BizContent.PaymentOrderId)} reason=mapping_lookup_{ex.GetType().Name}");
                return;
            }
        }

        if (checkout != null)
        {
            await ProcessOrderPaymentAsync(n.BizContent, checkout, context);
            return;
        }

        // Resolve escrow via the idempotency map (merch_order_id == initiate idempotency key).
        var idem = await _idempotencyRepo.GetAsync(merchOrderId);
        var escrowId = idem != null && idem.TryGetValue("escrowId", out var eid) ? eid?.ToString() : null;
        if (string.IsNullOrEmpty(escrowId))
        {
            context.Logger.LogWarning("Telebirr webhook: no escrow for merch_order_id.");
            return;
        }

        var escrow = await _escrowRepo.GetByIdAsync(escrowId);
        if (escrow == null)
        {
            context.Logger.LogWarning("Telebirr webhook: escrow record not found.");
            return;
        }

        // Layer 4: replay rejection (composite of order + telebirr ref + status).
        // NOTE: the dedupe key is recorded only AFTER the state transition commits
        // (see below). Recording it here would permanently lock out webhook
        // redelivery if the escrow update failed transiently, stranding a paid
        // payment in 'initiated'. The escrow CAS (expectedVersion 'initiated') is
        // the primary idempotency guard against concurrent duplicate deliveries.
        var replayKey = $"webhook:{merchOrderId}:{n.BizContent.PaymentOrderId}:{n.BizContent.TradeStatus}";

        var isSuccess = string.Equals(n.BizContent.TradeStatus, "Completed", StringComparison.OrdinalIgnoreCase);
        var createdAt = GetString(escrow, "createdAt");

        // Payer binding: the wallet that paid should be the buyer's registered phone.
        // A shared or borrowed account paying from someone else's wallet is the
        // signal KYC exists to catch. Enforced only when TELEBIRR_REQUIRE_PAYER_MATCH
        // is on (the field must be confirmed in Telebirr's live payload first);
        // otherwise a mismatch is recorded for the fraud review queue.
        if (isSuccess && !await PayerMatchesBuyerAsync(
                GetString(escrow, "buyerId"), escrowId, n.BizContent.Msisdn, "initiated", context))
        {
            return;
        }

        // Amount integrity: a forged/replayed notification must not carry a different amount.
        if (isSuccess)
        {
            var escrowAmount = GetDecimal(escrow, "amount");
            if (!decimal.TryParse(n.BizContent.TotalAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var notified)
                || Math.Abs(escrowAmount - notified) > 0.01m)
            {
                context.Logger.LogWarning("Telebirr webhook: amount mismatch — rejecting.");
                await AppendAuditAsync(escrowId, "webhook_amount_mismatch", "initiated", "initiated",
                    metadata: $"escrow={escrowAmount},webhook={n.BizContent.TotalAmount}");
                return;
            }
        }

        try
        {
            if (isSuccess)
            {
                var telebirrRef = n.BizContent.PaymentOrderId ?? "";
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
                    metadata: n.BizContent.TradeStatus);
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
    }

    /// <summary>Reconstruct the signed view of the notification (sign excluded by the signer).</summary>
    private static Dictionary<string, object?> ToSignable(TelebirrWebhookNotification n)
    {
        var biz = n.BizContent;
        var bizContent = new Dictionary<string, object?>
        {
            ["merch_order_id"] = biz?.MerchOrderId,
            ["payment_order_id"] = biz?.PaymentOrderId,
            ["total_amount"] = biz?.TotalAmount,
            ["trans_currency"] = biz?.TransCurrency,
            ["trade_status"] = biz?.TradeStatus,
            ["merch_code"] = biz?.MerchCode,
            ["trans_id"] = biz?.TransId,
        };
        if (biz?.Msisdn != null)
        {
            bizContent["msisdn"] = biz.Msisdn;
        }

        return new Dictionary<string, object?>
        {
            ["method"] = n.Method,
            ["nonce_str"] = n.NonceStr,
            ["timestamp"] = n.Timestamp,
            ["notify_time"] = n.NotifyTime,
            ["sign_type"] = n.SignType,
            ["biz_content"] = bizContent,
        };
    }

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

    /// <summary>
    /// ESC-01: a verified notification whose merch_order_id is an order checkout. It is
    /// recorded first (an inbox row keyed by checkout, payment and status, kept 30 days), then
    /// the order is funded exactly once, or the reason it was not is logged with an alarmable
    /// token and written to the escrow audit trail. Any payment that is not applied stays with
    /// the platform for a manual refund, and a redelivery of it keeps that verdict.
    /// </summary>
    private async Task ProcessOrderPaymentAsync(
        TelebirrWebhookBiz biz, OrderCheckoutMapping checkout, ILambdaContext context)
    {
        var payment = new OrderPayment(
            checkout, biz.PaymentOrderId ?? "", biz.TransId, biz.TradeStatus ?? "",
            OrderEscrowPrefix + checkout.OrderId);
        var inboxKey = $"webhook-inbox:{checkout.MerchOrderId}:{payment.PaymentOrderId}:{payment.TradeStatus}";

        string outcome;
        try
        {
            var firstDelivery = await _idempotencyRepo.StoreAsync(
                inboxKey, payment.EscrowId, "received", checkout.MerchOrderId);

            // A payment already judged unmatched or duplicate keeps that verdict: it is being
            // refunded by hand, so a redelivery must never fund the order with it later.
            if (!firstDelivery && await EarlierRefusalAsync(inboxKey) is { } earlier)
            {
                context.Logger.LogInformation(
                    $"Telebirr redelivery of an order payment already judged {SafeLogId(earlier)}; verdict kept: {Describe(payment)}.");
                return;
            }

            outcome = await ApplyOrderPaymentAsync(biz, payment, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason={ex.GetType().Name}");
            await TryRecordUnprocessedAsync(payment, ex, context);
            return;
        }

        try
        {
            await _idempotencyRepo.UpdateAsync(inboxKey, outcome);
        }
        catch (Exception ex)
        {
            // The notification was handled; only its inbox row is behind. Not a refund case.
            context.Logger.LogWarning(
                $"{BookkeepingToken} {Describe(payment)} step=inbox outcome={outcome} reason={ex.GetType().Name}");
        }
    }

    /// <summary>Apply an order payment notification; returns the inbox outcome.</summary>
    private async Task<string> ApplyOrderPaymentAsync(
        TelebirrWebhookBiz biz, OrderPayment payment, ILambdaContext context)
    {
        if (_orderRepo == null || _orderFunding == null)
        {
            context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=orders_not_configured");
            return "unprocessed";
        }

        if (!string.Equals(payment.TradeStatus, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return await RecordNotCompletedAsync(payment);
        }

        if (payment.PaymentOrderId.Length == 0)
        {
            return await RecordUnappliedAsync(payment, UnmatchedToken, "no_payment_order_id", context);
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
                return verdict;
            }
        }

        context.Logger.LogError($"{UnprocessedToken} {Describe(payment)} reason=funding_conflict");
        return "unprocessed";
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
            await TransactionAuditWriter.EmitAsync(
                _txAuditRepo, context, payment.Checkout.OrderId, "order", payment.Checkout.OrderId,
                "payment_verified", "payment_submitted", "funded", AuditContext.ForSystem(TelebirrActor),
                metadata: $"merchOrderId={payment.Checkout.MerchOrderId},paymentOrderId={payment.PaymentOrderId}");
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

    /// <summary>The earlier inbox outcome of this notification when it refused the payment (unmatched or duplicate), else null.</summary>
    private async Task<string?> EarlierRefusalAsync(string inboxKey)
    {
        var row = await _idempotencyRepo.GetAsync(inboxKey);
        var status = row != null && row.TryGetValue("status", out var value) ? value?.ToString() : null;
        return status != null && (status.StartsWith("unmatched:", StringComparison.Ordinal) || status == "duplicate")
            ? status
            : null;
    }

    /// <summary>
    /// A durable escrow-audit row for a payment that could not be processed, next to its log
    /// line. Best effort: the same outage can stop this write too, and then the log line is the
    /// only record.
    /// </summary>
    private async Task TryRecordUnprocessedAsync(OrderPayment payment, Exception cause, ILambdaContext context)
    {
        try
        {
            await AppendAuditAsync(payment.EscrowId, "order_payment_unprocessed", "", "",
                telebirrRef: payment.PaymentOrderId.Length > 0 ? payment.PaymentOrderId : null,
                metadata: $"orderId={payment.Checkout.OrderId},merchOrderId={payment.Checkout.MerchOrderId},"
                          + $"amount={payment.Checkout.Amount},reason={cause.GetType().Name}");
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
    /// again.
    /// </summary>
    private async Task<string> RepairRedeliveryAsync(
        Dictionary<string, object> order, OrderPayment payment, ILambdaContext context)
    {
        var status = GetString(order, "status");
        if (NoRepairOrderStatuses.Contains(status))
        {
            context.Logger.LogInformation(
                $"Telebirr redelivery for an order that is {SafeLogId(status)}; nothing to repair: {Describe(payment)}.");
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

        return "applied_again";
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
        var failed = string.Equals(payment.TradeStatus, "Failed", StringComparison.OrdinalIgnoreCase);
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
        if (telebirrRef != null) entry["telebirrRef"] = telebirrRef;
        if (metadata != null) entry["metadata"] = metadata;
        await _auditRepo.AppendAsync(entry);
    }

    private static APIGatewayProxyResponse Ok() => new()
    {
        StatusCode = 200,
        Body = "",
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
    };

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static decimal GetDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var v)) return 0m;
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

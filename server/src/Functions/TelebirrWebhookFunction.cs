using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Handles asynchronous Telebirr payment notifications: POST /webhooks/telebirr
/// (no JWT — Telebirr can't send one). Security layers: signature verification
/// (primary), timestamp freshness, nonce/replay rejection, amount match. On a
/// verified "Completed" it transitions the escrow initiated → escrowed; on
/// "Failed" it cancels. ALWAYS returns 200 OK so failures leak nothing to the
/// caller; internal failures are logged.
/// AOT-safe: System.Text.Json source-gen + System.Security.Cryptography only.
/// </summary>
public class TelebirrWebhookFunction
{
    private const int MaxTimestampSkewSeconds = 300;

    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IEscrowIdempotencyRepository _idempotencyRepo;
    private readonly ITelebirrSignatureService _signer;
    private readonly HashSet<string> _allowedSourceIps;
    private readonly string? _merchantCode;
    private readonly IUserRepository? _userRepo;
    private readonly bool _requirePayerMatch;

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
        bool requirePayerMatch = false)
    {
        _escrowRepo = escrowRepo;
        _auditRepo = auditRepo;
        _idempotencyRepo = idempotencyRepo;
        _signer = signer;
        _allowedSourceIps = new HashSet<string>(allowedSourceIps ?? Array.Empty<string>(), StringComparer.Ordinal);
        _merchantCode = merchantCode;
        _userRepo = userRepo;
        _requirePayerMatch = requirePayerMatch;
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
        if (isSuccess && !await PayerMatchesBuyerAsync(escrow, escrowId, n.BizContent.Msisdn, context))
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
    /// missing payer while a match is required) in the escrow audit trail.
    /// </summary>
    private async Task<bool> PayerMatchesBuyerAsync(
        Dictionary<string, object> escrow, string escrowId, string? payerMsisdn, ILambdaContext context)
    {
        if (string.IsNullOrWhiteSpace(payerMsisdn))
        {
            if (!_requirePayerMatch)
            {
                return true;
            }
            context.Logger.LogWarning("Telebirr webhook: payer phone required but absent.");
            await AppendAuditAsync(escrowId, "webhook_payer_unbound", "initiated", "initiated");
            return false;
        }

        if (_userRepo == null)
        {
            context.Logger.LogWarning("Telebirr webhook: payer phone present but no user repository to check it.");
            return !_requirePayerMatch;
        }

        var buyerId = GetString(escrow, "buyerId");
        var buyer = string.IsNullOrEmpty(buyerId) ? null : await _userRepo.GetUserByIdAsync(buyerId);
        var payer = Validation.RequestValidator.NormalizePhoneNumber(payerMsisdn) ?? payerMsisdn.Trim();
        if (buyer != null && string.Equals(buyer.PhoneNumber, payer, StringComparison.Ordinal))
        {
            return true;
        }

        context.Logger.LogWarning("Telebirr webhook: payer phone does not match the buyer's registered phone.");
        await AppendAuditAsync(escrowId, "webhook_payer_mismatch", "initiated", "initiated",
            metadata: $"buyer={buyerId},payerSuffix={payer[^Math.Min(4, payer.Length)..]}");
        return !_requirePayerMatch;
    }

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

using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-02: the admin queue of captured Telebirr payments the webhook did not apply, and the
/// record of their manual refund (the reserved escrow-audit row
/// <c>order_payment_refunded_manual</c>). The money moves in the Telebirr merchant portal; this
/// only records it. Nothing else changes: no order status, no escrow (the payment was never
/// applied). Contract: escrow-protocol.md "Additions in 1.20.0".
/// </summary>
public sealed class UnappliedPaymentService
{
    /// <summary>Rows evaluated per Scan page (the Scan's Limit, before the filter).</summary>
    internal const int ScanPageSize = 500;

    /// <summary>Scan pages per list call: at most 2,000 rows read, the F-35 cap of the audit feed.</summary>
    internal const int MaxScanPagesPerCall = 4;

    /// <summary>
    /// Orders whose trail one list call reads (one Query each, sequential). Past it the call stops
    /// and its token resumes right after the last order read, so a page dense with unapplied rows
    /// cannot run the Lambda out of time.
    /// </summary>
    internal const int MaxTrailQueriesPerCall = 50;

    /// <summary>Prefix of the claim row in escrow-idempotency that makes a refund record exclusive.</summary>
    internal const string ClaimPrefix = "refund-claim#";

    /// <summary>
    /// Extra field in the signed nextToken: the partition the previous call already read in full
    /// (with a Query), so its remaining rows at the start of the next Scan page are skipped.
    /// Table attributes never start with an underscore.
    /// </summary>
    internal const string HandledKeyField = "_handledEscrowId";

    internal const decimal MaxAmount = 100_000_000m;
    internal const string Currency = "ETB";

    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly ITransactionAuditRepository? _txAuditRepo;
    private readonly string? _claimTable;
    private readonly int _scanPageSize;
    private readonly int _maxTrailQueries;

    /// <param name="claimTable">escrow-idempotency, where the refund claim row goes; the record call needs it.</param>
    /// <param name="scanPageSize">Rows per Scan page; only tests pass a smaller one, to cross page boundaries.</param>
    /// <param name="maxTrailQueries">Trail reads per list call; only tests pass a smaller one.</param>
    public UnappliedPaymentService(
        IEscrowAuditRepository auditRepo, IOrderRepository orderRepo, ITransactionAuditRepository? txAuditRepo,
        string? claimTable, int scanPageSize = ScanPageSize, int maxTrailQueries = MaxTrailQueriesPerCall)
    {
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _txAuditRepo = txAuditRepo;
        _claimTable = string.IsNullOrEmpty(claimTable) ? null : claimTable;
        _scanPageSize = scanPageSize;
        _maxTrailQueries = Math.Max(1, maxTrailQueries);
    }

    /// <summary>
    /// GET /admin/payments/unapplied. The escrow-audit table has no index on the action, so this
    /// pages through a filtered Scan: each call reads at most <see cref="MaxScanPagesPerCall"/>
    /// pages and the trails of at most <see cref="MaxTrailQueriesPerCall"/> orders, and stops early
    /// once it has <paramref name="limit"/> payments (the last page read can add a few more). Every
    /// order found is re-read with a Query, so a refund row anywhere in its trail is seen. Pages can
    /// be empty while <c>nextToken</c> is set.
    /// </summary>
    public async Task<EscrowResult<UnappliedPaymentListResponse>> ListAsync(int limit, string? nextToken)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        Dictionary<string, AttributeValue>? startKey = null;
        var handled = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (startKey != null && startKey.Remove(HandledKeyField, out var done) && !string.IsNullOrEmpty(done.S))
            {
                handled.Add(done.S);
            }

            // Tokens of every admin list share one signing key: only an escrow-audit key may
            // start this Scan.
            if (startKey == null || startKey.Count != 2
                || !startKey.ContainsKey("escrowId") || !startKey.ContainsKey("timestamp"))
            {
                return EscrowResult<UnappliedPaymentListResponse>.Failure(
                    400, "ESCROW_023", "Invalid pagination token.");
            }
        }

        var payments = new List<UnappliedPaymentDto>();
        var lastKey = startKey;
        var pages = 0;
        var trailQueries = 0;
        do
        {
            var page = await _auditRepo.ScanKeysWithActionsAsync(
                UnappliedPayments.UnappliedActions, lastKey, _scanPageSize);
            pages++;
            Dictionary<string, AttributeValue>? resumeAfter = null;
            var cut = false;
            foreach (var hit in page.Hits)
            {
                if (handled.Contains(hit.EscrowId))
                {
                    continue;
                }

                // The page started under the cap, so at least one order of it was read before a cut.
                if (trailQueries >= _maxTrailQueries && resumeAfter != null)
                {
                    cut = true;
                    break;
                }

                handled.Add(hit.EscrowId);
                trailQueries++;
                payments.AddRange(await OpenPaymentsAsync(hit.EscrowId));
                resumeAfter = new Dictionary<string, AttributeValue>
                {
                    ["escrowId"] = new AttributeValue { S = hit.EscrowId },
                    ["timestamp"] = new AttributeValue { S = hit.LastTimestamp }
                };
            }

            // A cut resumes the Scan right after the last row of the last order read (a real item
            // key); that order's remaining rows are skipped as handled.
            lastKey = cut ? resumeAfter : page.LastEvaluatedKey;
            if (cut)
            {
                break;
            }
        } while (lastKey != null && payments.Count < limit && pages < MaxScanPagesPerCall
                 && trailQueries < _maxTrailQueries);

        string? token = null;
        if (lastKey != null)
        {
            var tokenKey = new Dictionary<string, AttributeValue>(lastKey);
            // A partition cut by the page boundary (or by the trail cap) continues at the start of
            // the next page; it was already read in full, so the next call must not list it again.
            if (lastKey.TryGetValue("escrowId", out var lastId) && lastId.S != null && handled.Contains(lastId.S))
            {
                tokenKey[HandledKeyField] = new AttributeValue { S = lastId.S };
            }

            token = PaginationTokenHelper.EncodeNextToken(tokenKey);
        }

        return EscrowResult<UnappliedPaymentListResponse>.Success(new UnappliedPaymentListResponse
        {
            Payments = payments,
            NextToken = token,
            Total = payments.Count
        });
    }

    /// <summary>
    /// POST /admin/payments/{orderId}/refund-manual: record that an admin refunded a captured
    /// payment in the merchant portal. Admin group only. A replay with the same reference answers
    /// <c>alreadyRecorded</c> and writes nothing. The row is written together with a claim row per
    /// payment, so of two admins recording one payment at once exactly one writes.
    /// </summary>
    public async Task<EscrowResult<RecordManualRefundResponse>> RecordRefundAsync(
        string orderId, RecordManualRefundRequest body, string actorUserId, bool adminGroupVerified,
        AuditContext auditContext, ILambdaContext? context)
    {
        if (!adminGroupVerified)
        {
            return Fail(403, "ESCROW_015", "Only an admin in the admin group can record a manual refund.");
        }

        if (_claimTable == null)
        {
            return Fail(503, "ADMIN_004", "Escrow idempotency table not configured.");
        }

        if (!IsSafeOrderId(orderId))
        {
            return Fail(404, "ESCROW_042", "No captured payment of this order waits for a manual refund.");
        }

        var paymentOrderId = (body.PaymentOrderId ?? "").Trim();
        if (!EscrowService.IsValidExternalRef(paymentOrderId))
        {
            return Fail(400, "ESCROW_040",
                "paymentOrderId must be the Telebirr payment order id: 4 to 64 letters, digits or . _ : / -",
                "paymentOrderId");
        }

        var telebirrRef = (body.TelebirrRef ?? "").Trim();
        if (!EscrowService.IsValidExternalRef(telebirrRef))
        {
            return Fail(400, "ESCROW_040",
                "telebirrRef must be the refund's merchant-portal transaction id: 4 to 64 letters, digits or . _ : / -",
                "telebirrRef");
        }

        var merchOrderId = (body.MerchOrderId ?? "").Trim();
        if (merchOrderId.Length > 0 && !EscrowService.IsValidExternalRef(merchOrderId))
        {
            return Fail(400, "ESCROW_040", "merchOrderId is malformed.", "merchOrderId");
        }

        if (body.Amount is not { } amount || amount <= 0 || amount > MaxAmount
            || amount != UnappliedPayments.Round(amount))
        {
            return Fail(400, "ESCROW_040",
                "amount must be the refunded amount in ETB: more than 0, at most two decimals.", "amount");
        }

        var reason = (body.Reason ?? "").Trim();
        if (reason.Length is < 10 or > 500)
        {
            return Fail(400, "ESCROW_040", "reason must be 10 to 500 characters.", "reason");
        }

        var escrowId = UnappliedPayments.EscrowKey(orderId);
        // Strongly consistent: a replay right after a write must see the row it wrote.
        var trail = await _auditRepo.GetAuditTrailAsync(escrowId, consistentRead: true);
        var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        var fundedPaymentOrderId = GetOrNull(order, "telebirrPaymentOrderId");
        var fundedMerchOrderId = GetOrNull(order, "telebirrMerchOrderId");

        UnappliedPayment? Find(IReadOnlyList<Dictionary<string, object>> rows)
        {
            var payments = UnappliedPayments.Evaluate(rows, fundedPaymentOrderId, fundedMerchOrderId);
            return payments.FirstOrDefault(p => p.PaymentOrderId == paymentOrderId)
                   ?? (merchOrderId.Length > 0
                       ? payments.FirstOrDefault(p => p.PaymentOrderId == null && p.MerchOrderId == merchOrderId)
                       : null);
        }

        var target = Find(trail);
        if (target == null)
        {
            return Fail(404, "ESCROW_042",
                "No captured payment with this payment order id waits for a manual refund on this order.");
        }

        if (target.Refund != null)
        {
            return Replay(orderId, escrowId, paymentOrderId, telebirrRef, target);
        }

        if (trail.Any(r => Get(r, "action") == UnappliedPayments.RefundedManual
                           && Get(r, "telebirrRef") == telebirrRef))
        {
            return Fail(409, "ESCROW_036",
                "This reference was already recorded for another payment of this order.");
        }

        if (target.FundedOrder || paymentOrderId == fundedPaymentOrderId)
        {
            return Fail(409, "ESCROW_044",
                "This payment funded the order: its money is held in the order's escrow and settles there. Do not refund it here.");
        }

        // Not decided for good (only unprocessed rows, or a payment the webhook could not name) and
        // the order still waits for a payment: Telebirr may still deliver it to the order.
        if (!target.HasFinalVerdict && GetOrNull(order, "status") == "payment_submitted")
        {
            return Fail(409, "ESCROW_044",
                "The order still waits for this payment and Telebirr may still deliver it. Wait until the order is funded or its payment fails.");
        }

        if (target.CapturedAmount is { } captured && UnappliedPayments.Round(amount) != captured)
        {
            return Fail(409, "ESCROW_043",
                $"The amount differs from the captured amount ({UnappliedPayments.FormatAmount(captured)} {Currency}).",
                "amount");
        }

        var paymentMerchOrderId = target.MerchOrderId ?? NullIfEmpty(merchOrderId);
        var metadata = UnappliedPayments.RefundMetadata(paymentOrderId, amount, paymentMerchOrderId);
        var claimKey = $"{ClaimPrefix}{orderId}#{target.PaymentOrderId ?? "merch:" + target.MerchOrderId}";
        var (written, recordedAt) = await AppendRefundRowAsync(
            escrowId, actorUserId, telebirrRef, metadata, reason, auditContext, claimKey, paymentOrderId);
        if (written != AuditClaimWriteResult.Written)
        {
            // Another admin claimed this payment first, or is claiming it now: answer from what is
            // recorded, read strongly consistent (the claim and its row commit together).
            var claimed = Find(await _auditRepo.GetAuditTrailAsync(escrowId, consistentRead: true));
            return claimed?.Refund != null
                ? Replay(orderId, escrowId, paymentOrderId, telebirrRef, claimed)
                : Fail(409, "ESCROW_036",
                    "Another admin is recording the refund of this payment right now. Reload and check before trying again.");
        }

        var amountChecked = target.CapturedAmount != null;

        await TransactionAuditWriter.EmitAsync(
            _txAuditRepo, context, orderId, "escrow", escrowId,
            "admin_payment_refunded_manual", null, null, auditContext,
            metadata: $"paymentOrderId={paymentOrderId};telebirrRef={telebirrRef};"
                      + $"amount={UnappliedPayments.FormatAmount(amount)};amountChecked={(amountChecked ? "true" : "false")};"
                      + $"reason={reason}");

        // Every client-supplied value is defanged, so no request can spell an alarm token here.
        Log(context,
            $"ORDER_PAYMENT_REFUNDED_MANUAL orderId={MoneyAlarmLog.LogId(orderId)} "
            + $"paymentOrderId={MoneyAlarmLog.LogId(paymentOrderId)} telebirrRef={MoneyAlarmLog.LogId(telebirrRef)} "
            + $"amount={UnappliedPayments.FormatAmount(amount)} amountChecked={(amountChecked ? "true" : "false")} "
            + $"admin={MoneyAlarmLog.LogId(actorUserId)}");

        return EscrowResult<RecordManualRefundResponse>.Success(new RecordManualRefundResponse
        {
            OrderId = orderId,
            EscrowId = escrowId,
            PaymentOrderId = paymentOrderId,
            MerchOrderId = paymentMerchOrderId,
            TelebirrRef = telebirrRef,
            Amount = UnappliedPayments.Round(amount),
            AmountChecked = amountChecked,
            RecordedAt = recordedAt,
            RecordedBy = actorUserId,
            Message = amountChecked
                ? "Refund recorded."
                : "Refund recorded. The captured amount was not on record, so the amount was not checked.",
            AlreadyRecorded = false
        });
    }

    /// <summary>
    /// The open payments of one order found by the Scan. The order is read only when its trail
    /// still has an open payment, to tell whether that payment funded it.
    /// </summary>
    private async Task<List<UnappliedPaymentDto>> OpenPaymentsAsync(string escrowId)
    {
        if (!escrowId.StartsWith(UnappliedPayments.OrderEscrowPrefix, StringComparison.Ordinal)
            || escrowId.Length == UnappliedPayments.OrderEscrowPrefix.Length)
        {
            return new List<UnappliedPaymentDto>();
        }

        var orderId = escrowId[UnappliedPayments.OrderEscrowPrefix.Length..];
        var trail = await _auditRepo.GetAuditTrailAsync(escrowId);
        if (!UnappliedPayments.HasOpen(trail, null, null))
        {
            return new List<UnappliedPaymentDto>();
        }

        var order = await _orderRepo.GetByIdAsync(orderId);
        var status = GetOrNull(order, "status");
        return UnappliedPayments
            .Evaluate(trail, GetOrNull(order, "telebirrPaymentOrderId"), GetOrNull(order, "telebirrMerchOrderId"))
            .Where(p => p.IsOpen)
            .Select(p => new UnappliedPaymentDto
            {
                OrderId = orderId,
                EscrowId = escrowId,
                PaymentOrderId = p.PaymentOrderId,
                MerchOrderId = p.MerchOrderId,
                Actions = p.Actions,
                Reasons = p.Reasons,
                FirstSeenAt = p.FirstSeenAt,
                LastSeenAt = p.LastSeenAt,
                CapturedAmount = p.CapturedAmount,
                Currency = Currency,
                OrderStatus = status,
                BuyerId = GetOrNull(order, "buyerId"),
                MayStillApply = !p.HasFinalVerdict && status == "payment_submitted"
            })
            .ToList();
    }

    /// <summary>
    /// A payment whose refund is already recorded: the same reference is a replay (nothing is
    /// written), another one is a conflict.
    /// </summary>
    private static EscrowResult<RecordManualRefundResponse> Replay(
        string orderId, string escrowId, string paymentOrderId, string telebirrRef, UnappliedPayment target)
    {
        var recorded = target.Refund!;
        if (!string.Equals(recorded.TelebirrRef, telebirrRef, StringComparison.Ordinal))
        {
            return Fail(409, "ESCROW_036",
                "The refund of this payment was already recorded with a different reference.");
        }

        return EscrowResult<RecordManualRefundResponse>.Success(new RecordManualRefundResponse
        {
            OrderId = orderId,
            EscrowId = escrowId,
            PaymentOrderId = recorded.PaymentOrderId ?? paymentOrderId,
            MerchOrderId = recorded.MerchOrderId,
            TelebirrRef = recorded.TelebirrRef,
            Amount = recorded.Amount,
            AmountChecked = target.CapturedAmount != null,
            RecordedAt = recorded.RecordedAt,
            RecordedBy = recorded.RecordedBy,
            Message = "Already recorded.",
            AlreadyRecorded = true
        });
    }

    /// <summary>
    /// The refund row and its claim row in escrow-idempotency, in one transaction: the row never
    /// overwrites one that shares its timestamp (then it tries the next instant), and the claim
    /// (<c>refund-claim#{orderId}#{payment}</c>, kept) lets only one writer record a payment.
    /// </summary>
    private async Task<(AuditClaimWriteResult Result, string Timestamp)> AppendRefundRowAsync(
        string escrowId, string actor, string telebirrRef, string metadata, string reason, AuditContext auditContext,
        string claimKey, string paymentOrderId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var timestamp = DateTime.UtcNow.ToString("o");
            var entry = new Dictionary<string, object>
            {
                { "auditId", EscrowAuditRepository.DeterministicAuditId(UnappliedPayments.RefundedManual, timestamp) },
                { "escrowId", escrowId },
                { "action", UnappliedPayments.RefundedManual },
                { "fromState", "" },
                { "toState", "" },
                { "actor", actor },
                { "timestamp", timestamp },
                { "telebirrRef", telebirrRef },
                { "metadata", metadata },
                { "reason", reason }
            };
            if (!string.IsNullOrEmpty(auditContext.IpAddress))
            {
                entry["ipAddress"] = auditContext.IpAddress!;
            }

            if (!string.IsNullOrEmpty(auditContext.CorrelationId))
            {
                entry["correlationId"] = auditContext.CorrelationId!;
            }

            var claim = new Dictionary<string, object>
            {
                { "idempotencyKey", claimKey },
                { "escrowId", escrowId },
                { "status", "refund_recorded" },
                { "paymentOrderId", paymentOrderId },
                { "telebirrRef", telebirrRef },
                { "claimedBy", actor },
                { "claimedAt", timestamp }
            };
            var result = await _auditRepo.AppendWithClaimAsync(entry, _claimTable!, claim);
            if (result != AuditClaimWriteResult.EntryExists)
            {
                return (result, timestamp);
            }
        }

        throw new InvalidOperationException("The manual refund row could not be written at a free timestamp.");
    }

    /// <summary>Order ids as they appear in <c>escrow-order-{orderId}</c>: 1 to 100 of [A-Za-z0-9_-].</summary>
    private static bool IsSafeOrderId(string orderId)
        => orderId.Length is >= 1 and <= 100
           && orderId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void Log(ILambdaContext? context, string line)
    {
        if (context != null)
        {
            context.Logger.LogInformation(line);
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    private static EscrowResult<RecordManualRefundResponse> Fail(
        int status, string code, string message, string? field = null)
        => EscrowResult<RecordManualRefundResponse>.Failure(status, code, message, field);

    private static string Get(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static string? GetOrNull(Dictionary<string, object>? row, string key)
        => row != null && row.TryGetValue(key, out var value) && value is not null && value.ToString() is { Length: > 0 } s
            ? s
            : null;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-41 (escrow-protocol.md 2.19.0): the admin exit of a legacy double escrow. An escrow paid through
/// <c>POST /escrow/initiate</c> that names an order whose own escrow (<c>escrow-order-{orderId}</c>) holds or
/// paid the trade's money is a second payment for one trade. The hourly sweeps never settle it (ESC-10,
/// <c>double_escrow</c>). An admin in the admin group refunds it to the buyer through the existing refund
/// (<c>POST /escrow/refund</c> with <c>doubleEscrow: true</c>): the settlement lock, the gateway call under the
/// stable reference <c>refund:{escrowId}</c>, the manual payout queue and the <c>refund</c> audit row are those
/// of every 1.x refund. Only the order hop of an admin refund (ESC-09 Part B) is skipped: the order and its
/// delivery belong to the order's own escrow, which is not touched.
/// </summary>
public sealed partial class EscrowService
{
    /// <summary>The refusal code of a double escrow refund that does not fit (always <c>409</c>).</summary>
    internal const string DoubleEscrowRefusalCode = "ESCROW_049";

    /// <summary>
    /// The statuses in which the order's own escrow holds the trade's money or paid it out, so a second escrow
    /// of the order is extra. Not <c>initiated</c> (that payment may never come: which one is extra is not
    /// known), <c>refunded</c> or <c>cancelled</c> (then the second escrow is the trade's only money).
    /// </summary>
    internal static readonly IReadOnlySet<string> OrderEscrowPaidStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "escrowed", "released", "completed", "disputed", "dispute_resolved", EscrowHoldFields.Status
    };

    /// <summary>Step 1d: the request rules of a double escrow refund, checked before anything is read.</summary>
    private static EscrowResult<RefundEscrowResponse>? DoubleEscrowRequestProblem(
        RefundEscrowRequest request, bool adminGroupVerified)
    {
        if (!adminGroupVerified)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                403, "ESCROW_021", "Only an admin in the admin group can refund a double escrow.");
        }

        if (request.OverrideOrderMismatch)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(400, "ESCROW_VALIDATION",
                "doubleEscrow cannot be combined with overrideOrderMismatch: the order is not touched.",
                "overrideOrderMismatch");
        }

        if (request.Trigger != "admin_override")
        {
            return EscrowResult<RefundEscrowResponse>.Failure(400, "ESCROW_VALIDATION",
                "A double escrow refund takes the trigger admin_override.", "trigger");
        }

        var reason = request.Reason?.Trim() ?? "";
        return reason.Length is < 10 or > 500
            ? EscrowResult<RefundEscrowResponse>.Failure(400, "ESCROW_VALIDATION",
                "reason must be 10 to 500 characters when refunding a double escrow.", "reason")
            : null;
    }

    /// <summary>
    /// After the escrow read (step 3): checks that the escrow is a double escrow and records the admin's decision
    /// before any money moves. Returns the answer to give now (a refusal, or the replay of a refund already made),
    /// or null to go on with the refund.
    /// <list type="bullet">
    /// <item>Refunded with a recorded decision: the replay, <c>200</c> with the refunded escrow.</item>
    /// <item>Not <c>escrowed</c>, the order's own escrow, no order, a ledger escrow, or an order escrow that is
    /// missing or not in <see cref="OrderEscrowPaidStatuses"/> (read strongly consistent on every call, a decision
    /// already recorded included): <c>409 ESCROW_049</c>, nothing written.</item>
    /// <item>No decision yet: one conditional write records it (<see cref="DoubleEscrowFields"/>) on what was read:
    /// still <c>escrowed</c>, the same order, no decision, no settlement lock, no manual payout. A write that loses
    /// reads the escrow again: a decision now there (another admin's, or this call's lost answer) is used, anything
    /// else is <c>409 ESCROW_049</c>.</item>
    /// <item>Then the escrow-audit row <c>double_escrow_refund</c> at the decision's time (the actor and reason
    /// recorded; written once, re-ensured by a retry) must be there before the refund: a failed write is
    /// <c>503 ESCROW_012</c> and nothing moved. The transaction-audit row on the order's trail follows at the same
    /// key, best effort.</item>
    /// </list>
    /// </summary>
    private async Task<EscrowResult<RefundEscrowResponse>?> PrepareDoubleEscrowRefundAsync(
        Dictionary<string, object> escrow, string reason, string actor)
    {
        var escrowId = GetString(escrow, "escrowId");
        var status = GetString(escrow, "status");
        if (status == "refunded" && GetStringOrNull(escrow, DoubleEscrowFields.RefundAt) != null)
        {
            return EscrowResult<RefundEscrowResponse>.Duplicate(BuildRefundResponse(escrow));
        }

        var orderId = GetStringOrNull(escrow, "orderId");
        var refusal = status != "escrowed"
            ? $"Only an escrowed escrow is refunded as a double payment; this one is '{status}'."
            : IsOrderFundedEscrow(escrow) || string.IsNullOrEmpty(orderId)
                ? "This escrow is not a second payment: it is an order's own escrow, or it names no order."
                : IsLedgerEscrow(escrow)
                    ? "A ledger escrow is never a double escrow; use the ledger's own refund."
                    : null;
        if (refusal != null)
        {
            return DoubleEscrowRefused(escrowId, refusal);
        }

        Dictionary<string, object>? orderEscrow;
        try
        {
            orderEscrow = await _escrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DOUBLE_ESCROW_REFUND_UNREAD escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            return EscrowResult<RefundEscrowResponse>.Failure(
                503, "ESCROW_012", "The order's own escrow could not be read. Nothing moved; try again.");
        }

        var orderEscrowStatus = orderEscrow == null ? null : GetString(orderEscrow, "status");
        if (orderEscrowStatus == null || !OrderEscrowPaidStatuses.Contains(orderEscrowStatus))
        {
            return DoubleEscrowRefused(escrowId, orderEscrowStatus == null
                ? "The order has no escrow of its own: this escrow is the trade's only payment."
                : $"The order's own escrow is '{orderEscrowStatus}': it does not hold or pay this trade's money, so this escrow is not a second payment.");
        }

        var decidedAt = GetStringOrNull(escrow, DoubleEscrowFields.RefundAt);
        var decidedBy = GetStringOrNull(escrow, DoubleEscrowFields.RefundBy);
        var decidedReason = GetStringOrNull(escrow, DoubleEscrowFields.RefundReason);
        var decidedOn = GetStringOrNull(escrow, DoubleEscrowFields.OrderEscrowStatus);
        if (decidedAt == null)
        {
            var at = DateTime.UtcNow.ToString("o");
            if (await _escrowRepo.TryRecordDoubleEscrowRefundAsync(
                    escrowId, GetString(escrow, "createdAt"), orderId!, actor, reason, at, orderEscrowStatus))
            {
                (decidedAt, decidedBy, decidedReason, decidedOn) = (at, actor, reason, orderEscrowStatus);
            }
            else
            {
                var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
                decidedAt = latest == null ? null : GetStringOrNull(latest, DoubleEscrowFields.RefundAt);
                if (latest == null || decidedAt == null)
                {
                    return DoubleEscrowRefused(escrowId,
                        "The escrow changed while the refund was decided (a settlement started, or it moved on). Nothing moved; reload.");
                }

                if (GetString(latest, "status") == "refunded")
                {
                    return EscrowResult<RefundEscrowResponse>.Duplicate(BuildRefundResponse(latest));
                }

                decidedBy = GetStringOrNull(latest, DoubleEscrowFields.RefundBy);
                decidedReason = GetStringOrNull(latest, DoubleEscrowFields.RefundReason);
                decidedOn = GetStringOrNull(latest, DoubleEscrowFields.OrderEscrowStatus);
            }
        }

        // The decision is on record; its audit row must be too before the money moves (a retry re-ensures it).
        var by = decidedBy ?? actor;
        var metadata = $"orderId={orderId};orderEscrowId=escrow-order-{orderId};orderEscrowStatus={decidedOn ?? orderEscrowStatus}"
                       + $";reason={decidedReason ?? reason}";
        bool written;
        try
        {
            written = await EnsureAuditAsync(escrowId, decidedAt, DoubleEscrowFields.AuditAction, "escrowed", "escrowed", by,
                metadata: metadata);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DOUBLE_ESCROW_REFUND_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            return EscrowResult<RefundEscrowResponse>.Failure(
                503, "ESCROW_012", "The decision could not be recorded. Nothing moved; try again.");
        }

        await TransactionAuditWriter.EmitOnceAsync(_transactionAuditRepo, null, orderId!, "escrow", escrowId,
            DoubleEscrowFields.AuditAction, "escrowed", "escrowed",
            by == actor ? _auditContext ?? new AuditContext { Actor = actor, ActorRole = "admin" }
                : new AuditContext { Actor = by, ActorRole = "admin" },
            decidedAt, metadata);
        if (written)
        {
            Console.WriteLine(
                $"ESCROW_DOUBLE_ESCROW_REFUND_DECIDED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(orderId)} orderEscrowStatus={MoneyAlarmLog.LogId(decidedOn ?? orderEscrowStatus)}");
        }

        return null;
    }

    private static EscrowResult<RefundEscrowResponse> DoubleEscrowRefused(string escrowId, string message)
    {
        Console.WriteLine($"ESCROW_DOUBLE_ESCROW_REFUND_REFUSED escrowId={MoneyAlarmLog.LogId(escrowId)}");
        return EscrowResult<RefundEscrowResponse>.Failure(409, DoubleEscrowRefusalCode, message);
    }
}

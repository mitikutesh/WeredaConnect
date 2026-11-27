using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-218 (escrow-protocol.md 2.18.0): an admin replaces the portal reference of a manual payout leg recorded by
/// mistake. Typical case: leg A was recorded with the portal id of leg B's payment, so B's own record is refused
/// (<c>ESCROW_047</c> names A). The correction writes A's reference (and the escrow's settlement reference field
/// when it holds the old one), moves A's <c>payout-ref#</c> item to the new reference and writes its escrow-audit
/// row, in one transaction conditioned on what was read. Nothing about money changes: no amount, status, paid time,
/// recorder or ledger decision, and the gateway is never called.
/// </summary>
public sealed partial class EscrowService
{
    /// <summary>400: the correction's reason is missing, shorter than 10 or longer than 500 characters after trimming.</summary>
    internal const string CorrectionReasonCode = "ESCROW_048";

    internal const int CorrectionReasonMinLength = 10;
    internal const int CorrectionReasonMaxLength = 500;

    internal const string ReferenceCorrectedAction = "manual_payout_ref_corrected";

    /// <summary>The correction's transaction; null where the admin Lambda did not wire it (tests).</summary>
    internal IPayoutReferenceCorrectionRepository? PayoutReferenceCorrections { get; init; }

    /// <summary>
    /// POST /admin/payouts/{escrowId}/{ref}/correct: the leg's reference becomes <paramref name="externalRef"/>. The
    /// leg must be recorded by an admin and still carry <paramref name="oldExternalRef"/>. A leg that already carries
    /// the new reference answers <c>alreadyCorrected</c> and writes nothing (a replay).
    /// </summary>
    public async Task<EscrowResult<CorrectPayoutReferenceResponse>> CorrectPayoutReferenceAsync(
        string escrowId, string leg, string? oldExternalRef, string? externalRef, string? reason,
        string actorUserId, bool adminGroupVerified, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        // The admin pool's admin group only, as for recording the payout.
        if (!adminGroupVerified)
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(
                403, "ESCROW_015", "Only an admin can correct a payout reference.");
        }

        if (PayoutReferenceCorrections == null || ManualPayoutClaims == null || string.IsNullOrEmpty(PayoutReferenceTable))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(503, "ADMIN_004", "Escrow tables not configured.");
        }

        var newRef = (externalRef ?? "").Trim();
        if (!IsValidExternalRef(newRef))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(400, "ESCROW_037",
                "externalRef must be the Telebirr transaction id: 4 to 64 letters, digits or . _ : / -", "externalRef");
        }

        var oldRef = (oldExternalRef ?? "").Trim();
        if (!IsValidExternalRef(oldRef))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(400, "ESCROW_037",
                "oldExternalRef must be the reference the leg carries now.", "oldExternalRef");
        }

        if (PayoutReferenceFields.Normalize(oldRef) == PayoutReferenceFields.Normalize(newRef))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(400, "ESCROW_037",
                "The new reference is the recorded one (references compare without case).", "externalRef");
        }

        var why = (reason ?? "").Trim();
        if (why.Length is < CorrectionReasonMinLength or > CorrectionReasonMaxLength)
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(400, CorrectionReasonCode,
                "reason is required: 10 to 500 characters saying why the recorded reference is wrong.", "reason");
        }

        var ledgerRef = Uri.UnescapeDataString(leg);
        var request = new CorrectionRequest(escrowId, oldRef, newRef, why, actorUserId);
        return ledgerRef.Contains('#', StringComparison.Ordinal)
            ? await CorrectLedgerReferenceAsync(request with { Ref = ledgerRef })
            : await CorrectLegReferenceAsync(request with { Ref = leg });
    }

    private sealed record CorrectionRequest(string EscrowId, string OldRef, string NewRef, string Reason, string Actor)
    {
        public string Ref { get; init; } = "";
    }

    /// <summary>A 1.x leg on the escrow row (1.8.0).</summary>
    private async Task<EscrowResult<CorrectPayoutReferenceResponse>> CorrectLegReferenceAsync(CorrectionRequest request)
    {
        var (escrowId, leg) = (request.EscrowId, request.Ref);
        if (!ManualPayoutFields.IsLeg(leg))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
        }

        for (var attempt = 1; attempt <= MaxCorrectionAttempts; attempt++)
        {
            var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (escrow == null)
            {
                return EscrowResult<CorrectPayoutReferenceResponse>.Failure(404, "ESCROW_016", "Escrow not found.");
            }

            var legStatus = GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(leg));
            if (legStatus == null)
            {
                return EscrowResult<CorrectPayoutReferenceResponse>.Failure(
                    404, "ESCROW_035", "This escrow has no manual payout for that leg.");
            }

            var orderId = GetStringOrNull(escrow, "orderId");
            var paidBy = GetStringOrNull(escrow, ManualPayoutFields.PaidByAttr(leg));
            if (CorrectionRefusal(legStatus == ManualPayoutFields.LegPaid, paidBy,
                    GetStringOrNull(escrow, ManualPayoutFields.RefAttr(leg)), request, orderId) is { } answer)
            {
                return answer;
            }

            // One escrow, one reference per leg (1.8.0): the other legs are on the row read.
            foreach (var other in ManualPayoutFields.AllLegs.Where(l => l != leg))
            {
                if (string.Equals(GetStringOrNull(escrow, ManualPayoutFields.RefAttr(other)), request.NewRef, StringComparison.Ordinal))
                {
                    return ReferenceTaken(new PayoutReferenceHolder(escrowId, other, null, null));
                }
            }

            var status = GetString(escrow, "status");
            var settlementField = ManualPayoutFields.SettlementRefAttr(leg);
            var now = ClaimNowIso();
            var write = await PayoutReferenceCorrections!.CorrectAsync(new PayoutReferenceCorrection
            {
                EscrowId = escrowId,
                Ref = leg,
                OldRef = request.OldRef,
                NewRef = request.NewRef,
                PaidBy = paidBy!,
                Now = now,
                Legacy = new LegacyLegRead(GetString(escrow, "createdAt"), status,
                    escrow.ContainsKey("version") ? GetInt(escrow, "version") : (int?)null),
                // Set by the close; while the queue is open it does not exist yet, and the close copies the new one.
                SettlementRefField = string.Equals(GetStringOrNull(escrow, settlementField), request.OldRef, StringComparison.Ordinal)
                    ? settlementField
                    : null,
                DeleteOldItem = await HoldsReferenceAsync(request.OldRef, escrowId, leg),
                NewItem = new PayoutReferenceRecord(PayoutReferenceTable!, request.NewRef, escrowId, leg, request.Actor, now),
                Audit = CorrectionAudit(request, status, now, $"leg={leg}")
            });
            if (await CorrectionOutcomeAsync(write, request, now, orderId) is { } outcome)
            {
                return outcome;
            }
        }

        return CorrectionChanged();
    }

    /// <summary>A ledger row (2.2.0, P.8): its <c>telebirrRef</c>, and the escrow's settlement field when it holds the old one.</summary>
    private async Task<EscrowResult<CorrectPayoutReferenceResponse>> CorrectLedgerReferenceAsync(CorrectionRequest request)
    {
        if (LedgerRepo == null)
        {
            return LedgerUnavailable<CorrectPayoutReferenceResponse>();
        }

        var (escrowId, reference) = (request.EscrowId, request.Ref);
        for (var attempt = 1; attempt <= MaxCorrectionAttempts; attempt++)
        {
            var row = await LedgerRepo.GetRowAsync(escrowId, reference);
            if (row == null)
            {
                return EscrowResult<CorrectPayoutReferenceResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
            }

            if (CorrectionRefusal(row.Status == LedgerFields.StatusDone, row.PaidBy, row.TelebirrRef, request, row.OrderId) is { } answer)
            {
                return answer;
            }

            // A row of amount copied its reference into the escrow when it became done; a later row of the same payee
            // may have replaced it there, and then the field is left alone.
            var settlementField = row.IsOfAmount ? LedgerSettlementRefField(row) : null;
            var escrow = settlementField == null ? null : await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            var writesEscrow = escrow != null
                               && string.Equals(GetStringOrNull(escrow, settlementField!), request.OldRef, StringComparison.Ordinal);
            var status = escrow == null ? "" : GetString(escrow, "status");
            var now = ClaimNowIso();
            var write = await PayoutReferenceCorrections!.CorrectAsync(new PayoutReferenceCorrection
            {
                EscrowId = escrowId,
                Ref = reference,
                OldRef = request.OldRef,
                NewRef = request.NewRef,
                PaidBy = row.PaidBy!,
                Now = now,
                Ledger = writesEscrow
                    ? new LedgerRowRead(row.Version, GetString(escrow!, "createdAt"), LedgerEscrow.From(escrow!).Version)
                    : new LedgerRowRead(row.Version),
                SettlementRefField = writesEscrow ? settlementField : null,
                DeleteOldItem = await HoldsReferenceAsync(request.OldRef, escrowId, reference),
                NewItem = new PayoutReferenceRecord(PayoutReferenceTable!, request.NewRef, escrowId, reference, request.Actor, now),
                Audit = CorrectionAudit(request, status, now, $"ref={reference}")
            });
            if (await CorrectionOutcomeAsync(write, request, now, row.OrderId) is { } outcome)
            {
                return outcome;
            }
        }

        return CorrectionChanged();
    }

    private const int MaxCorrectionAttempts = 3;

    /// <summary>
    /// The answer before any write, or null to go on: the replay of the same correction (the leg already carries the
    /// new reference), a leg not recorded yet, a leg the gateway paid (its reference is Telebirr's, never an admin's
    /// mistake, and has no reference item), or a leg that carries another reference than the one the admin saw.
    /// </summary>
    private static EscrowResult<CorrectPayoutReferenceResponse>? CorrectionRefusal(
        bool recorded, string? paidBy, string? current, CorrectionRequest request, string? orderId)
    {
        if (recorded && string.Equals(current, request.NewRef, StringComparison.Ordinal))
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Success(CorrectionResponse(
                request, orderId, null, alreadyCorrected: true, "This leg already carries this reference."));
        }

        if (!recorded)
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(409, "ESCROW_035",
                "This leg is not recorded yet: record its reference in Manual Payouts instead.");
        }

        if (string.IsNullOrEmpty(paidBy) || paidBy == ManualPayoutFields.PaidByGateway)
        {
            return EscrowResult<CorrectPayoutReferenceResponse>.Failure(409, "ESCROW_035",
                "Only a reference an admin recorded can be corrected; the gateway paid this leg.");
        }

        return string.Equals(current, request.OldRef, StringComparison.Ordinal)
            ? null
            : EscrowResult<CorrectPayoutReferenceResponse>.Failure(409, "ESCROW_036",
                "This leg carries another reference than the one sent. Reload and check.", "oldExternalRef");
    }

    /// <summary>
    /// Whether the old reference's item names this leg, so the correction removes it. An item another leg holds (one
    /// portal reference recorded on two legs before 2.12.0, FUP-215) or no item at all is left alone. A read that
    /// fails is not taken as "no item": it throws, and nothing is written.
    /// </summary>
    private async Task<bool> HoldsReferenceAsync(string externalRef, string escrowId, string reference)
    {
        var holder = await ManualPayoutClaims!.GetReferenceHolderAsync(externalRef);
        return holder != null && holder.EscrowId == escrowId && holder.Ref == reference;
    }

    /// <summary>
    /// The escrow-audit row of the correction, in its transaction: the old and the new reference and the reason
    /// (last: free text), the correcting admin as the actor.
    /// </summary>
    private static LedgerAudit CorrectionAudit(CorrectionRequest request, string status, string now, string where)
        => new(request.EscrowId, now, ReferenceCorrectedAction, status, status, request.Actor,
            $"{where};oldRef={request.OldRef};newRef={request.NewRef};reason={request.Reason}", request.NewRef);

    /// <summary>The answer after the transaction, or null to read again and retry.</summary>
    private async Task<EscrowResult<CorrectPayoutReferenceResponse>?> CorrectionOutcomeAsync(
        PayoutReferenceCorrectionWrite write, CorrectionRequest request, string now, string? orderId)
    {
        switch (write)
        {
            case PayoutReferenceCorrectionWrite.Written:
                Console.WriteLine($"ESCROW_PAYOUT_REF_CORRECTED escrowId={MoneyAlarmLog.LogId(request.EscrowId)} ref={MoneyAlarmLog.LogRef(request.Ref)}");
                return EscrowResult<CorrectPayoutReferenceResponse>.Success(CorrectionResponse(
                    request, orderId, now, alreadyCorrected: false, "Reference corrected."));
            case PayoutReferenceCorrectionWrite.ReferenceTaken:
                // Held by this very leg means a replay landed in between: read again and answer it.
                var holder = await ReadReferenceHolderAsync(request.NewRef);
                return holder != null && holder.EscrowId == request.EscrowId && holder.Ref == request.Ref
                    ? null
                    : ReferenceTaken(holder);
            default:
                return null;
        }
    }

    /// <summary>409 ESCROW_047 in the record's fixed form, which the admin app reads to name the escrow and leg.</summary>
    private static EscrowResult<CorrectPayoutReferenceResponse> ReferenceTaken(PayoutReferenceHolder? holder)
        => EscrowResult<CorrectPayoutReferenceResponse>.Failure(409, ReferenceTakenCode, ReferenceTakenMessage(holder), "externalRef");

    private static EscrowResult<CorrectPayoutReferenceResponse> CorrectionChanged()
        => EscrowResult<CorrectPayoutReferenceResponse>.Failure(409, "ESCROW_036",
            "The leg changed while its reference was being corrected. Reload and try again.");

    private static CorrectPayoutReferenceResponse CorrectionResponse(
        CorrectionRequest request, string? orderId, string? correctedAt, bool alreadyCorrected, string message) => new()
        {
            EscrowId = request.EscrowId,
            OrderId = orderId,
            Ref = request.Ref,
            OldExternalRef = request.OldRef,
            ExternalRef = request.NewRef,
            Reason = request.Reason,
            CorrectedBy = alreadyCorrected ? null : request.Actor,
            CorrectedAt = correctedAt,
            AlreadyCorrected = alreadyCorrected,
            Message = message
        };
}

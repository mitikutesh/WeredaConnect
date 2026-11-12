using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-05 (escrow-protocol.md 2.12.0): claims on manual payout legs and one leg per portal reference. An admin
/// claims a leg before paying it in the Telebirr merchant portal, so every other admin sees that it is being
/// paid; the claim ends on its own after <see cref="ManualPayoutClaimFields.Ttl"/>, when its holder releases it,
/// or when the leg is recorded. Recording a leg refuses a live claim of another admin, and claims an unclaimed
/// leg in the same conditional write. A reference already recorded on another leg is refused. Nothing here
/// changes an amount, a status or what recording a leg does to the escrow or the ledger.
/// </summary>
public sealed partial class EscrowService
{
    /// <summary>409: another admin holds a live claim on the leg.</summary>
    internal const string ClaimedByOtherCode = "ESCROW_046";

    /// <summary>409: the reference is already recorded on another leg or escrow.</summary>
    internal const string ReferenceTakenCode = "ESCROW_047";

    /// <summary>The claim writes and the reference read; null where the admin Lambda did not wire them (tests).</summary>
    internal IManualPayoutClaimRepository? ManualPayoutClaims { get; init; }

    /// <summary>
    /// escrow-idempotency, where each manual record writes its reference item. Empty: no reference item (only
    /// the unit tests; the admin Lambda refuses to record without it).
    /// </summary>
    internal string? PayoutReferenceTable { get; init; }

    /// <summary>The clock claims are judged by; tests set it.</summary>
    internal Func<DateTime> ClaimClock { get; init; } = () => DateTime.UtcNow;

    private string ClaimNowIso() => Iso(ClaimClock());

    /// <summary>
    /// POST /admin/payouts/{escrowId}/{ref}/claim: the caller claims a pending leg, or renews their own claim,
    /// for <see cref="ManualPayoutClaimFields.Ttl"/> from now. A live claim of another admin is refused
    /// (<c>409 ESCROW_046</c>) and never taken over.
    /// </summary>
    public async Task<EscrowResult<ManualPayoutClaimResponse>> ClaimManualPayoutAsync(
        string escrowId, string leg, string actorUserId, bool adminGroupVerified, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;
        if (ClaimPreconditionFailure(adminGroupVerified) is { } refused)
        {
            return refused;
        }

        var claims = ManualPayoutClaims!;
        var now = ClaimClock();
        var nowIso = Iso(now);
        var expiresAt = Iso(now + ManualPayoutClaimFields.Ttl);
        var ledgerRef = Uri.UnescapeDataString(leg);
        if (ledgerRef.Contains('#', StringComparison.Ordinal))
        {
            if (LedgerRepo == null)
            {
                return LedgerUnavailable<ManualPayoutClaimResponse>();
            }

            var row = await LedgerRepo.GetRowAsync(escrowId, ledgerRef);
            for (var attempt = 1; attempt <= 2 && row != null; attempt++)
            {
                if (row.Status != LedgerFields.StatusManual)
                {
                    return EscrowResult<ManualPayoutClaimResponse>.Failure(
                        409, "ESCROW_035", "This leg is not waiting in the manual payout queue.");
                }

                if (ManualPayoutClaimFields.IsHeldByOther(row.ClaimedBy, row.ClaimExpiresAt, actorUserId, nowIso))
                {
                    return ClaimedByOther<ManualPayoutClaimResponse>(row.ClaimedBy!, row.ClaimExpiresAt!);
                }

                if (await claims.TryClaimLedgerRowAsync(escrowId, ledgerRef, actorUserId, nowIso, expiresAt))
                {
                    var renewed = row.ClaimedBy == actorUserId && ManualPayoutClaimFields.IsLive(row.ClaimExpiresAt, nowIso);
                    await AuditClaimAsync(escrowId, "manual_payout_claimed", "", actorUserId,
                        $"ref={ledgerRef};expiresAt={expiresAt};renewed={(renewed ? "true" : "false")}");
                    return EscrowResult<ManualPayoutClaimResponse>.Success(ClaimResponse(
                        escrowId, row.OrderId, ledgerRef, actorUserId, nowIso, expiresAt, renewed));
                }

                // Another admin claimed it, or it was recorded, between the read and the write.
                row = await LedgerRepo.GetRowAsync(escrowId, ledgerRef);
            }

            return row == null
                ? EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_035", "No such payout leg.")
                : ClaimChanged();
        }

        if (!ManualPayoutFields.IsLeg(leg))
        {
            return EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (escrow == null)
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_016", "Escrow not found.");
            }

            var op = GetString(escrow, ManualPayoutFields.Op);
            var legStatus = GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(leg));
            if (!IsManualPayoutOpen(escrow) || legStatus == null || !ManualPayoutFields.LegBelongsToOp(leg, op))
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(
                    404, "ESCROW_035", "This escrow has no open manual payout for that leg.");
            }

            if (legStatus != ManualPayoutFields.LegPending)
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(
                    409, "ESCROW_035", "This leg is already recorded.");
            }

            var (claimedBy, _, claimExpiresAt) = LegClaim(escrow, leg);
            if (ManualPayoutClaimFields.IsHeldByOther(claimedBy, claimExpiresAt, actorUserId, nowIso))
            {
                return ClaimedByOther<ManualPayoutClaimResponse>(claimedBy!, claimExpiresAt!);
            }

            if (await claims.TryClaimLegAsync(escrowId, GetString(escrow, "createdAt"), op, leg, actorUserId, nowIso, expiresAt))
            {
                var renewed = claimedBy == actorUserId && ManualPayoutClaimFields.IsLive(claimExpiresAt, nowIso);
                var status = GetString(escrow, "status");
                await AuditClaimAsync(escrowId, "manual_payout_claimed", status, actorUserId,
                    $"leg={leg};expiresAt={expiresAt};renewed={(renewed ? "true" : "false")}");
                return EscrowResult<ManualPayoutClaimResponse>.Success(ClaimResponse(
                    escrowId, GetStringOrNull(escrow, "orderId"), leg, actorUserId, nowIso, expiresAt, renewed));
            }
        }

        return ClaimChanged();
    }

    /// <summary>
    /// POST /admin/payouts/{escrowId}/{ref}/unclaim: the holder gives the leg back. A leg with no claim, or
    /// with an expired claim of another admin, answers <c>released: false</c>; a live claim of another admin
    /// is refused (<c>409 ESCROW_046</c>).
    /// </summary>
    public async Task<EscrowResult<ManualPayoutClaimResponse>> ReleaseManualPayoutClaimAsync(
        string escrowId, string leg, string actorUserId, bool adminGroupVerified, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;
        if (ClaimPreconditionFailure(adminGroupVerified) is { } refused)
        {
            return refused;
        }

        var claims = ManualPayoutClaims!;
        var nowIso = ClaimNowIso();
        var ledgerRef = Uri.UnescapeDataString(leg);
        string? claimedBy;
        string? claimExpiresAt;
        string? orderId;
        string status;
        string reference;
        Func<Task<bool>> release;
        if (ledgerRef.Contains('#', StringComparison.Ordinal))
        {
            if (LedgerRepo == null)
            {
                return LedgerUnavailable<ManualPayoutClaimResponse>();
            }

            var row = await LedgerRepo.GetRowAsync(escrowId, ledgerRef);
            if (row == null)
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
            }

            (claimedBy, claimExpiresAt, orderId, status, reference) = (row.ClaimedBy, row.ClaimExpiresAt, row.OrderId, "", ledgerRef);
            release = () => claims.TryReleaseLedgerRowAsync(escrowId, ledgerRef, actorUserId);
        }
        else
        {
            if (!ManualPayoutFields.IsLeg(leg))
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
            }

            var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (escrow == null)
            {
                return EscrowResult<ManualPayoutClaimResponse>.Failure(404, "ESCROW_016", "Escrow not found.");
            }

            (claimedBy, _, claimExpiresAt) = LegClaim(escrow, leg);
            (orderId, status, reference) = (GetStringOrNull(escrow, "orderId"), GetString(escrow, "status"), leg);
            var createdAt = GetString(escrow, "createdAt");
            release = () => claims.TryReleaseLegAsync(escrowId, createdAt, leg, actorUserId);
        }

        if (ManualPayoutClaimFields.IsHeldByOther(claimedBy, claimExpiresAt, actorUserId, nowIso))
        {
            return ClaimedByOther<ManualPayoutClaimResponse>(claimedBy!, claimExpiresAt!);
        }

        // The caller's own claim, live or expired, is removed; anything else holds nothing to release.
        var released = false;
        if (claimedBy == actorUserId)
        {
            if (!await release())
            {
                // Taken over after it lapsed, ended by the record, or busy with a record in flight: reload.
                return EscrowResult<ManualPayoutClaimResponse>.Failure(
                    409, "ESCROW_035", "The claim changed while it was being released. Reload and try again.");
            }

            released = true;
            await AuditClaimAsync(escrowId, "manual_payout_claim_released", status, actorUserId,
                ledgerRef.Contains('#', StringComparison.Ordinal) ? $"ref={reference}" : $"leg={reference}");
        }

        return EscrowResult<ManualPayoutClaimResponse>.Success(new ManualPayoutClaimResponse
        {
            EscrowId = escrowId,
            OrderId = orderId,
            Ref = reference,
            Claimed = false,
            Released = released,
            Message = released ? "Claim released." : "This leg has no claim of yours to release."
        });
    }

    private EscrowResult<ManualPayoutClaimResponse>? ClaimPreconditionFailure(bool adminGroupVerified)
    {
        // The admin pool's admin group only, as for recording the payout the claim leads to.
        if (!adminGroupVerified)
        {
            return EscrowResult<ManualPayoutClaimResponse>.Failure(
                403, "ESCROW_015", "Only an admin can claim a manual payout.");
        }

        return ManualPayoutClaims == null
            ? EscrowResult<ManualPayoutClaimResponse>.Failure(503, "ADMIN_004", "Escrow tables not configured.")
            : null;
    }

    private static EscrowResult<ManualPayoutClaimResponse> ClaimChanged() => EscrowResult<ManualPayoutClaimResponse>.Failure(
        409, "ESCROW_035", "The leg changed while it was being claimed. Reload and try again.");

    private static ManualPayoutClaimResponse ClaimResponse(
        string escrowId, string? orderId, string reference, string actor, string claimedAt, string expiresAt, bool renewed) => new()
        {
            EscrowId = escrowId,
            OrderId = orderId,
            Ref = reference,
            Claimed = true,
            ClaimedBy = actor,
            ClaimedAt = claimedAt,
            ClaimExpiresAt = expiresAt,
            Renewed = renewed,
            Message = renewed ? "Claim renewed." : "Leg claimed. Pay it in the portal, then record its transaction ID."
        };

    /// <summary>The claim attributes of a 1.x leg on its escrow row.</summary>
    private static (string? By, string? At, string? ExpiresAt) LegClaim(Dictionary<string, object> escrow, string leg)
        => (GetStringOrNull(escrow, ManualPayoutClaimFields.ClaimedByAttr(leg)),
            GetStringOrNull(escrow, ManualPayoutClaimFields.ClaimedAtAttr(leg)),
            GetStringOrNull(escrow, ManualPayoutClaimFields.ClaimExpiresAtAttr(leg)));

    /// <summary>
    /// 409 ESCROW_046: another admin is paying this leg. The message starts in a fixed form, <c>Another admin
    /// ({id}) has claimed this leg until {time}.</c>, which the admin app reads to name them. On a record the admin
    /// has usually paid already, so that text says not to pay again and to contact the holder.
    /// </summary>
    private static EscrowResult<T> ClaimedByOther<T>(string claimedBy, string claimExpiresAt, bool onRecord = false)
        => EscrowResult<T>.Failure(409, ClaimedByOtherCode,
            $"Another admin ({claimedBy}) has claimed this leg until {claimExpiresAt}. " + (onRecord
                ? "Do not pay it again: if you already paid it in the portal, contact that admin so it is recorded once."
                : "Do not pay it; reload the page."));

    /// <summary>
    /// The 409 ESCROW_047 text. It names the place in a fixed form, <c>on escrow {escrowId}, leg {ref}.</c>,
    /// which the admin app reads to show it in the admin's language.
    /// </summary>
    private static string ReferenceTakenMessage(PayoutReferenceHolder? holder) => holder == null || holder.EscrowId.Length == 0
        ? "This reference is already recorded on another payout leg."
        : $"This reference is already recorded on escrow {holder.EscrowId}, leg {holder.Ref}.";

    /// <summary>The audit metadata part that names where a refused reference is used.</summary>
    private static string ReferenceTakenDetail(PayoutReferenceHolder? holder)
        => holder == null ? "usedOn=-" : $"usedOn={holder.EscrowId}/{holder.Ref}";

    /// <summary>The reference item of a record, or null when no reference table is wired.</summary>
    private PayoutReferenceRecord? BuildReferenceRecord(string externalRef, string escrowId, string reference, string actor, string recordedAt)
        => string.IsNullOrEmpty(PayoutReferenceTable)
            ? null
            : new PayoutReferenceRecord(PayoutReferenceTable, externalRef, escrowId, reference, actor, recordedAt);

    /// <summary>Where a reference is recorded; null when it cannot be read (the refusal still stands).</summary>
    private async Task<PayoutReferenceHolder?> ReadReferenceHolderAsync(string externalRef)
    {
        if (ManualPayoutClaims == null)
        {
            return null;
        }

        try
        {
            return await ManualPayoutClaims.GetReferenceHolderAsync(externalRef);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_PAYOUT_REFERENCE_READ_FAILED error={ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The leg's claim for the queue list, with whether the viewer holds it and whether it still lives. A claim
    /// that lapsed (not released, not recorded) is shown for <see cref="ManualPayoutClaimFields.ShownAfterExpiry"/>
    /// after its end, so other admins check the portal before paying; older ones are not shown.
    /// </summary>
    private static ManualPayoutLegDto WithClaim(
        ManualPayoutLegDto dto, string? claimedBy, string? claimedAt, string? claimExpiresAt, string? viewerId, string nowIso)
    {
        if (string.IsNullOrEmpty(claimedBy) || string.IsNullOrEmpty(claimExpiresAt))
        {
            return dto;
        }

        var shownUntil = Iso(DateTime.Parse(claimExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() + ManualPayoutClaimFields.ShownAfterExpiry);
        if (string.CompareOrdinal(shownUntil, nowIso) <= 0)
        {
            return dto;
        }

        return dto with
        {
            ClaimedBy = claimedBy,
            ClaimedAt = claimedAt,
            ClaimExpiresAt = claimExpiresAt,
            ClaimedByMe = viewerId != null && string.Equals(claimedBy, viewerId, StringComparison.Ordinal),
            ClaimLive = ManualPayoutClaimFields.IsLive(claimExpiresAt, nowIso)
        };
    }

    /// <summary>A claim's escrow-audit row; a failed write never changes the answer (the claim is written).</summary>
    private async Task AuditClaimAsync(string escrowId, string action, string status, string actor, string metadata)
    {
        try
        {
            await AppendAuditAsync(escrowId, action, status, status, actor, metadata: metadata);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_CLAIM_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }
    }
}

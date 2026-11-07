using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-20: how the settlement entry points move a ledger escrow. The gates in front of them (the fraud
/// hold, the payout delay, the order checks, the dispute freeze, the verdict mirror) run first, unchanged;
/// these methods replace only the whole-escrow lock and the gateway calls under it.
/// </summary>
public sealed partial class EscrowService
{
    private static EscrowResult<T> LedgerUnavailable<T>() => EscrowResult<T>.Failure(
        503, "ESCROW_012", "The escrow ledger could not be reached. Nothing was moved; try again.");

    private static EscrowResult<T> LedgerInvariantRefusal<T>() => EscrowResult<T>.Failure(
        503, "ESCROW_012", "This settlement failed a money check and was not made. An admin has been alerted.");

    private static EscrowResult<T> LedgerInFlight<T>() => EscrowResult<T>.Failure(
        409, "ESCROW_INVALID_TRANSITION", "A settlement is already in progress or completed for this escrow.");

    private static EscrowResult<T> LedgerRetrying<T>() => EscrowResult<T>.Failure(
        502, "ESCROW_013", "Telebirr refused this payout. The ledger sweep retries it with the same reference.");

    /// <summary>
    /// ESC-20 release of a ledger escrow (after the gates of <c>ReleaseCoreAsync</c>): the farmer and the
    /// trucker are each checked against the identity gate, the legs that may be paid are decided in one
    /// write, and each row is called at once. A blocked farmer keeps the crop leg held while a verified
    /// trucker is paid (F1); a blocked trucker keeps the transport leg held while the farmer is paid (F2).
    /// </summary>
    private async Task<EscrowResult<ReleaseEscrowResponse>> ReleaseLedgerAsync(
        ReleaseEscrowRequest request, Dictionary<string, object> escrowItem, string currentStatus,
        string authenticatedUserId, SettlementProgress progress)
    {
        if (LedgerRepo == null)
        {
            return LedgerUnavailable<ReleaseEscrowResponse>();
        }

        var state = LedgerEscrow.From(escrowItem);
        EscrowResult<string>? farmerBlock = null;
        var farmerOk = false;
        if (state.IsHeld(LedgerEscrowFields.LegCrop))
        {
            var (payout, payoutBlock) = await ResolvePayoutAsync(state.FarmerId, state.Amount, "farmer");
            farmerOk = payout.IsSuccess;
            if (!farmerOk)
            {
                farmerBlock = payout;
                var blockReason = payoutBlock ?? "unknown";
                if (authenticatedUserId == SystemActor)
                {
                    await NoteSettlementBlockedAsync(escrowItem, currentStatus,
                        SettlementBlockFields.Value("release", SettlementBlockFields.GatePayout, blockReason),
                        "release_blocked_kyc", PayoutBlockedMetadata(blockReason));
                }
                else
                {
                    await AppendAuditAsync(request.EscrowId, "release_blocked_kyc", currentStatus, currentStatus,
                        authenticatedUserId, metadata: PayoutBlockedMetadata(blockReason));
                }
            }
            else if (authenticatedUserId == SystemActor)
            {
                await ClearSettlementBlockAsync(escrowItem, "release", SettlementBlockFields.GatePayout);
            }
        }

        var truckerOk = await TruckerLegPayableAsync(escrowItem, state, currentStatus, noteBlock: farmerOk);
        var trigger = string.IsNullOrEmpty(request.Trigger) ? "delivered" : request.Trigger;
        var reason = currentStatus == "dispute_resolved" ? $"verdict:{GetStringOrNull(escrowItem, "disputeResolution") ?? "farmer_wins"}" : trigger;
        var decided = await DecideLedgerAsync(escrowItem, currentStatus,
            s => EscrowLedgerBook.PlanRelease(s, farmerOk, truckerOk, reason), authenticatedUserId, callNow: true,
            extraFields: new Dictionary<string, object> { ["releaseTrigger"] = trigger });

        switch (decided.Outcome)
        {
            case LedgerDecideOutcome.Unavailable:
                return LedgerUnavailable<ReleaseEscrowResponse>();
            case LedgerDecideOutcome.Invariant:
                return LedgerInvariantRefusal<ReleaseEscrowResponse>();
            case LedgerDecideOutcome.NothingToMove when farmerBlock != null:
                return EscrowResult<ReleaseEscrowResponse>.Failure(farmerBlock.Error!);
            case LedgerDecideOutcome.NothingToMove or LedgerDecideOutcome.StatusMoved or LedgerDecideOutcome.Busy:
                {
                    var latest = await _escrowRepo.GetByIdAsync(request.EscrowId, consistentRead: true);
                    return latest != null && GetString(latest, "status") is "released" or "completed"
                        ? EscrowResult<ReleaseEscrowResponse>.Duplicate(BuildReleaseResponse(latest))
                        : LedgerInFlight<ReleaseEscrowResponse>();
                }
        }

        // The decision is on record (write-ahead): from here every outcome is in the ledger, and the sweep
        // finishes whatever this call does not.
        progress.Recorded = true;
        await StoreIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId);
        if (decided.Rows.Any(r => r.Legs.Contains(LedgerEscrowFields.LegCrop)))
        {
            // The release is committed: the order is disbursing while its rows are open (1.17.0).
            await FollowMoneyAsync(request.EscrowId, "release");
        }

        var outcomes = new List<LedgerCallResult>();
        foreach (var row in decided.Rows)
        {
            outcomes.Add(await CallLedgerRowAsync(row, authenticatedUserId));
        }

        if (farmerBlock != null)
        {
            // The trucker leg moved on its own; the farmer leg waits for the identity gate (F1).
            return EscrowResult<ReleaseEscrowResponse>.Failure(farmerBlock.Error!);
        }

        return await LedgerSettleAnswerAsync(request.EscrowId, request.IdempotencyKey, outcomes,
            latest => EscrowResult<ReleaseEscrowResponse>.Success(new ReleaseEscrowResponse
            {
                EscrowId = request.EscrowId,
                Status = GetString(latest, "status"),
                FarmerPayout = GetDecimal(latest, "farmerPayout"),
                TruckerPayout = GetDecimalOrNull(latest, "truckerPayout"),
                Commission = GetDecimal(latest, "commission"),
                TelebirrRef = GetStringOrNull(latest, "telebirrReleaseRef"),
                TruckerTelebirrRef = GetStringOrNull(latest, "truckerTelebirrRef"),
                ReleasedAt = GetStringOrNull(latest, "releasedAt") ?? GetString(latest, "updatedAt"),
                Message = GetString(latest, "status") == "completed"
                    ? "Funds released: every leg is paid."
                    : "Released. A leg is still open in the ledger and is settled by the ledger sweep."
            }));
    }

    /// <summary>
    /// The transport leg of a ledger escrow may be paid now: it is held, carries money, has a trucker, and
    /// the trucker passes the identity gate. A block is recorded once per reason when
    /// <paramref name="noteBlock"/> is true (the farmer's own block takes the escrow's one reason slot).
    /// </summary>
    private async Task<bool> TruckerLegPayableAsync(
        Dictionary<string, object> escrowItem, LedgerEscrow state, string status, bool noteBlock)
    {
        if (!state.IsHeld(LedgerEscrowFields.LegTransport) || state.TruckerPayout <= 0)
        {
            return false;
        }

        var (dest, reason) = await ResolveTruckerPayeeAsync(state.TruckerId ?? "", state.TruckerPayout);
        if (dest.IsSuccess)
        {
            return true;
        }

        if (noteBlock)
        {
            await NoteSettlementBlockedAsync(escrowItem, status,
                SettlementBlockFields.Value("release", "trucker", reason ?? "unknown"),
                TruckerBlockedAuditAction, PayoutBlockedMetadata(reason ?? "unknown"));
        }

        return false;
    }

    /// <summary>
    /// ESC-20 refund of a ledger escrow (after the gates of <c>RefundCoreAsync</c>): every held leg goes back
    /// to the buyer in one <c>refund#all</c> row, called at once.
    /// </summary>
    private async Task<EscrowResult<RefundEscrowResponse>> RefundLedgerAsync(
        RefundEscrowRequest request, Dictionary<string, object> escrowItem, string currentStatus, string settleFrom,
        string authenticatedUserId, SettlementProgress progress)
    {
        if (LedgerRepo == null)
        {
            return LedgerUnavailable<RefundEscrowResponse>();
        }

        var reason = settleFrom == "dispute_resolved"
            ? $"verdict:{GetStringOrNull(escrowItem, "disputeResolution") ?? "buyer_wins"}"
            : GetStringOrNull(escrowItem, "refundReason") ?? request.Trigger;
        var decided = await DecideLedgerAsync(escrowItem, currentStatus,
            s => EscrowLedgerBook.PlanRefundAll(s, reason), authenticatedUserId, callNow: true,
            extraFields: new Dictionary<string, object> { ["refundTrigger"] = request.Trigger });

        switch (decided.Outcome)
        {
            case LedgerDecideOutcome.Unavailable:
                return LedgerUnavailable<RefundEscrowResponse>();
            case LedgerDecideOutcome.Invariant:
                return LedgerInvariantRefusal<RefundEscrowResponse>();
            case LedgerDecideOutcome.NothingToMove or LedgerDecideOutcome.StatusMoved or LedgerDecideOutcome.Busy:
                {
                    var latest = await _escrowRepo.GetByIdAsync(request.EscrowId, consistentRead: true);
                    return latest != null && GetString(latest, "status") == "refunded"
                        ? EscrowResult<RefundEscrowResponse>.Duplicate(BuildRefundResponse(latest))
                        : LedgerInFlight<RefundEscrowResponse>();
                }
        }

        progress.Recorded = true;
        await StoreIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId);
        var outcomes = new List<LedgerCallResult>();
        foreach (var row in decided.Rows)
        {
            outcomes.Add(await CallLedgerRowAsync(row, authenticatedUserId));
        }

        var refundAmount = decided.Rows.Sum(r => r.Amount);
        if (decided.Rows.Count == 0)
        {
            // Every held leg carried 0: decided without a row; nothing to call.
            await FollowMoneyAsync(request.EscrowId, "refund");
        }

        return await LedgerSettleAnswerAsync(request.EscrowId, request.IdempotencyKey, outcomes,
            latest => EscrowResult<RefundEscrowResponse>.Success(new RefundEscrowResponse
            {
                EscrowId = request.EscrowId,
                Status = GetString(latest, "status"),
                RefundAmount = refundAmount,
                TelebirrRef = GetStringOrNull(latest, "telebirrRefundRef"),
                RefundedAt = GetStringOrNull(latest, "refundedAt") ?? GetString(latest, "updatedAt"),
                Message = "Funds refunded to the buyer's Telebirr account."
            }));
    }

    /// <summary>
    /// The answer of a ledger settlement from its rows' outcomes, in the 1.x shapes: every row done is a
    /// success (with the escrow as it is now); a row in the manual queue is <c>202 ESCROW_034</c>, with the
    /// unknown-outcome message when the gateway did not say; a definite refusal is <c>502 ESCROW_013</c>,
    /// retried by the ledger sweep with the same reference.
    /// </summary>
    private async Task<EscrowResult<T>> LedgerSettleAnswerAsync<T>(
        string escrowId, string idempotencyKey, List<LedgerCallResult> outcomes,
        Func<Dictionary<string, object>, EscrowResult<T>> success)
    {
        if (outcomes.Any(o => o.Outcome is LedgerCallOutcome.Sent or LedgerCallOutcome.Unrecorded
                || (o.Outcome == LedgerCallOutcome.Manual && o.Row.ManualReason == LedgerFields.ManualOutcomeUnknown)))
        {
            await UpdateIdempotencyBestEffortAsync(idempotencyKey, escrowId, "manual_payout");
            return OutcomeUnknownWaiting<T>();
        }

        if (outcomes.Any(o => o.Outcome == LedgerCallOutcome.Manual))
        {
            await UpdateIdempotencyBestEffortAsync(idempotencyKey, escrowId, "manual_payout");
            return ManualPayoutWaiting<T>();
        }

        if (outcomes.Any(o => o.Outcome is LedgerCallOutcome.Failed or LedgerCallOutcome.Blocked or LedgerCallOutcome.Busy))
        {
            return LedgerRetrying<T>();
        }

        await UpdateIdempotencyBestEffortAsync(idempotencyKey, escrowId, "completed");
        var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        return latest == null ? LedgerUnavailable<T>() : success(latest);
    }

    // ──────────────────────────────────────────────
    // The per-leg verdict (P.8)
    // ──────────────────────────────────────────────

    /// <summary>
    /// ESC-20: settle a <c>partial_refund</c> verdict (the admin's split, <c>verdictAmounts</c> on the escrow).
    /// Called by the hourly settlement sweep like the other verdicts: the order and delivery follow the
    /// verdict first (as <c>farmer_wins</c>: a split needs the goods delivered), then the farmer and the
    /// trucker are checked against the identity gate, then the split is decided as one write
    /// (<c>verdict#farmer</c>, <c>verdict#trucker</c>, <c>verdict#buyer</c>) and each row is called. A payee
    /// who fails the gate blocks the whole split until the next run, so the verdict is never applied in part.
    /// </summary>
    internal async Task<EscrowResult<ReleaseEscrowResponse>> SettleSplitVerdictAsync(string escrowId, string actor = SystemActor)
    {
        var escrowItem = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        if (escrowItem == null)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(404, "ESCROW_016", "Escrow not found.");
        }

        if (!IsLedgerEscrow(escrowItem) || GetString(escrowItem, "status") != "dispute_resolved"
            || GetStringOrNull(escrowItem, "disputeResolution") != DisputeVerdicts.PartialRefund)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(422, "ESCROW_INVALID_TRANSITION",
                "Only a ledger escrow with a partial_refund verdict is settled as a split.");
        }

        if (LedgerRepo == null)
        {
            return LedgerUnavailable<ReleaseEscrowResponse>();
        }

        var amounts = VerdictAmounts.From(escrowItem);
        if (amounts == null)
        {
            AlarmLog(MoneyAlarmLog.LedgerInvariant(escrowId, "verdict", "AMOUNTS"));
            return LedgerInvariantRefusal<ReleaseEscrowResponse>();
        }

        if (await MirrorVerdictAsync(escrowItem, actor, "release") is { } block)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(block.HttpStatus, block.Code, block.Message);
        }

        var state = LedgerEscrow.From(escrowItem);
        if (amounts.FarmerAmount > 0)
        {
            var (payout, reason) = await ResolvePayoutAsync(state.FarmerId, amounts.FarmerAmount, "farmer");
            if (!payout.IsSuccess)
            {
                await NoteSettlementBlockedAsync(escrowItem, "dispute_resolved",
                    SettlementBlockFields.Value("release", SettlementBlockFields.GatePayout, reason ?? "unknown"),
                    "release_blocked_kyc", PayoutBlockedMetadata(reason ?? "unknown"));
                return EscrowResult<ReleaseEscrowResponse>.Failure(payout.Error!);
            }
        }

        if (amounts.TruckerAmount > 0)
        {
            var (dest, reason) = await ResolveTruckerPayeeAsync(state.TruckerId ?? "", amounts.TruckerAmount);
            if (!dest.IsSuccess)
            {
                await NoteSettlementBlockedAsync(escrowItem, "dispute_resolved",
                    SettlementBlockFields.Value("release", "trucker", reason ?? "unknown"),
                    TruckerBlockedAuditAction, PayoutBlockedMetadata(reason ?? "unknown"));
                return EscrowResult<ReleaseEscrowResponse>.Failure(dest.Error!);
            }
        }

        var decided = await DecideLedgerAsync(escrowItem, "dispute_resolved",
            s => EscrowLedgerBook.PlanSplit(s, amounts.FarmerAmount, amounts.TruckerAmount, amounts.BuyerRefund,
                $"verdict:{DisputeVerdicts.PartialRefund}").Plan,
            actor, callNow: true);
        switch (decided.Outcome)
        {
            case LedgerDecideOutcome.Written:
                break;
            case LedgerDecideOutcome.Invariant:
                return LedgerInvariantRefusal<ReleaseEscrowResponse>();
            case LedgerDecideOutcome.Unavailable:
                return LedgerUnavailable<ReleaseEscrowResponse>();
            case LedgerDecideOutcome.NothingToMove when decided.Escrow is { } now && now.IsHeld(LedgerEscrowFields.LegCrop):
                // The split no longer fits what is held (a leg moved since the verdict): an admin decides again.
                AlarmLog(MoneyAlarmLog.LedgerInvariant(escrowId, "verdict", "SPLIT"));
                return LedgerInvariantRefusal<ReleaseEscrowResponse>();
            default:
                return LedgerInFlight<ReleaseEscrowResponse>();
        }

        if (decided.Rows.Count > 0)
        {
            await FollowMoneyAsync(escrowId, "verdict");
        }

        var outcomes = new List<LedgerCallResult>();
        foreach (var row in decided.Rows)
        {
            outcomes.Add(await CallLedgerRowAsync(row, actor));
        }

        return await LedgerSettleAnswerAsync(escrowId, $"system:{escrowId}:split", outcomes,
            latest => EscrowResult<ReleaseEscrowResponse>.Success(new ReleaseEscrowResponse
            {
                EscrowId = escrowId,
                Status = GetString(latest, "status"),
                FarmerPayout = GetDecimal(latest, "farmerPayout"),
                TruckerPayout = GetDecimalOrNull(latest, "truckerPayout"),
                Commission = GetDecimal(latest, "commission"),
                TelebirrRef = GetStringOrNull(latest, "telebirrReleaseRef"),
                TruckerTelebirrRef = GetStringOrNull(latest, "truckerTelebirrRef"),
                ReleasedAt = GetStringOrNull(latest, "releasedAt") ?? GetString(latest, "updatedAt"),
                Message = "The split verdict is settled."
            }));
    }

    // ──────────────────────────────────────────────
    // The transport leg owed after the farmer was paid (F2)
    // ──────────────────────────────────────────────

    /// <summary>
    /// ESC-20: an hourly retry of a ledger escrow whose crop leg is decided and whose transport leg is still
    /// held because the trucker failed the identity gate. Decides and calls <c>release#trucker#{id}</c> once
    /// the gate passes. Replaces the 1.x trucker retry for ledger escrows.
    /// </summary>
    internal async Task<LedgerCallOutcome?> RetryLedgerTransportLegAsync(Dictionary<string, object> escrowItem)
    {
        if (LedgerRepo == null || !IsLedgerEscrow(escrowItem))
        {
            return null;
        }

        var state = LedgerEscrow.From(escrowItem);
        if (state.IsHeld(LedgerEscrowFields.LegCrop) || !state.IsHeld(LedgerEscrowFields.LegTransport))
        {
            return null;
        }

        if (!await TruckerLegPayableAsync(escrowItem, state, state.Status, noteBlock: true))
        {
            return LedgerCallOutcome.Blocked;
        }

        var decided = await DecideLedgerAsync(escrowItem, state.Status,
            s => EscrowLedgerBook.PlanRelease(s, payFarmer: false, payTrucker: true, "delivered"), SystemActor, callNow: true);
        if (decided.Outcome != LedgerDecideOutcome.Written || decided.Rows.Count == 0)
        {
            return null;
        }

        return (await CallLedgerRowAsync(decided.Rows[0], SystemActor)).Outcome;
    }

    // ──────────────────────────────────────────────
    // Duplicate payments (P.9)
    // ──────────────────────────────────────────────

    /// <summary>
    /// ESC-20: a second captured payment for an order the webhook decided for good is a duplicate: write
    /// <c>refund#duplicate#{paymentOrderId}</c> under the order's escrow, <c>pending</c>, payee the buyer, the
    /// captured amount, due at once (the ledger sweep makes the call). The escrow row is not written: the
    /// payment is outside <c>amount</c>. Returns false when no row was written (no ledger, no amount, a row
    /// already there, or the payment already refunded by hand).
    /// </summary>
    internal async Task<bool> RecordDuplicatePaymentRefundAsync(
        string orderId, string buyerId, string paymentOrderId, decimal amount)
    {
        if (LedgerRepo == null || string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(buyerId)
            || string.IsNullOrEmpty(paymentOrderId) || amount <= 0)
        {
            return false;
        }

        var escrowId = UnappliedPayments.EscrowKey(orderId);
        var trail = await _auditRepo.GetAuditTrailAsync(escrowId, consistentRead: true);
        if (trail.Any(r => GetString(r, "action") == UnappliedPayments.RefundedManual
                           && GetString(r, "metadata").Contains($"paymentOrderId={paymentOrderId}", StringComparison.Ordinal)))
        {
            return false; // an admin refunded it in the portal already (1.20.0)
        }

        var reference = LedgerRefs.RefundDuplicate(paymentOrderId);
        var nowIso = Iso(LedgerClock());
        var row = new LedgerRow
        {
            EscrowId = escrowId,
            Ref = reference,
            Kind = LedgerFields.KindRefund,
            PayeeRole = LedgerFields.PayeeBuyer,
            PayeeId = buyerId,
            Amount = EscrowLedgerBook.Round(amount),
            Status = LedgerFields.StatusPending,
            GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
            HoldRef = paymentOrderId,
            Reason = "duplicate_payment",
            Actor = SystemActor,
            OrderId = orderId,
            CreatedAt = nowIso,
            UpdatedAt = nowIso,
            DueAt = nowIso,
            Version = 1
        };
        var write = await LedgerRepo.PutRowAsync(row, new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(nowIso, reference, "ledger_pending"),
            "ledger_pending", "", "", SystemActor, LedgerAuditMetadata(row, 0)));
        return write == LedgerWrite.Written;
    }

    /// <summary>
    /// ESC-20 (P.9): true when a duplicate payment is being refunded through the ledger, so the 1.20.0
    /// manual record of the same payment must go through the manual payout queue instead (never twice).
    /// </summary>
    internal async Task<LedgerRow?> FindDuplicateRefundRowAsync(string orderId, string paymentOrderId)
        => LedgerRepo == null || string.IsNullOrEmpty(paymentOrderId)
            ? null
            : await LedgerRepo.GetRowAsync(UnappliedPayments.EscrowKey(orderId), LedgerRefs.RefundDuplicate(paymentOrderId));

    // ──────────────────────────────────────────────
    // The admin queue (P.8)
    // ──────────────────────────────────────────────

    /// <summary>The ledger's manual rows grouped per escrow, oldest first, in the 1.8.0 response shape.</summary>
    private async Task<List<ManualPayoutDto>> ListLedgerManualPayoutsAsync(Dictionary<string, UserDto?> userCache)
    {
        var result = new List<ManualPayoutDto>();
        if (LedgerRepo == null)
        {
            return result;
        }

        var rows = new List<LedgerRow>();
        Dictionary<string, AttributeValue>? startKey = null;
        var farFuture = Iso(LedgerClock().AddYears(10));
        do
        {
            var (page, next) = await LedgerRepo.QueryOpenAsync(LedgerFields.StatusManual, farFuture, 100, startKey);
            rows.AddRange(page);
            startKey = next;
        } while (startKey != null && rows.Count < 200);

        foreach (var group in rows.GroupBy(r => r.EscrowId))
        {
            var escrow = await _escrowRepo.GetByIdAsync(group.Key, consistentRead: true);
            var legs = new List<ManualPayoutLegDto>();
            foreach (var row in group.OrderBy(r => r.DueAt, StringComparer.Ordinal))
            {
                var payee = string.IsNullOrEmpty(row.PayeeId) ? null : await GetUserCachedAsync(row.PayeeId, userCache);
                var blockReason = row.PayeeRole == LedgerFields.PayeeBuyer
                    ? payee is { IsPhoneVerified: true } ? null : TruckerPayoutFields.BlockPhoneUnverified
                    : PayoutBlockReason(payee, row.Amount);
                legs.Add(new ManualPayoutLegDto
                {
                    Ref = row.Ref,
                    PayeeRole = row.PayeeRole ?? "",
                    PayeeId = row.PayeeId ?? "",
                    PayeeName = payee?.FullName,
                    PayeePhone = payee?.PhoneNumber,
                    PayeeVerified = blockReason == null,
                    BlockReason = blockReason,
                    Amount = row.Amount,
                    Status = ManualPayoutFields.LegPending,
                    GatewayRef = row.GatewayRef
                });
            }

            var first = group.OrderBy(r => r.DueAt, StringComparer.Ordinal).First();
            var status = escrow == null ? "" : GetString(escrow, "status");
            result.Add(new ManualPayoutDto
            {
                EscrowId = group.Key,
                OrderId = first.OrderId ?? (escrow == null ? null : GetStringOrNull(escrow, "orderId")),
                DeliveryId = escrow == null ? null : GetStringOrNull(escrow, "deliveryId"),
                Op = group.Any(r => r.PayeeRole != LedgerFields.PayeeBuyer) ? ManualPayoutFields.OpRelease : ManualPayoutFields.OpRefund,
                Reason = first.ManualReason ?? "",
                EscrowStatus = status,
                FromStatus = status,
                DeliveryConfirmedAt = escrow == null ? null : GetStringOrNull(escrow, "deliveryConfirmedAt"),
                DisputeResolution = escrow == null ? null : GetStringOrNull(escrow, "disputeResolution"),
                QueuedAt = first.DueAt ?? first.UpdatedAt,
                Currency = "ETB",
                Legs = legs,
                PendingAmount = group.Sum(r => r.Amount)
            });
        }

        return result.OrderBy(p => p.QueuedAt, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// P.8: an admin paid a ledger row in the merchant portal and records its reference: the row (only a
    /// <c>manual</c> one) becomes <c>done</c> with <c>paidBy</c> the admin, and the escrow follows its rows. A
    /// replay with the same reference answers the recorded row; another reference is refused.
    /// </summary>
    private async Task<EscrowResult<CompleteManualPayoutResponse>> CompleteLedgerManualPayoutAsync(
        string escrowId, string reference, string externalRef, string actorUserId)
    {
        if (LedgerRepo == null)
        {
            return LedgerUnavailable<CompleteManualPayoutResponse>();
        }

        var row = await LedgerRepo.GetRowAsync(escrowId, reference);
        if (row == null)
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(404, "ESCROW_035", "No such payout leg.");
        }

        if (row.Status == LedgerFields.StatusDone)
        {
            if (!string.Equals(row.TelebirrRef, externalRef, StringComparison.Ordinal))
            {
                return EscrowResult<CompleteManualPayoutResponse>.Failure(409, "ESCROW_036",
                    "This leg is already recorded with a different reference.");
            }

            return EscrowResult<CompleteManualPayoutResponse>.Success(await LedgerCompleteResponseAsync(row, alreadyRecorded: true));
        }

        if (row.Status != LedgerFields.StatusManual)
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(409, "ESCROW_035",
                "This leg is not waiting in the manual payout queue.");
        }

        var done = await CompleteLedgerRowAsync(row, externalRef, actorUserId, row.Attempts, actorUserId);
        if (done.Row == null)
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(409, "ESCROW_035",
                "The leg changed while it was being recorded. Reload and try again.");
        }

        if (done.AlreadyDone && !string.Equals(done.Row.TelebirrRef, externalRef, StringComparison.Ordinal))
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(409, "ESCROW_036",
                "This leg is already recorded with a different reference.");
        }

        return EscrowResult<CompleteManualPayoutResponse>.Success(
            await LedgerCompleteResponseAsync(done.Row, alreadyRecorded: done.AlreadyDone));
    }

    private async Task<CompleteManualPayoutResponse> LedgerCompleteResponseAsync(LedgerRow row, bool alreadyRecorded)
    {
        var escrow = await _escrowRepo.GetByIdAsync(row.EscrowId, consistentRead: true);
        var remaining = (await LedgerRepo!.GetRowsAsync(row.EscrowId))
            .Where(r => r.Status == LedgerFields.StatusManual)
            .Select(r => r.Ref)
            .ToList();
        return new CompleteManualPayoutResponse
        {
            EscrowId = row.EscrowId,
            OrderId = row.OrderId,
            Ref = row.Ref,
            LegStatus = ManualPayoutFields.LegPaid,
            EscrowStatus = escrow == null ? "" : GetString(escrow, "status"),
            ExternalRef = row.TelebirrRef ?? "",
            PaidAt = row.DoneAt ?? row.UpdatedAt,
            RemainingLegs = remaining,
            AlreadyRecorded = alreadyRecorded,
            Message = alreadyRecorded ? "This leg was already recorded with this reference." : "Payment recorded."
        };
    }
}

/// <summary>ESC-20: the dispute verdicts the admin records (escrow-protocol.md 1.9.0 and 2.1.0, P.8).</summary>
internal static class DisputeVerdicts
{
    public const string FarmerWins = "farmer_wins";
    public const string BuyerWins = "buyer_wins";
    public const string PartialRefund = "partial_refund";

    /// <summary>
    /// The way the order and delivery follow a verdict: a split needs the goods delivered, like
    /// <c>farmer_wins</c>, so it follows that verdict's mirror and dispositions.
    /// </summary>
    public static string MirrorDirection(string? resolution) => resolution == PartialRefund ? FarmerWins : resolution ?? "";
}

/// <summary>ESC-20 (P.8): the admin's split, stored on the escrow as <c>verdictAmounts</c>.</summary>
internal sealed record VerdictAmounts(decimal FarmerAmount, decimal TruckerAmount, decimal BuyerRefund)
{
    public Dictionary<string, object> ToMap() => new(StringComparer.Ordinal)
    {
        ["farmerAmount"] = FarmerAmount,
        ["truckerAmount"] = TruckerAmount,
        ["buyerRefund"] = BuyerRefund
    };

    public static VerdictAmounts? From(Dictionary<string, object> escrow)
    {
        if (!escrow.TryGetValue(LedgerEscrowFields.VerdictAmounts, out var raw) || raw is not Dictionary<string, object> map)
        {
            return null;
        }

        decimal? Read(string key) => map.TryGetValue(key, out var value)
                                     && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                                         NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        return Read("farmerAmount") is { } farmer && Read("truckerAmount") is { } trucker && Read("buyerRefund") is { } buyer
            ? new VerdictAmounts(farmer, trucker, buyer)
            : null;
    }
}

using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-20: the escrow ledger runner (escrow-protocol.md 2.2.0). A ledger escrow
/// (<c>settlementModel: ledger</c>) settles leg by leg through write-ahead rows instead of the
/// whole-escrow settlement lock:
/// <list type="number">
/// <item>Decide: the rows go in <c>pending</c> with the escrow's legs and totals, in one transaction
/// conditioned on the escrow version (<see cref="DecideLedgerAsync"/>).</item>
/// <item>Call: <c>callingSince</c> is stamped by a conditional write, then the gateway is called with the
/// row's fixed <c>gatewayRef</c> (<see cref="CallLedgerRowAsync"/>).</item>
/// <item>Record: <c>done</c> (with the escrow status the rows call for), <c>failed</c>, <c>sent</c> (unknown
/// outcome) or <c>manual</c>, each with its escrow-audit row in the same transaction.</item>
/// </list>
/// An unknown outcome is never retried blindly: the sweep asks the gateway for the row's status first
/// (<see cref="RecoverLedgerRowAsync"/>), and a gateway that cannot say sends the row to <c>manual</c>.
/// </summary>
public sealed partial class EscrowService
{
    /// <summary>
    /// ESC-20: the ledger table. Wired in every Lambda that settles, records or reads order escrows;
    /// without it a ledger escrow fails closed (<c>503 ESCROW_012</c>), never falls back to the lock.
    /// </summary>
    internal IEscrowLedgerRepository? LedgerRepo { get; init; }

    /// <summary>
    /// ESC-20: <c>ESCROW_LEDGER_ENABLED</c>. New order escrows are born on the ledger only while this is on
    /// and the table is wired. Turning it off sends new escrows back to the 1.x path; escrows already on the
    /// ledger still settle there.
    /// </summary>
    internal bool LedgerBirthEnabled { get; init; }

    /// <summary>The ledger's clock (tests move it to age rows).</summary>
    internal Func<DateTime> LedgerClock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Attempts of a ledger write that lost an escrow or row compare-and-set to a concurrent writer.</summary>
    private const int MaxLedgerWriteAttempts = 4;

    /// <summary>How long a row whose payee the identity gate blocks waits before the sweep tries it again.</summary>
    private static readonly TimeSpan LedgerPayeeBlockedRetry = TimeSpan.FromHours(1);

    private static string Iso(DateTime time) => time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    internal static bool IsLedgerEscrow(Dictionary<string, object> escrow) => LedgerEscrowFields.IsLedger(escrow);

    // ──────────────────────────────────────────────
    // Decide (P.5 step 1)
    // ──────────────────────────────────────────────

    internal enum LedgerDecideOutcome
    {
        Written,

        /// <summary>Nothing the plan would move is still held (decided before).</summary>
        NothingToMove,

        /// <summary>The escrow left the status the caller validated.</summary>
        StatusMoved,

        /// <summary>The write would break an invariant: not sent, alarm logged.</summary>
        Invariant,

        /// <summary>Concurrent writers kept winning the compare-and-set.</summary>
        Busy,

        /// <summary>No ledger table in this Lambda.</summary>
        Unavailable
    }

    internal sealed record LedgerDecideResult(LedgerDecideOutcome Outcome, LedgerEscrow? Escrow, IReadOnlyList<LedgerRow> Rows);

    /// <summary>
    /// Decide a movement: read the escrow (and every row) again on each attempt, plan it, check I1 to I4,
    /// then write the rows <c>pending</c> with the escrow update in one transaction conditioned on the
    /// version read. <paramref name="callNow"/> tells the sweep whether the writer calls the gateway itself
    /// (the rows are due 15 minutes later) or leaves it to the sweep (due at once).
    /// </summary>
    internal async Task<LedgerDecideResult> DecideLedgerAsync(
        Dictionary<string, object> escrowItem, string expectedStatus, Func<LedgerEscrow, LedgerPlan?> planner,
        string actor, bool callNow, IReadOnlyDictionary<string, object>? extraFields = null)
    {
        if (LedgerRepo == null)
        {
            return new LedgerDecideResult(LedgerDecideOutcome.Unavailable, null, Array.Empty<LedgerRow>());
        }

        var item = escrowItem;
        var escrowId = GetString(item, "escrowId");
        for (var attempt = 1; attempt <= MaxLedgerWriteAttempts; attempt++)
        {
            if (attempt > 1)
            {
                item = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
                if (item == null)
                {
                    return new LedgerDecideResult(LedgerDecideOutcome.StatusMoved, null, Array.Empty<LedgerRow>());
                }
            }

            var state = LedgerEscrow.From(item);
            if (state.Status != expectedStatus)
            {
                return new LedgerDecideResult(LedgerDecideOutcome.StatusMoved, state, Array.Empty<LedgerRow>());
            }

            var plan = planner(state);
            if (plan == null)
            {
                return new LedgerDecideResult(LedgerDecideOutcome.NothingToMove, state, Array.Empty<LedgerRow>());
            }

            var existing = await LedgerRepo.GetRowsAsync(escrowId);
            var clash = plan.Rows.FirstOrDefault(seed => existing.Any(row => row.Ref == seed.Ref));
            var check = clash != null
                ? "I4"
                : EscrowLedgerBook.CheckTransition(plan.Before, plan.After)
                  ?? EscrowLedgerBook.CheckInvariants(plan.After,
                      existing.Select(r => (r.Ref, r.Amount)).Concat(plan.Rows.Select(r => (r.Ref, r.Amount))));
            if (check != null)
            {
                AlarmLog(MoneyAlarmLog.LedgerInvariant(escrowId, clash?.Ref ?? plan.Rows.FirstOrDefault()?.Ref ?? "-", check));
                return new LedgerDecideResult(LedgerDecideOutcome.Invariant, state, Array.Empty<LedgerRow>());
            }

            var now = LedgerClock();
            var nowIso = Iso(now);
            var due = Iso(callNow ? now + LedgerFields.RecheckAfter : now);
            var rows = plan.Rows.Select(seed => new LedgerRow
            {
                EscrowId = escrowId,
                Ref = seed.Ref,
                Kind = seed.Kind,
                Legs = seed.Legs,
                PayeeRole = seed.PayeeRole,
                PayeeId = seed.PayeeId,
                Amount = seed.Amount,
                Status = LedgerFields.StatusPending,
                GatewayRef = LedgerRefs.GatewayRef(escrowId, seed.Ref),
                HoldRef = state.HoldRef,
                Reason = seed.Reason,
                Actor = actor,
                OrderId = state.OrderId,
                CreatedAt = nowIso,
                UpdatedAt = nowIso,
                DueAt = due,
                Version = 1
            }).ToList();
            var audits = rows.Select(row => new LedgerAudit(escrowId, LedgerRefs.AuditTimestamp(nowIso, row.Ref, "ledger_pending"),
                "ledger_pending", state.Status, state.Status, actor, LedgerAuditMetadata(row, row.Attempts))).ToList();

            var write = await LedgerRepo.DecideAsync(new LedgerDecision(plan.Before, plan.After, rows, audits, extraFields));
            if (write == LedgerWrite.Written)
            {
                return new LedgerDecideResult(LedgerDecideOutcome.Written, plan.After with { Version = state.Version + 1 }, rows);
            }

            // RowExists or EscrowConflict: another writer got there first. Read again: the legs it moved
            // are no longer held, so the next plan leaves them alone.
        }

        return new LedgerDecideResult(LedgerDecideOutcome.Busy, null, Array.Empty<LedgerRow>());
    }

    private static string LedgerAuditMetadata(LedgerRow row, int attempt, string? code = null)
        => $"ref={row.Ref};amount={row.Amount.ToString("0.00", CultureInfo.InvariantCulture)};attempt={attempt}"
           + (code == null ? "" : $";code={code}");

    // ──────────────────────────────────────────────
    // Call and record (P.5 steps 2 to 7)
    // ──────────────────────────────────────────────

    internal enum LedgerCallOutcome
    {
        Done,
        Sent,
        Failed,
        Manual,

        /// <summary>The payee fails the identity gate: no call, the row waits (F2).</summary>
        Blocked,

        /// <summary>Another caller holds the row (its stamp, or its version moved).</summary>
        Busy,

        /// <summary>The gateway was called but the result write did not land: the stamp stays, the sweep checks the status.</summary>
        Unrecorded
    }

    internal sealed record LedgerCallResult(LedgerCallOutcome Outcome, LedgerRow Row, string? Status = null);

    /// <summary>
    /// Call the gateway for one <c>pending</c> or <c>failed</c> row without a stamp: the gates again, the
    /// <c>callingSince</c> stamp, the call with the row's <c>gatewayRef</c>, then the result write.
    /// </summary>
    internal async Task<LedgerCallResult> CallLedgerRowAsync(LedgerRow row, string actor)
    {
        if (LedgerRepo == null || row.Status is not (LedgerFields.StatusPending or LedgerFields.StatusFailed) || row.CallingSince != null)
        {
            return new LedgerCallResult(LedgerCallOutcome.Busy, row);
        }

        // The gates at call time: a payout only to a verified payee (F2: a payee who lost verification
        // waits), a refund only to a buyer whose phone is verified (F7: otherwise the manual queue).
        // FUP-29: a system call (the ledger sweep, a system settlement) also waits while the payee is
        // suspended or CRITICAL, the same deferral with the risk reason; the admin's own call does not.
        var payout = row.PayeeRole is LedgerFields.PayeeFarmer or LedgerFields.PayeeTrucker;
        string destination;
        if (payout)
        {
            var (dest, blockReason) = await ResolvePayoutAsync(row.PayeeId, row.Amount, row.PayeeRole!, actor, row.EscrowId);
            if (!dest.IsSuccess)
            {
                var deferred = await WriteLedgerStatusAsync(row, row.Status, actor, mutate: r => r with
                {
                    LastCode = $"PAYEE_BLOCKED:{blockReason ?? "unknown"}",
                    DueAt = Iso(LedgerClock() + LedgerPayeeBlockedRetry)
                }, audit: row.LastCode == $"PAYEE_BLOCKED:{blockReason ?? "unknown"}" ? null : "ledger_payee_blocked");
                return new LedgerCallResult(LedgerCallOutcome.Blocked, deferred ?? row);
            }

            destination = dest.Value!;
        }
        else
        {
            var phone = await ResolveVerifiedRefundPhoneAsync(row.PayeeId);
            if (phone == null)
            {
                var manual = await WriteLedgerStatusAsync(row, LedgerFields.StatusManual, actor, mutate: r => r with
                {
                    ManualReason = LedgerFields.ManualPayeeNotVerified,
                    DueAt = Iso(LedgerClock())
                });
                return new LedgerCallResult(LedgerCallOutcome.Manual, manual ?? row);
            }

            destination = phone;
        }

        var now = LedgerClock();
        var nowIso = Iso(now);
        var due = Iso(now + LedgerFields.RecheckAfter);
        if (!await LedgerRepo.TryStampCallingAsync(row, nowIso, due))
        {
            return new LedgerCallResult(LedgerCallOutcome.Busy, row);
        }

        // From here on the gateway may be called: the stamp stays until a result is recorded, so a
        // caller that dies leaves a row the sweep checks with the gateway before anything else.
        var stamped = row with { CallingSince = nowIso, DueAt = due, UpdatedAt = nowIso, Version = row.Version + 1 };
        bool success;
        string? code;
        string? telebirrRef;
        if (payout)
        {
            var response = await CallReleaseAsync(new ReleaseFundsRequest
            {
                TransactionRef = row.GatewayRef,
                HoldRef = row.HoldRef ?? "",
                FarmerPhone = destination,
                Amount = row.Amount,
                PlatformCommission = 0m
            });
            (success, code, telebirrRef) = (response.Success, response.ErrorCode, response.TelebirrRef);
        }
        else
        {
            var response = await CallRefundAsync(new RefundFundsRequest
            {
                TransactionRef = row.GatewayRef,
                HoldRef = row.HoldRef ?? "",
                BuyerPhone = destination,
                Amount = row.Amount
            });
            (success, code, telebirrRef) = (response.Success, response.ErrorCode, response.TelebirrRef);
        }

        try
        {
            return await RecordLedgerCallAsync(stamped, success, code, telebirrRef, actor);
        }
        catch (Exception ex)
        {
            // The answer is lost to this run: the stamp stays, and the sweep asks the gateway in 15 minutes.
            Console.WriteLine(
                $"ESCROW_LEDGER_RESULT_UNRECORDED escrowId={MoneyAlarmLog.LogId(row.EscrowId)} ref={MoneyAlarmLog.LogRef(row.Ref)} error={ex.GetType().Name}");
            return new LedgerCallResult(LedgerCallOutcome.Unrecorded, stamped);
        }
    }

    /// <summary>What the gateway's answer makes of a stamped row (P.5 steps 3 to 6).</summary>
    private async Task<LedgerCallResult> RecordLedgerCallAsync(
        LedgerRow stamped, bool success, string? code, string? telebirrRef, string actor)
    {
        // A call that never left (no time left, no token) is not an attempt: Telebirr saw nothing.
        var sentNothing = code is TelebirrGatewayErrors.NoTime or TelebirrGatewayErrors.TokenFailed;
        var attempts = stamped.Attempts + (sentNothing ? 0 : 1);
        var nowIso = Iso(LedgerClock());

        if (success && !string.IsNullOrEmpty(telebirrRef))
        {
            var done = await CompleteLedgerRowAsync(stamped, telebirrRef, LedgerFields.PaidByGateway, attempts, actor);
            return new LedgerCallResult(done.Row != null ? LedgerCallOutcome.Done : LedgerCallOutcome.Unrecorded,
                done.Row ?? stamped, done.EscrowStatus);
        }

        string status;
        Func<LedgerRow, LedgerRow> mutate;
        if (success || code == TelebirrGatewayErrors.PayoutNotSupported)
        {
            // P.5 step 6: no payout API, or a success without a reference: the admin queue at once.
            var reason = success ? LedgerFields.ManualGatewayRefMissing : LedgerFields.ManualPayoutNotSupported;
            status = LedgerFields.StatusManual;
            mutate = r => r with { Attempts = attempts, ManualReason = reason, LastCode = code, DueAt = nowIso };
        }
        else if (IsOutcomeUnknownCode(code))
        {
            // P.5 step 5: the money may have moved. Never called again until a status check says it did not.
            status = LedgerFields.StatusSent;
            mutate = r => r with
            {
                Attempts = attempts, LastCode = code, SentAt = nowIso, DueAt = Iso(LedgerClock() + LedgerFields.RecheckAfter)
            };
        }
        else if (attempts >= LedgerFields.MaxAttempts)
        {
            // P.5 step 4: the third definite refusal.
            status = LedgerFields.StatusManual;
            mutate = r => r with { Attempts = attempts, LastCode = code, ManualReason = LedgerFields.ManualRefused3, DueAt = nowIso };
        }
        else
        {
            status = LedgerFields.StatusFailed;
            mutate = r => r with
            {
                Attempts = attempts, LastCode = code, DueAt = Iso(LedgerClock() + LedgerFields.RecheckAfter)
            };
        }

        var written = await WriteLedgerStatusAsync(stamped, status, actor, mutate, code: code);
        if (written == null)
        {
            return new LedgerCallResult(LedgerCallOutcome.Unrecorded, stamped);
        }

        // A payout the live gateway cannot make is the expected manual queue (1.8.0), and a call that never
        // left saw nothing: neither is a settlement failure. Everything else is alarmed.
        if (written.ManualReason != LedgerFields.ManualPayoutNotSupported && !(status == LedgerFields.StatusFailed && sentNothing))
        {
            AlarmLog(MoneyAlarmLog.SettlementFailed(stamped.EscrowId, "ledger_" + status,
                status == LedgerFields.StatusSent || written.ManualReason == LedgerFields.ManualOutcomeUnknown
                    ? MoneyAlarmLog.OutcomeUnknown
                    : MoneyAlarmLog.OutcomeFailed,
                code ?? written.ManualReason));
        }

        return new LedgerCallResult(status switch
        {
            LedgerFields.StatusSent => LedgerCallOutcome.Sent,
            LedgerFields.StatusManual => LedgerCallOutcome.Manual,
            _ => LedgerCallOutcome.Failed
        }, written);
    }

    /// <summary>
    /// A row status write that is not <c>done</c>: the row (removing the stamp) and its audit row in one
    /// transaction, conditioned on the row version. Returns the row as written, or null when another
    /// writer changed it first. <paramref name="audit"/> null writes no audit row (a repeated deferral).
    /// </summary>
    private async Task<LedgerRow?> WriteLedgerStatusAsync(
        LedgerRow row, string status, string actor, Func<LedgerRow, LedgerRow> mutate, string? code = null,
        string? audit = "")
    {
        var nowIso = Iso(LedgerClock());
        var after = mutate(row with { Status = status, UpdatedAt = nowIso, CallingSince = null }) with { Version = row.Version + 1 };
        var audits = new List<LedgerAudit>();
        if (audit != null)
        {
            var action = audit.Length == 0 ? "ledger_" + status : audit;
            audits.Add(new LedgerAudit(row.EscrowId, LedgerRefs.AuditTimestamp(nowIso, row.Ref, action), action, "", "", actor,
                LedgerAuditMetadata(row, after.Attempts, code ?? after.ManualReason ?? after.LastCode)));
        }

        var write = await LedgerRepo!.TransitionAsync(new LedgerTransition { Before = row, After = after, Audits = audits });
        return write == LedgerWrite.Written ? after : null;
    }

    /// <summary>
    /// The result of making a row done. FUP-05: <paramref name="ClaimedByOther"/> (the row read, another admin's live
    /// claim on it) and <paramref name="ReferenceTaken"/> (the reference is recorded on another leg) refuse an
    /// admin's manual record; nothing was written.
    /// </summary>
    internal sealed record LedgerCompleteResult(
        LedgerRow? Row, string? EscrowStatus, bool AlreadyDone = false, bool ClaimedByOther = false, bool ReferenceTaken = false);

    /// <summary>
    /// P.5 step 3 and 7: the row becomes <c>done</c>. For a row of <c>amount</c> the escrow moves in the same
    /// transaction to the status the rows call for, conditioned on the escrow version, so two rows finishing
    /// at once serialize and the second sees the first. A duplicate refund (outside <c>amount</c>) leaves the
    /// escrow alone and writes the <c>order_payment_refunded_manual</c> row of 1.20.0 with it (P.9).
    /// </summary>
    internal async Task<LedgerCompleteResult> CompleteLedgerRowAsync(
        LedgerRow current, string telebirrRef, string paidBy, int attempts, string actor, ManualRecordGuard? manualRecord = null)
    {
        var row = current;
        for (var attempt = 1; attempt <= MaxLedgerWriteAttempts; attempt++)
        {
            if (attempt > 1)
            {
                row = await LedgerRepo!.GetRowAsync(current.EscrowId, current.Ref);
                if (row == null)
                {
                    return new LedgerCompleteResult(null, null);
                }
            }

            if (row.Status == LedgerFields.StatusDone)
            {
                return new LedgerCompleteResult(row, null, AlreadyDone: true);
            }

            // FUP-05: an admin's record of a row another admin has claimed is refused.
            if (manualRecord != null
                && ManualPayoutClaimFields.IsHeldByOther(row.ClaimedBy, row.ClaimExpiresAt, manualRecord.Actor, manualRecord.Now))
            {
                return new LedgerCompleteResult(row, null, ClaimedByOther: true);
            }

            var doneAt = Iso(LedgerClock());
            var after = row with
            {
                Status = LedgerFields.StatusDone,
                TelebirrRef = telebirrRef,
                PaidBy = paidBy,
                DoneAt = doneAt,
                UpdatedAt = doneAt,
                CallingSince = null,
                DueAt = null,
                Attempts = Math.Max(attempts, row.Attempts),
                Version = row.Version + 1
            };
            if (manualRecord != null)
            {
                // The record ends the claim (the write removes it).
                after = after with { ClaimedBy = null, ClaimedAt = null, ClaimExpiresAt = null };
            }
            var audits = new List<LedgerAudit>
            {
                new(row.EscrowId, LedgerRefs.AuditTimestamp(doneAt, row.Ref, "ledger_done"), "ledger_done", "", "", actor,
                    LedgerAuditMetadata(row, after.Attempts) + $";paidBy={paidBy}", telebirrRef)
            };

            LedgerTransition transition;
            string? newStatus = null;
            if (!row.IsOfAmount)
            {
                if (row.Ref.StartsWith(LedgerRefs.RefundDuplicatePrefix, StringComparison.Ordinal))
                {
                    var paymentOrderId = row.Ref[LedgerRefs.RefundDuplicatePrefix.Length..];
                    audits.Add(new LedgerAudit(row.EscrowId, LedgerRefs.AuditTimestamp(doneAt, row.Ref, UnappliedPayments.RefundedManual),
                        UnappliedPayments.RefundedManual, "", "", paidBy == LedgerFields.PaidByGateway ? SystemActor : paidBy,
                        UnappliedPayments.RefundMetadata(paymentOrderId, row.Amount, null) + $";ledgerRef={row.Ref}", telebirrRef));
                }

                transition = new LedgerTransition { Before = row, After = after, Audits = audits, ManualRecord = manualRecord };
            }
            else
            {
                var escrowItem = await _escrowRepo.GetByIdAsync(row.EscrowId, consistentRead: true);
                if (escrowItem == null)
                {
                    return new LedgerCompleteResult(null, null);
                }

                var escrow = LedgerEscrow.From(escrowItem);
                var rows = (await LedgerRepo!.GetRowsAsync(row.EscrowId))
                    .Select(r => r.Ref == row.Ref ? after : r).ToList();
                if (rows.All(r => r.Ref != row.Ref))
                {
                    rows.Add(after);
                }

                // The money has moved: an invariant that fails now is alarmed, never a reason to leave it unrecorded.
                if (EscrowLedgerBook.CheckInvariants(escrow, rows.Select(r => (r.Ref, r.Amount))) is { } broken)
                {
                    AlarmLog(MoneyAlarmLog.LedgerInvariant(row.EscrowId, row.Ref, broken));
                }

                newStatus = EscrowLedgerBook.StatusFor(escrow, rows);
                var fields = new Dictionary<string, object>();
                if (LedgerRefs.IsFarmerPayout(row.Ref))
                {
                    fields["telebirrReleaseRef"] = telebirrRef;
                    fields["releasedAt"] = doneAt;
                }
                else if (row.PayeeRole == LedgerFields.PayeeTrucker)
                {
                    fields["truckerTelebirrRef"] = telebirrRef;
                }
                else if (row.PayeeRole == LedgerFields.PayeeBuyer)
                {
                    fields["telebirrRefundRef"] = telebirrRef;
                }

                if (newStatus == "completed")
                {
                    fields["completedAt"] = doneAt;
                }
                else if (newStatus == "refunded")
                {
                    fields["refundedAt"] = doneAt;
                }

                if (newStatus != null)
                {
                    audits.Add(new LedgerAudit(row.EscrowId, LedgerRefs.AuditTimestamp(doneAt, row.Ref, "status#" + newStatus),
                        newStatus switch { "released" => "release", "completed" => "complete", _ => "refund" },
                        escrow.Status, newStatus, actor, $"ledger;ref={row.Ref}", telebirrRef));
                }

                transition = new LedgerTransition
                {
                    Before = row,
                    After = after,
                    Audits = audits,
                    Escrow = (escrow.EscrowId, escrow.CreatedAt, escrow.Version),
                    EscrowStatus = newStatus,
                    EscrowFields = fields,
                    ManualRecord = manualRecord
                };
            }

            var write = await LedgerRepo!.TransitionAsync(transition);
            if (write == LedgerWrite.ReferenceTaken)
            {
                return new LedgerCompleteResult(row, null, ReferenceTaken: true);
            }

            if (write != LedgerWrite.Written)
            {
                continue;
            }

            await AfterLedgerDoneAsync(after, newStatus);
            return new LedgerCompleteResult(after, newStatus);
        }

        return new LedgerCompleteResult(null, null);
    }

    /// <summary>After a row is done: the transaction-audit money row (P.7) and the order following the money.</summary>
    private async Task AfterLedgerDoneAsync(LedgerRow done, string? newStatus)
    {
        var transactionId = done.OrderId ?? done.EscrowId;
        await TransactionAuditWriter.EmitOnceAsync(_transactionAuditRepo, null, transactionId, "escrow", done.EscrowId,
            "escrow_money_moved", null, newStatus, AuditContext.ForSystem(),
            LedgerRefs.AuditTimestamp(done.DoneAt ?? done.UpdatedAt, done.Ref, "escrow_money_moved"),
            $"escrowId={done.EscrowId};ref={done.Ref};amount={done.Amount.ToString("0.00", CultureInfo.InvariantCulture)};payee={done.PayeeRole ?? "-"}");

        if (done.IsOfAmount)
        {
            await FollowMoneyAsync(done.EscrowId, "ledger");
        }
    }

    /// <summary>
    /// The buyer's refund destination: the registered phone, only when it is verified (F7). Null sends the
    /// refund to the manual queue. Without a user repository the id passes through, as for payouts.
    /// </summary>
    private async Task<string?> ResolveVerifiedRefundPhoneAsync(string? buyerId)
    {
        if (_userRepo == null)
        {
            return buyerId ?? "";
        }

        var buyer = string.IsNullOrEmpty(buyerId) ? null : await _userRepo.GetUserByIdAsync(buyerId);
        return buyer is { IsPhoneVerified: true } && !string.IsNullOrEmpty(buyer.PhoneNumber) ? buyer.PhoneNumber : null;
    }

    // ──────────────────────────────────────────────
    // Recovery (P.5 step 5, P.6)
    // ──────────────────────────────────────────────

    /// <summary>
    /// A row that may have reached the gateway (<c>sent</c>, or a stamp older than 15 minutes): ask the
    /// gateway about the same reference first. A payment it confirms is recorded <c>done</c>; one it says
    /// never happened is reopened as <c>failed</c> (the next call uses the same reference); anything else
    /// (the live gateway answers <c>unknown</c>) goes to <c>manual</c>. Nothing is retried blindly.
    /// </summary>
    internal async Task<LedgerCallResult> RecoverLedgerRowAsync(LedgerRow row, string actor)
    {
        // A stamped row whose result was never recorded made one call that is not counted yet.
        var attempts = row.Attempts + (row.CallingSince != null && row.Status != LedgerFields.StatusSent ? 1 : 0);
        TransactionStatusResponse? status;
        try
        {
            status = await _telebirrGateway.GetTransactionStatusAsync(row.GatewayRef);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"ESCROW_LEDGER_STATUS_CHECK_FAILED escrowId={MoneyAlarmLog.LogId(row.EscrowId)} ref={MoneyAlarmLog.LogRef(row.Ref)} error={ex.GetType().Name}");
            return new LedgerCallResult(LedgerCallOutcome.Busy, row);
        }

        var answer = status?.Status ?? "";
        if (answer is "completed" or "success")
        {
            if (!string.IsNullOrEmpty(status!.TelebirrRef))
            {
                var done = await CompleteLedgerRowAsync(row, status.TelebirrRef, LedgerFields.PaidByGateway, attempts, actor);
                return new LedgerCallResult(done.Row != null ? LedgerCallOutcome.Done : LedgerCallOutcome.Busy,
                    done.Row ?? row, done.EscrowStatus);
            }

            var noRef = await WriteLedgerStatusAsync(row, LedgerFields.StatusManual, actor, r => r with
            {
                Attempts = attempts, ManualReason = LedgerFields.ManualGatewayRefMissing, DueAt = Iso(LedgerClock())
            });
            return new LedgerCallResult(noRef == null ? LedgerCallOutcome.Busy : LedgerCallOutcome.Manual, noRef ?? row);
        }

        if (answer is "failed" or "not_found" or "expired")
        {
            var refused = attempts >= LedgerFields.MaxAttempts;
            var reopened = await WriteLedgerStatusAsync(row,
                refused ? LedgerFields.StatusManual : LedgerFields.StatusFailed, actor, r => r with
                {
                    Attempts = attempts,
                    LastCode = $"STATUS_{answer.ToUpperInvariant()}",
                    ManualReason = refused ? LedgerFields.ManualRefused3 : null,
                    DueAt = Iso(LedgerClock())
                }, code: $"STATUS_{answer.ToUpperInvariant()}");
            return new LedgerCallResult(reopened == null
                ? LedgerCallOutcome.Busy
                : refused ? LedgerCallOutcome.Manual : LedgerCallOutcome.Failed, reopened ?? row);
        }

        // No reliable status check: an admin looks in the merchant portal before anything is paid.
        var unknown = await WriteLedgerStatusAsync(row, LedgerFields.StatusManual, actor, r => r with
        {
            Attempts = attempts, ManualReason = LedgerFields.ManualOutcomeUnknown, DueAt = Iso(LedgerClock())
        }, code: $"STATUS_{(answer.Length == 0 ? "NONE" : answer.ToUpperInvariant())}");
        if (unknown != null)
        {
            AlarmLog(MoneyAlarmLog.SettlementFailed(row.EscrowId, "ledger_manual", MoneyAlarmLog.OutcomeUnknown,
                LedgerFields.ManualOutcomeUnknown));
        }

        return new LedgerCallResult(unknown == null ? LedgerCallOutcome.Busy : LedgerCallOutcome.Manual, unknown ?? row);
    }

    // ──────────────────────────────────────────────
    // The ledger sweep (P.6)
    // ──────────────────────────────────────────────

    internal sealed class LedgerSweepCounts
    {
        public int Seen { get; set; }
        public int Done { get; set; }
        public int Sent { get; set; }
        public int Failed { get; set; }
        public int Manual { get; set; }
        public int Blocked { get; set; }
        public int Skipped { get; set; }
        public int Overdue { get; set; }
        public int ManualOverdue { get; set; }
        public int Invariant { get; set; }
        public bool Truncated { get; set; }
        public bool QueryFailed { get; set; }
    }

    /// <summary>
    /// The ledger sweep (escrow cron, every 15 minutes): <c>pending</c>, <c>sent</c> and <c>failed</c> rows
    /// due now, oldest first, page by page while <paramref name="hasBudget"/> says the run has time left.
    /// Every row handled moves its <c>dueAt</c> on (a call, a stamp, a deferral), so rows it skips never
    /// hold back the rows behind them. Then the overdue lines, open rows first and the manual queue last.
    /// </summary>
    internal async Task<LedgerSweepCounts> SweepLedgerAsync(Func<bool> hasBudget, ILambdaContext? context = null)
    {
        var counts = new LedgerSweepCounts();
        if (LedgerRepo == null)
        {
            return counts;
        }

        var now = LedgerClock();
        var nowIso = Iso(now);
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var openStatus in new[] { LedgerFields.StatusSent, LedgerFields.StatusPending, LedgerFields.StatusFailed })
        {
            Dictionary<string, AttributeValue>? startKey = null;
            do
            {
                if (!hasBudget())
                {
                    counts.Truncated = true;
                    break;
                }

                List<LedgerRow> page;
                try
                {
                    (page, startKey) = await LedgerRepo.QueryOpenAsync(openStatus, nowIso, 50, startKey);
                }
                catch (Exception ex)
                {
                    counts.QueryFailed = true;
                    context?.Logger.LogError(MoneyAlarmLog.SettlementFailed(null, "sweep_failed", MoneyAlarmLog.OutcomeFailed,
                        $"ledger_query:{ex.GetType().Name}"));
                    break;
                }

                foreach (var indexed in page)
                {
                    if (!hasBudget())
                    {
                        counts.Truncated = true;
                        break;
                    }

                    counts.Seen++;
                    try
                    {
                        await SweepLedgerRowAsync(indexed, now, counts, touched);
                    }
                    catch (Exception ex)
                    {
                        counts.Skipped++;
                        context?.Logger.LogError(
                            $"Ledger sweep: row {MoneyAlarmLog.LogId(indexed.EscrowId)} {MoneyAlarmLog.LogRef(indexed.Ref)} failed: [{ex.GetType().Name}]. Continuing.");
                    }
                }
            } while (startKey != null && !counts.Truncated);
        }

        // Re-check I1 and I3 on every escrow the sweep touched.
        foreach (var escrowId in touched)
        {
            if (await CheckLedgerEscrowAsync(escrowId) is { } broken)
            {
                counts.Invariant++;
                AlarmLog(MoneyAlarmLog.LedgerInvariant(escrowId, "-", broken));
            }
        }

        await ReportLedgerOverdueAsync(now, counts, context);
        return counts;
    }

    private async Task SweepLedgerRowAsync(LedgerRow indexed, DateTime now, LedgerSweepCounts counts, HashSet<string> touched)
    {
        // The index is eventually consistent: act on the row as it is now.
        var row = await LedgerRepo!.GetRowAsync(indexed.EscrowId, indexed.Ref);
        if (row == null || row.Status is not (LedgerFields.StatusPending or LedgerFields.StatusSent or LedgerFields.StatusFailed)
            || (row.DueAt != null && string.CompareOrdinal(row.DueAt, Iso(now)) > 0))
        {
            counts.Skipped++;
            return;
        }

        LedgerCallResult result;
        if (row.Status == LedgerFields.StatusSent)
        {
            result = await RecoverLedgerRowAsync(row, SystemActor);
        }
        else if (row.CallingSince != null)
        {
            var stampedAt = ParseUtcTimeOrNull(row.CallingSince);
            if (stampedAt != null && now - stampedAt.Value < LedgerFields.RecheckAfter)
            {
                counts.Skipped++; // a caller is on it right now
                return;
            }

            // The caller stamped the row and never recorded an answer: the call may have reached the gateway.
            result = await RecoverLedgerRowAsync(row, SystemActor);
        }
        else
        {
            result = await CallLedgerRowAsync(row, SystemActor);
        }

        if (row.IsOfAmount)
        {
            touched.Add(row.EscrowId);
        }

        switch (result.Outcome)
        {
            case LedgerCallOutcome.Done: counts.Done++; break;
            case LedgerCallOutcome.Sent: counts.Sent++; break;
            case LedgerCallOutcome.Failed: counts.Failed++; break;
            case LedgerCallOutcome.Manual: counts.Manual++; break;
            case LedgerCallOutcome.Blocked: counts.Blocked++; break;
            default: counts.Skipped++; break;
        }
    }

    /// <summary>I1 and I3 on one ledger escrow and all its rows; null when they hold.</summary>
    private async Task<string?> CheckLedgerEscrowAsync(string escrowId)
    {
        var item = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        if (item == null || !IsLedgerEscrow(item))
        {
            return null;
        }

        var rows = await LedgerRepo!.GetRowsAsync(escrowId);
        return EscrowLedgerBook.CheckInvariants(LedgerEscrow.From(item), rows.Select(r => (r.Ref, r.Amount)));
    }

    /// <summary>
    /// <c>LEDGER_ROW_OVERDUE</c> for every open row older than 24 hours, and the 1.8.0
    /// <c>MANUAL_PAYOUT_OVERDUE</c> line (with the ledger ref) for every manual row waiting that long.
    /// </summary>
    private async Task ReportLedgerOverdueAsync(DateTime now, LedgerSweepCounts counts, ILambdaContext? context)
    {
        var cutoff = Iso(now - LedgerFields.OverdueAfter);
        foreach (var openStatus in new[] { LedgerFields.StatusPending, LedgerFields.StatusSent, LedgerFields.StatusFailed, LedgerFields.StatusManual })
        {
            Dictionary<string, AttributeValue>? startKey = null;
            var pages = 0;
            do
            {
                List<LedgerRow> page;
                try
                {
                    // dueAt is at most the row's age for a manual row (the time it entered manual); for the
                    // others it moves forward, so the age comes from createdAt below.
                    (page, startKey) = await LedgerRepo!.QueryOpenAsync(openStatus,
                        openStatus == LedgerFields.StatusManual ? cutoff : Iso(now + LedgerPayeeBlockedRetry), 100, startKey);
                }
                catch (Exception ex)
                {
                    context?.Logger.LogError($"Ledger overdue report: {openStatus} query failed: [{ex.GetType().Name}]");
                    break;
                }

                foreach (var row in page)
                {
                    var since = ParseUtcTimeOrNull(openStatus == LedgerFields.StatusManual ? row.DueAt : row.CreatedAt);
                    if (since == null || now - since.Value < LedgerFields.OverdueAfter)
                    {
                        continue;
                    }

                    var ageHours = (int)(now - since.Value).TotalHours;
                    if (openStatus == LedgerFields.StatusManual)
                    {
                        counts.ManualOverdue++;
                        AlarmLog(MoneyAlarmLog.ManualPayoutOverdue(row.EscrowId, row.Ref.Replace('#', '.'), ageHours));
                    }
                    else
                    {
                        counts.Overdue++;
                        AlarmLog(MoneyAlarmLog.LedgerRowOverdue(row.EscrowId, row.Ref, openStatus, ageHours));
                    }
                }
            } while (startKey != null && ++pages < 20);
        }
    }
}

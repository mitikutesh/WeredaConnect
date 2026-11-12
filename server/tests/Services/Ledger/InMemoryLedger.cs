using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Tests.Services.Ledger;

/// <summary>
/// ESC-20 test double: the ledger table, the escrow rows and the escrow-audit rows in memory, with the
/// conditions of <see cref="EscrowLedgerRepository"/> (row ref unique, row version and status, escrow
/// version, status, no lock or queue, moving legs held; audit keys unique). The escrow repository
/// substitute reads the same rows, so the service sees every write it makes. Hooks let a test act as a
/// concurrent writer between a read and a write.
/// </summary>
internal sealed class InMemoryLedger : IEscrowLedgerRepository
{
    public Dictionary<string, Dictionary<string, object>> Escrows { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string EscrowId, string Ref), LedgerRow> Rows { get; } = new();
    public List<LedgerAudit> Audits { get; } = new();

    /// <summary>FUP-05: the reference items of manual records, by key, with where each is recorded.</summary>
    public Dictionary<string, PayoutReferenceRecord> References { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs once before the next decision is applied (a concurrent writer).</summary>
    public Action? BeforeNextDecide { get; set; }

    /// <summary>Runs once before the next row transition is applied (a concurrent writer).</summary>
    public Action<LedgerTransition>? BeforeNextTransition { get; set; }

    /// <summary>Throws from the next transition write (a Lambda that dies after the gateway answered).</summary>
    public Exception? ThrowOnNextTransition { get; set; }

    public int DecideWrites { get; private set; }

    public IEscrowRepository EscrowRepo { get; }

    public InMemoryLedger()
    {
        EscrowRepo = Substitute.For<IEscrowRepository>();
        EscrowRepo.GetByIdAsync(Arg.Any<string>()).Returns(ci => Copy(ci.ArgAt<string>(0)));
        EscrowRepo.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(ci => Copy(ci.ArgAt<string>(0)));
        EscrowRepo.TrySetSettlementBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
    }

    public Dictionary<string, object>? Copy(string escrowId)
    {
        if (!Escrows.TryGetValue(escrowId, out var item))
        {
            return null;
        }

        var copy = new Dictionary<string, object>(item, StringComparer.Ordinal);
        if (copy.TryGetValue(LedgerEscrowFields.LegStatus, out var legs) && legs is Dictionary<string, object> map)
        {
            copy[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>(map, StringComparer.Ordinal);
        }

        return copy;
    }

    public Dictionary<string, object> Escrow(string escrowId) => Escrows[escrowId];

    public LedgerEscrow State(string escrowId) => LedgerEscrow.From(Escrows[escrowId]);

    public LedgerRow Row(string escrowId, string reference) => Rows[(escrowId, reference)];

    public List<LedgerRow> RowsOf(string escrowId) => Rows.Values.Where(r => r.EscrowId == escrowId).OrderBy(r => r.Ref, StringComparer.Ordinal).ToList();

    public List<LedgerAudit> AuditsOf(string escrowId) => Audits.Where(a => a.EscrowId == escrowId).ToList();

    /// <summary>Change the escrow as another writer would (every escrow write adds to its version).</summary>
    public void Touch(string escrowId, string? field = null, object? value = null)
    {
        var item = Escrows[escrowId];
        item["version"] = Convert.ToInt32(item["version"]) + 1;
        if (field != null)
        {
            item[field] = value!;
        }
    }

    public Task<List<LedgerRow>> GetRowsAsync(string escrowId) => Task.FromResult(RowsOf(escrowId));

    public Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> GetRowsPageAsync(
        string escrowId, int limit, Dictionary<string, AttributeValue>? startKey)
        => Task.FromResult<(List<LedgerRow>, Dictionary<string, AttributeValue>?)>(
            (RowsOf(escrowId).OrderBy(r => r.Ref, StringComparer.Ordinal).Take(limit).ToList(), null));

    public Task<LedgerRow?> GetRowAsync(string escrowId, string reference)
        => Task.FromResult(Rows.TryGetValue((escrowId, reference), out var row) ? row : null);

    public Task<LedgerWrite> DecideAsync(LedgerDecision decision)
    {
        var hook = BeforeNextDecide;
        BeforeNextDecide = null;
        hook?.Invoke();

        if (decision.Rows.Any(r => Rows.ContainsKey((r.EscrowId, r.Ref))))
        {
            return Task.FromResult(LedgerWrite.RowExists);
        }

        var item = Escrows[decision.Before.EscrowId];
        var current = LedgerEscrow.From(item);
        var moving = decision.Before.Legs.Keys.Where(l => decision.Before.IsHeld(l) && !decision.After.IsHeld(l));
        if (current.Version != decision.Before.Version || current.Status != decision.Before.Status
            || item.ContainsKey(SettlementLockFields.Lock) || item.ContainsKey(ManualPayoutFields.Marker)
            || moving.Any(l => !current.IsHeld(l)))
        {
            return Task.FromResult(LedgerWrite.EscrowConflict);
        }

        if (decision.Audits.Any(a => Audits.Any(x => x.EscrowId == a.EscrowId && x.Timestamp == a.Timestamp)))
        {
            return Task.FromResult(LedgerWrite.EscrowConflict);
        }

        var after = decision.After;
        item[LedgerEscrowFields.LegStatus] = after.Legs.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.Ordinal);
        item["farmerPayout"] = after.FarmerPayout;
        if (after.TruckerPayout != decision.Before.TruckerPayout || item.ContainsKey("truckerPayout"))
        {
            item["truckerPayout"] = after.TruckerPayout;
        }

        item["commission"] = after.Commission;
        item[LedgerEscrowFields.RefundedTotal] = after.RefundedTotal;
        item[LedgerEscrowFields.ReleasedTotal] = after.ReleasedTotal;
        item["version"] = current.Version + 1;
        foreach (var (field, value) in decision.ExtraEscrowFields ?? new Dictionary<string, object>())
        {
            item[field] = value;
        }

        foreach (var row in decision.Rows)
        {
            Rows[(row.EscrowId, row.Ref)] = row;
        }

        Audits.AddRange(decision.Audits);
        DecideWrites++;
        return Task.FromResult(LedgerWrite.Written);
    }

    public Task<LedgerWrite> PutRowAsync(LedgerRow row, LedgerAudit audit)
    {
        if (Rows.ContainsKey((row.EscrowId, row.Ref)))
        {
            return Task.FromResult(LedgerWrite.RowExists);
        }

        Rows[(row.EscrowId, row.Ref)] = row;
        Audits.Add(audit);
        return Task.FromResult(LedgerWrite.Written);
    }

    public Task<bool> TryStampCallingAsync(LedgerRow row, string now, string dueAt)
    {
        if (!Rows.TryGetValue((row.EscrowId, row.Ref), out var current)
            || current.Version != row.Version
            || current.Status is not (LedgerFields.StatusPending or LedgerFields.StatusFailed)
            || current.CallingSince != null)
        {
            return Task.FromResult(false);
        }

        Rows[(row.EscrowId, row.Ref)] = current with
        {
            CallingSince = now,
            DueAt = dueAt,
            UpdatedAt = now,
            Version = current.Version + 1
        };
        return Task.FromResult(true);
    }

    public Task<LedgerWrite> TransitionAsync(LedgerTransition transition)
    {
        var hook = BeforeNextTransition;
        BeforeNextTransition = null;
        hook?.Invoke(transition);

        if (ThrowOnNextTransition is { } ex)
        {
            ThrowOnNextTransition = null;
            throw ex;
        }

        var key = (transition.Before.EscrowId, transition.Before.Ref);
        if (!Rows.TryGetValue(key, out var current) || current.Version != transition.Before.Version
            || current.Status != transition.Before.Status)
        {
            return Task.FromResult(LedgerWrite.RowConflict);
        }

        if (transition.Escrow is { } escrow)
        {
            var item = Escrows[escrow.EscrowId];
            if (Convert.ToInt32(item["version"]) != escrow.Version)
            {
                return Task.FromResult(LedgerWrite.EscrowConflict);
            }
        }

        if (transition.Audits.Any(a => Audits.Any(x => x.EscrowId == a.EscrowId && x.Timestamp == a.Timestamp)))
        {
            return Task.FromResult(LedgerWrite.EscrowConflict);
        }

        // FUP-05: the claim condition on the row, then the reference item's attribute_not_exists.
        if (transition.ManualRecord is { } manual)
        {
            if (ManualPayoutClaimFields.IsHeldByOther(current.ClaimedBy, current.ClaimExpiresAt, manual.Actor, manual.Now))
            {
                return Task.FromResult(LedgerWrite.RowConflict);
            }

            if (manual.Reference is { } reference)
            {
                if (References.ContainsKey(PayoutReferenceFields.Key(reference.ExternalRef)))
                {
                    return Task.FromResult(LedgerWrite.ReferenceTaken);
                }

                References[PayoutReferenceFields.Key(reference.ExternalRef)] = reference;
            }
        }

        var after = transition.After;
        if (after.Status != LedgerFields.StatusDone && after.DueAt == null)
        {
            after = after with { DueAt = after.UpdatedAt };
        }

        Rows[key] = after;
        if (transition.Escrow is { } target)
        {
            var item = Escrows[target.EscrowId];
            item["version"] = target.Version + 1;
            if (transition.EscrowStatus != null)
            {
                item["status"] = transition.EscrowStatus;
                item.Remove(SettlementBlockFields.Reason);
                item.Remove(SettlementBlockFields.ReasonAt);
            }

            foreach (var (field, value) in transition.EscrowFields ?? new Dictionary<string, object>())
            {
                item[field] = value;
            }
        }

        Audits.AddRange(transition.Audits);
        return Task.FromResult(LedgerWrite.Written);
    }

    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? Next)> QueryOwedTransportLegsAsync(
        string status, Dictionary<string, AttributeValue>? startKey)
    {
        var items = Escrows.Values
            .Where(e => (string)e["status"] == status && LedgerEscrowFields.IsLedger(e))
            .Select(e => LedgerEscrow.From(e))
            .Where(e => e.IsHeld(LedgerEscrowFields.LegTransport) && !e.IsHeld(LedgerEscrowFields.LegCrop))
            .Select(e => Copy(e.EscrowId)!)
            .ToList();
        return Task.FromResult((items, (Dictionary<string, AttributeValue>?)null));
    }

    public Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryOpenAsync(
        string openStatus, string dueBefore, int limit, Dictionary<string, AttributeValue>? startKey)
    {
        var skip = startKey != null && startKey.TryGetValue("skip", out var s) ? int.Parse(s.N) : 0;
        var all = Rows.Values
            .Where(r => r.Status == openStatus && string.CompareOrdinal(r.DueAt ?? r.CreatedAt, dueBefore) <= 0)
            .OrderBy(r => r.DueAt ?? r.CreatedAt, StringComparer.Ordinal)
            .ToList();
        var page = all.Skip(skip).Take(limit).ToList();
        var next = skip + limit < all.Count
            ? new Dictionary<string, AttributeValue> { ["skip"] = new() { N = (skip + limit).ToString() } }
            : null;
        return Task.FromResult((page, next));
    }
}

/// <summary>
/// ESC-20 test gateway: answers from a script per call (in order), records every call, and answers status
/// checks from <see cref="StatusAnswer"/>. Unscripted calls succeed with a fresh reference.
/// </summary>
internal sealed class ScriptedGateway : ITelebirrGateway
{
    public List<(string Kind, string TransactionRef, decimal Amount, string Destination, string HoldRef)> Calls { get; } = new();
    public List<string> StatusChecks { get; } = new();
    public Queue<(bool Success, string? Code, string? Ref)> Script { get; } = new();
    public Func<string, TransactionStatusResponse> StatusAnswer { get; set; } =
        r => new TransactionStatusResponse { TransactionRef = r, Status = "unknown", Amount = 0m, UpdatedAt = "" };

    public bool IsLiveTelebirr { get; set; }
    public bool SupportsHostedCheckout => false;

    private (bool Success, string? Code, string? Ref) Next(string kind, string reference)
        => Script.Count > 0 ? Script.Dequeue() : (true, null, $"GW-{kind}-{Calls.Count}");

    public Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request)
    {
        Calls.Add(("release", request.TransactionRef, request.Amount, request.FarmerPhone, request.HoldRef));
        var (success, code, reference) = Next("REL", request.TransactionRef);
        return Task.FromResult(new ReleaseFundsResponse { Success = success, ErrorCode = code, TelebirrRef = reference });
    }

    public Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request)
    {
        Calls.Add(("refund", request.TransactionRef, request.Amount, request.BuyerPhone, request.HoldRef));
        var (success, code, reference) = Next("REF", request.TransactionRef);
        return Task.FromResult(new RefundFundsResponse { Success = success, ErrorCode = code, TelebirrRef = reference });
    }

    public Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef)
    {
        StatusChecks.Add(transactionRef);
        return Task.FromResult(StatusAnswer(transactionRef));
    }

    public Task<HoldFundsResponse> HoldFundsAsync(HoldFundsRequest request) => throw new NotSupportedException();

    public string? BuildCheckoutUrl(string prepayId) => null;
}

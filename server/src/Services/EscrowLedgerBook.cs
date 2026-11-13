using System.Globalization;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Services;

/// <summary>ESC-20: one row of the escrow ledger (escrow-protocol.md 2.2.0, P.3).</summary>
internal sealed record LedgerRow
{
    public required string EscrowId { get; init; }
    public required string Ref { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Legs { get; init; } = Array.Empty<string>();
    public string? PayeeRole { get; init; }
    public string? PayeeId { get; init; }
    public required decimal Amount { get; init; }
    public required string Status { get; init; }
    public required string GatewayRef { get; init; }

    /// <summary>The payment the gateway call refers to: the escrow's hold, or a duplicate payment's own id.</summary>
    public string? HoldRef { get; init; }
    public string? TelebirrRef { get; init; }
    public int Attempts { get; init; }
    public string? LastCode { get; init; }
    public string? CallingSince { get; init; }
    public string? ManualReason { get; init; }
    public required string Reason { get; init; }
    public required string Actor { get; init; }
    public string? PaidBy { get; init; }
    public string? OrderId { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public string? SentAt { get; init; }
    public string? DoneAt { get; init; }
    public string? DueAt { get; init; }
    public int Version { get; init; }

    /// <summary>FUP-05: the admin who claimed this manual row, when, and until when (bookkeeping, no version).</summary>
    public string? ClaimedBy { get; init; }
    public string? ClaimedAt { get; init; }
    public string? ClaimExpiresAt { get; init; }

    /// <summary>
    /// FUP-06: the <see cref="DueAt"/> (when the row entered <c>manual</c>) whose overdue line the ledger sweep has
    /// announced (<c>MANUAL_PAYOUT_NEWLY_OVERDUE</c>). Bookkeeping, no version; never written by a row write.
    /// </summary>
    public string? ManualOverdueLoggedFor { get; init; }

    /// <summary>
    /// FUP-436: the <see cref="CreatedAt"/> of a row open in <c>pending</c>, <c>sent</c> or <c>failed</c> whose overdue
    /// line the ledger sweep has announced (<c>LEDGER_ROW_NEWLY_OVERDUE</c>). Bookkeeping, no version; never written
    /// by a row write.
    /// </summary>
    public string? OverdueLoggedFor { get; init; }

    public bool IsOfAmount => LedgerRefs.IsRowOfAmount(Ref);

    /// <summary>A row the gateway pays out of the platform account to the buyer.</summary>
    public bool IsRefundToBuyer => PayeeRole == LedgerFields.PayeeBuyer;
}

/// <summary>A row a decision writes, before it has a status or a time.</summary>
internal sealed record LedgerRowSeed(
    string Ref, string Kind, IReadOnlyList<string> Legs, string? PayeeRole, string? PayeeId, decimal Amount, string Reason);

/// <summary>
/// ESC-20: the money view of a ledger escrow (P.2). The leg amounts are what each leg still carries:
/// crop is <see cref="FarmerPayout"/>, transport <see cref="TruckerPayout"/>, commission
/// <see cref="Commission"/>. A leg that had no money at funding is absent from <see cref="Legs"/>.
/// </summary>
internal sealed record LedgerEscrow
{
    public required string EscrowId { get; init; }
    public required string CreatedAt { get; init; }
    public required int Version { get; init; }
    public required string Status { get; init; }
    public required decimal Amount { get; init; }
    public required decimal FarmerPayout { get; init; }
    public required decimal TruckerPayout { get; init; }
    public required decimal Commission { get; init; }
    public required decimal RefundedTotal { get; init; }
    public required decimal ReleasedTotal { get; init; }
    public required IReadOnlyDictionary<string, string> Legs { get; init; }
    public required string BuyerId { get; init; }
    public required string FarmerId { get; init; }
    public string? TruckerId { get; init; }
    public string? OrderId { get; init; }
    public string? HoldRef { get; init; }

    public decimal LegAmount(string leg) => leg switch
    {
        LedgerEscrowFields.LegCrop => FarmerPayout,
        LedgerEscrowFields.LegTransport => TruckerPayout,
        LedgerEscrowFields.LegCommission => Commission,
        _ => 0m
    };

    public string? LegState(string leg) => Legs.TryGetValue(leg, out var state) ? state : null;

    public bool IsHeld(string leg) => LegState(leg) == LedgerEscrowFields.Held;

    public static LedgerEscrow From(Dictionary<string, object> item)
    {
        var legs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetValue(LedgerEscrowFields.LegStatus, out var raw) && raw is Dictionary<string, object> map)
        {
            foreach (var (leg, state) in map)
            {
                if (state?.ToString() is { Length: > 0 } value)
                {
                    legs[leg] = value;
                }
            }
        }

        return new LedgerEscrow
        {
            EscrowId = Str(item, "escrowId"),
            CreatedAt = Str(item, "createdAt"),
            Version = (int)Dec(item, "version"),
            Status = Str(item, "status"),
            Amount = Dec(item, "amount"),
            FarmerPayout = Dec(item, "farmerPayout"),
            TruckerPayout = Dec(item, "truckerPayout"),
            Commission = Dec(item, "commission"),
            RefundedTotal = Dec(item, LedgerEscrowFields.RefundedTotal),
            ReleasedTotal = Dec(item, LedgerEscrowFields.ReleasedTotal),
            Legs = legs,
            BuyerId = Str(item, "buyerId"),
            FarmerId = Str(item, "farmerId"),
            TruckerId = NullIfEmpty(Str(item, "truckerId")),
            OrderId = NullIfEmpty(Str(item, "orderId")),
            HoldRef = NullIfEmpty(Str(item, "telebirrHoldRef"))
        };
    }

    private static string Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static decimal Dec(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return 0m;
        }

        return value switch
        {
            decimal d => d,
            int i => i,
            long l => l,
            double dbl => (decimal)dbl,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };
    }
}

/// <summary>What one decision changes: the escrow after it, and the rows it writes.</summary>
internal sealed record LedgerPlan(LedgerEscrow Before, LedgerEscrow After, IReadOnlyList<LedgerRowSeed> Rows);

/// <summary>A split the rules refuse (<c>400 ADMIN_021</c>), with the field it names.</summary>
internal sealed record LedgerSplitRefusal(string Field, string Message);

/// <summary>
/// ESC-20: the pure rules of the escrow ledger (escrow-protocol.md 2.2.0): the plans of every movement,
/// the invariants I1 to I4 and the escrow status the rows call for. No I/O, so every path is tested
/// directly.
/// </summary>
internal static class EscrowLedgerBook
{
    /// <summary>
    /// The legs an order escrow is born with: every leg that carries money is <c>held</c> (the transport leg
    /// of a self-pickup order and a zero commission are absent).
    /// </summary>
    public static Dictionary<string, object> BirthLegs(decimal farmerPayout, decimal truckerPayout, decimal commission)
    {
        var legs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [LedgerEscrowFields.LegCrop] = LedgerEscrowFields.Held
        };
        if (truckerPayout > 0)
        {
            legs[LedgerEscrowFields.LegTransport] = LedgerEscrowFields.Held;
        }

        if (commission > 0)
        {
            legs[LedgerEscrowFields.LegCommission] = LedgerEscrowFields.Held;
        }

        return legs;
    }

    /// <summary>
    /// A release (P.4): <c>release#farmer</c> when the crop leg is held and the farmer may be paid
    /// (the commission is then <c>kept</c>), and <c>release#trucker#{truckerId}</c> when the transport leg
    /// is held and the trucker may be paid. The legs settle in any order (F1, F2). Null when nothing moves.
    /// </summary>
    public static LedgerPlan? PlanRelease(LedgerEscrow before, bool payFarmer, bool payTrucker, string reason)
    {
        var legs = new Dictionary<string, string>(before.Legs, StringComparer.Ordinal);
        var rows = new List<LedgerRowSeed>();
        var released = before.ReleasedTotal;
        var moved = false;

        if (payFarmer && before.IsHeld(LedgerEscrowFields.LegCrop))
        {
            legs[LedgerEscrowFields.LegCrop] = LedgerEscrowFields.Released;
            if (before.IsHeld(LedgerEscrowFields.LegCommission))
            {
                legs[LedgerEscrowFields.LegCommission] = LedgerEscrowFields.Kept;
            }

            if (before.FarmerPayout > 0)
            {
                rows.Add(new LedgerRowSeed(LedgerRefs.ReleaseFarmer, LedgerFields.KindRelease,
                    new[] { LedgerEscrowFields.LegCrop }, LedgerFields.PayeeFarmer, before.FarmerId, before.FarmerPayout, reason));
                released += before.FarmerPayout;
            }

            moved = true;
        }

        if (payTrucker && before.IsHeld(LedgerEscrowFields.LegTransport) && before.TruckerPayout > 0
            && !string.IsNullOrEmpty(before.TruckerId))
        {
            legs[LedgerEscrowFields.LegTransport] = LedgerEscrowFields.Released;
            rows.Add(new LedgerRowSeed(LedgerRefs.ReleaseTrucker(before.TruckerId), LedgerFields.KindRelease,
                new[] { LedgerEscrowFields.LegTransport }, LedgerFields.PayeeTrucker, before.TruckerId, before.TruckerPayout, reason));
            released += before.TruckerPayout;
            moved = true;
        }

        return moved
            ? new LedgerPlan(before, before with { Legs = legs, ReleasedTotal = Round(released) }, rows)
            : null;
    }

    /// <summary>
    /// <c>refund#all</c> (P.4): every held leg goes back to the buyer in one row, and each refunded leg is
    /// set to 0. Null when no leg is held.
    /// </summary>
    public static LedgerPlan? PlanRefundAll(LedgerEscrow before, string reason)
    {
        var held = LedgerEscrowFields.AllLegs.Where(before.IsHeld).ToList();
        if (held.Count == 0)
        {
            return null;
        }

        var legs = new Dictionary<string, string>(before.Legs, StringComparer.Ordinal);
        var amount = 0m;
        foreach (var leg in held)
        {
            legs[leg] = LedgerEscrowFields.Refunded;
            amount += before.LegAmount(leg);
        }

        amount = Round(amount);
        var after = before with
        {
            Legs = legs,
            FarmerPayout = held.Contains(LedgerEscrowFields.LegCrop) ? 0m : before.FarmerPayout,
            TruckerPayout = held.Contains(LedgerEscrowFields.LegTransport) ? 0m : before.TruckerPayout,
            Commission = held.Contains(LedgerEscrowFields.LegCommission) ? 0m : before.Commission,
            RefundedTotal = Round(before.RefundedTotal + amount)
        };
        var rows = amount > 0
            ? new[] { new LedgerRowSeed(LedgerRefs.RefundAll, LedgerFields.KindRefund, held, LedgerFields.PayeeBuyer, before.BuyerId, amount, reason) }
            : Array.Empty<LedgerRowSeed>();
        return new LedgerPlan(before, after, rows);
    }

    /// <summary>
    /// The admin's per-leg verdict (P.8, F5). Each amount is 0 or more with at most 2 decimals;
    /// <paramref name="farmerAmount"/> is at most the crop leg held and <paramref name="truckerAmount"/> at
    /// most the transport leg held (0 when it is not held). The commission is kept when the farmer gets
    /// anything and goes back to the buyer otherwise, so the three amounts add up to the crop and transport
    /// legs held, plus the commission held when <paramref name="farmerAmount"/> is 0. Writes
    /// <c>verdict#farmer</c>, <c>verdict#trucker</c> and <c>verdict#buyer</c> for the amounts above 0.
    /// </summary>
    public static (LedgerPlan? Plan, LedgerSplitRefusal? Refusal) PlanSplit(
        LedgerEscrow before, decimal farmerAmount, decimal truckerAmount, decimal buyerRefund, string reason)
    {
        foreach (var (field, value) in new[] { ("farmerAmount", farmerAmount), ("truckerAmount", truckerAmount), ("buyerRefund", buyerRefund) })
        {
            if (value < 0 || Round(value) != value)
            {
                return (null, new LedgerSplitRefusal(field, $"{field} must be 0 or more with at most 2 decimals."));
            }
        }

        if (!before.IsHeld(LedgerEscrowFields.LegCrop))
        {
            return (null, new LedgerSplitRefusal("farmerAmount", "The crop leg is no longer held, so there is nothing to split."));
        }

        var cropHeld = before.FarmerPayout;
        if (farmerAmount > cropHeld)
        {
            return (null, new LedgerSplitRefusal("farmerAmount",
                $"farmerAmount must be at most the crop leg held ({Format(cropHeld)} ETB)."));
        }

        var transportHeld = before.IsHeld(LedgerEscrowFields.LegTransport) ? before.TruckerPayout : 0m;
        if (truckerAmount > transportHeld)
        {
            return (null, new LedgerSplitRefusal("truckerAmount",
                $"truckerAmount must be at most the transport leg held ({Format(transportHeld)} ETB)."));
        }

        if (truckerAmount > 0 && string.IsNullOrEmpty(before.TruckerId))
        {
            return (null, new LedgerSplitRefusal("truckerAmount", "This escrow has no trucker to pay."));
        }

        var commissionHeld = before.IsHeld(LedgerEscrowFields.LegCommission) ? before.Commission : 0m;
        var keepCommission = farmerAmount > 0;
        var expected = Round(cropHeld + transportHeld + (keepCommission ? 0m : commissionHeld));
        if (Round(farmerAmount + truckerAmount + buyerRefund) != expected)
        {
            return (null, new LedgerSplitRefusal("buyerRefund",
                $"farmerAmount + truckerAmount + buyerRefund must equal {Format(expected)} ETB: the crop and transport legs held"
                + (keepCommission ? " (the commission is kept because the farmer is paid)." : ", plus the commission held because the farmer gets nothing.")));
        }

        var legs = new Dictionary<string, string>(before.Legs, StringComparer.Ordinal);
        var toBuyer = new List<string>();
        legs[LedgerEscrowFields.LegCrop] = farmerAmount > 0 ? LedgerEscrowFields.Released : LedgerEscrowFields.Refunded;
        if (farmerAmount < cropHeld)
        {
            toBuyer.Add(LedgerEscrowFields.LegCrop);
        }

        if (before.IsHeld(LedgerEscrowFields.LegTransport))
        {
            legs[LedgerEscrowFields.LegTransport] = truckerAmount > 0 ? LedgerEscrowFields.Released : LedgerEscrowFields.Refunded;
            if (truckerAmount < transportHeld)
            {
                toBuyer.Add(LedgerEscrowFields.LegTransport);
            }
        }

        if (before.IsHeld(LedgerEscrowFields.LegCommission))
        {
            legs[LedgerEscrowFields.LegCommission] = keepCommission ? LedgerEscrowFields.Kept : LedgerEscrowFields.Refunded;
            if (!keepCommission)
            {
                toBuyer.Add(LedgerEscrowFields.LegCommission);
            }
        }

        var after = before with
        {
            Legs = legs,
            FarmerPayout = farmerAmount,
            TruckerPayout = before.IsHeld(LedgerEscrowFields.LegTransport) ? truckerAmount : before.TruckerPayout,
            Commission = before.IsHeld(LedgerEscrowFields.LegCommission) && !keepCommission ? 0m : before.Commission,
            RefundedTotal = Round(before.RefundedTotal + buyerRefund),
            ReleasedTotal = Round(before.ReleasedTotal + farmerAmount + truckerAmount)
        };

        var rows = new List<LedgerRowSeed>();
        if (farmerAmount > 0)
        {
            rows.Add(new LedgerRowSeed(LedgerRefs.VerdictFarmer, LedgerFields.KindVerdict,
                new[] { LedgerEscrowFields.LegCrop }, LedgerFields.PayeeFarmer, before.FarmerId, farmerAmount, reason));
        }

        if (truckerAmount > 0)
        {
            rows.Add(new LedgerRowSeed(LedgerRefs.VerdictTrucker, LedgerFields.KindVerdict,
                new[] { LedgerEscrowFields.LegTransport }, LedgerFields.PayeeTrucker, before.TruckerId, truckerAmount, reason));
        }

        if (buyerRefund > 0)
        {
            rows.Add(new LedgerRowSeed(LedgerRefs.VerdictBuyer, LedgerFields.KindVerdict,
                toBuyer, LedgerFields.PayeeBuyer, before.BuyerId, buyerRefund, reason));
        }

        return (new LedgerPlan(before, after, rows), null);
    }

    /// <summary>
    /// I1 to I3 (P.6) on an escrow and every row it has (the existing ones and those about to be written).
    /// Returns the first check that fails, or null.
    /// </summary>
    public static string? CheckInvariants(LedgerEscrow escrow, IEnumerable<(string Ref, decimal Amount)> rows)
    {
        // I1: the legs still carried plus what went back to the buyer is what the buyer paid.
        if (Round(escrow.FarmerPayout + escrow.TruckerPayout + escrow.Commission + escrow.RefundedTotal) != Round(escrow.Amount))
        {
            return "I1";
        }

        // I2: what was decided for the farmer and the trucker is the amount of the released legs.
        var releasedLegs = LedgerEscrowFields.AllLegs
            .Where(leg => escrow.LegState(leg) == LedgerEscrowFields.Released)
            .Sum(escrow.LegAmount);
        if (Round(releasedLegs) != Round(escrow.ReleasedTotal))
        {
            return "I2";
        }

        // I3: the rows of amount add up to everything decided so far.
        var rowsOfAmount = rows.Where(r => LedgerRefs.IsRowOfAmount(r.Ref)).Sum(r => r.Amount);
        if (Round(rowsOfAmount) != Round(escrow.ReleasedTotal + escrow.RefundedTotal))
        {
            return "I3";
        }

        if (escrow.FarmerPayout < 0 || escrow.TruckerPayout < 0 || escrow.Commission < 0
            || escrow.RefundedTotal < 0 || escrow.ReleasedTotal < 0)
        {
            return "I1";
        }

        return null;
    }

    /// <summary>
    /// I4 (P.6) on one decision: a leg leaves <c>held</c> once. A leg that was not held keeps its state and
    /// amount, no leg appears or disappears, <c>amount</c> never changes and the totals only grow.
    /// </summary>
    public static string? CheckTransition(LedgerEscrow before, LedgerEscrow after)
    {
        if (before.Amount != after.Amount
            || after.RefundedTotal < before.RefundedTotal
            || after.ReleasedTotal < before.ReleasedTotal
            || !before.Legs.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .SequenceEqual(after.Legs.Keys.OrderBy(k => k, StringComparer.Ordinal)))
        {
            return "I4";
        }

        foreach (var leg in before.Legs.Keys)
        {
            if (!before.IsHeld(leg)
                && (after.LegState(leg) != before.LegState(leg) || after.LegAmount(leg) != before.LegAmount(leg)))
            {
                return "I4";
            }
        }

        return null;
    }

    /// <summary>
    /// The escrow status the rows call for (P.5), or null to leave it. The status follows the money: it
    /// moves in the write that makes the deciding row <c>done</c>.
    /// <list type="bullet">
    /// <item><c>refunded</c>: every leg decided, the crop leg refunded (the sale did not happen; a transport leg
    /// paid before stays paid and counted in <c>releasedTotal</c>), every row of amount done.</item>
    /// <item><c>completed</c>: every leg released, refunded or kept, the crop leg released, every row of amount done.</item>
    /// <item><c>released</c>: a farmer payout is done and something is still open.</item>
    /// </list>
    /// A duplicate refund (outside <c>amount</c>) never holds back <c>completed</c> or <c>refunded</c>.
    /// </summary>
    public static string? StatusFor(LedgerEscrow escrow, IReadOnlyList<LedgerRow> rows)
    {
        var legs = escrow.Legs;
        var rowsOfAmount = rows.Where(r => r.IsOfAmount).ToList();
        var allDone = rowsOfAmount.All(r => r.Status == LedgerFields.StatusDone);
        var allDecided = legs.Count > 0 && legs.Values.All(s => s != LedgerEscrowFields.Held);
        string? target = null;
        if (allDecided && allDone)
        {
            // The crop leg names the outcome: a trucker paid first (F1) and the crop money back to the buyer is
            // a refund, not a completed sale, so the order is never shown settled for it.
            target = escrow.LegState(LedgerEscrowFields.LegCrop) switch
            {
                LedgerEscrowFields.Refunded => "refunded",
                LedgerEscrowFields.Released => "completed",
                _ => null
            };
        }

        if (target == null && rowsOfAmount.Any(r => LedgerRefs.IsFarmerPayout(r.Ref) && r.Status == LedgerFields.StatusDone))
        {
            target = "released";
        }

        return target == null || target == escrow.Status ? null : target;
    }

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(decimal value) => Round(value).ToString("0.00", CultureInfo.InvariantCulture);
}

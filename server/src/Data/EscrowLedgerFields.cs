using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-20: names of the escrow ledger (escrow-protocol.md 2.2.0, "Additions in 2.2.0"). The table
/// <c>{prefix}-escrow-ledger</c> holds one row per money movement of an order escrow (partition key
/// <c>escrowId</c>, sort key <c>ref</c>); the sparse <c>LedgerOpenIndex</c> (<c>openStatus</c>,
/// <c>dueAt</c>) holds the rows that are not <c>done</c>. Shared by the repository, the service and the
/// tests so nobody spells an attribute twice.
/// </summary>
internal static class LedgerFields
{
    public const string IndexName = "LedgerOpenIndex";

    public const string EscrowId = "escrowId";
    public const string Ref = "ref";
    public const string Kind = "kind";
    public const string Legs = "legs";
    public const string PayeeRole = "payeeRole";
    public const string PayeeId = "payeeId";
    public const string Amount = "amount";
    public const string Status = "status";
    public const string GatewayRef = "gatewayRef";
    public const string HoldRef = "holdRef";
    public const string TelebirrRef = "telebirrRef";
    public const string Attempts = "attempts";
    public const string LastCode = "lastCode";
    public const string CallingSince = "callingSince";
    public const string ManualReason = "manualReason";
    public const string Reason = "reason";
    public const string Actor = "actor";
    public const string PaidBy = "paidBy";
    public const string OrderId = "orderId";
    public const string CreatedAt = "createdAt";
    public const string UpdatedAt = "updatedAt";
    public const string SentAt = "sentAt";
    public const string DoneAt = "doneAt";
    public const string OpenStatus = "openStatus";
    public const string DueAt = "dueAt";

    /// <summary>FUP-06: the <c>dueAt</c> of the manual row whose overdue the ledger sweep announced (no version moves).</summary>
    public const string ManualOverdueLoggedFor = "manualOverdueLoggedFor";

    /// <summary>The row's own compare-and-set counter: every row write adds 1 and is conditioned on the value read.</summary>
    public const string Version = "version";

    public const string StatusPending = "pending";
    public const string StatusSent = "sent";
    public const string StatusDone = "done";
    public const string StatusFailed = "failed";
    public const string StatusManual = "manual";

    public const string KindRelease = "release";
    public const string KindRefund = "refund";
    public const string KindVerdict = "verdict";
    public const string KindShift = "shift";
    public const string KindTopup = "topup";

    public const string PayeeFarmer = "farmer";
    public const string PayeeTrucker = "trucker";
    public const string PayeeBuyer = "buyer";

    public const string PaidByGateway = "gateway";

    public const string ManualPayoutNotSupported = "PAYOUT_NOT_SUPPORTED";
    public const string ManualGatewayRefMissing = "GATEWAY_REF_MISSING";
    public const string ManualOutcomeUnknown = "GATEWAY_OUTCOME_UNKNOWN";
    public const string ManualRefused3 = "GATEWAY_REFUSED_3";
    public const string ManualPayeeNotVerified = "PAYEE_NOT_VERIFIED";

    /// <summary>Gateway calls a row may make before a refusal sends it to <c>manual</c> (P.5 step 4).</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// How long a row that may have reached the gateway (<c>sent</c>, or a <c>callingSince</c> stamp)
    /// waits before the sweep checks its status, and how long a <c>failed</c> row waits for its retry.
    /// Far above the longest caller (escrow Lambda 30 s, escrow-cron 5 min), so a stamp this old has no
    /// living owner.
    /// </summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(15);

    /// <summary>A row open longer than this logs <c>LEDGER_ROW_OVERDUE</c> on every sweep.</summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(24);

    public static bool IsOpen(string? status) => status is StatusPending or StatusSent or StatusFailed or StatusManual;
}

/// <summary>ESC-20: the escrow row attributes the ledger adds (escrow-protocol.md 2.2.0, P.2).</summary>
internal static class LedgerEscrowFields
{
    public const string SettlementModel = "settlementModel";
    public const string ModelLedger = "ledger";
    public const string LegStatus = "legStatus";
    public const string RefundedTotal = "refundedTotal";
    public const string ReleasedTotal = "releasedTotal";
    public const string PreviousTruckerIds = "previousTruckerIds";

    /// <summary>ESC-20 (P.8): the admin's split, a map of <c>farmerAmount</c>, <c>truckerAmount</c> and <c>buyerRefund</c>.</summary>
    public const string VerdictAmounts = "verdictAmounts";

    public const string LegCrop = "crop";
    public const string LegTransport = "transport";
    public const string LegCommission = "commission";

    public const string Held = "held";
    public const string Released = "released";
    public const string Refunded = "refunded";
    public const string Kept = "kept";

    public static readonly string[] AllLegs = { LegCrop, LegTransport, LegCommission };

    public static bool IsLedger(Dictionary<string, object> escrow)
        => escrow.TryGetValue(SettlementModel, out var model)
           && string.Equals(model?.ToString(), ModelLedger, StringComparison.Ordinal);
}

/// <summary>ESC-20: the ledger refs (escrow-protocol.md 2.2.0, P.4) and the reference sent to the gateway (P.5).</summary>
internal static class LedgerRefs
{
    public const string ReleaseFarmer = "release#farmer";
    public const string RefundAll = "refund#all";
    public const string VerdictFarmer = "verdict#farmer";
    public const string VerdictTrucker = "verdict#trucker";
    public const string VerdictBuyer = "verdict#buyer";

    public const string ReleaseTruckerPrefix = "release#trucker#";
    public const string RefundDuplicatePrefix = "refund#duplicate#";
    public const string ShiftPrefix = "shift#";

    public static string ReleaseTrucker(string truckerId) => ReleaseTruckerPrefix + truckerId;

    public static string RefundDuplicate(string paymentOrderId) => RefundDuplicatePrefix + paymentOrderId;

    /// <summary>
    /// I3: the rows that move part of the escrow's <c>amount</c>. A duplicate payment is outside it,
    /// and a shift moves money between two legs inside it.
    /// </summary>
    public static bool IsRowOfAmount(string reference)
        => !reference.StartsWith(RefundDuplicatePrefix, StringComparison.Ordinal)
           && !reference.StartsWith(ShiftPrefix, StringComparison.Ordinal);

    /// <summary>A farmer payout: the row whose <c>done</c> moves the escrow to <c>released</c>.</summary>
    public static bool IsFarmerPayout(string reference) => reference is ReleaseFarmer or VerdictFarmer;

    /// <summary>
    /// P.5: the reference sent to the gateway, fixed when the row is written. Live Telebirr takes a
    /// merch_order_id of ASCII letters and digits only (FUP-36), and <c>{escrowId}:{ref}</c> runs to about
    /// 100 characters, so the reference is a stable mapping of that string: <c>WCL</c> and the first 27
    /// hex characters of its SHA-256, 30 characters like the order checkout's merch_order_id. Every retry
    /// sends the row's stored value.
    /// </summary>
    public static string GatewayRef(string escrowId, string reference)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{escrowId}:{reference}")));
        return "WCL" + hash[..27];
    }

    /// <summary>
    /// The escrow-audit key offset of one row's transition, in ticks (under 10 ms), so the audit rows that
    /// several refs and actions write at the same instant never share a key (escrowId + timestamp).
    /// </summary>
    public static long AuditTickOffset(string reference, string action)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{reference}|{action}"));
        return (BitConverter.ToUInt32(hash, 0) % 99991) + 1;
    }

    /// <summary>An ISO time plus the offset of <paramref name="reference"/> and <paramref name="action"/>: the audit key of one transition.</summary>
    public static string AuditTimestamp(string isoTime, string reference, string action)
    {
        var parsed = DateTime.Parse(isoTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return parsed.AddTicks(AuditTickOffset(reference, action)).ToString("o", CultureInfo.InvariantCulture);
    }
}

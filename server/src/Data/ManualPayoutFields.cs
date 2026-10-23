namespace WeredaConnect.Server.Data;

/// <summary>
/// Attribute, leg and operation names of the manual payout queue (ESC-02). The queue
/// lives on the escrow row itself: a release or refund the live gateway cannot make
/// marks the row <c>manualPayout = open</c> (the partition key of the sparse
/// <c>ManualPayoutIndex</c>) with one status triple per payee leg. Shared by the
/// repository, the service and the tests so nobody spells an attribute twice.
/// </summary>
internal static class ManualPayoutFields
{
    public const string IndexName = "ManualPayoutIndex";

    /// <summary>"open" while waiting; REMOVEd when the last leg is paid.</summary>
    public const string Marker = "manualPayout";
    public const string MarkerOpen = "open";

    /// <summary>ISO time the escrow entered the queue; sort key of the index (oldest first).</summary>
    public const string QueuedAt = "manualPayoutQueuedAt";

    /// <summary>"release" or "refund"; always equals the held <c>settlementLock</c>.</summary>
    public const string Op = "manualPayoutOp";
    public const string Reason = "manualPayoutReason";
    public const string FromStatus = "manualPayoutFromStatus";

    /// <summary>
    /// ESC-05: random id of the recovery write that parked a stale settlement, so a recovery
    /// whose answer was lost can tell its own write from a concurrent one.
    /// </summary>
    public const string ParkId = "manualPayoutParkId";

    public const string OpRelease = "release";
    public const string OpRefund = "refund";

    public const string LegFarmer = "farmer";
    public const string LegTrucker = "trucker";
    public const string LegBuyer = "buyer";

    public const string LegPending = "pending";
    public const string LegPaid = "paid";

    /// <summary>The <c>paidBy</c> value of a leg the gateway itself paid before the queueing.</summary>
    public const string PaidByGateway = "gateway";

    /// <summary>Why a settlement was queued instead of completed.</summary>
    public const string ReasonPayoutNotSupported = "PAYOUT_NOT_SUPPORTED";
    public const string ReasonGatewayRefMissing = "GATEWAY_REF_MISSING";

    /// <summary>
    /// ESC-05: the gateway may have paid (a timeout, a lost or unreadable answer, a call that
    /// threw, or a settlement lock older than 15 minutes). The admin checks the portal first.
    /// </summary>
    public const string ReasonGatewayOutcomeUnknown = "GATEWAY_OUTCOME_UNKNOWN";

    /// <summary>
    /// Prefix of the failure code (audit metadata) when the queue write itself failed:
    /// <c>MANUAL_QUEUE_FAILED:&lt;reason&gt;</c>. The money alarm reads it (ESC-13).
    /// </summary>
    public const string QueueFailedCodePrefix = "MANUAL_QUEUE_FAILED:";

    public static readonly string[] AllLegs = { LegFarmer, LegTrucker, LegBuyer };

    public static bool IsLeg(string? leg) => leg is LegFarmer or LegTrucker or LegBuyer;

    /// <summary>Legs that belong to an operation: release pays farmer (+ trucker), refund pays the buyer.</summary>
    public static bool LegBelongsToOp(string leg, string op) => op switch
    {
        OpRelease => leg is LegFarmer or LegTrucker,
        OpRefund => leg is LegBuyer,
        _ => false
    };

    public static string StatusAttr(string leg) => $"manual{Capitalise(leg)}Status";
    public static string RefAttr(string leg) => $"manual{Capitalise(leg)}Ref";
    public static string PaidByAttr(string leg) => $"manual{Capitalise(leg)}PaidBy";
    public static string PaidAtAttr(string leg) => $"manual{Capitalise(leg)}PaidAt";

    private static string Capitalise(string leg)
        => leg.Length == 0 ? leg : char.ToUpperInvariant(leg[0]) + leg[1..];
}

/// <summary>
/// The settlement lock on the escrow row. <c>settlementLock</c> holds the operation
/// (release or refund); ESC-05 adds <c>settlementLockAt</c>, the ISO time it was taken, and
/// <c>settlementLockId</c>, a random id that tells one lock from another (two locks can carry
/// the same time). From the moment the lock is taken the gateway may have been called, so a
/// lock older than <see cref="StaleAfter"/> is an unknown outcome that the recovery step parks.
/// </summary>
internal static class SettlementLockFields
{
    public const string Lock = "settlementLock";
    public const string LockAt = "settlementLockAt";
    public const string LockId = "settlementLockId";

    /// <summary>
    /// Far above the longest settlement run (escrow Lambda 30 s, escrow-cron 5 min), so a
    /// lock this old has no living owner.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The trucker leg of a release that the release itself could not pay (ESC-04). The escrow
/// stays <c>released</c> (the farmer is paid) with <c>truckerPayoutPending = true</c> until the
/// leg is paid: by the hourly retry in escrow-cron, under its own lock (the row keeps the
/// release's <c>settlementLock</c>, so that one cannot be reused), or through the manual payout
/// queue. Only then does the escrow become <c>completed</c>.
/// </summary>
internal static class TruckerPayoutFields
{
    public const string Pending = "truckerPayoutPending";

    /// <summary>
    /// ISO time the leg went pending; the overdue alarm counts from it (fallback <c>releasedAt</c>).
    /// Written since ESC-04 with every pending flag, so a pending leg without it is legacy (before
    /// ESC-04, when the old runbook said to pay it by hand) and is parked, never retried.
    /// </summary>
    public const string PendingSince = "truckerPayoutPendingSince";

    /// <summary>Why the payout gate blocks the trucker, one of the Block* values; audited once per value.</summary>
    public const string BlockReason = "truckerPayoutBlockReason";

    /// <summary>ISO time the block reason was set: the deterministic key of its audit row, re-ensured each run.</summary>
    public const string BlockReasonAt = "truckerPayoutBlockReasonAt";

    /// <summary>Random id and ISO time of the retry's lock; from the lock on the gateway may have been called.</summary>
    public const string LockId = "truckerPayoutLockId";
    public const string LockAt = "truckerPayoutLockAt";

    public const string Ref = "truckerTelebirrRef";
    public const string PaidAt = "truckerPaidAt";

    public const string BlockNoTrucker = "no_trucker";
    public const string BlockProfileMissing = "profile_missing";
    public const string BlockPhoneUnverified = "phone_unverified";
    public const string BlockFaydaRequired = "fayda_required";

    /// <summary>The escrow has no truckerId and the order's trucker is not the delivery's.</summary>
    public const string BlockTruckerMismatch = "trucker_mismatch";
}

/// <summary>
/// ESC-10: why a settlement of an <c>escrowed</c> or <c>dispute_resolved</c> escrow does not go ahead
/// on the hourly sweeps: the farmer payout gate (<c>release:payout:&lt;reason&gt;</c>) or the sweep's
/// order and delivery check (<c>release:trade:&lt;reason&gt;</c>, <c>refund:trade:&lt;reason&gt;</c>).
/// Stored by compare-and-set with the time first seen, which keys the reason's one audit row. The
/// <c>released</c> and <c>refunded</c> status writes remove both fields, and a gate that passes
/// removes a reason of its own gate.
/// </summary>
internal static class SettlementBlockFields
{
    public const string Reason = "settlementBlockReason";
    public const string ReasonAt = "settlementBlockReasonAt";

    public const string GatePayout = "payout";
    public const string GateTrade = "trade";

    public static string Value(string op, string gate, string reason) => $"{op}:{gate}:{reason}";

    public static string Prefix(string op, string gate) => $"{op}:{gate}:";
}

/// <summary>
/// FRD-03: an escrow frozen for fraud review (<c>status = held</c>, fraud-detection-architecture.md
/// section 18). Only a settlement sweep writes it (<c>heldBy = system</c>), from <c>escrowed</c> or
/// <c>dispute_resolved</c>, and only an admin in the admin group releases or refunds it. Every field
/// but the status is admin-only.
/// </summary>
internal static class EscrowHoldFields
{
    public const string Status = "held";
    public const string HoldReason = "holdReason";
    public const string HeldAt = "heldAt";
    public const string HeldBy = "heldBy";
    public const string HeldFromStatus = "heldFromStatus";

    /// <summary>What the sweep was about to do: <see cref="ForRelease"/> or <see cref="ForRefund"/>.</summary>
    public const string HeldFor = "heldFor";

    /// <summary>A party's user row says suspended (FRD-03).</summary>
    public const string ReasonAccountSuspended = "account_suspended";

    /// <summary>
    /// A party's user row says suspended with <c>suspendReason = fraud_confirmed</c>, which only the Fraud
    /// Review reject action writes (FRD-05). Takes precedence over <see cref="ReasonAccountSuspended"/>.
    /// </summary>
    public const string ReasonFraudConfirmed = "fraud_confirmed";

    /// <summary>A party at risk tier CRITICAL, a truncated score read included (FRD-04).</summary>
    public const string ReasonRiskCritical = "risk_critical";

    public const string ForRelease = "release";
    public const string ForRefund = "refund";
}

/// <summary>
/// One payee leg written when an escrow enters the manual payout queue. A leg the gateway
/// already paid (the farmer, when only the trucker leg failed) is seeded <c>paid</c> with
/// its real reference and <c>PaidBy = gateway</c>.
/// </summary>
public sealed record ManualPayoutLegSeed(string Leg, string Status, string? Ref, string? PaidBy, string? PaidAt);

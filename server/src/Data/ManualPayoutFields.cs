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
/// One payee leg written when an escrow enters the manual payout queue. A leg the gateway
/// already paid (the farmer, when only the trucker leg failed) is seeded <c>paid</c> with
/// its real reference and <c>PaidBy = gateway</c>.
/// </summary>
public sealed record ManualPayoutLegSeed(string Leg, string Status, string? Ref, string? PaidBy, string? PaidAt);

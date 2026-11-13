using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-287 (delivery-verification-api.md 2.8.0 P.2 "The admin side", order-lifecycle.md 2.16.0): the admin's
/// view of a pickup judgement and the rules of its re-open, as pure functions of the rows read.
/// <c>AdminFunction</c> does the reads and the one transaction; what it decides is here.
/// <para>
/// Only a judgement that left the order <c>accepted</c> with its money held is re-opened:
/// <c>both_present_no_scan</c> and <c>unchecked_check_in</c>, the two outcomes that send the order to the
/// admin queue and move nothing. The no-show outcomes are refused. A trucker no-show cancelled the
/// delivery and released the trip (the order waits in <c>awaiting_transport</c> for the buyer's choice), and
/// a farmer or self-pickup buyer no-show cancelled the order with its refund armed in the same transaction,
/// which the hourly sweep sends. Undoing them would bring a cancelled delivery, a released booking or an
/// armed refund back, which this action never does. Their wrong <c>NO_SHOW</c> event is excused on the
/// party's reputation (ESC-32).
/// </para>
/// </summary>
public static class PickupReopen
{
    /// <summary>The <c>{action}</c> of <c>POST /admin/orders/{orderId}/{action}</c>.</summary>
    public const string Action = "reopen-pickup";

    /// <summary>
    /// Server-only order map: the last re-open (<see cref="StampAt"/>, <see cref="StampExit"/>,
    /// <see cref="StampFromJudgement"/>, <see cref="StampFromVersion"/> and, for <c>pending</c>,
    /// <see cref="StampDeadlineAt"/>). No admin id and no reason: those are in the audit trail only.
    /// </summary>
    public const string Field = "pickupReopen";

    public const string StampAt = "at";
    public const string StampExit = "exit";
    public const string StampFromJudgement = "fromJudgement";
    public const string StampFromVersion = "fromVersion";
    public const string StampDeadlineAt = "deadlineAt";

    /// <summary>What the re-open does.</summary>
    public static class Exit
    {
        /// <summary>Back to <c>pickupJudgement: pending</c> with a new <c>pickupDeadlineAt</c>; judged again then.</summary>
        public const string Pending = "pending";

        /// <summary>The handoff happened: the delivery <c>pickedUp</c>, the order <c>pickup_confirmed</c>, judged <c>scanned</c>.</summary>
        public const string HandoffConfirmed = "handoff_confirmed";
    }

    /// <summary>The transaction-audit action of a re-open (the admin Audit Log and the transaction timeline).</summary>
    public const string AuditAction = "admin_pickup_reopened";

    /// <summary>The log token of an applied re-open.</summary>
    public const string LogToken = "PICKUP_REOPENED";

    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;
    public const int MinDeadlineHours = 1;
    public const int MaxDeadlineHours = 168;

    /// <summary>400: a field of the request is missing or out of range (<c>field</c> names it).</summary>
    public const string InvalidCode = "PICKUP_REOPEN_INVALID";

    /// <summary>409: the order's judgement, or the exit asked for, cannot be re-opened (the message says why).</summary>
    public const string NotAllowedCode = "PICKUP_REOPEN_NOT_ALLOWED";

    /// <summary>409: the order, its delivery or its escrow changed since it was read; nothing was written.</summary>
    public const string ChangedCode = "PICKUP_REOPEN_CHANGED";

    /// <summary>409: the order's escrow no longer holds the money as the judgement left it; nothing was written.</summary>
    public const string MoneyNotHeldCode = "PICKUP_REOPEN_MONEY_NOT_HELD";

    /// <summary>
    /// The admin queue reasons a judgement writes. The re-open answers such an entry and removes it; another
    /// reason (the 21 day hold cap) is about the money and stays.
    /// </summary>
    public static readonly IReadOnlySet<string> JudgementQueueReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        PickupJudgement.ReasonBothPresent, PickupJudgement.ReasonUnchecked, PickupJudgement.ReasonLateEvidence
    };

    /// <summary>What the re-open does with the order's admin queue entry (<see cref="QueuePlanOf"/>).</summary>
    public enum QueuePlan
    {
        /// <summary>No entry at read: none may land before the write (an absence condition).</summary>
        NotQueued,

        /// <summary>The judgement's entry: removed, while its <c>adminQueuedAt</c> is the one read.</summary>
        Remove,

        /// <summary>
        /// The judgement's entry also carries the order escrow's hold cap (the escrow cron stamped
        /// <c>holdCapReachedAt</c> on the escrow instead of writing an entry of its own): kept, as <c>hold_cap</c>.
        /// </summary>
        ToHoldCap,

        /// <summary>An entry for another reason (the hold cap), or one another escrow owns: left as it is.</summary>
        Keep
    }

    /// <summary>
    /// The plan for the order's admin queue entry. An escrow at its hold cap (<c>holdCapReachedAt</c>) has left the
    /// sweep windows and waits on the queue entry that names it (<c>adminQueueEscrowId</c>, else the order escrow),
    /// even when that entry was written for the judgement: removing it would hold the money for good with nothing
    /// to show it. <paramref name="escrow"/> is the order's escrow as read, or null without one.
    /// </summary>
    public static QueuePlan QueuePlanOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? escrow)
    {
        if (Str(order, AdminQueueFields.Marker).Length == 0)
        {
            return QueuePlan.NotQueued;
        }

        if (!JudgementQueueReasons.Contains(Str(order, AdminQueueFields.Reason)) || Str(order, AdminQueueFields.QueuedAt).Length == 0)
        {
            return QueuePlan.Keep;
        }

        if (escrow == null || !IsHoldCapped(escrow))
        {
            return QueuePlan.Remove;
        }

        var entryEscrow = Str(order, AdminQueueFields.EscrowId) is { Length: > 0 } named ? named : $"escrow-order-{Str(order, "orderId")}";
        return entryEscrow == Str(escrow, "escrowId") ? QueuePlan.ToHoldCap : QueuePlan.Keep;
    }

    /// <summary>Whether the escrow reached its hold cap with its order in the admin queue (ESC-06).</summary>
    public static bool IsHoldCapped(IReadOnlyDictionary<string, object> escrow)
        => Str(escrow, AdminQueueFields.HoldCapReachedAt).Length > 0;

    /// <summary>The judged outcomes that left the order <c>accepted</c> with nothing moved.</summary>
    public static bool IsReopenableOutcome(string judgement)
        => judgement is PickupJudgement.Outcome.BothPresentNoScan or PickupJudgement.Outcome.UncheckedCheckIn;

    /// <summary>Why the order's pickup cannot be re-opened (the 409's message), or null when it can.</summary>
    public static string? RefusalOf(IReadOnlyDictionary<string, object> order)
    {
        var judgement = Str(order, PickupJudgement.Field);
        if (!PickupJudgement.IsJudged(order) || judgement is "" or PickupJudgement.Outcome.Pending)
        {
            return "The pickup of this order is not judged, so there is nothing to re-open.";
        }

        switch (judgement)
        {
            case PickupJudgement.Outcome.Scanned:
                return "The pickup was judged picked up, so there is nothing to re-open.";
            case PickupJudgement.Outcome.TruckerNoShow:
                return "A trucker no-show cancelled the delivery and released the trip, and the buyer chooses the transport again: "
                       + "it cannot be re-opened. A wrong no-show is excused on the trucker's reputation.";
            case PickupJudgement.Outcome.FarmerNoShow or PickupJudgement.Outcome.BuyerNoShow:
                return "A no-show cancelled this order and armed its refund: it cannot be re-opened. "
                       + "A wrong no-show is excused on the party's reputation.";
        }

        if (!IsReopenableOutcome(judgement))
        {
            return "This judgement cannot be re-opened.";
        }

        var status = Str(order, "status");
        return status == "accepted"
            ? null
            : $"The order is {status} now: only an order still accepted after its judgement can be re-opened.";
    }

    /// <summary>
    /// Why the admin cannot confirm the handoff (the 409's message), or null when it can. A self-pickup's handoff
    /// is the buyer's delivery confirmation, which pays the farmer: support settles it with a dispute verdict. The
    /// order needs its active delivery (<paramref name="delivery"/>, as read), still <c>pending</c>.
    /// </summary>
    public static string? HandoffRefusalOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? delivery)
    {
        if (PickupJudgement.IsSelfPickup(order))
        {
            return "A self-pickup handoff is the buyer's delivery confirmation, which pays the farmer: settle it with a dispute verdict instead.";
        }

        var deliveryId = Str(order, "activeDeliveryId");
        if (deliveryId.Length == 0 || delivery == null)
        {
            return "The order has no active delivery to mark picked up.";
        }

        if (Str(delivery, "deliveryId") != deliveryId || Str(delivery, "orderId") != Str(order, "orderId"))
        {
            return "The order's active delivery is not this order's.";
        }

        var status = Str(delivery, "status");
        return status == "pending"
            ? null
            : $"The order's delivery is {status}: only a pending delivery can be marked picked up.";
    }

    /// <summary>The exits the action takes for the order as read: none when its judgement cannot be re-opened.</summary>
    public static List<string> ExitsOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? delivery)
    {
        var exits = new List<string>();
        if (RefusalOf(order) != null)
        {
            return exits;
        }

        exits.Add(Exit.Pending);
        if (HandoffRefusalOf(order, delivery) == null)
        {
            exits.Add(Exit.HandoffConfirmed);
        }

        return exits;
    }

    /// <summary>
    /// Whether <paramref name="escrow"/> still holds the order's money as the judgement left it: <c>escrowed</c>, no
    /// settlement under way (<c>settlementLock</c>), no refund armed (<c>refundReason</c>), no delivery confirmed
    /// and never in the manual payout queue, the escrow sweep's own test for a plain hold. An escrow <c>held</c>
    /// for fraud review does not: its review comes first. The write checks the same in its transaction
    /// (<c>OrderRepository.EscrowHoldsMoneyCheck</c>).
    /// </summary>
    public static bool HoldsMoney(IReadOnlyDictionary<string, object> escrow)
        => Str(escrow, "status") == "escrowed"
           && !escrow.ContainsKey(SettlementLockFields.Lock)
           && !escrow.ContainsKey("refundReason")
           && !escrow.ContainsKey("deliveryConfirmedAt")
           && !escrow.ContainsKey(ManualPayoutFields.Marker);

    /// <summary>The <see cref="Field"/> stamp a re-open writes.</summary>
    public static Dictionary<string, object> Stamp(string exit, string fromJudgement, int fromVersion, string at, string? deadlineAt)
    {
        var stamp = new Dictionary<string, object>
        {
            { StampAt, at },
            { StampExit, exit },
            { StampFromJudgement, fromJudgement },
            { StampFromVersion, fromVersion }
        };
        if (deadlineAt != null)
        {
            stamp[StampDeadlineAt] = deadlineAt;
        }

        return stamp;
    }

    /// <summary>Whether the order already carries this re-open: the same exit, from the version the admin read (a replay).</summary>
    public static bool IsReplay(IReadOnlyDictionary<string, object> order, int expectedVersion, string exit)
        => StampOf(order) is { } stamp
           && Str(stamp, StampExit) == exit
           && int.TryParse(Str(stamp, StampFromVersion), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
           && from == expectedVersion;

    /// <summary>The last re-open of the order, or null.</summary>
    public static AdminPickupReopenDto? LastReopenOf(IReadOnlyDictionary<string, object> order)
        => StampOf(order) is { } stamp
            ? new AdminPickupReopenDto
            {
                At = NullIfEmpty(Str(stamp, StampAt)),
                Exit = NullIfEmpty(Str(stamp, StampExit)),
                FromJudgement = NullIfEmpty(Str(stamp, StampFromJudgement)),
                DeadlineAt = NullIfEmpty(Str(stamp, StampDeadlineAt))
            }
            : null;

    /// <summary>
    /// The pickup judgement as the admin reads it: with the stamped farm point, and per check-in the fix, its
    /// accuracy, the distance to the point, out of range and the sender, which the parties' view leaves out.
    /// <paramref name="delivery"/> is the order's active delivery as read (it decides whether the handoff can be
    /// confirmed). Null for an order never stamped judgeable. Admin routes only.
    /// </summary>
    public static AdminPickupEvidenceDto? EvidenceOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? delivery)
    {
        if (!PickupJudgement.IsStamped(order) && StampOf(order) == null)
        {
            return null;
        }

        Dictionary<string, AdminCheckInEvidenceDto>? checkIns = null;
        foreach (var kind in new[] { PickupJudgement.Kind.ArrivedPickup, PickupJudgement.Kind.CropsReady })
        {
            if (PickupJudgement.EntryOf(order, kind) is not { } entry || Str(entry, PickupJudgement.EntryReceivedAt).Length == 0)
            {
                continue;
            }

            checkIns ??= new Dictionary<string, AdminCheckInEvidenceDto>(StringComparer.Ordinal);
            checkIns[kind] = new AdminCheckInEvidenceDto
            {
                ReceivedAt = Str(entry, PickupJudgement.EntryReceivedAt),
                CapturedAt = NullIfEmpty(Str(entry, PickupJudgement.EntryCapturedAt)),
                By = NullIfEmpty(Str(entry, PickupJudgement.EntryBy)),
                Latitude = Number(entry, PickupJudgement.EntryLatitude),
                Longitude = Number(entry, PickupJudgement.EntryLongitude),
                AccuracyM = Number(entry, PickupJudgement.EntryAccuracyM),
                DistanceKm = Number(entry, PickupJudgement.EntryDistanceKm),
                DistanceChecked = PickupJudgement.Bool(entry, PickupJudgement.EntryDistanceChecked),
                Late = PickupJudgement.Bool(entry, PickupJudgement.EntryLate),
                OutOfRange = PickupJudgement.Bool(entry, PickupJudgement.EntryOutOfRange)
            };
        }

        return new AdminPickupEvidenceDto
        {
            Judgement = NullIfEmpty(Str(order, PickupJudgement.Field)),
            JudgedAt = NullIfEmpty(Str(order, PickupJudgement.JudgedAtField)),
            DeadlineAt = NullIfEmpty(Str(order, PickupDeadline.Field)),
            SelfPickup = PickupJudgement.IsSelfPickup(order),
            PointLatitude = Number(order, PickupJudgement.PickupLatitudeField),
            PointLongitude = Number(order, PickupJudgement.PickupLongitudeField),
            CheckIns = checkIns,
            ReopenExits = ExitsOf(order, delivery),
            LastReopen = LastReopenOf(order)
        };
    }

    private static IReadOnlyDictionary<string, object>? StampOf(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue(Field, out var raw) && raw is IReadOnlyDictionary<string, object> stamp ? stamp : null;

    private static string Str(IReadOnlyDictionary<string, object> item, string key) => PickupJudgement.Str(item, key);

    private static double? Number(IReadOnlyDictionary<string, object> item, string key)
        => PickupJudgement.TryNumber(item, key, out var number) ? number : null;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

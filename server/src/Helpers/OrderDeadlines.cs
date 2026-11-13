using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-16 (spec 4.5): the next deadline of an order, derived only from its status and its own stamps,
/// as a pure function of the row. Every order write keeps it on the row as <see cref="AtField"/>,
/// <see cref="KindField"/> and <see cref="ShardField"/> (<c>OrderRepository</c>), so the sparse
/// <see cref="IndexName"/> holds exactly the orders with something pending, and the order cron reads the
/// due ones instead of walking the status partitions.
/// <para>
/// The kinds the index carries are the ones the <c>commitment_deadlines</c> sweep owns: T3 (the
/// farmer's answer), T4 (the trucker's answer), T5 (the buyer's transport choice), the reminder before
/// each, and T7 (the pickup-overdue marker). The rules mirror the ESC-15 walk they replace: only a
/// stamped <c>farmerDeadlineAt</c> expires the farmer (a funded row from before the stamps is left to
/// the escrow sweep), a reminder is due <see cref="OrderCommitment.ReminderLead"/> before its deadline
/// and only for a window of at least <see cref="OrderCommitment.MinReminderWindow"/>, and each marker
/// is stamped once. T1 and T2 stay with the hourly <c>auto_cancel_expired</c> action, whose
/// <c>createdAt</c> key condition already reads only the rows past due; T10 and T12 stay with the escrow
/// cron (they follow the escrow); T6 and T11 are not built yet (ESC-19 and P3). ESC-36 adds T8 and T9 on
/// a <c>pickup_confirmed</c> or <c>in_transit</c> order stamped <c>dropoffJudgement: pending</c>
/// (<see cref="Helpers.DropoffJudgement.Due"/>).
/// </para>
/// <para>
/// The index has one partition (<see cref="Shard"/>): the sweep's question, "everything due before
/// now, oldest first", is then one key condition with no lookback. Partitioning by the deadline's day
/// (the spec's first idea) would make the sweep guess how many past days to read, and a row that stays
/// due across midnight (a lost race retried, an unreadable trip) would fall out of the window. Order
/// writes are far below one partition's limits; should they ever not be, the shard becomes a hash of
/// the order id and the sweep reads each shard.
/// </para>
/// <para>
/// FUP-170: one kind is not the sweep's. <see cref="Kind.PickupWaiting"/> (an <c>accepted</c> order past
/// its pickup deadline whose notice went out) is kept in its own partition, <see cref="WatchShard"/>,
/// which the sweep never reads, so the admin's Stuck Orders can list the order with a Query until its
/// pickup or its cancel, and the sweep's oldest-first read is never held up by orders that only wait.
/// The partition of an entry follows from its kind (<see cref="ShardOf"/>). ESC-34's
/// <see cref="Kind.PickupJudgement"/> is swept: a judgeable order is never parked in the watch partition.
/// </para>
/// </summary>
public static class OrderDeadlines
{
    /// <summary>When the sweep must look at the order next (ISO 8601 UTC); the sort key of the index.</summary>
    public const string AtField = "nextDeadlineAt";

    /// <summary>What is due then (<see cref="Kind"/>).</summary>
    public const string KindField = "nextDeadlineKind";

    /// <summary>The partition key of the index; present exactly when <see cref="AtField"/> is.</summary>
    public const string ShardField = "deadlineShard";

    public const string IndexName = "DeadlineIndex";

    /// <summary>The partition the sweep reads (see the class remarks).</summary>
    public const string Shard = "0";

    /// <summary>FUP-170: the partition of the entries the sweep never acts on (<see cref="Kind.PickupWaiting"/>); only the admin lists it.</summary>
    public const string WatchShard = "watch";

    /// <summary>The three attributes, server-only, written and removed together.</summary>
    public static readonly string[] Fields = { AtField, KindField, ShardField };

    /// <summary>Values of <see cref="KindField"/>.</summary>
    public static class Kind
    {
        /// <summary>T3 reminder: <c>farmerReminderAt</c> is stamped.</summary>
        public const string FarmerReminder = "farmer_reminder";

        /// <summary>T3: <c>farmerDeadlineAt</c>; the order is cancelled (<c>farmer_response_expired</c>).</summary>
        public const string FarmerResponse = "farmer_response";

        /// <summary>T4 reminder: <c>truckerReminderAt</c> is stamped.</summary>
        public const string TruckerReminder = "trucker_reminder";

        /// <summary>T4: <c>truckerDeadlineAt</c>; the transport fails (<c>trucker_expired</c>).</summary>
        public const string TruckerResponse = "trucker_response";

        /// <summary>T5 reminder: <c>buyerChoiceReminderAt</c> is stamped.</summary>
        public const string BuyerChoiceReminder = "buyer_choice_reminder";

        /// <summary>T5: <c>buyerChoiceDeadlineAt</c>; the order is cancelled (<c>transport_not_arranged</c>).</summary>
        public const string BuyerChoice = "buyer_choice";

        /// <summary>T7: <c>pickupDeadlineAt</c>; <c>pickupOverdueNotifiedAt</c> is stamped.</summary>
        public const string PickupOverdue = "pickup_overdue";

        /// <summary>
        /// ESC-34, T7 on an order stamped <c>pickupJudgement: pending</c>: <c>pickupDeadlineAt</c> plus
        /// <see cref="Helpers.PickupJudgement.SettleMargin"/>; the pickup is judged by the check-ins. Swept
        /// (<see cref="Shard"/>) whether or not a notice was stamped, so a judgeable order is never parked
        /// in the watch partition before its judgement.
        /// </summary>
        public const string PickupJudgement = "pickup_judgement";

        /// <summary>
        /// FUP-170: after T7, <c>pickupDeadlineAt</c> with <c>pickupOverdueNotifiedAt</c> stamped: the order
        /// waits for its pickup or its cancel. Listed for the admin, never acted on (<see cref="WatchShard"/>).
        /// ESC-34: also an order still <c>accepted</c> after its judgement (both present, an unmeasured
        /// arrival, a delivery the order lagged), and one whose judgement the flag turned off.
        /// </summary>
        public const string PickupWaiting = "pickup_waiting";

        /// <summary>
        /// ESC-36, T8 on an order stamped <c>dropoffJudgement: pending</c> with no counted drop-off check-in:
        /// <c>dropoffDeadlineAt</c> plus <see cref="Helpers.PickupJudgement.SettleMargin"/>; the delivery is overdue.
        /// </summary>
        public const string DeliveryOverdue = "delivery_overdue";

        /// <summary>
        /// ESC-36, T9 on such an order once the trucker checked in at the drop-off: the check-in's receipt plus
        /// <see cref="OrderTimings.SilentBuyerWindow"/> and the margin; the delivery is confirmed for a silent
        /// buyer or disputed.
        /// </summary>
        public const string SilentBuyer = "silent_buyer";
    }

    /// <summary>Whether <paramref name="key"/> is one of the deadline attributes (never taken from a caller's field map).</summary>
    public static bool IsDeadlineField(string key) => key is AtField or KindField or ShardField;

    /// <summary>FUP-170: the partition an entry of <paramref name="kind"/> is kept in.</summary>
    public static string ShardOf(string kind) => kind == Kind.PickupWaiting ? WatchShard : Shard;

    /// <summary>FUP-170: whether the sweep acts on a deadline of <paramref name="kind"/> (it reads only <see cref="Shard"/>).</summary>
    public static bool IsSwept(string kind) => ShardOf(kind) == Shard;

    /// <summary>The order's next deadline with this container's timings, or null when nothing is pending.</summary>
    public static OrderDeadline? Next(IReadOnlyDictionary<string, object> order) => Next(order, OrderTimings.Current);

    /// <summary>The order's next deadline, or null when nothing is pending.</summary>
    public static OrderDeadline? Next(IReadOnlyDictionary<string, object> order, OrderTimings timings)
    {
        var candidates = new List<OrderDeadline>(2);
        switch (Str(order, "status"))
        {
            case "funded":
                AddFunded(order, timings, candidates);
                break;
            case OrderCommitment.AwaitingTransportStatus:
                AddAwaitingTransport(order, timings, candidates);
                break;
            case "accepted":
                if (OrderCommitment.TryParseUtc(Str(order, PickupDeadline.Field), out var pickupBy))
                {
                    // ESC-34: a judgeable order is judged a moment after its deadline, in the sweep's partition.
                    // Otherwise T7 until the marker is stamped; after it the order waits in the watch partition
                    // (FUP-170). The judgement stamps the marker, so a judged order still accepted waits there.
                    candidates.Add(Helpers.PickupJudgement.IsPending(order)
                        ? new OrderDeadline(pickupBy + Helpers.PickupJudgement.SettleMargin, Kind.PickupJudgement)
                        : new OrderDeadline(pickupBy,
                            Str(order, OrderCommitment.PickupOverdueNotifiedAtField).Length == 0 ? Kind.PickupOverdue : Kind.PickupWaiting));
                }

                break;
            case "pickup_confirmed":
            case "in_transit":
                // ESC-36: a delivery judged at T8, or at T9 once the trucker checked in at the drop-off.
                if (Helpers.DropoffJudgement.IsPending(order) && Helpers.DropoffJudgement.Due(order, timings) is { } dropoff)
                {
                    candidates.Add(dropoff);
                }

                break;
        }

        OrderDeadline? earliest = null;
        foreach (var candidate in candidates)
        {
            if (earliest == null || candidate.At < earliest.Value.At)
            {
                earliest = candidate;
            }
        }

        return earliest;
    }

    /// <summary>The deadline the row carries, or null when it has none (or an unreadable one).</summary>
    public static OrderDeadline? Stored(IReadOnlyDictionary<string, object> order)
        => OrderCommitment.TryParseUtc(Str(order, AtField), out var at) && Str(order, KindField) is { Length: > 0 } kind
            ? new OrderDeadline(at, kind)
            : null;

    /// <summary>Whether the row already carries <paramref name="next"/> (all three attributes, or none of them).</summary>
    public static bool IsStored(IReadOnlyDictionary<string, object> order, OrderDeadline? next)
    {
        if (next is not { } due)
        {
            return Str(order, AtField).Length == 0 && Str(order, KindField).Length == 0 && Str(order, ShardField).Length == 0;
        }

        return Stored(order) is { } stored
               && stored.At == due.At
               && stored.Kind == due.Kind
               && Str(order, ShardField) == due.Shard;
    }

    private static void AddFunded(IReadOnlyDictionary<string, object> order, OrderTimings timings, List<OrderDeadline> candidates)
    {
        // T3: only a stamped deadline expires the farmer's request (see the class remarks).
        if (OrderCommitment.FarmerResponseOf(order) == OrderCommitment.Response.Pending
            && OrderCommitment.TryParseUtc(Str(order, OrderCommitment.FarmerDeadlineAtField), out var farmerDeadline))
        {
            candidates.Add(new OrderDeadline(farmerDeadline, Kind.FarmerResponse));
            // A row from before the feature has no farmerResponse attribute: the cron's conditional
            // stamp could never match it, so no reminder is due.
            if (Str(order, OrderCommitment.FarmerResponseField).Length > 0
                && Str(order, OrderCommitment.FarmerReminderAtField).Length == 0
                && OrderCommitment.TryParseUtc(Str(order, "paymentVerifiedAt"), out var askedAt)
                && farmerDeadline - askedAt >= OrderCommitment.MinReminderWindow)
            {
                candidates.Add(new OrderDeadline(farmerDeadline - timings.ReminderLead, Kind.FarmerReminder));
            }
        }

        // T4: the stamp, or truckerAskedAt plus the window for a row without it.
        if (OrderCommitment.TruckerResponseOf(order) == OrderCommitment.Response.Pending
            && TruckerDeadline(order, timings) is { } truckerDeadline)
        {
            candidates.Add(new OrderDeadline(truckerDeadline, Kind.TruckerResponse));
            if (Str(order, OrderCommitment.TruckerResponseField).Length > 0
                && Str(order, OrderCommitment.TruckerReminderAtField).Length == 0
                && OrderCommitment.TryParseUtc(Str(order, OrderCommitment.TruckerAskedAtField), out var askedAt)
                && truckerDeadline - askedAt >= OrderCommitment.MinReminderWindow)
            {
                candidates.Add(new OrderDeadline(truckerDeadline - timings.ReminderLead, Kind.TruckerReminder));
            }
        }
    }

    private static void AddAwaitingTransport(IReadOnlyDictionary<string, object> order, OrderTimings timings, List<OrderDeadline> candidates)
    {
        // T5: the stamp, or updatedAt plus the window for a row without it (the ESC-15 rule).
        DateTime? deadline = OrderCommitment.TryParseUtc(Str(order, OrderCommitment.BuyerChoiceDeadlineAtField), out var stamped)
            ? stamped
            : OrderCommitment.TryParseUtc(Str(order, "updatedAt"), out var updated) ? updated + timings.BuyerChoiceWindow : null;
        if (deadline is not { } due)
        {
            return;
        }

        candidates.Add(new OrderDeadline(due, Kind.BuyerChoice));
        // The buyer's window is always the full one, so only the lead matters (as in ESC-15).
        if (Str(order, OrderCommitment.BuyerChoiceReminderAtField).Length == 0)
        {
            candidates.Add(new OrderDeadline(due - timings.ReminderLead, Kind.BuyerChoiceReminder));
        }
    }

    private static DateTime? TruckerDeadline(IReadOnlyDictionary<string, object> order, OrderTimings timings)
    {
        if (OrderCommitment.TryParseUtc(Str(order, OrderCommitment.TruckerDeadlineAtField), out var stamped))
        {
            return stamped;
        }

        return OrderCommitment.TryParseUtc(Str(order, OrderCommitment.TruckerAskedAtField), out var asked)
            ? asked + timings.ResponseWindow
            : null;
    }

    private static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}

/// <summary>ESC-16: one pending deadline of an order: when, and what is due then (<see cref="OrderDeadlines.Kind"/>).</summary>
public readonly record struct OrderDeadline(DateTime At, string Kind)
{
    /// <summary><see cref="At"/> as stored (ISO 8601 UTC, sortable as text).</summary>
    public string AtIso => OrderCommitment.Iso(At);

    /// <summary>FUP-170: the index partition it is kept in (<see cref="OrderDeadlines.ShardOf"/>).</summary>
    public string Shard => OrderDeadlines.ShardOf(Kind);
}

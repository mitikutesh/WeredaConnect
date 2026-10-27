using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using static WeredaConnect.Server.Services.OrderCommitment;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-21: which party is told what when an order, delivery or escrow row changes, as pure
/// decisions on the stream record's old and new images (notifications-api.md section 3 is the
/// source of truth). The stream function writes the inbox rows and sends the pushes; nothing here
/// reads a table or the clock, so every row of the event matrix is table-tested.
///
/// Rules that hold for every record:
/// - the party whose own action caused the change is never told (the farmer's decline, the
///   buyer's cancel, the disputer's own dispute is an inbox-only receipt);
/// - one event per party per record: the first rule that names a party wins, so a write that moves
///   several things at once tells each party the strongest one;
/// - params are plain text and never a phone number: names, crop code, quantity, amounts, ISO
///   deadlines and server reason codes (a client's free text never becomes a <c>reason</c>).
/// </summary>
public static class NotificationRules
{
    public const string OrdersTable = "orders";
    public const string DeliveriesTable = "deliveries";
    public const string EscrowsTable = "escrows";

    public const string InsertEvent = "INSERT";
    public const string ModifyEvent = "MODIFY";

    public const string OrderEntity = "order";
    public const string DeliveryEntity = "delivery";
    public const string EscrowEntity = "escrow";

    /// <summary>The buyer's window to report a problem after delivery, before the escrow auto-releases.</summary>
    public static readonly TimeSpan DisputeWindow = TimeSpan.FromHours(48);

    /// <summary>Order statuses in which the buyer's money is held, so a cancel from one refunds it.</summary>
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.Ordinal)
    {
        "funded", AwaitingTransportStatus, "accepted", "pickup_confirmed", "in_transit"
    };

    /// <summary>Order statuses before any money moved.</summary>
    private static readonly HashSet<string> UnpaidStatuses = new(StringComparer.Ordinal)
    {
        "created", "pending_payment"
    };

    /// <summary>The <c>cancelReason</c> the unpaid-order cron writes (as a code, or the legacy sentence below).</summary>
    public const string UnpaidExpiredReason = "unpaid_expired";

    public const string FarmerResponseExpiredReason = "farmer_response_expired";

    /// <summary>Server cancel codes that name the buyer or the farmer as the one who cancelled.</summary>
    private static readonly Dictionary<string, string> CancelReasonActors = new(StringComparer.Ordinal)
    {
        { PickupDeadline.CancelReason, Party.Buyer },
        { "buyer_withdrew", Party.Buyer },
        { "farmer_cancelled", Party.Farmer }
    };

    private static readonly IReadOnlyList<NotificationEvent> NoEvents = Array.Empty<NotificationEvent>();

    /// <summary>
    /// The events one stream record produces. <paramref name="table"/> is <c>orders</c>,
    /// <c>deliveries</c> or <c>escrows</c>; <paramref name="eventName"/> the record's
    /// <c>INSERT</c>, <c>MODIFY</c> or <c>REMOVE</c>. Orders and escrows speak on <c>MODIFY</c>
    /// (both images needed), deliveries on <c>INSERT</c>; everything else is empty.
    /// </summary>
    public static IReadOnlyList<NotificationEvent> Derive(
        string table, string eventName, StreamImage oldImage, StreamImage newImage, DateTime recordTimeUtc)
    {
        return table switch
        {
            OrdersTable when eventName == ModifyEvent && oldImage.Exists && newImage.Exists
                => DeriveOrder(oldImage, newImage, recordTimeUtc),
            DeliveriesTable when eventName == InsertEvent && newImage.Exists
                => DeriveDelivery(newImage),
            EscrowsTable when eventName == ModifyEvent && oldImage.Exists && newImage.Exists
                => DeriveEscrow(oldImage, newImage),
            _ => NoEvents
        };
    }

    // ===== orders =====

    /// <summary>
    /// An order row changed: one status move (the strongest row of the matrix), the farmer's
    /// accept that keeps a trip order <c>funded</c> and asks the trucker, and the cron's reminder
    /// and pickup-overdue markers.
    /// </summary>
    private static IReadOnlyList<NotificationEvent> DeriveOrder(StreamImage old, StreamImage row, DateTime recordTimeUtc)
    {
        var orderId = row.String("orderId");
        if (orderId.Length == 0)
        {
            return NoEvents;
        }

        var events = new EventSet(OrderEntity, orderId, orderId, OrderParams(row));
        var from = old.String("status");
        var to = row.String("status");

        if (from != to)
        {
            OrderStatusChanged(events, old, row, from, to, recordTimeUtc);
        }
        else if (to == "funded")
        {
            FarmerAccepted(events, old, row);
        }

        OrderMarkers(events, old, row);
        return events.Events;
    }

    private static void OrderStatusChanged(
        EventSet events, StreamImage old, StreamImage row, string from, string to, DateTime recordTimeUtc)
    {
        var buyer = row.String("buyerId");
        var farmer = row.String("farmerId");
        var trucker = row.String("truckerId");

        switch (to)
        {
            case "funded":
                events.Add(buyer, NotificationTypes.PaymentConfirmed);
                events.AddWithDeadline(farmer, NotificationTypes.OrderRequest, row, FarmerDeadlineAtField);
                break;

            case "accepted":
                {
                    // Everyone committed. The party whose accept completed it is not told: the
                    // trucker on a trip order, else the farmer (self-pickup, or the farmer-only
                    // accept with the flag off). The trucker of a trip order hears it once their
                    // answer is in (a legacy trip row without the field counts as accepted, spec K2).
                    var actor = BecameValue(old, row, TruckerResponseField, Response.Accepted) ? trucker : farmer;
                    var pickup = row.String(PickupDateField) is { Length: > 0 } date ? date : row.String("pickupDeadlineAt");
                    var confirmed = Params((NotificationParams.PickupDate, pickup));
                    events.Add(buyer, NotificationTypes.OrderConfirmed, confirmed);
                    if (farmer != actor)
                    {
                        events.Add(farmer, NotificationTypes.OrderConfirmed, confirmed);
                    }

                    if (trucker != actor && TruckerResponse(row, to) == Response.Accepted)
                    {
                        events.Add(trucker, NotificationTypes.OrderConfirmed, confirmed);
                    }

                    break;
                }

            case "rejected":
                {
                    var decline = row.String(DeclineReasonField);
                    events.Add(buyer, NotificationTypes.OrderRejected,
                        Params((NotificationParams.Reason, DeclineReasons.Contains(decline) ? decline : null)));
                    break;
                }

            case AwaitingTransportStatus:
                {
                    var failure = Params((NotificationParams.Reason, row.String(TransportFailureField)));
                    events.AddWithDeadline(buyer, NotificationTypes.TransportFailed, row, BuyerChoiceDeadlineAtField, failure);
                    var farmerResponse = row.String(FarmerResponseField);
                    if (farmerResponse == Response.Accepted)
                    {
                        events.Add(farmer, NotificationTypes.TransportFailedKeepCrops, failure);
                    }
                    else if (farmerResponse is "" or Response.Pending)
                    {
                        // The trip was full or gone right after funding (a second write): the
                        // farmer's order_request can no longer be answered, so close it.
                        events.Add(farmer, NotificationTypes.RequestOnHold, failure);
                    }

                    if (row.String(TruckerResponseField) == Response.Expired)
                    {
                        events.Add(trucker, NotificationTypes.RequestExpired, failure);
                    }

                    break;
                }

            case "cancelled":
                OrderCancelled(events, row, from);
                break;

            case "payment_failed":
                events.Add(buyer, NotificationTypes.PaymentFailed);
                break;

            case "pickup_confirmed":
                events.Add(buyer, NotificationTypes.PickedUp);
                events.Add(farmer, NotificationTypes.HandoffRecorded);
                break;

            case "in_transit":
                events.Add(buyer, NotificationTypes.InTransit);
                break;

            case "delivered":
                {
                    // The one exception to "the actor is never told": the entry opens the buyer's own
                    // 48 hour window to report a problem.
                    var windowEnd = recordTimeUtc + DisputeWindow;
                    events.Add(buyer, NotificationTypes.Delivered,
                        Params((NotificationParams.DeadlineAt, Iso(windowEnd))), deadlineUtc: windowEnd);
                    events.Add(farmer, NotificationTypes.DeliveredPayoutPending, FarmerLeg(row));
                    if (TruckerResponse(row, to) == Response.Accepted)
                    {
                        events.Add(trucker, NotificationTypes.DeliveredPayoutPending, TruckerLeg(row));
                    }

                    break;
                }

            // disbursing tells nobody, and settled only the buyer: the payees hear from the escrow,
            // which moves the money (released, completed, the manual queue, a payout block), so one
            // payout is announced once and never out of order across the two streams.
            case "settled":
                events.Add(buyer, NotificationTypes.OrderSettled);
                break;

                // disputed and dispute_resolved: the escrow row carries them (DeriveEscrow).
        }
    }

    /// <summary>
    /// The farmer accepted a trip order: the order stays <c>funded</c>, the buyer hears the farmer
    /// said yes and the trucker is asked. The farmer, who acted, hears nothing.
    /// </summary>
    private static void FarmerAccepted(EventSet events, StreamImage old, StreamImage row)
    {
        if (!BecameValue(old, row, FarmerResponseField, Response.Accepted))
        {
            return;
        }

        events.Add(row.String("buyerId"), NotificationTypes.FarmerAccepted);
        if (BecameValue(old, row, TruckerResponseField, Response.Pending))
        {
            events.AddWithDeadline(row.String("truckerId"), NotificationTypes.TransportRequest, row, TruckerDeadlineAtField);
        }
    }

    /// <summary>
    /// An order cancelled. The buyer of an unpaid order hears only that the cron closed it; on a
    /// paid order every party who did not cancel hears it, the farmer who let the request lapse
    /// that it expired, and the trucker only once asked or committed.
    /// </summary>
    private static void OrderCancelled(EventSet events, StreamImage row, string from)
    {
        var reason = row.String("cancelReason");
        var unpaidExpiry = UnpaidStatuses.Contains(from)
                           && (reason == UnpaidExpiredReason
                               || reason.StartsWith(LegacyAutoCancelPrefix, StringComparison.Ordinal));
        var by = CancelledByOf(row, reason, unpaidExpiry);
        var paid = PaidStatuses.Contains(from);
        var cancel = Params(
            (NotificationParams.Reason, unpaidExpiry ? UnpaidExpiredReason : CancelReasonCode(reason)),
            (NotificationParams.CancelledBy, by));

        var buyer = row.String("buyerId");
        if (unpaidExpiry)
        {
            events.Add(buyer, NotificationTypes.OrderExpiredUnpaid, cancel);
        }
        else if (paid && by != Party.Buyer)
        {
            events.Add(buyer, NotificationTypes.OrderCancelled, cancel);
        }

        if (!paid)
        {
            return;
        }

        var farmer = row.String("farmerId");
        if (reason == FarmerResponseExpiredReason)
        {
            events.Add(farmer, NotificationTypes.RequestExpired, cancel);
        }
        else if (by != Party.Farmer)
        {
            events.Add(farmer, NotificationTypes.OrderCancelled, cancel);
        }

        if (TruckerResponse(row, from) is Response.Pending or Response.Accepted && by != Party.Trucker)
        {
            events.Add(row.String("truckerId"), NotificationTypes.OrderCancelled, cancel);
        }
    }

    /// <summary>
    /// The cron's markers: a reminder 6 hours before a party's answer is due, and the pickup that
    /// did not happen by <c>pickupDeadlineAt</c>. Each fires once, when the attribute appears.
    /// </summary>
    private static void OrderMarkers(EventSet events, StreamImage old, StreamImage row)
    {
        if (Appeared(old, row, FarmerReminderAtField))
        {
            events.AddWithDeadline(row.String("farmerId"), NotificationTypes.RequestReminder, row, FarmerDeadlineAtField);
        }

        if (Appeared(old, row, TruckerReminderAtField))
        {
            events.AddWithDeadline(row.String("truckerId"), NotificationTypes.RequestReminder, row, TruckerDeadlineAtField);
        }

        if (Appeared(old, row, BuyerChoiceReminderAtField))
        {
            events.AddWithDeadline(row.String("buyerId"), NotificationTypes.RequestReminder, row, BuyerChoiceDeadlineAtField);
        }

        if (Appeared(old, row, PickupOverdueNotifiedAtField))
        {
            events.Add(row.String("buyerId"), NotificationTypes.PickupOverdue);
            events.Add(row.String("farmerId"), NotificationTypes.PickupMissed);
            events.Add(row.String("truckerId"), NotificationTypes.PickupMissed);
        }
    }

    /// <summary>Names, crop, quantity and the order total: what every order event can say.</summary>
    private static Dictionary<string, string> OrderParams(StreamImage row)
    {
        var p = Names(row);
        Put(p, NotificationParams.CropType, row.String("cropType"));
        Put(p, NotificationParams.QuantityKg, Quantity(row.Number("quantity")));
        Put(p, NotificationParams.AmountEtb, Money(row.Number("orderTotal")));
        return p;
    }

    /// <summary>The farmer's own leg (<c>cropCost</c>), never the buyer's total; absent means no amount.</summary>
    private static Dictionary<string, string?> FarmerLeg(StreamImage row)
        => Params((NotificationParams.AmountEtb, Money(row.Number("cropCost"))));

    /// <summary>The trucker's own leg (<c>transportCost</c>).</summary>
    private static Dictionary<string, string?> TruckerLeg(StreamImage row)
        => Params((NotificationParams.AmountEtb, Money(row.Number("transportCost"))));

    /// <summary>
    /// The trucker's answer as <see cref="OrderCommitment.TruckerResponseOf"/> reads it: the stored
    /// value; for a trip row without it, <c>not_asked</c> while the money waits on the farmer
    /// (<paramref name="status"/> <c>funded</c> or <c>awaiting_transport</c>) and <c>accepted</c> after.
    /// </summary>
    private static string TruckerResponse(StreamImage row, string status)
    {
        var stored = row.String(TruckerResponseField);
        if (stored.Length > 0)
        {
            return stored;
        }

        if (row.String("truckerId").Length == 0 && row.String("tripId").Length == 0)
        {
            return Response.None;
        }

        return status is "funded" or AwaitingTransportStatus ? Response.NotAsked : Response.Accepted;
    }

    /// <summary>
    /// Who cancelled: the stored <c>cancelledBy</c>, else what the reason code says. A cancel
    /// without either is the buyer's: before 2.0.0 only the buyer cancelled from the app, and every
    /// system cancel writes a code (sync keeps clients from writing one, and the unpaid cron's
    /// sentence counts only on an unpaid order: <paramref name="unpaidExpiry"/>).
    /// </summary>
    private static string CancelledByOf(StreamImage row, string reason, bool unpaidExpiry)
    {
        var stored = row.String(CancelledByField);
        if (stored.Length > 0)
        {
            return stored;
        }

        if (CancelReasonActors.TryGetValue(reason, out var actor))
        {
            return actor;
        }

        return unpaidExpiry || ServerCancelReasons.Contains(reason) ? Party.System : Party.Buyer;
    }

    /// <summary>A server cancel code as the <c>reason</c> param; the buyer's own text is dropped.</summary>
    private static string? CancelReasonCode(string reason) => ServerCancelReasons.Contains(reason) ? reason : null;

    // ===== deliveries =====

    /// <summary>
    /// A delivery created. A trucker delivery tells the buyer and the farmer (the trucker made it);
    /// a self-pickup tells the farmer the buyer is coming (the buyer made it).
    /// </summary>
    private static IReadOnlyList<NotificationEvent> DeriveDelivery(StreamImage row)
    {
        var deliveryId = row.String("deliveryId");
        if (deliveryId.Length == 0)
        {
            return NoEvents;
        }

        var p = Names(row);
        Put(p, NotificationParams.CropType, row.String("cropType"));
        Put(p, NotificationParams.QuantityKg, Quantity(row.Number("quantity")));
        Put(p, NotificationParams.PickupDate, row.String(PickupDateField));

        var orderId = row.String("orderId");
        var events = new EventSet(DeliveryEntity, deliveryId, orderId.Length > 0 ? orderId : null, p);
        if (row.String("deliveryType") == Fulfillment.SelfPickup)
        {
            events.Add(row.String("farmerId"), NotificationTypes.BuyerCollecting);
        }
        else
        {
            events.Add(row.String("buyerId"), NotificationTypes.DeliveryScheduled);
            events.Add(row.String("farmerId"), NotificationTypes.DeliveryScheduled);
        }

        return events.Events;
    }

    // ===== escrows =====

    /// <summary>
    /// An escrow row changed: the money moves (refund, release, completion), a dispute opened or
    /// decided, a fraud hold, and the markers that a payout waits (a payout check, the manual queue).
    /// </summary>
    private static IReadOnlyList<NotificationEvent> DeriveEscrow(StreamImage old, StreamImage row)
    {
        var escrowId = row.String("escrowId");
        if (escrowId.Length == 0)
        {
            return NoEvents;
        }

        var p = Names(row);
        Put(p, NotificationParams.AmountEtb, Money(row.Number("amount")));
        var orderId = row.String("orderId");
        var events = new EventSet(EscrowEntity, escrowId, orderId.Length > 0 ? orderId : null, p);

        var from = old.String("status");
        var to = row.String("status");
        if (from != to)
        {
            EscrowStatusChanged(events, row, from, to);
        }

        EscrowMarkers(events, old, row);
        return events.Events;
    }

    private static void EscrowStatusChanged(EventSet events, StreamImage row, string from, string to)
    {
        var buyer = row.String("buyerId");
        var farmer = row.String("farmerId");
        // The escrow names the trip's trucker from funding on, before the trucker is asked or
        // could decline; only a delivery (the trucker's own, once committed) ties them to the trade.
        var trucker = row.String("deliveryId").Length > 0 ? row.String("truckerId") : string.Empty;

        switch (to)
        {
            case "refunded":
                events.Add(buyer, NotificationTypes.RefundSent);
                break;

            // completed -> released is only the legacy trucker-leg park: the farmer was paid long ago.
            case "released" when from != "completed":
                events.Add(farmer, NotificationTypes.PayoutSent, EscrowLeg(row, "farmerPayout"));
                break;

            case "completed":
                if (row.Number("truckerPayout") is > 0)
                {
                    events.Add(trucker, NotificationTypes.PayoutSent, EscrowLeg(row, "truckerPayout"));
                }

                break;

            case "disputed":
                {
                    var disputer = row.String("disputedBy");
                    var opened = Params((NotificationParams.Party, RoleOf(row, disputer)));
                    foreach (var party in new[] { buyer, farmer, trucker })
                    {
                        if (party.Length > 0 && party == disputer)
                        {
                            events.Add(party, NotificationTypes.DisputeReceived, push: false);
                        }
                        else
                        {
                            events.Add(party, NotificationTypes.DisputeOpened, opened);
                        }
                    }

                    break;
                }

            case "dispute_resolved":
                {
                    var resolved = Params((NotificationParams.Resolution, row.String("disputeResolution")));
                    events.Add(buyer, NotificationTypes.DisputeResolved, resolved);
                    events.Add(farmer, NotificationTypes.DisputeResolved, resolved);
                    events.Add(trucker, NotificationTypes.DisputeResolved, resolved);
                    break;
                }

            case EscrowHoldFields.Status:
                events.Add(buyer, NotificationTypes.UnderReview, push: false);
                events.Add(farmer, NotificationTypes.UnderReview, push: false);
                events.Add(trucker, NotificationTypes.UnderReview, push: false);
                break;
        }
    }

    /// <summary>
    /// A payout that waits on its payee: the farmer's identity or phone check on the release sweep
    /// (<c>settlementBlockReason</c> = <c>release:payout:&lt;reason&gt;</c>), the trucker's leg held by
    /// the same checks (<c>truckerPayoutPending</c> with <c>truckerPayoutBlockReason</c>, at the
    /// release or on the hourly retry), and a release the admin pays by hand (<c>manualPayout</c>).
    /// A trade block (order or delivery state) or a gateway failure is not the payee's to fix and
    /// tells nobody.
    /// </summary>
    private static void EscrowMarkers(EventSet events, StreamImage old, StreamImage row)
    {
        var payoutGate = SettlementBlockFields.Prefix(ManualPayoutFields.OpRelease, SettlementBlockFields.GatePayout);
        var settlementBlock = row.String(SettlementBlockFields.Reason);
        if (settlementBlock.StartsWith(payoutGate, StringComparison.Ordinal)
            && settlementBlock != old.String(SettlementBlockFields.Reason))
        {
            var leg = EscrowLeg(row, "farmerPayout");
            leg[NotificationParams.Reason] = settlementBlock[payoutGate.Length..];
            events.Add(row.String("farmerId"), NotificationTypes.PayoutBlocked, leg);
        }

        var truckerBlock = row.String(TruckerPayoutFields.BlockReason);
        if (row.Bool(TruckerPayoutFields.Pending) == true && truckerBlock.Length > 0
            && truckerBlock != old.String(TruckerPayoutFields.BlockReason))
        {
            var leg = EscrowLeg(row, "truckerPayout");
            leg[NotificationParams.Reason] = truckerBlock;
            events.Add(row.String("truckerId"), NotificationTypes.PayoutBlocked, leg);
        }

        if (BecameValue(old, row, ManualPayoutFields.Marker, ManualPayoutFields.MarkerOpen)
            && row.String(ManualPayoutFields.Op) is ManualPayoutFields.OpRelease or "")
        {
            if (ManualLegPending(row, ManualPayoutFields.LegFarmer, fallback: true))
            {
                events.Add(row.String("farmerId"), NotificationTypes.PayoutProcessing, EscrowLeg(row, "farmerPayout"));
            }

            if (ManualLegPending(row, ManualPayoutFields.LegTrucker, fallback: row.Number("truckerPayout") is > 0))
            {
                events.Add(row.String("truckerId"), NotificationTypes.PayoutProcessing, EscrowLeg(row, "truckerPayout"));
            }
        }
    }

    /// <summary>Whether a payee leg of the manual queue still waits; a row without leg fields uses <paramref name="fallback"/>.</summary>
    private static bool ManualLegPending(StreamImage row, string leg, bool fallback)
    {
        var status = row.String(ManualPayoutFields.StatusAttr(leg));
        return status.Length == 0 ? fallback : status == ManualPayoutFields.LegPending;
    }

    /// <summary>One payee's amount on the escrow row as the event's <c>amountEtb</c>.</summary>
    private static Dictionary<string, string?> EscrowLeg(StreamImage row, string field)
        => Params((NotificationParams.AmountEtb, Money(row.Number(field))));

    /// <summary>The role of a party on the row (<c>buyer</c>, <c>farmer</c>, <c>trucker</c>), or null for anyone else.</summary>
    private static string? RoleOf(StreamImage row, string userId)
    {
        if (userId.Length == 0)
        {
            return null;
        }

        if (userId == row.String("buyerId"))
        {
            return Party.Buyer;
        }

        if (userId == row.String("farmerId"))
        {
            return Party.Farmer;
        }

        return userId == row.String("truckerId") ? Party.Trucker : null;
    }

    // ===== shared helpers =====

    /// <summary>The display names the row denormalizes, scrubbed of phone numbers before the inbox stores them.</summary>
    private static Dictionary<string, string> Names(StreamImage row)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        Put(p, NotificationParams.BuyerName, PushText.ScrubName(row.String("buyerName")));
        Put(p, NotificationParams.FarmerName, PushText.ScrubName(row.String("farmerName")));
        Put(p, NotificationParams.TruckerName, PushText.ScrubName(row.String("truckerName")));
        return p;
    }

    /// <summary>The attribute was absent before this write and is present after it.</summary>
    private static bool Appeared(StreamImage old, StreamImage row, string field) => !old.Has(field) && row.Has(field);

    /// <summary>The string attribute changed to <paramref name="value"/> in this write.</summary>
    private static bool BecameValue(StreamImage old, StreamImage row, string field, string value)
        => row.String(field) == value && old.String(field) != value;

    /// <summary>Quantity in kilograms, digits only (<c>500</c>, <c>1250.5</c>).</summary>
    private static string? Quantity(double? kg)
        => kg is > 0 ? kg.Value.ToString("0.##", CultureInfo.InvariantCulture) : null;

    /// <summary>An ETB amount with two decimals, digits and a point only (<c>42500.00</c>).</summary>
    private static string? Money(double? etb)
        => etb is >= 0 ? etb.Value.ToString("0.00", CultureInfo.InvariantCulture) : null;

    private static void Put(Dictionary<string, string> p, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            p[key] = value;
        }
    }

    /// <summary>Per-event params on top of the row's; a null or empty value removes the key.</summary>
    private static Dictionary<string, string?> Params(params (string Key, string? Value)[] entries)
    {
        var p = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            p[key] = value;
        }

        return p;
    }

    /// <summary>
    /// The events of one record: one per recipient (the first rule to name a party wins), the
    /// row's params under each event's own, and a deadline only on an action-required type.
    /// </summary>
    private sealed class EventSet
    {
        private readonly string _entity;
        private readonly string _entityId;
        private readonly string? _orderId;
        private readonly Dictionary<string, string> _rowParams;
        private readonly List<NotificationEvent> _events = new();
        private readonly HashSet<string> _told = new(StringComparer.Ordinal);

        public EventSet(string entity, string entityId, string? orderId, Dictionary<string, string> rowParams)
        {
            _entity = entity;
            _entityId = entityId;
            _orderId = orderId;
            _rowParams = rowParams;
        }

        public IReadOnlyList<NotificationEvent> Events => _events;

        public void Add(
            string recipientId, string type, Dictionary<string, string?>? extra = null,
            bool push = true, DateTime? deadlineUtc = null)
        {
            if (recipientId.Length == 0 || !_told.Add(recipientId))
            {
                return;
            }

            var p = new Dictionary<string, string>(_rowParams, StringComparer.Ordinal);
            if (extra != null)
            {
                foreach (var (key, value) in extra)
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        p.Remove(key);
                    }
                    else
                    {
                        p[key] = value;
                    }
                }
            }

            _events.Add(new NotificationEvent(
                recipientId, type, _entity, _entityId, _orderId, p, push,
                NotificationTypes.ActionRequired.Contains(type) ? deadlineUtc : null));
        }

        /// <summary>An event whose deadline (param and push time to live) is the row's ISO <paramref name="deadlineField"/>.</summary>
        public void AddWithDeadline(
            string recipientId, string type, StreamImage row, string deadlineField,
            Dictionary<string, string?>? extra = null)
        {
            var raw = row.String(deadlineField);
            var p = extra == null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(extra, StringComparer.Ordinal);
            p[NotificationParams.DeadlineAt] = raw;
            Add(recipientId, type, p, deadlineUtc: TryParseUtc(raw, out var deadline) ? deadline : null);
        }
    }
}

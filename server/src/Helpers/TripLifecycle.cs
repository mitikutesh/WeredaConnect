using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-17 (design 4.10, edge case G4): the two trip moves the server makes on its own, without the
/// trucker. One rule each, shared by every caller so the sync hook, the cron and the tests agree.
/// <list type="bullet">
/// <item>An <c>open</c> trip whose Ethiopia day is over, that holds no booking and that no live order
/// still names, closes itself: <c>cancelled</c> with <c>cancelReason: expired</c>.</item>
/// <item>An <c>inTransit</c> trip becomes <c>delivered</c> once every delivery on it is done or
/// cancelled and at least one is done.</item>
/// </list>
/// </summary>
public static class TripLifecycle
{
    /// <summary>The <c>cancelReason</c> of a trip the order cron closed because it left unbooked.</summary>
    public const string ExpiredCancelReason = "expired";

    /// <summary>
    /// Delivery statuses where the truck's work is done: the buyer confirmed the handoff, or a
    /// dispute was raised on it (a sync dispute starts only from <c>delivered</c>; an admin's
    /// <c>delivered_anyway</c> disposition also lands here).
    /// </summary>
    private static readonly HashSet<string> DoneDeliveryStatuses = new(StringComparer.Ordinal)
    {
        "delivered", "disputed"
    };

    /// <summary>
    /// Order statuses that no longer need their trip: closed for good, or the transport already
    /// failed and the order waits for the buyer's choice (<c>awaiting_transport</c>). A failed payment
    /// cannot be retried on a trip that has left (the payment gate refuses it). Any other status,
    /// unpaid ones included, keeps the trip open until the order resolves.
    /// </summary>
    private static readonly HashSet<string> OffTripOrderStatuses = new(StringComparer.Ordinal)
    {
        "cancelled", "rejected", "payment_failed", OrderCommitment.AwaitingTransportStatus
    };

    /// <summary>
    /// Whether a trip with these delivery statuses is finished: at least one delivery, every one
    /// done or cancelled, and at least one done (a trip whose deliveries were all cancelled carried
    /// nothing, so it is not delivered).
    /// </summary>
    public static bool AllDeliveriesDone(IReadOnlyCollection<string> deliveryStatuses)
        => deliveryStatuses.Count > 0
           && deliveryStatuses.All(s => s == "cancelled" || DoneDeliveryStatuses.Contains(s))
           && deliveryStatuses.Any(DoneDeliveryStatuses.Contains);

    /// <summary>Whether an order in <paramref name="orderStatus"/> still counts on its trip.</summary>
    public static bool OrderStillOnTrip(string orderStatus) => !OffTripOrderStatuses.Contains(orderStatus);

    /// <summary>
    /// Order statuses that still wait for the truck to carry them: paid and booked or agreed (no
    /// delivery may exist yet), or picked up.
    /// </summary>
    private static readonly HashSet<string> AwaitingCarriageOrderStatuses = new(StringComparer.Ordinal)
    {
        "funded", "accepted", "pickup_confirmed", "in_transit"
    };

    /// <summary>
    /// The first order on the trip that still waits for the truck (status <c>funded</c>,
    /// <c>accepted</c>, <c>pickup_confirmed</c> or <c>in_transit</c>) and has no done delivery among
    /// <paramref name="deliveries"/>; null when there is none. An order row can lag the delivery that
    /// just finished it, so a done delivery for the order counts over the order's own status. A trip
    /// with such an order is not finished even when every delivery on it is: the order's delivery may
    /// not have been created yet.
    /// </summary>
    public static string? FirstOrderAwaitingCarriage(
        IEnumerable<(string OrderId, string Status)> orders,
        IEnumerable<(string Status, string OrderId)> deliveries)
    {
        var carried = deliveries
            .Where(d => DoneDeliveryStatuses.Contains(d.Status) && d.OrderId.Length > 0)
            .Select(d => d.OrderId)
            .ToHashSet(StringComparer.Ordinal);
        return orders
            .Where(o => AwaitingCarriageOrderStatuses.Contains(o.Status) && !carried.Contains(o.OrderId))
            .Select(o => o.OrderId)
            .FirstOrDefault();
    }
}

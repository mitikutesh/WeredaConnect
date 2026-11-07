using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-17 (design 4.10): the trip moves the server makes after a delivery, and the answer a client
/// gets when it changes a trip the server already closed. The rules are in <see cref="TripLifecycle"/>;
/// the order cron closes the trips that left unbooked (<c>OrderCronFunction</c>).
/// </summary>
public partial class SyncFunction
{
    /// <summary>
    /// ESC-17: after a delivery reached <c>delivered</c>, <c>disputed</c> or <c>cancelled</c> through
    /// sync, an <c>inTransit</c> trip becomes <c>delivered</c> when every delivery on it is done or
    /// cancelled and at least one is done (<see cref="TripLifecycle.AllDeliveriesDone"/>), and no
    /// order on it still waits for the truck without a done delivery
    /// (<see cref="TripLifecycle.FirstOrderAwaitingCarriage"/>: a funded or accepted order may have
    /// no delivery yet). The deliveries and the orders are read through their TripIdIndex, which can
    /// lag the write this request just made, so that delivery counts with the status it was given
    /// (and a lagging order row of a done delivery does not hold the trip). The trip write is
    /// conditioned on the version read and on <c>inTransit</c>
    /// (<see cref="ITripRepository.TryMarkDeliveredAsync"/>), so the trucker's own "Mark delivered"
    /// landing first wins and a replay changes nothing. Behind <c>COMMITMENTS_ENABLED</c> like the
    /// other 4.10 trip rules. Best effort: the delivery write has landed, a failure is logged with
    /// <c>TRIP_COMPLETE_FAILED</c>, and the trucker can still mark the trip delivered.
    /// </summary>
    /// <param name="delivery">The delivery row as read before the write (its trip and type).</param>
    private async Task TryCompleteTripAsync(
        Dictionary<string, AttributeValue> delivery, string deliveryId, string deliveryStatus, ILambdaContext context)
    {
        if (!CommitmentsEnabled
            || deliveryStatus is not ("delivered" or "disputed" or "cancelled")
            || AttrString(delivery, "deliveryType") == SelfPickupDeliveryType)
        {
            return;
        }

        var tripId = AttrString(delivery, "tripId");
        if (tripId.Length == 0)
        {
            return;
        }

        try
        {
            var trip = await _tripRepo.GetRawItemAsync(tripId);
            if (trip == null || AttrString(trip, "status") != "inTransit" || trip.ContainsKey("deletedAt"))
            {
                return;
            }

            var deliveries = await ReadTripDeliveryStatusesAsync(
                tripId, deliveryId, deliveryStatus, AttrString(delivery, "orderId"));
            if (deliveries == null)
            {
                context.Logger.LogWarning(
                    $"Trip {MoneyAlarmLog.LogId(tripId)}: more than {MaxTripDeliveryPages * 50} deliveries; left for the trucker to mark delivered.");
                return;
            }

            if (!TripLifecycle.AllDeliveriesDone(deliveries.Select(d => d.Status).ToList()))
            {
                return;
            }

            var orders = await _orderRepo.QueryByTripAsync(tripId) ?? new List<Dictionary<string, object>>();
            var waiting = TripLifecycle.FirstOrderAwaitingCarriage(
                orders.Select(o => (ItemString(o, "orderId"), ItemString(o, "status"))), deliveries);
            if (waiting != null)
            {
                context.Logger.LogInformation(
                    $"Trip {MoneyAlarmLog.LogId(tripId)}: order {MoneyAlarmLog.LogId(waiting)} still waits for the truck; not marked delivered.");
                return;
            }

            var marked = await _tripRepo.TryMarkDeliveredAsync(
                tripId, AttrString(trip, "createdAt"), ItemVersion(trip), DateTime.UtcNow.ToString("o"));
            context.Logger.LogInformation(marked
                ? $"Trip {MoneyAlarmLog.LogId(tripId)}: inTransit -> delivered (all {deliveries.Count} deliveries done or cancelled)."
                : $"Trip {MoneyAlarmLog.LogId(tripId)}: changed since it was read; not marked delivered.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"TRIP_COMPLETE_FAILED tripId={MoneyAlarmLog.LogId(tripId)} deliveryId={MoneyAlarmLog.LogId(deliveryId)} reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// The status and order of every delivery on the trip (soft-deleted rows are left out by the
    /// repository), with <paramref name="deliveryId"/> at <paramref name="deliveryStatus"/>; null when
    /// the trip has more than <see cref="MaxTripDeliveryPages"/> pages of them. Unlike the check-in's
    /// <c>ReadTripDeliveriesAsync</c>, which decides on the first pages, a trip is never finished on
    /// a partial list.
    /// </summary>
    private async Task<List<(string Status, string OrderId)>?> ReadTripDeliveryStatusesAsync(
        string tripId, string deliveryId, string deliveryStatus, string deliveryOrderId)
    {
        var statuses = new Dictionary<string, (string Status, string OrderId)>(StringComparer.Ordinal);
        Dictionary<string, AttributeValue>? startKey = null;
        for (var page = 0; page < MaxTripDeliveryPages; page++)
        {
            var (items, next) = await _deliveryRepo.QueryDeliveriesAsync(tripId: tripId, limit: 50, exclusiveStartKey: startKey);
            foreach (var item in items ?? new List<Dictionary<string, object>>())
            {
                var id = ItemString(item, "deliveryId");
                if (id.Length > 0)
                {
                    statuses[id] = (ItemString(item, "status"), ItemString(item, "orderId"));
                }
            }

            if (next == null)
            {
                var orderId = deliveryOrderId.Length > 0 || !statuses.TryGetValue(deliveryId, out var indexed)
                    ? deliveryOrderId
                    : indexed.OrderId;
                statuses[deliveryId] = (deliveryStatus, orderId);
                return statuses.Values.ToList();
            }

            startKey = next;
        }

        return null;
    }

    /// <summary>
    /// ESC-17: a change to a trip that is already over (<c>cancelled</c>, <c>delivered</c> or
    /// deleted). When what the client asks already holds (a cancel of a cancelled trip, a delivered
    /// for a delivered one) or the client is behind the stored version (the order cron closed the
    /// trip, or the last delivery made it delivered, while the phone was offline), the answer is a
    /// conflict with the stored row, so the client takes the server's trip (server wins) instead of
    /// retrying an error. Null otherwise: the caller keeps its refusal.
    /// </summary>
    /// <param name="requestedStatus">The status the update sends (null when it sends none); <c>cancelled</c> for a delete.</param>
    private static SyncOperationResult? TerminalTripConflict(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> trip, string? requestedStatus)
    {
        var stored = trip.TryGetValue("deletedAt", out var deletedAt) && !string.IsNullOrEmpty(deletedAt.S)
            ? "cancelled"
            : AttrString(trip, "status");
        var storedVersion = ItemVersion(trip);
        var serverData = ScrubSyncSecrets(CropRepository.FromItem(trip));

        if (requestedStatus == stored)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "conflict",
                ServerVersion = storedVersion,
                ServerData = serverData,
                Message = $"TRIP_ALREADY_{stored.ToUpperInvariant()}: The trip is already {stored}."
            };
        }

        return StaleClientVersion(operation, storedVersion) is int sent
            ? StaleVersionConflict(operation, storedVersion, sent, serverData)
            : null;
    }
}

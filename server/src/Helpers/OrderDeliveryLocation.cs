using System.Text.Json;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.9.0, the "Delivery location" of
/// delivery-verification-api.md P.4): the order's own drop-off point, <c>deliveryLatitude</c> and
/// <c>deliveryLongitude</c> (the names of the buyer intent's point). The buyer picks a place when
/// ordering (the clients start from the intent's place or the buyer's registered place) and may
/// change it until pickup, in the drop-off note's edit window (<see cref="OrderDropoffNote"/>). Its
/// readers are wider than the note's (<see cref="VisibleTo"/>): the order's trucker reads it while
/// asked, before committing, since the drop-off town decides the answer and the point is a town
/// centroid from the public place list. Only a trucked order has one: on an order without a trip
/// (self-pickup) it is ignored. The track, the other stops on a truck, the check-in effects and the
/// delivered GPS check read it (<see cref="TripTrack.PlannedDropoff"/>).
/// </summary>
public static class OrderDeliveryLocation
{
    public const string LatitudeField = "deliveryLatitude";
    public const string LongitudeField = "deliveryLongitude";

    /// <summary>Sync error prefix for a change after pickup; permanent like every <c>ORDER_*</c> code.</summary>
    public const string LockedCode = "ORDER_DELIVERY_LOCATION_LOCKED";

    public const string LockedMessage = LockedCode + ": The drop-off place can only be changed before pickup.";

    /// <summary>Whether a payload carries either field.</summary>
    public static bool Sent(IReadOnlyDictionary<string, object> data)
        => data.ContainsKey(LatitudeField) || data.ContainsKey(LongitudeField);

    /// <summary>
    /// Why the point a payload sends is not valid, or null when it is (or when it sends none). Both
    /// fields or neither; each a number, latitude 3 to 15 and longitude 32.5 to 48.5 (Ethiopia with a
    /// margin, the box of a <c>location_ping</c> and a trip's destination point). Both null is no
    /// point (on update: clear it).
    /// </summary>
    public static string? ValidationError(IReadOnlyDictionary<string, object> data)
    {
        var hasLatitude = data.TryGetValue(LatitudeField, out var latitude);
        var hasLongitude = data.TryGetValue(LongitudeField, out var longitude);
        if (!hasLatitude && !hasLongitude)
        {
            return null;
        }

        if (hasLatitude != hasLongitude || IsNull(latitude) != IsNull(longitude))
        {
            return $"{LatitudeField} and {LongitudeField} must be sent together.";
        }

        if (IsNull(latitude))
        {
            return null;
        }

        var errors = new List<string>();
        if (!TryNumber(latitude, out var lat)
            || lat < LocationPingRules.MinLatitude || lat > LocationPingRules.MaxLatitude)
        {
            errors.Add($"{LatitudeField} must be a number between 3 and 15.");
        }

        if (!TryNumber(longitude, out var lng)
            || lng < LocationPingRules.MinLongitude || lng > LocationPingRules.MaxLongitude)
        {
            errors.Add($"{LongitudeField} must be a number between 32.5 and 48.5.");
        }

        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    /// <summary>
    /// The point a payload sends, null for none (both null, or neither sent). Call after
    /// <see cref="ValidationError"/> returned null.
    /// </summary>
    public static TrackPoint? FromPayload(IReadOnlyDictionary<string, object> data)
        => data.TryGetValue(LatitudeField, out var latitude) && TryNumber(latitude, out var lat)
           && data.TryGetValue(LongitudeField, out var longitude) && TryNumber(longitude, out var lng)
            ? new TrackPoint(lat, lng)
            : null;

    /// <summary>The stored point of an order row; null when it has none.</summary>
    public static TrackPoint? Of(IReadOnlyDictionary<string, object>? order)
        => TripTrack.PointOf(order, LatitudeField, LongitudeField);

    /// <summary>
    /// Whether <paramref name="userId"/> may read the point of this order row: its buyer and its
    /// farmer always; its trucker (<c>truckerId</c>) while the order's money is held
    /// (<see cref="ContactReveal.OpenOrderStatuses"/>) and the trucker is still on the order: not
    /// asked yet, asked, or committed (<see cref="TruckerStillOn"/>), so the trucker answers the
    /// transport request knowing where the goods go; a trucker who declined, expired, dropped out or
    /// failed on capacity reads nothing more, also the order's later changes; anyone else never.
    /// Wider than the drop-off note's rule (<see cref="OrderDropoffNote.VisibleTo"/>), which waits for
    /// the trucker's commitment.
    /// </summary>
    public static bool VisibleTo(IReadOnlyDictionary<string, object> order, string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (userId == Text(order, "buyerId") || userId == Text(order, "farmerId"))
        {
            return true;
        }

        return userId == Text(order, "truckerId")
               && ContactReveal.OpenOrderStatuses.Contains(Text(order, "status") ?? string.Empty)
               && TruckerStillOn.Contains(OrderCommitment.TruckerResponseOf(order));
    }

    /// <summary>
    /// The trucker answers (<see cref="OrderCommitment.TruckerResponseOf"/>, so an order from before
    /// the commitments counts by its status) of a trucker still on the order. Not <c>declined</c>,
    /// <c>expired</c>, <c>cancelled</c> (a drop-out) or <c>failed_capacity</c>: those orders keep the
    /// <c>truckerId</c> in <c>awaiting_transport</c>, but the trucker has left them.
    /// </summary>
    private static readonly IReadOnlySet<string> TruckerStillOn = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderCommitment.Response.NotAsked, OrderCommitment.Response.Pending, OrderCommitment.Response.Accepted
    };

    /// <summary>The point <paramref name="userId"/> may read on this row (<see cref="VisibleTo"/>), else null.</summary>
    public static TrackPoint? For(IReadOnlyDictionary<string, object>? order, string? userId)
        => order != null && VisibleTo(order, userId) ? Of(order) : null;

    /// <summary>
    /// A trucked order (a trip or a trucker bound; not <c>fulfillment: self_pickup</c>): the only
    /// kind with a drop-off point. On any other order the fields are ignored.
    /// </summary>
    public static bool HasTrip(IReadOnlyDictionary<string, object> order)
        => Text(order, OrderCommitment.FulfillmentField) != OrderCommitment.Fulfillment.SelfPickup
           && (!string.IsNullOrEmpty(Text(order, "tripId")) || !string.IsNullOrEmpty(Text(order, "truckerId")));

    /// <summary>Drops both fields from a payload (ignored, never stored).</summary>
    public static void Drop(Dictionary<string, object> data)
    {
        data.Remove(LatitudeField);
        data.Remove(LongitudeField);
    }

    /// <summary>
    /// A sync result whose order row (<c>serverData</c>) goes to <paramref name="userId"/>: the point
    /// left out when that caller may not read it (<see cref="VisibleTo"/>). Any other result is
    /// returned unchanged.
    /// </summary>
    public static SyncOperationResult ForCaller(SyncOperationResult result, string? userId)
    {
        if (result.ServerData is not { } row
            || !(row.ContainsKey(LatitudeField) || row.ContainsKey(LongitudeField))
            || VisibleTo(row, userId))
        {
            return result;
        }

        var scrubbed = new Dictionary<string, object>(row);
        Drop(scrubbed);
        return result with { ServerData = scrubbed };
    }

    /// <summary>The same point, or both none (a stored number comes back as invariant text).</summary>
    public static bool Same(TrackPoint? a, TrackPoint? b)
        => a == null || b == null
            ? a == null && b == null
            : Math.Abs(a.Latitude - b.Latitude) < 1e-9 && Math.Abs(a.Longitude - b.Longitude) < 1e-9;

    private static string? Text(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsNull(object? value)
        => value is null || value is JsonElement { ValueKind: JsonValueKind.Null };

    /// <summary>A JSON number (or a CLR number); text is not a number here.</summary>
    private static bool TryNumber(object? value, out double number)
    {
        number = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            _ => double.NaN
        };
        return !double.IsNaN(number) && !double.IsInfinity(number);
    }
}

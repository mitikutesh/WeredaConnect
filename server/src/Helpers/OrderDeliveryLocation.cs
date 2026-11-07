using System.Text.Json;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.8.0, the "Delivery location" of
/// delivery-verification-api.md P.4): the order's own drop-off point, <c>deliveryLatitude</c> and
/// <c>deliveryLongitude</c> (the names of the buyer intent's point). The buyer picks a place when
/// ordering (the clients start from the intent's place or the buyer's registered place) and may
/// change it until pickup, by the rules of the drop-off note (<see cref="OrderDropoffNote"/>: the
/// same readers, the same edit window). The track, the other stops on a truck, the check-in effects
/// and the delivered GPS check read it before the trip's destination point
/// (<see cref="TripTrack.PlannedDropoff"/>).
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
    /// The point <paramref name="userId"/> may read on this row: the readers of the drop-off note
    /// (<see cref="OrderDropoffNote.VisibleTo"/>: the buyer, the farmer, the trucker once committed
    /// to a held order), else null.
    /// </summary>
    public static TrackPoint? For(IReadOnlyDictionary<string, object>? order, string? userId)
        => order != null && OrderDropoffNote.VisibleTo(order, userId) ? Of(order) : null;

    /// <summary>The same point, or both none (a stored number comes back as invariant text).</summary>
    public static bool Same(TrackPoint? a, TrackPoint? b)
        => a == null || b == null
            ? a == null && b == null
            : Math.Abs(a.Latitude - b.Latitude) < 1e-9 && Math.Abs(a.Longitude - b.Longitude) < 1e-9;

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

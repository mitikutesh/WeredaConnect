using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// LT-07: the decisions behind the side effects of an accepted check-in (live-trip.md section 6.3,
/// live-trip-api.md sections 4 and 5): when a picked-up delivery has left its pickup, when the truck
/// is near a drop-off, and when two check-ins are an impossible jump. Pure: the caller reads the rows
/// and writes the results. A check-in never moves money; these only move a delivery to
/// <c>inTransit</c>, stamp <c>nearbyNotifiedAt</c> and raise fraud signals.
/// </summary>
public static class CheckInEffects
{
    /// <summary>A <c>pickedUp</c> delivery moves to <c>inTransit</c> at a check-in this far from its pickup or more.</summary>
    public const double DepartureKm = 2.0;

    /// <summary>The buyer hears <c>truck_nearby</c> at a check-in this close to the drop-off or closer.</summary>
    public const double NearbyKm = 5.0;

    /// <summary>Two consecutive check-ins faster than this, and farther apart than <see cref="JumpMinKm"/>, are a jump.</summary>
    public const double JumpKmh = 130;

    /// <summary>Check-ins closer than this are never a jump (GPS noise between two fixes taken close in time).</summary>
    public const double JumpMinKm = 1.0;

    /// <summary>
    /// A check-in less precise than this (a cell tower guess) has no effect by its position: its error
    /// alone could cross the 2 km, 5 km and 1 km lines. It is still stored and drawn on the track.
    /// </summary>
    public const double MaxAccuracyM = 1000;

    /// <summary>Server-only: when <c>truck_nearby</c> was decided for the delivery. Stamped once, conditionally.</summary>
    public const string NearbyNotifiedAtField = "nearbyNotifiedAt";

    /// <summary>Server-only: the truck's great circle distance to the drop-off at that check-in, one decimal.</summary>
    public const string NearbyDistanceKmField = "nearbyDistanceKm";

    /// <summary>Server-only: the start of the arrival window in minutes from the stamp, a multiple of 5.</summary>
    public const string NearbyMinutesField = "nearbyMinutes";

    /// <summary>The <c>source</c> in the transaction-audit metadata of a system move to <c>inTransit</c>.</summary>
    public const string AuditSource = "check_in";

    /// <summary>
    /// Whether the check-in was captured on this delivery's road: at or after its <c>pickedUpAt</c>
    /// (the server time the pickup synced, never earlier than the real pickup). A check-in that waited
    /// offline from before the pickup says nothing about this delivery, so it moves and stamps nothing.
    /// False without a readable <c>pickedUpAt</c>.
    /// </summary>
    public static bool CapturedOnRoad(Dictionary<string, object> delivery, DateTime capturedAtUtc)
        => TripTrack.PickedUpAt(delivery) is { } pickedUpAt && capturedAtUtc >= pickedUpAt;

    /// <summary>Whether a check-in is precise enough for the effects of its position: <c>accuracyM</c> at most <see cref="MaxAccuracyM"/>.</summary>
    public static bool PreciseEnough(double? accuracyM) => accuracyM is { } accuracy && accuracy <= MaxAccuracyM;

    /// <summary>
    /// The distance from the pickup when a <c>pickedUp</c> delivery has left it: the check-in is on
    /// the delivery's road and <see cref="DepartureKm"/> or more from <paramref name="pickup"/> (its
    /// gpsPickup, else its listing location). Null when the delivery stays as it is.
    /// </summary>
    public static double? DepartedKm(
        Dictionary<string, object> delivery, TrackPoint? pickup, TrackPoint fix, DateTime capturedAtUtc)
    {
        if (Text(delivery, "status") != "pickedUp" || pickup == null || !CapturedOnRoad(delivery, capturedAtUtc))
        {
            return null;
        }

        var km = DistanceKm(pickup, fix);
        return km >= DepartureKm ? km : null;
    }

    /// <summary>
    /// The distance to the drop-off when the buyer should hear <c>truck_nearby</c>: the delivery is
    /// <c>pickedUp</c> or <c>inTransit</c>, has no <c>nearbyNotifiedAt</c>, the check-in is on its road
    /// and within <see cref="NearbyKm"/> of <paramref name="dropoff"/>. Null otherwise.
    /// </summary>
    public static double? NearbyKmOf(
        Dictionary<string, object> delivery, TrackPoint? dropoff, TrackPoint fix, DateTime capturedAtUtc)
    {
        if (Text(delivery, "status") is not ("pickedUp" or "inTransit")
            || !string.IsNullOrEmpty(Text(delivery, NearbyNotifiedAtField))
            || dropoff == null
            || !CapturedOnRoad(delivery, capturedAtUtc))
        {
            return null;
        }

        var km = DistanceKm(dropoff, fix);
        return km <= NearbyKm ? km : null;
    }

    /// <summary>
    /// The <c>minutes</c> of <c>truck_nearby</c>: from <paramref name="nowUtc"/> to the start of the
    /// arrival window (<see cref="TripTrack.ArrivalWindow"/>, as the track shows it), rounded to 5,
    /// never below 0. Null without a window.
    /// </summary>
    public static int? MinutesUntil(string? windowFrom, DateTime nowUtc)
    {
        if (!LocationPingRules.TryParseUtc(windowFrom, out var from))
        {
            return null;
        }

        var minutes = Math.Max(0, (from - nowUtc).TotalMinutes);
        return (int)(Math.Round(minutes / 5, MidpointRounding.AwayFromZero) * 5);
    }

    /// <summary>
    /// Whether two check-ins of a trip are an impossible jump: more than <see cref="JumpMinKm"/> apart
    /// (great circle) at more than <see cref="JumpKmh"/>. Two check-ins captured at the same moment and
    /// that far apart are a jump too.
    /// </summary>
    public static bool IsJump(TrackFix a, TrackFix b, out double km, out double kmh)
    {
        km = GpsValidator.HaversineDistance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        var hours = Math.Abs((b.CapturedAtUtc - a.CapturedAtUtc).TotalHours);
        kmh = hours > 0 ? km / hours : double.PositiveInfinity;
        return km > JumpMinKm && kmh > JumpKmh;
    }

    /// <summary>
    /// The check-ins captured just before and just after <paramref name="capturedAtUtc"/> among the
    /// trip's <paramref name="fixes"/> (in capture order): the pairs a new check-in makes consecutive.
    /// A late offline check-in lands between two that are already stored, so both pairs are new.
    /// </summary>
    public static (TrackFix? Before, TrackFix? After) Neighbours(IReadOnlyList<TrackFix> fixes, DateTime capturedAtUtc)
    {
        TrackFix? before = null;
        TrackFix? after = null;
        foreach (var fix in fixes)
        {
            if (fix.CapturedAtUtc < capturedAtUtc)
            {
                before = fix;
            }
            else if (fix.CapturedAtUtc > capturedAtUtc)
            {
                after = fix;
                break;
            }
        }

        return (before, after);
    }

    /// <summary>A distance at one decimal (half away from zero on the decimal value), as the push and the stamp carry it.</summary>
    public static double RoundKm(double km) => TripTrack.Round(km, 1);

    /// <summary>One decimal, invariant (<c>2.4</c>).</summary>
    public static string FormatKm(double km) => RoundKm(km).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Great circle distance in kilometres.</summary>
    public static double DistanceKm(TrackPoint a, TrackPoint b)
        => GpsValidator.HaversineDistance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    private static string? Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;
}

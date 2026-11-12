using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>A check-in of the trucker's own phone that counts as evidence of where the truck was (FUP-203).</summary>
public sealed record TruckerCheckIn(DateTime CapturedAtUtc, double Latitude, double Longitude);

/// <summary>What the trucker's own check-ins say about a confirmed delivery (FUP-203).</summary>
public enum TruckerGpsOutcome
{
    /// <summary>No road check-in of the trucker in the delivery's window, or none fresh: no evidence, no row.</summary>
    NoEvidence,

    /// <summary>Rule 4 on the pickup point and the trucker's last check-in does not flag.</summary>
    Consistent,

    /// <summary>Neither the order's own drop-off point nor the trip's destination point exists: nothing to judge by.</summary>
    NoDropoffPoint,

    /// <summary>A road check-in in the window was within the drop-off radius: the truck reached the drop-off.</summary>
    ReachedDropoff,

    /// <summary>From its last check-in the truck could still have reached the drop-off by the confirmation.</summary>
    CouldReachDropoff,

    /// <summary>The trucker's own evidence is anomalous: the trucker's row is written (through the attribution guard).</summary>
    Anomaly
}

/// <summary>The outcome, and for an anomaly the signal row's description.</summary>
public sealed record TruckerGpsVerdict(TruckerGpsOutcome Outcome, string? Description = null)
{
    public bool IsAnomaly => Outcome == TruckerGpsOutcome.Anomaly;
}

/// <summary>
/// FUP-203 (fraud-detection-architecture.md Rule 4 and section 15.3): the trucker's <c>GPS_ANOMALY</c> row is
/// judged only from the trucker's own phone, never from the drop-off coordinates the confirming buyer's phone
/// sends. Rule 4 with the trucker's last check-in (live-trip-api.md section 1) in place of the buyer's drop-off:
/// the same thresholds (an inter-woreda trip whose two points are under 1 km apart, or identical points),
/// against the stored pickup scan point. Two guards make sure that check-in stands for where the truck was when
/// the buyer confirmed: no road check-in of the delivery's window was near the drop-off, and from the last one
/// the truck could not have reached the drop-off by the confirmation at a speed no truck reaches. Pure: the
/// caller reads the window's rows, the trip and the order.
/// </summary>
public static class TruckerGpsEvidence
{
    /// <summary>
    /// The last road check-in counts only when taken at most this long before the confirmation: the hourly
    /// cadence plus half an hour (live-trip.md 5.2), the admin trail's gap (live-trip-api.md section 3). An
    /// older one says nothing of where the truck was at the confirmation.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(90);

    /// <summary>The confirm's drop-off radius (the 5 km of the delivered GPS check).</summary>
    public const double DropoffRadiusKm = 5.0;

    /// <summary>Pages of the window read before it stops. A window holds a few hundred rows at most (20 fixes an hour).</summary>
    public const int MaxWindowPages = 4;

    /// <summary>
    /// Check-ins the trucker's phone takes on the road (live-trip-api.md section 1 <c>source</c>). <c>scan</c> and
    /// <c>start</c> are taken at a pickup scan (the trucker's phone scans only at pickups, and a "scans only" trip
    /// still records them), so they say nothing of where the truck was when the buyer confirmed.
    /// </summary>
    private static readonly HashSet<string> RoadSources = new(StringComparer.Ordinal)
    {
        "hourly", "near_stop", "manual", "arrived"
    };

    /// <summary>Phone clock skew the ingest accepts: a check-in up to this long after the confirmation still counts.</summary>
    public static TimeSpan ClockSkew => LocationPingRules.MaxFutureSkew;

    /// <summary>
    /// The trip-locations sort keys of the delivery's window, from <paramref name="pickedUpAtUtc"/> to
    /// <paramref name="confirmedAtUtc"/> plus <see cref="ClockSkew"/>, both included (every kind of the last
    /// millisecond: <c>~</c> sorts after each kind).
    /// </summary>
    public static (string From, string To) WindowKeys(DateTime pickedUpAtUtc, DateTime confirmedAtUtc)
        => (LocationPingRules.FormatUtc(pickedUpAtUtc), LocationPingRules.FormatUtc(confirmedAtUtc + ClockSkew) + "#~");

    /// <summary>
    /// The trucker's road check-ins in the delivery's window, in capture order: <c>fix</c> rows of
    /// <paramref name="truckerId"/> with coordinates, precise (<see cref="CheckInEffects.MaxAccuracyM"/>, invariant
    /// 6 of live-trip-api.md), not flagged as mocked by the phone, from a road source, captured from the pickup to
    /// the confirmation plus the clock skew.
    /// </summary>
    public static List<TruckerCheckIn> RoadCheckIns(
        IEnumerable<TripLocationRow> rows, string truckerId, DateTime pickedUpAtUtc, DateTime confirmedAtUtc)
    {
        var until = confirmedAtUtc + ClockSkew;
        var checkIns = new List<TruckerCheckIn>();
        foreach (var row in rows)
        {
            if (!TripTrack.TryParseSortKey(row.SortKey, out var capturedAt, out var kind)
                || kind != LocationPingRules.KindFix
                || !string.Equals(row.TruckerId, truckerId, StringComparison.Ordinal)
                || row.Latitude is not { } latitude
                || row.Longitude is not { } longitude
                || row.AccuracyM is not { } accuracy
                || accuracy > CheckInEffects.MaxAccuracyM
                || row.Mocked == true
                || row.Source is not { } source
                || !RoadSources.Contains(source)
                || capturedAt < pickedUpAtUtc
                || capturedAt > until)
            {
                continue;
            }

            checkIns.Add(new TruckerCheckIn(capturedAt, latitude, longitude));
        }

        checkIns.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        return checkIns;
    }

    /// <summary>The last road check-in when it is fresh (<see cref="Freshness"/>), else null.</summary>
    public static TruckerCheckIn? LastFresh(IReadOnlyList<TruckerCheckIn> checkIns, DateTime confirmedAtUtc)
        => checkIns.Count > 0 && confirmedAtUtc - checkIns[^1].CapturedAtUtc <= Freshness ? checkIns[^1] : null;

    /// <summary>Rule 4 on the stored pickup point and the trucker's check-in, with the trip's woredas.</summary>
    public static FraudCheckResult PickupRule(TrackPoint pickup, TruckerCheckIn checkIn, string fromWoreda, string toWoreda)
        => FraudDetectionService.GpsConsistency(
            pickup.Latitude, pickup.Longitude, checkIn.Latitude, checkIn.Longitude, fromWoreda, toWoreda);

    /// <summary>
    /// The verdict on a confirmed delivery. <paramref name="dropoffPoints"/> are the points the confirm accepts
    /// (the order's own drop-off point and the trip's destination point, either may be missing): the nearer one
    /// counts, so a point the buyer chose can only clear the trucker, never flag them.
    /// <paramref name="maxTruckKmh"/> is the impossible delivery time speed (FRD-09, 120 km/h by default).
    /// </summary>
    public static TruckerGpsVerdict Judge(
        IReadOnlyList<TruckerCheckIn> checkIns, TrackPoint pickup, DateTime confirmedAtUtc,
        string fromWoreda, string toWoreda, IReadOnlyList<TrackPoint> dropoffPoints, double maxTruckKmh)
    {
        if (LastFresh(checkIns, confirmedAtUtc) is not { } last)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoEvidence);
        }

        if (!PickupRule(pickup, last, fromWoreda, toWoreda).IsFlagged)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.Consistent);
        }

        if (dropoffPoints.Count == 0)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoDropoffPoint);
        }

        // Back at the pickup after the drop-off (the next load, or home) while the buyer had not synced the
        // confirm yet: the trucker's own evidence shows the truck reached the drop-off.
        if (checkIns.Any(c => NearestKm(c, dropoffPoints) <= DropoffRadiusKm))
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.ReachedDropoff);
        }

        // Loaded for an hour after the scan, then drove: the last check-in is at the farm, but there was time.
        // The clock skew is added to the time left, so a phone clock running ahead never makes it shorter.
        var sinceLast = confirmedAtUtc - last.CapturedAtUtc;
        var hoursLeft = ((sinceLast > TimeSpan.Zero ? sinceLast : TimeSpan.Zero) + ClockSkew).TotalHours;
        var toDropoffKm = NearestKm(last, dropoffPoints);
        if (toDropoffKm - DropoffRadiusKm <= maxTruckKmh * hoursLeft)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.CouldReachDropoff);
        }

        var minutes = (int)Math.Round(Math.Max(0, sinceLast.TotalMinutes), MidpointRounding.AwayFromZero);
        var fromPickupKm = GpsValidator.HaversineDistance(pickup.Latitude, pickup.Longitude, last.Latitude, last.Longitude);
        return new TruckerGpsVerdict(TruckerGpsOutcome.Anomaly, string.Create(CultureInfo.InvariantCulture,
            $"Trucker check-in {minutes} min before the confirmation: {fromPickupKm:0.00} km from the pickup point, "
            + $"{toDropoffKm:0.0} km from the drop-off point."));
    }

    private static double NearestKm(TruckerCheckIn checkIn, IReadOnlyList<TrackPoint> points)
        => points.Min(p => GpsValidator.HaversineDistance(checkIn.Latitude, checkIn.Longitude, p.Latitude, p.Longitude));
}

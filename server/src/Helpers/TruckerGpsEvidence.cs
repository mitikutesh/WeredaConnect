using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>A check-in of the trucker's own phone that counts as evidence of where the truck was (FUP-203).</summary>
public sealed record TruckerCheckIn(DateTime CapturedAtUtc, double Latitude, double Longitude);

/// <summary>
/// The trucker's own drop-off evidence of ESC-36 on the order (FUP-245): the drop-off check-in, or without one the
/// out-of-range attempt (<c>IsAttempt</c>). <c>Measured</c>: a check-in measured within 5 km of a drop-off point
/// fixed at pickup.
/// </summary>
public sealed record TruckerDropoffEvidence(DateTime ReceivedAtUtc, bool IsAttempt, bool Measured);

/// <summary>What the trucker's own check-ins say about a confirmed delivery (FUP-203, FUP-245).</summary>
public enum TruckerGpsOutcome
{
    /// <summary>No road check-in of the trucker in the delivery's window, or none fresh: no evidence, no row.</summary>
    NoEvidence,

    /// <summary>Rule 4 on the pickup point does not flag some check-in of the window: the truck left the pickup.</summary>
    Consistent,

    /// <summary>
    /// The trip has no destination point (a trip from before trip-listing-api 2.1.0): only the buyer's order point
    /// would be left to judge by, and it may only clear. FUP-245: also the trucker's drop-off check-in that had no
    /// point to be measured against (none was fixed at pickup).
    /// </summary>
    NoDropoffPoint,

    /// <summary>A road check-in in the window was within the drop-off radius: the truck reached the drop-off.</summary>
    ReachedDropoff,

    /// <summary>From its last check-in the truck could still have reached the drop-off by the confirmation.</summary>
    CouldReachDropoff,

    /// <summary>
    /// FUP-245: the trucker's own drop-off check-in (ESC-36) was within 5 km of a drop-off point (fixed at pickup, or read live when none was):
    /// the truck reached the drop-off.
    /// </summary>
    CheckedInAtDropoff,

    /// <summary>
    /// FUP-245: only the trucker's out-of-range drop-off attempt (ESC-36, FUP-204). Not evidence of a fake by itself:
    /// the point it missed may be the buyer's own pin, so no row.
    /// </summary>
    OutOfRangeAttempt,

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
/// sends. Rule 4 with the trucker's check-ins (live-trip-api.md section 1) in place of the buyer's drop-off: the
/// same thresholds (an inter-woreda trip whose two points are under 1 km apart, or identical points), against
/// the stored pickup scan point, for every check-in of the delivery's window (the truck never left the pickup),
/// the last one fresh. Two guards make sure the truck was not at the drop-off when the buyer confirmed: no
/// check-in of the window was near the drop-off, and from the last one the truck could not have reached the
/// trip's destination by the confirmation at a speed no truck reaches. Pure: the caller reads the window's rows,
/// the trip and the order.
/// <para>
/// FUP-245: with <c>dropoffClassification</c> on, the trucker's drop-off check-in of ESC-36 (taken by the
/// trucker's phone at the handoff) is read first and, when the order holds one for the delivery, decides in place
/// of the road check-ins (<see cref="DropoffEvidence"/>, <see cref="JudgeDropoff"/>); without one the road
/// check-ins decide as above.
/// </para>
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
    /// Whether the truck never left the pickup by its own evidence: Rule 4 flags the stored pickup point against
    /// every counted check-in of the window, not only the last one. The buyer chooses when the confirm reaches
    /// the server (a late sync, offline or on purpose), so the last check-in alone could be a truck that drove to
    /// the drop-off, came back to the same farm for its next load and was judged there; any check-in away from
    /// the pickup clears the trucker, and a late sync only adds such check-ins.
    /// </summary>
    public static bool NeverLeftPickup(
        IReadOnlyList<TruckerCheckIn> checkIns, TrackPoint pickup, string fromWoreda, string toWoreda)
        => checkIns.Count > 0 && checkIns.All(c => PickupRule(pickup, c, fromWoreda, toWoreda).IsFlagged);

    /// <summary>
    /// The verdict on a confirmed delivery. <paramref name="tripDestination"/> is the trip's destination point,
    /// which the trucker set: without it there is nothing the buyer cannot move to judge by, so no row.
    /// <paramref name="orderPoint"/> is the order's own drop-off point (the buyer's choice, locked at pickup),
    /// used only to clear: the nearer of the two counts. <paramref name="maxTruckKmh"/> is the impossible
    /// delivery time speed (FRD-09, 120 km/h by default).
    /// </summary>
    public static TruckerGpsVerdict Judge(
        IReadOnlyList<TruckerCheckIn> checkIns, TrackPoint pickup, DateTime confirmedAtUtc,
        string fromWoreda, string toWoreda, TrackPoint? tripDestination, TrackPoint? orderPoint, double maxTruckKmh)
    {
        if (LastFresh(checkIns, confirmedAtUtc) is not { } last)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoEvidence);
        }

        if (!NeverLeftPickup(checkIns, pickup, fromWoreda, toWoreda))
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.Consistent);
        }

        if (tripDestination == null)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoDropoffPoint);
        }

        var dropoffPoints = orderPoint == null ? new[] { tripDestination } : new[] { tripDestination, orderPoint };

        // A drop-off a few km from the farm: a check-in near it is the truck at the drop-off.
        if (checkIns.Any(c => NearestKm(c, dropoffPoints) <= DropoffRadiusKm))
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.ReachedDropoff);
        }

        // Loaded for an hour after the scan, then drove: every check-in is at the farm, but there was time.
        // The clock skew is added to the time left, so a phone clock up to 2 minutes ahead never makes it shorter.
        // A phone clock further ahead whose check-in synced late is not caught (fraud spec Rule 4, residual).
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

    /// <summary>
    /// FUP-245: the trucker's own drop-off evidence for the delivery on <paramref name="order"/> (read strongly
    /// consistent at the confirmation): the stored drop-off check-in (<see cref="DropoffJudgement.CheckInField"/>),
    /// else the kept out-of-range attempt (<see cref="DropoffJudgement.AttemptField"/>). An entry counts only on an
    /// order stamped at pickup whose active delivery is <paramref name="deliveryId"/>, sent by
    /// <paramref name="truckerId"/> (the delivery's trucker as stored), and received by the server in the
    /// delivery's window (from <paramref name="pickedUpAtUtc"/> to the confirmation plus <see cref="ClockSkew"/>).
    /// A check-in kept after T8 counts too: it is still the trucker's phone at the drop-off before the confirm.
    /// Null when there is none: the road check-ins decide.
    /// </summary>
    public static TruckerDropoffEvidence? DropoffEvidence(
        IReadOnlyDictionary<string, object>? order, string deliveryId, string truckerId,
        DateTime pickedUpAtUtc, DateTime confirmedAtUtc)
    {
        if (order == null || deliveryId.Length == 0 || truckerId.Length == 0
            || !DropoffJudgement.IsStamped(order) || Str(order, "activeDeliveryId") != deliveryId)
        {
            return null;
        }

        var until = confirmedAtUtc + ClockSkew;
        TruckerDropoffEvidence? Of(IReadOnlyDictionary<string, object>? entry, bool attempt)
            => entry != null
               && Str(entry, PickupJudgement.EntryBy) == truckerId
               && DropoffJudgement.ReceivedAt(entry) is { } received
               && received >= pickedUpAtUtc
               && received <= until
                ? new TruckerDropoffEvidence(received, attempt, !attempt && DropoffJudgement.IsMeasured(entry))
                : null;

        return Of(DropoffJudgement.CheckInOf(order), attempt: false) ?? Of(DropoffJudgement.AttemptOf(order), attempt: true);
    }

    /// <summary>
    /// FUP-245: the verdict from the trucker's drop-off evidence, in place of the road check-ins. It never writes
    /// the row. ESC-36 keeps a check-in only within 5 km of a drop-off point (fixed at pickup, or read live when none was) (the truck reached the
    /// drop-off: <see cref="TruckerGpsOutcome.CheckedInAtDropoff"/>) or, with no point fixed, unmeasured (no
    /// destination point: <see cref="TruckerGpsOutcome.NoDropoffPoint"/>, as on the road check-ins). An
    /// out-of-range attempt is not evidence of a fake by itself: the point it missed may be the buyer's own pin
    /// (FUP-204), and the buyer's input never decides the trucker's row (<see cref="TruckerGpsOutcome.OutOfRangeAttempt"/>).
    /// </summary>
    public static TruckerGpsVerdict JudgeDropoff(TruckerDropoffEvidence evidence)
        => new(evidence.IsAttempt ? TruckerGpsOutcome.OutOfRangeAttempt
            : evidence.Measured ? TruckerGpsOutcome.CheckedInAtDropoff
            : TruckerGpsOutcome.NoDropoffPoint);

    private static double NearestKm(TruckerCheckIn checkIn, IReadOnlyList<TrackPoint> points)
        => points.Min(p => GpsValidator.HaversineDistance(checkIn.Latitude, checkIn.Longitude, p.Latitude, p.Longitude));

    private static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}

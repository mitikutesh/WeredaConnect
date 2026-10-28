using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>A point on the map (WGS84), unrounded.</summary>
public sealed record TrackPoint(double Latitude, double Longitude);

/// <summary>A stored check-in (<c>fix</c>) of the trip, with its coordinates at 4 decimals.</summary>
public sealed record TrackFix(DateTime CapturedAtUtc, double Latitude, double Longitude, string ReceivedAt, double? AccuracyM, string? Source);

/// <summary>Another order's stop on the same truck. Unrounded for the arrival maths; rounded on output.</summary>
public sealed record TrackStop(string Kind, double Latitude, double Longitude, bool Done);

/// <summary>The arrival window and the distance left (great circle x 1.3).</summary>
public sealed record TrackArrival(DateTime FromUtc, DateTime ToUtc, double RemainingKm);

/// <summary>Where a delivery is in its life, for the track.</summary>
public enum TrackPhase
{
    /// <summary>Not picked up yet: no position at all.</summary>
    NotStarted,

    /// <summary><c>pickedUp</c> or <c>inTransit</c>: the window from pickup to now.</summary>
    OnRoad,

    /// <summary><c>delivered</c>, <c>cancelled</c> or <c>disputed</c>: a summary, no coordinates.</summary>
    Ended
}

/// <summary>
/// LT-06: builds the track of one delivery (live-trip-api.md section 2, live-trip.md sections 6.2
/// and 7) from rows the caller read. Pure: the caller passes the server clock, so the window and the
/// arrival maths are deterministic in tests. Never writes anything.
/// </summary>
public static class TripTrack
{
    public const string SharingNotStarted = "not_started";
    public const string SharingLive = "live";
    public const string SharingPaused = "paused";
    public const string SharingDeclined = "declined";
    public const string SharingEnded = "ended";

    public const string StopPickup = "pickup";
    public const string StopDropoff = "dropoff";

    /// <summary>Party coordinates: 3 decimals (about 110 m).</summary>
    public const int PartyDecimals = 3;

    /// <summary>Other stops: 2 decimals (about 1 km).</summary>
    public const int OtherStopDecimals = 2;

    /// <summary>Roads are longer than the great circle.</summary>
    public const double RoadFactor = 1.3;

    /// <summary>Added for each other undelivered stop nearer to the truck than this drop-off.</summary>
    public const double MinutesPerStop = 20;

    public const double MinKmh = 20;
    public const double MaxKmh = 60;

    /// <summary>The speed when the window has only one fix.</summary>
    public const double SingleFixKmh = 40;

    /// <summary>The speed is the median of the last this many legs.</summary>
    public const int SpeedLegs = 3;

    public const double BaseHalfWidthMinutes = 10;
    public const double MaxHalfWidthMinutes = 90;

    /// <summary>The window never collapses: when the clamp to now leaves less, it is this wide.</summary>
    public const double MinWidthMinutes = 10;

    private static readonly TimeSpan RoundStep = TimeSpan.FromMinutes(5);

    /// <summary>Where the delivery is in its life: ended, on the road (picked up with a time), or not started.</summary>
    public static TrackPhase PhaseOf(Dictionary<string, object> delivery)
    {
        switch (Text(delivery, "status"))
        {
            case "delivered":
            case "cancelled":
            case "disputed":
                return TrackPhase.Ended;
            case "pickedUp":
            case "inTransit":
                return PickedUpAt(delivery) is null ? TrackPhase.NotStarted : TrackPhase.OnRoad;
            default:
                return TrackPhase.NotStarted;
        }
    }

    /// <summary>
    /// The track. <paramref name="pickup"/> is the delivery's gpsPickup or its listing location,
    /// <paramref name="dropoff"/> the drop-off point when one is known, <paramref name="otherStops"/>
    /// the other orders' stops on the trip. Rows outside the delivery's window never reach the answer.
    /// </summary>
    public static DeliveryTrackResponse Build(
        Dictionary<string, object> delivery,
        IReadOnlyList<TripLocationRow> rows,
        TrackPoint? pickup,
        TrackPoint? dropoff,
        IReadOnlyList<TrackStop> otherStops,
        DateTime nowUtc)
    {
        var deliveryId = Text(delivery, "deliveryId") ?? string.Empty;
        var asOf = LocationPingRules.FormatUtc(nowUtc);
        var pickedUpAt = PickedUpAt(delivery);
        var pickedUpText = pickedUpAt is { } p ? LocationPingRules.FormatUtc(p) : null;

        switch (PhaseOf(delivery))
        {
            case TrackPhase.Ended:
                return new DeliveryTrackResponse
                {
                    DeliveryId = deliveryId,
                    Sharing = SharingEnded,
                    AsOf = asOf,
                    Trail = new List<TrackTrailPointDto>(),
                    Pauses = new List<TrackPauseDto>(),
                    // After delivery: no coordinates anywhere, only the summary.
                    Pickup = new TrackPickupDto { At = pickedUpText },
                    Dropoff = new TrackDropoffDto(),
                    OtherStops = new List<TrackStopDto>(),
                    Summary = SummaryOf(delivery, rows, pickedUpAt, nowUtc)
                };

            case TrackPhase.NotStarted:
                return new DeliveryTrackResponse
                {
                    DeliveryId = deliveryId,
                    Sharing = SharingNotStarted,
                    AsOf = asOf,
                    Trail = new List<TrackTrailPointDto>(),
                    Pauses = new List<TrackPauseDto>(),
                    Pickup = PickupDto(pickup, null),
                    Dropoff = DropoffDto(dropoff),
                    // Nothing of the truck before pickup, not even which stops it has done.
                    OtherStops = new List<TrackStopDto>()
                };
        }

        var from = pickedUpAt!.Value;
        // A phone clock may run up to 2 minutes ahead (the ingest accepts it).
        var windowEnd = nowUtc + LocationPingRules.MaxFutureSkew;
        var fixes = FixesIn(rows, from, windowEnd);
        var events = EventsUntil(rows, windowEnd);

        var latest = events.Count > 0 ? events[^1] : null;
        string sharing;
        DateTime? pausedUntil = null;
        if (latest?.Kind == LocationPingRules.KindDeclined)
        {
            sharing = SharingDeclined;
        }
        else if (latest?.Kind == LocationPingRules.KindPaused && latest.PausedUntilUtc is { } until && until > nowUtc)
        {
            sharing = SharingPaused;
            pausedUntil = until;
        }
        else
        {
            sharing = SharingLive;
        }

        var arrival = ArrivalWindow(fixes, dropoff, otherStops, nowUtc, pausedUntil);
        var last = fixes.Count > 0 ? fixes[^1] : null;

        return new DeliveryTrackResponse
        {
            DeliveryId = deliveryId,
            Sharing = sharing,
            PausedUntil = pausedUntil is { } pu ? LocationPingRules.FormatUtc(pu) : null,
            AsOf = asOf,
            LastFix = last == null
                ? null
                : new TrackLastFixDto
                {
                    Latitude = Round(last.Latitude, PartyDecimals),
                    Longitude = Round(last.Longitude, PartyDecimals),
                    CapturedAt = LocationPingRules.FormatUtc(last.CapturedAtUtc),
                    ReceivedAt = last.ReceivedAt,
                    AccuracyM = last.AccuracyM
                },
            Trail = fixes.Select(f => new TrackTrailPointDto
            {
                Latitude = Round(f.Latitude, PartyDecimals),
                Longitude = Round(f.Longitude, PartyDecimals),
                CapturedAt = LocationPingRules.FormatUtc(f.CapturedAtUtc),
                ReceivedAt = f.ReceivedAt,
                Source = f.Source
            }).ToList(),
            Pauses = PausesOf(events, from),
            Pickup = PickupDto(pickup, pickedUpText),
            Dropoff = DropoffDto(dropoff),
            OtherStops = otherStops.Select(s => new TrackStopDto
            {
                Kind = s.Kind,
                Latitude = Round(s.Latitude, OtherStopDecimals),
                Longitude = Round(s.Longitude, OtherStopDecimals),
                Done = s.Done
            }).ToList(),
            Eta = arrival == null
                ? null
                : new TrackEtaDto
                {
                    From = LocationPingRules.FormatUtc(arrival.FromUtc),
                    To = LocationPingRules.FormatUtc(arrival.ToUtc)
                },
            RemainingKm = arrival == null ? null : Math.Round(arrival.RemainingKm, 1, MidpointRounding.AwayFromZero)
        };
    }

    /// <summary>
    /// The arrival window (live-trip.md section 6.2). Distance left = great circle (last fix, drop-off)
    /// x 1.3; travel = distance / speed, plus 20 minutes for each other undelivered stop nearer to the
    /// truck than the drop-off; speed = median of the last up to 3 legs, clamped to 20 to 60 km/h (40
    /// with one fix); centre = last fix time (or the end of a running pause, when later) + travel;
    /// half-width = 10 min + a quarter of the last fix's age, at most 90 min; both ends on 5 minute
    /// marks; the start never before now, and the window at least 10 minutes wide after that clamp.
    /// Null without a fix or a drop-off point.
    /// </summary>
    public static TrackArrival? ArrivalWindow(
        IReadOnlyList<TrackFix> fixes,
        TrackPoint? dropoff,
        IReadOnlyList<TrackStop> otherStops,
        DateTime nowUtc,
        DateTime? pausedUntilUtc)
    {
        if (fixes.Count == 0 || dropoff == null)
        {
            return null;
        }

        var last = fixes[^1];
        var toDropoff = GpsValidator.HaversineDistance(last.Latitude, last.Longitude, dropoff.Latitude, dropoff.Longitude);
        var remainingKm = toDropoff * RoadFactor;
        var nearerStops = otherStops.Count(s => !s.Done
            && GpsValidator.HaversineDistance(last.Latitude, last.Longitude, s.Latitude, s.Longitude) < toDropoff);

        var travelMinutes = remainingKm / SpeedKmh(fixes) * 60 + nearerStops * MinutesPerStop;
        var start = pausedUntilUtc is { } until && until > last.CapturedAtUtc ? until : last.CapturedAtUtc;
        var centre = start + TimeSpan.FromMinutes(travelMinutes);

        var ageMinutes = Math.Max(0, (nowUtc - last.CapturedAtUtc).TotalMinutes);
        var half = TimeSpan.FromMinutes(Math.Min(BaseHalfWidthMinutes + ageMinutes / 4, MaxHalfWidthMinutes));

        var from = RoundToStep(centre - half);
        var to = RoundToStep(centre + half);
        var earliest = CeilToStep(nowUtc);
        if (from < earliest)
        {
            from = earliest;
        }

        var narrowest = from + TimeSpan.FromMinutes(MinWidthMinutes);
        if (to < narrowest)
        {
            to = narrowest;
        }

        return new TrackArrival(from, to, remainingKm);
    }

    /// <summary>
    /// The truck's speed: the median of the last up to <see cref="SpeedLegs"/> legs (great circle over
    /// time), clamped to 20 to 60 km/h; 40 with a single fix.
    /// </summary>
    public static double SpeedKmh(IReadOnlyList<TrackFix> fixes)
    {
        var speeds = new List<double>();
        for (var i = fixes.Count - 1; i > 0 && speeds.Count < SpeedLegs; i--)
        {
            var hours = (fixes[i].CapturedAtUtc - fixes[i - 1].CapturedAtUtc).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var km = GpsValidator.HaversineDistance(
                fixes[i - 1].Latitude, fixes[i - 1].Longitude, fixes[i].Latitude, fixes[i].Longitude);
            speeds.Add(km / hours);
        }

        if (speeds.Count == 0)
        {
            return SingleFixKmh;
        }

        speeds.Sort();
        var middle = speeds.Count / 2;
        var median = speeds.Count % 2 == 1 ? speeds[middle] : (speeds[middle - 1] + speeds[middle]) / 2;
        return Math.Clamp(median, MinKmh, MaxKmh);
    }

    /// <summary>
    /// Another delivery's stops on the trip, from its planned points only (never where the truck
    /// really stopped): its pickup (its listing location) and its drop-off (<see cref="PlannedDropoff"/>),
    /// each with whether it is done. A stop done before <paramref name="windowStartUtc"/> (this delivery's
    /// pickup) is outside the viewer's window and left out, and so is a done stop whose time is unknown.
    /// A cancelled delivery has no stops left; a stop without coordinates is left out. Nothing that
    /// identifies the order is kept.
    /// </summary>
    public static IEnumerable<TrackStop> StopsOf(
        Dictionary<string, object> other, TrackPoint? plannedPickup, TrackPoint? plannedDropoff, DateTime windowStartUtc)
    {
        var status = Text(other, "status");
        if (status == "cancelled")
        {
            yield break;
        }

        var droppedOff = status is "delivered" or "disputed";
        var pickedUpAtText = Text(other, "pickedUpAt");
        var pickedUp = droppedOff || status is "pickedUp" or "inTransit" || !string.IsNullOrEmpty(pickedUpAtText);

        if (plannedPickup != null && InWindow(pickedUp, pickedUpAtText, windowStartUtc))
        {
            yield return new TrackStop(StopPickup, plannedPickup.Latitude, plannedPickup.Longitude, pickedUp);
        }

        if (plannedDropoff != null && InWindow(droppedOff, Text(other, "confirmedAt"), windowStartUtc))
        {
            yield return new TrackStop(StopDropoff, plannedDropoff.Latitude, plannedDropoff.Longitude, droppedOff);
        }
    }

    /// <summary>
    /// The planned drop-off point of a delivery: the trip's destination point today, the reference the
    /// delivered GPS check uses (the server has no coordinates for the buyer's registered place). The
    /// one place LT-12 switches to the order's own point; <paramref name="delivery"/> is for that.
    /// </summary>
    public static TrackPoint? PlannedDropoff(Dictionary<string, object>? trip, Dictionary<string, object> delivery)
        => PointOf(trip, "destinationLatitude", "destinationLongitude");

    /// <summary>A stop still to do, or one done at a known time no earlier than the window's start.</summary>
    private static bool InWindow(bool done, string? doneAtText, DateTime windowStartUtc)
        => !done || (LocationPingRules.TryParseUtc(doneAtText, out var doneAt) && doneAt >= windowStartUtc);

    /// <summary>Rounds half away from zero on the decimal value (so 8.7525 gives 8.753, not 8.752).</summary>
    public static double Round(double value, int decimals)
        => (double)Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);

    private static TrackSummaryDto SummaryOf(
        Dictionary<string, object> delivery, IReadOnlyList<TripLocationRow> rows, DateTime? pickedUpAt, DateTime nowUtc)
    {
        if (pickedUpAt is not { } from)
        {
            return new TrackSummaryDto { DistanceKm = 0, DurationMin = 0, Fixes = 0 };
        }

        var to = LocationPingRules.LeftRoadAt(delivery, Text(delivery, "status") ?? string.Empty) ?? nowUtc;
        var fixes = FixesIn(rows, from, to);
        var km = 0.0;
        for (var i = 1; i < fixes.Count; i++)
        {
            km += GpsValidator.HaversineDistance(
                fixes[i - 1].Latitude, fixes[i - 1].Longitude, fixes[i].Latitude, fixes[i].Longitude);
        }

        return new TrackSummaryDto
        {
            DistanceKm = Math.Round(km, 1, MidpointRounding.AwayFromZero),
            DurationMin = (int)Math.Max(0, Math.Round((to - from).TotalMinutes, MidpointRounding.AwayFromZero)),
            Fixes = fixes.Count
        };
    }

    /// <summary>The fixes captured from <paramref name="fromUtc"/> to <paramref name="toUtc"/> (both included), in capture order.</summary>
    private static List<TrackFix> FixesIn(IReadOnlyList<TripLocationRow> rows, DateTime fromUtc, DateTime toUtc)
    {
        var fixes = new List<TrackFix>();
        foreach (var row in rows)
        {
            if (!TryParseSortKey(row.SortKey, out var capturedAt, out var kind)
                || kind != LocationPingRules.KindFix
                || row.Latitude is not { } lat
                || row.Longitude is not { } lng
                || capturedAt < fromUtc
                || capturedAt > toUtc)
            {
                continue;
            }

            fixes.Add(new TrackFix(capturedAt, lat, lng, row.ReceivedAt, row.AccuracyM, row.Source));
        }

        fixes.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        return fixes;
    }

    private sealed record TrackEvent(DateTime CapturedAtUtc, string Kind, DateTime? PausedUntilUtc);

    /// <summary>
    /// The trip's sharing events (paused, resumed, declined) up to <paramref name="toUtc"/>, in capture
    /// order. The whole trip counts, not only the window: a driver declines once per trip, at the first
    /// pickup. Events carry no coordinates.
    /// </summary>
    private static List<TrackEvent> EventsUntil(IReadOnlyList<TripLocationRow> rows, DateTime toUtc)
    {
        var events = new List<TrackEvent>();
        foreach (var row in rows)
        {
            if (!TryParseSortKey(row.SortKey, out var capturedAt, out var kind)
                || kind == LocationPingRules.KindFix
                || capturedAt > toUtc)
            {
                continue;
            }

            DateTime? pausedUntil = LocationPingRules.TryParseUtc(row.PausedUntil, out var until) ? until : null;
            events.Add(new TrackEvent(capturedAt, kind, pausedUntil));
        }

        // Stable: rows arrive in sort key order, so equal times keep that order.
        return events.OrderBy(e => e.CapturedAtUtc).ToList();
    }

    /// <summary>
    /// The pauses: each <c>paused</c> event runs to its <c>pausedUntil</c>, or to the next event when
    /// that comes first (a <c>resumed</c>, a new pause, a decline). Only pauses that reach past pickup,
    /// and those start no earlier than pickup.
    /// </summary>
    private static List<TrackPauseDto> PausesOf(List<TrackEvent> events, DateTime pickedUpAt)
    {
        var spans = new List<(DateTime From, DateTime Until)>();
        (DateTime From, DateTime Until)? current = null;

        foreach (var e in events)
        {
            if (current is { } running)
            {
                spans.Add(e.CapturedAtUtc < running.Until ? (running.From, e.CapturedAtUtc) : running);
                current = null;
            }

            if (e.Kind == LocationPingRules.KindPaused && e.PausedUntilUtc is { } until)
            {
                current = (e.CapturedAtUtc, until);
            }
        }

        if (current is { } open)
        {
            spans.Add(open);
        }

        return spans
            .Where(s => s.Until > pickedUpAt)
            .Select(s => new TrackPauseDto
            {
                From = LocationPingRules.FormatUtc(s.From < pickedUpAt ? pickedUpAt : s.From),
                Until = LocationPingRules.FormatUtc(s.Until)
            })
            .ToList();
    }

    private static TrackPickupDto PickupDto(TrackPoint? point, string? at) => new()
    {
        Latitude = point is null ? null : Round(point.Latitude, PartyDecimals),
        Longitude = point is null ? null : Round(point.Longitude, PartyDecimals),
        At = at
    };

    private static TrackDropoffDto DropoffDto(TrackPoint? point) => new()
    {
        Latitude = point is null ? null : Round(point.Latitude, PartyDecimals),
        Longitude = point is null ? null : Round(point.Longitude, PartyDecimals),
        // LT-12 adds the order's dropoffNote.
        Note = null
    };

    /// <summary>The sort key <c>{capturedAt}#{kind}</c>.</summary>
    private static bool TryParseSortKey(string sortKey, out DateTime capturedAtUtc, out string kind)
    {
        capturedAtUtc = default;
        kind = string.Empty;
        var hash = sortKey.LastIndexOf('#');
        if (hash <= 0 || hash == sortKey.Length - 1
            || !LocationPingRules.TryParseUtc(sortKey[..hash], out capturedAtUtc))
        {
            return false;
        }

        kind = sortKey[(hash + 1)..];
        return true;
    }

    /// <summary>The delivery's <c>pickedUpAt</c> as UTC, or null when missing or unreadable.</summary>
    public static DateTime? PickedUpAt(Dictionary<string, object> delivery)
        => LocationPingRules.TryParseUtc(Text(delivery, "pickedUpAt"), out var at) ? at : null;

    private static DateTime RoundToStep(DateTime utc)
    {
        var step = RoundStep.Ticks;
        return new DateTime((utc.Ticks + step / 2) / step * step, DateTimeKind.Utc);
    }

    private static DateTime CeilToStep(DateTime utc)
    {
        var step = RoundStep.Ticks;
        return new DateTime((utc.Ticks + step - 1) / step * step, DateTimeKind.Utc);
    }

    private static string? Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// A coordinate pair from an item's two number attributes (stored numbers arrive as invariant
    /// text); null when either is missing or not a finite number.
    /// </summary>
    public static TrackPoint? PointOf(Dictionary<string, object>? item, string latitudeKey, string longitudeKey)
    {
        if (item == null
            || NumberOf(item, latitudeKey) is not { } lat
            || NumberOf(item, longitudeKey) is not { } lng)
        {
            return null;
        }

        return new TrackPoint(lat, lng);
    }

    /// <summary>The point stored as a map (<c>{latitude, longitude}</c>) under <paramref name="key"/>, such as gpsPickup.</summary>
    public static TrackPoint? MapPointOf(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value is Dictionary<string, object> map
            ? PointOf(map, "latitude", "longitude")
            : null;

    private static double? NumberOf(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return null;
        }

        double? number = value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
        return number is { } n && !double.IsNaN(n) && !double.IsInfinity(n) ? n : null;
    }
}

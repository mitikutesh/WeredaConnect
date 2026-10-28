using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// LT-13: builds the admin trail of one delivery (live-trip-api.md section 3, live-trip.md sections
/// 4 and 6.4) from rows the caller read: every fix and event of the trip from the delivery's pickup to
/// its delivery (to now while it is on the road) at 4 decimals, the legs between precise fixes with
/// the <c>GPS_JUMP</c> rule of <see cref="CheckInEffects.IsJump"/>, the pauses of
/// <see cref="TripTrack"/>, the gaps and what they add up to. Pure: the caller passes the server
/// clock. Never writes anything.
/// </summary>
public static class AdminTrail
{
    /// <summary>Admin coordinates: 4 decimals (about 11 m), as stored.</summary>
    public const int AdminDecimals = 4;

    /// <summary>
    /// A stretch of the window longer than this without a check-in (paused time left out) is a gap:
    /// the phone checks in about once an hour (live-trip.md section 5.2), plus half an hour of margin.
    /// </summary>
    public const double GapMinutes = 90;

    /// <summary>A check-in received more than this after its capture waited offline ("sent late").</summary>
    public const double LateMinutes = 15;

    private const string SelfPickupDeliveryType = "self_pickup";

    /// <summary>
    /// The trip whose check-ins belong to the delivery; null for a self-pickup, which has no truck (the
    /// sync stores the delivery's own id as its <c>tripId</c>), and for a delivery without one.
    /// </summary>
    public static string? TripIdOf(Dictionary<string, object> delivery)
    {
        var tripId = Text(delivery, "tripId");
        if (string.IsNullOrEmpty(tripId)
            || Text(delivery, "deliveryType") == SelfPickupDeliveryType
            || tripId == Text(delivery, "deliveryId"))
        {
            return null;
        }

        return tripId;
    }

    /// <summary>
    /// The trail. <paramref name="rows"/> are the trip's rows (all of them; the window is cut here).
    /// Before pickup, and for a self-pickup, the lists are empty and the summary is all zeros.
    /// </summary>
    public static AdminDeliveryTrailResponse Build(
        Dictionary<string, object> delivery, IReadOnlyList<TripLocationRow> rows, DateTime nowUtc)
    {
        var status = Text(delivery, "status") ?? string.Empty;
        var tripId = TripIdOf(delivery);
        var pickedUpAt = TripTrack.PickedUpAt(delivery);
        var phase = TripTrack.PhaseOf(delivery);
        var open = phase == TrackPhase.OnRoad;

        // Ended: to the time the delivery left the road (confirmedAt, cancelledAt), as the track's
        // summary. Open: to now, reading the fixes a phone clock up to 2 minutes ahead may stamp.
        DateTime? leftRoad = phase == TrackPhase.Ended && pickedUpAt != null
            ? LocationPingRules.LeftRoadAt(delivery, status) ?? nowUtc
            : null;
        var windowEnd = open ? nowUtc + LocationPingRules.MaxFutureSkew : leftRoad;
        var lastMoment = open ? nowUtc : leftRoad;

        var points = new List<AdminTrailPointDto>();
        var legs = new List<AdminTrailLegDto>();
        var pauses = new List<TrackPauseDto>();
        var gaps = new List<AdminTrailGapDto>();
        var fixes = new List<TrackFix>();
        var declined = false;

        if (pickedUpAt is { } from && windowEnd is { } end && lastMoment is { } last && end >= from)
        {
            points = PointsIn(rows, from, end);
            fixes = TripTrack.FixesIn(rows, from, end);
            legs = LegsOf(fixes);

            var events = TripTrack.EventsUntil(rows, end);
            var spans = TripTrack.PauseSpans(events, from);
            pauses = TripTrack.PausesOf(events, from);

            // The driver answers the consent once per trip, so a decline before this pickup still
            // holds at pickup unless a later event before it (a resume) replaced it.
            var before = events.LastOrDefault(e => e.CapturedAtUtc < from);
            declined = before?.Kind == LocationPingRules.KindDeclined
                       || events.Any(e => e.CapturedAtUtc >= from && e.Kind == LocationPingRules.KindDeclined);

            if (tripId != null)
            {
                gaps = GapsOf(fixes, spans, from, last);
            }
        }

        var speeds = legs.Where(l => l.Kmh != null).Select(l => l.Kmh!.Value).ToList();
        var fixPoints = points.Where(p => p.Kind == LocationPingRules.KindFix).ToList();

        return new AdminDeliveryTrailResponse
        {
            DeliveryId = Text(delivery, "deliveryId") ?? string.Empty,
            TripId = tripId,
            Status = status,
            Open = open,
            From = pickedUpAt is { } p ? LocationPingRules.FormatUtc(p) : null,
            Until = leftRoad is { } l ? LocationPingRules.FormatUtc(l) : null,
            AsOf = LocationPingRules.FormatUtc(nowUtc),
            Pickup = StopOf(delivery, "gpsPickup", pickedUpAt),
            Dropoff = StopOf(
                delivery, "gpsDropoff",
                LocationPingRules.TryParseUtc(Text(delivery, "confirmedAt"), out var confirmedAt) ? confirmedAt : null),
            Points = points,
            Legs = legs,
            Pauses = pauses,
            Gaps = gaps,
            Summary = new AdminTrailSummaryDto
            {
                SharedWholeWay = tripId != null && fixes.Count > 0 && gaps.Count == 0 && !declined,
                Declined = declined,
                Fixes = fixPoints.Count,
                CoarseFixes = fixPoints.Count(f => f.Coarse),
                MockedFixes = fixPoints.Count(f => f.Mocked == true),
                LateFixes = fixPoints.Count(f => f.Late),
                Jumps = legs.Count(leg => leg.Jump),
                Gaps = gaps.Count,
                DistanceKm = TripTrack.Round(legs.Sum(leg => leg.DistanceKm), 1),
                DurationMin = pickedUpAt is { } start && lastMoment is { } stop
                    ? (int)Math.Max(0, Math.Round((stop - start).TotalMinutes, MidpointRounding.AwayFromZero))
                    : 0,
                MaxKmh = speeds.Count > 0 ? speeds.Max() : null
            }
        };
    }

    /// <summary>
    /// The legs: one per pair of consecutive fixes precise enough for the effects of their position
    /// (<see cref="CheckInEffects.PreciseEnough"/>), as the <c>GPS_JUMP</c> check pairs them; a coarse
    /// fix is skipped, so the leg runs from the precise fix before it to the one after.
    /// </summary>
    internal static List<AdminTrailLegDto> LegsOf(IReadOnlyList<TrackFix> fixes)
    {
        var precise = fixes.Where(f => CheckInEffects.PreciseEnough(f.AccuracyM)).ToList();
        var legs = new List<AdminTrailLegDto>();
        for (var i = 1; i < precise.Count; i++)
        {
            var a = precise[i - 1];
            var b = precise[i];
            var jump = CheckInEffects.IsJump(a, b, out var km, out var kmh);
            legs.Add(new AdminTrailLegDto
            {
                FromCapturedAt = LocationPingRules.FormatUtc(a.CapturedAtUtc),
                ToCapturedAt = LocationPingRules.FormatUtc(b.CapturedAtUtc),
                DistanceKm = TripTrack.Round(km, 2),
                Minutes = TripTrack.Round((b.CapturedAtUtc - a.CapturedAtUtc).TotalMinutes, 1),
                Kmh = double.IsInfinity(kmh) ? null : TripTrack.Round(kmh, 1),
                Jump = jump
            });
        }

        return legs;
    }

    /// <summary>
    /// The gaps: from the pickup to the first fix, between fixes (coarse ones too: the phone was
    /// sharing) and from the last fix to <paramref name="lastUtc"/> (the delivery, or now), each stretch
    /// longer than <see cref="GapMinutes"/> once the paused time inside it is left out.
    /// </summary>
    internal static List<AdminTrailGapDto> GapsOf(
        IReadOnlyList<TrackFix> fixes, IReadOnlyList<(DateTime From, DateTime Until)> pauses, DateTime fromUtc, DateTime lastUtc)
    {
        var anchors = new List<DateTime> { fromUtc };
        anchors.AddRange(fixes.Select(f => f.CapturedAtUtc));
        anchors.Add(lastUtc);

        var gaps = new List<AdminTrailGapDto>();
        for (var i = 1; i < anchors.Count; i++)
        {
            var start = anchors[i - 1];
            var stop = anchors[i];
            if (stop <= start)
            {
                continue;
            }

            var paused = pauses.Sum(p => Overlap(start, stop, p.From, p.Until).TotalMinutes);
            var minutes = (stop - start).TotalMinutes - paused;
            if (minutes > GapMinutes)
            {
                gaps.Add(new AdminTrailGapDto
                {
                    From = LocationPingRules.FormatUtc(start),
                    Until = LocationPingRules.FormatUtc(stop),
                    Minutes = (int)Math.Round(minutes, MidpointRounding.AwayFromZero)
                });
            }
        }

        return gaps;
    }

    /// <summary>Every row of the window (fixes with coordinates, and events), in capture order.</summary>
    private static List<AdminTrailPointDto> PointsIn(IReadOnlyList<TripLocationRow> rows, DateTime fromUtc, DateTime toUtc)
    {
        var points = new List<(DateTime CapturedAt, AdminTrailPointDto Point)>();
        foreach (var row in rows)
        {
            if (!TripTrack.TryParseSortKey(row.SortKey, out var capturedAt, out var kind)
                || capturedAt < fromUtc
                || capturedAt > toUtc)
            {
                continue;
            }

            var isFix = kind == LocationPingRules.KindFix;
            if (isFix && (row.Latitude is null || row.Longitude is null))
            {
                // Never stored (the ingest requires them); the track skips such a row too.
                continue;
            }

            var late = LocationPingRules.TryParseUtc(row.ReceivedAt, out var receivedAt)
                       && (receivedAt - capturedAt).TotalMinutes > LateMinutes;

            points.Add((capturedAt, new AdminTrailPointDto
            {
                CapturedAt = LocationPingRules.FormatUtc(capturedAt),
                ReceivedAt = row.ReceivedAt,
                Kind = kind,
                Source = isFix ? row.Source : null,
                Latitude = isFix ? TripTrack.Round(row.Latitude!.Value, AdminDecimals) : null,
                Longitude = isFix ? TripTrack.Round(row.Longitude!.Value, AdminDecimals) : null,
                AccuracyM = isFix ? row.AccuracyM : null,
                Mocked = isFix ? row.Mocked : null,
                PausedUntil = kind == LocationPingRules.KindPaused && LocationPingRules.TryParseUtc(row.PausedUntil, out var until)
                    ? LocationPingRules.FormatUtc(until)
                    : null,
                Coarse = isFix && !CheckInEffects.PreciseEnough(row.AccuracyM),
                Late = late
            }));
        }

        // Stable: rows arrive in sort key order, so equal times keep that order.
        return points.OrderBy(p => p.CapturedAt).Select(p => p.Point).ToList();
    }

    private static AdminTrailStopDto StopOf(Dictionary<string, object> delivery, string key, DateTime? at)
    {
        var point = TripTrack.MapPointOf(delivery, key);
        return new AdminTrailStopDto
        {
            Latitude = point is null ? null : TripTrack.Round(point.Latitude, AdminDecimals),
            Longitude = point is null ? null : TripTrack.Round(point.Longitude, AdminDecimals),
            At = at is { } time ? LocationPingRules.FormatUtc(time) : null
        };
    }

    private static TimeSpan Overlap(DateTime aFrom, DateTime aUntil, DateTime bFrom, DateTime bUntil)
    {
        var start = aFrom > bFrom ? aFrom : bFrom;
        var stop = aUntil < bUntil ? aUntil : bUntil;
        return stop > start ? stop - start : TimeSpan.Zero;
    }

    private static string? Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;
}

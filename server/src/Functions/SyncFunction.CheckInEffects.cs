using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-07: the side effects of an accepted check-in (live-trip.md section 6.3, live-trip-api.md
/// sections 4 and 5). They run after a new <c>fix</c> is stored, never for a sharing event or an
/// idempotent repeat, and they never fail the ingest: each effect, and each delivery within one, is
/// caught and logged on its own. Each is idempotent through a conditional write.
/// <list type="bullet">
/// <item>Auto in transit: a <c>pickedUp</c> delivery 2 km or more from its pickup moves to
/// <c>inTransit</c> as a system actor, with the order coupling and the audit row a trucker's own
/// transition gets.</item>
/// <item>Truck nearby: a delivery on the road within 5 km of its drop-off is stamped
/// <c>nearbyNotifiedAt</c> once; the stamp's stream record sends the buyer <c>truck_nearby</c>
/// (<see cref="NotificationRules"/>, the ESC-21 pipeline).</item>
/// <item>Fraud signals: <c>GPS_JUMP</c> and <c>GPS_MOCKED</c> on the trucker, best effort.</item>
/// </list>
/// Only a check-in with <c>accuracyM</c> of 1,000 or less moves, stamps or jumps
/// (<see cref="CheckInEffects.MaxAccuracyM"/>); <c>GPS_MOCKED</c> does not depend on it.
/// A check-in never moves money (live-trip-api.md invariant 4).
/// </summary>
public partial class SyncFunction
{
    // The trip's check-ins read once per batch, with the batch's own new rows added, so a batch of
    // offline check-ins sees its earlier rows even before an index or a read would.
    private readonly Dictionary<string, List<TripLocationRow>> _batchTripRows = new(StringComparer.Ordinal);

    // Listing locations (the planned pickups) read once per batch.
    private readonly Dictionary<string, TrackPoint?> _batchListingPoints = new(StringComparer.Ordinal);

    /// <summary>The effects of the new check-in <paramref name="row"/>. Never throws.</summary>
    private async Task RunCheckInEffectsAsync(
        LocationPing ping,
        TripLocationRow row,
        Dictionary<string, AttributeValue> tripItem,
        IReadOnlyList<Dictionary<string, object>> deliveries,
        DateTime now,
        ILambdaContext context)
    {
        if (ping.Kind != LocationPingRules.KindFix || row.Latitude is not { } latitude || row.Longitude is not { } longitude)
        {
            return;
        }

        var fix = new TrackPoint(latitude, longitude);
        if (_batchTripRows.TryGetValue(row.TripId, out var cached))
        {
            AddRow(cached, row);
        }

        // A coarse check-in (accuracy over 1 km) moves, stamps and jumps nothing; a mock flag counts anyway.
        if (CheckInEffects.PreciseEnough(row.AccuracyM))
        {
            await IsolatedEffectAsync("auto_in_transit", row.TripId,
                () => AutoInTransitAsync(ping, fix, deliveries, row.TruckerId, context), context);
            await IsolatedEffectAsync("truck_nearby", row.TripId,
                () => TruckNearbyAsync(ping, row, fix, tripItem, deliveries, now, context), context);
        }

        await IsolatedEffectAsync("fraud_signals", row.TripId,
            () => CheckInFraudSignalsAsync(ping, row, context), context);
    }

    /// <summary>
    /// Each <c>pickedUp</c> delivery of the trip that the check-in shows 2 km or more past its pickup
    /// (its gpsPickup, else its listing location) moves to <c>inTransit</c>. The delivery is read
    /// again strongly consistent first, so the version the write is conditional on is current (the
    /// trip's deliveries come from an index).
    /// </summary>
    private async Task AutoInTransitAsync(
        LocationPing ping, TrackPoint fix, IReadOnlyList<Dictionary<string, object>> deliveries, string truckerId,
        ILambdaContext context)
    {
        foreach (var listed in deliveries)
        {
            if (Text(listed, "status") != "pickedUp" || Text(listed, "truckerId") != truckerId)
            {
                continue;
            }

            var deliveryId = Text(listed, "deliveryId") ?? string.Empty;
            try
            {
                var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
                if (delivery == null || Text(delivery, "truckerId") != truckerId)
                {
                    continue;
                }

                var pickup = TripTrack.MapPointOf(delivery, "gpsPickup") ?? await ListingPointAsync(delivery);
                if (CheckInEffects.DepartedKm(delivery, pickup, fix, ping.CapturedAtUtc) is not { } km)
                {
                    continue;
                }

                await MoveInTransitAsync(delivery, km, context);
            }
            catch (Exception ex)
            {
                LogEffectFailure("auto_in_transit", ping.TripId, deliveryId, ex, context);
            }
        }
    }

    /// <summary>
    /// The system <c>pickedUp → inTransit</c>: the conditional write, then the transaction-audit row
    /// and the order coupling (<c>pickup_confirmed → in_transit</c>) through the same calls a trucker's
    /// own transition makes (<see cref="EmitTxAuditAsync"/>, <see cref="TryAdvanceOrderForDeliveryAsync"/>).
    /// </summary>
    private async Task MoveInTransitAsync(Dictionary<string, object> delivery, double km, ILambdaContext context)
    {
        var deliveryId = Text(delivery, "deliveryId") ?? string.Empty;
        var tripId = Text(delivery, "tripId") ?? string.Empty;
        if (!int.TryParse(Text(delivery, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            context.Logger.LogWarning(
                $"CHECKIN_AUTO_TRANSIT_SKIPPED deliveryId={MoneyAlarmLog.LogId(deliveryId)} reason=no_version");
            return;
        }

        if (!await _deliveryRepo.TryMarkInTransitAsync(deliveryId, tripId, version))
        {
            // Moved on since the read (the trucker's own button, another check-in): nothing to do.
            return;
        }

        var orderId = Text(delivery, "orderId");
        var hasOrder = !string.IsNullOrEmpty(orderId);
        await EmitTxAuditAsync(context, hasOrder ? orderId! : deliveryId, "delivery", deliveryId,
            DeliveryActionFor("inTransit"), "pickedUp", "inTransit", ctxOverride: AuditContext.ForSystem(),
            metadata: $"source={CheckInEffects.AuditSource};distanceKm={CheckInEffects.FormatKm(km)}");
        if (hasOrder)
        {
            await TryAdvanceOrderForDeliveryAsync(orderId!, "inTransit", context);
        }

        context.Logger.LogInformation(
            $"CHECKIN_AUTO_TRANSIT deliveryId={MoneyAlarmLog.LogId(deliveryId)} orderId={MoneyAlarmLog.LogId(orderId)} "
            + $"distanceKm={CheckInEffects.FormatKm(km)}");
    }

    /// <summary>
    /// Each delivery on the road that the check-in shows within 5 km of its drop-off
    /// (<see cref="TripTrack.PlannedDropoff"/>) and that has no <c>nearbyNotifiedAt</c> is stamped,
    /// with the distance and the minutes to the start of the arrival window. The stamp is the only
    /// write: its stream record sends <c>truck_nearby</c> to the buyer, and a stamp that is already
    /// there (a repeat, another check-in first) fails its condition and sends nothing.
    /// </summary>
    private async Task TruckNearbyAsync(
        LocationPing ping, TripLocationRow row, TrackPoint fix, Dictionary<string, AttributeValue> tripItem,
        IReadOnlyList<Dictionary<string, object>> deliveries, DateTime now, ILambdaContext context)
    {
        Dictionary<string, object>? trip = null;
        foreach (var delivery in deliveries)
        {
            if (Text(delivery, "truckerId") != row.TruckerId)
            {
                continue;
            }

            trip ??= CropRepository.FromItem(tripItem);
            var dropoff = TripTrack.PlannedDropoff(trip, delivery);
            if (CheckInEffects.NearbyKmOf(delivery, dropoff, fix, ping.CapturedAtUtc) is not { } km)
            {
                continue;
            }

            var deliveryId = Text(delivery, "deliveryId") ?? string.Empty;
            try
            {
                // The minutes are a nicety: without them the push still goes, without its time part.
                int? minutes = null;
                try
                {
                    minutes = await NearbyMinutesAsync(delivery, row, trip, dropoff!, deliveries, now);
                }
                catch (Exception ex)
                {
                    LogEffectFailure("truck_nearby_minutes", ping.TripId, deliveryId, ex, context);
                }

                var stamped = await _deliveryRepo.TryStampNearbyAsync(
                    deliveryId, Text(delivery, "tripId") ?? row.TripId, LocationPingRules.FormatUtc(now),
                    CheckInEffects.RoundKm(km), minutes);
                if (stamped)
                {
                    context.Logger.LogInformation(
                        $"CHECKIN_TRUCK_NEARBY deliveryId={MoneyAlarmLog.LogId(deliveryId)} distanceKm={CheckInEffects.FormatKm(km)} "
                        + $"minutes={(minutes is { } m ? m.ToString(CultureInfo.InvariantCulture) : "-")}");
                }
            }
            catch (Exception ex)
            {
                LogEffectFailure("truck_nearby", ping.TripId, deliveryId, ex, context);
            }
        }
    }

    /// <summary>
    /// The minutes from now to the start of the delivery's arrival window, as its track would show
    /// it (<see cref="TripTrack.Build"/>: the fixes since its pickup, a running pause, and the other
    /// orders' undelivered stops at their planned points).
    /// </summary>
    private async Task<int?> NearbyMinutesAsync(
        Dictionary<string, object> delivery,
        TripLocationRow row,
        Dictionary<string, object> trip,
        TrackPoint dropoff,
        IReadOnlyList<Dictionary<string, object>> deliveries,
        DateTime now)
    {
        var rows = await TripRowsAsync(row);
        var deliveryId = Text(delivery, "deliveryId");
        var windowStart = TripTrack.PickedUpAt(delivery)!.Value;
        var otherStops = new List<TrackStop>();
        foreach (var other in deliveries)
        {
            if (Text(other, "deliveryId") == deliveryId || Text(other, "status") == "cancelled")
            {
                continue;
            }

            otherStops.AddRange(TripTrack.StopsOf(
                other, await ListingPointAsync(other), TripTrack.PlannedDropoff(trip, other), windowStart));
        }

        var track = TripTrack.Build(delivery, rows, pickup: null, dropoff, otherStops, now);
        return CheckInEffects.MinutesUntil(track.Eta?.From, now);
    }

    /// <summary>
    /// <c>GPS_MOCKED</c> when the phone flagged the check-in as a mock location; <c>GPS_JUMP</c> when it
    /// and the trip's check-in just before or just after it (in capture order) are more than 1 km apart
    /// at over 130 km/h, both with an accuracy of 1 km or better. On the trucker, the trip as the
    /// entity; one row per user, type and hour at most (<see cref="TryRecordFraudSignalAsync"/>).
    /// </summary>
    private async Task CheckInFraudSignalsAsync(LocationPing ping, TripLocationRow row, ILambdaContext context)
    {
        var capturedAt = LocationPingRules.FormatUtc(ping.CapturedAtUtc);
        if (ping.Mocked == true)
        {
            await TryRecordFraudSignalAsync(row.TruckerId, FraudDetectionService.GpsMockedSignal, ping.TripId, "trip",
                $"Check-in {capturedAt} was flagged by the phone as a mock location.", context);
        }

        if (!CheckInEffects.PreciseEnough(row.AccuracyM))
        {
            return;
        }

        // A coarse check-in is no neighbour either: the pairs are the trip's precise check-ins.
        var fixes = TripTrack.FixesIn(await TripRowsAsync(row), DateTime.MinValue, DateTime.MaxValue);
        fixes.RemoveAll(f => !CheckInEffects.PreciseEnough(f.AccuracyM));
        var current = fixes.Find(f => f.CapturedAtUtc == ping.CapturedAtUtc);
        if (current == null)
        {
            return;
        }

        var (before, after) = CheckInEffects.Neighbours(fixes, ping.CapturedAtUtc);
        foreach (var neighbour in new[] { before, after })
        {
            if (neighbour != null && CheckInEffects.IsJump(neighbour, current, out var km, out var kmh))
            {
                var speed = double.IsInfinity(kmh) ? "-" : Math.Round(kmh).ToString(CultureInfo.InvariantCulture);
                await TryRecordFraudSignalAsync(row.TruckerId, FraudDetectionService.GpsJumpSignal, ping.TripId, "trip",
                    $"Check-in {capturedAt} is {CheckInEffects.FormatKm(km)} km from the check-in at "
                    + $"{LocationPingRules.FormatUtc(neighbour.CapturedAtUtc)} ({speed} km/h).", context);
                return;
            }
        }
    }

    /// <summary>
    /// The trip's check-ins (read once per batch) with <paramref name="row"/> among them: the read is
    /// eventually consistent, so the row just written may not be in it yet.
    /// </summary>
    private async Task<List<TripLocationRow>> TripRowsAsync(TripLocationRow row)
    {
        if (!_batchTripRows.TryGetValue(row.TripId, out var rows))
        {
            rows = await _tripLocationRepo!.ListByTripAsync(row.TripId);
            _batchTripRows[row.TripId] = rows;
        }

        AddRow(rows, row);
        return rows;
    }

    private static void AddRow(List<TripLocationRow> rows, TripLocationRow row)
    {
        if (!rows.Exists(r => string.Equals(r.SortKey, row.SortKey, StringComparison.Ordinal)))
        {
            rows.Add(row);
        }
    }

    /// <summary>A delivery's planned pickup point: its listing's location, read once per batch. Null without one.</summary>
    private async Task<TrackPoint?> ListingPointAsync(Dictionary<string, object> delivery)
    {
        var listingId = Text(delivery, "listingId");
        if (string.IsNullOrEmpty(listingId))
        {
            return null;
        }

        if (!_batchListingPoints.TryGetValue(listingId, out var point))
        {
            point = TripTrack.PointOf(await _cropRepo.GetByIdAsync(listingId), "latitude", "longitude");
            _batchListingPoints[listingId] = point;
        }

        return point;
    }

    /// <summary>Runs one effect; a failure is logged and goes no further (not to the ingest, not to the next effect).</summary>
    private static async Task IsolatedEffectAsync(string effect, string tripId, Func<Task> run, ILambdaContext context)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            LogEffectFailure(effect, tripId, null, ex, context);
        }
    }

    private static void LogEffectFailure(string effect, string tripId, string? deliveryId, Exception ex, ILambdaContext context)
        => context.Logger.LogWarning(
            $"CHECKIN_EFFECT_FAILED effect={effect} tripId={MoneyAlarmLog.LogId(tripId)} "
            + $"deliveryId={MoneyAlarmLog.LogId(deliveryId)} reason={ex.GetType().Name}");

    private static string? Text(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;
}

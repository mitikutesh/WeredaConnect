using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-203 (fraud-detection-architecture.md Rule 4 and sections 14.3 and 15.3): the trucker's
/// <c>GPS_ANOMALY</c> row of a confirmed delivery, judged from the trucker's own check-ins
/// (<see cref="TruckerGpsEvidence"/>), never from the drop-off coordinates the confirming buyer sends.
/// </summary>
public partial class SyncFunction
{
    /// <summary>
    /// With <see cref="GpsAnomalyOnTrucker"/> on, after a delivery's <c>delivered</c> write: read the trucker's
    /// check-ins of the delivery's window (from its <c>pickedUpAt</c> to the confirmation, one bounded consistent
    /// Query of the trip-locations table), and when they show a truck that never left the pickup, too shortly
    /// before the confirmation to have reached the drop-off, write the row on the delivery's trucker through the
    /// attribution guard (<see cref="IFraudDetectionService.RecordAttributedSignalAsync"/>: one per delivery,
    /// one per buyer and trucker, two per trucker in 30 days). The ids, the pickup time and the pickup point
    /// come from the row as stored before this write, never from the request. No check-in, a stale one, sharing
    /// declined (D2), a window read that stopped at its page cap, no pickup point or a trip without a destination
    /// point: no row.
    /// A self-pickup has no trucker. Nothing of the check-ins goes back to the caller. Best effort: a failed
    /// read or write is logged and never fails the confirm.
    /// </summary>
    private async Task TryRecordTruckerGpsAnomalyAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> existing, string confirmedAtText,
        string? authenticatedUserId, ILambdaContext context)
    {
        if (!GpsAnomalyOnTrucker || _fraudService == null || _tripLocationRepo == null
            || string.IsNullOrEmpty(authenticatedUserId)
            || AttrString(existing, "deliveryType") == SelfPickupDeliveryType)
        {
            return;
        }

        // Only the delivery's buyer confirms it (the transition check refused anyone else before the write).
        var truckerId = AttrString(existing, "truckerId");
        if (string.IsNullOrEmpty(truckerId) || truckerId == authenticatedUserId
            || AttrString(existing, "buyerId") != authenticatedUserId)
        {
            return;
        }

        // One attempt per delivery and batch: a skip for one delivery must not stop the next one's.
        var signalType = FraudDetectionService.GpsAnomalySignal;
        if (!_batchSignals.Add($"{truckerId}|{signalType}|{operation.EntityId}"))
        {
            return;
        }

        try
        {
            var verdict = await JudgeTruckerGpsEvidenceAsync(existing, truckerId, confirmedAtText);
            if (verdict is { IsAnomaly: true })
            {
                await _fraudService.RecordAttributedSignalAsync(
                    truckerId, authenticatedUserId, signalType, operation.EntityId, "delivery", verdict.Description);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to record fraud signal {signalType} for delivery {MoneyAlarmLog.LogId(operation.EntityId)}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// The reads behind <see cref="TruckerGpsEvidence.Judge"/>, cheapest first: the check-in window, then the trip
    /// (woredas and destination point) only with a fresh check-in, then the order's own drop-off point only when
    /// Rule 4 flags every check-in and the trip has a destination. Null when the stored row lacks what the rule
    /// needs.
    /// </summary>
    private async Task<TruckerGpsVerdict?> JudgeTruckerGpsEvidenceAsync(
        Dictionary<string, AttributeValue> existing, string truckerId, string confirmedAtText)
    {
        var tripId = AttrString(existing, "tripId");
        if (string.IsNullOrEmpty(tripId)
            || !LocationPingRules.TryParseUtc(AttrString(existing, "pickedUpAt"), out var pickedUpAt)
            || !LocationPingRules.TryParseUtc(confirmedAtText, out var confirmedAt)
            || StoredPoint(existing, "gpsPickup") is not { } pickup)
        {
            return null;
        }

        var (from, to) = TruckerGpsEvidence.WindowKeys(pickedUpAt, confirmedAt);
        var window = await _tripLocationRepo!.ListBetweenAsync(tripId, from, to, TruckerGpsEvidence.MaxWindowPages);
        if (window is not { Complete: true })
        {
            // Rows left unread: no road check-in near the drop-off cannot be shown.
            return null;
        }

        var checkIns = TruckerGpsEvidence.RoadCheckIns(window.Rows, truckerId, pickedUpAt, confirmedAt);
        if (TruckerGpsEvidence.LastFresh(checkIns, confirmedAt) == null)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoEvidence);
        }

        var tripItem = await GetTripItemAsync(tripId);
        var fromWoreda = tripItem == null ? "" : AttrString(tripItem, "fromWoreda") ?? "";
        var toWoreda = tripItem == null ? "" : AttrString(tripItem, "toWoreda") ?? "";
        if (!TruckerGpsEvidence.NeverLeftPickup(checkIns, pickup, fromWoreda, toWoreda))
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.Consistent);
        }

        // The trip's destination point, which the trucker set, is required: a trip without one (older than
        // trip-listing-api 2.1.0) would leave only the buyer's order point, so no row.
        if (TripTrack.PointOf(tripItem == null ? null : CropRepository.FromItem(tripItem),
                "destinationLatitude", "destinationLongitude") is not { } tripDestination)
        {
            return new TruckerGpsVerdict(TruckerGpsOutcome.NoDropoffPoint);
        }

        // The order's own drop-off point (the confirm accepts it too; locked at pickup) may only clear the trucker.
        // Read strongly consistent.
        var orderId = AttrString(existing, "orderId");
        var orderPoint = string.IsNullOrEmpty(orderId)
            ? null
            : OrderDeliveryLocation.Of(await _orderRepo.GetByIdAsync(orderId, consistentRead: true));

        var maxTruckKmh = PatternRules?.Settings.MaxDeliveryKmh ?? TransactionPatternSettings.Default.MaxDeliveryKmh;
        return TruckerGpsEvidence.Judge(
            checkIns, pickup, confirmedAt, fromWoreda, toWoreda, tripDestination, orderPoint, maxTruckKmh);
    }

    /// <summary>A stored map point (<c>{latitude, longitude}</c>, numbers), such as <c>gpsPickup</c>; null when missing.</summary>
    private static TrackPoint? StoredPoint(Dictionary<string, AttributeValue> item, string key)
    {
        if (!item.TryGetValue(key, out var attr) || attr.M == null
            || !attr.M.TryGetValue("latitude", out var lat)
            || !attr.M.TryGetValue("longitude", out var lng)
            || !double.TryParse(lat.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(lng.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            return null;
        }

        return new TrackPoint(latitude, longitude);
    }
}

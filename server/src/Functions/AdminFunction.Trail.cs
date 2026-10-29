using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-13: GET /admin/deliveries/{id}/trail, the trip trail of a delivery as dispute evidence
/// (live-trip-api.md section 3). Reads the delivery and its trip's check-ins; never writes.
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// The trail of one delivery for the admin dispute page. Admin group only, like the other evidence
    /// and money routes: anyone else gets the admin refusal (<c>403 ADMIN_001</c>). An unknown or
    /// soft-deleted delivery is <c>404 ADMIN_018</c>. <see cref="AdminTrail"/> cuts the window (pickup
    /// to delivery, or now) and works out the legs, pauses, gaps and summary.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDeliveryTrailAsync(
        string deliveryId,
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_deliveriesTable) || string.IsNullOrEmpty(_tripLocationsTable))
        {
            context.Logger.LogError("Delivery trail is not configured (DELIVERIES_TABLE or TRIP_LOCATIONS_TABLE missing).");
            return LambdaResponse.Error(503, "ADMIN_004", "Trail tables not configured.");
        }

        var delivery = await new DeliveryRepository(_dynamoDb, _deliveriesTable).GetByIdAsync(deliveryId);
        if (delivery == null || (delivery.TryGetValue("deletedAt", out var deletedAt) && deletedAt != null))
        {
            return LambdaResponse.Error(404, "ADMIN_018", "Delivery not found.");
        }

        // Before pickup, and for a self-pickup (no truck), there is nothing to read.
        var tripId = AdminTrail.TripIdOf(delivery);
        var rows = tripId != null && TripTrack.PickedUpAt(delivery) != null
            ? await new TripLocationRepository(_dynamoDb, _tripLocationsTable).ListByTripAsync(tripId)
            : new List<TripLocationRow>();

        var planned = rows.Count > 0 && tripId != null
            ? await PlannedStopsAsync(tripId, context)
            : null;
        var response = AdminTrail.Build(delivery, rows, Clock(), planned);
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDeliveryTrailResponse);
    }

    /// <summary>The most pages of a trip's deliveries read for its planned stops, as the track.</summary>
    private const int MaxTripDeliveryPages = 10;

    /// <summary>
    /// The trip's stop list: every non-cancelled order's planned pickup (its listing's location) and
    /// the trip's destination point, the points the track's other stops use. Null when the trips or
    /// crops table is not configured or a read fails: the trail then judges pauses by the QR scans
    /// alone. Never fails the trail.
    /// </summary>
    private async Task<List<TrackPoint>?> PlannedStopsAsync(string tripId, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_tripsTable) || string.IsNullOrEmpty(_cropsTable))
        {
            return null;
        }

        try
        {
            var trip = await new TripRepository(_dynamoDb, _tripsTable).GetByIdAsync(tripId);
            var crops = new CropRepository(_dynamoDb, _cropsTable);
            var deliveries = new DeliveryRepository(_dynamoDb, _deliveriesTable);
            var points = new List<TrackPoint>();
            var listings = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, AttributeValue>? startKey = null;
            var pages = 0;
            do
            {
                var (items, next) = await deliveries.QueryDeliveriesAsync(
                    tripId: tripId, limit: 50, exclusiveStartKey: startKey);
                foreach (var other in items)
                {
                    if (other.TryGetValue("status", out var status) && status?.ToString() == "cancelled")
                    {
                        continue;
                    }

                    if (other.TryGetValue("listingId", out var listing) && listing?.ToString() is { Length: > 0 } listingId
                        && listings.Add(listingId)
                        && TripTrack.PointOf(await crops.GetByIdAsync(listingId), "latitude", "longitude") is { } pickup)
                    {
                        points.Add(pickup);
                    }

                    if (TripTrack.PlannedDropoff(trip, other) is { } dropoff && !points.Contains(dropoff))
                    {
                        points.Add(dropoff);
                    }
                }

                startKey = next;
            }
            while (startKey != null && ++pages < MaxTripDeliveryPages);

            return points;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"trail: planned stops of trip {MoneyAlarmLog.LogId(tripId)} unavailable: [{ex.GetType().Name}]");
            return null;
        }
    }
}

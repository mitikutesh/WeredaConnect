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

        var response = AdminTrail.Build(delivery, rows, Clock());
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDeliveryTrailResponse);
    }
}

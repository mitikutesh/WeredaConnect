using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// SIM-56: <c>GET /intents</c>, the caller's own buying requests (push-alerts-api.md 1.5.0).
/// The app writes them through the <c>/sync</c> <c>buyer_intent</c> entity but had no way to read
/// them back, so a web reload (no local store) or a new phone showed an empty My Requests.
///
/// Buyers only. The partition key is the authenticated user's id (the JWT <c>sub</c>), never a
/// query parameter, so a buyer reads only their own requests (role visibility: own intents).
/// Only <c>active</c> rows come back (the app deletes a fulfilled request from the server; an
/// update can still store <c>fulfilled</c> or <c>expired</c>), newest first.
///
/// Error codes:
/// - 401: INTENT_UNAUTHORIZED - no authenticated user
/// - 403: INTENT_FORBIDDEN    - the caller is not a buyer
/// - 405: INTENT_VALIDATION   - a method other than GET
/// - 503: INTENT_UNAVAILABLE  - intents are not provisioned on this environment
/// - 500: INTENT_SERVER_ERROR - the read failed
/// </summary>
public partial class MatchFunction
{
    private const string IntentsPath = "/intents";

    private static bool IsIntentsRoute(APIGatewayProxyRequest request)
        => string.Equals(request.Resource, IntentsPath, StringComparison.Ordinal)
           || string.Equals(request.Path?.TrimEnd('/'), IntentsPath, StringComparison.Ordinal);

    private async Task<APIGatewayProxyResponse> HandleListIntentsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var buyerId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(buyerId))
        {
            return LambdaResponse.Error(401, "INTENT_UNAUTHORIZED", "Authentication required.");
        }

        if (!string.Equals(AuthHelper.GetClaim(request, "custom:role"), "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return LambdaResponse.Error(403, "INTENT_FORBIDDEN", "Only buyers have buying requests.");
        }

        if (_intentRepo == null)
        {
            return LambdaResponse.Error(503, "INTENT_UNAVAILABLE",
                "Buying requests are not enabled on this environment.");
        }

        try
        {
            var rows = await _intentRepo.QueryByBuyerAsync(buyerId);
            var intents = rows
                // Defence in depth: the query is keyed by the caller, so another buyer's row
                // cannot appear; a row that says otherwise is never returned.
                .Where(row => GetString(row, "buyerId") == buyerId)
                .Where(row => IsActiveIntent(row))
                .Select(ToIntentDto)
                .OfType<IntentDto>()
                .OrderByDescending(intent => intent.CreatedAt, StringComparer.Ordinal)
                .ToList();

            return LambdaResponse.Ok(
                new ListIntentsResponse { Intents = intents, Count = intents.Count },
                AppJsonContext.Default.ListIntentsResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error listing intents: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "INTENT_SERVER_ERROR", "Failed to read buying requests.");
        }
    }

    /// <summary>A row without a status was written active (the create forces it).</summary>
    private static bool IsActiveIntent(Dictionary<string, object> row)
    {
        var status = GetStringOrNull(row, "status");
        return string.IsNullOrEmpty(status) || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The row as the client reads it, or null when it lacks what the app needs to show it
    /// (an id and a crop type); such a row is left out rather than failing the whole list.
    /// </summary>
    private static IntentDto? ToIntentDto(Dictionary<string, object> row)
    {
        var intentId = GetString(row, "intentId");
        var cropType = GetString(row, "cropType");
        if (intentId.Length == 0 || cropType.Length == 0)
        {
            return null;
        }

        return new IntentDto
        {
            IntentId = intentId,
            CropType = cropType,
            QuantityNeeded = GetDouble(row, "quantityNeeded") ?? 0,
            MaxPricePerKg = GetDouble(row, "maxPricePerKg") ?? 0,
            DeliveryWoreda = GetString(row, "deliveryWoreda"),
            DeliveryLatitude = GetDouble(row, "deliveryLatitude"),
            DeliveryLongitude = GetDouble(row, "deliveryLongitude"),
            NeededByDate = GetStringOrNull(row, "neededByDate"),
            Status = "active",
            CreatedAt = GetString(row, "createdAt"),
            UpdatedAt = GetStringOrNull(row, "updatedAt"),
            Version = (int)(GetDouble(row, "version") ?? 1),
        };
    }
}

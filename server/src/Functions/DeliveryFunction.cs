using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /deliveries and GET /deliveries/{id} Lambda handler.
/// Provides read-only access to delivery records.
/// Create/update/delete operations are handled through /sync endpoint.
///
/// Endpoints:
/// - GET /deliveries - Query deliveries with optional filters (status, tripId, listingId, etc.)
/// - GET /deliveries/{id} - Get a single delivery by ID (participants only)
///
/// Error codes:
/// - 400: DELIVERY_VALIDATION - Invalid query parameters
/// - 401: DELIVERY_UNAUTHORIZED - Authentication required or not a participant
/// - 404: DELIVERY_NOT_FOUND - Delivery does not exist
/// - 500: DELIVERY_SERVER_ERROR - Unexpected server failure
/// </summary>
public class DeliveryFunction
{
    private readonly IDeliveryRepository _deliveryRepo;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public DeliveryFunction()
    {
        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE")
                              ?? throw new InvalidOperationException("DELIVERIES_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _deliveryRepo = new DeliveryRepository(dynamoDb, deliveriesTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public DeliveryFunction(IDeliveryRepository deliveryRepo)
    {
        _deliveryRepo = deliveryRepo ?? throw new ArgumentNullException(nameof(deliveryRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"DeliveryFunction invoked: {request.HttpMethod} {request.Path}");

        // Route based on HTTP method and path
        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetDeliveryByIdAsync(request, context),
            "GET" => await HandleGetDeliveriesAsync(request, context),
            _ => LambdaResponse.Error(405, "DELIVERY_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /deliveries - Query deliveries with optional filters.
    /// Authentication required.
    /// Query parameters: status, tripId, listingId, limit, nextToken
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetDeliveriesAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // Authentication required for listing endpoint
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "DELIVERY_UNAUTHORIZED", "Authentication required.");
        }

        // Parse query parameters
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("tripId", out var tripId);
        queryParams.TryGetValue("listingId", out var listingId);

        // Parse limit (default 20, max 50)
        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "DELIVERY_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 50.");
            }
        }

        // Parse nextToken (base64-encoded DynamoDB LastEvaluatedKey)
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return LambdaResponse.Error(400, "DELIVERY_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        try
        {
            // Query deliveries
            var queryResult = await _deliveryRepo.QueryDeliveriesAsync(
                status, tripId, listingId, limit, exclusiveStartKey);
            var items = queryResult.Items;
            var lastEvaluatedKey = queryResult.LastEvaluatedKey;

            // Security: filter to only show deliveries where the user is a participant
            var userId = GetAuthenticatedUserId(request);
            var filteredItems = items.Where(i => IsParticipant(i, userId)).ToList();

            // Map to DTOs (hide phone numbers and QR tokens in list view)
            var deliveries = filteredItems
                .Select(i => MapToDeliveryDto(i, includePhone: false, includeQrToken: false))
                .ToList();

            // Encode nextToken if more results available
            string? nextTokenResponse = null;
            var hasMore = lastEvaluatedKey != null;
            if (hasMore)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(lastEvaluatedKey!);
            }

            var response = new GetDeliveriesResponse
            {
                Deliveries = deliveries,
                NextToken = nextTokenResponse,
                HasMore = hasMore,
                Total = filteredItems.Count
            };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetDeliveriesResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying deliveries: {ex.Message}");
            return LambdaResponse.Error(500, "DELIVERY_SERVER_ERROR", "Failed to query deliveries.");
        }
    }

    /// <summary>
    /// GET /deliveries/{id} - Get a single delivery by ID.
    /// Only participants (farmerId, truckerId, buyerId) can access.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetDeliveryByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // Authentication required
        if (!IsAuthenticated(request))
        {
            return LambdaResponse.Error(401, "DELIVERY_UNAUTHORIZED", "Authentication required.");
        }

        if (!request.PathParameters.TryGetValue("id", out var deliveryId) || string.IsNullOrEmpty(deliveryId))
        {
            return LambdaResponse.Error(400, "DELIVERY_VALIDATION", "Missing delivery ID in path.");
        }

        try
        {
            var delivery = await _deliveryRepo.GetByIdAsync(deliveryId);

            if (delivery == null)
            {
                return LambdaResponse.Error(404, "DELIVERY_NOT_FOUND", "Delivery not found.");
            }

            // Check if deleted
            if (delivery.TryGetValue("deletedAt", out var deletedAt) && deletedAt != null)
            {
                return LambdaResponse.Error(404, "DELIVERY_NOT_FOUND", "Delivery has been cancelled.");
            }

            // Check if requester is a participant — reject non-participants
            var userId = GetAuthenticatedUserId(request);
            var participant = IsParticipant(delivery, userId);

            if (!participant)
            {
                return LambdaResponse.Error(403, "DELIVERY_UNAUTHORIZED", "You are not a participant in this delivery.");
            }

            var deliveryDto = MapToDeliveryDto(delivery, includePhone: true, includeQrToken: true);
            var response = new GetDeliveryResponse { Delivery = deliveryDto };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetDeliveryResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting delivery {deliveryId}: {ex.Message}");
            return LambdaResponse.Error(500, "DELIVERY_SERVER_ERROR", "Failed to retrieve delivery.");
        }
    }

    /// <summary>
    /// Map a DynamoDB item dictionary to a DeliveryDto.
    /// Handles type conversions from string/number representations.
    /// </summary>
    internal static DeliveryDto MapToDeliveryDto(
        Dictionary<string, object> item,
        bool includePhone = true,
        bool includeQrToken = true)
    {
        return new DeliveryDto
        {
            DeliveryId = GetString(item, "deliveryId"),
            TripId = GetString(item, "tripId"),
            ListingId = GetString(item, "listingId"),
            FarmerId = GetString(item, "farmerId"),
            FarmerName = GetString(item, "farmerName"),
            FarmerPhone = includePhone ? GetStringOrNull(item, "farmerPhone") : null,
            TruckerId = GetString(item, "truckerId"),
            TruckerName = GetString(item, "truckerName"),
            TruckerPhone = includePhone ? GetStringOrNull(item, "truckerPhone") : null,
            BuyerId = GetString(item, "buyerId"),
            BuyerName = GetString(item, "buyerName"),
            BuyerPhone = includePhone ? GetStringOrNull(item, "buyerPhone") : null,
            QrToken = includeQrToken ? GetString(item, "qrToken") : "",
            Status = GetString(item, "status"),
            GpsPickup = GetLatLng(item, "gpsPickup"),
            GpsDropoff = GetLatLng(item, "gpsDropoff"),
            PickupPhotoRef = GetStringOrNull(item, "pickupPhotoRef"),
            DropoffPhotoRef = GetStringOrNull(item, "dropoffPhotoRef"),
            PickedUpAt = GetStringOrNull(item, "pickedUpAt"),
            ConfirmedAt = GetStringOrNull(item, "confirmedAt"),
            DisputeReason = GetStringOrNull(item, "disputeReason"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            DeletedAt = GetStringOrNull(item, "deletedAt"),
            Version = GetInt(item, "version")
        };
    }

    /// <summary>
    /// Check if the request is authenticated by looking for an Authorization header
    /// or JWT authorizer claims in the request context.
    /// </summary>
    private static bool IsAuthenticated(APIGatewayProxyRequest request)
    {
        // Check for Authorization header (case-insensitive lookup)
        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "authorization", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(header.Value))
                {
                    return true;
                }
            }
        }

        // Check for JWT authorizer claims in request context
        if (request.RequestContext?.Authorizer != null
            && request.RequestContext.Authorizer.Count > 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extract the authenticated user ID from JWT authorizer claims.
    /// </summary>
    private static string? GetAuthenticatedUserId(APIGatewayProxyRequest request)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null) return null;

        if (authorizer.ContainsKey("sub"))
            return authorizer["sub"]?.ToString();
        if (authorizer.ContainsKey("userId"))
            return authorizer["userId"]?.ToString();

        return null;
    }

    /// <summary>
    /// Check if the user is a participant in the delivery (farmer, trucker, or buyer).
    /// </summary>
    private static bool IsParticipant(Dictionary<string, object> item, string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        var farmerId = GetStringOrNull(item, "farmerId");
        var truckerId = GetStringOrNull(item, "truckerId");
        var buyerId = GetStringOrNull(item, "buyerId");

        return userId == farmerId || userId == truckerId || userId == buyerId;
    }

    /// <summary>
    /// Get a required string value from the item dictionary.
    /// </summary>
    private static string GetString(Dictionary<string, object> item, string key)
    {
        return item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";
    }

    /// <summary>
    /// Get an optional string value from the item dictionary.
    /// </summary>
    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
    {
        return item.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    /// <summary>
    /// Get a required int value from the item dictionary.
    /// DynamoDB stores numbers as strings, so we need to parse.
    /// </summary>
    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
            return 0;

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }

    /// <summary>
    /// Parse a LatLngDto from the item dictionary.
    /// Handles nested Dictionary&lt;string, object&gt; (from test data or future FromItem).
    /// </summary>
    private static LatLngDto? GetLatLng(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
            return null;

        // Handle nested dictionary (e.g., from test data or enriched FromItem)
        if (value is Dictionary<string, object> nested)
        {
            if (nested.TryGetValue("latitude", out var latObj) &&
                nested.TryGetValue("longitude", out var lngObj))
            {
                var lat = ParseDouble(latObj);
                var lng = ParseDouble(lngObj);
                if (lat.HasValue && lng.HasValue)
                {
                    return new LatLngDto { Latitude = lat.Value, Longitude = lng.Value };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parse a double from various types (string, int, double, etc.)
    /// </summary>
    private static double? ParseDouble(object? value)
    {
        if (value == null) return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s => double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null,
            _ => null
        };
    }

}

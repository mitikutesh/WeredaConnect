using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /trips and GET /trips/{id} Lambda handler.
/// Provides read-only access to trip listings.
/// Create/update/delete operations are handled through /sync endpoint.
///
/// Endpoints:
/// - GET /trips - Query trip listings with optional filters (fromWoreda, toWoreda, status, etc.)
/// - GET /trips/{id} - Get a single trip listing by ID
///
/// Error codes:
/// - 400: TRIP_VALIDATION - Invalid query parameters
/// - 404: TRIP_NOT_FOUND - Trip listing does not exist
/// - 500: TRIP_SERVER_ERROR - Unexpected server failure
///
/// See: specs/contracts/trip-listing-api.md
/// </summary>
public class TripFunction
{
    private readonly ITripRepository _tripRepo;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public TripFunction()
    {
        var tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE")
                         ?? throw new InvalidOperationException("TRIPS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _tripRepo = new TripRepository(dynamoDb, tripsTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public TripFunction(ITripRepository tripRepo)
    {
        _tripRepo = tripRepo ?? throw new ArgumentNullException(nameof(tripRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"TripFunction invoked: {request.HttpMethod} {request.Path}");

        // Route based on HTTP method and path
        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetTripByIdAsync(request, context),
            "GET" => await HandleGetTripsAsync(request, context),
            _ => LambdaResponse.Error(405, "TRIP_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /trips - Query trip listings with optional filters.
    /// Query parameters: truckerId, fromWoreda, toWoreda, status, minCapacity, maxPrice,
    /// departureDateFrom, departureDateTo, limit, nextToken
    ///
    /// Role-based visibility:
    /// - Trucker: can only see own trips (truckerId forced to authenticated userId)
    /// - Farmer/Buyer/Admin/Unauthenticated: can browse all trips
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetTripsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // Parse query parameters
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("fromWoreda", out var fromWoreda);
        queryParams.TryGetValue("toWoreda", out var toWoreda);
        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("truckerId", out var truckerId);

        // Role-based visibility enforcement
        var authenticatedUserId = AuthHelper.GetUserId(request);
        var role = AuthHelper.GetClaim(request, "custom:role");

        if (string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase))
        {
            // Truckers can only see their own trip listings
            if (!string.IsNullOrEmpty(truckerId) && truckerId != authenticatedUserId)
            {
                return LambdaResponse.Error(403, "TRIP_FORBIDDEN",
                    "Truckers can only view their own trip listings.");
            }
            truckerId = authenticatedUserId;
        }

        // Parse limit (default 20, max 50)
        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "TRIP_VALIDATION",
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
                return LambdaResponse.Error(400, "TRIP_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        try
        {
            List<Dictionary<string, object>> items;
            Dictionary<string, AttributeValue>? lastEvaluatedKey;

            if (!string.IsNullOrEmpty(truckerId))
            {
                // Query by trucker using TruckerIdCreatedAtIndex GSI
                var queryResult = await _tripRepo.QueryTripsByTruckerAsync(
                    truckerId, status, limit, exclusiveStartKey);
                items = queryResult.Items;
                lastEvaluatedKey = queryResult.LastEvaluatedKey;
            }
            else
            {
                // Default: query by status using StatusDepartureDateIndex GSI
                var queryResult = await _tripRepo.QueryTripsAsync(
                    fromWoreda, toWoreda, status, limit, exclusiveStartKey);
                items = queryResult.Items;
                lastEvaluatedKey = queryResult.LastEvaluatedKey;
            }

            // Apply client-side filters that DynamoDB FilterExpression doesn't handle efficiently
            queryParams.TryGetValue("minCapacity", out var minCapacityStr);
            queryParams.TryGetValue("maxPrice", out var maxPriceStr);
            queryParams.TryGetValue("departureDateFrom", out var departureDateFrom);
            queryParams.TryGetValue("departureDateTo", out var departureDateTo);

            if (!string.IsNullOrEmpty(minCapacityStr))
            {
                if (!double.TryParse(minCapacityStr, out var minCapacity) || minCapacity < 0)
                {
                    return LambdaResponse.Error(400, "TRIP_VALIDATION",
                        "Invalid minCapacity parameter. Must be a non-negative number.");
                }
                items = items.Where(i => GetDouble(i, "capacity") >= minCapacity).ToList();
            }

            if (!string.IsNullOrEmpty(maxPriceStr))
            {
                if (!double.TryParse(maxPriceStr, out var maxPrice) || maxPrice < 0)
                {
                    return LambdaResponse.Error(400, "TRIP_VALIDATION",
                        "Invalid maxPrice parameter. Must be a non-negative number.");
                }
                items = items.Where(i => GetDouble(i, "pricePerQuintal") <= maxPrice).ToList();
            }

            if (!string.IsNullOrEmpty(departureDateFrom))
            {
                items = items.Where(i =>
                    string.Compare(GetString(i, "departureDate"), departureDateFrom, StringComparison.Ordinal) >= 0)
                    .ToList();
            }

            if (!string.IsNullOrEmpty(departureDateTo))
            {
                items = items.Where(i =>
                    string.Compare(GetString(i, "departureDate"), departureDateTo, StringComparison.Ordinal) <= 0)
                    .ToList();
            }

            // Determine if the request is authenticated
            var isAuthenticated = IsAuthenticated(request);

            // Map to DTOs (hide phone for unauthenticated users)
            var trips = items.Select(i => MapToTripDto(i, isAuthenticated)).ToList();

            // Encode nextToken if more results available
            string? nextTokenResponse = null;
            var hasMore = lastEvaluatedKey != null;
            if (hasMore)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(lastEvaluatedKey!);
            }

            var response = new GetTripsResponse
            {
                Trips = trips,
                NextToken = nextTokenResponse,
                HasMore = hasMore,
                Total = trips.Count
            };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetTripsResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying trips: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "TRIP_SERVER_ERROR", "Failed to query trip listings.");
        }
    }

    /// <summary>
    /// GET /trips/{id} - Get a single trip listing by ID.
    ///
    /// Role-based visibility:
    /// - Trucker: can only view own trips (403 if trip belongs to another trucker)
    /// - Farmer/Buyer/Admin/Unauthenticated: can view any trip
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetTripByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.PathParameters.TryGetValue("id", out var tripId) || string.IsNullOrEmpty(tripId))
        {
            return LambdaResponse.Error(400, "TRIP_VALIDATION", "Missing trip ID in path.");
        }

        try
        {
            var trip = await _tripRepo.GetByIdAsync(tripId);

            if (trip == null)
            {
                return LambdaResponse.Error(404, "TRIP_NOT_FOUND", "Trip listing not found.");
            }

            // Check if deleted
            if (trip.TryGetValue("deletedAt", out var deletedAt) && deletedAt != null)
            {
                return LambdaResponse.Error(404, "TRIP_NOT_FOUND", "Trip listing has been cancelled.");
            }

            // F-SEC-03: Check if cancelled via status update path (which doesn't set deletedAt)
            if (trip.TryGetValue("status", out var statusVal)
                && statusVal?.ToString() == "cancelled")
            {
                return LambdaResponse.Error(404, "TRIP_NOT_FOUND", "Trip listing has been cancelled.");
            }

            // Role-based visibility: truckers can only view their own trips
            var role = AuthHelper.GetClaim(request, "custom:role");
            if (string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase))
            {
                var authenticatedUserId = AuthHelper.GetUserId(request);
                var tripOwnerId = trip.TryGetValue("truckerId", out var ownerVal) ? ownerVal?.ToString() : null;
                if (tripOwnerId != authenticatedUserId)
                {
                    return LambdaResponse.Error(403, "TRIP_FORBIDDEN",
                        "Truckers can only view their own trip listings.");
                }
            }

            var isAuthenticated = IsAuthenticated(request);
            var tripDto = MapToTripDto(trip, isAuthenticated);
            var response = new GetTripResponse { Trip = tripDto };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetTripResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting trip {tripId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "TRIP_SERVER_ERROR", "Failed to retrieve trip listing.");
        }
    }

    /// <summary>
    /// Map a DynamoDB item dictionary to a TripDto.
    /// Handles type conversions from string/number representations.
    /// </summary>
    internal static TripDto MapToTripDto(Dictionary<string, object> item, bool includePhone = true)
    {
        return new TripDto
        {
            TripId = GetString(item, "tripId"),
            TruckerId = GetString(item, "truckerId"),
            TruckerName = GetString(item, "truckerName"),
            TruckerPhone = includePhone ? GetStringOrNull(item, "truckerPhone") : null,
            VehiclePlate = GetStringOrNull(item, "vehiclePlate"),
            VehicleDescription = GetStringOrNull(item, "vehicleDescription"),
            FromWoreda = GetString(item, "fromWoreda"),
            ToWoreda = GetString(item, "toWoreda"),
            DepartureDate = GetString(item, "departureDate"),
            Capacity = GetDouble(item, "capacity"),
            PricePerQuintal = GetDouble(item, "pricePerQuintal"),
            Status = GetString(item, "status"),
            MatchedListings = GetStringList(item, "matchedListings"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            DeletedAt = GetStringOrNull(item, "deletedAt"),
            Version = GetInt(item, "version")
        };
    }

    /// <summary>
    /// Check if the request is authenticated by verifying JWT authorizer claims
    /// in the request context (set by API Gateway after JWT validation).
    /// Raw Authorization headers are NOT trusted — they can be spoofed.
    /// </summary>
    private static bool IsAuthenticated(APIGatewayProxyRequest request)
        => AuthHelper.IsAuthenticated(request);

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
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }

    /// <summary>
    /// Get a required double value from the item dictionary.
    /// DynamoDB stores numbers as strings, so we need to parse.
    /// </summary>
    private static double GetDouble(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0.0;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal dec => (double)dec,
            string s => double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0.0,
            _ => 0.0
        };
    }

    /// <summary>
    /// Get a string list from the item dictionary.
    /// Handles JSON arrays stored as strings from DynamoDB.
    /// </summary>
    private static List<string> GetStringList(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return new List<string>();
        }

        if (value is string s)
        {
            if (string.IsNullOrEmpty(s) || s == "[]")
            {
                return new List<string>();
            }

            // Try to parse as JSON array
            try
            {
                var list = JsonSerializer.Deserialize(s, AppJsonContext.Default.ListString);
                return list ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        return new List<string>();
    }

}

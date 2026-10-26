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
    /// Role-based visibility (F-36 — browse-all is Buyer/Admin only):
    /// - Trucker: can only see own trips (truckerId forced to authenticated userId)
    /// - Buyer / Admin: can browse all trips
    /// - Farmer / unknown role / unauthenticated: 403 TRIP_FORBIDDEN
    ///
    /// SIM-31: cancelled or soft-deleted trips (the ones GET /trips/{id} answers with 404)
    /// are never listed, for any role; status=cancelled returns an empty page.
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
        else if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase)
                 && !AdminAuthHelper.IsAdmin(request))
        {
            // F-36: only buyers and admins may browse all trip listings. Farmers,
            // unknown roles, and unauthenticated callers are forbidden (CRITICAL
            // role-visibility rule — only buyers see ALL listings).
            return LambdaResponse.Error(403, "TRIP_FORBIDDEN",
                "Only buyers and administrators can browse trip listings.");
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

            if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            {
                // SIM-31: cancelled trips are never listed, so do not read the cancelled
                // partition at all. The post-filter validation below still runs.
                items = new List<Dictionary<string, object>>();
                lastEvaluatedKey = null;
            }
            else if (!string.IsNullOrEmpty(truckerId))
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

            // SIM-31: drop every trip the detail endpoint hides (404), so list and detail agree.
            // The repository only excludes deletedAt; a trip cancelled through a status update
            // has no deletedAt and would otherwise still be listed.
            items = items.Where(i => !IsCancelledOrDeleted(i)).ToList();

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

            // ESC-48: the phone goes only to the trip's trucker and admins. A buyer browsing
            // could otherwise call the trucker and agree to trade off the platform.
            var isAdmin = AdminAuthHelper.IsAdmin(request);
            var trips = items
                .Select(i => MapToTripDto(i, includePhone: isAdmin || IsOwner(i, authenticatedUserId)))
                .ToList();

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
    /// Role-based visibility (F-36 — Buyer/Admin only, plus owning Trucker):
    /// - Trucker: can only view own trips (403 if trip belongs to another trucker)
    /// - Buyer / Admin: can view any trip
    /// - Farmer / unknown role / unauthenticated: 403 TRIP_FORBIDDEN
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

            // Soft-deleted, or (F-SEC-03) cancelled via the status update path, which does
            // not set deletedAt. Same predicate as the list (SIM-31).
            if (IsCancelledOrDeleted(trip))
            {
                return LambdaResponse.Error(404, "TRIP_NOT_FOUND", "Trip listing has been cancelled.");
            }

            // Role-based visibility (F-36): trucker (own only), buyer/admin (any), else 403.
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
            else if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase)
                     && !AdminAuthHelper.IsAdmin(request))
            {
                return LambdaResponse.Error(403, "TRIP_FORBIDDEN",
                    "Only buyers and administrators can view trip listings.");
            }

            var tripDto = MapToTripDto(trip,
                includePhone: AdminAuthHelper.IsAdmin(request) || IsOwner(trip, AuthHelper.GetUserId(request)));
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
    /// SIM-31: a trip is hidden from every read (detail 404, list omitted) when it is
    /// soft-deleted (deletedAt set) or cancelled through a status update (no deletedAt).
    /// </summary>
    internal static bool IsCancelledOrDeleted(Dictionary<string, object> item)
        => (item.TryGetValue("deletedAt", out var deletedAt) && deletedAt != null)
           || (item.TryGetValue("status", out var status) && status?.ToString() == "cancelled");

    /// <summary>Whether the caller is the trucker who owns this trip.</summary>
    private static bool IsOwner(Dictionary<string, object> item, string? userId)
        => !string.IsNullOrEmpty(userId) && GetStringOrNull(item, "truckerId") == userId;

    /// <summary>
    /// Map a DynamoDB item dictionary to a TripDto.
    /// Handles type conversions from string/number representations.
    /// <paramref name="includePhone"/> is true only for the owning trucker and admins (ESC-48).
    /// </summary>
    internal static TripDto MapToTripDto(Dictionary<string, object> item, bool includePhone)
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
            BookedQuintals = GetDoubleOrNull(item, "bookedQuintals"),
            RemainingQuintals = GetDoubleOrNull(item, "remainingQuintals"),
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
    /// Get an optional double value from the item dictionary: null when the attribute is
    /// absent (the client then applies its own default), like <see cref="GetDouble"/> otherwise.
    /// </summary>
    private static double? GetDoubleOrNull(Dictionary<string, object> item, string key)
        => item.ContainsKey(key) ? GetDouble(item, key) : null;

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

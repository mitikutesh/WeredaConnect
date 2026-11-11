using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /ratings and GET /ratings/{id} Lambda handler. Read-only access to
/// ratings. Creates are handled through the /sync endpoint.
///
/// Endpoints:
/// - GET /ratings?rateeId=X - Ratings for a user (with aggregate summary)
/// - GET /ratings/{id}      - A single rating by its id
///
/// Error codes:
/// - 400: RATING_VALIDATION - Invalid/missing parameters
/// - 401: UNAUTHORIZED      - Not authenticated
/// - 403: RATING_FORBIDDEN  - The caller may not read this ratee's ratings (rating-api.md 1.2.0)
/// - 404: RATING_NOT_FOUND  - Rating does not exist
/// - 500: RATING_SERVER_ERROR - Unexpected server failure
/// </summary>
public class RatingFunction
{
    public const string ForbiddenCode = "RATING_FORBIDDEN";

    private readonly IRatingRepository _ratingRepo;

    /// <summary>
    /// ESC-29: reads the ratee's users row (role, stored rating sum and count, reputation) in one
    /// GetItem. Null when the users or reputation-events table is not configured: the role rule
    /// still holds (it needs only the caller's claim) and the summary keeps the 1.1.0 walk.
    /// </summary>
    private readonly ReputationService? _reputation;

    /// <summary><c>reputationPublicTags</c>: the summary carries the effective rating and the public reputation. Off by default.</summary>
    internal bool PublicTags { get; init; } = ReputationFlags.PublicTagsEnabled;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public RatingFunction()
    {
        var ratingsTable = Environment.GetEnvironmentVariable("RATINGS_TABLE")
                           ?? throw new InvalidOperationException("RATINGS_TABLE is not set.");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        var eventsTable = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE");

        var dynamoDb = new AmazonDynamoDBClient();
        _ratingRepo = new RatingRepository(dynamoDb, ratingsTable);
        if (!string.IsNullOrEmpty(usersTable) && !string.IsNullOrEmpty(eventsTable))
        {
            // The policy parameter is read only while the feature is on (reputation-api.md section 11).
            var settings = ReputationFlags.PenaltiesEnabled
                ? ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line))
                : ReputationPolicySettings.Defaults;
            _reputation = new ReputationService(new ReputationEventRepository(dynamoDb, eventsTable, usersTable), settings);
        }
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public RatingFunction(IRatingRepository ratingRepo, ReputationService? reputation = null)
    {
        _ratingRepo = ratingRepo ?? throw new ArgumentNullException(nameof(ratingRepo));
        _reputation = reputation;
    }

    private static bool IsParty(string? role)
        => string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuyer(string? role) => string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase);

    private static APIGatewayProxyResponse Forbidden()
        => LambdaResponse.Error(403, ForbiddenCode, "You can only read your own ratings.");

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"RatingFunction invoked: {request.HttpMethod} {request.Path}");

        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetRatingByIdAsync(request, context),
            "GET" => await HandleGetRatingsAsync(request, context),
            _ => LambdaResponse.Error(405, "RATING_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>GET /ratings?rateeId=X — ratings for a ratee plus aggregate.</summary>
    private async Task<APIGatewayProxyResponse> HandleGetRatingsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        if (!queryParams.TryGetValue("rateeId", out var rateeId) || string.IsNullOrEmpty(rateeId))
        {
            return LambdaResponse.Error(400, "RATING_VALIDATION", "rateeId query parameter is required.");
        }

        // rating-api.md 1.2.0 "Read rules": a buyer reads any farmer's or trucker's ratings, a
        // farmer or trucker only their own, nobody else anything. Decided from the caller's claim
        // before any read, so a refusal never tells whether the id exists.
        var callerRole = AuthHelper.GetClaim(request, "custom:role");
        if (!IsBuyer(callerRole) && !(IsParty(callerRole) && rateeId == authenticatedUserId))
        {
            return Forbidden();
        }

        queryParams.TryGetValue("rateeRole", out var rateeRole);
        if (string.IsNullOrEmpty(rateeRole))
        {
            rateeRole = null;
        }
        else if (rateeRole is not ("farmer" or "trucker"))
        {
            return LambdaResponse.Error(400, "RATING_VALIDATION", "rateeRole must be farmer or trucker.");
        }

        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "RATING_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 50.");
            }
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return LambdaResponse.Error(400, "RATING_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        try
        {
            // The ratee's row: its current role, the stored sum and count, the reputation.
            ReputationRead? ratee = null;
            if (_reputation != null)
            {
                ratee = await _reputation.ReadAsync(rateeId, withEvents: false);
                if (ratee == null || !IsParty(ratee.User.Role) || !string.IsNullOrEmpty(ratee.User.DeletionState))
                {
                    // No users row under this id (an unknown id, or a legacy account whose row sits
                    // under another id), a buyer or a deleted account: the 1.1.0 read, unchanged. The
                    // ratings stored for the id are listed and walked; an unknown id has none, so it
                    // still reads empty (no account oracle). No reputation is attached.
                    ratee = null;
                }
                else
                {
                    rateeRole ??= ratee.User.Role.ToLowerInvariant();
                }
            }

            // With a role filter DynamoDB applies the limit before the filter, so one Query can
            // come back short or empty with more behind it (a ratee rated in two roles): fill the
            // page and look one row ahead, as the other lists do. Without a filter, the 1.1.0 read.
            var queryResult = rateeRole == null
                ? await _ratingRepo.QueryByRateeAsync(rateeId, limit, exclusiveStartKey)
                : await PageFill.ReadAsync(
                    (count, key) => _ratingRepo.QueryByRateeAsync(rateeId, count, key, rateeRole), limit, exclusiveStartKey);
            var ratings = queryResult.Items.Select(MapToRatingDto).ToList();

            // Summary source: the users row when the role asked for is the current one and the sum
            // is exact (ratingsAggregatedAt); otherwise the 1.1.0 walk, filtered by the role.
            var isCurrentRole = ratee != null && string.Equals(ratee.User.Role, rateeRole, StringComparison.OrdinalIgnoreCase);
            double average;
            int count;
            if (isCurrentRole && !string.IsNullOrEmpty(ratee!.User.RatingsAggregatedAt))
            {
                count = (int)ratee.Ratings.Count;
                average = count > 0 ? (double)ratee.Ratings.Sum / count : 0.0;
            }
            else
            {
                (average, count) = await _ratingRepo.GetSummaryAsync(rateeId, rateeRole);
            }

            string? nextTokenResponse = null;
            if (queryResult.Item2 != null)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(queryResult.Item2);
            }

            // The effective rating and the public reputation only for the current role, with
            // reputationPublicTags on. An unstamped row has no stored rating to deduct from.
            var showReputation = PublicTags && isCurrentRole;
            var response = new GetRatingsResponse
            {
                Ratings = ratings,
                Summary = new RatingSummaryDto
                {
                    Average = average,
                    Count = count,
                    RateeRole = rateeRole,
                    EffectiveRating = showReputation && count > 0 ? ratee!.Aggregate.EffectiveRating : null
                },
                Reputation = showReputation ? ReputationReadModels.Public(ratee!, publicTags: true) : null,
                NextToken = nextTokenResponse
            };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetRatingsResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying ratings: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "RATING_SERVER_ERROR", "Failed to query ratings.");
        }
    }

    /// <summary>GET /ratings/{id} — a single rating.</summary>
    private async Task<APIGatewayProxyResponse> HandleGetRatingByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.PathParameters.TryGetValue("id", out var ratingId) || string.IsNullOrEmpty(ratingId))
        {
            return LambdaResponse.Error(400, "RATING_VALIDATION", "Missing rating ID in path.");
        }

        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        try
        {
            // rating-api.md 1.2.0 "Read rules": a buyer reads a rating of a farmer or trucker, a
            // farmer or trucker only one they received. Anyone else is refused before the read.
            var callerRole = AuthHelper.GetClaim(request, "custom:role");
            if (!IsBuyer(callerRole) && !IsParty(callerRole))
            {
                return Forbidden();
            }

            var rating = await _ratingRepo.GetByIdAsync(ratingId);
            if (IsParty(callerRole))
            {
                // The same answer for a rating of someone else and for no rating at all.
                if (rating == null || GetString(rating, "rateeId") != authenticatedUserId)
                {
                    return Forbidden();
                }
            }
            else if (rating != null && !IsParty(GetString(rating, "rateeRole")))
            {
                return Forbidden();
            }

            if (rating == null)
            {
                return LambdaResponse.Error(404, "RATING_NOT_FOUND", "Rating not found.");
            }

            var response = new GetRatingResponse { Rating = MapToRatingDto(rating) };
            return LambdaResponse.Ok(response, AppJsonContext.Default.GetRatingResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting rating {ratingId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "RATING_SERVER_ERROR", "Failed to retrieve rating.");
        }
    }

    /// <summary>Map a DynamoDB item dictionary to a RatingDto.</summary>
    internal static RatingDto MapToRatingDto(Dictionary<string, object> item)
    {
        return new RatingDto
        {
            RatingId = GetString(item, "ratingId"),
            OrderId = GetString(item, "orderId"),
            RaterId = GetString(item, "raterId"),
            RateeId = GetString(item, "rateeId"),
            RateeRole = GetString(item, "rateeRole"),
            Stars = GetInt(item, "stars"),
            Comment = GetStringOrNull(item, "comment"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            Version = GetInt(item, "version")
        };
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

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
}

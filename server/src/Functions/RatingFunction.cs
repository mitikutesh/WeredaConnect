using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

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
/// - 404: RATING_NOT_FOUND  - Rating does not exist
/// - 500: RATING_SERVER_ERROR - Unexpected server failure
/// </summary>
public class RatingFunction
{
    private readonly IRatingRepository _ratingRepo;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public RatingFunction()
    {
        var ratingsTable = Environment.GetEnvironmentVariable("RATINGS_TABLE")
                           ?? throw new InvalidOperationException("RATINGS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _ratingRepo = new RatingRepository(dynamoDb, ratingsTable);
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public RatingFunction(IRatingRepository ratingRepo)
    {
        _ratingRepo = ratingRepo ?? throw new ArgumentNullException(nameof(ratingRepo));
    }

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
            var queryResult = await _ratingRepo.QueryByRateeAsync(rateeId, limit, exclusiveStartKey);
            var ratings = queryResult.Items.Select(MapToRatingDto).ToList();

            var (average, count) = await _ratingRepo.GetSummaryAsync(rateeId);

            string? nextTokenResponse = null;
            if (queryResult.LastEvaluatedKey != null)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(queryResult.LastEvaluatedKey);
            }

            var response = new GetRatingsResponse
            {
                Ratings = ratings,
                Summary = new RatingSummaryDto { Average = average, Count = count },
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
            var rating = await _ratingRepo.GetByIdAsync(ratingId);
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
        if (!item.TryGetValue(key, out var value)) return 0;
        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}

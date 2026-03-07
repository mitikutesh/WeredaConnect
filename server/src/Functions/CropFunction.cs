using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /crops and GET /crops/{id} Lambda handler.
/// Provides read-only access to crop listings.
/// Create/update/delete operations are handled through /sync endpoint.
///
/// Endpoints:
/// - GET /crops - Query crop listings with optional filters (woreda, cropType, status)
/// - GET /crops/{id} - Get a single crop listing by ID
///
/// Error codes:
/// - 400: CROP_VALIDATION - Invalid query parameters
/// - 404: CROP_NOT_FOUND - Crop listing does not exist
/// - 500: CROP_SERVER_ERROR - Unexpected server failure
///
/// See: specs/contracts/sync-protocol.md (architectural context)
/// </summary>
public class CropFunction
{
    private readonly ICropRepository _cropRepo;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public CropFunction()
    {
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE")
                         ?? throw new InvalidOperationException("CROPS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _cropRepo = new CropRepository(dynamoDb, cropsTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public CropFunction(ICropRepository cropRepo)
    {
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"CropFunction invoked: {request.HttpMethod} {request.Path}");

        // Route based on HTTP method and path
        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetCropByIdAsync(request, context),
            "GET" => await HandleGetCropsAsync(request, context),
            _ => LambdaResponse.Error(405, "CROP_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /crops - Query crop listings with optional filters.
    /// Query parameters: woreda, cropType, status, limit, nextToken
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetCropsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // Parse query parameters
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("farmerId", out var farmerId);
        queryParams.TryGetValue("woreda", out var woreda);
        queryParams.TryGetValue("cropType", out var cropType);
        queryParams.TryGetValue("status", out var status);

        // Parse limit (default 20, max 100)
        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 100)
            {
                return LambdaResponse.Error(400, "CROP_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 100.");
            }
        }

        // Parse nextToken (base64-encoded DynamoDB LastEvaluatedKey)
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return LambdaResponse.Error(400, "CROP_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        try
        {
            // Query crops
            var queryResult = await _cropRepo.QueryCropsAsync(
                farmerId, woreda, cropType, status, limit, exclusiveStartKey);
            var items = queryResult.Items;
            var lastEvaluatedKey = queryResult.LastEvaluatedKey;

            // Map to DTOs
            var crops = items.Select(MapToCropDto).ToList();

            // Encode nextToken if more results available
            string? nextTokenResponse = null;
            if (lastEvaluatedKey != null)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(lastEvaluatedKey);
            }

            var response = new GetCropsResponse
            {
                Crops = crops,
                NextToken = nextTokenResponse
            };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetCropsResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying crops: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "CROP_SERVER_ERROR", "Failed to query crop listings.");
        }
    }

    /// <summary>
    /// GET /crops/{id} - Get a single crop listing by ID.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetCropByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.PathParameters.TryGetValue("id", out var cropId) || string.IsNullOrEmpty(cropId))
        {
            return LambdaResponse.Error(400, "CROP_VALIDATION", "Missing crop ID in path.");
        }

        try
        {
            var crop = await _cropRepo.GetByIdAsync(cropId);

            if (crop == null)
            {
                return LambdaResponse.Error(404, "CROP_NOT_FOUND", "Crop listing not found.");
            }

            // Check if deleted
            if (crop.TryGetValue("deletedAt", out var deletedAt) && deletedAt != null)
            {
                return LambdaResponse.Error(404, "CROP_NOT_FOUND", "Crop listing has been deleted.");
            }

            var cropDto = MapToCropDto(crop);
            var response = new GetCropResponse { Crop = cropDto };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetCropResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting crop {cropId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "CROP_SERVER_ERROR", "Failed to retrieve crop listing.");
        }
    }

    /// <summary>
    /// Map a DynamoDB item dictionary to a CropDto.
    /// Handles type conversions from string/number representations.
    /// </summary>
    private static CropDto MapToCropDto(Dictionary<string, object> item)
    {
        return new CropDto
        {
            CropId = GetString(item, "cropId"),
            FarmerId = GetString(item, "farmerId"),
            CropType = GetString(item, "cropType"),
            Woreda = GetString(item, "woreda"),
            QuantityKg = GetInt(item, "quantity"),
            PricePerKg = GetDecimal(item, "pricePerKg"),
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            Version = GetInt(item, "version"),
            Description = GetStringOrNull(item, "description"),
            Location = GetStringOrNull(item, "location"),
            DeletedAt = GetStringOrNull(item, "deletedAt")
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
    /// Get a required decimal value from the item dictionary.
    /// DynamoDB stores numbers as strings, so we need to parse.
    /// </summary>
    private static decimal GetDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0m;
        }

        return value switch
        {
            decimal d => d,
            double db => (decimal)db,
            float f => (decimal)f,
            string s => decimal.TryParse(s, out var result) ? result : 0m,
            _ => 0m
        };
    }

}

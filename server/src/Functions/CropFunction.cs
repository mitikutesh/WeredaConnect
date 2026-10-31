using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Crop listing reads and photo upload URLs.
/// Create/update/delete operations are handled through /sync endpoint.
///
/// Endpoints:
/// - GET /crops - Query crop listings with optional filters (woreda, cropType, status)
/// - GET /crops/{id} - Get a single crop listing by ID
/// - POST /crops/{id}/photos - Presigned PUT URLs for a listing's photos (crop-listing-api 1.6.0)
///
/// Error codes:
/// - 400: CROP_VALIDATION - Invalid query parameters, photo id, body, count, type or size
/// - 403: CROP_FORBIDDEN - Role or ownership refused
/// - 404: CROP_NOT_FOUND - Crop listing does not exist
/// - 500: CROP_SERVER_ERROR - Unexpected server failure, or photo storage not configured
///
/// See: specs/contracts/crop-listing-api.md
/// </summary>
public class CropFunction
{
    private readonly ICropRepository _cropRepo;

    // crop-listing-api 1.6.0. Null or empty when photo storage is not configured: uploads then
    // answer CROP_SERVER_ERROR and reads leave `photos` off.
    private readonly IAmazonS3? _s3Client;
    private readonly string? _incomingBucket;
    private readonly string? _photosBucket;

    /// <summary>Lifetime of a presigned PUT for one original.</summary>
    internal const int UploadUrlExpirySeconds = 900;

    /// <summary>Lifetime of a presigned GET of a thumb (list cards).</summary>
    internal static readonly TimeSpan ThumbUrlLifetime = TimeSpan.FromHours(6);

    /// <summary>Lifetime of a presigned GET of a full rendition (detail and zoom).</summary>
    internal static readonly TimeSpan FullUrlLifetime = TimeSpan.FromHours(1);

    /// <summary>The server clock for URL expiry; tests set it.</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table and bucket names from environment variables.
    /// </summary>
    public CropFunction()
    {
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE")
                         ?? throw new InvalidOperationException("CROPS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _cropRepo = new CropRepository(dynamoDb, cropsTable);
        _s3Client = new AmazonS3Client();
        _incomingBucket = Environment.GetEnvironmentVariable("CROP_PHOTO_INCOMING_BUCKET");
        _photosBucket = Environment.GetEnvironmentVariable("CROP_PHOTOS_BUCKET");
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies. Without S3 arguments photo storage is
    /// treated as not configured.
    /// </summary>
    public CropFunction(
        ICropRepository cropRepo,
        IAmazonS3? s3Client = null,
        string? incomingBucket = null,
        string? photosBucket = null)
    {
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _s3Client = s3Client;
        _incomingBucket = incomingBucket;
        _photosBucket = photosBucket;
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
            "POST" when request.PathParameters?.ContainsKey("id") == true
                        && request.Path?.EndsWith("/photos", StringComparison.Ordinal) == true
                => await HandleRequestPhotoUploadsAsync(request, context),
            _ => LambdaResponse.Error(405, "CROP_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /crops - Query crop listings with optional filters.
    /// Query parameters: woreda, cropType, status, limit, nextToken
    ///
    /// Role-based visibility (F-36 — browse-all is Buyer/Admin only):
    /// - Farmer: can only see own crops (farmerId forced to authenticated userId)
    /// - Buyer / Admin: can browse all crops
    /// - Trucker / unknown role / unauthenticated: 403 CROP_FORBIDDEN
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

        // Role-based visibility enforcement
        var authenticatedUserId = AuthHelper.GetUserId(request);
        var role = AuthHelper.GetClaim(request, "custom:role");

        if (string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
        {
            // Farmers can only see their own crop listings
            if (!string.IsNullOrEmpty(farmerId) && farmerId != authenticatedUserId)
            {
                return LambdaResponse.Error(403, "CROP_FORBIDDEN",
                    "Farmers can only view their own crop listings.");
            }
            farmerId = authenticatedUserId;
        }
        else if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase)
                 && !AdminAuthHelper.IsAdmin(request))
        {
            // F-36: only buyers and admins may browse all crop listings. Truckers,
            // unknown roles, and unauthenticated callers are forbidden (CRITICAL
            // role-visibility rule — only buyers see ALL listings).
            return LambdaResponse.Error(403, "CROP_FORBIDDEN",
                "Only buyers and administrators can browse crop listings.");
        }

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

            // ESC-48: the phone goes only to the listing's farmer and admins. A buyer browsing
            // could otherwise call the farmer and agree to trade off the platform.
            var isAdmin = AdminAuthHelper.IsAdmin(request);
            // 1.6.0: thumbs only on the list, signed here, after the role gate above.
            var crops = items
                .Select(i => MapToCropDto(i, includePhone: isAdmin || IsOwner(i, authenticatedUserId)) with
                {
                    Photos = SignPhotos(i, includeFull: false, context)
                })
                .ToList();

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
    ///
    /// Role-based visibility (F-36 — Buyer/Admin only, plus owning Farmer):
    /// - Farmer: can only view own crops (403 if crop belongs to another farmer)
    /// - Buyer / Admin: can view any crop
    /// - Trucker / unknown role / unauthenticated: 403 CROP_FORBIDDEN
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

            // Role-based visibility (F-36): farmer (own only), buyer/admin (any), else 403.
            var role = AuthHelper.GetClaim(request, "custom:role");
            if (string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
            {
                var authenticatedUserId = AuthHelper.GetUserId(request);
                var cropOwnerId = crop.TryGetValue("farmerId", out var ownerVal) ? ownerVal?.ToString() : null;
                if (cropOwnerId != authenticatedUserId)
                {
                    return LambdaResponse.Error(403, "CROP_FORBIDDEN",
                        "Farmers can only view their own crop listings.");
                }
            }
            else if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase)
                     && !AdminAuthHelper.IsAdmin(request))
            {
                return LambdaResponse.Error(403, "CROP_FORBIDDEN",
                    "Only buyers and administrators can view crop listings.");
            }

            // 1.6.0: the detail adds the full rendition, signed only after the role gate above.
            var cropDto = MapToCropDto(crop,
                includePhone: AdminAuthHelper.IsAdmin(request) || IsOwner(crop, AuthHelper.GetUserId(request))) with
            {
                Photos = SignPhotos(crop, includeFull: true, context)
            };
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
    /// POST /crops/{id}/photos (crop-listing-api 1.6.0): presigned PUT URLs for 1 to 4 originals.
    ///
    /// Farmers only. The listing may not exist yet (photos go up before the create); when it
    /// does, it must be the caller's. The key is built from the caller's own id, so a farmer can
    /// never write into another farmer's photos. Content-Type and Content-Length are both signed.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleRequestPhotoUploadsAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var callerId = AuthHelper.GetUserId(request);
        var role = AuthHelper.GetClaim(request, "custom:role");
        if (string.IsNullOrEmpty(callerId) || !string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
        {
            return LambdaResponse.Error(403, "CROP_FORBIDDEN", "Only farmers can upload crop photos.");
        }

        if (!request.PathParameters.TryGetValue("id", out var cropId) || !CropPhotoKeys.IsUuid(cropId))
        {
            return LambdaResponse.Error(400, "CROP_VALIDATION", "The listing id must be a UUID.");
        }

        CropPhotoUploadRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.CropPhotoUploadRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        var photos = body?.Photos;
        if (photos == null || photos.Count < 1 || photos.Count > CropPhotoKeys.MaxPerListing)
        {
            return LambdaResponse.Error(400, "CROP_VALIDATION",
                $"photos must list 1 to {CropPhotoKeys.MaxPerListing} photos.", "photos");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<(string PhotoId, string ContentType, long SizeBytes)>(photos.Count);
        foreach (var photo in photos)
        {
            if (photo == null || !CropPhotoKeys.IsUuid(photo.PhotoId)
                || !seen.Add(photo.PhotoId!.ToLowerInvariant()))
            {
                return LambdaResponse.Error(400, "CROP_VALIDATION",
                    "Each photoId must be a distinct UUID.", "photoId");
            }

            if (photo.ContentType == null || !CropPhotoKeys.AllowedContentTypes.Contains(photo.ContentType))
            {
                return LambdaResponse.Error(400, "CROP_VALIDATION",
                    "contentType must be image/jpeg, image/png or image/webp.", "contentType");
            }

            if (photo.SizeBytes is not long size || size < 1 || size > CropPhotoKeys.MaxOriginalBytes)
            {
                return LambdaResponse.Error(400, "CROP_VALIDATION",
                    $"sizeBytes must be between 1 and {CropPhotoKeys.MaxOriginalBytes}.", "sizeBytes");
            }

            accepted.Add((photo.PhotoId.ToLowerInvariant(), photo.ContentType, size));
        }

        if (_s3Client == null || string.IsNullOrEmpty(_incomingBucket))
        {
            context.Logger.LogError("Crop photo upload requested but CROP_PHOTO_INCOMING_BUCKET is not configured.");
            return LambdaResponse.Error(500, "CROP_SERVER_ERROR", "Photo upload is not configured.");
        }

        try
        {
            // The listing may not exist yet; when it does, only its own farmer may add photos.
            var existing = await _cropRepo.GetByIdAsync(cropId);
            if (existing != null && GetStringOrNull(existing, "farmerId") != callerId)
            {
                return LambdaResponse.Error(403, "CROP_FORBIDDEN",
                    "Farmers can only add photos to their own crop listings.");
            }

            var expires = Clock().AddSeconds(UploadUrlExpirySeconds);
            var uploads = new List<CropPhotoUploadUrl>(accepted.Count);
            foreach (var (photoId, contentType, sizeBytes) in accepted)
            {
                var presign = new GetPreSignedUrlRequest
                {
                    BucketName = _incomingBucket,
                    Key = CropPhotoKeys.IncomingKey(callerId, cropId, photoId),
                    Verb = HttpVerb.PUT,
                    Expires = expires,
                    ContentType = contentType,
                };
                // Signed with the type, so S3 refuses a PUT of any other length (SignatureDoesNotMatch).
                presign.Headers.ContentLength = sizeBytes;

                uploads.Add(new CropPhotoUploadUrl
                {
                    PhotoId = photoId,
                    UploadUrl = _s3Client.GetPreSignedURL(presign),
                    ContentType = contentType,
                    SizeBytes = sizeBytes,
                });
            }

            return LambdaResponse.Ok(new CropPhotoUploadResponse
            {
                Uploads = uploads,
                ExpiresInSeconds = UploadUrlExpirySeconds,
            }, AppJsonContext.Default.CropPhotoUploadResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error signing crop photo uploads for {cropId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "CROP_SERVER_ERROR", "Failed to create photo upload URLs.");
        }
    }

    /// <summary>
    /// 1.6.0: presigned GETs of a listing's renditions, one entry per stored photo id in order.
    /// Keys come from the item's own farmerId and cropId. Null (field omitted) when the listing
    /// has no photos, photo storage is not configured, or signing fails: a read never fails on photos.
    /// </summary>
    private List<CropPhotoDto>? SignPhotos(Dictionary<string, object> item, bool includeFull, ILambdaContext context)
    {
        if (_s3Client == null || string.IsNullOrEmpty(_photosBucket))
        {
            return null;
        }

        var photoIds = CropPhotoKeys.StoredPhotoIds(item);
        var farmerId = GetStringOrNull(item, "farmerId");
        var cropId = GetStringOrNull(item, "cropId");
        if (photoIds.Count == 0 || string.IsNullOrEmpty(farmerId) || string.IsNullOrEmpty(cropId))
        {
            return null;
        }

        try
        {
            var now = Clock();
            var photos = new List<CropPhotoDto>(photoIds.Count);
            foreach (var photoId in photoIds)
            {
                photos.Add(new CropPhotoDto
                {
                    PhotoId = photoId,
                    ThumbUrl = SignGet(CropPhotoKeys.ThumbKey(farmerId, cropId, photoId), now + ThumbUrlLifetime),
                    FullUrl = includeFull
                        ? SignGet(CropPhotoKeys.FullKey(farmerId, cropId, photoId), now + FullUrlLifetime)
                        : null,
                });
            }

            return photos;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Crop photo URLs not signed for {cropId}: [{ex.GetType().Name}]");
            return null;
        }
    }

    private string SignGet(string key, DateTime expires)
        => _s3Client!.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _photosBucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires,
        });

    /// <summary>Whether the caller is the farmer who owns this listing.</summary>
    private static bool IsOwner(Dictionary<string, object> item, string? userId)
        => !string.IsNullOrEmpty(userId) && GetStringOrNull(item, "farmerId") == userId;

    /// <summary>
    /// Map a DynamoDB item dictionary to a CropDto.
    /// Handles type conversions from string/number representations.
    /// <paramref name="includePhone"/> is true only for the owning farmer and admins (ESC-48).
    /// </summary>
    private static CropDto MapToCropDto(Dictionary<string, object> item, bool includePhone)
    {
        return new CropDto
        {
            CropId = GetString(item, "cropId"),
            FarmerId = GetString(item, "farmerId"),
            FarmerName = GetStringOrNull(item, "farmerName"),
            FarmerPhone = includePhone ? GetStringOrNull(item, "farmerPhone") : null,
            CropType = GetString(item, "cropType"),
            Woreda = GetString(item, "woreda"),
            QuantityKg = GetDecimal(item, "quantity"),
            PricePerKg = GetDecimal(item, "pricePerKg"),
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            Version = GetInt(item, "version"),
            Description = GetStringOrNull(item, "description"),
            Location = GetStringOrNull(item, "location"),
            LocationName = GetStringOrNull(item, "locationName"),
            DeletedAt = GetStringOrNull(item, "deletedAt"),
            FarmerReliability = GetDoubleOrNull(item, "farmerReliability"),
            Latitude = GetDoubleOrNull(item, "latitude"),
            Longitude = GetDoubleOrNull(item, "longitude")
        };
    }

    /// <summary>Get an optional double value from the item dictionary.</summary>
    private static double? GetDoubleOrNull(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            decimal m => (double)m,
            int i => i,
            long l => l,
            _ => double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r)
                ? r
                : null,
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

using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /match — buyer intent matching. Returns crop listings ranked by
/// relevance against the buyer's criteria, scored with <see cref="MatchScoring"/>
/// (the same weighted, multi-signal formula as the mobile on-device scorer).
///
/// Candidate generation queries the exact crop type plus accepted substitutes
/// via CropTypeCreatedAtIndex; near-miss tolerance and ranking are applied
/// in-Lambda. Geo relevance uses true haversine distance from the already-stored
/// crop coordinates to the buyer's delivery point (passed as latitude/longitude),
/// falling back to a woreda-string proxy when coordinates are unavailable.
///
/// Query parameters:
/// - cropType   (required) — the crop the buyer wants
/// - maxPricePerKg (optional) — budget ceiling (near-miss band applies above it)
/// - quantityNeeded (optional) — needed quantity (partial fills score lower)
/// - deliveryWoreda (optional) — for the woreda-string geo fallback
/// - latitude, longitude (optional) — buyer delivery point for haversine geo
/// - limit (optional, default 20, max 100) — number of ranked matches to return
///
/// Role visibility (F-36): buyers and admins only — matching reads ALL crops,
/// which only buyers/admins may do. Farmers, truckers, and unknown roles get 403.
///
/// Error codes:
/// - 400: MATCH_VALIDATION — invalid/missing query parameters
/// - 403: MATCH_FORBIDDEN — caller is not a buyer or admin
/// - 500: MATCH_SERVER_ERROR — unexpected server failure
///
/// See: specs/contracts/match-api.md
/// </summary>
public class MatchFunction
{
    private readonly ICropRepository _cropRepo;

    /// <summary>Candidates fetched per page from each crop type before scoring.</summary>
    private const int CandidateLimitPerType = 100;

    /// <summary>
    /// Safety cap on pages walked per crop type. Without paginating, only the
    /// first page of candidates is ever scored, silently hiding cheaper/closer
    /// matches once a crop type is popular. 10 pages (~1000 candidates/type) is
    /// ample for ranking; truncation beyond it is logged, not silent.
    /// </summary>
    private const int MaxCandidatePages = 10;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public MatchFunction()
    {
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE")
                         ?? throw new InvalidOperationException("CROPS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _cropRepo = new CropRepository(dynamoDb, cropsTable);
    }

    /// <summary>Constructor for testing with a mocked repository.</summary>
    public MatchFunction(ICropRepository cropRepo)
    {
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"MatchFunction invoked: {request.HttpMethod} {request.Path}");

        return request.HttpMethod switch
        {
            "GET" => await HandleGetMatchesAsync(request, context),
            _ => LambdaResponse.Error(405, "MATCH_VALIDATION", "Method not allowed.")
        };
    }

    private async Task<APIGatewayProxyResponse> HandleGetMatchesAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        // F-36: only buyers and admins may match (matching reads ALL crops).
        var role = AuthHelper.GetClaim(request, "custom:role");
        if (!string.Equals(role, "buyer", StringComparison.OrdinalIgnoreCase)
            && !AdminAuthHelper.IsAdmin(request))
        {
            return LambdaResponse.Error(403, "MATCH_FORBIDDEN",
                "Only buyers and administrators can match crop listings.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        if (!queryParams.TryGetValue("cropType", out var cropType) || string.IsNullOrWhiteSpace(cropType))
        {
            return LambdaResponse.Error(400, "MATCH_VALIDATION", "cropType is required.");
        }

        // Optional numeric/string criteria.
        if (!TryParseOptionalDouble(queryParams, "maxPricePerKg", out var maxPricePerKg)
            || !TryParseOptionalDouble(queryParams, "quantityNeeded", out var quantityNeeded)
            || !TryParseOptionalDouble(queryParams, "latitude", out var latitude)
            || !TryParseOptionalDouble(queryParams, "longitude", out var longitude))
        {
            return LambdaResponse.Error(400, "MATCH_VALIDATION",
                "maxPricePerKg, quantityNeeded, latitude and longitude must be numbers.");
        }

        queryParams.TryGetValue("deliveryWoreda", out var deliveryWoreda);

        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 100)
            {
                return LambdaResponse.Error(400, "MATCH_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 100.");
            }
        }

        var criteria = new MatchCriteria
        {
            CropType = cropType,
            MaxPricePerKg = maxPricePerKg,
            QuantityNeeded = quantityNeeded,
            DeliveryWoreda = deliveryWoreda,
            DeliveryLatitude = latitude,
            DeliveryLongitude = longitude,
            NowUtc = DateTime.UtcNow,
        };

        try
        {
            // Candidate generation: query each candidate crop type (exact +
            // substitutes). De-dupe by cropId across types.
            var seen = new HashSet<string>();
            var scored = new List<MatchDto>();

            foreach (var type in MatchScoring.CandidateCropTypes(cropType))
            {
                // Walk all candidate pages for this type (bounded) so a popular
                // crop type doesn't silently hide better matches on page 2+.
                Dictionary<string, AttributeValue>? startKey = null;
                var pages = 0;

                do
                {
                    var (items, lastKey) = await _cropRepo.QueryCropsAsync(
                        cropType: type, limit: CandidateLimitPerType, exclusiveStartKey: startKey);

                    foreach (var item in items)
                    {
                        var cropId = GetString(item, "cropId");
                        if (cropId.Length == 0 || !seen.Add(cropId))
                        {
                            continue;
                        }

                        // Only active/available listings are matchable.
                        if (!string.Equals(GetString(item, "status"), "available",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidate = ToCandidate(item, criteria.NowUtc);
                        if (!MatchScoring.IsCandidate(criteria, candidate))
                        {
                            continue;
                        }

                        var score = MatchScoring.ScoreListing(criteria, candidate);
                        scored.Add(ToMatchDto(item, candidate, score,
                            MatchScoring.IsSubstitute(cropType, candidate.CropType)));
                    }

                    startKey = lastKey;
                }
                while (startKey != null && ++pages < MaxCandidatePages);

                if (startKey != null)
                {
                    // Strip control chars from the caller-influenced crop type
                    // before logging, to prevent CloudWatch log forging (CRLF).
                    var safeType = new string(type.Where(c => !char.IsControl(c)).ToArray());
                    context.Logger.LogWarning(
                        $"Match candidate truncation for cropType={safeType}: more candidates "
                        + $"exist beyond {MaxCandidatePages} pages of {CandidateLimitPerType}.");
                }
            }

            // Rank by relevance (highest first); tie-break by cheaper price.
            scored.Sort((a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.PricePerKg.CompareTo(b.PricePerKg);
            });

            var matches = scored.Take(limit).ToList();
            var response = new GetMatchesResponse { Matches = matches, Count = matches.Count };
            return LambdaResponse.Ok(response, AppJsonContext.Default.GetMatchesResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error matching crops: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "MATCH_SERVER_ERROR", "Failed to match crop listings.");
        }
    }

    private static CropCandidate ToCandidate(Dictionary<string, object> item, DateTime nowUtc)
    {
        return new CropCandidate
        {
            CropType = GetString(item, "cropType"),
            PricePerKg = GetDouble(item, "pricePerKg") ?? 0,
            Quantity = GetDouble(item, "quantity") ?? 0,
            LocationName = GetStringOrNull(item, "locationName"),
            Latitude = GetDouble(item, "latitude"),
            Longitude = GetDouble(item, "longitude"),
            CreatedAt = ParseDate(GetStringOrNull(item, "createdAt"), nowUtc),
        };
    }

    private static MatchDto ToMatchDto(
        Dictionary<string, object> item, CropCandidate candidate, double score, bool isSubstitute)
    {
        return new MatchDto
        {
            CropId = GetString(item, "cropId"),
            FarmerId = GetString(item, "farmerId"),
            FarmerName = GetStringOrNull(item, "farmerName"),
            CropType = candidate.CropType,
            Woreda = GetString(item, "woreda"),
            QuantityKg = (int)candidate.Quantity,
            PricePerKg = (decimal)candidate.PricePerKg,
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            Latitude = candidate.Latitude,
            Longitude = candidate.Longitude,
            Score = score,
            IsSubstitute = isSubstitute,
        };
    }

    private static bool TryParseOptionalDouble(
        IDictionary<string, string> queryParams, string key, out double? value)
    {
        value = null;
        if (!queryParams.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true; // absent is valid (optional)
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static DateTime ParseDate(string? raw, DateTime fallback)
    {
        if (!string.IsNullOrEmpty(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

    private static double? GetDouble(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                ? r
                : null,
            _ => null,
        };
    }
}

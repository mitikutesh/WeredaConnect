using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class MatchFunctionTests
{
    private readonly ICropRepository _mockCropRepo;
    private readonly MatchFunction _function;
    private readonly TestLambdaContext _context;

    public MatchFunctionTests()
    {
        _mockCropRepo = Substitute.For<ICropRepository>();
        _function = new MatchFunction(_mockCropRepo);
        _context = new TestLambdaContext();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static APIGatewayProxyRequest MakeRequest(
        string role = "buyer",
        string userId = "buyer-1",
        Dictionary<string, string>? queryParams = null)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/match",
            QueryStringParameters = queryParams,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "sub", userId },
                    { "custom:role", role },
                },
            },
        };
    }

    private static Dictionary<string, object> CropItem(
        string cropId,
        string cropType = "wheat",
        string status = "available",
        double pricePerKg = 100,
        double quantity = 10,
        string locationName = "Adama",
        double? latitude = null,
        double? longitude = null)
    {
        var item = new Dictionary<string, object>
        {
            { "cropId", cropId },
            { "farmerId", "farmer-1" },
            { "farmerName", "Test Farmer" },
            { "cropType", cropType },
            { "quantity", quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "pricePerKg", pricePerKg.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "woreda", "Adama" },
            { "locationName", locationName },
            { "status", status },
            { "createdAt", "2026-01-01T00:00:00Z" },
            { "updatedAt", "2026-01-01T00:00:00Z" },
            { "version", "1" },
        };
        if (latitude.HasValue)
        {
            item["latitude"] = latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (longitude.HasValue)
        {
            item["longitude"] = longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return item;
    }

    private void StubType(string cropType, params Dictionary<string, object>[] items)
    {
        _mockCropRepo.QueryCropsAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(t => t == cropType),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((items.ToList(), (Dictionary<string, AttributeValue>?)null));
    }

    private static JsonElement ParseMatches(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("matches");

    // ── tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Match_AsBuyer_ReturnsScoredMatches()
    {
        StubType("wheat", CropItem("crop-1"));
        StubType("barley"); // no substitutes available

        var request = MakeRequest(queryParams: new Dictionary<string, string>
        {
            { "cropType", "wheat" },
            { "maxPricePerKg", "150" },
            { "quantityNeeded", "5" },
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        var matches = ParseMatches(response.Body);
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("crop-1", matches[0].GetProperty("cropId").GetString());
        Assert.False(matches[0].GetProperty("isSubstitute").GetBoolean());
    }

    [Fact]
    public async Task Match_IncludesSubstitutes_RankedBelowExact()
    {
        StubType("wheat", CropItem("wheat-1", cropType: "wheat"));
        StubType("barley", CropItem("barley-1", cropType: "barley"));

        var request = MakeRequest(queryParams: new Dictionary<string, string>
        {
            { "cropType", "wheat" },
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        var matches = ParseMatches(response.Body);
        Assert.Equal(2, matches.GetArrayLength());
        // Exact wheat outranks the barley substitute.
        Assert.Equal("wheat-1", matches[0].GetProperty("cropId").GetString());
        Assert.Equal("barley-1", matches[1].GetProperty("cropId").GetString());
        Assert.True(matches[1].GetProperty("isSubstitute").GetBoolean());
    }

    [Fact]
    public async Task Match_RanksCheaperCropHigher()
    {
        StubType("wheat",
            CropItem("expensive", pricePerKg: 100),
            CropItem("cheap", pricePerKg: 50));
        StubType("barley");

        var request = MakeRequest(queryParams: new Dictionary<string, string>
        {
            { "cropType", "wheat" },
            { "maxPricePerKg", "100" },
        });

        var response = await _function.FunctionHandler(request, _context);

        var matches = ParseMatches(response.Body);
        Assert.Equal("cheap", matches[0].GetProperty("cropId").GetString());
    }

    [Fact]
    public async Task Match_ExcludesNonAvailableListings()
    {
        StubType("wheat",
            CropItem("sold-1", status: "sold"),
            CropItem("available-1", status: "available"));
        StubType("barley");

        var request = MakeRequest(queryParams: new Dictionary<string, string>
        {
            { "cropType", "wheat" },
        });

        var response = await _function.FunctionHandler(request, _context);

        var matches = ParseMatches(response.Body);
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("available-1", matches[0].GetProperty("cropId").GetString());
    }

    [Fact]
    public async Task Match_MissingCropType_Returns400()
    {
        var request = MakeRequest(queryParams: new Dictionary<string, string>());
        var response = await _function.FunctionHandler(request, _context);
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("MATCH_VALIDATION", response.Body);
    }

    [Theory]
    [InlineData("trucker")]
    [InlineData("farmer")]
    public async Task Match_NonBuyerNonAdmin_Returns403(string role)
    {
        var request = MakeRequest(role: role, queryParams: new Dictionary<string, string>
        {
            { "cropType", "wheat" },
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("MATCH_FORBIDDEN", response.Body);
    }
}

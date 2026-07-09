using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class RatingFunctionTests
{
    private readonly IRatingRepository _mockRatingRepo;
    private readonly RatingFunction _function;
    private readonly TestLambdaContext _context;

    public RatingFunctionTests()
    {
        _mockRatingRepo = Substitute.For<IRatingRepository>();
        _function = new RatingFunction(_mockRatingRepo);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeGetRequest(
        Dictionary<string, string>? queryParams = null,
        Dictionary<string, string>? pathParams = null,
        string? userId = "buyer-001")
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/ratings",
            QueryStringParameters = queryParams,
            PathParameters = pathParams,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = userId != null
                    ? new APIGatewayCustomAuthorizerContext { { "sub", userId } }
                    : null
            }
        };
    }

    private static Dictionary<string, object> CreateRatingItem(
        string ratingId = "order-001:farmer-001",
        string rateeId = "farmer-001",
        int stars = 5)
    {
        return new Dictionary<string, object>
        {
            { "ratingId", ratingId },
            { "orderId", "order-001" },
            { "raterId", "buyer-001" },
            { "rateeId", rateeId },
            { "rateeRole", "farmer" },
            { "stars", stars.ToString() },
            { "comment", "Great quality" },
            { "createdAt", "2026-07-09T10:00:00Z" },
            { "updatedAt", "2026-07-09T10:00:00Z" },
            { "version", "1" }
        };
    }

    [Fact]
    public async Task GetRatings_MissingRateeId_Returns400()
    {
        var response = await _function.FunctionHandler(MakeGetRequest(), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("RATING_VALIDATION", response.Body);
    }

    [Fact]
    public async Task GetRatings_Unauthenticated_Returns401()
    {
        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "rateeId", "farmer-001" } },
            userId: null);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task GetRatings_ReturnsRatingsAndSummary()
    {
        _mockRatingRepo.QueryByRateeAsync("farmer-001", Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { CreateRatingItem() },
                (Dictionary<string, AttributeValue>?)null));
        _mockRatingRepo.GetSummaryAsync("farmer-001").Returns((4.5, 12));

        var request = MakeGetRequest(
            queryParams: new Dictionary<string, string> { { "rateeId", "farmer-001" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("order-001:farmer-001", response.Body);
        Assert.Contains("\"average\":4.5", response.Body);
        Assert.Contains("\"count\":12", response.Body);
    }

    [Fact]
    public async Task GetRatingById_Found_ReturnsRating()
    {
        _mockRatingRepo.GetByIdAsync("order-001:farmer-001")
            .Returns(CreateRatingItem());

        var request = MakeGetRequest(
            pathParams: new Dictionary<string, string> { { "id", "order-001:farmer-001" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("order-001:farmer-001", response.Body);
    }

    [Fact]
    public async Task GetRatingById_NotFound_Returns404()
    {
        _mockRatingRepo.GetByIdAsync(Arg.Any<string>())
            .Returns((Dictionary<string, object>?)null);

        var request = MakeGetRequest(
            pathParams: new Dictionary<string, string> { { "id", "missing:x" } });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("RATING_NOT_FOUND", response.Body);
    }
}

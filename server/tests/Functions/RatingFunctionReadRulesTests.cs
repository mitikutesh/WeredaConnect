using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-29 (rating-api.md 1.2.0 "Read rules"): a buyer reads any farmer's or trucker's ratings, a
/// farmer or trucker only their own; the list and the summary are for one <c>rateeRole</c>; the
/// summary comes from the users row once the sum is exact; the effective rating and the public
/// reputation appear only with <c>reputationPublicTags</c> on.
/// </summary>
public class RatingFunctionReadRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRatingRepository _ratings = Substitute.For<IRatingRepository>();
    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0);
    private readonly TestLambdaContext _ctx = new();

    public RatingFunctionReadRulesTests()
    {
        var farmer = _repo.AddUser("farmer-1");
        farmer.RatingSum = 46;
        farmer.RatingCount = 10;
        _repo.AddUser("farmer-2");
        _repo.AddUser("trucker-1", "trucker");
        _repo.AddUser("buyer-2", "buyer");
        _ratings.QueryByRateeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<string?>())
            .Returns((new List<Dictionary<string, object>> { Rating("order-1:farmer-1", "farmer-1", "farmer") },
                (Dictionary<string, AttributeValue>?)null));
        _ratings.GetSummaryAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns((3.0, 2));
        _ratings.GetByIdAsync("order-1:farmer-1").Returns(Rating("order-1:farmer-1", "farmer-1", "farmer"));
        _ratings.GetByIdAsync("order-2:trucker-1").Returns(Rating("order-2:trucker-1", "trucker-1", "trucker"));
    }

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask);

    private RatingFunction Function(bool publicTags = false) => new(_ratings, Service()) { PublicTags = publicTags };

    // ===== GET /ratings?rateeId= : who may read =====

    [Theory]
    [InlineData("buyer-1", "buyer", "farmer-1", 200)]
    [InlineData("buyer-1", "buyer", "trucker-1", 200)]
    [InlineData("farmer-1", "farmer", "farmer-1", 200)]
    [InlineData("trucker-1", "trucker", "trucker-1", 200)]
    [InlineData("farmer-1", "farmer", "farmer-2", 403)]
    [InlineData("farmer-1", "farmer", "trucker-1", 403)]
    [InlineData("trucker-1", "trucker", "farmer-1", 403)]
    [InlineData("farmer-1", "farmer", "ghost", 403)]
    [InlineData("admin-1", "admin", "farmer-1", 403)]
    [InlineData("someone", null, "farmer-1", 403)]
    public async Task List_FollowsTheReadRules(string caller, string? role, string ratee, int status)
    {
        var response = await Function().FunctionHandler(List(caller, role, ratee), _ctx);

        response.StatusCode.Should().Be(status, response.Body);
        if (status == 403)
        {
            response.Body.Should().Contain("RATING_FORBIDDEN");
            await _ratings.DidNotReceiveWithAnyArgs().QueryByRateeAsync(default!);
            _repo.Committed.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("buyer-2")]
    [InlineData("ghost")]
    public async Task List_ABuyerAskingAboutABuyerOrAnUnknownId_GetsAnEmptyAnswer(string ratee)
    {
        var body = await Ok(List("buyer-1", "buyer", ratee));

        body.GetProperty("ratings").GetArrayLength().Should().Be(0);
        body.GetProperty("summary").GetProperty("average").GetDouble().Should().Be(0);
        body.GetProperty("summary").GetProperty("count").GetInt32().Should().Be(0);
        await _ratings.DidNotReceiveWithAnyArgs().QueryByRateeAsync(default!);
    }

    // ===== the role filter and the summary source =====

    [Fact]
    public async Task List_DefaultsToTheCurrentRole_AndTakesTheSummaryFromTheUsersRow()
    {
        var body = await Ok(List("buyer-1", "buyer", "farmer-1"));

        await _ratings.Received(1).QueryByRateeAsync("farmer-1", 20, null, "farmer");
        await _ratings.DidNotReceiveWithAnyArgs().GetSummaryAsync(default!);
        var summary = body.GetProperty("summary");
        summary.GetProperty("average").GetDouble().Should().Be(4.6);
        summary.GetProperty("count").GetInt32().Should().Be(10);
        summary.GetProperty("rateeRole").GetString().Should().Be("farmer");
    }

    [Fact]
    public async Task List_AnotherRole_IsFiltered_AndWalked_WithNoReputation()
    {
        var body = await Ok(List("buyer-1", "buyer", "farmer-1", "trucker"), publicTags: true);

        await _ratings.Received(1).QueryByRateeAsync("farmer-1", 20, null, "trucker");
        await _ratings.Received(1).GetSummaryAsync("farmer-1", "trucker");
        body.GetProperty("summary").GetProperty("rateeRole").GetString().Should().Be("trucker");
        body.GetProperty("summary").TryGetProperty("effectiveRating", out _).Should().BeFalse();
        body.TryGetProperty("reputation", out _).Should().BeFalse();
    }

    [Fact]
    public async Task List_ARowTheBackfillSkipped_KeepsTheWalk()
    {
        _repo.Users["farmer-1"].RatingsAggregatedAt = null;

        var body = await Ok(List("buyer-1", "buyer", "farmer-1"));

        await _ratings.Received(1).GetSummaryAsync("farmer-1", "farmer");
        body.GetProperty("summary").GetProperty("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task List_ABadRole_IsRefused()
    {
        var response = await Function().FunctionHandler(List("buyer-1", "buyer", "farmer-1", "buyer"), _ctx);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("RATING_VALIDATION");
    }

    // ===== reputationPublicTags =====

    [Fact]
    public async Task List_PublicTagsOn_AddsTheEffectiveRating_AndThePublicReputation_NeverThePoints()
    {
        await Service().RecordAsync(new ReputationCandidate
        {
            UserId = "farmer-1", EventId = "CANCEL#o1", Code = ReputationCodes.LateCancel, Party = "farmer",
            OrderId = "o1", BuyerId = "buyer-9", OccurredAt = T0.AddDays(-1)
        }, timeoutPenaltiesOn: true, new TestLambdaLogger());

        var response = await Function(publicTags: true).FunctionHandler(List("buyer-1", "buyer", "farmer-1"), _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        var body = JsonDocument.Parse(response.Body).RootElement;
        body.GetProperty("summary").GetProperty("effectiveRating").GetDecimal().Should().Be(4.2m);
        var reputation = body.GetProperty("reputation");
        reputation.GetProperty("userId").GetString().Should().Be("farmer-1");
        reputation.GetProperty("tier").GetString().Should().Be("good", "warning reads as good");
        reputation.GetProperty("effectiveRating").GetDecimal().Should().Be(4.2m);
        response.Body.Should().NotContain("activePoints").And.NotContain("warning");
    }

    [Fact]
    public async Task List_PublicTagsOff_HasNeither()
    {
        var body = await Ok(List("buyer-1", "buyer", "farmer-1"), publicTags: false);

        body.GetProperty("summary").TryGetProperty("effectiveRating", out _).Should().BeFalse();
        body.TryGetProperty("reputation", out _).Should().BeFalse();
    }

    // ===== GET /ratings/{id} =====

    [Theory]
    [InlineData("buyer-1", "buyer", "order-1:farmer-1", 200)]
    [InlineData("buyer-1", "buyer", "order-2:trucker-1", 200)]
    [InlineData("buyer-1", "buyer", "order-9:nobody", 404)]
    [InlineData("farmer-1", "farmer", "order-1:farmer-1", 200)]
    [InlineData("trucker-1", "trucker", "order-2:trucker-1", 200)]
    [InlineData("farmer-2", "farmer", "order-1:farmer-1", 403)]
    [InlineData("trucker-1", "trucker", "order-1:farmer-1", 403)]
    [InlineData("farmer-1", "farmer", "order-9:nobody", 403)]
    [InlineData("admin-1", "admin", "order-1:farmer-1", 403)]
    [InlineData("someone", null, "order-1:farmer-1", 403)]
    public async Task ById_FollowsTheReadRules(string caller, string? role, string ratingId, int status)
    {
        var request = Request(caller, role);
        request.PathParameters = new Dictionary<string, string> { { "id", ratingId } };

        var response = await Function().FunctionHandler(request, _ctx);

        response.StatusCode.Should().Be(status, response.Body);
    }

    // ===== helpers =====

    private async Task<JsonElement> Ok(APIGatewayProxyRequest request, bool publicTags = false)
    {
        var response = await Function(publicTags).FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static APIGatewayProxyRequest List(string caller, string? role, string ratee, string? rateeRole = null)
    {
        var request = Request(caller, role);
        request.QueryStringParameters = new Dictionary<string, string> { { "rateeId", ratee } };
        if (rateeRole != null)
        {
            request.QueryStringParameters["rateeRole"] = rateeRole;
        }

        return request;
    }

    private static APIGatewayProxyRequest Request(string caller, string? role)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", caller } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/ratings",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static Dictionary<string, object> Rating(string id, string ratee, string role) => new()
    {
        { "ratingId", id }, { "orderId", id.Split(':')[0] }, { "raterId", "buyer-1" }, { "rateeId", ratee },
        { "rateeRole", role }, { "stars", "5" }, { "createdAt", "2026-07-09T10:00:00Z" },
        { "updatedAt", "2026-07-09T10:00:00Z" }, { "version", "1" }
    };
}

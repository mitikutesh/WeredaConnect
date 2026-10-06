using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class LogoutFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly LogoutFunction _function;
    private readonly TestLambdaContext _context;

    public LogoutFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _function = new LogoutFunction(_mockAuth);
        _context = new TestLambdaContext();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>A logout request from <paramref name="callerId"/> (null: no authorizer claims).</summary>
    private static APIGatewayProxyRequest MakeRequest(object body, string? callerId = "user-A")
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, SerializerOptions)
        };
        if (callerId != null)
        {
            request.RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", callerId } }
            };
        }

        return request;
    }

    private static string MakeJwt(string sub)
    {
        var payload = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{{\"sub\":\"{sub}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{payload}.signature";
    }

    /// <summary>The owner lookup (REFRESH_TOKEN_AUTH) says the token belongs to <paramref name="sub"/>.</summary>
    private void TokenBelongsTo(string refreshToken, string sub, string? idToken = null)
    {
        _mockAuth.RefreshAsync(refreshToken).Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
        {
            AccessToken = "minted-access",
            IdToken = idToken ?? MakeJwt(sub),
            RefreshToken = null,
            ExpiresIn = 3600
        }));
    }

    [Fact]
    public async Task FunctionHandler_ValidLogout_Returns200()
    {
        TokenBelongsTo("valid-refresh", "user-A");
        _mockAuth.LogoutAsync("valid-refresh")
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest(new { refreshToken = "valid-refresh" }, callerId: "user-A");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"loggedOut\":true", response.Body);
        await _mockAuth.Received(1).LogoutAsync("valid-refresh");
    }

    [Fact]
    public async Task FunctionHandler_MissingToken_Returns400()
    {
        var request = MakeRequest(new { refreshToken = "" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_017", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "bad-json" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_IncludesSecurityHeaders()
    {
        TokenBelongsTo("some-token", "user-A");
        _mockAuth.LogoutAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest(new { refreshToken = "some-token" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    // --- SIM-60: the refresh token must belong to the signed-in user ---

    [Fact]
    public async Task Logout_AnotherUsersRefreshToken_Returns403AndDoesNotRevoke()
    {
        // user-A holds user-B's refresh token (shared phone, leaked storage): RevokeToken
        // would revoke it for user-B, so the request is refused before any revoke.
        TokenBelongsTo("user-b-refresh", "user-B");

        var response = await _function.FunctionHandler(
            MakeRequest(new { refreshToken = "user-b-refresh" }, callerId: "user-A"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_026", response.Body);
        Assert.DoesNotContain("user-b-refresh", response.Body);
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Logout_ProductionClaimsShape_BindsToTheClaimsSub()
    {
        // Payload 1.0 behind the JWT authorizer nests the claims under "claims" as a
        // JsonElement; on 2.0 the caller id was null in production.
        TokenBelongsTo("user-b-refresh", "user-B");
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new { refreshToken = "user-b-refresh" }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "claims", JsonDocument.Parse("{\"sub\":\"user-A\"}").RootElement }
                }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_026", response.Body);
        await _mockAuth.Received(1).RefreshAsync("user-b-refresh");
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Logout_NoCallerIdentity_Returns401AndMakesNoCognitoCall()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { refreshToken = "some-refresh" }, callerId: null), _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("AUTH_020", response.Body);
        await _mockAuth.DidNotReceive().RefreshAsync(Arg.Any<string>());
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Logout_InactiveRefreshToken_Returns200WithoutRevoke()
    {
        // Unknown, expired or already revoked: nothing live to revoke, logout stays idempotent.
        _mockAuth.RefreshAsync("stale-refresh").Returns(AuthResult<CognitoAuthTokens>.Failure(
            401, "AUTH_018", "Refresh token is invalid or expired.", "refreshToken"));

        var response = await _function.FunctionHandler(
            MakeRequest(new { refreshToken = "stale-refresh" }), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"loggedOut\":true", response.Body);
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(429, "AUTH_006")]
    [InlineData(503, "AUTH_008")]
    public async Task Logout_OwnerLookupFails_ReturnsErrorAndDoesNotRevoke(int status, string code)
    {
        _mockAuth.RefreshAsync("some-refresh").Returns(AuthResult<CognitoAuthTokens>.Failure(
            status, code, "Cognito said no."));

        var response = await _function.FunctionHandler(
            MakeRequest(new { refreshToken = "some-refresh" }), _context);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(code, response.Body);
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Logout_IdTokenWithoutSub_Returns403AndDoesNotRevoke()
    {
        // Fail closed: an owner that cannot be read is not the caller.
        TokenBelongsTo("odd-refresh", "user-A", idToken: "not-a-jwt");

        var response = await _function.FunctionHandler(
            MakeRequest(new { refreshToken = "odd-refresh" }, callerId: "user-A"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_026", response.Body);
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }
}

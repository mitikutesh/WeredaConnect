using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Tests.Functions;

public class RefreshFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly RefreshFunction _function;
    private readonly TestLambdaContext _context;

    public RefreshFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _function = new RefreshFunction(_mockAuth);
        _context = new TestLambdaContext();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(object body) => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    [Fact]
    public async Task FunctionHandler_ValidRefresh_Returns200WithNewTokens()
    {
        _mockAuth.RefreshAsync("valid-refresh")
            .Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = "new-access",
                IdToken = "new-id",
                RefreshToken = null,
                ExpiresIn = 3600
            }));

        var request = MakeRequest(new { refreshToken = "valid-refresh" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("new-access", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidRefreshToken_Returns401()
    {
        _mockAuth.RefreshAsync("expired")
            .Returns(AuthResult<CognitoAuthTokens>.Failure(
                401, "AUTH_018", "Refresh token expired.", "refreshToken"));

        var request = MakeRequest(new { refreshToken = "expired" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("AUTH_018", response.Body);
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
        var request = new APIGatewayProxyRequest { Body = "not-json" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    // ===== Suspended accounts cannot refresh =====

    private static string FakeIdToken(string sub)
    {
        static string B64(string raw) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"sub\":\"{sub}\"}}")}.sig";
    }

    private static UserDto Profile(string userId, bool suspended) => new()
    {
        UserId = userId,
        PhoneNumber = "+251912345678",
        Role = "farmer",
        Woreda = "Adama",
        IsPhoneVerified = true,
        Suspended = suspended,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    private (RefreshFunction fn, IUserRepository repo) FunctionWithProfile(UserDto? profile)
    {
        var repo = Substitute.For<IUserRepository>();
        repo.GetUserByIdAsync("user-1").Returns(profile);
        _mockAuth.RefreshAsync("refresh-token").Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
        {
            AccessToken = "new-access",
            IdToken = FakeIdToken("user-1"),
            ExpiresIn = 900
        }));
        _mockAuth.LogoutAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        return (new RefreshFunction(_mockAuth, repo), repo);
    }

    [Fact]
    public async Task FunctionHandler_SuspendedProfile_Returns403AndRevokesRefreshToken()
    {
        var (fn, _) = FunctionWithProfile(Profile("user-1", suspended: true));

        var response = await fn.FunctionHandler(MakeRequest(new { refreshToken = "refresh-token" }), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_025", response.Body);
        Assert.DoesNotContain("new-access", response.Body);
        await _mockAuth.Received(1).LogoutAsync("refresh-token");
    }

    [Fact]
    public async Task FunctionHandler_ActiveProfile_Returns200()
    {
        var (fn, _) = FunctionWithProfile(Profile("user-1", suspended: false));

        var response = await fn.FunctionHandler(MakeRequest(new { refreshToken = "refresh-token" }), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("new-access", response.Body);
        await _mockAuth.DidNotReceive().LogoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_MissingProfile_DoesNotBlockRefresh()
    {
        var (fn, _) = FunctionWithProfile(null);

        var response = await fn.FunctionHandler(MakeRequest(new { refreshToken = "refresh-token" }), _context);

        Assert.Equal(200, response.StatusCode);
    }
}

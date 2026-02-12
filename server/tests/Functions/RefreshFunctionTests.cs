using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

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
}

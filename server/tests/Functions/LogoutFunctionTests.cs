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

    private static APIGatewayProxyRequest MakeRequest(object body) => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    [Fact]
    public async Task FunctionHandler_ValidLogout_Returns200()
    {
        _mockAuth.LogoutAsync("valid-refresh")
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest(new { refreshToken = "valid-refresh" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"loggedOut\":true", response.Body);
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
        _mockAuth.LogoutAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest(new { refreshToken = "some-token" });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }
}

using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class LoginFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly IUserRepository _mockRepo;
    private readonly IRateLimitService _mockRateLimit;
    private readonly LoginFunction _function;
    private readonly TestLambdaContext _context;

    public LoginFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _function = new LoginFunction(_mockAuth, _mockRepo, _mockRateLimit);
        _context = new TestLambdaContext();

        // Default: rate limit allows login
        _mockRateLimit.CheckLoginAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(object body) => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    private static UserDto TestUser => new()
    {
        UserId = "user-123",
        PhoneNumber = "+251912345678",
        Role = "farmer",
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private static CognitoAuthTokens TestTokens => new()
    {
        AccessToken = "access-tok",
        IdToken = "id-tok",
        RefreshToken = "refresh-tok",
        ExpiresIn = 3600
    };

    [Fact]
    public async Task FunctionHandler_ValidLogin_Returns200WithTokensAndUser()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(TestTokens));
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(TestUser);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("access-tok", response.Body);
        Assert.Contains("user-123", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidLogin_ResetsFailureCounter()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(TestTokens));
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(TestUser);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).ResetLoginAttemptsAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_InvalidCredentials_Returns401()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014", "Invalid credentials."));
        _mockRateLimit.RecordFailedLoginAsync(Arg.Any<string>()).Returns(1);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "wrong"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("AUTH_014", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidCredentials_RecordsFailedAttempt()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014", "Invalid credentials."));
        _mockRateLimit.RecordFailedLoginAsync(Arg.Any<string>()).Returns(1);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "wrong"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).RecordFailedLoginAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_ThirdFailedAttempt_Returns403Lockout()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014", "Invalid credentials."));
        _mockRateLimit.RecordFailedLoginAsync(Arg.Any<string>()).Returns(3);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "wrong"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_015", response.Body);
        Assert.Contains("locked", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_AccountLocked_Returns403BeforeCognito()
    {
        _mockRateLimit.CheckLoginAllowedAsync("+251912345678")
            .Returns(AuthResult<bool>.Failure(403, "AUTH_015", "Account locked."));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_015", response.Body);
        // Cognito should NOT have been called
        await _mockAuth.DidNotReceive().LoginAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_ServiceError_DoesNotRecordFailedAttempt()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Failure(503, "AUTH_008", "Service unavailable."));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.DidNotReceive().RecordFailedLoginAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_UserNotConfirmed_Returns403()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Failure(403, "AUTH_016", "Not confirmed."));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_016", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UserNotFoundInDb_Returns404()
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(TestTokens));
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns((UserDto?)null);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("AUTH_013", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "{{bad" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidationFails_Returns400()
    {
        var request = MakeRequest(new
        {
            phoneNumber = "bad",
            password = ""
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }
}
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

        // Default: the source IP is under its cap
        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Success(true));

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
    public async Task FunctionHandler_SuspendedAccount_Returns403WithoutTokens()
    {
        // Admin-suspended accounts must not authenticate even with valid
        // credentials: Cognito succeeds, but the profile's Suspended flag blocks
        // the login with AUTH_025 and no tokens may leak in the response.
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(TestTokens));
        _mockRepo.GetUserByPhoneAsync("+251912345678")
            .Returns(TestUser with { Suspended = true });

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_025", response.Body);
        Assert.DoesNotContain("access-tok", response.Body);
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

    /// <summary>
    /// Build a structurally valid JWT whose payload carries the given sub.
    /// Signature is garbage — the function only decodes the payload of a
    /// token Cognito itself just issued.
    /// </summary>
    private static string MakeJwt(string sub)
    {
        var payload = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{{\"sub\":\"{sub}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{payload}.signature";
    }

    [Fact]
    public async Task FunctionHandler_ResolvesProfileByTokenSub_NotByPhone()
    {
        // The profile row matching the Cognito sub must win: a phone lookup
        // can hit a stale duplicate row whose userId the server would then
        // reject on every authenticated write (e.g. message sends).
        var tokens = new CognitoAuthTokens
        {
            AccessToken = "access-tok",
            IdToken = MakeJwt("sub-42"),
            RefreshToken = "refresh-tok",
            ExpiresIn = 3600
        };
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(tokens));
        _mockRepo.GetUserByIdAsync("sub-42").Returns(new UserDto
        {
            UserId = "sub-42",
            PhoneNumber = "+251912345678",
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("sub-42", response.Body);
        await _mockRepo.DidNotReceive().GetUserByPhoneAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_SubRowMissing_FallsBackToPhoneLookup()
    {
        var tokens = new CognitoAuthTokens
        {
            AccessToken = "access-tok",
            IdToken = MakeJwt("sub-42"),
            RefreshToken = "refresh-tok",
            ExpiresIn = 3600
        };
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(tokens));
        _mockRepo.GetUserByIdAsync("sub-42").Returns((UserDto?)null);
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(TestUser);

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("user-123", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_NonJwtIdToken_StillResolvesByPhone()
    {
        // Existing behavior for opaque test tokens: no sub → phone lookup.
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
        Assert.Contains("user-123", response.Body);
    }

    // ===== Per-IP cap and device tracking =====

    private void SetupSuccessfulLogin(UserDto user)
    {
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = "access-token",
                IdToken = "id-token",
                RefreshToken = "refresh-token",
                ExpiresIn = 900
            }));
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(user);
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = ci.Arg<int>() + 1 }));
    }

    private static APIGatewayProxyRequest LoginFromDevice(string deviceId, string deviceName = "Samsung A12")
    {
        var request = MakeRequest(new { phoneNumber = "+251912345678", password = "secure123" });
        request.Headers = new Dictionary<string, string>
        {
            { "X-Device-Id", deviceId },
            { "X-Device-Name", deviceName }
        };
        return request;
    }

    [Fact]
    public async Task FunctionHandler_IpCapReached_Returns429WithoutTouchingCognito()
    {
        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), "login", Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many requests from this network.", retryAfterSeconds: 120));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = "+251912345678", password = "secure123" }), _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("120", response.Headers["Retry-After"]);
        await _mockAuth.DidNotReceive().LoginAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FirstDevice_IsRememberedButNotFlaggedNew()
    {
        SetupSuccessfulLogin(TestUser);

        var response = await _function.FunctionHandler(LoginFromDevice("device-aaaa-1111"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"newDevice\":false", response.Body);
        await _mockRepo.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.KnownDevices.Contains("device-aaaa-1111")), 1);
    }

    [Fact]
    public async Task FunctionHandler_UnknownDevice_IsFlaggedNewAndSmsSentOnlyWhenEnabled()
    {
        var sms = Substitute.For<ISmsSender>();
        sms.SendAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var function = new LoginFunction(_mockAuth, _mockRepo, _mockRateLimit, sms);
        SetupSuccessfulLogin(TestUser with { KnownDevices = ["device-known-0000"] });

        Environment.SetEnvironmentVariable("NEW_DEVICE_SMS_ENABLED", null);
        var quiet = await function.FunctionHandler(LoginFromDevice("device-bbbb-2222"), _context);
        Assert.Contains("\"newDevice\":true", quiet.Body);
        await sms.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<string>());

        Environment.SetEnvironmentVariable("NEW_DEVICE_SMS_ENABLED", "true");
        try
        {
            var noisy = await function.FunctionHandler(LoginFromDevice("device-cccc-3333", "Tecno <a href=x>Spark"), _context);
            Assert.Contains("\"newDevice\":true", noisy.Body);
            await sms.Received(1).SendAsync("+251912345678", Arg.Is<string>(m => m.Contains("Tecno a hrefxSpark") && !m.Contains("<")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEW_DEVICE_SMS_ENABLED", null);
        }
    }

    [Fact]
    public async Task FunctionHandler_KnownDevice_IsNotFlaggedAndNothingIsWritten()
    {
        SetupSuccessfulLogin(TestUser with { KnownDevices = ["device-known-0000"] });

        var response = await _function.FunctionHandler(LoginFromDevice("device-known-0000"), _context);

        Assert.Contains("\"newDevice\":false", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task FunctionHandler_MalformedDeviceId_IsIgnored()
    {
        SetupSuccessfulLogin(TestUser);

        var response = await _function.FunctionHandler(LoginFromDevice("bad id with spaces!"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }
}

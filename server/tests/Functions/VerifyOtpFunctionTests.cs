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

public class VerifyOtpFunctionTests
{
    private const string Phone = "+251912345678";

    private readonly ICognitoAuthService _mockAuth;
    private readonly IUserRepository _mockRepo;
    private readonly IRateLimitService _mockRateLimit;
    private readonly IOtpService _mockOtp;
    private readonly ISmsSender _mockSms;
    private readonly VerifyOtpFunction _function;
    private readonly TestLambdaContext _context;

    public VerifyOtpFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _mockOtp = Substitute.For<IOtpService>();
        _mockSms = Substitute.For<ISmsSender>();
        _function = new VerifyOtpFunction(_mockAuth, _mockRepo, _mockRateLimit, _mockOtp, _mockSms);
        _context = new TestLambdaContext();

        // Default: the source IP is under its cap
        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Success(true));

        _mockRateLimit.CheckOtpAttemptAllowedAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        _mockRateLimit.CheckOtpSendAllowedAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        _mockOtp.VerifyAsync(Phone, "482913").Returns(AuthResult<string>.Success("user-1"));
        _mockOtp.VerifyAsync(Phone, Arg.Is<string>(c => c != "482913"))
            .Returns(AuthResult<string>.Failure(400, "AUTH_012", "Invalid or expired code.", "otpCode"));
        _mockOtp.IssueAsync(Phone, Arg.Any<string>()).Returns(AuthResult<OtpIssueResult>.Success(new OtpIssueResult
        {
            Code = "111222",
            ExpiresAt = "2026-09-25T12:05:00Z"
        }));
        _mockSms.SendAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _mockAuth.AdminConfirmSignUpAsync(Phone).Returns(AuthResult<bool>.Success(true));
        _mockAuth.LoginAsync(Phone, "secure123").Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
        {
            AccessToken = "access-token",
            IdToken = "id-token",
            RefreshToken = "refresh-token",
            ExpiresIn = 900
        }));
        _mockRepo.GetUserByPhoneAsync(Phone).Returns(PendingUser());
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = ci.Arg<int>() + 1 }));
    }

    private static UserDto PendingUser(bool verified = false) => new()
    {
        UserId = "user-1",
        PhoneNumber = Phone,
        Role = "farmer",
        Woreda = "Adama",
        PreferredLanguage = "am",
        IsPhoneVerified = verified,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(object body, string path = "/auth/verify-otp") => new()
    {
        Path = path,
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    // ===== verify-otp =====

    [Fact]
    public async Task Verify_ValidCode_ConfirmsPhoneAndReturnsTokens()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("access-token", response.Body);
        Assert.Contains("\"isPhoneVerified\":true", response.Body);
        await _mockAuth.Received(1).AdminConfirmSignUpAsync(Phone);
        await _mockRepo.Received(1).UpdateUserAsync(Arg.Is<UserDto>(u => u.IsPhoneVerified), 1);
        await _mockRateLimit.Received(1).ResetOtpAttemptsAsync(Phone);
    }

    [Fact]
    public async Task Verify_LocalPhoneFormat_IsNormalised()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = "0912345678", otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockOtp.Received(1).VerifyAsync(Phone, "482913");
    }

    [Fact]
    public async Task Verify_WrongCode_Returns400AndCountsAttempt()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "000000", password = "secure123" }), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_012", response.Body);
        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync(Phone);
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
        // Only the password pre-check reached Cognito; no tokens were minted.
        await _mockAuth.Received(1).LoginAsync(Phone, "secure123");
        Assert.DoesNotContain("access-token", response.Body);
    }

    [Fact]
    public async Task Verify_TooManyAttempts_ReturnsLimitError()
    {
        _mockRateLimit.CheckOtpAttemptAllowedAsync(Phone)
            .Returns(AuthResult<bool>.Failure(400, "AUTH_012", "Maximum OTP verification attempts exceeded.", "otpCode"));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(400, response.StatusCode);
        await _mockOtp.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Verify_BadFormat_Returns400()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "12ab", password = "secure123" }), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_011", response.Body);
    }

    [Fact]
    public async Task Verify_MissingPassword_Returns400()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "" }), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("password", response.Body);
    }

    [Fact]
    public async Task Verify_AlreadyVerifiedUser_SkipsConfirmAndLogsIn()
    {
        _mockRepo.GetUserByPhoneAsync(Phone).Returns(PendingUser(verified: true));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Verify_WrongPassword_IsRefusedBeforeTheCodeIsConsumed()
    {
        _mockAuth.LoginAsync(Phone, "wrong")
            .Returns(AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014", "Invalid credentials."));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "wrong" }), _context);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("AUTH_014", response.Body);
        await _mockOtp.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Verify_UnconfirmedUser_CorrectPasswordAnswersNotConfirmed_ThenConfirms()
    {
        // Cognito reports an unconfirmed user only after the password matched.
        _mockAuth.LoginAsync(Phone, "secure123").Returns(
            AuthResult<CognitoAuthTokens>.Failure(403, "AUTH_016", "Phone number not verified."),
            AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = "access-token",
                IdToken = "id-token",
                RefreshToken = "refresh-token",
                ExpiresIn = 900
            }));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("access-token", response.Body);
        await _mockAuth.Received(1).AdminConfirmSignUpAsync(Phone);
        await _mockAuth.Received(2).LoginAsync(Phone, "secure123");
    }

    [Fact]
    public async Task Verify_CodeBoundToAnotherRegistration_IsRejected()
    {
        _mockOtp.VerifyAsync(Phone, "482913").Returns(AuthResult<string>.Success("user-other"));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_012", response.Body);
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Verify_DevBypassCode_IsNotBoundToARegistration()
    {
        _mockOtp.VerifyAsync(Phone, "123456").Returns(AuthResult<string>.Success(IOtpService.AnyUser));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "123456", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockAuth.Received(1).AdminConfirmSignUpAsync(Phone);
    }

    [Fact]
    public async Task Verify_VersionConflict_RetriesWithFreshProfile()
    {
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 1)
            .Returns(AuthResult<UserDto>.Failure(409, "AUTH_019", "Modified.", "version"));
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 2)
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = 3 }));
        _mockRepo.GetUserByPhoneAsync(Phone).Returns(PendingUser(), PendingUser() with { Version = 2 });

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockRepo.Received(1).UpdateUserAsync(Arg.Any<UserDto>(), 2);
    }

    [Fact]
    public async Task Verify_SuspendedUser_Returns403()
    {
        _mockRepo.GetUserByPhoneAsync(Phone).Returns(PendingUser() with { Suspended = true });

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone, otpCode = "482913", password = "secure123" }), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_025", response.Body);
        Assert.DoesNotContain("access-token", response.Body);
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
    }

    // ===== resend-otp =====

    [Fact]
    public async Task Resend_UnknownPhone_Returns200WithoutSending()
    {
        _mockRepo.GetUserByPhoneAsync(Phone).Returns((UserDto?)null);

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone }, "/auth/resend-otp"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("2026-09-25T12:05:00Z", response.Body);
        await _mockOtp.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockSms.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Resend_AlreadyVerified_Returns200WithoutSending()
    {
        _mockRepo.GetUserByPhoneAsync(Phone).Returns(PendingUser(verified: true));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone }, "/auth/resend-otp"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockOtp.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Resend_PendingUser_IssuesCodeAndSendsSms()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone }, "/auth/resend-otp"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("2026-09-25T12:05:00Z", response.Body);
        await _mockSms.Received(1).SendAsync(Phone, Arg.Is<string>(m => m.Contains("111222")));
        await _mockRateLimit.Received(1).RecordOtpSendAsync(Phone);
        await _mockOtp.Received(1).IssueAsync(Phone, "user-1");
    }

    [Fact]
    public async Task Resend_DailyCapReached_Returns429()
    {
        _mockRateLimit.CheckOtpSendAllowedAsync(Phone)
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many codes today."));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone }, "/auth/resend-otp"), _context);

        Assert.Equal(429, response.StatusCode);
        await _mockOtp.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Resend_WithinCooldown_Returns429()
    {
        _mockOtp.IssueAsync(Phone, Arg.Any<string>())
            .Returns(AuthResult<OtpIssueResult>.Failure(429, "AUTH_006", "Please wait.", retryAfterSeconds: 42));

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = Phone }, "/auth/resend-otp"), _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("42", response.Headers["Retry-After"]);
        await _mockSms.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Resend_InvalidPhone_Returns400()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = "12" }, "/auth/resend-otp"), _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Theory]
    [InlineData("am", "482913")]
    [InlineData("om", "482913")]
    [InlineData("en", "482913")]
    [InlineData(null, "482913")]
    public void OtpSms_EveryLanguageCarriesTheCode(string? language, string code)
    {
        Assert.Contains(code, OtpSms.Compose(code, language));
    }
}

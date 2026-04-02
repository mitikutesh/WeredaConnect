using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class PasswordResetFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly IRateLimitService _mockRateLimit;
    private readonly PasswordResetFunction _function;
    private readonly TestLambdaContext _context;

    public PasswordResetFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _function = new PasswordResetFunction(_mockAuth, _mockRateLimit);
        _context = new TestLambdaContext();

        // Default: rate limit allows OTP attempts
        _mockRateLimit.CheckOtpAttemptAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(string path, object body) => new()
    {
        Path = path,
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    // ─── FunctionHandler routing ─────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_UnknownPath_Returns404()
    {
        var request = new APIGatewayProxyRequest { Path = "/auth/unknown" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("AUTH_020", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_NullPath_Returns404()
    {
        var request = new APIGatewayProxyRequest { Path = null };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
    }

    // ─── ForgotPassword: success ─────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_ValidPhone_Returns200WithMessage()
    {
        _mockAuth.ForgotPasswordAsync(Arg.Any<string>())
            .Returns(AuthResult<string>.Success("SMS"));

        var request = MakeRequest("/auth/forgot-password", new
        {
            phoneNumber = "+251912345678"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("Verification code sent", response.Body);
    }

    [Fact]
    public async Task ForgotPassword_NormalizesEthiopianPhonePrefix()
    {
        _mockAuth.ForgotPasswordAsync("+251912345678")
            .Returns(AuthResult<string>.Success("SMS"));

        // 0-prefix should be normalised to +251
        var request = MakeRequest("/auth/forgot-password", new
        {
            phoneNumber = "0912345678"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockAuth.Received(1).ForgotPasswordAsync("+251912345678");
    }

    // ─── ForgotPassword: validation errors ───────────────────────────────────

    [Fact]
    public async Task ForgotPassword_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            Path = "/auth/forgot-password",
            Body = "{{bad"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task ForgotPassword_MissingPhoneNumber_Returns400()
    {
        var request = MakeRequest("/auth/forgot-password", new { });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task ForgotPassword_InvalidPhoneFormat_Returns400()
    {
        var request = MakeRequest("/auth/forgot-password", new
        {
            phoneNumber = "notaphone"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    // ─── ForgotPassword: rate limiting ───────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_RateLimited_Returns429WithoutCallingCognito()
    {
        _mockRateLimit.CheckOtpAttemptAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many attempts.", retryAfterSeconds: 600));

        var request = MakeRequest("/auth/forgot-password", new
        {
            phoneNumber = "+251912345678"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("AUTH_006", response.Body);
        await _mockAuth.DidNotReceive().ForgotPasswordAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ForgotPassword_CognitoFailure_RecordsFailedOtpAttempt()
    {
        _mockAuth.ForgotPasswordAsync(Arg.Any<string>())
            .Returns(AuthResult<string>.Failure(500, "AUTH_008", "Service unavailable."));

        var request = MakeRequest("/auth/forgot-password", new
        {
            phoneNumber = "+251912345678"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync("+251912345678");
    }

    // ─── ResetPassword: success ──────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_ValidRequest_Returns200()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "123456",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("passwordReset", response.Body);
    }

    [Fact]
    public async Task ResetPassword_NormalizesPhonePrefix()
    {
        _mockAuth.ConfirmForgotPasswordAsync("+251912345678", Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "0912345678",
            confirmationCode = "123456",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        await _mockAuth.Received(1)
            .ConfirmForgotPasswordAsync("+251912345678", Arg.Any<string>(), Arg.Any<string>());
    }

    // ─── ResetPassword: validation errors ────────────────────────────────────

    [Fact]
    public async Task ResetPassword_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            Path = "/auth/reset-password",
            Body = "{{bad"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task ResetPassword_MissingConfirmationCode_Returns400()
    {
        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("confirmationCode", response.Body);
    }

    [Fact]
    public async Task ResetPassword_MissingNewPassword_Returns400()
    {
        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "123456",
            newPassword = ""
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("newPassword", response.Body);
    }

    // ─── ResetPassword: wrong code increments OTP counter ────────────────────

    [Fact]
    public async Task ResetPassword_WrongCode_RecordsFailedOtpAttemptAndReturns400()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(400, "AUTH_018", "Invalid or expired code.", "confirmationCode"));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "wrong",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_018", response.Body);
        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync("+251912345678");
    }

    [Fact]
    public async Task ResetPassword_ExpiredCode_RecordsFailedOtpAttempt()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(400, "AUTH_019", "Code expired.", "confirmationCode"));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "expired",
            newPassword = "NewPass1dev"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync("+251912345678");
    }

    [Fact]
    public async Task ResetPassword_UserNotFound_RecordsFailedOtpAttemptToPreventEnumeration()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(404, "AUTH_013", "User not found.", "phoneNumber"));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251900000000",
            confirmationCode = "123456",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(404, response.StatusCode);
        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync("+251900000000");
    }

    [Fact]
    public async Task ResetPassword_PasswordPolicyFailure_DoesNotRecordFailedOtpAttempt()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(400, "AUTH_008", "Password does not meet requirements.", "newPassword"));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "123456",
            newPassword = "weak"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.DidNotReceive().RecordFailedOtpAttemptAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResetPassword_ServiceError_DoesNotRecordFailedOtpAttempt()
    {
        _mockAuth.ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(503, "AUTH_008", "Service unavailable."));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "123456",
            newPassword = "NewPass1dev"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.DidNotReceive().RecordFailedOtpAttemptAsync(Arg.Any<string>());
    }

    // ─── ResetPassword: rate limiting ────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_RateLimited_Returns429WithoutCallingCognito()
    {
        _mockRateLimit.CheckOtpAttemptAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many attempts.", retryAfterSeconds: 600));

        var request = MakeRequest("/auth/reset-password", new
        {
            phoneNumber = "+251912345678",
            confirmationCode = "123456",
            newPassword = "NewPass1dev"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(429, response.StatusCode);
        await _mockAuth.DidNotReceive()
            .ConfirmForgotPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}

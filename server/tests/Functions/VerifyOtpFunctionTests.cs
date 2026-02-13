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
    private readonly ICognitoAuthService _mockAuth;
    private readonly IUserRepository _mockRepo;
    private readonly IRateLimitService _mockRateLimit;
    private readonly VerifyOtpFunction _function;
    private readonly TestLambdaContext _context;

    public VerifyOtpFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _function = new VerifyOtpFunction(_mockAuth, _mockRepo, _mockRateLimit);
        _context = new TestLambdaContext();

        // Default: rate limit allows OTP attempts
        _mockRateLimit.CheckOtpAttemptAllowedAsync(Arg.Any<string>())
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
        IsPhoneVerified = false,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    [Fact]
    public async Task FunctionHandler_ValidOtp_Returns200WithVerified()
    {
        _mockAuth.ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        var user = TestUser;
        _mockRepo.GetUserByIdAsync("user-123").Returns(user);
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 1)
            .Returns(AuthResult<UserDto>.Success(user with { IsPhoneVerified = true, Version = 2 }));

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "123456"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"verified\":true", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidOtp_ResetsOtpAttemptCounter()
    {
        _mockAuth.ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));
        _mockRepo.GetUserByIdAsync(Arg.Any<string>()).Returns((UserDto?)null);

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "123456"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).ResetOtpAttemptsAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_InvalidOtp_Returns400AndRecordsAttempt()
    {
        _mockAuth.ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(400, "AUTH_011", "Invalid OTP code.", "otpCode"));

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "999999"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_011", response.Body);
        await _mockRateLimit.Received(1).RecordFailedOtpAttemptAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_ExpiredOtp_Returns400WithoutRecordingAttempt()
    {
        _mockAuth.ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Failure(400, "AUTH_010", "OTP expired.", "otpCode"));

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "123456"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_010", response.Body);
        // Expired OTP should NOT count as a failed attempt
        await _mockRateLimit.DidNotReceive().RecordFailedOtpAttemptAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_OtpAttemptsExceeded_Returns400BeforeCognito()
    {
        _mockRateLimit.CheckOtpAttemptAllowedAsync("+251912345678")
            .Returns(AuthResult<bool>.Failure(400, "AUTH_012",
                "Maximum OTP verification attempts exceeded.", "otpCode"));

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "123456"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_012", response.Body);
        // Cognito should NOT have been called
        await _mockAuth.DidNotReceive().ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "bad{json" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ResponseIncludesSecurityHeaders()
    {
        _mockAuth.ConfirmSignUpAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));
        _mockRepo.GetUserByIdAsync(Arg.Any<string>()).Returns((UserDto?)null);

        var request = MakeRequest(new
        {
            userId = "user-123",
            phoneNumber = "+251912345678",
            otpCode = "123456"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
    }
}

using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class RegisterFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly IFaydaOidcService _mockFayda;
    private readonly IUserRepository _mockRepo;
    private readonly IRateLimitService _mockRateLimit;
    private readonly IOtpService _mockOtp;
    private readonly ISmsSender _mockSms;
    private readonly IAuthStateRepository _mockAuthState;
    private readonly RegisterFunction _function;
    private readonly TestLambdaContext _context;

    public RegisterFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockFayda = Substitute.For<IFaydaOidcService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _mockOtp = Substitute.For<IOtpService>();
        _mockSms = Substitute.For<ISmsSender>();
        _mockAuthState = Substitute.For<IAuthStateRepository>();
        _function = new RegisterFunction(_mockAuth, _mockFayda, _mockRepo, _mockRateLimit,
            _mockOtp, _mockSms, _mockAuthState);
        _context = new TestLambdaContext();

        // Default: the source IP is under its cap
        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Success(true));

        // Default: rate limit allows registration
        _mockRateLimit.CheckRegistrationAllowedAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        // Default: Fayda token is valid and released the registration phone, so the
        // SIM counts as proven and the SMS code is skipped (201 with tokens).
        _mockFayda.ValidateVerificationToken(Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-123",
                FullName = "Test User",
                PhoneNumber = "+251912345678",
                Jti = "jti-001"
            }));

        // Default: first use of the verification token
        _mockAuthState.PutIfAbsentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<int>())
            .Returns(true);

        // Default: an OTP can be issued and the SMS goes out
        _mockOtp.IssueAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<OtpIssueResult>.Success(new OtpIssueResult
            {
                Code = "482913",
                ExpiresAt = "2026-09-25T12:05:00Z"
            }));
        _mockSms.SendAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        // Default: admin confirm succeeds
        _mockAuth.AdminConfirmSignUpAsync(Arg.Any<string>())
            .Returns(AuthResult<bool>.Success(true));

        // Default: login succeeds (returns tokens)
        _mockAuth.LoginAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = "access-token",
                IdToken = "id-token",
                RefreshToken = "refresh-token",
                ExpiresIn = 3600
            }));
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
    public async Task FunctionHandler_ValidRequest_Returns201WithTokens()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-abc"));

        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>()));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "valid-token",
            displayName = "Abebe",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
        Assert.Contains("user-abc", response.Body);
        Assert.Contains("access-token", response.Body);
        Assert.Contains("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task FunctionHandler_ValidRequest_RecordsRegistration()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-abc"));

        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>()));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "valid-token",
            preferredLanguage = "am"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRateLimit.Received(1).RecordRegistrationAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_InvalidFaydaToken_Returns400()
    {
        _mockFayda.ValidateVerificationToken(Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid verification token.", "faydaVerificationToken"));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "bad-token",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_001", response.Body);
        // Cognito should NOT have been called
        await _mockAuth.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_RegistrationFloodLimited_Returns429()
    {
        _mockRateLimit.CheckRegistrationAllowedAsync("+251912345678")
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many registration attempts."));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "valid-token",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("AUTH_006", response.Body);
        // Cognito should NOT have been called
        await _mockAuth.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "not json{{{" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidationFails_Returns400WithFirstError()
    {
        var request = MakeRequest(new
        {
            phoneNumber = "invalid",
            password = "ab",
            role = "admin",
            woreda = "",
            faydaVerificationToken = "token"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_001", response.Body); // First error: phone
    }

    [Fact]
    public async Task FunctionHandler_CognitoPhoneExists_Returns409()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Failure(409, "AUTH_005", "Phone exists", "phoneNumber"));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "valid-token",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("AUTH_005", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DbFailure_Returns500()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-xyz"));

        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .ThrowsAsync(new Exception("DynamoDB down"));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            faydaVerificationToken = "valid-token",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        // A Cognito user with no profile record can never log in, so registration
        // must NOT report success when the profile write fails.
        Assert.Equal(500, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
        // ...and it must roll back the orphaned Cognito user so the phone isn't bricked.
        await _mockAuth.Received(1).DeleteUserAsync("+251912345678");
    }

    [Fact]
    public async Task FunctionHandler_NullBody_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = null };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    // ===== Phone ownership: SMS code unless Fayda released the same phone =====

    private void SetupCognitoSuccess(string userId = "user-abc")
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success(userId));
        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>()));
    }

    private static object RegisterBody(string? faydaToken = null) => new
    {
        phoneNumber = "+251912345678",
        password = "secure123",
        role = "farmer",
        woreda = "Adama",
        faydaVerificationToken = faydaToken,
        displayName = "Abebe",
        preferredLanguage = "am"
    };

    private void SetupFaydaPhoneDiffers()
    {
        _mockFayda.ValidateVerificationToken(Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-123",
                FullName = "Test User",
                PhoneNumber = "+251999999999",
                Jti = "jti-002"
            }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FunctionHandler_NoFaydaToken_Returns400Auth021WithoutSigningUp(string? token)
    {
        SetupCognitoSuccess();

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody(token)), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_021", response.Body);
        Assert.Contains("faydaVerificationToken", response.Body);
        await _mockAuth.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>());
        await _mockRepo.DidNotReceive().CreateUserAsync(Arg.Any<UserDto>());
        await _mockOtp.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FaydaPhoneDiffers_Returns202OtpRequiredAndKeepsFaydaVerified()
    {
        SetupCognitoSuccess();
        SetupFaydaPhoneDiffers();

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(202, response.StatusCode);
        Assert.Contains("\"otpRequired\":true", response.Body);
        Assert.Contains("2026-09-25T12:05:00Z", response.Body);
        Assert.DoesNotContain("access-token", response.Body);
        await _mockRepo.Received(1).CreateUserAsync(Arg.Is<UserDto>(u => !u.IsPhoneVerified && u.FaydaVerified));
        await _mockOtp.Received(1).IssueAsync("+251912345678", "user-abc");
        await _mockSms.Received(1).SendAsync("+251912345678", Arg.Is<string>(m => m.Contains("482913")));
        await _mockRateLimit.Received(1).RecordOtpSendAsync("+251912345678");
        await _mockAuth.DidNotReceive().AdminConfirmSignUpAsync(Arg.Any<string>());
        await _mockAuth.DidNotReceive().LoginAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FaydaTokenWithoutPhoneClaim_Returns202()
    {
        SetupCognitoSuccess();
        _mockFayda.ValidateVerificationToken(Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-123",
                FullName = "Test User",
                Jti = "jti-003"
            }));

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(202, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_FaydaTokenReused_Returns400()
    {
        SetupCognitoSuccess();
        _mockAuthState.PutIfAbsentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<int>())
            .Returns(false);

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_001", response.Body);
        await _mockAuth.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_CognitoFails_ReleasesFaydaTokenForRetry()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Failure(503, "AUTH_008", "Unavailable."));

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(503, response.StatusCode);
        await _mockAuthState.Received(1).DeleteAsync("AUTHSTATE#JTI#jti-001");
    }

    [Fact]
    public async Task FunctionHandler_UnconfirmedPhoneRegistersAgain_DropsTheStaleProfileRow()
    {
        // CognitoAuthService replaces a still-unconfirmed user itself and returns the
        // new sub; the profile row of the replaced user must not survive, or one phone
        // maps to two profiles.
        SetupCognitoSuccess("user-new");
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(new UserDto
        {
            UserId = "user-old",
            PhoneNumber = "+251912345678",
            Role = "farmer",
            Woreda = "Adama",
            IsPhoneVerified = false,
            CreatedAt = "2026-09-25T10:00:00Z",
            UpdatedAt = "2026-09-25T10:00:00Z",
            Version = 1
        });
        SetupFaydaPhoneDiffers();

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(202, response.StatusCode);
        Assert.Contains("user-new", response.Body);
        await _mockRepo.Received(1).DeleteUserAsync("user-old");
        await _mockRepo.Received(1).CreateUserAsync(Arg.Is<UserDto>(u => u.UserId == "user-new"));
        await _mockAuth.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
        await _mockOtp.Received(1).IssueAsync("+251912345678", "user-new");
    }

    [Fact]
    public async Task FunctionHandler_VerifiedProfileAlreadyHoldsThePhone_RollsBackAndReturns409()
    {
        // A verified profile under another id is someone's account: never deleted here.
        SetupCognitoSuccess("user-new");
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(new UserDto
        {
            UserId = "user-old",
            PhoneNumber = "+251912345678",
            Role = "farmer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-09-25T10:00:00Z",
            UpdatedAt = "2026-09-25T10:00:00Z",
            Version = 1
        });

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("AUTH_005", response.Body);
        await _mockAuth.Received(1).DeleteUserAsync("+251912345678");
        await _mockRepo.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
        await _mockRepo.DidNotReceive().CreateUserAsync(Arg.Any<UserDto>());
        await _mockAuthState.Received(1).DeleteAsync("AUTHSTATE#JTI#jti-001");
    }

    [Fact]
    public async Task FunctionHandler_ProfileWriteThrows_ReleasesFaydaTokenAndRollsBack()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-abc"));
        _mockRepo.CreateUserAsync(Arg.Any<UserDto>()).Returns<AuthResult<UserDto>>(_ => throw new InvalidOperationException("dynamo down"));

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(500, response.StatusCode);
        await _mockAuth.Received(1).DeleteUserAsync("+251912345678");
        await _mockAuthState.Received(1).DeleteAsync("AUTHSTATE#JTI#jti-001");
    }

    [Fact]
    public async Task FunctionHandler_OtpServiceMissing_Returns202WithoutExpiry()
    {
        SetupCognitoSuccess();
        SetupFaydaPhoneDiffers();
        var bare = new RegisterFunction(_mockAuth, _mockFayda, _mockRepo, _mockRateLimit);

        var response = await bare.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(202, response.StatusCode);
        Assert.Contains("\"otpRequired\":true", response.Body);
        Assert.DoesNotContain("2026-09-25T12:05:00Z", response.Body);
    }

    private static UserDto FaydaHolder(string phone, bool phoneVerified) => new()
    {
        UserId = "user-held",
        PhoneNumber = phone,
        Role = "farmer",
        Woreda = "Adama",
        FaydaId = "FAYDA-123",
        FaydaVerified = true,
        IsPhoneVerified = phoneVerified,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    [Fact]
    public async Task FunctionHandler_FaydaIdHeldByAbandonedSignUpOnAnotherPhone_ReplacesIt()
    {
        // Mistyped phone last time: the pending sign-up holds the Fayda ID. It never
        // proved its phone and Cognito never confirmed it, so it is removed and the
        // new registration goes ahead.
        SetupCognitoSuccess();
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(FaydaHolder("+251911111111", false));
        _mockAuth.IsUnconfirmedOrMissingAsync("+251911111111").Returns(true);

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(201, response.StatusCode);
        await _mockAuth.Received(1).DeleteUserAsync("+251911111111");
        await _mockRepo.Received(1).DeleteUserAsync("user-held");
        await _mockRepo.Received(1).CreateUserAsync(Arg.Is<UserDto>(u => u.FaydaId == "FAYDA-123"));
    }

    [Fact]
    public async Task FunctionHandler_FaydaIdHeldByUnverifiedProfileButConfirmedCognitoUser_Returns409()
    {
        // The profile flag alone is not trusted: a confirmed Cognito user is a live account.
        SetupCognitoSuccess();
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(FaydaHolder("+251911111111", false));
        _mockAuth.IsUnconfirmedOrMissingAsync("+251911111111").Returns(false);

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("FAYDA_004", response.Body);
        await _mockAuth.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
        await _mockRepo.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FaydaIdHeldByVerifiedAccount_Returns409WithoutAskingCognito()
    {
        SetupCognitoSuccess();
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(FaydaHolder("+251911111111", true));

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("FAYDA_004", response.Body);
        await _mockAuth.DidNotReceive().IsUnconfirmedOrMissingAsync(Arg.Any<string>());
        await _mockAuth.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FaydaIdHeldByAbandonedSignUpOnSamePhone_LeavesItToTheLeftoverCleanup()
    {
        SetupCognitoSuccess("user-new");
        var holder = FaydaHolder("+251912345678", false);
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(holder);
        _mockRepo.GetUserByPhoneAsync("+251912345678").Returns(holder);
        _mockAuth.IsUnconfirmedOrMissingAsync("+251912345678").Returns(true);

        var response = await _function.FunctionHandler(MakeRequest(RegisterBody("valid-token")), _context);

        Assert.Equal(201, response.StatusCode);
        // No explicit Cognito delete: RegisterAsync replaces the unconfirmed user itself.
        await _mockAuth.DidNotReceive().DeleteUserAsync(Arg.Any<string>());
        await _mockRepo.Received(1).DeleteUserAsync("user-held");
    }

    [Theory]
    [InlineData("+251912345678", "+251912345678", true)]
    [InlineData("0912345678", "+251912345678", true)]
    [InlineData("+251999999999", "+251912345678", false)]
    [InlineData(null, "+251912345678", false)]
    public void IsPhoneProven_ComparesNormalisedFaydaPhone(string? faydaPhone, string registrationPhone, bool expected)
    {
        var verification = new FaydaVerificationResult
        {
            FaydaId = "F",
            FullName = "N",
            PhoneNumber = faydaPhone
        };

        Assert.Equal(expected, RegisterFunction.IsPhoneProven(verification, registrationPhone));
    }
}

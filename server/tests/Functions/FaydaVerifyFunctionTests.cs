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

public class FaydaVerifyFunctionTests
{
    private const string StateKey = "AUTHSTATE#OIDC#state-xyz";

    private readonly IFaydaOidcService _mockFayda;
    private readonly IUserRepository _mockRepo;
    private readonly IAuthStateRepository _mockAuthState;
    private readonly IRateLimitService _mockRateLimit;
    private readonly FaydaVerifyFunction _function;
    private readonly TestLambdaContext _context;

    public FaydaVerifyFunctionTests()
    {
        _mockFayda = Substitute.For<IFaydaOidcService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _mockAuthState = Substitute.For<IAuthStateRepository>();
        _mockRateLimit = Substitute.For<IRateLimitService>();
        _function = new FaydaVerifyFunction(_mockFayda, _mockRepo, _mockAuthState, _mockRateLimit);
        _context = new TestLambdaContext();

        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Success(true));

        // Default: a registration login was started for state-xyz
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("registration"));

        // Default: code exchange succeeds and Fayda released a phone
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-123",
                FullName = "Abebe Kebede",
                PhoneNumber = "+251912345678"
            }));

        // Default: no duplicate Fayda ID
        _mockRepo.GetUserByFaydaIdAsync(Arg.Any<string>()).Returns((UserDto?)null);

        // Default: generate a mock verification token
        _mockFayda.GenerateVerificationToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns("mock-verification-token");

        // Default: the provider's authorize URL
        _mockFayda.BuildAuthorizationUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => AuthResult<string>.Success(
                $"https://fayda.test/authorize?state={ci.ArgAt<string>(1)}&nonce={ci.ArgAt<string>(2)}&code_challenge={ci.ArgAt<string>(3)}"));
    }

    private static AuthStateEntry StateRow(string purpose, string? userId = null, string? redirect = null) => new()
    {
        Key = StateKey,
        Attributes = new Dictionary<string, string>
        {
            { "nonce", "nonce-1" },
            { "codeVerifier", "verifier-1" },
            { "purpose", purpose },
            { "redirectUri", redirect ?? "weredaconnect://fayda-callback" },
            { "createdAt", "2026-09-25T10:00:00Z" }
        }.Concat(userId == null ? [] : new[] { new KeyValuePair<string, string>("userId", userId) })
         .ToDictionary(kv => kv.Key, kv => kv.Value),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(object? body, string path = "/auth/fayda-verify", string? userId = null)
    {
        var request = new APIGatewayProxyRequest
        {
            Path = path,
            Body = body == null ? null : JsonSerializer.Serialize(body, SerializerOptions)
        };
        if (userId != null)
        {
            request.RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } }
            };
        }
        return request;
    }

    private static object VerifyBody(string state = "state-xyz", string code = "valid-code",
        string redirect = "weredaconnect://fayda-callback") => new
        {
            authorizationCode = code,
            state,
            redirectUri = redirect
        };

    // ===== fayda-verify (pre-registration) =====

    [Fact]
    public async Task Verify_ValidCode_Returns200WithPhoneBoundVerificationToken()
    {
        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("mock-verification-token", response.Body);
        Assert.Contains("Abebe Kebede", response.Body);
        Assert.Contains("nosniff", response.Headers["X-Content-Type-Options"]);
        _mockFayda.Received(1).GenerateVerificationToken("FAYDA-123", "Abebe Kebede", "+251912345678");
    }

    [Fact]
    public async Task Verify_UsesStoredPkceVerifierAndNonce()
    {
        await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        await _mockFayda.Received(1).ExchangeCodeAsync("valid-code", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");
        await _mockAuthState.Received(1).ConsumeAsync(StateKey);
    }

    [Fact]
    public async Task Verify_UnknownState_Returns400WithoutExchange()
    {
        _mockAuthState.ConsumeAsync(Arg.Any<string>()).Returns((AuthStateEntry?)null);

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody(state: "forged")), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
        await _mockFayda.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Verify_StateStartedForLinking_IsRejected()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-1"));

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
    }

    [Fact]
    public async Task Verify_RedirectMismatch_IsRejected()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(redirect: "https://evil.example/cb")), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
    }

    [Theory]
    [InlineData("codeVerifier")]
    [InlineData("nonce")]
    [InlineData("redirectUri")]
    public async Task Verify_StateRowMissingBinding_FailsClosedWithoutExchange(string missing)
    {
        var row = StateRow("registration");
        _mockAuthState.ConsumeAsync(StateKey).Returns(new AuthStateEntry
        {
            Key = row.Key,
            Attributes = row.Attributes.Where(kv => kv.Key != missing).ToDictionary(kv => kv.Key, kv => kv.Value),
            ExpiresAt = row.ExpiresAt
        });

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
        await _mockFayda.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    // ===== redirect allow-list =====

    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    private const string WebCallback = "https://dev-app.weredaconnect.com/fayda-callback.html";

    [Theory]
    [InlineData("weredaconnect://fayda-callback")]
    [InlineData("weredaconnect://fayda-link-callback")]
    public void IsAllowedRedirect_DeepLinks_AlwaysAllowed(string uri)
    {
        Assert.True(FaydaStartRequest.IsAllowedRedirect(uri, Env()));
    }

    [Fact]
    public void IsAllowedRedirect_WebCallback_AllowedOnlyWithMock()
    {
        var mock = Env(("FAYDA_MOCK", "true"), (FaydaStartRequest.MockWebRedirectUrisEnv, WebCallback + ",http://localhost:8080/fayda-callback.html"),
            ("AWS_LAMBDA_FUNCTION_NAME", "wereda-connect-dev-auth-fayda-verify"));
        var live = Env((FaydaStartRequest.MockWebRedirectUrisEnv, WebCallback));

        Assert.True(FaydaStartRequest.IsAllowedRedirect(WebCallback, mock));
        Assert.True(FaydaStartRequest.IsAllowedRedirect("http://localhost:8080/fayda-callback.html", mock));
        Assert.False(FaydaStartRequest.IsAllowedRedirect(WebCallback, live));
    }

    [Fact]
    public void IsAllowedRedirect_WebCallback_RefusedOnProdFunctionEvenWithMockFlag()
    {
        var env = Env(("FAYDA_MOCK", "true"), (FaydaStartRequest.MockWebRedirectUrisEnv, WebCallback),
            ("AWS_LAMBDA_FUNCTION_NAME", "wereda-connect-prod-auth-fayda-verify"));

        Assert.False(FaydaStartRequest.IsAllowedRedirect(WebCallback, env));
    }

    [Theory]
    [InlineData("https://dev-app.weredaconnect.com/fayda-callback.html?x=1")]
    [InlineData("https://dev-app.weredaconnect.com/other.html")]
    [InlineData("https://dev-app.weredaconnect.com.evil.example/fayda-callback.html")]
    [InlineData("http://dev-app.weredaconnect.com/fayda-callback.html")]
    public void IsAllowedRedirect_WebCallback_ExactMatchOnly(string uri)
    {
        var env = Env(("FAYDA_MOCK", "true"),
            (FaydaStartRequest.MockWebRedirectUrisEnv, WebCallback + ",http://dev-app.weredaconnect.com/fayda-callback.html"));

        Assert.False(FaydaStartRequest.IsAllowedRedirect(uri, env));
    }

    [Fact]
    public async Task Verify_DuplicateFaydaId_Returns409()
    {
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(new UserDto
        {
            UserId = "existing-user",
            PhoneNumber = "+251900000000",
            Role = "farmer",
            Woreda = "Adama",
            FaydaId = "FAYDA-123",
            FaydaVerified = true,
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("FAYDA_004", response.Body);
    }

    [Fact]
    public async Task Verify_FaydaIdHeldByUnverifiedSignUp_MintsTokenForRegisterToDecide()
    {
        // A sign-up whose phone was never proven (mistyped number, OTP never entered)
        // must not lock its own Fayda identity out: register decides whether to replace it.
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(new UserDto
        {
            UserId = "pending-user",
            PhoneNumber = "+251900000000",
            Role = "farmer",
            Woreda = "Adama",
            FaydaId = "FAYDA-123",
            FaydaVerified = true,
            IsPhoneVerified = false,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("verificationToken", response.Body);
    }

    [Fact]
    public async Task Verify_InvalidCode_Returns400()
    {
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid or expired authorization code.", "authorizationCode"));

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody(code: "bad")), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_001", response.Body);
    }

    [Fact]
    public async Task Verify_FaydaServiceUnavailable_Returns503()
    {
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable."));

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("FAYDA_003", response.Body);
    }

    [Fact]
    public async Task Verify_InvalidJson_Returns400()
    {
        var response = await _function.FunctionHandler(
            new APIGatewayProxyRequest { Path = "/auth/fayda-verify", Body = "{not json" }, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task Verify_NullBody_Returns400()
    {
        var response = await _function.FunctionHandler(MakeRequest(null), _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Theory]
    [InlineData("", "state-xyz", "weredaconnect://fayda-callback", "FAYDA_001")]
    [InlineData("code", "", "weredaconnect://fayda-callback", "FAYDA_002")]
    [InlineData("code", "state-xyz", "", "AUTH_007")]
    public async Task Verify_MissingField_Returns400(string code, string state, string redirect, string expectedCode)
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { authorizationCode = code, state, redirectUri = redirect }), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains(expectedCode, response.Body);
    }

    [Fact]
    public async Task AnyRoute_IpRateLimited_Returns429()
    {
        _mockRateLimit.CheckIpAllowedAsync(Arg.Any<string?>(), "fayda", Arg.Any<int>(), Arg.Any<int>())
            .Returns(AuthResult<bool>.Failure(429, "AUTH_006", "Too many requests.", retryAfterSeconds: 30));

        var response = await _function.FunctionHandler(MakeRequest(VerifyBody()), _context);

        Assert.Equal(429, response.StatusCode);
        await _mockAuthState.DidNotReceive().ConsumeAsync(Arg.Any<string>());
    }

    // ===== fayda-start =====

    [Fact]
    public async Task Start_StoresStateNoncePkceAndReturnsAuthorizeUrl()
    {
        IReadOnlyDictionary<string, string>? stored = null;
        string? storedKey = null;
        _mockAuthState.PutAsync(Arg.Do<string>(k => storedKey = k),
            Arg.Do<IReadOnlyDictionary<string, string>>(a => stored = a), FaydaVerifyFunction.StateTtlSeconds)
            .Returns(Task.CompletedTask);

        var response = await _function.FunctionHandler(
            MakeRequest(new { phoneNumber = "0912345678" }, "/auth/fayda-start"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(stored);
        Assert.StartsWith("AUTHSTATE#OIDC#", storedKey);
        Assert.Equal("registration", stored!["purpose"]);
        // The proof is bound to the phone Fayda releases, never to a client-supplied one.
        Assert.False(stored.ContainsKey("phone"));
        Assert.True(stored["codeVerifier"].Length >= 43);
        Assert.False(stored.ContainsKey("userId"));

        using var doc = JsonDocument.Parse(response.Body);
        var state = doc.RootElement.GetProperty("state").GetString();
        var url = doc.RootElement.GetProperty("authorizeUrl").GetString();
        Assert.Equal(storedKey, "AUTHSTATE#OIDC#" + state);
        Assert.Contains($"state={state}", url);
        Assert.Contains("nonce=" + stored["nonce"], url);
        Assert.DoesNotContain(stored["codeVerifier"], url);
    }

    [Fact]
    public async Task Start_EmptyBody_IsAccepted()
    {
        var response = await _function.FunctionHandler(MakeRequest(null, "/auth/fayda-start"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("authorizeUrl", response.Body);
    }

    [Fact]
    public async Task Start_ForeignRedirect_IsRejected()
    {
        var response = await _function.FunctionHandler(
            MakeRequest(new { redirectUri = "https://evil.example/cb" }, "/auth/fayda-start"), _context);

        Assert.Equal(400, response.StatusCode);
        await _mockAuthState.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UserStart_RequiresAuthentication()
    {
        var response = await _function.FunctionHandler(MakeRequest(null, "/user/fayda-start"), _context);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task UserStart_BindsStateToCaller()
    {
        IReadOnlyDictionary<string, string>? stored = null;
        _mockAuthState.PutAsync(Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, string>>(a => stored = a), Arg.Any<int>())
            .Returns(Task.CompletedTask);

        var response = await _function.FunctionHandler(MakeRequest(null, "/user/fayda-start", userId: "user-7"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("link", stored!["purpose"]);
        Assert.Equal("user-7", stored["userId"]);
    }

    // ===== fayda-link (post-registration) =====

    private UserDto LinkingUser(bool phoneVerified = true) => new()
    {
        UserId = "user-7",
        PhoneNumber = "+251912345678",
        Role = "buyer",
        Woreda = "Adama",
        IsPhoneVerified = phoneVerified,
        FaydaVerified = false,
        CreatedAt = "2026-09-01T00:00:00Z",
        UpdatedAt = "2026-09-01T00:00:00Z",
        Version = 2
    };

    [Fact]
    public async Task Link_ValidCode_MarksProfileVerified()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-7"));
        _mockRepo.GetUserByIdAsync("user-7").Returns(LinkingUser());
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 2)
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = 3 }));

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"faydaVerified\":true", response.Body);
        Assert.Contains("Abebe Kebede", response.Body);
        await _mockRepo.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.FaydaVerified && u.FaydaId == "FAYDA-123" && u.FaydaVerifiedAt != null), 2);
        await _mockFayda.Received(1).ExchangeCodeAsync("valid-code", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");
    }

    [Fact]
    public async Task Link_MatchingFaydaPhone_AlsoProvesThePhone()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-7"));
        _mockRepo.GetUserByIdAsync("user-7").Returns(LinkingUser(phoneVerified: false));
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 2)
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = 3 }));

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"phoneVerified\":true", response.Body);
        await _mockRepo.Received(1).UpdateUserAsync(Arg.Is<UserDto>(u => u.IsPhoneVerified), 2);
    }

    [Fact]
    public async Task Link_StateOfAnotherUser_IsRejected()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-8"));

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Link_FaydaIdHeldByAnotherAccount_Returns409()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-7"));
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(LinkingUser() with { UserId = "user-9" });

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("FAYDA_004", response.Body);
    }

    // ===== fayda-link: the identity must be the account's own =====

    private void LinkStarted(UserDto user)
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-7"));
        _mockRepo.GetUserByIdAsync("user-7").Returns(user);
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = ci.ArgAt<int>(1) + 1 }));
    }

    private void FaydaReturns(string faydaId, string? phone) =>
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = faydaId,
                FullName = "Someone",
                PhoneNumber = phone
            }));

    [Fact]
    public async Task Link_SameFaydaIdAsAccount_Reverifies()
    {
        LinkStarted(LinkingUser() with { FaydaId = "FAYDA-123", FaydaVerified = true, FaydaVerifiedAt = "2026-09-01T00:00:00Z" });
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123").Returns(LinkingUser() with { FaydaId = "FAYDA-123" });

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockRepo.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.FaydaId == "FAYDA-123" && u.FaydaVerifiedAt == "2026-09-01T00:00:00Z"), 2);
    }

    [Fact]
    public async Task Link_DifferentFaydaIdThanAccount_Returns403AndKeepsTheAccount()
    {
        LinkStarted(LinkingUser() with { FaydaId = "FAYDA-OWNER", FaydaVerified = true });
        FaydaReturns("FAYDA-STRANGER", "+251912345678");

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("FAYDA_005", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Link_FirstBind_FaydaPhoneDiffersFromAccount_Returns403()
    {
        LinkStarted(LinkingUser());
        FaydaReturns("FAYDA-123", "+251911111111");

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("FAYDA_006", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Link_FirstBind_LocalFormatFaydaPhone_Matches()
    {
        LinkStarted(LinkingUser());
        FaydaReturns("FAYDA-123", "0912345678");

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Link_FirstBind_NoPhoneClaim_FailsClosed()
    {
        LinkStarted(LinkingUser());
        FaydaReturns("FAYDA-123", null);

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("FAYDA_007", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Link_VersionRace_RowGainedAnotherFaydaId_Returns403()
    {
        _mockAuthState.ConsumeAsync(StateKey).Returns(StateRow("link", userId: "user-7"));
        _mockRepo.GetUserByIdAsync("user-7").Returns(
            LinkingUser(),
            LinkingUser() with { FaydaId = "FAYDA-OTHER", FaydaVerified = true, Version = 3 });
        _mockRepo.UpdateUserAsync(Arg.Any<UserDto>(), 2)
            .Returns(AuthResult<UserDto>.Failure(409, "CONFLICT", "version"));

        var response = await _function.FunctionHandler(
            MakeRequest(VerifyBody(), "/user/fayda-link", userId: "user-7"), _context);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("FAYDA_005", response.Body);
        await _mockRepo.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), 3);
    }

    [Fact]
    public async Task Link_Unauthenticated_Returns401()
    {
        var response = await _function.FunctionHandler(MakeRequest(VerifyBody(), "/user/fayda-link"), _context);

        Assert.Equal(401, response.StatusCode);
    }
}

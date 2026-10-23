using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Fayda (VeriFayda eSignet) verification, both before and after registration.
///   POST /auth/fayda-start   (public)        → state, nonce, PKCE stored server-side; returns the authorize URL
///   POST /auth/fayda-verify  (public)        → code + state → verification token for POST /auth/register
///   POST /user/fayda-start   (authenticated) → same, bound to the caller's user id
///   POST /user/fayda-link    (authenticated) → code + state → the caller's profile becomes Fayda-verified,
///                                               only when the identity is the account's own (FAYDA_005/006/007 otherwise)
/// State rows live 10 minutes and are consumed exactly once. The nonce is checked
/// inside the ID token, the PKCE verifier never leaves the server.
/// Dev bypass: DEV_FAYDA_BYPASS=true accepts test-code-123 on fayda-verify and fayda-link. Its identity
/// (FAYDA-DEV-001) has no phone, so fayda-link only accepts it for an account that already holds it.
/// </summary>
public class FaydaVerifyFunction
{
    internal const int StateTtlSeconds = 600;
    internal const int IpMaxPer5Min = 30;
    private const int IpWindowSeconds = 300;
    private const string StateKeyPrefix = "AUTHSTATE#OIDC#";
    private const string PurposeRegistration = "registration";
    private const string PurposeLink = "link";

    private readonly IFaydaOidcService _faydaService;
    private readonly IUserRepository _userRepository;
    private readonly IAuthStateRepository _authState;
    private readonly IRateLimitService? _rateLimitService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Resolves SSM parameter names and Secrets Manager ARNs to actual values.
    /// </summary>
    public FaydaVerifyFunction()
    {
        var clientIdParam = Environment.GetEnvironmentVariable("FAYDA_CLIENT_ID_PARAM")
                            ?? throw new InvalidOperationException("FAYDA_CLIENT_ID_PARAM is not set.");
        var issuerUrlParam = Environment.GetEnvironmentVariable("FAYDA_ISSUER_URL_PARAM")
                             ?? throw new InvalidOperationException("FAYDA_ISSUER_URL_PARAM is not set.");
        // The relying party's private key for private_key_jwt (SSM name or Secrets Manager ARN).
        var clientPrivateKeyRef = Environment.GetEnvironmentVariable("FAYDA_CLIENT_PRIVATE_KEY_ARN")
                                  ?? throw new InvalidOperationException("FAYDA_CLIENT_PRIVATE_KEY_ARN is not set.");
        var signingSecretArn = Environment.GetEnvironmentVariable("FAYDA_SIGNING_SECRET_ARN")
                               ?? throw new InvalidOperationException("FAYDA_SIGNING_SECRET_ARN is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        // Resolve SSM params and secrets to actual values
        var clientId = AwsParameterResolver.GetSsmParameterValue(clientIdParam);
        var issuerUrl = AwsParameterResolver.GetSsmParameterValue(issuerUrlParam);
        var signingSecret = AwsParameterResolver.GetSecretOrParameterValue(signingSecretArn);

        var dynamoDb = new AmazonDynamoDBClient();

        // Read on first code exchange, not here, and uncached until it parses: the
        // parameter starts as the seeded PLACEHOLDER and gets its key later (the dev
        // mock writes one on its first call; for live Fayda an operator pastes it).
        _faydaService = new FaydaOidcService(
            new HttpClient(), clientId,
            () => AwsParameterResolver.FetchSecretOrParameterValue(clientPrivateKeyRef),
            issuerUrl, signingSecret);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _authState = new AuthStateRepository(dynamoDb, rateLimitTable);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public FaydaVerifyFunction(
        IFaydaOidcService faydaService,
        IUserRepository userRepository,
        IAuthStateRepository authState,
        IRateLimitService? rateLimitService = null)
    {
        _faydaService = faydaService ?? throw new ArgumentNullException(nameof(faydaService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _authState = authState ?? throw new ArgumentNullException(nameof(authState));
        _rateLimitService = rateLimitService;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var path = request.Path?.ToLowerInvariant() ?? "";
        context.Logger.LogInformation($"Fayda invoked: {path}");

        var ipBlock = await CheckIpAsync(request);
        if (ipBlock != null)
        {
            return ipBlock;
        }

        if (path.EndsWith("/user/fayda-start", StringComparison.Ordinal))
        {
            return await HandleStartAsync(request, context, PurposeLink);
        }
        if (path.EndsWith("/user/fayda-link", StringComparison.Ordinal))
        {
            return await HandleLinkAsync(request, context);
        }
        if (path.EndsWith("/fayda-start", StringComparison.Ordinal))
        {
            return await HandleStartAsync(request, context, PurposeRegistration);
        }

        return await HandleVerifyAsync(request, context);
    }

    // ──────────────────────────────────────────────
    // start: mint state / nonce / PKCE, hand back the authorize URL
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleStartAsync(
        APIGatewayProxyRequest request, ILambdaContext context, string purpose)
    {
        string? userId = null;
        if (purpose == PurposeLink)
        {
            userId = AuthHelper.GetUserId(request);
            if (string.IsNullOrEmpty(userId))
            {
                return LambdaResponse.Error(401, "AUTH_031", "Authentication required.");
            }

            // ACC-02: no Fayda link for an account that is being or has been deleted.
            var gone = await AccountDeletionGuard.RefuseIfDeletingAsync(_userRepository, userId);
            if (gone != null)
            {
                return gone;
            }
        }

        FaydaStartRequest? body = null;
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            try
            {
                body = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.FaydaStartRequest);
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
            }
        }

        var redirectUri = string.IsNullOrWhiteSpace(body?.RedirectUri) ? FaydaStartRequest.DefaultRedirectUri : body!.RedirectUri!;
        if (!FaydaStartRequest.IsAllowedRedirect(redirectUri))
        {
            return LambdaResponse.Error(400, "AUTH_007", "redirectUri is not allowed.", "redirectUri");
        }

        var state = RandomToken(32);
        var nonce = RandomToken(32);
        var codeVerifier = RandomToken(48);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var urlResult = await _faydaService.BuildAuthorizationUrlAsync(redirectUri, state, nonce, codeChallenge);
        if (!urlResult.IsSuccess)
        {
            var error = urlResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var record = new Dictionary<string, string>
        {
            { "nonce", nonce },
            { "codeVerifier", codeVerifier },
            { "purpose", purpose },
            { "redirectUri", redirectUri },
            { "createdAt", DateTime.UtcNow.ToString("o") }
        };
        if (userId != null)
        {
            record["userId"] = userId;
        }

        await _authState.PutAsync(StateKeyPrefix + state, record, StateTtlSeconds);

        AuditLogger.LogSecurity("FAYDA_START", userId: userId, detail: purpose,
            correlationId: AuditLogger.GetCorrelationId(request), outcome: "success",
            sourceIp: AuditLogger.GetSourceIp(request));

        return LambdaResponse.Ok(new FaydaStartResponse
        {
            State = state,
            AuthorizeUrl = urlResult.Value!,
            ExpiresAt = DateTime.UtcNow.AddSeconds(StateTtlSeconds).ToString("o")
        }, AppJsonContext.Default.FaydaStartResponse);
    }

    // ──────────────────────────────────────────────
    // verify (pre-registration): code + state → verification token
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleVerifyAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var parsed = ParseVerifyBody(request, out var faydaRequest);
        if (parsed != null)
        {
            return parsed;
        }

        var exchange = await ExchangeAsync(faydaRequest!, PurposeRegistration, expectedUserId: null);
        if (!exchange.IsSuccess)
        {
            var error = exchange.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var verification = exchange.Value!;

        // Check if Fayda ID is already linked to another user. A holder whose phone was
        // never proven may be this person's own abandoned sign-up: let the token through
        // and let register decide (it replaces the holder only if Cognito never
        // confirmed it either).
        var existingUser = await _userRepository.GetUserByFaydaIdAsync(verification.FaydaId);
        if (existingUser != null && existingUser.IsPhoneVerified)
        {
            return LambdaResponse.Error(409, "FAYDA_004",
                "This Fayda ID is already linked to another account.", "authorizationCode");
        }

        // Generate verification token (phone-bound when Fayda released a phone)
        var verificationToken = _faydaService.GenerateVerificationToken(
            verification.FaydaId, verification.FullName, verification.PhoneNumber);
        var expiresAt = DateTime.UtcNow.AddMinutes(30).ToString("o");

        return LambdaResponse.Ok(new FaydaVerifyResponse
        {
            Verified = true,
            VerifiedName = verification.FullName,
            VerificationToken = verificationToken,
            ExpiresAt = expiresAt
        }, AppJsonContext.Default.FaydaVerifyResponse);
    }

    // ──────────────────────────────────────────────
    // link (post-registration): code + state → profile verified
    // ──────────────────────────────────────────────

    private async Task<APIGatewayProxyResponse> HandleLinkAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "AUTH_031", "Authentication required.");
        }

        // ACC-02: checked before the code is exchanged, so the single-use state is not spent.
        var gone = await AccountDeletionGuard.RefuseIfDeletingAsync(_userRepository, userId);
        if (gone != null)
        {
            return gone;
        }

        var parsed = ParseVerifyBody(request, out var faydaRequest);
        if (parsed != null)
        {
            return parsed;
        }

        var exchange = await ExchangeAsync(faydaRequest!, PurposeLink, expectedUserId: userId);
        if (!exchange.IsSuccess)
        {
            var error = exchange.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var verification = exchange.Value!;
        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        var holder = await _userRepository.GetUserByFaydaIdAsync(verification.FaydaId);
        if (holder != null && holder.UserId != userId)
        {
            AuditLogger.LogSecurity("FAYDA_LINK_DUPLICATE", userId: userId, correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.Error(409, "FAYDA_004",
                "This Fayda ID is already linked to another account.", "authorizationCode");
        }

        var user = await _userRepository.GetUserByIdAsync(userId);
        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013", "User profile not found.");
        }

        // The account is known: the Fayda identity must be this account's owner,
        // not just any valid Fayda holder.
        var mismatch = CheckIdentityMatch(user, verification);
        if (mismatch != null)
        {
            return IdentityMismatch(mismatch, userId, correlationId, sourceIp);
        }

        var now = DateTime.UtcNow.ToString("o");
        UserDto Linked(UserDto current) => current with
        {
            FaydaId = verification.FaydaId,
            FaydaVerified = true,
            FaydaVerifiedAt = current.FaydaVerified ? current.FaydaVerifiedAt ?? now : now,
            // Every link is a fresh Fayda login: the proof a payment above 10,000 ETB needs.
            FaydaAuthenticatedAt = now,
            FullName = string.IsNullOrWhiteSpace(current.FullName) ? verification.FullName : current.FullName,
            IsPhoneVerified = current.IsPhoneVerified || RegisterFunction.IsPhoneProven(verification, current.PhoneNumber)
        };

        var saved = await _userRepository.UpdateUserAsync(Linked(user), user.Version);
        if (!saved.IsSuccess)
        {
            // Lost a version race: re-read and apply the Fayda fields to the current row.
            var fresh = await _userRepository.GetUserByIdAsync(userId);
            if (fresh == null)
            {
                return LambdaResponse.Error(404, "AUTH_013", "User profile not found.");
            }
            var freshMismatch = CheckIdentityMatch(fresh, verification);
            if (freshMismatch != null)
            {
                return IdentityMismatch(freshMismatch, userId, correlationId, sourceIp);
            }
            saved = await _userRepository.UpdateUserAsync(Linked(fresh), fresh.Version);
            if (!saved.IsSuccess)
            {
                var error = saved.Error!;
                return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
            }
        }

        AuditLogger.LogSecurity("FAYDA_LINKED", userId: userId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Ok(new FaydaLinkResponse
        {
            FaydaVerified = true,
            FaydaVerifiedAt = saved.Value!.FaydaVerifiedAt ?? now,
            VerifiedName = verification.FullName,
            PhoneVerified = saved.Value.IsPhoneVerified
        }, AppJsonContext.Default.FaydaLinkResponse);
    }

    /// <summary>Why a Fayda identity was refused for the signed-in account.</summary>
    internal sealed record IdentityMismatchResult(string Code, string Reason, string Message);

    internal const string FaydaIdMismatchCode = "FAYDA_005";
    internal const string FaydaPhoneMismatchCode = "FAYDA_006";
    internal const string FaydaUnconfirmedCode = "FAYDA_007";

    /// <summary>
    /// Null when the Fayda identity belongs to <paramref name="user"/>; otherwise why not.
    /// An account that already holds a Fayda ID only accepts that same ID (re-verification).
    /// An account without one binds it only when Fayda released a phone number equal to
    /// the account's phone: with no phone claim nothing ties the identity to this account,
    /// so it fails closed rather than binding a stranger's ID.
    /// </summary>
    internal static IdentityMismatchResult? CheckIdentityMatch(UserDto user, FaydaVerificationResult verification)
    {
        if (!string.IsNullOrEmpty(user.FaydaId))
        {
            return string.Equals(user.FaydaId, verification.FaydaId, StringComparison.Ordinal)
                ? null
                : new IdentityMismatchResult(FaydaIdMismatchCode, "fayda_id",
                    "This Fayda ID does not match the one on your account.");
        }

        if (string.IsNullOrWhiteSpace(verification.PhoneNumber))
        {
            return new IdentityMismatchResult(FaydaUnconfirmedCode, "no_phone_claim",
                "We could not confirm this Fayda ID belongs to your account.");
        }

        return RegisterFunction.IsPhoneProven(verification, user.PhoneNumber)
            ? null
            : new IdentityMismatchResult(FaydaPhoneMismatchCode, "phone",
                "The phone number on this Fayda ID does not match your account.");
    }

    private static APIGatewayProxyResponse IdentityMismatch(
        IdentityMismatchResult mismatch, string userId, string? correlationId, string? sourceIp)
    {
        AuditLogger.LogSecurity("FAYDA_LINK_MISMATCH", userId: userId, detail: mismatch.Reason,
            correlationId: correlationId, outcome: "blocked", sourceIp: sourceIp);
        return LambdaResponse.Error(403, mismatch.Code, mismatch.Message, "authorizationCode");
    }

    // ──────────────────────────────────────────────
    // shared pieces
    // ──────────────────────────────────────────────

    private static APIGatewayProxyResponse? ParseVerifyBody(
        APIGatewayProxyRequest request, out FaydaVerifyRequest? faydaRequest)
    {
        faydaRequest = null;
        try
        {
            faydaRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.FaydaVerifyRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        var validation = RequestValidator.ValidateFaydaVerifyRequest(faydaRequest);
        return validation.IsValid ? null : LambdaResponse.ValidationError(validation);
    }

    /// <summary>
    /// Consume the state (single use), check it was started for this purpose and
    /// user, then exchange the code with the stored PKCE verifier and nonce.
    /// </summary>
    private async Task<AuthResult<FaydaVerificationResult>> ExchangeAsync(
        FaydaVerifyRequest faydaRequest, string purpose, string? expectedUserId)
    {
        // Dev bypass for CI and emulator smoke runs (refused by the infra guard and
        // by DevBypass at runtime in production)
        if (DevBypass.IsEnabled("DEV_FAYDA_BYPASS") && faydaRequest.AuthorizationCode == "test-code-123")
        {
            await _authState.DeleteAsync(StateKeyPrefix + faydaRequest.State);
            return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-DEV-001",
                FullName = "Test User"
            });
        }

        var stateRow = await _authState.ConsumeAsync(StateKeyPrefix + faydaRequest.State);
        if (stateRow == null)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_002",
                "This verification attempt is unknown or has expired. Please start again.", "state");
        }

        if (!string.Equals(stateRow.Get("purpose"), purpose, StringComparison.Ordinal)
            || (expectedUserId != null && !string.Equals(stateRow.Get("userId"), expectedUserId, StringComparison.Ordinal)))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_002",
                "This verification attempt does not belong to this request.", "state");
        }

        var storedRedirect = stateRow.Get("redirectUri");
        if (string.IsNullOrEmpty(storedRedirect)
            || !string.Equals(storedRedirect, faydaRequest.RedirectUri, StringComparison.Ordinal))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_002",
                "redirectUri does not match the started verification.", "redirectUri");
        }

        // Fail closed: without the PKCE verifier and nonce the exchange would skip
        // the code-injection and replay checks.
        var codeVerifier = stateRow.Get("codeVerifier");
        var nonce = stateRow.Get("nonce");
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(nonce))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_002",
                "This verification attempt is incomplete. Please start again.", "state");
        }

        return await _faydaService.ExchangeCodeAsync(
            faydaRequest.AuthorizationCode, faydaRequest.RedirectUri, codeVerifier, nonce);
    }

    private async Task<APIGatewayProxyResponse?> CheckIpAsync(APIGatewayProxyRequest request)
    {
        if (_rateLimitService == null)
        {
            return null;
        }

        var sourceIp = AuditLogger.GetSourceIp(request);
        var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "fayda", IpMaxPer5Min, IpWindowSeconds);
        if (!ipCheck.IsSuccess)
        {
            return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
        }
        await _rateLimitService.RecordIpAsync(sourceIp, "fayda", IpWindowSeconds);
        return null;
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

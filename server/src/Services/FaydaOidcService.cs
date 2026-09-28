using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Fayda National ID (VeriFayda eSignet) OpenID Connect client.
/// Discovers the provider's endpoints, builds the authorization URL the app opens,
/// exchanges the returned code (with PKCE) for an ID token, validates it against
/// the provider's JWKS (issuer, audience, lifetime, signature, nonce) and issues
/// the short-lived verification token that registration consumes once.
/// AOT-compatible: System.Text.Json source generation and System.IdentityModel.Tokens.Jwt.
/// Client authentication is client_secret_post; private_key_jwt is a follow-up once
/// the production credentials are issued.
/// </summary>
public sealed class FaydaOidcService : IFaydaOidcService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _issuerUrl;
    private readonly byte[] _signingSecret;
    private readonly IReadOnlyList<TimeSpan> _tokenRetryDelays;

    // Pauses between token-endpoint attempts. Short, so three attempts fit well
    // inside the Lambda's 10 second budget.
    private static readonly TimeSpan[] DefaultTokenRetryDelays =
        [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(800)];

    private const int VerificationTokenValidityMinutes = 30;
    private const int JwksCacheMinutes = 60;
    private const int DiscoveryCacheMinutes = 60;
    private const int RejectedDiscoveryCacheMinutes = 5;
    private const string DefaultScope = "openid profile phone";
    private const string DefaultAcrValues = "mosip:idp:acr:generated-code mosip:idp:acr:biometrics";
    private static readonly string[] AllowedIdTokenAlgorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256];

    // Cached provider metadata (thread-safe via volatile + timestamp check)
    private static volatile IList<SecurityKey>? _cachedJwksKeys;
    private static DateTime _jwksCacheExpiry = DateTime.MinValue;
    private static volatile FaydaDiscoveryDocument? _cachedDiscovery;
    private static DateTime _discoveryCacheExpiry = DateTime.MinValue;
    // Negative cache for a discovery document of another issuer, so a misconfigured
    // issuer does not refetch it on every start / exchange / JWKS lookup.
    private static DateTime _discoveryRejectedUntil = DateTime.MinValue;

    public FaydaOidcService(
        HttpClient httpClient,
        string clientId,
        string clientSecret,
        string issuerUrl,
        string signingSecret,
        IReadOnlyList<TimeSpan>? tokenRetryDelays = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _issuerUrl = issuerUrl ?? throw new ArgumentNullException(nameof(issuerUrl));
        _signingSecret = Encoding.UTF8.GetBytes(signingSecret ?? throw new ArgumentNullException(nameof(signingSecret)));
        _tokenRetryDelays = tokenRetryDelays ?? DefaultTokenRetryDelays;
    }

    /// <summary>Test seam: forget cached provider metadata.</summary>
    internal static void ResetCaches()
    {
        _cachedJwksKeys = null;
        _jwksCacheExpiry = DateTime.MinValue;
        _cachedDiscovery = null;
        _discoveryCacheExpiry = DateTime.MinValue;
        _discoveryRejectedUntil = DateTime.MinValue;
    }

    public async Task<AuthResult<string>> BuildAuthorizationUrlAsync(
        string redirectUri, string state, string nonce, string codeChallenge)
    {
        var discovery = await GetDiscoveryAsync();
        var authorizeEndpoint = discovery?.AuthorizationEndpoint ?? $"{_issuerUrl.TrimEnd('/')}/authorize";

        var query = new Dictionary<string, string>
        {
            { "client_id", _clientId },
            { "redirect_uri", redirectUri },
            { "response_type", "code" },
            { "scope", DefaultScope },
            { "state", state },
            { "nonce", nonce },
            { "code_challenge", codeChallenge },
            { "code_challenge_method", "S256" },
            { "acr_values", DefaultAcrValues }
        };
        var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var separator = authorizeEndpoint.Contains('?') ? "&" : "?";

        return AuthResult<string>.Success($"{authorizeEndpoint}{separator}{queryString}");
    }

    /// <summary>
    /// POST the token request, repeating it only while the code cannot have been used
    /// yet: the connection never opened, or Fayda's gateway answered 502/503/504
    /// before reaching the token service. A timeout or any other failure is not
    /// repeated, since Fayda may already have redeemed the single-use code.
    /// Returns null when every attempt failed without a response.
    /// </summary>
    private async Task<HttpResponseMessage?> PostTokenRequestWithRetryAsync(
        string tokenEndpoint, Dictionary<string, string> form)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response;
            bool retryable;
            try
            {
                response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
                retryable = response.StatusCode is HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError
                                                      or HttpRequestError.NameResolutionError)
            {
                response = null;
                retryable = true;
            }
            catch (Exception)
            {
                return null;
            }

            if (!retryable || attempt >= _tokenRetryDelays.Count)
            {
                return response;
            }

            response?.Dispose();
            await Task.Delay(_tokenRetryDelays[attempt]);
        }
    }

    public async Task<AuthResult<FaydaVerificationResult>> ExchangeCodeAsync(
        string authorizationCode,
        string redirectUri,
        string? codeVerifier = null,
        string? expectedNonce = null)
    {
        var discovery = await GetDiscoveryAsync();
        var tokenEndpoint = discovery?.TokenEndpoint ?? $"{_issuerUrl.TrimEnd('/')}/token";

        var form = new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "code", authorizationCode },
            { "redirect_uri", redirectUri },
            { "client_id", _clientId },
            { "client_secret", _clientSecret }
        };
        if (!string.IsNullOrEmpty(codeVerifier))
        {
            form["code_verifier"] = codeVerifier;
        }

        var httpResponse = await PostTokenRequestWithRetryAsync(tokenEndpoint, form);
        if (httpResponse == null || (int)httpResponse.StatusCode >= 500)
        {
            // Fayda is down or failing: "try again later", not "your code is invalid".
            return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable.");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid or expired authorization code.", "authorizationCode");
        }

        var responseBody = await httpResponse.Content.ReadAsStringAsync();
        FaydaTokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize(responseBody, FaydaJsonContext.Default.FaydaTokenResponse);
        }
        catch (JsonException)
        {
            tokenResponse = null;
        }

        if (tokenResponse?.IdToken == null)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid token response from Fayda.", "authorizationCode");
        }

        // Validate ID token signature against Fayda JWKS
        var jwksKeys = await FetchJwksKeysAsync();
        if (jwksKeys == null)
        {
            return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Unable to fetch Fayda signing keys for token validation.");
        }

        // Keep the OIDC claim names: the default inbound map renames "sub" and "name"
        // to the SOAP claim types, which would make every ID token look identity-less.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [_issuerUrl.TrimEnd('/'), _issuerUrl.TrimEnd('/') + "/"],
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwksKeys,
            // Asymmetric algorithms only: blocks alg=none and HS256-with-public-key confusion.
            ValidAlgorithms = AllowedIdTokenAlgorithms,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(tokenResponse.IdToken, validationParameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // JWKS cache may be stale — force refresh and retry once
            _jwksCacheExpiry = DateTime.MinValue;
            jwksKeys = await FetchJwksKeysAsync();
            if (jwksKeys == null)
            {
                return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                    "Unable to fetch Fayda signing keys for token validation.");
            }
            validationParameters.IssuerSigningKeys = jwksKeys;

            try
            {
                principal = handler.ValidateToken(tokenResponse.IdToken, validationParameters, out _);
            }
            catch (Exception)
            {
                return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                    "Invalid ID token signature from Fayda.", "authorizationCode");
            }
        }
        catch (Exception)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid ID token from Fayda.", "authorizationCode");
        }

        // The nonce ties this ID token to the login we started (replay defence).
        if (!string.IsNullOrEmpty(expectedNonce))
        {
            var nonce = principal.FindFirst("nonce")?.Value;
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            {
                return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_002",
                    "The identity response does not belong to this verification attempt.", "state");
            }
        }

        var faydaId = principal.FindFirst("sub")?.Value;
        var name = principal.FindFirst("name")?.Value
                   ?? principal.FindFirst("given_name")?.Value
                   ?? "Unknown";
        var phoneNumber = principal.FindFirst("phone_number")?.Value;

        if (string.IsNullOrEmpty(faydaId))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Missing identity in Fayda response.", "authorizationCode");
        }

        return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
        {
            FaydaId = faydaId,
            FullName = name,
            PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim()
        });
    }

    /// <summary>
    /// OpenID discovery document, cached for an hour. Null when the provider does
    /// not serve one (callers fall back to conventional paths).
    /// </summary>
    private async Task<FaydaDiscoveryDocument?> GetDiscoveryAsync()
    {
        if (_cachedDiscovery != null && DateTime.UtcNow < _discoveryCacheExpiry)
        {
            return _cachedDiscovery;
        }
        if (DateTime.UtcNow < _discoveryRejectedUntil)
        {
            return null;
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_issuerUrl.TrimEnd('/')}/.well-known/openid-configuration");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var document = JsonSerializer.Deserialize(json, FaydaJsonContext.Default.FaydaDiscoveryDocument);
            // OIDC Discovery 4.3: the document must describe the configured issuer,
            // otherwise its endpoints (token, JWKS) are not trusted.
            if (document == null)
            {
                return null;
            }
            if (!string.Equals(document.Issuer?.TrimEnd('/'), _issuerUrl.TrimEnd('/'), StringComparison.Ordinal))
            {
                // Falls back to issuer-derived endpoints; usually means the SSM issuer
                // does not match what the provider publishes.
                Console.WriteLine($"[FaydaOidc] WARN discovery issuer '{document.Issuer}' does not match configured issuer '{_issuerUrl}'; ignoring discovery");
                _discoveryRejectedUntil = DateTime.UtcNow.AddMinutes(RejectedDiscoveryCacheMinutes);
                return null;
            }

            _cachedDiscovery = document;
            _discoveryCacheExpiry = DateTime.UtcNow.AddMinutes(DiscoveryCacheMinutes);
            return document;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch and cache JWKS keys from the provider: the discovery document's jwks_uri
    /// first, then the conventional eSignet paths. Cached for 60 minutes.
    /// </summary>
    private async Task<IList<SecurityKey>?> FetchJwksKeysAsync()
    {
        if (_cachedJwksKeys != null && DateTime.UtcNow < _jwksCacheExpiry)
        {
            return _cachedJwksKeys;
        }

        var issuerBase = _issuerUrl.TrimEnd('/');
        var candidates = new List<string>();
        var discovery = await GetDiscoveryAsync();
        if (!string.IsNullOrEmpty(discovery?.JwksUri))
        {
            candidates.Add(discovery.JwksUri);
        }
        candidates.Add($"{issuerBase}/.well-known/jwks.json");
        candidates.Add($"{issuerBase}/oauth/jwks");
        candidates.Add($"{issuerBase}/jwks");

        string? jwksJson = null;
        foreach (var endpoint in candidates.Distinct())
        {
            try
            {
                var response = await _httpClient.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    jwksJson = await response.Content.ReadAsStringAsync();
                    break;
                }
            }
            catch
            {
                // Try next endpoint
            }
        }

        if (string.IsNullOrEmpty(jwksJson))
        {
            return null;
        }

        try
        {
            var jwks = new JsonWebKeySet(jwksJson);
            _cachedJwksKeys = jwks.GetSigningKeys();
            _jwksCacheExpiry = DateTime.UtcNow.AddMinutes(JwksCacheMinutes);
            return _cachedJwksKeys;
        }
        catch
        {
            return null;
        }
    }

    public string GenerateVerificationToken(string faydaId, string name, string? phoneNumber = null)
    {
        var key = new SymmetricSecurityKey(_signingSecret);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("faydaId", faydaId),
            new("name", name),
            new("purpose", "fayda-verification")
        };
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            // Binds the proof to the phone Fayda released, so registration can
            // skip the SMS code only for that number.
            claims.Add(new Claim("phone", phoneNumber));
        }

        var token = new JwtSecurityToken(
            issuer: "weredaconnect",
            audience: "weredaconnect-registration",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(VerificationTokenValidityMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public AuthResult<FaydaVerificationResult> ValidateVerificationToken(string token)
    {
        var key = new SymmetricSecurityKey(_signingSecret);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "weredaconnect",
            ValidateAudience = true,
            ValidAudience = "weredaconnect-registration",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, validationParameters, out _);

            var faydaId = principal.FindFirst("faydaId")?.Value;
            var name = principal.FindFirst("name")?.Value;
            var purpose = principal.FindFirst("purpose")?.Value;
            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            var phone = principal.FindFirst("phone")?.Value;

            if (purpose != "fayda-verification" || string.IsNullOrEmpty(faydaId) || string.IsNullOrEmpty(jti))
            {
                return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                    "Invalid verification token.", "faydaVerificationToken");
            }

            return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = faydaId,
                FullName = name ?? "Unknown",
                PhoneNumber = string.IsNullOrWhiteSpace(phone) ? null : phone,
                Jti = jti
            });
        }
        catch (SecurityTokenExpiredException)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Verification token has expired. Please verify with Fayda again.", "faydaVerificationToken");
        }
        catch (Exception)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid verification token.", "faydaVerificationToken");
        }
    }
}

/// <summary>
/// Internal DTO for Fayda OIDC token endpoint response.
/// </summary>
internal sealed record FaydaTokenResponse
{
    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }
}

/// <summary>The subset of the OpenID discovery document this client uses.</summary>
internal sealed record FaydaDiscoveryDocument
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; init; }

    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; init; }

    [JsonPropertyName("userinfo_endpoint")]
    public string? UserinfoEndpoint { get; init; }
}

/// <summary>
/// Source-generated JSON context for Fayda-specific types (AOT-safe).
/// </summary>
[JsonSerializable(typeof(FaydaTokenResponse))]
[JsonSerializable(typeof(FaydaDiscoveryDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FaydaJsonContext : JsonSerializerContext
{
}

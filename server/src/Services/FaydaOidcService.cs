using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Fayda National ID (VeriFayda eSignet) OpenID Connect client.
/// Discovers the provider's endpoints, builds the authorization URL the app opens,
/// exchanges the returned code (with PKCE) for an ID token, validates it against
/// the provider's JWKS (issuer, audience, lifetime, signature, nonce), reads the
/// user's name and phone from the signed userinfo response and issues the
/// short-lived verification token that registration consumes once.
/// Client authentication is private_key_jwt (RS256), the only method Fayda accepts.
/// The mock (tools/fayda-mock) and live Fayda differ only in the issuer, client id
/// and client private key this class is given.
/// AOT-compatible: System.Text.Json source generation and System.IdentityModel.Tokens.Jwt.
/// </summary>
public sealed class FaydaOidcService : IFaydaOidcService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly Func<string> _clientPrivateKeyProvider;
    private RsaSecurityKey? _clientKey;
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
    // eSignet releases user data only for claims requested here, and only via userinfo.
    private const string RequestedClaims =
        "{\"userinfo\":{\"name\":{\"essential\":true},\"phone_number\":{\"essential\":true}},\"id_token\":{}}";
    private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private const int ClientAssertionValidityMinutes = 2;

    // eSignet layout: the issuer is the host root, the rest lives under /v1/esignet.
    // Used when discovery is unavailable, and tried before the generic OIDC paths.
    private const string EsignetDiscoveryPath = "/v1/esignet/oidc/.well-known/openid-configuration";
    private const string EsignetTokenPath = "/v1/esignet/oauth/v2/token";
    private const string EsignetUserinfoPath = "/v1/esignet/oidc/userinfo";
    // Live Fayda advertises jwks_uri as /.well-known/jwks.json and answers 404 there.
    private const string EsignetJwksPath = "/v1/esignet/oauth/.well-known/jwks.json";
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

    /// <param name="clientPrivateKey">
    /// The relying party's RSA private key for private_key_jwt: PEM, JWK JSON, or a
    /// base64-encoded JWK (the form the Fayda partner portal hands out).
    /// </param>
    public FaydaOidcService(
        HttpClient httpClient,
        string clientId,
        string clientPrivateKey,
        string issuerUrl,
        string signingSecret,
        IReadOnlyList<TimeSpan>? tokenRetryDelays = null)
        : this(httpClient, clientId,
            () => clientPrivateKey ?? throw new ArgumentNullException(nameof(clientPrivateKey)),
            issuerUrl, signingSecret, tokenRetryDelays)
    {
    }

    /// <summary>
    /// Resolves the client private key on first use, so a Lambda that only builds
    /// authorization URLs never needs it, and a key that is created after this
    /// Lambda's cold start (the dev mock creates it on its own first call) is still found.
    /// </summary>
    public FaydaOidcService(
        HttpClient httpClient,
        string clientId,
        Func<string> clientPrivateKeyProvider,
        string issuerUrl,
        string signingSecret,
        IReadOnlyList<TimeSpan>? tokenRetryDelays = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientPrivateKeyProvider = clientPrivateKeyProvider ?? throw new ArgumentNullException(nameof(clientPrivateKeyProvider));
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
        string redirectUri, string state, string nonce, string codeChallenge, string? loginHint = null)
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
            { "acr_values", DefaultAcrValues },
            { "claims", RequestedClaims }
        };
        if (!string.IsNullOrEmpty(loginHint))
        {
            query["login_hint"] = loginHint;
        }
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
        string tokenEndpoint, Func<Dictionary<string, string>> buildForm)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response;
            bool retryable;
            // Built per attempt: each carries a fresh client assertion (new jti),
            // because the provider rejects a replayed one. Outside the try: the key
            // was checked by GetClientKey, so a failure here is a bug, not an outage.
            using var content = new FormUrlEncodedContent(buildForm());
            try
            {
                response = await _httpClient.PostAsync(tokenEndpoint, content);
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
        var tokenEndpoint = discovery?.TokenEndpoint ?? $"{_issuerUrl.TrimEnd('/')}{EsignetTokenPath}";

        var clientKey = GetClientKey();
        if (clientKey == null)
        {
            // Our configuration, not the user's code: the key is missing or unreadable.
            return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable.");
        }

        Dictionary<string, string> BuildForm()
        {
            var form = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", authorizationCode },
                { "redirect_uri", redirectUri },
                { "client_id", _clientId },
                { "client_assertion_type", ClientAssertionType },
                { "client_assertion", CreateClientAssertion(clientKey, tokenEndpoint) }
            };
            if (!string.IsNullOrEmpty(codeVerifier))
            {
                form["code_verifier"] = codeVerifier;
            }
            return form;
        }

        var httpResponse = await PostTokenRequestWithRetryAsync(tokenEndpoint, BuildForm);
        if (httpResponse == null || (int)httpResponse.StatusCode >= 500)
        {
            // Fayda is down or failing: "try again later", not "your code is invalid".
            return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable.");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            // invalid_client here means our registration (client id, key, assertion
            // audience), not the user's code: log it so a live cut-over is diagnosable.
            Console.WriteLine($"[FaydaOidc] WARN token endpoint answered {(int)httpResponse.StatusCode} error={await ReadOAuthErrorAsync(httpResponse)}");
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

        // Keep the OIDC claim names: the default inbound map renames "sub" and "name"
        // to the SOAP claim types, which would make every ID token look identity-less.
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [_issuerUrl.TrimEnd('/'), _issuerUrl.TrimEnd('/') + "/"],
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Asymmetric algorithms only: blocks alg=none and HS256-with-public-key confusion.
            ValidAlgorithms = AllowedIdTokenAlgorithms,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var (principal, idTokenError) = await ValidateProviderJwtAsync(tokenResponse.IdToken, validationParameters, "ID token");
        if (principal == null)
        {
            return idTokenError!;
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
        if (string.IsNullOrEmpty(faydaId))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Missing identity in Fayda response.", "authorizationCode");
        }

        // Fayda puts name and phone in userinfo, not in the ID token. The ID token
        // claims stay as a fallback for a provider that also carries them there.
        IReadOnlyDictionary<string, string> userinfo = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            var userinfoEndpoint = discovery?.UserinfoEndpoint ?? $"{_issuerUrl.TrimEnd('/')}{EsignetUserinfoPath}";
            var (claims, userinfoError) = await FetchUserinfoAsync(userinfoEndpoint, tokenResponse.AccessToken, faydaId);
            if (claims == null)
            {
                return userinfoError!;
            }
            userinfo = claims;
        }

        string? Claim(string type) =>
            userinfo.TryGetValue(type, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : principal.FindFirst(type)?.Value;

        var name = Claim("name") ?? Claim("given_name") ?? "Unknown";
        var phoneNumber = Claim("phone_number");

        return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
        {
            FaydaId = faydaId,
            FullName = name,
            PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim()
        });
    }

    /// <summary>
    /// Validates a JWT the provider signed (ID token or userinfo) against its JWKS,
    /// refreshing the cached keys once when the token names a key we do not have yet.
    /// </summary>
    private async Task<(ClaimsPrincipal? Principal, AuthResult<FaydaVerificationResult>? Error)> ValidateProviderJwtAsync(
        string jwt, TokenValidationParameters parameters, string what)
    {
        var jwksKeys = await FetchJwksKeysAsync();
        if (jwksKeys == null)
        {
            return (null, AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Unable to fetch Fayda signing keys for token validation."));
        }

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        parameters.IssuerSigningKeys = jwksKeys;
        try
        {
            return (handler.ValidateToken(jwt, parameters, out _), null);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // JWKS cache may be stale: force a refresh and retry once
            _jwksCacheExpiry = DateTime.MinValue;
            jwksKeys = await FetchJwksKeysAsync();
            if (jwksKeys == null)
            {
                return (null, AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                    "Unable to fetch Fayda signing keys for token validation."));
            }
            parameters.IssuerSigningKeys = jwksKeys;

            try
            {
                return (handler.ValidateToken(jwt, parameters, out _), null);
            }
            catch (Exception)
            {
                return (null, AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                    $"Invalid {what} signature from Fayda.", "authorizationCode"));
            }
        }
        catch (Exception)
        {
            return (null, AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                $"Invalid {what} from Fayda.", "authorizationCode"));
        }
    }

    /// <summary>
    /// GET the userinfo endpoint with the access token. eSignet answers with a JWT
    /// signed by the same key as the ID token (application/jwt); a plain JSON
    /// answer is accepted too (OIDC Core 5.3.2, trusted through TLS). Its sub must
    /// be the ID token's sub. Returns the string claims, or an error result.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string>? Claims, AuthResult<FaydaVerificationResult>? Error)> FetchUserinfoAsync(
        string userinfoEndpoint, string accessToken, string expectedSub)
    {
        static AuthResult<FaydaVerificationResult> Unavailable() =>
            AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003", "Fayda identity service is temporarily unavailable.");
        static AuthResult<FaydaVerificationResult> Invalid(string message) =>
            AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001", message, "authorizationCode");

        string body;
        string? mediaType;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                return (null, Unavailable());
            }
            if (!response.IsSuccessStatusCode)
            {
                return (null, Invalid("Fayda did not release the identity details."));
            }
            body = (await response.Content.ReadAsStringAsync()).Trim();
            mediaType = response.Content.Headers.ContentType?.MediaType;
        }
        catch (Exception)
        {
            return (null, Unavailable());
        }

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.StartsWith('{') && !string.Equals(mediaType, "application/jwt", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        claims[property.Name] = property.Value.GetString()!;
                    }
                }
            }
            catch (JsonException)
            {
                return (null, Invalid("Invalid userinfo response from Fayda."));
            }
        }
        else
        {
            if (body.Split('.').Length == 5)
            {
                // Encrypted userinfo (JWE) is a per-client registration option; this
                // client registers without it.
                Console.WriteLine("[FaydaOidc] ERROR userinfo is encrypted (JWE); register the Fayda client without userinfo encryption");
                return (null, Unavailable());
            }

            var parameters = new TokenValidationParameters
            {
                // eSignet's signed userinfo need not carry iss, aud or exp; when it
                // does, they are checked below instead.
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = false,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = AllowedIdTokenAlgorithms,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            var (principal, error) = await ValidateProviderJwtAsync(body, parameters, "userinfo");
            if (principal == null)
            {
                return (null, error);
            }

            var iss = principal.FindFirst("iss")?.Value;
            if (iss != null && !string.Equals(iss.TrimEnd('/'), _issuerUrl.TrimEnd('/'), StringComparison.Ordinal))
            {
                return (null, Invalid("Invalid userinfo from Fayda."));
            }
            var audiences = principal.FindAll("aud").Select(c => c.Value).ToList();
            if (audiences.Count > 0 && !audiences.Contains(_clientId, StringComparer.Ordinal))
            {
                return (null, Invalid("Invalid userinfo from Fayda."));
            }
            foreach (var claim in principal.Claims)
            {
                claims.TryAdd(claim.Type, claim.Value);
            }
        }

        // OIDC Core 5.3.2: the userinfo sub must be the ID token's sub.
        if (!claims.TryGetValue("sub", out var sub) || !string.Equals(sub, expectedSub, StringComparison.Ordinal))
        {
            return (null, Invalid("The identity details do not belong to this sign-in."));
        }
        return (claims, null);
    }

    /// <summary>
    /// The OAuth "error" code of a failed token response (never the whole body,
    /// which could echo request data), or "unknown".
    /// </summary>
    private static async Task<string> ReadOAuthErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var code = error.GetString()!;
                // OAuth error codes are short snake_case words; anything else is not logged.
                return code.Length <= 64 && code.All(c => char.IsAsciiLetterLower(c) || c == '_') ? code : "unknown";
            }
        }
        catch (Exception)
        {
            // Not JSON: nothing to report beyond the status.
        }
        return "unknown";
    }

    /// <summary>
    /// The parsed client private key, cached once it has signed a probe assertion.
    /// Null (and logged) when the key is missing, a placeholder, public only or too
    /// short; the next call reads the parameter again.
    /// </summary>
    private RsaSecurityKey? GetClientKey()
    {
        if (_clientKey != null)
        {
            return _clientKey;
        }
        try
        {
            var key = ParseClientPrivateKey(_clientPrivateKeyProvider());
            // Prove it can sign before caching, so the token request cannot fail later.
            _ = CreateClientAssertion(key, _issuerUrl);
            _clientKey = key;
            return _clientKey;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FaydaOidc] ERROR client private key unavailable ({ex.GetType().Name}); set the Fayda client private key parameter");
            return null;
        }
    }

    /// <summary>
    /// Reads an RSA private key given as PEM, JWK JSON, or base64 of either.
    /// Throws when it is none of those or has no private part.
    /// </summary>
    internal static RsaSecurityKey ParseClientPrivateKey(string value)
    {
        var text = (value ?? "").Trim();
        if (!text.StartsWith("-----BEGIN", StringComparison.Ordinal) && !text.StartsWith('{'))
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
        }

        if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(text);
            return RequireUsableKey(new RsaSecurityKey(rsa));
        }

        var jwk = new JsonWebKey(text);
        if (!string.Equals(jwk.Kty, "RSA", StringComparison.Ordinal) || string.IsNullOrEmpty(jwk.D))
        {
            throw new InvalidOperationException("The Fayda client key must be an RSA private key.");
        }
        static byte[]? Decode(string? part) => string.IsNullOrEmpty(part) ? null : Base64UrlEncoder.DecodeBytes(part);
        var parameters = new RSAParameters
        {
            Modulus = Decode(jwk.N),
            Exponent = Decode(jwk.E),
            D = Decode(jwk.D),
            P = Decode(jwk.P),
            Q = Decode(jwk.Q),
            DP = Decode(jwk.DP),
            DQ = Decode(jwk.DQ),
            InverseQ = Decode(jwk.QI)
        };
        return RequireUsableKey(new RsaSecurityKey(RSA.Create(parameters)) { KeyId = string.IsNullOrEmpty(jwk.Kid) ? null : jwk.Kid });
    }

    private const int MinimumClientKeyBits = 2048;

    /// <summary>A private RSA key of at least 2048 bits (a public-only PEM imports fine, so check).</summary>
    private static RsaSecurityKey RequireUsableKey(RsaSecurityKey key)
    {
        if (key.Rsa.KeySize < MinimumClientKeyBits)
        {
            throw new InvalidOperationException($"The Fayda client key must be at least {MinimumClientKeyBits} bits.");
        }
        // Throws for a key without a private part.
        _ = key.Rsa.ExportParameters(includePrivateParameters: true);
        return key;
    }

    /// <summary>
    /// private_key_jwt client assertion (RFC 7523): iss and sub are the client id,
    /// aud is the token endpoint, a fresh jti, a short lifetime, signed RS256.
    /// </summary>
    private string CreateClientAssertion(RsaSecurityKey key, string tokenEndpoint)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _clientId,
            audience: tokenEndpoint,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, _clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(now).ToString(), ClaimValueTypes.Integer64)
            ],
            expires: now.AddMinutes(ClientAssertionValidityMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
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

        // eSignet serves discovery under /v1/esignet (the root path answers 404 on
        // live Fayda); the generic OIDC location is the fallback.
        var issuerBase = _issuerUrl.TrimEnd('/');
        foreach (var candidate in new[] { $"{issuerBase}{EsignetDiscoveryPath}", $"{issuerBase}/.well-known/openid-configuration" })
        {
            var (found, document) = await TryFetchDiscoveryAsync(candidate);
            if (found)
            {
                return document;
            }
        }
        return null;
    }

    /// <summary>
    /// One discovery location. found is false when nothing usable is there (try the
    /// next one); true with a null document when the document names another issuer.
    /// </summary>
    private async Task<(bool Found, FaydaDiscoveryDocument? Document)> TryFetchDiscoveryAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            var json = await response.Content.ReadAsStringAsync();
            var document = JsonSerializer.Deserialize(json, FaydaJsonContext.Default.FaydaDiscoveryDocument);
            // OIDC Discovery 4.3: the document must describe the configured issuer,
            // otherwise its endpoints (token, JWKS) are not trusted.
            if (document == null)
            {
                return (false, null);
            }
            if (!string.Equals(document.Issuer?.TrimEnd('/'), _issuerUrl.TrimEnd('/'), StringComparison.Ordinal))
            {
                // Falls back to issuer-derived endpoints; usually means the SSM issuer
                // does not match what the provider publishes.
                Console.WriteLine($"[FaydaOidc] WARN discovery issuer '{document.Issuer}' does not match configured issuer '{_issuerUrl}'; ignoring discovery");
                _discoveryRejectedUntil = DateTime.UtcNow.AddMinutes(RejectedDiscoveryCacheMinutes);
                return (true, null);
            }

            _cachedDiscovery = document;
            _discoveryCacheExpiry = DateTime.UtcNow.AddMinutes(DiscoveryCacheMinutes);
            return (true, document);
        }
        catch (Exception)
        {
            return (false, null);
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
        // Live Fayda's advertised jwks_uri answers 404; the keys are served here.
        candidates.Add($"{issuerBase}{EsignetJwksPath}");
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

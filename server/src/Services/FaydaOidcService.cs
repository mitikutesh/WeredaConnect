using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Fayda National ID OIDC service implementation.
/// Exchanges authorization codes with Fayda IdP, validates ID tokens,
/// and generates/validates short-lived verification tokens for registration.
/// AOT-compatible — uses System.Text.Json and System.IdentityModel.Tokens.Jwt.
/// </summary>
public sealed class FaydaOidcService : IFaydaOidcService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _issuerUrl;
    private readonly byte[] _signingSecret;

    private const int VerificationTokenValidityMinutes = 30;
    private const int JwksCacheMinutes = 60;

    // Cached JWKS keys from Fayda issuer (thread-safe via volatile + timestamp check)
    private static volatile IList<SecurityKey>? _cachedJwksKeys;
    private static DateTime _jwksCacheExpiry = DateTime.MinValue;

    public FaydaOidcService(
        HttpClient httpClient,
        string clientId,
        string clientSecret,
        string issuerUrl,
        string signingSecret)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _issuerUrl = issuerUrl ?? throw new ArgumentNullException(nameof(issuerUrl));
        _signingSecret = Encoding.UTF8.GetBytes(signingSecret ?? throw new ArgumentNullException(nameof(signingSecret)));
    }

    public async Task<AuthResult<FaydaVerificationResult>> ExchangeCodeAsync(
        string authorizationCode,
        string redirectUri)
    {
        var tokenEndpoint = $"{_issuerUrl.TrimEnd('/')}/token";

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "code", authorizationCode },
            { "redirect_uri", redirectUri },
            { "client_id", _clientId },
            { "client_secret", _clientSecret }
        });

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.PostAsync(tokenEndpoint, formContent);
        }
        catch (Exception)
        {
            return AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable.");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid or expired authorization code.", "authorizationCode");
        }

        var responseBody = await httpResponse.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize(responseBody, FaydaJsonContext.Default.FaydaTokenResponse);

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

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuerUrl.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwksKeys,
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

        var faydaId = principal.FindFirst("sub")?.Value;
        var name = principal.FindFirst("name")?.Value
                   ?? principal.FindFirst("given_name")?.Value
                   ?? "Unknown";

        if (string.IsNullOrEmpty(faydaId))
        {
            return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Missing identity in Fayda response.", "authorizationCode");
        }

        return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
        {
            FaydaId = faydaId,
            FullName = name
        });
    }

    /// <summary>
    /// Fetch and cache JWKS keys from the Fayda issuer's well-known endpoint.
    /// Tries standard OIDC jwks_uri, then falls back to common eSignet path.
    /// Keys are cached for 60 minutes to avoid per-request network calls.
    /// </summary>
    private async Task<IList<SecurityKey>?> FetchJwksKeysAsync()
    {
        if (_cachedJwksKeys != null && DateTime.UtcNow < _jwksCacheExpiry)
        {
            return _cachedJwksKeys;
        }

        var issuerBase = _issuerUrl.TrimEnd('/');
        string? jwksJson = null;

        // Try standard OIDC JWKS endpoint
        var jwksEndpoints = new[]
        {
            $"{issuerBase}/.well-known/jwks.json",
            $"{issuerBase}/oauth/jwks",
            $"{issuerBase}/jwks"
        };

        foreach (var endpoint in jwksEndpoints)
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

    public string GenerateVerificationToken(string faydaId, string name)
    {
        var key = new SymmetricSecurityKey(_signingSecret);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("faydaId", faydaId),
            new Claim("name", name),
            new Claim("purpose", "fayda-verification")
        };

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

            if (purpose != "fayda-verification" || string.IsNullOrEmpty(faydaId))
            {
                return AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                    "Invalid verification token.", "faydaVerificationToken");
            }

            return AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = faydaId,
                FullName = name ?? "Unknown"
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

/// <summary>
/// Source-generated JSON context for Fayda-specific types (AOT-safe).
/// </summary>
[JsonSerializable(typeof(FaydaTokenResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FaydaJsonContext : JsonSerializerContext
{
}

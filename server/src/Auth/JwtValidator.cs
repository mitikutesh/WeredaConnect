using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace WeredaConnect.Server.Auth;

/// <summary>
/// Result of JWT token validation.
/// </summary>
public sealed record JwtValidationResult
{
    public required bool IsValid { get; init; }
    public string? UserId { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Role { get; init; }
    public string? Woreda { get; init; }
    public string? ErrorMessage { get; init; }

    public static JwtValidationResult Valid(string userId, string phoneNumber, string role, string woreda) =>
        new()
        {
            IsValid = true,
            UserId = userId,
            PhoneNumber = phoneNumber,
            Role = role,
            Woreda = woreda
        };

    public static JwtValidationResult Invalid(string errorMessage) =>
        new()
        {
            IsValid = false,
            ErrorMessage = errorMessage
        };
}

/// <summary>
/// Interface for JWT token validation.
/// Enables testing with mocks while keeping production code AOT-compatible.
/// </summary>
public interface IJwtValidator
{
    JwtValidationResult ValidateToken(string token);
}

/// <summary>
/// Cognito JWT validator for API Gateway Lambda authorizer.
/// Validates token structure, expiry, issuer, audience, and extracts required claims.
///
/// AOT-compatible: uses System.IdentityModel.Tokens.Jwt for parsing and
/// Microsoft.IdentityModel.Tokens for validation parameters. No reflection.
///
/// JWKS keys should be fetched from:
/// https://cognito-idp.{region}.amazonaws.com/{userPoolId}/.well-known/jwks.json
/// and cached. API Gateway caches authorizer results for the configured TTL.
/// </summary>
public sealed class CognitoJwtValidator : IJwtValidator
{
    private readonly string _issuer;
    private readonly string _clientId;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParameters;

    /// <param name="userPoolId">Cognito User Pool ID (e.g., "me-south-1_AbCdEfGhI").</param>
    /// <param name="region">AWS region (e.g., "me-south-1").</param>
    /// <param name="clientId">Cognito App Client ID.</param>
    /// <param name="signingKey">Optional signing key. If null, signature validation is skipped (useful for testing).</param>
    public CognitoJwtValidator(string userPoolId, string region, string clientId, SecurityKey? signingKey = null)
    {
        ArgumentNullException.ThrowIfNull(userPoolId);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(clientId);

        _clientId = clientId;
        _issuer = $"https://cognito-idp.{region}.amazonaws.com/{userPoolId}";
        _tokenHandler = new JwtSecurityTokenHandler();

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            RequireExpirationTime = true,
            RequireSignedTokens = signingKey is not null,
            ValidateIssuerSigningKey = signingKey is not null,
            IssuerSigningKey = signingKey
        };

        // If no signing key provided, skip signature validation
        if (signingKey is null)
        {
            _validationParameters.SignatureValidator = (token, _) => new JwtSecurityToken(token);
        }
    }

    public JwtValidationResult ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return JwtValidationResult.Invalid("Token is required.");
        }

        try
        {
            if (!_tokenHandler.CanReadToken(token))
            {
                return JwtValidationResult.Invalid("Token is malformed and cannot be read.");
            }

            var principal = _tokenHandler.ValidateToken(token, _validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken)
            {
                return JwtValidationResult.Invalid("Token is not a valid JWT.");
            }

            // Extract required claims per auth-api.md contract
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? principal.FindFirst("sub")?.Value;
            var phoneNumber = principal.FindFirst("phone_number")?.Value;
            var role = principal.FindFirst("custom:role")?.Value;
            var woreda = principal.FindFirst("custom:woreda")?.Value;

            if (string.IsNullOrEmpty(userId))
                return JwtValidationResult.Invalid("Token is missing required claim: sub.");

            if (string.IsNullOrEmpty(phoneNumber))
                return JwtValidationResult.Invalid("Token is missing required claim: phone_number.");

            if (string.IsNullOrEmpty(role))
                return JwtValidationResult.Invalid("Token is missing required claim: custom:role.");

            if (string.IsNullOrEmpty(woreda))
                return JwtValidationResult.Invalid("Token is missing required claim: custom:woreda.");

            return JwtValidationResult.Valid(userId, phoneNumber, role, woreda);
        }
        catch (SecurityTokenExpiredException)
        {
            return JwtValidationResult.Invalid("Token has expired.");
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return JwtValidationResult.Invalid("Token has an invalid issuer.");
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return JwtValidationResult.Invalid("Token has an invalid audience.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return JwtValidationResult.Invalid("Token has an invalid signature.");
        }
        catch (SecurityTokenException ex)
        {
            return JwtValidationResult.Invalid($"Token validation failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return JwtValidationResult.Invalid($"Token is malformed: {ex.Message}");
        }
    }
}

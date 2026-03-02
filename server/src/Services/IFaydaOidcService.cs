using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Interface for Fayda National ID OIDC integration.
/// Handles code exchange, verification token generation, and validation.
/// </summary>
public interface IFaydaOidcService
{
    /// <summary>
    /// Exchange a Fayda authorization code for identity claims.
    /// Calls the Fayda token endpoint, validates the ID token via JWKS.
    /// </summary>
    Task<AuthResult<FaydaVerificationResult>> ExchangeCodeAsync(string authorizationCode, string redirectUri);

    /// <summary>
    /// Generate a short-lived verification token (30 min) signed with server key.
    /// Embeds faydaId and name as claims for use during registration.
    /// </summary>
    string GenerateVerificationToken(string faydaId, string name);

    /// <summary>
    /// Validate a verification token: check signature, expiry, extract claims.
    /// Returns the embedded FaydaVerificationResult on success.
    /// </summary>
    AuthResult<FaydaVerificationResult> ValidateVerificationToken(string token);
}

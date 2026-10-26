using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Interface for Fayda National ID OIDC integration.
/// Handles the authorization URL, code exchange (PKCE + nonce), verification
/// token generation, and validation.
/// </summary>
public interface IFaydaOidcService
{
    /// <summary>
    /// The provider's authorization URL for a login the server started: carries the
    /// client id, redirect URI, scope, state, nonce and the S256 PKCE challenge.
    /// <paramref name="loginHint"/>, when given, is passed as the OIDC <c>login_hint</c>;
    /// the server only sets it against the mock provider (see <c>FaydaStartRequest.MockLoginHint</c>).
    /// </summary>
    Task<AuthResult<string>> BuildAuthorizationUrlAsync(
        string redirectUri, string state, string nonce, string codeChallenge, string? loginHint = null);

    /// <summary>
    /// Exchange a Fayda authorization code for identity claims.
    /// Calls the token endpoint (with the PKCE verifier), validates the ID token via
    /// JWKS and checks its nonce against the one the server issued.
    /// </summary>
    Task<AuthResult<FaydaVerificationResult>> ExchangeCodeAsync(
        string authorizationCode, string redirectUri, string? codeVerifier = null, string? expectedNonce = null);

    /// <summary>
    /// Generate a short-lived verification token (30 min) signed with server key.
    /// Embeds faydaId, name and (when Fayda released it) the phone number; the
    /// token id makes it single-use at registration.
    /// </summary>
    string GenerateVerificationToken(string faydaId, string name, string? phoneNumber = null);

    /// <summary>
    /// Validate a verification token: check signature, expiry, extract claims.
    /// Returns the embedded FaydaVerificationResult on success.
    /// </summary>
    AuthResult<FaydaVerificationResult> ValidateVerificationToken(string token);
}

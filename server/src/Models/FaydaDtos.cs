namespace WeredaConnect.Server.Models;

/// <summary>
/// POST /auth/fayda-verify request body.
/// Client sends the authorization code received from Fayda OIDC redirect.
/// </summary>
public sealed record FaydaVerifyRequest
{
    public required string AuthorizationCode { get; init; }
    public required string State { get; init; }
    public required string RedirectUri { get; init; }
}

/// <summary>
/// POST /auth/fayda-verify success response (200 OK).
/// Returns a short-lived verification token to include in registration.
/// </summary>
public sealed record FaydaVerifyResponse
{
    public required bool Verified { get; init; }
    public required string FaydaId { get; init; }
    public required string VerifiedName { get; init; }
    public required string VerificationToken { get; init; }
    public required string ExpiresAt { get; init; }
}

/// <summary>
/// Internal result from Fayda OIDC code exchange (not serialized to API).
/// </summary>
public sealed record FaydaVerificationResult
{
    public required string FaydaId { get; init; }
    public required string FullName { get; init; }
}

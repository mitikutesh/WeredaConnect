using WeredaConnect.Server.Helpers;

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
/// POST /auth/fayda-start and /user/fayda-start request body (all fields optional).
/// </summary>
public sealed record FaydaStartRequest
{
    public const string DefaultRedirectUri = "weredaconnect://fayda-callback";

    /// <summary>Accepted for forward compatibility; the proof is bound to the phone Fayda releases, not to this value.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>The app's callback; only the registered deep links (and, against the mock, the web app's callback page) are accepted.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>
    /// Comma-separated web callback pages accepted only while the mock provider is in use
    /// (FAYDA_MOCK=true, which the infra guard refuses in production and DevBypass refuses
    /// on a prod function name). The real eSignet client registers the deep links only.
    /// </summary>
    public const string MockWebRedirectUrisEnv = "FAYDA_MOCK_WEB_REDIRECT_URIS";

    public static bool IsAllowedRedirect(string redirectUri, Func<string, string?>? env = null)
    {
        if (string.Equals(redirectUri, DefaultRedirectUri, StringComparison.Ordinal)
            || string.Equals(redirectUri, "weredaconnect://fayda-link-callback", StringComparison.Ordinal))
        {
            return true;
        }

        if (!DevBypass.IsEnabled("FAYDA_MOCK", env))
        {
            return false;
        }

        var read = env ?? Environment.GetEnvironmentVariable;
        var configured = read(MockWebRedirectUrisEnv);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsSafeWebRedirect(entry) && string.Equals(redirectUri, entry, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>https, or plain http on the loopback host for local web runs; never a query or fragment.</summary>
    private static bool IsSafeWebRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }
        return parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && (parsed.Host == "localhost" || parsed.Host == "127.0.0.1"));
    }
}

/// <summary>
/// fayda-start response: open <see cref="AuthorizeUrl"/> in the system browser and
/// send the returned code together with <see cref="State"/> to verify or link.
/// </summary>
public sealed record FaydaStartResponse
{
    public required string State { get; init; }
    public required string AuthorizeUrl { get; init; }
    public required string ExpiresAt { get; init; }
}

/// <summary>
/// POST /user/fayda-link success response (200 OK).
/// </summary>
public sealed record FaydaLinkResponse
{
    public required bool FaydaVerified { get; init; }
    public required string FaydaVerifiedAt { get; init; }
    public required string VerifiedName { get; init; }
    /// <summary>True when the phone is proven (was already, or Fayda released the same number).</summary>
    public required bool PhoneVerified { get; init; }
}

/// <summary>
/// POST /auth/fayda-verify success response (200 OK).
/// Returns a short-lived verification token to include in registration.
/// </summary>
public sealed record FaydaVerifyResponse
{
    public required bool Verified { get; init; }
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
    /// <summary>E.164 phone from Fayda's phone_number claim, when the IdP released it.</summary>
    public string? PhoneNumber { get; init; }
    /// <summary>Verification-token id, used to make the token single-use at registration.</summary>
    public string? Jti { get; init; }
}

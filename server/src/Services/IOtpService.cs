namespace WeredaConnect.Server.Services;

/// <summary>A freshly issued one-time code and when it stops being valid.</summary>
public sealed record OtpIssueResult
{
    public required string Code { get; init; }
    public required string ExpiresAt { get; init; }
}

/// <summary>
/// Phone-ownership one-time codes for registration. The code itself is only
/// ever handed to the SMS sender; the store keeps an HMAC of it.
/// </summary>
public interface IOtpService
{
    /// <summary>Marker returned by <see cref="VerifyAsync"/> when the dev bypass code was used.</summary>
    const string AnyUser = "*";

    /// <summary>
    /// Issue a new code for the phone, bound to the registration (Cognito sub)
    /// it was sent for. Fails with AUTH_006 while the resend cooldown from the
    /// previous code is still running.
    /// </summary>
    Task<AuthResult<OtpIssueResult>> IssueAsync(string phoneNumber, string userId);

    /// <summary>
    /// Check a code. Success consumes the code and returns the user id it was
    /// bound to (<see cref="AnyUser"/> for the dev bypass). Any failure is
    /// AUTH_012; the caller counts attempts through the rate limiter.
    /// </summary>
    Task<AuthResult<string>> VerifyAsync(string phoneNumber, string code);
}

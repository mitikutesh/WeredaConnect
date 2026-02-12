using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.RateLimiting;

/// <summary>
/// Application-level rate limiting per auth-api.md contract.
/// Complements API Gateway throttling with per-user tracking in DynamoDB.
/// </summary>
public interface IRateLimitService
{
    /// <summary>
    /// Check if login is allowed for this phone number.
    /// Returns failure with AUTH_015 (403) if account is locked.
    /// </summary>
    Task<AuthResult<bool>> CheckLoginAllowedAsync(string phoneNumber);

    /// <summary>
    /// Record a failed login attempt. Locks account after 3 consecutive failures (15-min lockout).
    /// Returns the new failure count.
    /// </summary>
    Task<int> RecordFailedLoginAsync(string phoneNumber);

    /// <summary>
    /// Reset login failure counter on successful login.
    /// </summary>
    Task ResetLoginAttemptsAsync(string phoneNumber);

    /// <summary>
    /// Check if registration is allowed for this phone number (max 5 per day).
    /// Returns failure with AUTH_006 (429) if limit exceeded.
    /// </summary>
    Task<AuthResult<bool>> CheckRegistrationAllowedAsync(string phoneNumber);

    /// <summary>
    /// Record a successful registration for flood tracking.
    /// </summary>
    Task RecordRegistrationAsync(string phoneNumber);

    /// <summary>
    /// Check if OTP verification attempt is allowed (max 3 per code).
    /// Returns failure with AUTH_012 (400) if limit exceeded.
    /// </summary>
    Task<AuthResult<bool>> CheckOtpAttemptAllowedAsync(string phoneNumber);

    /// <summary>
    /// Record a failed OTP attempt.
    /// </summary>
    Task RecordFailedOtpAttemptAsync(string phoneNumber);

    /// <summary>
    /// Reset OTP attempt counter on successful verification.
    /// </summary>
    Task ResetOtpAttemptsAsync(string phoneNumber);
}
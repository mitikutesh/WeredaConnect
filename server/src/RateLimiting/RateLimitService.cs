using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.RateLimiting;

/// <summary>
/// Application-level rate limiting enforced via DynamoDB counters.
/// Implements the 4 rules from auth-api.md contract:
///   1. Failed login lockout: 3 consecutive failures → 15-minute lockout (AUTH_015)
///   2. Registration flood: 5 per phone per day (AUTH_006)
///   3. OTP attempts: 3 per OTP code (AUTH_012)
///   4. OTP resend: 1 per 60 seconds (tracked but not yet exposed as endpoint)
/// </summary>
public sealed class RateLimitService : IRateLimitService
{
    private readonly IRateLimitRepository _repository;

    // Thresholds from auth-api.md contract
    internal const int MaxLoginFailures = 3;
    internal const int LoginLockoutSeconds = 900; // 15 minutes
    internal const int MaxRegistrationsPerDay = 5;
    internal const int RegistrationWindowSeconds = 86400; // 24 hours
    internal const int MaxOtpAttempts = 3;
    internal const int OtpWindowSeconds = 300; // 5 minutes (matches OTP expiry)
    internal const int MaxSyncPerMinute = 60;
    internal const int SyncWindowSeconds = 60; // 1 minute

    public RateLimitService(IRateLimitRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<AuthResult<bool>> CheckLoginAllowedAsync(string phoneNumber)
    {
        var key = $"LOGIN#{phoneNumber}";
        var entry = await _repository.GetAsync(key);

        if (entry == null)
        {
            return AuthResult<bool>.Success(true);
        }

        // Check if account is locked
        if (entry.LockedUntil != null &&
            DateTime.TryParse(entry.LockedUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lockedUntil))
        {
            if (lockedUntil > DateTime.UtcNow)
            {
                var retryAfterSeconds = (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds);
                return AuthResult<bool>.Failure(403, "AUTH_015",
                    "Account locked due to too many failed attempts.", retryAfterSeconds: retryAfterSeconds);
            }

            // Lockout expired — reset counter so user gets fresh attempts
            await _repository.DeleteAsync(key);
            return AuthResult<bool>.Success(true);
        }

        // Not locked, but the failed-attempt window may have elapsed. DynamoDB
        // TTL deletion is best-effort and lags, so reset the stale counter in
        // code — otherwise the count accumulates across windows (the LOGIN# key
        // is reused, unlike the date/hour-bucketed REGISTER#/FRAUD_ keys) and
        // locks the user out earlier than the intended window. See IsWindowExpired.
        if (IsWindowExpired(entry))
        {
            await _repository.DeleteAsync(key);
        }

        return AuthResult<bool>.Success(true);
    }

    public async Task<int> RecordFailedLoginAsync(string phoneNumber)
    {
        var key = $"LOGIN#{phoneNumber}";
        var newCount = await _repository.IncrementAsync(key, LoginLockoutSeconds);

        // Lock account if threshold reached
        if (newCount >= MaxLoginFailures)
        {
            var lockedUntil = DateTime.UtcNow.AddSeconds(LoginLockoutSeconds).ToString("o");
            await _repository.SetLockAsync(key, lockedUntil, LoginLockoutSeconds);
        }

        return newCount;
    }

    public async Task ResetLoginAttemptsAsync(string phoneNumber)
    {
        var key = $"LOGIN#{phoneNumber}";
        await _repository.DeleteAsync(key);
    }

    public async Task<AuthResult<bool>> CheckRegistrationAllowedAsync(string phoneNumber)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var key = $"REGISTER#{phoneNumber}#{date}";
        var entry = await _repository.GetAsync(key);

        if (entry != null && entry.Count >= MaxRegistrationsPerDay)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many registration attempts. Please try again tomorrow.");
        }

        return AuthResult<bool>.Success(true);
    }

    public async Task RecordRegistrationAsync(string phoneNumber)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var key = $"REGISTER#{phoneNumber}#{date}";
        await _repository.IncrementAsync(key, RegistrationWindowSeconds);
    }

    public async Task<AuthResult<bool>> CheckOtpAttemptAllowedAsync(string phoneNumber)
    {
        var key = $"OTP#{phoneNumber}";
        var entry = await _repository.GetAsync(key);

        if (entry == null)
        {
            return AuthResult<bool>.Success(true);
        }

        // Reset when the window has elapsed. DynamoDB TTL deletion is best-effort
        // (can lag by up to 48h), so we cannot rely on the item disappearing —
        // enforce the window in code or the count accumulates across windows.
        if (IsWindowExpired(entry))
        {
            await _repository.DeleteAsync(key);
            return AuthResult<bool>.Success(true);
        }

        if (entry.Count >= MaxOtpAttempts)
        {
            return AuthResult<bool>.Failure(400, "AUTH_012",
                "Maximum OTP verification attempts exceeded. Please request a new OTP.", "otpCode");
        }

        return AuthResult<bool>.Success(true);
    }

    public async Task RecordFailedOtpAttemptAsync(string phoneNumber)
    {
        var key = $"OTP#{phoneNumber}";
        await _repository.IncrementAsync(key, OtpWindowSeconds);
    }

    public async Task ResetOtpAttemptsAsync(string phoneNumber)
    {
        var key = $"OTP#{phoneNumber}";
        await _repository.DeleteAsync(key);
    }

    public async Task<AuthResult<bool>> CheckSyncAllowedAsync(string userId)
    {
        var key = $"SYNC#{userId}";
        var entry = await _repository.GetAsync(key);

        if (entry == null)
        {
            return AuthResult<bool>.Success(true);
        }

        // Reset when the 60s window has elapsed. DynamoDB TTL deletion is
        // best-effort and can lag by up to 48h, so the stale counter MUST be
        // treated as expired here — otherwise the count accumulates across
        // windows and the user is rate-limited permanently (every write goes
        // through /sync, so this locks the whole app for that user).
        if (IsWindowExpired(entry))
        {
            await _repository.DeleteAsync(key);
            return AuthResult<bool>.Success(true);
        }

        if (entry.Count >= MaxSyncPerMinute)
        {
            return AuthResult<bool>.Failure(429, "SYNC_RATE_LIMITED",
                "Too many sync requests. Please try again later.");
        }

        return AuthResult<bool>.Success(true);
    }

    public async Task RecordSyncAsync(string userId)
    {
        var key = $"SYNC#{userId}";
        await _repository.IncrementAsync(key, SyncWindowSeconds);
    }

    /// <summary>
    /// Whether a counter's fixed window has elapsed. The window end is anchored
    /// on the first request (see <see cref="RateLimitRepository.IncrementAsync"/>,
    /// which sets the TTL with <c>if_not_exists</c>), so once <see cref="RateLimitEntry.Ttl"/>
    /// is in the past the count belongs to a stale window and must be reset.
    /// </summary>
    private static bool IsWindowExpired(RateLimitEntry entry) =>
        entry.Ttl > 0 && entry.Ttl <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

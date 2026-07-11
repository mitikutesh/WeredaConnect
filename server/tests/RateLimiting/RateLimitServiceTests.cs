using NSubstitute;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.RateLimiting;

public class RateLimitServiceTests
{
    private readonly IRateLimitRepository _mockRepo;
    private readonly RateLimitService _service;

    public RateLimitServiceTests()
    {
        _mockRepo = Substitute.For<IRateLimitRepository>();
        _service = new RateLimitService(_mockRepo);
    }

    // --- Login lockout ---

    [Fact]
    public async Task CheckLoginAllowed_NoEntry_ReturnsSuccess()
    {
        _mockRepo.GetAsync("LOGIN#+251912345678").Returns((RateLimitEntry?)null);

        var result = await _service.CheckLoginAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckLoginAllowed_NotLocked_ReturnsSuccess()
    {
        _mockRepo.GetAsync("LOGIN#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "LOGIN#+251912345678",
            Count = 2,
            LockedUntil = null,
            Ttl = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()
        });

        var result = await _service.CheckLoginAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckLoginAllowed_Locked_Returns403()
    {
        var lockedUntil = DateTime.UtcNow.AddMinutes(10).ToString("o");
        _mockRepo.GetAsync("LOGIN#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "LOGIN#+251912345678",
            Count = 3,
            LockedUntil = lockedUntil,
            Ttl = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()
        });

        var result = await _service.CheckLoginAllowedAsync("+251912345678");

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.Error!.HttpStatus);
        Assert.Equal("AUTH_015", result.Error.Code);
    }

    [Fact]
    public async Task CheckLoginAllowed_LockExpired_ReturnsSuccessAndResetsCounter()
    {
        var lockedUntil = DateTime.UtcNow.AddMinutes(-1).ToString("o");
        _mockRepo.GetAsync("LOGIN#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "LOGIN#+251912345678",
            Count = 3,
            LockedUntil = lockedUntil,
            Ttl = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()
        });

        var result = await _service.CheckLoginAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
        // Counter must be deleted so user gets fresh 3 attempts
        await _mockRepo.Received(1).DeleteAsync("LOGIN#+251912345678");
    }

    [Fact]
    public async Task RecordFailedLogin_BelowThreshold_DoesNotLock()
    {
        _mockRepo.IncrementAsync("LOGIN#+251912345678", RateLimitService.LoginLockoutSeconds)
            .Returns(2);

        var count = await _service.RecordFailedLoginAsync("+251912345678");

        Assert.Equal(2, count);
        await _mockRepo.DidNotReceive().SetLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task RecordFailedLogin_AtThreshold_SetsLock()
    {
        _mockRepo.IncrementAsync("LOGIN#+251912345678", RateLimitService.LoginLockoutSeconds)
            .Returns(3);

        var count = await _service.RecordFailedLoginAsync("+251912345678");

        Assert.Equal(3, count);
        await _mockRepo.Received(1).SetLockAsync(
            "LOGIN#+251912345678",
            Arg.Any<string>(),
            RateLimitService.LoginLockoutSeconds);
    }

    [Fact]
    public async Task RecordFailedLogin_AboveThreshold_SetsLock()
    {
        _mockRepo.IncrementAsync("LOGIN#+251912345678", RateLimitService.LoginLockoutSeconds)
            .Returns(5);

        await _service.RecordFailedLoginAsync("+251912345678");

        await _mockRepo.Received(1).SetLockAsync(
            "LOGIN#+251912345678",
            Arg.Any<string>(),
            RateLimitService.LoginLockoutSeconds);
    }

    [Fact]
    public async Task ResetLoginAttempts_DeletesEntry()
    {
        await _service.ResetLoginAttemptsAsync("+251912345678");

        await _mockRepo.Received(1).DeleteAsync("LOGIN#+251912345678");
    }

    // --- Registration flood ---

    [Fact]
    public async Task CheckRegistrationAllowed_NoEntry_ReturnsSuccess()
    {
        _mockRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        var result = await _service.CheckRegistrationAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckRegistrationAllowed_BelowLimit_ReturnsSuccess()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        _mockRepo.GetAsync($"REGISTER#+251912345678#{date}").Returns(new RateLimitEntry
        {
            Pk = $"REGISTER#+251912345678#{date}",
            Count = 4,
            Ttl = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()
        });

        var result = await _service.CheckRegistrationAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckRegistrationAllowed_AtLimit_Returns429()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        _mockRepo.GetAsync($"REGISTER#+251912345678#{date}").Returns(new RateLimitEntry
        {
            Pk = $"REGISTER#+251912345678#{date}",
            Count = 5,
            Ttl = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()
        });

        var result = await _service.CheckRegistrationAllowedAsync("+251912345678");

        Assert.False(result.IsSuccess);
        Assert.Equal(429, result.Error!.HttpStatus);
        Assert.Equal("AUTH_006", result.Error.Code);
    }

    [Fact]
    public async Task RecordRegistration_IncrementsWithDailyKey()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await _service.RecordRegistrationAsync("+251912345678");

        await _mockRepo.Received(1).IncrementAsync(
            $"REGISTER#+251912345678#{date}",
            RateLimitService.RegistrationWindowSeconds);
    }

    // --- OTP attempts ---

    [Fact]
    public async Task CheckOtpAttemptAllowed_NoEntry_ReturnsSuccess()
    {
        _mockRepo.GetAsync("OTP#+251912345678").Returns((RateLimitEntry?)null);

        var result = await _service.CheckOtpAttemptAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckOtpAttemptAllowed_BelowLimit_ReturnsSuccess()
    {
        _mockRepo.GetAsync("OTP#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "OTP#+251912345678",
            Count = 2,
            Ttl = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        });

        var result = await _service.CheckOtpAttemptAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckOtpAttemptAllowed_AtLimit_Returns400()
    {
        _mockRepo.GetAsync("OTP#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "OTP#+251912345678",
            Count = 3,
            Ttl = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        });

        var result = await _service.CheckOtpAttemptAllowedAsync("+251912345678");

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.Error!.HttpStatus);
        Assert.Equal("AUTH_012", result.Error.Code);
    }

    [Fact]
    public async Task CheckOtpAttemptAllowed_AtLimitButWindowExpired_ResetsAndAllows()
    {
        _mockRepo.GetAsync("OTP#+251912345678").Returns(new RateLimitEntry
        {
            Pk = "OTP#+251912345678",
            Count = 3,
            // Window already elapsed — DynamoDB may not have deleted the row yet.
            Ttl = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
        });

        var result = await _service.CheckOtpAttemptAllowedAsync("+251912345678");

        Assert.True(result.IsSuccess);
        await _mockRepo.Received(1).DeleteAsync("OTP#+251912345678");
    }

    [Fact]
    public async Task RecordFailedOtpAttempt_IncrementsCounter()
    {
        await _service.RecordFailedOtpAttemptAsync("+251912345678");

        await _mockRepo.Received(1).IncrementAsync(
            "OTP#+251912345678",
            RateLimitService.OtpWindowSeconds);
    }

    // --- Sync rate limiting ---

    [Fact]
    public async Task CheckSyncAllowed_NoEntry_ReturnsSuccess()
    {
        _mockRepo.GetAsync("SYNC#user-1").Returns((RateLimitEntry?)null);

        var result = await _service.CheckSyncAllowedAsync("user-1");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckSyncAllowed_BelowLimit_ReturnsSuccess()
    {
        _mockRepo.GetAsync("SYNC#user-1").Returns(new RateLimitEntry
        {
            Pk = "SYNC#user-1",
            Count = RateLimitService.MaxSyncPerMinute - 1,
            Ttl = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
        });

        var result = await _service.CheckSyncAllowedAsync("user-1");

        Assert.True(result.IsSuccess);
        await _mockRepo.DidNotReceive().DeleteAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckSyncAllowed_AtLimitWithinWindow_Returns429()
    {
        _mockRepo.GetAsync("SYNC#user-1").Returns(new RateLimitEntry
        {
            Pk = "SYNC#user-1",
            Count = RateLimitService.MaxSyncPerMinute,
            Ttl = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
        });

        var result = await _service.CheckSyncAllowedAsync("user-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(429, result.Error!.HttpStatus);
        Assert.Equal("SYNC_RATE_LIMITED", result.Error.Code);
    }

    [Fact]
    public async Task CheckSyncAllowed_AtLimitButWindowExpired_ResetsAndAllows()
    {
        // Regression: a stale counter whose window has elapsed must reset in code.
        // DynamoDB TTL deletion lags (up to 48h), and previously the check trusted
        // it — so once Count hit the limit the user was rate-limited permanently.
        _mockRepo.GetAsync("SYNC#user-1").Returns(new RateLimitEntry
        {
            Pk = "SYNC#user-1",
            Count = RateLimitService.MaxSyncPerMinute + 500,
            Ttl = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds()
        });

        var result = await _service.CheckSyncAllowedAsync("user-1");

        Assert.True(result.IsSuccess);
        await _mockRepo.Received(1).DeleteAsync("SYNC#user-1");
    }

    [Fact]
    public async Task RecordSync_IncrementsWithSyncWindow()
    {
        await _service.RecordSyncAsync("user-1");

        await _mockRepo.Received(1).IncrementAsync(
            "SYNC#user-1",
            RateLimitService.SyncWindowSeconds);
    }

    [Fact]
    public async Task ResetOtpAttempts_DeletesEntry()
    {
        await _service.ResetOtpAttemptsAsync("+251912345678");

        await _mockRepo.Received(1).DeleteAsync("OTP#+251912345678");
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new RateLimitService(null!));
    }
}

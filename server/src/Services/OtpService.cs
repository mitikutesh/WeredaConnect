using System.Security.Cryptography;
using System.Text;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Services;

/// <summary>
/// One-time codes stored as HMAC-SHA256 hashes in the auth-state store.
/// 6 digits, 5 minute validity, 60 second resend cooldown, constant-time compare.
/// DEV_OTP_BYPASS=true accepts 123456 (the production bypass guard forbids the flag).
/// </summary>
public sealed class OtpService : IOtpService
{
    internal const int ValiditySeconds = 300;
    internal const int ResendCooldownSeconds = 60;
    internal const string DevBypassCode = "123456";

    private const string KeyPrefix = "AUTHSTATE#OTP#";
    private const string HashAttribute = "codeHash";
    private const string IssuedAtAttribute = "issuedAt";
    private const string ExpiresAtIsoAttribute = "expiresAtIso";
    private const string UserIdAttribute = "userId";

    private readonly IAuthStateRepository _store;
    private readonly byte[] _secret;
    private readonly Func<string?> _devBypassReader;
    private readonly Func<DateTimeOffset> _clock;

    public OtpService(
        IAuthStateRepository store,
        byte[] secret,
        Func<string?>? devBypassReader = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        if (_secret.Length < 16)
        {
            throw new ArgumentException("OTP secret must be at least 16 bytes.", nameof(secret));
        }
        _devBypassReader = devBypassReader
            ?? (() => Helpers.DevBypass.IsEnabled("DEV_OTP_BYPASS") ? "true" : null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AuthResult<OtpIssueResult>> IssueAsync(string phoneNumber, string userId)
    {
        var key = KeyPrefix + phoneNumber;
        var now = _clock();

        var existing = await _store.GetAsync(key);
        if (existing != null
            && long.TryParse(existing.Get(IssuedAtAttribute), out var issuedAt)
            && issuedAt + ResendCooldownSeconds > now.ToUnixTimeSeconds())
        {
            var retryAfter = (int)(issuedAt + ResendCooldownSeconds - now.ToUnixTimeSeconds());
            return AuthResult<OtpIssueResult>.Failure(429, "AUTH_006",
                "Please wait before requesting another code.", retryAfterSeconds: retryAfter);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAt = now.AddSeconds(ValiditySeconds);

        await _store.PutAsync(key, new Dictionary<string, string>
        {
            { HashAttribute, Hash(phoneNumber, code) },
            { IssuedAtAttribute, now.ToUnixTimeSeconds().ToString() },
            { ExpiresAtIsoAttribute, expiresAt.UtcDateTime.ToString("o") },
            { UserIdAttribute, userId }
        }, ValiditySeconds);

        return AuthResult<OtpIssueResult>.Success(new OtpIssueResult
        {
            Code = code,
            ExpiresAt = expiresAt.UtcDateTime.ToString("o")
        });
    }

    public async Task<AuthResult<string>> VerifyAsync(string phoneNumber, string code)
    {
        var key = KeyPrefix + phoneNumber;

        if (_devBypassReader() == "true" && code == DevBypassCode)
        {
            await _store.DeleteAsync(key);
            return AuthResult<string>.Success(IOtpService.AnyUser);
        }

        var entry = await _store.GetAsync(key);
        var storedHash = entry?.Get(HashAttribute);
        if (storedHash == null)
        {
            return AuthResult<string>.Failure(400, "AUTH_012",
                "Invalid or expired code. Please request a new one.", "otpCode");
        }

        var expected = Encoding.UTF8.GetBytes(storedHash);
        var actual = Encoding.UTF8.GetBytes(Hash(phoneNumber, code));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return AuthResult<string>.Failure(400, "AUTH_012",
                "Invalid or expired code. Please request a new one.", "otpCode");
        }

        await _store.DeleteAsync(key);
        return AuthResult<string>.Success(entry!.Get(UserIdAttribute) ?? IOtpService.AnyUser);
    }

    private string Hash(string phoneNumber, string code)
    {
        var mac = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"{phoneNumber}:{code}"));
        return Convert.ToBase64String(mac);
    }
}

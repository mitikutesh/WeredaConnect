using System.Text;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

public class OtpServiceTests
{
    private const string Phone = "+251912345678";
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("unit-test-secret-at-least-16-bytes");

    /// <summary>In-memory stand-in with the same expiry semantics as the DynamoDB store.</summary>
    private sealed class FakeAuthState : IAuthStateRepository
    {
        private readonly Dictionary<string, (Dictionary<string, string> attrs, long expiresAt)> _rows = new();

        public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

        public Task<AuthStateEntry?> GetAsync(string key) => Task.FromResult(Read(key));

        public Task PutAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds)
        {
            _rows[key] = (new Dictionary<string, string>(attributes), Now().ToUnixTimeSeconds() + ttlSeconds);
            return Task.CompletedTask;
        }

        public Task<bool> PutIfAbsentAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds)
        {
            if (_rows.ContainsKey(key))
            {
                return Task.FromResult(false);
            }
            _rows[key] = (new Dictionary<string, string>(attributes), Now().ToUnixTimeSeconds() + ttlSeconds);
            return Task.FromResult(true);
        }

        public Task<AuthStateEntry?> ConsumeAsync(string key)
        {
            var entry = Read(key);
            _rows.Remove(key);
            return Task.FromResult(entry);
        }

        public Task DeleteAsync(string key)
        {
            _rows.Remove(key);
            return Task.CompletedTask;
        }

        public void ExpireEverything() => Now = () => DateTimeOffset.UtcNow.AddHours(1);

        private AuthStateEntry? Read(string key)
        {
            if (!_rows.TryGetValue(key, out var row) || row.expiresAt <= Now().ToUnixTimeSeconds())
            {
                return null;
            }
            return new AuthStateEntry { Key = key, Attributes = row.attrs, ExpiresAt = row.expiresAt };
        }
    }

    private static OtpService Create(FakeAuthState store, string? devBypass = null)
        => new(store, Secret, () => devBypass, () => store.Now());

    [Fact]
    public async Task Issue_ThenVerify_SucceedsOnceOnly()
    {
        var store = new FakeAuthState();
        var service = Create(store);

        var issued = await service.IssueAsync(Phone, "user-1");
        Assert.True(issued.IsSuccess);
        Assert.Matches("^[0-9]{6}$", issued.Value!.Code);

        var first = await service.VerifyAsync(Phone, issued.Value.Code);
        Assert.True(first.IsSuccess);
        Assert.Equal("user-1", first.Value);

        var second = await service.VerifyAsync(Phone, issued.Value.Code);
        Assert.False(second.IsSuccess);
        Assert.Equal("AUTH_012", second.Error!.Code);
    }

    [Fact]
    public async Task Verify_WrongCode_Fails()
    {
        var store = new FakeAuthState();
        var service = Create(store);
        var issued = await service.IssueAsync(Phone, "user-1");
        var wrong = issued.Value!.Code == "000000" ? "111111" : "000000";

        var result = await service.VerifyAsync(Phone, wrong);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_012", result.Error!.Code);
    }

    [Fact]
    public async Task Verify_OtherPhoneSameCode_Fails()
    {
        var store = new FakeAuthState();
        var service = Create(store);
        var issued = await service.IssueAsync(Phone, "user-1");

        var result = await service.VerifyAsync("+251911111111", issued.Value!.Code);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Verify_ExpiredCode_Fails()
    {
        var store = new FakeAuthState();
        var service = Create(store);
        var issued = await service.IssueAsync(Phone, "user-1");
        store.ExpireEverything();

        var result = await service.VerifyAsync(Phone, issued.Value!.Code);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Issue_WithinCooldown_IsRateLimited()
    {
        var store = new FakeAuthState();
        var service = Create(store);
        await service.IssueAsync(Phone, "user-1");

        var again = await service.IssueAsync(Phone, "user-1");

        Assert.False(again.IsSuccess);
        Assert.Equal("AUTH_006", again.Error!.Code);
        Assert.NotNull(again.Error.RetryAfterSeconds);
        Assert.InRange(again.Error.RetryAfterSeconds!.Value, 1, OtpService.ResendCooldownSeconds);
    }

    [Fact]
    public async Task Issue_AfterCooldown_ReplacesCode()
    {
        var store = new FakeAuthState();
        var service = Create(store);
        var first = await service.IssueAsync(Phone, "user-1");
        store.Now = () => DateTimeOffset.UtcNow.AddSeconds(OtpService.ResendCooldownSeconds + 1);

        var second = await service.IssueAsync(Phone, "user-1");

        Assert.True(second.IsSuccess);
        if (first.Value!.Code != second.Value!.Code)
        {
            Assert.False((await service.VerifyAsync(Phone, first.Value.Code)).IsSuccess);
        }
        Assert.True((await service.VerifyAsync(Phone, second.Value.Code)).IsSuccess);
    }

    [Fact]
    public async Task Verify_DevBypass_AcceptsMagicCodeOnlyWhenEnabled()
    {
        var store = new FakeAuthState();

        var enabled = Create(store, devBypass: "true");
        var bypassed = await enabled.VerifyAsync(Phone, OtpService.DevBypassCode);
        Assert.True(bypassed.IsSuccess);
        Assert.Equal(IOtpService.AnyUser, bypassed.Value);

        var disabled = Create(store);
        Assert.False((await disabled.VerifyAsync(Phone, OtpService.DevBypassCode)).IsSuccess);
    }

    [Fact]
    public void Constructor_RejectsShortSecret()
    {
        Assert.Throws<ArgumentException>(() => new OtpService(new FakeAuthState(), new byte[8]));
    }
}

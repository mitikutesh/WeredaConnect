using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// SIM-48 / ESC-11: sync idempotency keys are scoped to the authenticated caller,
/// so the same client key from two users never maps to the same stored key.
/// </summary>
public class SyncIdempotencyKeyTests
{
    [Fact]
    public void ForCaller_DocumentedFormatKeyForCaller_ReturnsKeyUnchanged()
    {
        const string key = "farmer-001:crop_listing:crop-1:create:1";

        Assert.Equal(key, SyncIdempotencyKey.ForCaller("farmer-001", key));
    }

    [Fact]
    public void ForCaller_KeyWithoutUserSegment_PrefixesCaller()
    {
        Assert.Equal("farmer-001:web_create_order-1",
            SyncIdempotencyKey.ForCaller("farmer-001", "web_create_order-1"));
    }

    [Fact]
    public void ForCaller_KeyWithAnotherUsersPrefix_PrefixesCaller()
    {
        const string key = "farmer-001:crop_listing:crop-1:create:1";

        Assert.Equal("farmer-002:" + key, SyncIdempotencyKey.ForCaller("farmer-002", key));
    }

    [Fact]
    public void ForCaller_CallerIdIsPrefixOfKeyOwner_PrefixesCaller()
    {
        // "farmer-001" is a string prefix of "farmer-0011" but not its segment.
        const string key = "farmer-0011:crop_listing:crop-1:create:1";

        Assert.Equal("farmer-001:" + key, SyncIdempotencyKey.ForCaller("farmer-001", key));
    }

    [Theory]
    [InlineData("farmer-001:crop_listing:crop-1:create:1")]
    [InlineData("farmer-002:crop_listing:crop-1:create:1")]
    [InlineData("web_create_order-1")]
    [InlineData("user:crop_listing:crop-1:create:1")]
    public void ForCaller_SameKeyTwoCallers_NeverCollide(string key)
    {
        var first = SyncIdempotencyKey.ForCaller("farmer-001", key);
        var second = SyncIdempotencyKey.ForCaller("farmer-002", key);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.StartsWith("farmer-001:", first);
        Assert.StartsWith("farmer-002:", second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad:id")]
    public void ForCaller_CallerNullEmptyOrWithColon_ReturnsNull(string? callerId)
    {
        Assert.Null(SyncIdempotencyKey.ForCaller(callerId, "farmer-001:crop_listing:crop-1:create:1"));
    }
}

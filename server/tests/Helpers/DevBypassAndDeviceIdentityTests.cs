using Amazon.Lambda.APIGatewayEvents;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

public class DevBypassTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> values) =>
        name => values.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void FlagOn_OutsideProd_IsEnabled()
    {
        Assert.True(DevBypass.IsEnabled("DEV_OTP_BYPASS", Env(new()
        {
            ["DEV_OTP_BYPASS"] = "true",
            ["AWS_LAMBDA_FUNCTION_NAME"] = "wereda-connect-dev-auth-verify-otp"
        })));
    }

    [Fact]
    public void FlagOff_IsDisabled()
    {
        Assert.False(DevBypass.IsEnabled("DEV_OTP_BYPASS", Env(new())));
        Assert.False(DevBypass.IsEnabled("DEV_OTP_BYPASS", Env(new() { ["DEV_OTP_BYPASS"] = "false" })));
    }

    [Theory]
    [InlineData("AWS_LAMBDA_FUNCTION_NAME", "wereda-connect-prod-auth-verify-otp")]
    [InlineData("ENVIRONMENT", "prod")]
    [InlineData("ENVIRONMENT", "production")]
    public void FlagOn_InProd_IsStillDisabled(string marker, string value)
    {
        Assert.False(DevBypass.IsEnabled("DEV_FAYDA_BYPASS", Env(new()
        {
            ["DEV_FAYDA_BYPASS"] = "true",
            [marker] = value
        })));
    }
}

public class DeviceIdentityTests
{
    private static APIGatewayProxyRequest WithHeaders(params (string name, string value)[] headers) => new()
    {
        Headers = headers.ToDictionary(h => h.name, h => h.value)
    };

    [Theory]
    [InlineData("device-aaaa-1111", "device-aaaa-1111")]
    [InlineData("  device-aaaa-1111 ", "device-aaaa-1111")]
    [InlineData("short", null)]
    [InlineData("has spaces in it!!", null)]
    [InlineData("", null)]
    public void ReadId_AcceptsOnlyInstallIdShapedValues(string raw, string? expected)
    {
        Assert.Equal(expected, DeviceIdentity.ReadId(WithHeaders(("x-device-id", raw))));
    }

    [Fact]
    public void ReadId_HeaderNameIsCaseInsensitive()
    {
        Assert.Equal("device-aaaa-1111", DeviceIdentity.ReadId(WithHeaders(("X-DEVICE-ID", "device-aaaa-1111"))));
        Assert.Null(DeviceIdentity.ReadId(new APIGatewayProxyRequest()));
    }

    [Theory]
    [InlineData("Samsung SM-A125F", "Samsung SM-A125F")]
    [InlineData("Tecno <a href=\"http://evil\">Spark</a>", "Tecno a hrefhttpevilSparka")]
    [InlineData("Call +251900000000 now! http://x.y/z", "Call 251900000000 now httpx.yz")]
    [InlineData("\u0007\u0000", "a new device")]
    [InlineData("", "a new device")]
    public void ReadName_StripsEverythingOutsideTheAllowList(string raw, string expected)
    {
        Assert.Equal(expected, DeviceIdentity.ReadName(WithHeaders(("X-Device-Name", raw))));
    }

    [Fact]
    public void ReadName_IsCappedAt40Characters()
    {
        var name = DeviceIdentity.ReadName(WithHeaders(("X-Device-Name", new string('a', 100))));
        Assert.Equal(40, name.Length);
    }

    [Fact]
    public void Remember_KeepsNewestAndCapsTheList()
    {
        var known = Enumerable.Range(0, DeviceIdentity.MaxKnownDevices).Select(i => $"d{i}").ToList();

        var updated = DeviceIdentity.Remember(known, "new-device");

        Assert.Equal(DeviceIdentity.MaxKnownDevices, updated.Count);
        Assert.Equal("new-device", updated[^1]);
        Assert.DoesNotContain("d0", updated);
        Assert.Equal(["d1", "new-device"], DeviceIdentity.Remember(["d1", "new-device"], "new-device").TakeLast(2));
    }
}

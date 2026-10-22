using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// Parsing of the Telebirr credentials blob, focused on the optional
/// <c>httpProxy</c> (Ethiopian egress) field and its env-override precedence.
/// </summary>
public class TelebirrConfigTests
{
    // Minimal valid blob; {{proxyLine}} injects an optional httpProxy entry.
    private static string Json(string? proxyLine) => $$"""
    {
      "fabricAppId": "fab",
      "appSecret": "sec",
      "merchantAppId": "app",
      "merchantCode": "220100",
      "baseUrl": "https://telebirr.example/gw",
      "webUrl": "https://telebirr.example/paygate",
      "merchantPrivateKeyPem": "pk",
      "telebirrPublicKeyPem": "pub",
      "returnUrl": "weredaconnect://payment/complete",
      "notifyUrl": "https://server.example/webhooks/telebirr"{{proxyLine}}
    }
    """;

    [Fact]
    public void FromJson_reads_httpProxy_from_the_secret_blob()
    {
        var cfg = TelebirrConfig.FromJson(Json(",\n  \"httpProxy\": \"http://et-proxy:8080\""));
        cfg.HttpProxy.Should().Be("http://et-proxy:8080");
    }

    [Fact]
    public void FromJson_env_override_wins_over_the_blob()
    {
        var cfg = TelebirrConfig.FromJson(
            Json(",\n  \"httpProxy\": \"http://from-blob:8080\""),
            httpProxy: "http://from-env:3128");
        cfg.HttpProxy.Should().Be("http://from-env:3128");
    }

    [Fact]
    public void FromJson_no_proxy_is_null()
    {
        TelebirrConfig.FromJson(Json(null)).HttpProxy.Should().BeNull();
    }

    [Fact]
    public void FromJson_blank_proxy_is_null()
    {
        TelebirrConfig.FromJson(Json(",\n  \"httpProxy\": \"   \"")).HttpProxy.Should().BeNull();
    }

    // FUP-36: the legacy delivery initiate is refused on live Telebirr. "Live" is read off the
    // base URL host, and anything that is not plainly the developer's own machine is live.
    [Theory]
    [InlineData("http://127.0.0.1:8790", true)]
    [InlineData("http://localhost:8790", true)]
    [InlineData("http://[::1]:8790", true)]
    [InlineData("http://host.docker.internal:8790", true)]
    [InlineData("https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway", false)]
    [InlineData("https://app.ethiotelebirr.et:38443/apiaccess/payment/gateway", false)]
    [InlineData("https://sim.example.ngrok.io", false)]
    [InlineData("not a url", false)]
    public void IsLocalSimulator_is_true_only_for_a_loopback_or_docker_host_base_url(string baseUrl, bool expected)
    {
        var cfg = TelebirrConfig.FromJson(Json(null).Replace("https://telebirr.example/gw", baseUrl));
        cfg.IsLocalSimulator.Should().Be(expected);
    }
}

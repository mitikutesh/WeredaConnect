using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>ESC-45: the one map a Telebirr notification is verified and read from.</summary>
public class TelebirrNotifyParserTests
{
    private const string Flat =
        "\"merch_order_id\":\"WCO1\",\"payment_order_id\":\"TB1\",\"trade_status\":\"Completed\",\"sign\":\"S\",\"sign_type\":\"SHA256WithRSA\"";

    [Fact]
    public void A_flat_body_is_read_with_its_signature()
    {
        TelebirrNotifyParser.TryParse("{" + Flat + "}", out var parsed, out _).Should().BeTrue();

        parsed!.Sign.Should().Be("S");
        parsed.Fields["payment_order_id"].Should().Be("TB1");
    }

    [Theory]
    [InlineData("\"trade_status=Completed\":\"x\"")]
    [InlineData("\"a&b\":\"x\"")]
    [InlineData("\"notify_url\":\"https://x/n&payment_order_id=TB1\"")]
    public void A_separator_in_a_signed_key_or_value_refuses_the_body(string member)
    {
        TelebirrNotifyParser.TryParse("{" + Flat + "," + member + "}", out _, out var error).Should().BeFalse();

        error.Should().Be("separator_in_field");
    }

    [Fact]
    public void An_equals_sign_in_a_value_is_kept()
    {
        TelebirrNotifyParser.TryParse("{" + Flat + ",\"notify_url\":\"https://x/n?a=1\"}", out var parsed, out _)
            .Should().BeTrue();

        parsed!.Fields["notify_url"].Should().Be("https://x/n?a=1");
    }

    [Fact]
    public void Unsigned_transport_keys_are_ignored_whatever_they_hold()
    {
        // Never in the signed string and never read: a URL query in raw_request must not refuse a
        // real notification, nor a header object.
        var body = "{" + Flat + ",\"raw_request\":\"appid=1&merch_code=2&sign=x\",\"header\":{\"a\":\"b&c\"},"
                   + "\"wallet_reference_data\":[1,2],\"refund_info\":null}";

        TelebirrNotifyParser.TryParse(body, out var parsed, out var error).Should().BeTrue(error);

        parsed!.Fields.Should().NotContainKeys("raw_request", "header", "wallet_reference_data", "refund_info");
    }

    [Fact]
    public void A_signature_given_twice_refuses_the_body()
    {
        TelebirrNotifyParser.TryParse("{" + Flat + ",\"sign\":\"T\"}", out _, out var error).Should().BeFalse();

        error.Should().Be("duplicate_key");
    }
}

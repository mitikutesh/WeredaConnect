using System.Globalization;
using System.Text.Json;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-45: reads an inbound Telebirr notification into ONE flat map of strings, the map that is
/// both verified and read (a field that is read but not signed would let a captured notification be
/// re-labelled). Telebirr's documented notify is a flat JSON body (Notify_Callback, "Step 7");
/// a live body is sometimes wrapped in a <c>data</c> envelope, and our earlier simulator nested the
/// payment fields in <c>biz_content</c>. Rules:
/// <list type="bullet">
/// <item>The source object is the top level, or <c>data</c> when the top level has no
/// <c>merch_order_id</c> and no <c>biz_content</c> but <c>data</c> has one; then only
/// <c>data</c>'s members are read. <c>sign</c> comes from the source, or from the top level only when
/// the source has none.</item>
/// <item>Strings as is, numbers by their raw text, booleans "1"/"0", null is absent.</item>
/// <item>A <c>biz_content</c> object is flattened one level with the same rules; any other object
/// or array value refuses the body, and so does a key seen twice (in one object, or between the
/// source and its <c>biz_content</c>).</item>
/// <item>A key containing <c>&amp;</c> or <c>=</c>, or a value containing <c>&amp;</c>, refuses the
/// body. The signed string joins <c>key=value</c> pairs with <c>&amp;</c> and escapes nothing, so
/// such a field could re-split a captured notification into a different map with the same signed
/// string (a payment reference moved into another field, for example).</item>
/// <item>Keys that are never signed (<see cref="TelebirrSignatureService.IsExcludedFromSigning"/>:
/// <c>raw_request</c>, <c>header</c>, <c>wallet_reference_data</c>, ...) are ignored whatever they
/// hold, apart from <c>sign</c> and <c>sign_type</c>; nothing reads them, and a URL query in
/// <c>raw_request</c> must not refuse a real notification.</item>
/// </list>
/// AOT-safe: <see cref="JsonDocument"/> only, no serializer metadata.
/// </summary>
public static class TelebirrNotifyParser
{
    /// <summary>A notification read into its signed field map plus its signature.</summary>
    public sealed record ParsedNotify(IReadOnlyDictionary<string, string> Fields, string? Sign);

    public static bool TryParse(string body, out ParsedNotify? parsed, out string error)
    {
        parsed = null;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not_an_object";
                return false;
            }

            var source = root;
            var fromEnvelope = false;
            if (!root.TryGetProperty("merch_order_id", out _)
                && !root.TryGetProperty("biz_content", out _)
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("merch_order_id", out _))
            {
                source = data;
                fromEnvelope = true;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!AddMembers(source, fields, seen, allowBizContent: true, out error))
            {
                return false;
            }

            // The signature itself is not signed, so taking it from outside the envelope reads no
            // unsigned payment field.
            var sign = fields.GetValueOrDefault("sign");
            if (sign == null && fromEnvelope
                && root.TryGetProperty("sign", out var rootSign) && rootSign.ValueKind == JsonValueKind.String)
            {
                sign = rootSign.GetString();
            }

            parsed = new ParsedNotify(fields, sign);
            return true;
        }
        catch (JsonException)
        {
            error = "unparseable";
            return false;
        }
    }

    /// <summary>
    /// The same map with <c>transId</c> renamed <c>trans_id</c>, or null when that rename does not
    /// apply. Telebirr signs the transaction id as <c>trans_id</c> but has been seen sending it as
    /// <c>transId</c>; the rename applies only when <c>trans_id</c> itself is absent, so an unsigned
    /// <c>trans_id</c> can never ride next to a signed <c>transId</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? WithTransIdAlias(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("transId", out var transId) || fields.ContainsKey("trans_id"))
        {
            return null;
        }

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (key != "transId")
            {
                renamed[key] = value;
            }
        }

        renamed["trans_id"] = transId;
        return renamed;
    }

    /// <summary>The payment fields the webhook acts on, read from the verified map.</summary>
    public static TelebirrWebhookBiz ToBiz(IReadOnlyDictionary<string, string> fields) => new()
    {
        MerchOrderId = fields.GetValueOrDefault("merch_order_id"),
        PaymentOrderId = fields.GetValueOrDefault("payment_order_id"),
        TotalAmount = fields.GetValueOrDefault("total_amount"),
        TransCurrency = fields.GetValueOrDefault("trans_currency"),
        TradeStatus = fields.GetValueOrDefault("trade_status"),
        MerchCode = fields.GetValueOrDefault("merch_code"),
        TransId = fields.GetValueOrDefault("trans_id"),
        Msisdn = fields.GetValueOrDefault("msisdn"),
    };

    /// <summary>
    /// When the notification was sent: <c>notify_time</c>, else <c>timestamp</c> (our earlier
    /// simulator), the first that is an epoch in range. The documented unit is contradictory
    /// (the schema says seconds, the sample and live bodies are milliseconds), so it is decided by
    /// digit range: 10^12 to 10^14 is milliseconds, 10^9 to 10^11 is seconds. Anything else is
    /// null, and the caller refuses the notification (fail closed).
    /// </summary>
    public static DateTimeOffset? SentAt(IReadOnlyDictionary<string, string> fields)
        => ParseEpoch(fields.GetValueOrDefault("notify_time")) ?? ParseEpoch(fields.GetValueOrDefault("timestamp"));

    public static DateTimeOffset? ParseEpoch(string? raw)
    {
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value switch
        {
            >= 1_000_000_000_000 and < 100_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(value),
            >= 1_000_000_000 and < 100_000_000_000 => DateTimeOffset.FromUnixTimeSeconds(value),
            _ => null,
        };
    }

    private static bool AddMembers(
        JsonElement obj, Dictionary<string, string> fields, HashSet<string> seen, bool allowBizContent, out string error)
    {
        error = "";
        foreach (var member in obj.EnumerateObject())
        {
            if (!seen.Add(member.Name))
            {
                error = "duplicate_key";
                return false;
            }

            var unsigned = TelebirrSignatureService.IsExcludedFromSigning(member.Name);
            if (unsigned && member.Name is not ("sign" or "sign_type"))
            {
                // Never signed and never read.
                continue;
            }

            if (!unsigned && member.Name.AsSpan().IndexOfAny('&', '=') >= 0)
            {
                error = "separator_in_field";
                return false;
            }

            var value = member.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    break;
                case JsonValueKind.String:
                    var text = value.GetString()!;
                    if (!unsigned && text.Contains('&'))
                    {
                        error = "separator_in_field";
                        return false;
                    }

                    fields[member.Name] = text;
                    break;
                case JsonValueKind.Number:
                    fields[member.Name] = value.GetRawText();
                    break;
                case JsonValueKind.True:
                    fields[member.Name] = "1";
                    break;
                case JsonValueKind.False:
                    fields[member.Name] = "0";
                    break;
                case JsonValueKind.Object when allowBizContent && member.Name == "biz_content":
                    if (!AddMembers(value, fields, seen, allowBizContent: false, out error))
                    {
                        return false;
                    }

                    break;
                default:
                    error = "nested_value";
                    return false;
            }
        }

        return true;
    }
}

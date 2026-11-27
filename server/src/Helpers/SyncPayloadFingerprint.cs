using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// The fingerprint of a sync operation, stored with its idempotency key (SIM-28,
/// sync-protocol.md 1.38.0). A key sent again with the same operation answers
/// <c>duplicate</c>; sent with a different one it is refused, so a changed payload is
/// never dropped as if it had been applied.
/// </summary>
/// <remarks>
/// The fingerprint is the lowercase hex SHA-256 of a canonical JSON of what the operation
/// asks for: <c>entityType</c>, <c>entityId</c>, <c>action</c>, <c>clientVersion</c> and
/// <c>data</c>. Object keys are sorted (ordinal) at every depth and numbers are written by
/// value (<c>18</c>, <c>18.0</c> and <c>1.8e1</c> are the same), so a retry that spells the same
/// payload differently matches. <c>operationId</c> and <c>timestamp</c> are left out: a
/// client may renew them on a resend. AOT-safe: the JSON is written by hand, no reflection.
/// </remarks>
public static class SyncPayloadFingerprint
{
    /// <summary>The fingerprint of <paramref name="operation"/> as it is now; take it before a handler changes its data.</summary>
    public static string Of(SyncOperationRequest operation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("action", operation.Action);
            if (operation.ClientVersion is int clientVersion)
            {
                writer.WriteNumber("clientVersion", clientVersion);
            }

            writer.WritePropertyName("data");
            WriteValue(writer, operation.Data);
            writer.WriteString("entityId", operation.EntityId);
            writer.WriteString("entityType", operation.EntityType);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                WriteElement(writer, element);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int or long or short or byte or decimal or double or float:
                WriteNumber(writer, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0");
                break;
            case IDictionary<string, object> map:
                writer.WriteStartObject();
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, map[key]);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element.GetRawText());
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>A number by value: the shortest decimal form, or the text as sent past the decimal range.</summary>
    private static void WriteNumber(Utf8JsonWriter writer, string text)
    {
        var canonical = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture)
            : text;
        writer.WriteRawValue(canonical, skipInputValidation: true);
    }
}

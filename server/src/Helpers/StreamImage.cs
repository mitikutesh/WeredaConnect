using System.Globalization;
using System.Text.Json;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-21: a DynamoDB Streams image (<c>NewImage</c> or <c>OldImage</c>) read straight from the
/// event JSON, in the AOT-friendly way the stream functions already use (<see cref="JsonDocument"/>,
/// no typed DynamoDBEvent). Attributes are DynamoDB JSON: <c>{"status":{"S":"funded"}}</c>,
/// <c>{"quantity":{"N":"500"}}</c>, <c>{"bookedOrderIds":{"SS":[...]}}</c>.
/// </summary>
public readonly struct StreamImage
{
    private readonly JsonElement? _element;

    public StreamImage(JsonElement? element)
    {
        _element = element is { ValueKind: JsonValueKind.Object } ? element : null;
    }

    /// <summary>True when the record carried this image (an INSERT has no OldImage).</summary>
    public bool Exists => _element != null;

    /// <summary>The string attribute, or empty when absent or not a string.</summary>
    public string String(string key)
    {
        if (_element is { } image && image.TryGetProperty(key, out var attr)
            && attr.TryGetProperty("S", out var s) && s.ValueKind == JsonValueKind.String)
        {
            return s.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>The number attribute (DynamoDB keeps numbers as strings), or null when absent or unreadable.</summary>
    public double? Number(string key)
    {
        if (_element is { } image && image.TryGetProperty(key, out var attr)
            && attr.TryGetProperty("N", out var n) && n.ValueKind == JsonValueKind.String
            && double.TryParse(n.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>The boolean attribute, or null when absent.</summary>
    public bool? Bool(string key)
    {
        if (_element is { } image && image.TryGetProperty(key, out var attr)
            && attr.TryGetProperty("BOOL", out var b)
            && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return b.GetBoolean();
        }

        return null;
    }

    /// <summary>Whether the attribute is present with any type.</summary>
    public bool Has(string key) => _element is { } image && image.TryGetProperty(key, out _);

    /// <summary>The attribute's value as text for a string or number attribute (null when absent).</summary>
    public string? Text(string key)
    {
        var s = String(key);
        if (s.Length > 0)
        {
            return s;
        }

        var n = Number(key);
        return n?.ToString(CultureInfo.InvariantCulture);
    }
}

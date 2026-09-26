using System.Text.RegularExpressions;
using Amazon.Lambda.APIGatewayEvents;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Per-install identity the clients send on sign-in (X-Device-Id, a UUID minted
/// once per install, and X-Device-Name, a short model description). Used to
/// notice sign-ins from a device the account has never used.
/// </summary>
public static partial class DeviceIdentity
{
    public const string IdHeader = "X-Device-Id";
    public const string NameHeader = "X-Device-Name";
    public const int MaxKnownDevices = 10;

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdPattern();

    public static string? ReadId(APIGatewayProxyRequest request)
    {
        var raw = ReadHeader(request, IdHeader)?.Trim();
        return raw != null && IdPattern().IsMatch(raw) ? raw : null;
    }

    /// <summary>
    /// The device name reaches the account holder's SMS, so it is reduced to a
    /// short allow-listed string (letters, digits, space, dot, underscore, hyphen,
    /// parentheses) that cannot carry links or a phishing sentence.
    /// </summary>
    public static string ReadName(APIGatewayProxyRequest request)
    {
        var raw = ReadHeader(request, NameHeader)?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return "a new device";
        }
        var cleaned = new string(raw
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')')
            .ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "a new device";
        }
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }

    /// <summary>Known devices plus this one, newest last, capped at <see cref="MaxKnownDevices"/>.</summary>
    public static List<string> Remember(IReadOnlyList<string> known, string deviceId)
    {
        var updated = known.Where(d => d != deviceId).ToList();
        updated.Add(deviceId);
        return updated.Count <= MaxKnownDevices ? updated : updated[^MaxKnownDevices..];
    }

    /// <summary>The SMS notice in the user's language (am, om, en).</summary>
    public static string NewDeviceSms(string deviceName, string? language) => language switch
    {
        "am" => $"ወረዳኮኔክት፡ በ{deviceName} ላይ አዲስ መግቢያ ተከናውኗል። እርስዎ ካልሆኑ የይለፍ ቃልዎን ይቀይሩ።",
        "om" => $"WeredaConnect: seensa haaraa {deviceName} irraa. Yoo isin hin taane jecha icciitii keessan jijjiiraa.",
        _ => $"WeredaConnect: new sign-in on {deviceName}. If this was not you, change your password."
    };

    private static string? ReadHeader(APIGatewayProxyRequest request, string name)
    {
        if (request.Headers == null)
        {
            return null;
        }
        foreach (var (key, value) in request.Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }
}

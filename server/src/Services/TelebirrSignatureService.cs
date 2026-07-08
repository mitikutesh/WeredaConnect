using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeredaConnect.Server.Services;

/// <summary>RSA signature padding mode. PSS matches live Telebirr / the SDK default.</summary>
public enum TelebirrSignaturePadding
{
    Pss,
    Pkcs1
}

/// <summary>
/// SHA256WithRSA signing/verification for Telebirr, matching the OgBek SDK and
/// the local sandbox simulator's canonicalization exactly.
///
/// PSS uses SHA-256 with salt length 32 and MGF1-SHA256 (Telebirr default);
/// PKCS1 is deterministic and used for cross-language fixture tests.
/// </summary>
public sealed class TelebirrSignatureService : ITelebirrSignatureService
{
    // Keys never included in the signed string (they carry the signature itself,
    // transport headers, or fields Telebirr appends after signing).
    private static readonly HashSet<string> ExcludedKeys = new(StringComparer.Ordinal)
    {
        "sign", "sign_type", "header", "refund_info", "openType", "raw_request"
    };

    private readonly string? _privateKeyPem;
    private readonly string? _publicKeyPem;
    private readonly TelebirrSignaturePadding _padding;

    public TelebirrSignatureService(
        string? merchantPrivateKeyPem,
        string? telebirrPublicKeyPem,
        TelebirrSignaturePadding padding = TelebirrSignaturePadding.Pss)
    {
        _privateKeyPem = merchantPrivateKeyPem;
        _publicKeyPem = telebirrPublicKeyPem;
        _padding = padding;
    }

    /// <summary>Parse "pss"/"pkcs1" (case-insensitive) into the enum; defaults to PSS.</summary>
    public static TelebirrSignaturePadding ParsePadding(string? value)
        => string.Equals(value, "pkcs1", StringComparison.OrdinalIgnoreCase)
            ? TelebirrSignaturePadding.Pkcs1
            : TelebirrSignaturePadding.Pss;

    public string BuildCanonicalString(IReadOnlyDictionary<string, object?> payload)
    {
        var flat = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Flatten(payload, flat);
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in flat)
        {
            if (!first)
            {
                sb.Append('&');
            }

            sb.Append(kv.Key).Append('=').Append(kv.Value);
            first = false;
        }

        return sb.ToString();
    }

    public string SignObject(IReadOnlyDictionary<string, object?> payload)
        => Sign(BuildCanonicalString(payload));

    public string Sign(string canonical)
    {
        if (string.IsNullOrEmpty(_privateKeyPem))
        {
            throw new InvalidOperationException(
                "TelebirrSignatureService: merchant private key is required to sign.");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
        var data = Encoding.UTF8.GetBytes(canonical);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, PaddingFor(_padding));
        return Convert.ToBase64String(sig);
    }

    public bool VerifyObject(IReadOnlyDictionary<string, object?> payload, string base64Signature)
        => Verify(BuildCanonicalString(payload), base64Signature);

    public bool Verify(string canonical, string base64Signature)
    {
        if (string.IsNullOrEmpty(_publicKeyPem))
        {
            throw new InvalidOperationException(
                "TelebirrSignatureService: Telebirr public key is required to verify.");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            var data = Encoding.UTF8.GetBytes(canonical);
            var sig = Convert.FromBase64String(base64Signature);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, PaddingFor(_padding));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static RSASignaturePadding PaddingFor(TelebirrSignaturePadding p)
        => p == TelebirrSignaturePadding.Pkcs1
            ? RSASignaturePadding.Pkcs1
            : RSASignaturePadding.Pss;

    /// <summary>
    /// Recursively flatten a payload into the signed key/value map. Nested objects
    /// (e.g. biz_content) merge their fields up one level, matching the simulator.
    /// </summary>
    private static void Flatten(IReadOnlyDictionary<string, object?> obj, IDictionary<string, string> outMap)
    {
        foreach (var (key, value) in obj)
        {
            Add(key, value, outMap);
        }
    }

    private static void Add(string key, object? value, IDictionary<string, string> outMap)
    {
        if (value is null)
        {
            return;
        }

        if (ExcludedKeys.Contains(key))
        {
            return;
        }

        switch (value)
        {
            case bool b:
                outMap[key] = b ? "1" : "0";
                break;
            case string s:
                outMap[key] = s;
                break;
            case IReadOnlyDictionary<string, object?> nested:
                foreach (var (k, v) in nested)
                {
                    Add(k, v, outMap);
                }

                break;
            case IDictionary<string, object?> nestedMut:
                foreach (var (k, v) in nestedMut)
                {
                    Add(k, v, outMap);
                }

                break;
            default:
                outMap[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                break;
        }
    }
}

namespace WeredaConnect.Server.Services;

/// <summary>
/// Telebirr request/webhook signing and verification (SHA256WithRSA).
/// Mirrors the canonicalization used by the OgBek/Telebirr SDK and the local
/// sandbox simulator (tools/telebirr-sandbox/telebirr-crypto.js) so the same
/// code interops with both the simulator and the live Telebirr sandbox.
/// AOT-safe: uses System.Security.Cryptography only, no reflection.
/// </summary>
public interface ITelebirrSignatureService
{
    /// <summary>
    /// Build the canonical string that gets signed/verified: recursively flatten
    /// top-level + nested objects (e.g. biz_content) into one map, drop excluded
    /// keys, drop nulls (keep empty strings), booleans → "1"/"0", sort keys
    /// ascending (ordinal), join as key=value&amp;key2=value2 (no URL-encoding).
    /// </summary>
    string BuildCanonicalString(IReadOnlyDictionary<string, object?> payload);

    /// <summary>Sign the canonical string of <paramref name="payload"/>; base64 output.</summary>
    string SignObject(IReadOnlyDictionary<string, object?> payload);

    /// <summary>Sign an already-built canonical string; base64 output.</summary>
    string Sign(string canonical);

    /// <summary>Verify a base64 signature over the canonical string of <paramref name="payload"/>.</summary>
    bool VerifyObject(IReadOnlyDictionary<string, object?> payload, string base64Signature);

    /// <summary>Verify a base64 signature over an already-built canonical string.</summary>
    bool Verify(string canonical, string base64Signature);
}

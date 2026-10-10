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

    /// <summary>
    /// ESC-45: the canonical string of an already-flat map (an inbound notification exactly as
    /// it was read): excluded keys dropped, keys sorted ordinally, joined as key=value&amp;... No
    /// flattening, so the string covers exactly the fields the caller will read.
    /// </summary>
    string BuildFlatCanonicalString(IReadOnlyDictionary<string, string> fields);

    /// <summary>
    /// ESC-45: verify a Telebirr notification over exactly <paramref name="fields"/>, with the
    /// configured padding first and then the other one (Ethio Telecom's samples sign with PSS, the
    /// deployed config said PKCS#1; both are checked against Telebirr's public key with SHA-256).
    /// <paramref name="matched"/> says which padding verified.
    /// </summary>
    bool VerifyFlat(IReadOnlyDictionary<string, string> fields, string base64Signature, out TelebirrSignaturePadding matched);
}

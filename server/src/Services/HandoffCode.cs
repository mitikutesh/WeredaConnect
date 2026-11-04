using System.Security.Cryptography;
using System.Text;

namespace WeredaConnect.Server.Services;

/// <summary>
/// The typed fallback for a QR handoff (usability audit T-1, delivery-verification-api.md 1.22.0):
/// nine digits a person types when the camera or the screen fails. The first eight are derived
/// from the delivery's QR nonce (the 32-byte secret the farmer's phone stores in the
/// delivery-qr-secrets table) and the delivery id; the ninth is a Damm check digit, so the app
/// catches every single wrong digit and every swap of two neighbours before it sends anything.
///
/// Derivation (mirrored by the mobile <c>HandoffCode</c>, byte for byte):
/// HMAC-SHA256 with the UTF-8 nonce string as key over <c>wc-handoff-code:v1:{deliveryId}</c>,
/// the first 8 bytes read big-endian modulo 10^8, left-padded to 8 digits, then the Damm digit.
/// One code serves both legs because one nonce does: the pickup and the drop-off each consume it
/// once.
/// </summary>
internal static class HandoffCode
{
    /// <summary>Digits in a code, the check digit included.</summary>
    public const int Length = 9;

    /// <summary>Wrong codes a leg takes before typed codes are locked for it.</summary>
    public const int MaxMisses = 5;

    private const string Context = "wc-handoff-code:v1:";

    /// <summary>The Damm quasigroup of order 10 (weakly totally anti-symmetric).</summary>
    private static readonly int[,] DammTable =
    {
        { 0, 3, 1, 7, 5, 9, 8, 6, 4, 2 },
        { 7, 0, 9, 2, 1, 5, 4, 8, 6, 3 },
        { 4, 2, 0, 6, 8, 7, 1, 3, 5, 9 },
        { 1, 7, 5, 0, 9, 8, 3, 4, 2, 6 },
        { 6, 1, 2, 3, 0, 4, 5, 9, 7, 8 },
        { 3, 6, 7, 4, 2, 0, 9, 5, 8, 1 },
        { 5, 8, 6, 9, 7, 2, 0, 1, 3, 4 },
        { 8, 9, 4, 5, 3, 6, 2, 0, 1, 7 },
        { 9, 4, 3, 8, 6, 1, 7, 2, 0, 5 },
        { 2, 5, 8, 1, 4, 3, 6, 7, 9, 0 },
    };

    /// <summary>The nine-digit code of <paramref name="nonce"/> for <paramref name="deliveryId"/>.</summary>
    public static string Derive(string nonce, string deliveryId)
    {
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(nonce),
            Encoding.UTF8.GetBytes(Context + deliveryId));
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = ((value * 256) + digest[i]) % 100_000_000;
        }

        var body = value.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return body + CheckDigit(body);
    }

    /// <summary>The Damm check digit of a string of ASCII digits.</summary>
    public static int CheckDigit(string digits)
    {
        var interim = 0;
        foreach (var c in digits)
        {
            interim = DammTable[interim, c - '0'];
        }

        return interim;
    }

    /// <summary>
    /// Nine ASCII digits whose Damm check passes. Anything else can never be a code, so it is
    /// refused as a validation error and never counted as a miss.
    /// </summary>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Length)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return CheckDigit(code) == 0;
    }

    /// <summary>
    /// The id of one typed-code attempt in the secret row: the operation id and the first 16 hex
    /// digits of SHA-256 of the code. A retry of the same operation with the same code is the same
    /// attempt (counted once); the same operation id with another code is a new attempt, so a
    /// client cannot reuse one id to guess for free (errors never store the idempotency key).
    /// </summary>
    public static string AttemptId(string operationId, string code) =>
        operationId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..16].ToLowerInvariant();

    /// <summary>Whether <paramref name="code"/> is the nonce's code, compared in fixed time.</summary>
    public static bool Matches(string code, string nonce, string deliveryId) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(code),
            Encoding.ASCII.GetBytes(Derive(nonce, deliveryId)));
}

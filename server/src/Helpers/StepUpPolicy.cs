using System.Globalization;
using Amazon.Lambda.APIGatewayEvents;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Step-up authentication for money-moving actions. A Cognito ID token carries
/// auth_time, the moment the user last typed a password; refreshed tokens keep
/// the original value. Actions at or above <see cref="ThresholdEtb"/> require
/// auth_time to be within <see cref="MaxAgeSeconds"/>, otherwise the client is
/// told to sign in again (AUTH_040) and retry.
/// Payments use <see cref="CheckPayment"/> instead: a recent password up to
/// 10,000 ETB, a recent Fayda login above it (AUTH_041).
/// Environment: STEP_UP_THRESHOLD_ETB (default 10000), STEP_UP_MAX_AGE_SECONDS (default 300).
/// </summary>
public static class StepUpPolicy
{
    public const string ErrorCode = "AUTH_040";
    public const string ErrorMessage = "Please sign in again to confirm this action.";

    /// <summary>A payment above the Fayda threshold needs a recent Fayda login.</summary>
    public const string FaydaErrorCode = "AUTH_041";
    public const string FaydaErrorMessage = "Confirm it's you with Fayda to pay more than 10,000 ETB.";

    internal const decimal DefaultThresholdEtb = 10_000m;
    internal const int DefaultMaxAgeSeconds = 300;

    public static decimal ThresholdEtb
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("STEP_UP_THRESHOLD_ETB");
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0
                ? v
                : DefaultThresholdEtb;
        }
    }

    public static int MaxAgeSeconds
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("STEP_UP_MAX_AGE_SECONDS");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v
                : DefaultMaxAgeSeconds;
        }
    }

    public static bool RequiresFreshAuth(decimal amountEtb) => amountEtb >= ThresholdEtb;

    /// <summary>
    /// True when the caller's token proves a recent password entry. A token
    /// without auth_time (an access token, or a malformed claim) is never fresh.
    /// </summary>
    public static bool IsFresh(APIGatewayProxyRequest request, DateTimeOffset? now = null)
    {
        var authTime = AuthHelper.GetAuthTime(request);
        if (authTime == null)
        {
            return false;
        }
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return current - authTime.Value <= MaxAgeSeconds;
    }

    /// <summary>
    /// Null when the action may proceed; otherwise the error message (with code
    /// prefix) to return. Amount below the threshold never needs step-up.
    /// </summary>
    public static string? Check(APIGatewayProxyRequest request, decimal amountEtb)
        => Check(AuthHelper.GetAuthTime(request), amountEtb);

    /// <summary>Same check from an already-extracted auth_time (null = never fresh).</summary>
    public static string? Check(long? authTime, decimal amountEtb, DateTimeOffset? now = null)
    {
        if (!RequiresFreshAuth(amountEtb))
        {
            return null;
        }
        if (authTime == null)
        {
            return $"{ErrorCode}: {ErrorMessage}";
        }
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return current - authTime.Value <= MaxAgeSeconds ? null : $"{ErrorCode}: {ErrorMessage}";
    }

    /// <summary>
    /// Step-up for a buyer's payment, in two tiers split at the NBE Fayda threshold
    /// (<see cref="KycPolicy.FaydaThresholdEtb"/>): up to it, a password entered within
    /// <see cref="MaxAgeSeconds"/> (auth_time, AUTH_040); above it, a Fayda login within
    /// the same window (<paramref name="faydaAuthenticatedAt"/>, written by
    /// /user/fayda-link, AUTH_041) and no password. Null when the payment may proceed.
    /// </summary>
    public static string? CheckPayment(
        long? authTime, string? faydaAuthenticatedAt, decimal amountEtb, DateTimeOffset? now = null)
    {
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (amountEtb > KycPolicy.FaydaThresholdEtb)
        {
            var faydaAt = ParseIsoSeconds(faydaAuthenticatedAt);
            return faydaAt != null && current - faydaAt.Value <= MaxAgeSeconds
                ? null
                : $"{FaydaErrorCode}: {FaydaErrorMessage}";
        }
        return authTime != null && current - authTime.Value <= MaxAgeSeconds
            ? null
            : $"{ErrorCode}: {ErrorMessage}";
    }

    /// <summary>Error code of a <see cref="CheckPayment"/> message (AUTH_040 or AUTH_041).</summary>
    public static string CodeOf(string message)
        => message.StartsWith(FaydaErrorCode, StringComparison.Ordinal) ? FaydaErrorCode : ErrorCode;

    private static long? ParseIsoSeconds(string? iso)
        => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeSeconds()
            : null;
}

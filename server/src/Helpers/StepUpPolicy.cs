using System.Globalization;
using Amazon.Lambda.APIGatewayEvents;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Step-up authentication for money-moving actions. A Cognito ID token carries
/// auth_time, the moment the user last typed a password; refreshed tokens keep
/// the original value. Actions at or above <see cref="ThresholdEtb"/> require
/// auth_time to be within <see cref="MaxAgeSeconds"/>, otherwise the client is
/// told to sign in again (AUTH_040) and retry.
/// Environment: STEP_UP_THRESHOLD_ETB (default 10000), STEP_UP_MAX_AGE_SECONDS (default 300).
/// </summary>
public static class StepUpPolicy
{
    public const string ErrorCode = "AUTH_040";
    public const string ErrorMessage = "Please sign in again to confirm this action.";

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
}

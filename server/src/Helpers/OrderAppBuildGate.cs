using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-25 (sync-protocol.md 1.41.0, spec K1): the minimum app build for <c>order</c> operations on
/// <c>/sync</c>. An app below it cannot show the commitment statuses, so it may not act on orders at
/// all. The minimum comes from <see cref="EnvVar"/>, set per environment in the infra config; 0 or
/// absent turns the gate off. It does not depend on <c>COMMITMENTS_ENABLED</c>: the gate goes live
/// before the flag does.
/// </summary>
public static class OrderAppBuildGate
{
    public const string EnvVar = "MIN_ORDER_APP_BUILD";

    private const string TooOldText = "This version of the app is too old for orders. Update WeredaConnect to continue.";

    /// <summary>To an app that sends <c>X-App-Build</c> (build 2 or later, so it has the 1.17.0 client).</summary>
    public const string AppUpdateRequiredMessage = "APP_UPDATE_REQUIRED: " + TooOldText;

    /// <summary>
    /// To an app that sends no readable <c>X-App-Build</c> (build 1 or older): it may predate the 1.17.0
    /// client, which retries an unknown code forever (FUP-75), while <c>ORDER_VALIDATION</c> is permanent
    /// on every client since 1.10.0.
    /// </summary>
    public const string NoBuildMessage = "ORDER_VALIDATION: " + TooOldText;

    /// <summary>The minimum from <see cref="EnvVar"/>.</summary>
    public static int FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// A positive whole number is the minimum; anything else (absent, empty, 0, negative, unreadable)
    /// turns the gate off. The infra tests pin the value of every environment.
    /// </summary>
    public static int Parse(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) && minimum > 0
            ? minimum
            : 0;

    /// <summary>
    /// The refusal for an order operation from <paramref name="clientBuild"/> (null: no readable
    /// <c>X-App-Build</c>, <see cref="ClientAppBuild.Read"/>), or null when it may go on.
    /// </summary>
    public static string? Refusal(int minimum, int? clientBuild)
    {
        if (minimum <= 0)
        {
            return null;
        }

        if (clientBuild == null)
        {
            return NoBuildMessage;
        }

        return clientBuild < minimum ? AppUpdateRequiredMessage : null;
    }
}

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Runtime guard for the development shortcuts (fixed OTP code, fixed Fayda code).
/// The infra guard refuses the flags in a production deployment; this is the
/// second lock: even with the flag set, a Lambda whose function name carries the
/// production prefix (wereda-connect-prod-*) or an ENVIRONMENT of "prod" never
/// honours a bypass.
/// </summary>
public static class DevBypass
{
    public static bool IsEnabled(string flag, Func<string, string?>? env = null)
    {
        var read = env ?? Environment.GetEnvironmentVariable;
        if (!string.Equals(read(flag), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var environment = read("ENVIRONMENT");
        if (string.Equals(environment, "prod", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var functionName = read("AWS_LAMBDA_FUNCTION_NAME") ?? "";
        return !functionName.Contains("-prod-", StringComparison.OrdinalIgnoreCase)
               && !functionName.EndsWith("-prod", StringComparison.OrdinalIgnoreCase);
    }
}

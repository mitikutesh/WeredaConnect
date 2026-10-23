using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-61: the app build the mobile and web clients send on every request in <see cref="Header"/>
/// (the <c>+N</c> of <c>version:</c> in <c>mobile/pubspec.yaml</c>). Builds before 2 send nothing, so
/// a missing or unreadable value means an old app.
/// </summary>
public static class ClientAppBuild
{
    public const string Header = "X-App-Build";

    /// <summary>The caller's build, or null when the header is missing or not a positive whole number.</summary>
    public static int? Read(APIGatewayProxyRequest request)
    {
        if (request.Headers == null)
        {
            return null;
        }

        foreach (var (key, value) in request.Headers)
        {
            if (string.Equals(key, Header, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var build)
                       && build > 0
                    ? build
                    : null;
            }
        }

        return null;
    }
}

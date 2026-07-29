using System.Net;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Loads Telebirr configuration and builds the real gateway. Config source:
///   - TELEBIRR_LOCAL_CREDENTIALS_FILE : path to the simulator's
///     credentials.local.json (local dev), OR
///   - TELEBIRR_SECRET_ARN : Secrets Manager ARN (prod) or SSM Parameter Store
///     name (dev/staging) holding the same JSON shape.
/// notify_url and allowed source IPs come from env (deployment-specific).
/// </summary>
public static class TelebirrGatewayFactory
{
    public static TelebirrConfig LoadConfigOrThrow()
    {
        var notifyUrl = Environment.GetEnvironmentVariable("TELEBIRR_NOTIFY_URL");
        var allowedIps = Environment.GetEnvironmentVariable("TELEBIRR_ALLOWED_IPS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Optional Ethiopian egress proxy (env overrides the secret's httpProxy).
        var httpProxy = Environment.GetEnvironmentVariable("TELEBIRR_HTTP_PROXY");

        var localFile = Environment.GetEnvironmentVariable("TELEBIRR_LOCAL_CREDENTIALS_FILE");
        if (!string.IsNullOrEmpty(localFile))
        {
            if (!File.Exists(localFile))
            {
                throw new InvalidOperationException(
                    $"TELEBIRR_LOCAL_CREDENTIALS_FILE points at a missing file: {localFile}");
            }

            return TelebirrConfig.FromJson(File.ReadAllText(localFile), notifyUrl, allowedIps, httpProxy);
        }

        var secretRef = Environment.GetEnvironmentVariable("TELEBIRR_SECRET_ARN")
            ?? throw new InvalidOperationException(
                "No Telebirr config source: set TELEBIRR_LOCAL_CREDENTIALS_FILE (local) or TELEBIRR_SECRET_ARN (AWS).");

        var json = AwsParameterResolver.GetSecretOrParameterValue(secretRef);
        return TelebirrConfig.FromJson(json, notifyUrl, allowedIps, httpProxy);
    }

    public static TelebirrSandboxGateway CreateGateway(TelebirrConfig config)
    {
        var http = new HttpClient(BuildHandler(config.HttpProxy))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var signer = new TelebirrSignatureService(
            config.MerchantPrivateKeyPem, config.TelebirrPublicKeyPem, config.Padding);
        return new TelebirrSandboxGateway(http, signer, config);
    }

    /// <summary>
    /// Build the HTTP message handler, routing through an Ethiopian egress proxy
    /// when configured (Telebirr's sandbox only accepts Ethiopian source IPs).
    /// AOT-safe: SocketsHttpHandler + WebProxy, no reflection.
    /// </summary>
    private static SocketsHttpHandler BuildHandler(string? httpProxy)
    {
        var handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(httpProxy))
        {
            var proxyUri = new Uri(httpProxy);
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                var parts = proxyUri.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
            }

            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }
}

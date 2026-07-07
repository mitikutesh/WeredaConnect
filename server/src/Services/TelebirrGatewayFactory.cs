using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Loads Telebirr configuration and builds the real gateway. Config source:
///   - TELEBIRR_LOCAL_CREDENTIALS_FILE : path to the simulator's
///     credentials.local.json (local dev), OR
///   - TELEBIRR_SECRET_ARN : AWS Secrets Manager secret (same JSON shape).
/// notify_url and allowed source IPs come from env (deployment-specific).
/// </summary>
public static class TelebirrGatewayFactory
{
    public static TelebirrConfig LoadConfigOrThrow()
    {
        var notifyUrl = Environment.GetEnvironmentVariable("TELEBIRR_NOTIFY_URL");
        var allowedIps = Environment.GetEnvironmentVariable("TELEBIRR_ALLOWED_IPS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var localFile = Environment.GetEnvironmentVariable("TELEBIRR_LOCAL_CREDENTIALS_FILE");
        if (!string.IsNullOrEmpty(localFile))
        {
            if (!File.Exists(localFile))
            {
                throw new InvalidOperationException(
                    $"TELEBIRR_LOCAL_CREDENTIALS_FILE points at a missing file: {localFile}");
            }

            return TelebirrConfig.FromJson(File.ReadAllText(localFile), notifyUrl, allowedIps);
        }

        var secretArn = Environment.GetEnvironmentVariable("TELEBIRR_SECRET_ARN")
            ?? throw new InvalidOperationException(
                "No Telebirr config source: set TELEBIRR_LOCAL_CREDENTIALS_FILE (local) or TELEBIRR_SECRET_ARN (AWS).");

        using var client = new AmazonSecretsManagerClient(
            new AmazonSecretsManagerConfig { Timeout = TimeSpan.FromSeconds(10) });
        var secret = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn })
            .GetAwaiter().GetResult();
        var json = secret.SecretString
            ?? throw new InvalidOperationException("Telebirr secret is empty.");
        return TelebirrConfig.FromJson(json, notifyUrl, allowedIps);
    }

    public static TelebirrSandboxGateway CreateGateway(TelebirrConfig config)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var signer = new TelebirrSignatureService(
            config.MerchantPrivateKeyPem, config.TelebirrPublicKeyPem, config.Padding);
        return new TelebirrSandboxGateway(http, signer, config);
    }
}

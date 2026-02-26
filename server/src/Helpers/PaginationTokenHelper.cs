using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// HMAC-signed pagination tokens for DynamoDB LastEvaluatedKey.
/// Prevents clients from crafting arbitrary DynamoDB scan positions.
/// Format: base64payload.hmacSignature
/// </summary>
public static class PaginationTokenHelper
{
    private static byte[]? _signingKey;
    private static readonly object _lock = new();

    private static byte[] GetSigningKey()
    {
        if (_signingKey != null)
        {
            return _signingKey;
        }

        lock (_lock)
        {
            if (_signingKey != null)
            {
                return _signingKey;
            }

            // Try direct env var first (local dev / testing backward compat)
            var directSecret = Environment.GetEnvironmentVariable("PAGINATION_SECRET");
            if (!string.IsNullOrEmpty(directSecret))
            {
                _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(directSecret));
                return _signingKey;
            }

            // Load from Secrets Manager at runtime
            var secretArn = Environment.GetEnvironmentVariable("PAGINATION_SECRET_ARN")
                ?? throw new InvalidOperationException(
                    "PAGINATION_SECRET_ARN environment variable is required. " +
                    "Set via CDK infrastructure (Secrets Manager ARN).");

            using var client = new AmazonSecretsManagerClient();
            var response = client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretArn,
            }).GetAwaiter().GetResult();

            _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(response.SecretString));
            return _signingKey;
        }
    }

    /// <summary>
    /// Encode DynamoDB LastEvaluatedKey as an HMAC-signed base64 nextToken.
    /// </summary>
    public static string EncodeNextToken(Dictionary<string, AttributeValue> lastEvaluatedKey)
    {
        var tokenData = new Dictionary<string, string>();

        foreach (var kvp in lastEvaluatedKey)
        {
            if (kvp.Value.S != null)
            {
                tokenData[kvp.Key] = kvp.Value.S;
            }
            else if (kvp.Value.N != null)
            {
                tokenData[kvp.Key] = kvp.Value.N;
            }
        }

        var json = JsonSerializer.Serialize(tokenData, AppJsonContext.Default.DictionaryStringString);
        var bytes = Encoding.UTF8.GetBytes(json);
        var payload = Convert.ToBase64String(bytes);
        var signature = ComputeHmac(payload);

        return $"{payload}.{signature}";
    }

    /// <summary>
    /// Decode HMAC-signed nextToken back to DynamoDB LastEvaluatedKey.
    /// Returns null if the token signature is invalid or the token is malformed.
    /// </summary>
    public static Dictionary<string, AttributeValue>? DecodeNextToken(string nextToken)
    {
        var parts = nextToken.Split('.', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var payload = parts[0];
        var signature = parts[1];

        // Verify HMAC signature
        var expectedSignature = ComputeHmac(payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature)))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);

            var tokenData = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);
            if (tokenData == null)
            {
                return null;
            }

            var result = new Dictionary<string, AttributeValue>();
            foreach (var kvp in tokenData)
            {
                result[kvp.Key] = new AttributeValue { S = kvp.Value };
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHmac(string payload)
    {
        using var hmac = new HMACSHA256(GetSigningKey());
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}

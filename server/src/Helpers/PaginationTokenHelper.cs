using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// HMAC-signed pagination tokens for DynamoDB LastEvaluatedKey.
/// Prevents clients from crafting arbitrary DynamoDB scan positions.
/// Format: base64payload.hmacSignature
/// </summary>
public static class PaginationTokenHelper
{
    // TODO: Move to SSM Parameter Store for production. MVP uses a derived constant.
    private static readonly byte[] SigningKey = GetSigningKey();

    private static byte[] GetSigningKey()
    {
        var secret = Environment.GetEnvironmentVariable("PAGINATION_SECRET")
            ?? "WeredaConnect-Pagination-Default-Key-v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
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
                tokenData[kvp.Key] = kvp.Value.S;
            else if (kvp.Value.N != null)
                tokenData[kvp.Key] = kvp.Value.N;
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
        using var hmac = new HMACSHA256(SigningKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}

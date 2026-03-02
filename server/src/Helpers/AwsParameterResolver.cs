using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Resolves AWS Secrets Manager secrets and SSM Parameter Store values at Lambda startup.
/// Uses synchronous GetAwaiter().GetResult() — intended for constructor-time resolution only.
/// Follows the same pattern as PaginationTokenHelper for AOT compatibility.
/// </summary>
internal static class AwsParameterResolver
{
    /// <summary>
    /// Resolve a Secrets Manager ARN to its plaintext secret value.
    /// </summary>
    public static string GetSecretValue(string secretArn)
    {
        using var client = new AmazonSecretsManagerClient();
        var response = client.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = secretArn,
        }).GetAwaiter().GetResult();

        return response.SecretString
               ?? throw new InvalidOperationException($"Secret {secretArn} has no string value.");
    }

    /// <summary>
    /// Resolve an SSM Parameter Store parameter name to its value.
    /// </summary>
    public static string GetSsmParameterValue(string parameterName)
    {
        using var client = new AmazonSimpleSystemsManagementClient();
        var response = client.GetParameterAsync(new GetParameterRequest
        {
            Name = parameterName,
            WithDecryption = true,
        }).GetAwaiter().GetResult();

        return response.Parameter.Value
               ?? throw new InvalidOperationException($"SSM parameter {parameterName} has no value.");
    }
}

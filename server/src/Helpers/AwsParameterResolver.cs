using System.Collections.Concurrent;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Resolves AWS Secrets Manager secrets and SSM Parameter Store values at Lambda startup.
/// Uses synchronous GetAwaiter().GetResult() — intended for constructor-time resolution only.
/// Follows the same pattern as PaginationTokenHelper for AOT compatibility.
/// Values are cached for the lifetime of the Lambda container (one AWS call per reference).
/// </summary>
internal static class AwsParameterResolver
{
    // The shortest consuming Lambda timeout is 10s; the SDK default (100s)
    // would outlive it. 5s leaves headroom to surface a clean error instead
    // of a Lambda timeout.
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    // Check-then-set (not GetOrAdd): two concurrent first-resolves may both
    // fetch, which is benign — same value, idempotent write.
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    // Test seams — production code never assigns these. NOTE: plain static
    // fields, safe only while a single test class exercises the resolver;
    // other suites never reach it because TestEnvironmentSetup sets
    // PAGINATION_SECRET, which short-circuits PaginationTokenHelper.
    internal static Func<string, string>? SecretsFetchOverride;
    internal static Func<string, string>? SsmFetchOverride;

    internal static void ResetForTests()
    {
        Cache.Clear();
        SecretsFetchOverride = null;
        SsmFetchOverride = null;
    }

    /// <summary>
    /// Resolve a secret reference from whichever backend it points at: a Secrets
    /// Manager ARN (prod) or an SSM Parameter Store name such as
    /// /wereda-connect-dev/pagination-secret (dev/staging). The same *_ARN env
    /// vars carry either shape — infra decides per environment, the server just
    /// routes on the value.
    /// </summary>
    public static string GetSecretOrParameterValue(string reference)
    {
        if (Cache.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        var value = FetchSecretOrParameterValue(reference);
        Cache[reference] = value;
        return value;
    }

    /// <summary>
    /// Same routing as <see cref="GetSecretOrParameterValue"/>, without the cache:
    /// one AWS call each time. For a value that may still be a seeded PLACEHOLDER
    /// when first read (the Fayda client key); the caller caches it once usable.
    /// </summary>
    public static string FetchSecretOrParameterValue(string reference)
    {
        if (reference.Contains(":secretsmanager:", StringComparison.Ordinal))
        {
            return (SecretsFetchOverride ?? GetSecretValue)(reference);
        }
        if (reference.StartsWith("arn:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported secret reference '{reference}': expected a Secrets Manager ARN "
                + "or an SSM parameter name.");
        }
        return (SsmFetchOverride ?? GetSsmParameterValue)(reference);
    }

    /// <summary>
    /// Resolve a Secrets Manager ARN to its plaintext secret value.
    /// </summary>
    public static string GetSecretValue(string secretArn)
    {
        using var client = new AmazonSecretsManagerClient(
            new AmazonSecretsManagerConfig { Timeout = ClientTimeout });
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
        using var client = new AmazonSimpleSystemsManagementClient(
            new AmazonSimpleSystemsManagementConfig { Timeout = ClientTimeout });
        var response = client.GetParameterAsync(new GetParameterRequest
        {
            Name = parameterName,
            WithDecryption = true,
        }).GetAwaiter().GetResult();

        return response.Parameter.Value
               ?? throw new InvalidOperationException($"SSM parameter {parameterName} has no value.");
    }
}

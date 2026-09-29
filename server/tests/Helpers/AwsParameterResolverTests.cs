using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// Tests the backend routing and container-lifetime caching of
/// AwsParameterResolver.GetSecretOrParameterValue. Uses the internal fetch
/// overrides — no AWS calls. All tests live in this single class so xUnit's
/// sequential-within-class execution protects the shared static state.
/// </summary>
public sealed class AwsParameterResolverTests : IDisposable
{
    private const string SecretArn =
        "arn:aws:secretsmanager:eu-north-1:123456789012:secret:/wereda-connect-prod/pagination-secret-AbC123";

    private const string ParameterName = "/wereda-connect-dev/pagination-secret";

    public AwsParameterResolverTests()
    {
        AwsParameterResolver.ResetForTests();
    }

    public void Dispose()
    {
        AwsParameterResolver.ResetForTests();
    }

    [Fact]
    public void SecretsManagerArn_RoutesToSecretsManager()
    {
        var ssmCalls = 0;
        AwsParameterResolver.SecretsFetchOverride = reference =>
        {
            Assert.Equal(SecretArn, reference);
            return "secret-value";
        };
        AwsParameterResolver.SsmFetchOverride = _ => { ssmCalls++; return "wrong"; };

        var value = AwsParameterResolver.GetSecretOrParameterValue(SecretArn);

        Assert.Equal("secret-value", value);
        Assert.Equal(0, ssmCalls);
    }

    [Fact]
    public void ParameterName_RoutesToSsm()
    {
        var secretsCalls = 0;
        AwsParameterResolver.SecretsFetchOverride = _ => { secretsCalls++; return "wrong"; };
        AwsParameterResolver.SsmFetchOverride = reference =>
        {
            Assert.Equal(ParameterName, reference);
            return "param-value";
        };

        var value = AwsParameterResolver.GetSecretOrParameterValue(ParameterName);

        Assert.Equal("param-value", value);
        Assert.Equal(0, secretsCalls);
    }

    [Fact]
    public void NonSecretsManagerArn_ThrowsDescriptiveError()
    {
        var ssmParameterArn =
            "arn:aws:ssm:eu-north-1:123456789012:parameter/wereda-connect-dev/pagination-secret";

        var ex = Assert.Throws<InvalidOperationException>(
            () => AwsParameterResolver.GetSecretOrParameterValue(ssmParameterArn));

        Assert.Contains("Unsupported secret reference", ex.Message);
        Assert.Contains(ssmParameterArn, ex.Message);
    }

    [Fact]
    public void SecondCall_IsServedFromCache()
    {
        var fetches = 0;
        AwsParameterResolver.SsmFetchOverride = _ => { fetches++; return "param-value"; };

        var first = AwsParameterResolver.GetSecretOrParameterValue(ParameterName);
        var second = AwsParameterResolver.GetSecretOrParameterValue(ParameterName);

        Assert.Equal("param-value", first);
        Assert.Equal("param-value", second);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public void DistinctReferences_AreCachedIndependently()
    {
        AwsParameterResolver.SecretsFetchOverride = _ => "from-secrets";
        AwsParameterResolver.SsmFetchOverride = _ => "from-ssm";

        Assert.Equal("from-secrets", AwsParameterResolver.GetSecretOrParameterValue(SecretArn));
        Assert.Equal("from-ssm", AwsParameterResolver.GetSecretOrParameterValue(ParameterName));
    }

    [Fact]
    public void FetchFailure_Propagates_AndDoesNotPoisonTheCache()
    {
        AwsParameterResolver.SsmFetchOverride =
            _ => throw new InvalidOperationException("transient failure");

        Assert.Throws<InvalidOperationException>(
            () => AwsParameterResolver.GetSecretOrParameterValue(ParameterName));

        // A later successful fetch must not be masked by a cached failure.
        AwsParameterResolver.SsmFetchOverride = _ => "recovered";
        Assert.Equal("recovered", AwsParameterResolver.GetSecretOrParameterValue(ParameterName));
    }

    [Fact]
    public void FetchSecretOrParameterValue_RoutesTheSameButNeverCaches()
    {
        var values = new Queue<string>(["PLACEHOLDER", "real-key"]);
        AwsParameterResolver.SsmFetchOverride = _ => values.Dequeue();
        AwsParameterResolver.SecretsFetchOverride = _ => "from-secrets-manager";

        Assert.Equal("PLACEHOLDER", AwsParameterResolver.FetchSecretOrParameterValue(ParameterName));
        Assert.Equal("real-key", AwsParameterResolver.FetchSecretOrParameterValue(ParameterName));
        Assert.Equal("from-secrets-manager", AwsParameterResolver.FetchSecretOrParameterValue(SecretArn));
        Assert.Throws<InvalidOperationException>(() =>
            AwsParameterResolver.FetchSecretOrParameterValue("arn:aws:ssm:eu-north-1:123456789012:parameter/x"));
    }
}

using FluentAssertions;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>Tests that build the Lambdas through their runtime constructors set process-wide variables, so they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LambdaConstructorEnvironmentCollection
{
    public const string Name = "LambdaConstructorEnvironment";
}

/// <summary>
/// FRD-13: the runtime constructors (the ones the Lambdas run) wire the payout delay. The unit tests build the
/// functions through their test constructors and set <c>PayoutDelay</c> themselves, so without this a Lambda that
/// stopped wiring it would stamp no <c>releaseNotBefore</c> and every other test would still pass. Only the
/// environment is set: the constructors make no AWS call (dummy credentials keep the SDK from probing for real ones).
/// </summary>
[Collection(LambdaConstructorEnvironmentCollection.Name)]
public sealed class PayoutDelayWiringTests : IDisposable
{
    private static readonly Dictionary<string, string> Environment_ = new()
    {
        { "AWS_REGION", "eu-north-1" },
        { "AWS_ACCESS_KEY_ID", "test-access-key" },
        { "AWS_SECRET_ACCESS_KEY", "test-secret-key" },
        { "SYNC_METADATA_TABLE", "sync-metadata-test" },
        { "CROPS_TABLE", "crops-test" },
        { "TRIPS_TABLE", "trips-test" },
        { "DELIVERIES_TABLE", "deliveries-test" },
        { "RATE_LIMITS_TABLE", "rate-limits-test" },
        { "USERS_TABLE", "users-test" },
        { "ORDERS_TABLE", "orders-test" },
        { "ESCROWS_TABLE", "escrows-test" },
        { "ESCROW_AUDIT_TABLE", "escrow-audit-test" },
        { "ESCROW_IDEMPOTENCY_TABLE", "escrow-idempotency-test" },
        { "FRAUD_SIGNALS_TABLE", "fraud-signals-test" },
        { "TELEBIRR_MOCK", "true" }
    };

    private readonly Dictionary<string, string?> _saved = new();

    public PayoutDelayWiringTests()
    {
        foreach (var (name, value) in Environment_)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void SyncLambda_WiresThePayoutDelay()
    {
        new SyncFunction().PayoutDelay.Should().NotBeNull();
    }

    [Fact]
    public void EscrowCronLambda_WiresThePayoutDelay()
    {
        new EscrowCronFunction().PayoutDelay.Should().NotBeNull();
    }
}

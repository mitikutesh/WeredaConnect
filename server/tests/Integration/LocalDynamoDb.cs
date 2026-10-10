using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// Where the real-DynamoDB tests run. CI's "Integration Tests (LocalStack)" job sets
/// <c>AWS_ENDPOINT_URL</c> to its LocalStack service and <c>REQUIRE_LOCALSTACK=true</c>; locally,
/// start LocalStack (<c>docker run --rm -p 4566:4566 -e SERVICES=dynamodb localstack/localstack:3</c>)
/// or DynamoDB Local and set <c>AWS_ENDPOINT_URL</c>. Without an endpoint the tests are skipped
/// (the unit and coverage jobs), unless <c>REQUIRE_LOCALSTACK</c> is set: then they run and fail,
/// so the LocalStack job can never pass by skipping everything.
/// </summary>
internal static class LocalDynamoDbEnvironment
{
    public static string? Endpoint => Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") is { Length: > 0 } url ? url : null;

    public static bool Required => string.Equals(
        Environment.GetEnvironmentVariable("REQUIRE_LOCALSTACK"), "true", StringComparison.OrdinalIgnoreCase);

    public const string SkipReason =
        "Needs DynamoDB: set AWS_ENDPOINT_URL to LocalStack or DynamoDB Local (CI sets it in the LocalStack job).";

    public static string? Skip => Endpoint == null && !Required ? SkipReason : null;
}

/// <summary>A fact that runs against real DynamoDB (see <see cref="LocalDynamoDbEnvironment"/>).</summary>
public sealed class LocalDynamoDbFactAttribute : FactAttribute
{
    public LocalDynamoDbFactAttribute()
    {
        Skip = LocalDynamoDbEnvironment.Skip;
    }
}

/// <summary>A theory that runs against real DynamoDB (see <see cref="LocalDynamoDbEnvironment"/>).</summary>
public sealed class LocalDynamoDbTheoryAttribute : TheoryAttribute
{
    public LocalDynamoDbTheoryAttribute()
    {
        Skip = LocalDynamoDbEnvironment.Skip;
    }
}

[CollectionDefinition(Name)]
public sealed class LocalDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "LocalDynamoDb";
}

/// <summary>
/// One set of tables for the collection, with the deployed key schemas and indexes
/// (<c>infra/lib/stacks/database-stack.ts</c>), uniquely named per run and deleted afterwards.
/// </summary>
public sealed class LocalDynamoDbFixture : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _created = new();

    public IAmazonDynamoDB Client { get; private set; } = null!;
    public string Escrows => $"escrows-{_suffix}";
    public string EscrowAudit => $"escrow-audit-{_suffix}";
    public string Idempotency => $"escrow-idempotency-{_suffix}";
    public string Orders => $"orders-{_suffix}";
    public string Deliveries => $"deliveries-{_suffix}";
    public string TransactionAudit => $"transaction-audit-{_suffix}";
    public string Users => $"users-{_suffix}";
    public string Crops => $"crops-{_suffix}";
    public string Trips => $"trips-{_suffix}";

    public async Task InitializeAsync()
    {
        var endpoint = LocalDynamoDbEnvironment.Endpoint;
        if (endpoint == null)
        {
            if (LocalDynamoDbEnvironment.Required)
            {
                throw new InvalidOperationException("REQUIRE_LOCALSTACK is set but AWS_ENDPOINT_URL is not.");
            }

            return; // every test in the collection is skipped
        }

        Client = new AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), new AmazonDynamoDBConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1"
        });

        await CreateAsync(Escrows, ("escrowId", "createdAt"),
            ("DeliveryIndex", "deliveryId", "createdAt"),
            ("StatusExpiresIndex", "status", "expiresAt"),
            ("OrderIndex", "orderId", "createdAt"),
            ("ManualPayoutIndex", "manualPayout", "manualPayoutQueuedAt"));
        await CreateAsync(EscrowAudit, ("escrowId", "timestamp"));
        await CreateAsync(Idempotency, ("idempotencyKey", null));
        await CreateAsync(Orders, ("orderId", "createdAt"), ("AdminQueueIndex", "adminQueue", "adminQueuedAt"));
        await CreateAsync(Deliveries, ("deliveryId", "tripId"),
            ("ListingIndex", "listingId", "createdAt"),
            ("ParticipantIndex", "participantId", "createdAt"),
            ("FarmerIdCreatedAtIndex", "farmerId", "createdAt"),
            ("TruckerIdCreatedAtIndex", "truckerId", "createdAt"));
        await CreateAsync(TransactionAudit, ("transactionId", "timestamp"), ("EntityIndex", "entityId", "timestamp"));
        await CreateAsync(Users, ("userId", null));
        await CreateAsync(Crops, ("cropId", "createdAt"));
        await CreateAsync(Trips, ("tripId", "createdAt"));
    }

    public async Task DisposeAsync()
    {
        foreach (var table in _created)
        {
            try
            {
                await Client.DeleteTableAsync(table);
            }
            catch (AmazonServiceException)
            {
                // Best-effort cleanup of a throwaway table.
            }
        }

        Client?.Dispose();
    }

    private async Task CreateAsync(
        string table, (string Hash, string? Range) key, params (string Name, string Hash, string Range)[] indexes)
    {
        var attributes = new Dictionary<string, ScalarAttributeType>(StringComparer.Ordinal) { [key.Hash] = ScalarAttributeType.S };
        var schema = new List<KeySchemaElement> { new(key.Hash, KeyType.HASH) };
        if (key.Range != null)
        {
            attributes[key.Range] = ScalarAttributeType.S;
            schema.Add(new KeySchemaElement(key.Range, KeyType.RANGE));
        }

        var request = new CreateTableRequest
        {
            TableName = table,
            KeySchema = schema,
            BillingMode = BillingMode.PAY_PER_REQUEST
        };
        if (indexes.Length > 0)
        {
            request.GlobalSecondaryIndexes = indexes.Select(i =>
            {
                attributes[i.Hash] = ScalarAttributeType.S;
                attributes[i.Range] = ScalarAttributeType.S;
                return new GlobalSecondaryIndex
                {
                    IndexName = i.Name,
                    KeySchema = new List<KeySchemaElement> { new(i.Hash, KeyType.HASH), new(i.Range, KeyType.RANGE) },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                };
            }).ToList();
        }

        request.AttributeDefinitions = attributes.Select(a => new AttributeDefinition(a.Key, a.Value)).ToList();
        await Client.CreateTableAsync(request);
        _created.Add(table);

        for (var i = 0; i < 50; i++)
        {
            var described = await Client.DescribeTableAsync(table);
            if (described.Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}

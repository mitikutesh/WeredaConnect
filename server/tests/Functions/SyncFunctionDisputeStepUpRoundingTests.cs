using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>Tests that set <c>STEP_UP_THRESHOLD_ETB</c>, a process-wide variable, run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StepUpThresholdEnvironmentCollection
{
    public const string Name = "StepUpThresholdEnvironment";
}

/// <summary>
/// FUP-26: the delivery dispute step-up rounds the order total to the cent away from zero, as the
/// escrow amount is rounded, so a sync dispute and <c>POST /escrow/dispute</c> decide alike. Only a
/// threshold off the default tells the rounding modes apart (9,999.995 gives 10,000.00 either way).
/// </summary>
[Collection(StepUpThresholdEnvironmentCollection.Name)]
public sealed class SyncFunctionDisputeStepUpRoundingTests : IDisposable
{
    private const string OrderId = "00000000-0000-0000-0000-000000000006";
    private const string BuyerId = "00000000-0000-0000-0000-000000000003";
    private const string DeliveryId = "00000000-0000-0000-0000-000000000010";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string ThresholdVariable = "STEP_UP_THRESHOLD_ETB";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string? _savedThreshold = Environment.GetEnvironmentVariable(ThresholdVariable);
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    public SyncFunctionDisputeStepUpRoundingTests()
    {
        Environment.SetEnvironmentVariable(ThresholdVariable, "10000.01");
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { DeliveredDelivery() } });
        _function = new SyncFunction(_syncMeta, Substitute.For<ICropRepository>(), _dynamoDb,
            "test-trips-table", DeliveriesTable, userRepo: Substitute.For<IUserRepository>(), orderRepo: _orders);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(ThresholdVariable, _savedThreshold);

    [Fact]
    public async Task DeliveryDispute_HalfCentOverTheThreshold_RoundsUp_AndRequiresStepUp()
    {
        // 10,000.005 is 10,000.01 away from zero (the escrow amount); to even it would be 10,000.00.
        SetupOrderTotal("10000.005");

        var result = await DisputeWithStaleAuthAsync();

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("AUTH_040: ");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task DeliveryDispute_JustUnderTheThreshold_NeedsNoStepUp()
    {
        // Proves the threshold above is in force: 10,000.004 is 10,000.00, under 10,000.01. With no
        // escrow service wired the freeze then asks for a retry.
        SetupOrderTotal("10000.004");

        var result = await DisputeWithStaleAuthAsync();

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_DISPUTE_RETRY: ");
    }

    private void SetupOrderTotal(string total) => _orders.GetByIdAsync(OrderId).Returns(new Dictionary<string, object>
    {
        { "orderId", OrderId }, { "buyerId", BuyerId }, { "orderTotal", total }, { "status", "delivered" }, { "version", "1" }
    });

    private static Dictionary<string, AttributeValue> DeliveredDelivery() => new()
    {
        { "deliveryId", new AttributeValue { S = DeliveryId } },
        { "tripId", new AttributeValue { S = "00000000-0000-0000-0000-000000000005" } },
        { "orderId", new AttributeValue { S = OrderId } },
        { "farmerId", new AttributeValue { S = "00000000-0000-0000-0000-000000000002" } },
        { "truckerId", new AttributeValue { S = "00000000-0000-0000-0000-000000000001" } },
        { "buyerId", new AttributeValue { S = BuyerId } },
        { "status", new AttributeValue { S = "delivered" } },
        { "confirmedAt", new AttributeValue { S = DateTime.UtcNow.AddHours(-1).ToString("o") } },
        { "createdAt", new AttributeValue { S = "2026-10-09T08:00:00Z" } },
        { "version", new AttributeValue { N = "1" } }
    };

    private async Task<JsonElement> DisputeWithStaleAuthAsync()
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-26",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-dispute",
                        IdempotencyKey = $"{BuyerId}:delivery:{DeliveryId}:update:12345",
                        EntityType = "delivery",
                        EntityId = DeliveryId,
                        Action = "update",
                        Data = new Dictionary<string, object>
                        {
                            { "status", "disputed" }, { "disputeReason", "wet bags" }, { "version", 1 }
                        }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", BuyerId },
                    { "custom:role", "buyer" },
                    { "auth_time", DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString() }
                }
            }
        };

        var response = await _function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}

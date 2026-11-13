using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-370 (order-lifecycle 2.22.0): the order rows of <c>/sync</c> results carry <c>dropoffConfirmAt</c> like
/// the order reads: T9 with the order timings the cron judges by (a non-default <c>silent-buyer-hours</c> here),
/// to the order's parties, only with the drop-off judgement on. The row is the conflict echo of a stale update,
/// the row a client adopts.
/// </summary>
public sealed class SyncFunctionDropoffConfirmAtTests
{
    private const string OrderId = "ord-t9";
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly DateTime Received = DateTime.UtcNow.AddHours(-3);

    /// <summary>The SSM <c>silent-buyer-hours</c> set to 60 instead of the default 48.</summary>
    private static readonly OrderTimings Timings = OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(60) };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionDropoffConfirmAtTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _orders.GetByIdAsync(OrderId, true).Returns(_ => Order());
        _orders.GetByIdAsync(OrderId).Returns(_ => Order());
    }

    /// <summary>A trucked order on the road (version 9) whose trucker checked in at the drop-off before T8.</summary>
    private static Dictionary<string, object> Order() => new()
    {
        { "orderId", OrderId }, { "listingId", "listing-t9" }, { "createdAt", DateTime.UtcNow.AddDays(-4).ToString("o") },
        { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId }, { "tripId", "trip-t9" },
        { "status", "in_transit" }, { "version", "9" }, { "activeDeliveryId", "del-t9" },
        { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
        { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending },
        { DropoffJudgement.DeadlineField, OrderCommitment.Iso(DateTime.UtcNow.AddHours(40)) },
        {
            DropoffJudgement.CheckInField, new Dictionary<string, object>
            {
                { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(Received) },
                { PickupJudgement.EntryDistanceChecked, true },
                { PickupJudgement.EntryLate, false },
                { PickupJudgement.EntryBy, TruckerId }
            }
        }
    };

    private SyncFunction MakeFunction(bool dropoff) => new(_syncMeta, _crops, _dynamoDb, "test-trips-table", "test-deliveries-table",
        fraudService: _fraud, orderRepo: _orders)
    {
        CommitmentsEnabled = true,
        DropoffClassificationEnabled = dropoff,
        OrderTimingsSource = () => Timings
    };

    /// <summary>A stale <c>in_transit</c> update (version 3 against 9): answered with the stored row.</summary>
    private async Task<JsonElement> StaleUpdateAsync(string userId, bool dropoff = true)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-370",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-fup-370",
                        IdempotencyKey = $"{userId}:order:{OrderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = OrderId,
                        Action = "update",
                        Data = new Dictionary<string, object> { { "status", "in_transit" }, { "version", 3 } }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await MakeFunction(dropoff).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        var result = JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        return result.GetProperty("serverData");
    }

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public async Task EachPartysConflictRow_CarriesT9_WithTheSsmWindow(string userId)
    {
        var row = await StaleUpdateAsync(userId);

        row.GetProperty("dropoffConfirmAt").GetString().Should().Be(OrderCommitment.Iso(Received.AddHours(60)));
        row.TryGetProperty(DropoffJudgement.CheckInField, out _).Should().BeFalse("the raw check-in never leaves the server");
        row.GetProperty(PickupJudgement.CheckInsField).GetProperty(PickupJudgement.Kind.ArrivedDropoff)
            .GetProperty(PickupJudgement.EntryReceivedAt).GetString().Should().Be(OrderCommitment.Iso(Received));
    }

    [Fact]
    public async Task WithTheJudgementOff_TheRowCarriesNone()
    {
        var row = await StaleUpdateAsync(BuyerId, dropoff: false);

        row.TryGetProperty("dropoffConfirmAt", out _).Should().BeFalse();
        row.GetProperty(DropoffJudgement.Field).GetString().Should().Be(DropoffJudgement.Outcome.Pending);
    }
}

using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-370 against real DynamoDB (LocalStack in CI): the drop-off check-in as the repository stores and reads
/// it back (a nested map) gives <c>GET /orders/{id}</c> its <c>dropoffConfirmAt</c> with the order timings the
/// read uses, and the field is never written to the row.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DropoffConfirmAtDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T09:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public DropoffConfirmAtDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private string Id(string name) => $"{name}-fup370-{_run}";

    private string OrderId => Id("order");

    /// <summary>An order picked up with the flag on: the stamp written by the order's own advance.</summary>
    private async Task SeedOrderAsync(DateTime t8)
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = OrderId },
                ["createdAt"] = new() { S = CreatedAt },
                ["status"] = new() { S = "accepted" },
                ["version"] = new() { N = "6" },
                ["buyerId"] = new() { S = Id("buyer") },
                ["farmerId"] = new() { S = Id("farmer") },
                ["truckerId"] = new() { S = Id("trucker") },
                ["tripId"] = new() { S = Id("trip") },
                ["listingId"] = new() { S = Id("listing") },
                ["quantity"] = new() { N = "500" },
                ["activeDeliveryId"] = new() { S = Id("delivery") },
                [OrderCommitment.FulfillmentField] = new() { S = OrderCommitment.Fulfillment.Trucker },
                [OrderCommitment.FarmerResponseField] = new() { S = OrderCommitment.Response.Accepted },
                [OrderCommitment.TruckerResponseField] = new() { S = OrderCommitment.Response.Accepted }
            }
        });

        var pickedUpAt = t8 - OrderTimings.Defaults.DeliveryOverdue;
        var advance = DropoffJudgement.PickupStamp(pickedUpAt, new TrackPoint(9.03, 38.74), new TrackPoint(9.01, 38.76),
            tripRead: true, OrderTimings.Defaults, nowUtc: pickedUpAt);
        advance["status"] = "in_transit";
        (await Repo.UpdateAsync(OrderId, advance, 6)).IsSuccess.Should().BeTrue();
    }

    private Dictionary<string, object> Entry(DateTime received) => new()
    {
        { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(received) },
        { PickupJudgement.EntryBy, Id("trucker") },
        { PickupJudgement.EntryDistanceChecked, true },
        { PickupJudgement.EntryLate, false },
        { PickupJudgement.EntryLatitude, 9.031 },
        { PickupJudgement.EntryLongitude, 38.74 },
        { PickupJudgement.EntryDistanceKm, 0.11 }
    };

    [LocalDynamoDbFact]
    public async Task TheStoredCheckIn_GivesTheReadT9_WithTheSsmWindow_AndNothingIsStored()
    {
        await SeedOrderAsync(DateTime.UtcNow.AddHours(30));
        var received = DateTime.UtcNow;
        (await Repo.TryRecordDropoffCheckInAsync(OrderId, Entry(received), whileOpen: true)).Should().BeTrue();
        var stored = DropoffJudgement.ReceivedAt(DropoffJudgement.CheckInOf((await Repo.GetByIdAsync(OrderId, consistentRead: true))!)!)!.Value;

        var function = new OrderFunction(Repo)
        {
            DropoffJudgementOn = true,
            OrderTimingsSource = () => OrderTimings.Defaults with { SilentBuyerWindow = TimeSpan.FromHours(36) }
        };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = $"/orders/{OrderId}",
            PathParameters = new Dictionary<string, string> { { "id", OrderId } },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", Id("buyer") } }
            }
        };

        var response = await function.FunctionHandler(request, new TestLambdaContext());

        response.StatusCode.Should().Be(200, response.Body);
        var order = JsonDocument.Parse(response.Body).RootElement.GetProperty("order");
        order.GetProperty("dropoffConfirmAt").GetString().Should().Be(OrderCommitment.Iso(stored.AddHours(36)));
        (await Repo.GetByIdAsync(OrderId, consistentRead: true))!.Should().NotContainKey(DropoffJudgement.ConfirmAtField);
    }
}

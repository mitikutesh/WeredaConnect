using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.Extensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-06 against real DynamoDB (LocalStack in CI): the buyer's cancel of an accepted order after a
/// transport failure, with the real condition expressions. The pending delivery is cancelled first,
/// so a pickup that lands first refuses the cancel and leaves the order accepted; a version moved
/// after the delivery cancel is re-read and retried; and a delivery can no longer be created on the
/// cancelled order.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TransportCancelDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly IDeliveryRepository _deliveryHook = Substitute.For<IDeliveryRepository>();

    public TransportCancelDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _deliveryHook.TryCancelPendingAsync(Arg.Any<string>())
            .Returns(call => DeliveryRepo.TryCancelPendingAsync(call.Arg<string>()));
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var escrowService = new EscrowService(EscrowRepo, new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            idempotencyRepo: null, telebirrGateway: null, userRepo: null, orderRepo: OrderRepo, deliveryRepo: DeliveryRepo);
        return new SyncFunction(syncMeta, CropRepo, Ddb, _db.Trips, _db.Deliveries,
            orderRepo: OrderRepo, escrowRepo: EscrowRepo,
            transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit),
            orderEscrowService: escrowService, deliveryRepo: _deliveryHook);
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private async Task<(string OrderId, string DeliveryId, string ListingId)> SeedAcceptedOrderAsync()
    {
        var orderId = $"order-esc06-{Guid.NewGuid():N}";
        var deliveryId = $"delivery-esc06-{Guid.NewGuid():N}";
        var listingId = $"crop-esc06-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(listingId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("reserved"),
                ["quantity"] = N("0"),
                ["version"] = N("1")
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("accepted"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S("farmer-esc06"),
                ["listingId"] = S(listingId),
                ["quantity"] = N("300"),
                ["orderTotal"] = N("8000"),
                ["cropCost"] = N("7800"),
                ["tripId"] = S("trip-esc06"),
                ["truckerId"] = S("trucker-esc06"),
                ["pickupDeadlineAt"] = S(DateTime.UtcNow.AddHours(-2).ToString("o")),
                ["activeDeliveryId"] = S(deliveryId),
                ["activeDeliveryClaimedAt"] = S(CreatedAt),
                ["version"] = N("4")
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId),
                ["tripId"] = S("trip-esc06"),
                ["orderId"] = S(orderId),
                ["status"] = S("pending"),
                ["createdAt"] = S(CreatedAt),
                ["version"] = N("1")
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Escrows,
            Item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = S($"escrow-order-{orderId}"),
                ["createdAt"] = S(CreatedAt),
                ["orderId"] = S(orderId),
                ["source"] = S("order"),
                ["status"] = S("escrowed"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S("farmer-esc06"),
                ["amount"] = N("8000"),
                ["expiresAt"] = S("2026-10-08T10:00:00.0000000Z"),
                ["version"] = N("1")
            }
        });
        return (orderId, deliveryId, listingId);
    }

    private async Task<JsonElement> CancelAsync(string orderId, int version)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc06-it",
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = $"op-{Guid.NewGuid():N}",
                        IdempotencyKey = $"{_buyerId}:order:{orderId}:update:1",
                        EntityType = "order",
                        EntityId = orderId,
                        Action = "update",
                        Data = new Dictionary<string, object> { { "status", "cancelled" }, { "version", version } }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", _buyerId } }
            }
        };

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private async Task SetDeliveryStatusAsync(string deliveryId, string status) => await Ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = _db.Deliveries,
        Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(deliveryId), ["tripId"] = S("trip-esc06") },
        UpdateExpression = "SET #s = :s",
        ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = S(status) }
    });

    [LocalDynamoDbFact]
    public async Task Cancel_PastTheDeadline_CancelsDeliveryAndOrder_FreesTheSlot_RestocksAndSchedulesTheRefund()
    {
        var (orderId, deliveryId, listingId) = await SeedAcceptedOrderAsync();

        var result = await CancelAsync(orderId, version: 4);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var order = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        order!["status"].Should().Be("cancelled");
        order["cancelReason"].Should().Be("transport_failed_buyer_cancelled");
        order.Should().NotContainKey("activeDeliveryId");
        (await DeliveryRepo.GetByIdAsync(deliveryId, consistentRead: true))!["status"].Should().Be("cancelled");
        var escrow = await EscrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
        escrow!["refundReason"].Should().Be("order_transport_failed");
        var listing = await CropRepo.GetByIdAsync(listingId);
        Convert.ToDouble(listing!["quantity"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(3.0);
    }

    [LocalDynamoDbFact]
    public async Task Cancel_PickupLandsJustBeforeTheDeliveryCancel_IsRefused_AndTheOrderStaysAccepted()
    {
        var (orderId, deliveryId, _) = await SeedAcceptedOrderAsync();
        // Configure(): setting up must not run the default (real) cancel.
        _deliveryHook.Configure().TryCancelPendingAsync(deliveryId).Returns(async _ =>
        {
            // The trucker's pickup scan lands after the eligibility read: the real condition refuses.
            await SetDeliveryStatusAsync(deliveryId, "pickedUp");
            return await DeliveryRepo.TryCancelPendingAsync(deliveryId);
        });

        var result = await CancelAsync(orderId, version: 4);

        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
        var order = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        order!["status"].Should().Be("accepted");
        order["version"].ToString().Should().Be("4");
        (await EscrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true))!.Should().NotContainKey("refundReason");
    }

    [LocalDynamoDbFact]
    public async Task Cancel_VersionMovedAfterTheDeliveryCancel_IsRereadAndRetried()
    {
        var (orderId, deliveryId, _) = await SeedAcceptedOrderAsync();
        // Configure(): setting up must not run the default (real) cancel.
        _deliveryHook.Configure().TryCancelPendingAsync(deliveryId).Returns(async _ =>
        {
            var cancelled = await DeliveryRepo.TryCancelPendingAsync(deliveryId);
            // A system write bumps the order version between the delivery cancel and the order cancel.
            await Ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _db.Orders,
                Key = new Dictionary<string, AttributeValue> { ["orderId"] = S(orderId), ["createdAt"] = S(CreatedAt) },
                UpdateExpression = "SET version = :v",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":v"] = N("5") }
            });
            return cancelled;
        });

        var result = await CancelAsync(orderId, version: 4);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(6);
        (await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!["status"].Should().Be("cancelled");
    }

    [LocalDynamoDbFact]
    public async Task Cancel_StaleClientVersion_ChangesNothing()
    {
        var (orderId, deliveryId, _) = await SeedAcceptedOrderAsync();

        var result = await CancelAsync(orderId, version: 3);

        result.GetProperty("status").GetString().Should().Be("conflict");
        (await DeliveryRepo.GetByIdAsync(deliveryId, consistentRead: true))!["status"].Should().Be("pending");
        (await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!["status"].Should().Be("accepted");
    }

    [LocalDynamoDbTheory]
    [InlineData("pickedUp", false)]
    [InlineData("pending", true)]
    public async Task Cancel_OrderWithoutASlotClaim_ReadsItsDeliveriesThroughTheListingIndex(string status, bool cancels)
    {
        // An order whose delivery predates the slot claim: no activeDeliveryId on the order row.
        var (orderId, deliveryId, listingId) = await SeedAcceptedOrderAsync();
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Orders,
            Key = new Dictionary<string, AttributeValue> { ["orderId"] = S(orderId), ["createdAt"] = S(CreatedAt) },
            UpdateExpression = "REMOVE activeDeliveryId, activeDeliveryClaimedAt"
        });
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(deliveryId), ["tripId"] = S("trip-esc06") },
            UpdateExpression = "SET listingId = :l",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":l"] = S(listingId) }
        });
        await SetDeliveryStatusAsync(deliveryId, status);
        // Another order's delivery on the same listing is not this order's.
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S($"delivery-esc06-other-{Guid.NewGuid():N}"),
                ["tripId"] = S("trip-esc06"),
                ["orderId"] = S("order-someone-else"),
                ["listingId"] = S(listingId),
                ["status"] = S("inTransit"),
                ["createdAt"] = S(CreatedAt),
                ["version"] = N("1")
            }
        });

        var result = await CancelAsync(orderId, version: 4);

        if (cancels)
        {
            result.GetProperty("status").GetString().Should().Be("success", result.ToString());
            (await DeliveryRepo.GetByIdAsync(deliveryId, consistentRead: true))!["status"].Should().Be("cancelled");
        }
        else
        {
            result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.");
            (await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!["status"].Should().Be("accepted");
        }
    }

    [LocalDynamoDbFact]
    public async Task DeliveryCreate_OnTheCancelledOrder_IsRefusedByTheSlotCondition()
    {
        var (orderId, _, _) = await SeedAcceptedOrderAsync();
        (await CancelAsync(orderId, version: 4)).GetProperty("status").GetString().Should().Be("success");

        var lateId = $"delivery-esc06-late-{Guid.NewGuid():N}";
        var write = await OrderRepo.TryCreateDeliveryWithSlotAsync(orderId, _db.Deliveries, new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(lateId),
            ["tripId"] = S("trip-esc06"),
            ["orderId"] = S(orderId),
            ["status"] = S("pending"),
            ["createdAt"] = S(CreatedAt),
            ["version"] = N("1")
        });

        write.Should().Be(DeliverySlotWriteResult.OrderNotActive);
        (await DeliveryRepo.GetByIdAsync(lateId, consistentRead: true)).Should().BeNull();
        (await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!.Should().NotContainKey("activeDeliveryId");
    }
}

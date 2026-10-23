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
using Xunit.Abstractions;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-32 against real DynamoDB (LocalStack in CI): the buyer's transport-failure cancel and a
/// dispute freeze racing for the same order escrow. The cancel writes the order and the refund arm
/// in one transaction conditioned on a plain hold, and every freeze write requires that no refund
/// is armed, so exactly one of them lands: never a cancelled order over frozen money. Each
/// interleaving is forced with a hook, then both run concurrently many times.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CancelDisputeRaceDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string EscrowExpiresAt = "2026-10-08T10:00:00.0000000Z";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly IDeliveryRepository _deliveryHook = Substitute.For<IDeliveryRepository>();
    private readonly ITestOutputHelper _output;

    public CancelDisputeRaceDynamoDbTests(LocalDynamoDbFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
        _deliveryHook.TryCancelPendingAsync(Arg.Any<string>())
            .Returns(call => DeliveryRepo.TryCancelPendingAsync(call.Arg<string>()));
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    /// <summary>The escrow service as the sync Lambda builds it (delivery disputes, the transport cancel).</summary>
    private EscrowService SyncEscrowService(IEscrowRepository? escrowRepo = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, idempotencyRepo: null, telebirrGateway: null,
        orderRepo: OrderRepo, deliveryRepo: DeliveryRepo, transactionAuditRepo: TxRepo);

    /// <summary>The escrow service as the escrow Lambda builds it, for <c>POST /escrow/dispute</c>.</summary>
    private EscrowService EndpointEscrowService() => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), Substitute.For<ITelebirrGateway>(),
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo);

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            orderRepo: OrderRepo, escrowRepo: EscrowRepo, transactionAuditRepo: TxRepo,
            orderEscrowService: SyncEscrowService(), deliveryRepo: _deliveryHook);
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private sealed record Trade(string OrderId, string DeliveryId, string EscrowId);

    /// <summary>An accepted order past its pickup deadline, its pending delivery and its plain escrowed money.</summary>
    private async Task<Trade> SeedAcceptedOrderAsync()
    {
        var orderId = $"order-fup32-{Guid.NewGuid():N}";
        var deliveryId = $"delivery-fup32-{Guid.NewGuid():N}";
        var listingId = $"crop-fup32-{Guid.NewGuid():N}";
        var escrowId = $"escrow-order-{orderId}";
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
                ["farmerId"] = S(_farmerId),
                ["listingId"] = S(listingId),
                ["quantity"] = N("300"),
                ["orderTotal"] = N("8000"),
                ["cropCost"] = N("7800"),
                ["tripId"] = S("trip-fup32"),
                ["truckerId"] = S("trucker-fup32"),
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
                ["tripId"] = S("trip-fup32"),
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
                ["escrowId"] = S(escrowId),
                ["createdAt"] = S(CreatedAt),
                ["orderId"] = S(orderId),
                ["source"] = S("order"),
                ["status"] = S("escrowed"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["amount"] = N("8000"),
                ["expiresAt"] = S(EscrowExpiresAt),
                ["version"] = N("1")
            }
        });
        return new Trade(orderId, deliveryId, escrowId);
    }

    private async Task<JsonElement> CancelAsync(string orderId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup32-it",
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = $"op-{Guid.NewGuid():N}",
                        IdempotencyKey = $"{_buyerId}:order:{orderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = orderId,
                        Action = "update",
                        Data = new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } }
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

    /// <summary>A freeze through the shared ESC-07 path (the same status write and audit row as <c>POST /escrow/dispute</c>).</summary>
    private Task<DeliveryDisputeFreezeResult> FreezeAsync(Trade trade, IEscrowRepository? escrowRepo = null)
        => SyncEscrowService(escrowRepo).FreezeForDeliveryDisputeAsync(trade.DeliveryId, trade.OrderId, _farmerId, "The buyer is lying.");

    private async Task<List<string>> EscrowAuditActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString()!).ToList();

    private async Task<List<string>> OrderTxActionsAsync(string orderId)
        => (await TxRepo.GetByTransactionAsync(orderId))
            .Where(r => Str(r, "entityType") == "order").Select(r => Str(r, "action")!).ToList();

    // ===== Freeze first: the cancel is refused =====

    [LocalDynamoDbFact]
    public async Task FreezeLandsBetweenTheCheckAndTheCancel_RefusesTheCancel_AndTheOrderStaysAccepted()
    {
        var trade = await SeedAcceptedOrderAsync();
        DeliveryDisputeFreezeResult? freeze = null;
        // Configure(): setting up must not run the default (real) cancel. The hook runs after the
        // escrow check and before the order write, the window FUP-32 is about.
        _deliveryHook.Configure().TryCancelPendingAsync(trade.DeliveryId).Returns(async _ =>
        {
            var cancelled = await DeliveryRepo.TryCancelPendingAsync(trade.DeliveryId);
            freeze = await FreezeAsync(trade);
            return cancelled;
        });

        var result = await CancelAsync(trade.OrderId);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.Frozen);
        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        var order = await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true);
        Str(order, "status").Should().Be("accepted");
        Str(order, "version").Should().Be("4");
        Str(order, "activeDeliveryId").Should().Be(trade.DeliveryId);
        var escrow = await EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);
        Str(escrow, "status").Should().Be("disputed");
        escrow.Should().NotContainKey("refundReason");
        Str(escrow, "expiresAt").Should().Be(EscrowExpiresAt);
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute");
        (await OrderTxActionsAsync(trade.OrderId)).Should().NotContain("order_cancelled");
    }

    [LocalDynamoDbFact]
    public async Task FreezeAndAVersionBumpBothLand_TheEscrowRefusalDecides_NotARetry()
    {
        var trade = await SeedAcceptedOrderAsync();
        var order = await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true);
        (await FreezeAsync(trade)).Outcome.Should().Be(DeliveryDisputeFreeze.Frozen);
        var arm = EscrowRepo.BuildRefundArm(trade.EscrowId, CreatedAt, "order_transport_failed", DateTime.UtcNow.ToString("o"));

        // Stale version (3, the row is at 4) and a frozen escrow: both conditions fail.
        var write = await OrderRepo.TryUpdateWithAsync(trade.OrderId,
            new Dictionary<string, object> { { "status", "cancelled" } }, 3, "accepted", new[] { arm });

        write.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);
        Str(await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true), "status").Should().Be(Str(order, "status"));
    }

    // ===== Cancel first: every freeze path is refused =====

    [LocalDynamoDbFact]
    public async Task CancelLandsFirst_ThenEveryFreezeIsRefused_AndTheRefundStaysArmed()
    {
        var trade = await SeedAcceptedOrderAsync();
        (await CancelAsync(trade.OrderId)).GetProperty("status").GetString().Should().Be("success");

        // POST /escrow/dispute, by either party.
        foreach (var party in new[] { _farmerId, _buyerId })
        {
            var dispute = await EndpointEscrowService().DisputeAsync(new DisputeEscrowRequest
            {
                EscrowId = trade.EscrowId,
                DisputedBy = party,
                Reason = "not_delivered",
                IdempotencyKey = $"{party}:{trade.EscrowId}:dispute:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            }, party);
            dispute.Error!.HttpStatus.Should().Be(409);
            dispute.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        }

        // A delivery dispute (sync) or the escrow-cron legacy freeze.
        var freeze = await FreezeAsync(trade);
        freeze.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);

        // The status write itself, whatever path calls it.
        var raw = () => EscrowRepo.UpdateStatusAsync(trade.EscrowId, CreatedAt, "disputed", "escrowed",
            new Dictionary<string, object> { { "disputeId", "dispute-raw" } });
        await raw.Should().ThrowAsync<ConditionalCheckFailedException>();

        Str(await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true), "status").Should().Be("cancelled");
        var escrow = await EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);
        Str(escrow, "status").Should().Be("escrowed");
        Str(escrow, "refundReason").Should().Be("order_transport_failed");
        escrow.Should().NotContainKey("disputeId");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().BeEquivalentTo("refund_scheduled", "dispute_refused_refund_scheduled");
    }

    [LocalDynamoDbFact]
    public async Task ArmLandsBetweenTheFreezesReadAndItsWrite_RefusesTheFreeze()
    {
        var trade = await SeedAcceptedOrderAsync();
        JsonElement? cancel = null;
        // The freeze has read a plain escrow; the whole cancel lands before its status write.
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (_, status) =>
            {
                if (status == "disputed" && cancel == null)
                {
                    // Off the test's synchronization context, so blocking here cannot deadlock.
                    cancel = Task.Run(() => CancelAsync(trade.OrderId)).GetAwaiter().GetResult();
                }

                return null;
            }
        };

        var freeze = await FreezeAsync(trade, racing);

        cancel!.Value.GetProperty("status").GetString().Should().Be("success");
        freeze.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
        Str(await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true), "status").Should().Be("cancelled");
        var escrow = await EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);
        Str(escrow, "status").Should().Be("escrowed");
        Str(escrow, "refundReason").Should().Be("order_transport_failed");
    }

    // ===== Both at once, many times =====

    [LocalDynamoDbFact]
    public async Task CancelAndFreezeConcurrently_NeverLeaveACancelledOrderOverFrozenMoney()
    {
        // The cancel reads a lot before it writes, so a freeze started with it always wins. Each round
        // starts the freeze a little later, from at once to past the cancel's whole run, so rounds land
        // before the check, inside the window and after the commit. The hooked tests above force each.
        var warmUp = await SeedAcceptedOrderAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await CancelAsync(warmUp.OrderId)).GetProperty("status").GetString().Should().Be("success");
        var cancelTakes = clock.Elapsed;

        var cancelsWon = 0;
        var freezesWon = 0;
        for (var round = 0; round < 25; round++)
        {
            var trade = await SeedAcceptedOrderAsync();
            var start = new TaskCompletionSource();
            var freezeAfter = TimeSpan.FromTicks(cancelTakes.Ticks * round / 20);
            var cancelTask = Task.Run(async () => { await start.Task; return await CancelAsync(trade.OrderId); });
            var freezeTask = Task.Run(async () =>
            {
                await start.Task;
                await Task.Delay(freezeAfter);
                try
                {
                    return await FreezeAsync(trade);
                }
                catch (AmazonDynamoDBException)
                {
                    // An undecided freeze (it met the cancel's transaction in flight): the sync
                    // client retries it as DELIVERY_DISPUTE_RETRY. Nothing was written.
                    return null;
                }
            });
            start.SetResult();
            var cancel = await cancelTask;
            var freeze = await freezeTask;

            var order = await OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true);
            var escrow = await EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);
            var orderCancelled = Str(order, "status") == "cancelled";
            var frozen = Str(escrow, "status") == "disputed";
            var armed = Str(escrow, "refundReason") != null;

            (orderCancelled && frozen).Should().BeFalse($"round {round}: a cancelled order over frozen money");
            orderCancelled.Should().Be(armed, $"round {round}: the cancel and its refund arm land together");
            (cancel.GetProperty("status").GetString() == "success").Should().Be(orderCancelled, $"round {round}");
            (freeze?.Outcome == DeliveryDisputeFreeze.Frozen).Should().Be(frozen, $"round {round}");
            if (orderCancelled)
            {
                Str(escrow, "status").Should().Be("escrowed", $"round {round}");
                cancelsWon++;
            }

            if (frozen)
            {
                Str(order, "status").Should().Be("accepted", $"round {round}");
                freezesWon++;
            }

            if (!orderCancelled && !frozen)
            {
                // Neither landed only when both met in flight: the cancel answered a transient
                // error (the client resends it) or the freeze was undecided (retried). Never a
                // permanent refusal on both sides.
                var transientCancel = cancel.GetProperty("status").GetString() == "error"
                    && !cancel.GetProperty("message").GetString()!.StartsWith("ORDER_", StringComparison.Ordinal);
                (transientCancel || freeze == null).Should().BeTrue($"round {round}: nothing landed and nothing will retry");
            }
        }

        _output.WriteLine(
            $"25 rounds over a {cancelTakes.TotalMilliseconds:F0} ms cancel: the cancel won {cancelsWon}, the freeze won {freezesWon}.");
        (cancelsWon + freezesWon).Should().BePositive();
    }
}

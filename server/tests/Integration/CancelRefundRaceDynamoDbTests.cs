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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>Own tables: the auto-refund sweep reads the whole expired window, so no other suite's rows reach it.</summary>
[CollectionDefinition(Name)]
public sealed class CancelRefundRaceDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "CancelRefundRaceDynamoDb";
}

/// <summary>
/// FUP-53 against real DynamoDB (LocalStack in CI): the three writers that close an order and then
/// arm or take its refund (the funded order's reject or cancel in sync, the admin refund before
/// pickup, the auto-refund sweep's cancel of a stale funded order), each racing a dispute freeze,
/// and <c>POST /escrow/dispute</c> racing a writer that closes its order. Every interleaving is
/// forced with a hook at the losing point. The end state is always one of: order closed and refund
/// armed (or paid), or order unchanged and nothing armed. Never a closed order over frozen money.
/// </summary>
[Collection(CancelRefundRaceDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CancelRefundRaceDynamoDbTests
{
    private const string AdminId = "admin-fup53";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string CancelRefused = "ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly List<string> _refunded = new();

    public CancelRefundRaceDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            lock (_refunded)
            {
                _refunded.Add(call.Arg<RefundFundsRequest>().TransactionRef);
            }

            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private EscrowIdempotencyRepository IdempotencyRepo => new(Ddb, _db.Idempotency);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    /// <summary>The escrow service as the sync Lambda builds it (no gateway, no idempotency table).</summary>
    private EscrowService SyncEscrowService(IEscrowRepository? escrowRepo = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, idempotencyRepo: null, telebirrGateway: null,
        orderRepo: OrderRepo, deliveryRepo: DeliveryRepo, transactionAuditRepo: TxRepo);

    /// <summary>The escrow service as the escrow, admin and cron Lambdas build it.</summary>
    private EscrowService MoneyService(IEscrowRepository? escrowRepo = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, IdempotencyRepo, _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo, cropRepo: CropRepo);

    private SyncFunction Sync(IEscrowRepository? escrowRepo, bool commitments)
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, CropRepo, Ddb, _db.Trips, _db.Deliveries,
            orderRepo: OrderRepo, escrowRepo: escrowRepo ?? EscrowRepo, transactionAuditRepo: TxRepo,
            orderEscrowService: SyncEscrowService(escrowRepo), deliveryRepo: DeliveryRepo)
        {
            CommitmentsEnabled = commitments
        };
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private sealed record Trade(string OrderId, string EscrowId, string ListingId, string EscrowCreatedAt, string ExpiresAt);

    /// <summary>
    /// A paid order before pickup with its plain <c>escrowed</c> money (8,000 ETB) and its stock taken
    /// (the listing holds 0 quintals; the order's 300 kg come back as 3). <paramref name="ledger"/>
    /// makes it an ESC-20 ledger escrow with every leg held; <paramref name="expired"/> puts it in the
    /// auto-refund sweep's window.
    /// </summary>
    private async Task<Trade> SeedAsync(string orderStatus = "funded", bool ledger = false, bool expired = false)
    {
        var orderId = $"order-fup53-{Guid.NewGuid():N}";
        var listingId = $"crop-fup53-{Guid.NewGuid():N}";
        var escrowId = $"escrow-order-{orderId}";
        var escrowCreatedAt = expired ? DateTime.UtcNow.AddDays(-8).ToString("o") : CreatedAt;
        var expiresAt = (expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(6)).ToString("o");
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
                ["status"] = S(orderStatus),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["listingId"] = S(listingId),
                ["quantity"] = N("300"),
                ["orderTotal"] = N("8000"),
                ["cropCost"] = N("7800"),
                ["version"] = N("4")
            }
        });
        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(escrowCreatedAt),
            ["orderId"] = S(orderId),
            ["source"] = S("order"),
            ["status"] = S("escrowed"),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-FUP53"),
            ["expiresAt"] = S(expiresAt),
            ["version"] = N("1")
        };
        if (ledger)
        {
            escrow[LedgerEscrowFields.SettlementModel] = S(LedgerEscrowFields.ModelLedger);
            escrow[LedgerEscrowFields.LegStatus] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    [LedgerEscrowFields.LegCrop] = S(LedgerEscrowFields.Held),
                    [LedgerEscrowFields.LegCommission] = S(LedgerEscrowFields.Held)
                }
            };
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = escrow });
        return new Trade(orderId, escrowId, listingId, escrowCreatedAt, expiresAt);
    }

    private async Task<JsonElement> SendOrderAsync(
        SyncFunction sync, string userId, string orderId, Dictionary<string, object> data)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup53-it",
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = $"op-{Guid.NewGuid():N}",
                        IdempotencyKey = $"{userId}:order:{orderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = orderId,
                        Action = "update",
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await sync.FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    /// <summary>The farmer's reject of a funded order: the legacy status write, or the commitment command.</summary>
    private Task<JsonElement> RejectAsync(Trade trade, bool commitments, IEscrowRepository? escrowRepo = null)
        => SendOrderAsync(Sync(escrowRepo, commitments), _farmerId, trade.OrderId, commitments
            ? new Dictionary<string, object> { { "farmerResponse", "decline" }, { "version", 4 } }
            : new Dictionary<string, object> { { "status", "rejected" }, { "version", 4 } });

    /// <summary>
    /// A dispute freeze through the shared ESC-07 path: the same status write, conditions and audit
    /// row as <c>POST /escrow/dispute</c>, without its before-pickup rule, so it can land anywhere.
    /// </summary>
    private Task<DeliveryDisputeFreezeResult> FreezeAsync(Trade trade)
        => SyncEscrowService().FreezeForDeliveryDisputeAsync($"delivery-of-{trade.OrderId}", trade.OrderId, _farmerId, "It never came.");

    /// <summary>Runs <paramref name="race"/> once, off the caller's context, from inside a synchronous hook.</summary>
    private static Action<string> Once(Func<Task> race)
    {
        var ran = false;
        return _ =>
        {
            if (ran)
            {
                return;
            }

            ran = true;
            Task.Run(race).GetAwaiter().GetResult();
        };
    }

    private Task<Dictionary<string, object>?> OrderAsync(Trade trade) => OrderRepo.GetByIdAsync(trade.OrderId, consistentRead: true);
    private Task<Dictionary<string, object>?> EscrowAsync(Trade trade) => EscrowRepo.GetByIdAsync(trade.EscrowId, consistentRead: true);

    private async Task<List<string>> EscrowAuditActionsAsync(Trade trade)
        => (await AuditRepo.GetAuditTrailAsync(trade.EscrowId, consistentRead: true)).Select(a => a["action"].ToString()!).ToList();

    private async Task<List<string>> OrderTxActionsAsync(Trade trade)
        => (await TxRepo.GetByTransactionAsync(trade.OrderId))
            .Where(r => Str(r, "entityType") == "order").Select(r => Str(r, "action")!).ToList();

    /// <summary>Quintals on the listing: 0 while the order holds its stock, 3 once it was given back.</summary>
    private async Task<double> ListingQuantityAsync(Trade trade)
        => double.Parse(Str(await CropRepo.GetByIdAsync(trade.ListingId), "quantity")!, System.Globalization.CultureInfo.InvariantCulture);

    private int Refunds(Trade trade)
    {
        lock (_refunded)
        {
            return _refunded.Count(r => r.Contains(trade.EscrowId, StringComparison.Ordinal));
        }
    }

    /// <summary>The order is as it was seeded, the money is frozen by the dispute, and nothing is armed.</summary>
    private async Task OrderUntouchedOverFrozenMoneyAsync(Trade trade, string orderStatus)
    {
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be(orderStatus);
        Str(order, "version").Should().Be("4");
        order.Should().NotContainKey("cancelReason");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("disputed");
        escrow.Should().NotContainKey("refundReason");
        Str(escrow, "expiresAt").Should().Be(trade.ExpiresAt);
        (await EscrowAuditActionsAsync(trade)).Should().NotContain("refund_scheduled");
        (await OrderTxActionsAsync(trade)).Should().BeEmpty();
        (await ListingQuantityAsync(trade)).Should().Be(0);
        Refunds(trade).Should().Be(0);
    }

    // ──────────────────────────────────────────────
    // 1. Sync: the reject or cancel of a funded order
    // ──────────────────────────────────────────────

    [LocalDynamoDbTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FundedReject_FreezeLandsBetweenThePlanAndTheWrite_RefusesTheReject(bool commitments, bool ledger)
    {
        var trade = await SeedAsync(ledger: ledger);
        DeliveryDisputeFreezeResult? freeze = null;
        // The reject has read a plain hold; the freeze lands right before its transaction.
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = Once(async () => freeze = await FreezeAsync(trade))
        };

        var result = await RejectAsync(trade, commitments, racing);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.Frozen);
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(CancelRefused);
        await OrderUntouchedOverFrozenMoneyAsync(trade, "funded");
    }

    [LocalDynamoDbTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FundedReject_LandsFirst_ThenEveryFreezeIsRefused_AndTheRefundStaysArmed(bool commitments, bool ledger)
    {
        var trade = await SeedAsync(ledger: ledger);

        var result = await RejectAsync(trade, commitments);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(5);
        (await FreezeAsync(trade)).Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
        foreach (var party in new[] { _farmerId, _buyerId })
        {
            var dispute = await MoneyService().DisputeAsync(new DisputeEscrowRequest
            {
                EscrowId = trade.EscrowId,
                DisputedBy = party,
                Reason = "not_delivered",
                IdempotencyKey = $"{party}:{trade.EscrowId}:dispute:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            }, party);
            dispute.Error!.HttpStatus.Should().Be(409);
            dispute.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
            dispute.Error.Message.Should().Contain("refund to the buyer is scheduled");
        }

        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("rejected");
        Str(order, "version").Should().Be("5");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("escrowed");
        Str(escrow, "refundReason").Should().Be("order_rejected");
        Str(escrow, "version").Should().Be("2");
        DateTime.Parse(Str(escrow, "expiresAt")!).ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        escrow.Should().NotContainKey("disputeId");
        // The same rows as before FUP-53: one refund_scheduled by the system, one order hop, the stock back.
        (await EscrowAuditActionsAsync(trade)).Where(a => a != "dispute_refused_refund_scheduled").Should().Equal("refund_scheduled");
        (await OrderTxActionsAsync(trade)).Should().ContainSingle();
        (await ListingQuantityAsync(trade)).Should().Be(3);
    }

    [LocalDynamoDbFact]
    public async Task FundedCancel_ByTheBuyer_RacesTheSameWay()
    {
        // COMMITMENTS_ENABLED off: the buyer may still cancel a funded order.
        var frozenFirst = await SeedAsync();
        var racing = new FaultyEscrowRepository(EscrowRepo) { OnBuildRefundArm = Once(() => FreezeAsync(frozenFirst)) };
        var cancel = new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } };

        var refused = await SendOrderAsync(Sync(racing, commitments: false), _buyerId, frozenFirst.OrderId, cancel);

        refused.GetProperty("message").GetString().Should().Be(CancelRefused);
        await OrderUntouchedOverFrozenMoneyAsync(frozenFirst, "funded");

        var plain = await SeedAsync();
        var landed = await SendOrderAsync(Sync(null, commitments: false), _buyerId, plain.OrderId, cancel);

        landed.GetProperty("status").GetString().Should().Be("success", landed.ToString());
        Str(await OrderAsync(plain), "status").Should().Be("cancelled");
        Str(await EscrowAsync(plain), "refundReason").Should().Be("order_cancelled");
        (await FreezeAsync(plain)).Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
    }

    [LocalDynamoDbFact]
    public async Task FundedReject_AnAcceptLandsBetweenThePlanAndTheWrite_IsAConflict_AndNothingIsArmed()
    {
        // The other loser: the order moved, the escrow did not. Neither half of the transaction lands.
        var trade = await SeedAsync();
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = Once(async () => (await OrderRepo.UpdateAsync(trade.OrderId,
                new Dictionary<string, object> { { "status", "accepted" } }, 4)).IsSuccess.Should().BeTrue())
        };

        var result = await RejectAsync(trade, commitments: false, racing);

        result.GetProperty("status").GetString().Should().Be("conflict");
        Str(await OrderAsync(trade), "status").Should().Be("accepted");
        var escrow = await EscrowAsync(trade);
        escrow.Should().NotContainKey("refundReason");
        Str(escrow, "expiresAt").Should().Be(trade.ExpiresAt);
        (await EscrowAuditActionsAsync(trade)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task FundedReject_OfAnEscrowHeldForReview_ClosesWithoutAnArm_AndAReleasedHoldArmsInstead()
    {
        // FRD-03: the reject stays allowed while the escrow is held, and its admin refunds it.
        var held = await SeedAsync();
        await EscrowRepo.UpdateStatusAsync(held.EscrowId, held.EscrowCreatedAt, EscrowHoldFields.Status, "escrowed");

        (await RejectAsync(held, commitments: false)).GetProperty("status").GetString().Should().Be("success");

        Str(await OrderAsync(held), "status").Should().Be("rejected");
        var escrow = await EscrowAsync(held);
        Str(escrow, "status").Should().Be(EscrowHoldFields.Status);
        escrow.Should().NotContainKey("refundReason");

        // The hold is released between the plan and the write: the fence refuses, the second plan arms.
        var released = await SeedAsync();
        await EscrowRepo.UpdateStatusAsync(released.EscrowId, released.EscrowCreatedAt, EscrowHoldFields.Status, "escrowed");
        var checks = 0;
        var hooked = new ReleasingHoldRepository(EscrowRepo, async () =>
        {
            if (checks++ == 0)
            {
                await EscrowRepo.UpdateStatusAsync(released.EscrowId, released.EscrowCreatedAt, "escrowed", EscrowHoldFields.Status);
            }
        });

        (await RejectAsync(released, commitments: false, hooked.Repository)).GetProperty("status").GetString().Should().Be("success");

        Str(await OrderAsync(released), "status").Should().Be("rejected");
        Str(await EscrowAsync(released), "refundReason").Should().Be("order_rejected");
    }

    /// <summary>Runs a step when the hold fence is built, right before the transaction that carries it.</summary>
    private sealed class ReleasingHoldRepository
    {
        public ReleasingHoldRepository(IEscrowRepository inner, Func<Task> onStatusCheck)
        {
            Repository = Substitute.For<IEscrowRepository>();
            Repository.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>())
                .Returns(call => inner.QueryByOrderIdAsync(call.Arg<string>(), call.Arg<int>()));
            Repository.GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>())
                .Returns(call => inner.GetByIdAsync(call.Arg<string>(), call.Arg<bool>()));
            Repository.GetByIdAsync(Arg.Any<string>()).Returns(call => inner.GetByIdAsync(call.Arg<string>()));
            Repository.BuildRefundArm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(call => inner.BuildRefundArm(
                    call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(3), call.ArgAt<bool>(4)));
            Repository.BuildStatusCheck(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
            {
                Task.Run(onStatusCheck).GetAwaiter().GetResult();
                return inner.BuildStatusCheck(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2));
            });
        }

        public IEscrowRepository Repository { get; }
    }

    // ──────────────────────────────────────────────
    // 2. The admin refund before pickup
    // ──────────────────────────────────────────────

    private static RefundEscrowRequest AdminRefund(Trade trade) => new()
    {
        EscrowId = trade.EscrowId,
        Trigger = "admin_override",
        IdempotencyKey = $"admin:{trade.EscrowId}:refund:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
    };

    [LocalDynamoDbTheory]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("awaiting_transport")]
    public async Task AdminRefund_FreezeLandsBetweenItsReadAndTheCancel_RefusesBoth_AndPaysNothing(string orderStatus)
    {
        var trade = await SeedAsync(orderStatus);
        DeliveryDisputeFreezeResult? freeze = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = Once(async () => freeze = await FreezeAsync(trade))
        };
        if (orderStatus == "awaiting_transport")
        {
            // Only funded and accepted orders reach the admin's cancel; anything else is refused before it.
            var refusedEarly = await MoneyService(racing).RefundAsync(AdminRefund(trade), AdminId, "admin", adminGroupVerified: true);
            refusedEarly.Error!.HttpStatus.Should().Be(422);
            freeze.Should().BeNull();
            Str(await OrderAsync(trade), "status").Should().Be(orderStatus);
            Refunds(trade).Should().Be(0);
            return;
        }

        var result = await MoneyService(racing).RefundAsync(AdminRefund(trade), AdminId, "admin", adminGroupVerified: true);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.Frozen);
        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Contain("it is 'disputed' now");
        await OrderUntouchedOverFrozenMoneyAsync(trade, orderStatus);
        (await EscrowAsync(trade)).Should().NotContainKey(SettlementLockFields.Lock);
    }

    [LocalDynamoDbTheory]
    [InlineData("funded")]
    [InlineData("accepted")]
    public async Task AdminRefund_FreezeArrivesBetweenTheCancelAndTheLock_IsRefused_AndTheRefundIsPaid(string orderStatus)
    {
        var trade = await SeedAsync(orderStatus);
        DeliveryDisputeFreezeResult? freeze = null;
        // The order is cancelled and the refund armed in one write; the freeze comes before the lock.
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnAcquireSettlementLock = Once(async () => freeze = await FreezeAsync(trade))
        };

        var result = await MoneyService(racing).RefundAsync(AdminRefund(trade), AdminId, "admin", adminGroupVerified: true);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be("escrow_refunded");
        Str(order, "version").Should().Be("5");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("refunded");
        Str(escrow, "refundReason").Should().Be("admin_refund");
        escrow.Should().NotContainKey("disputeId");
        Refunds(trade).Should().Be(1);
        (await ListingQuantityAsync(trade)).Should().Be(3);
        (await EscrowAuditActionsAsync(trade)).Count(a => a == "refund_scheduled").Should().Be(1);
        (await OrderTxActionsAsync(trade)).Should().Contain("order_cancelled");
    }

    [LocalDynamoDbFact]
    public async Task AdminRefund_WithNothingInTheWay_EndsAsBefore()
    {
        var trade = await SeedAsync();

        var result = await MoneyService().RefundAsync(AdminRefund(trade), AdminId, "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        Str(await OrderAsync(trade), "status").Should().Be("cancelled");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("refunded");
        Str(escrow, "refundReason").Should().Be("admin_refund");
        var trail = await AuditRepo.GetAuditTrailAsync(trade.EscrowId, consistentRead: true);
        trail.Select(a => a["action"].ToString()).Should().BeEquivalentTo("refund_scheduled", "refund");
        trail.Single(a => a["action"].ToString() == "refund_scheduled")["actor"].ToString().Should().Be(AdminId);
        (await OrderTxActionsAsync(trade)).Should().Equal("order_cancelled");
        Refunds(trade).Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // 3. The auto-refund sweep's cancel of a stale funded order
    // ──────────────────────────────────────────────

    private async Task SweepAsync(IEscrowRepository escrowRepo)
    {
        var cron = new EscrowCronFunction(MoneyService(escrowRepo), escrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
            cropRepo: CropRepo, idempotencyRepo: IdempotencyRepo, transactionAuditRepo: TxRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" },
            new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(5) });
        response.StatusCode.Should().Be(200, response.Body);
    }

    [LocalDynamoDbTheory]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    public async Task Sweep_FreezeLandsBetweenItsReadAndTheCancel_LeavesTheOrder_AndRefundsNothing(string orderStatus)
    {
        var trade = await SeedAsync(orderStatus, expired: true);
        DeliveryDisputeFreezeResult? freeze = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnBuildRefundArm = id =>
            {
                if (id == trade.EscrowId && freeze == null)
                {
                    freeze = Task.Run(() => FreezeAsync(trade)).GetAwaiter().GetResult();
                }
            }
        };

        await SweepAsync(racing);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.Frozen);
        await OrderUntouchedOverFrozenMoneyAsync(trade, orderStatus);
    }

    [LocalDynamoDbTheory]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    public async Task Sweep_FreezeArrivesBetweenTheCancelAndTheRefund_IsRefused_AndTheRefundIsPaid(string orderStatus)
    {
        var trade = await SeedAsync(orderStatus, expired: true);
        DeliveryDisputeFreezeResult? freeze = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnAcquireSettlementLock = id =>
            {
                if (id == trade.EscrowId && freeze == null)
                {
                    freeze = Task.Run(() => FreezeAsync(trade)).GetAwaiter().GetResult();
                }
            }
        };

        await SweepAsync(racing);

        freeze!.Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);
        var order = await OrderAsync(trade);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be("escrow_refunded");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("refunded");
        escrow.Should().NotContainKey("disputeId");
        Refunds(trade).Should().Be(1);
        (await ListingQuantityAsync(trade)).Should().Be(3);
    }

    [LocalDynamoDbFact]
    public async Task Sweep_WithNothingInTheWay_EndsAsBefore_AndARetryAfterAFailedRefundStillPays()
    {
        var trade = await SeedAsync(expired: true);
        var failing = await SeedAsync(expired: true);
        var fail = true;
        _gateway.RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef.Contains(failing.EscrowId))).Returns(_ => fail
            ? new RefundFundsResponse { Success = false, ErrorCode = "TELEBIRR_REFUND_FAILED" }
            : new RefundFundsResponse { Success = true, TelebirrRef = "GW-RETRY" });

        await SweepAsync(EscrowRepo);

        Str(await OrderAsync(trade), "status").Should().Be("cancelled");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("refunded");
        Str(escrow, "refundReason").Should().Be(EscrowCronFunction.ExpiredOrderRefundReason);
        // The sweep never wrote a refund_scheduled row, and still does not.
        (await EscrowAuditActionsAsync(trade)).Should().Equal("refund");
        Refunds(trade).Should().Be(1);
        (await ListingQuantityAsync(trade)).Should().Be(3);

        // The other one: cancelled and armed, the gateway refused. It stays armed for the next run,
        // which finds the order closed and only refunds.
        Str(await OrderAsync(failing), "status").Should().Be("cancelled");
        var armed = await EscrowAsync(failing);
        Str(armed, "status").Should().Be("escrowed");
        Str(armed, "refundReason").Should().Be(EscrowCronFunction.ExpiredOrderRefundReason);
        (await FreezeAsync(failing)).Outcome.Should().Be(DeliveryDisputeFreeze.RefusedRefundScheduled);

        fail = false;
        await SweepAsync(EscrowRepo);

        Str(await EscrowAsync(failing), "status").Should().Be("refunded");
        Str(await OrderAsync(failing), "version").Should().Be("5");
    }

    // ──────────────────────────────────────────────
    // 4. POST /escrow/dispute: the order is checked in the freeze's transaction
    // ──────────────────────────────────────────────

    private DisputeEscrowRequest Dispute(Trade trade, string key) => new()
    {
        EscrowId = trade.EscrowId,
        DisputedBy = _buyerId,
        Reason = "not_delivered",
        IdempotencyKey = key
    };

    private string DisputeKey(Trade trade) => $"{_buyerId}:{trade.EscrowId}:dispute:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    private Task CloseOrderRawAsync(Trade trade, string status) => Ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = _db.Orders,
        Key = new Dictionary<string, AttributeValue> { ["orderId"] = S(trade.OrderId), ["createdAt"] = S(CreatedAt) },
        UpdateExpression = "SET #s = :s, #v = :v",
        ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status", ["#v"] = "version" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = S(status), [":v"] = N("5") }
    });

    [LocalDynamoDbTheory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task Dispute_OrderClosedBetweenItsReadAndItsWrite_IsRefused_AndNothingIsFrozen(string closedAs)
    {
        // The dispute read a picked-up order; a writer closes the order before the freeze, and has
        // not armed its refund yet (the gap of a two-step writer).
        var trade = await SeedAsync("pickup_confirmed");
        var key = DisputeKey(trade);
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (_, status) =>
            {
                if (status == "disputed")
                {
                    Task.Run(() => CloseOrderRawAsync(trade, closedAs)).GetAwaiter().GetResult();
                }

                return null;
            }
        };

        var result = await MoneyService(racing).DisputeAsync(Dispute(trade, key), _buyerId);

        // The existing scheduled-refund answer, word for word (the clients read its wording).
        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        result.Error.Message.Should().Be("A refund to the buyer is scheduled for this escrow; it can no longer be disputed.");
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("escrowed");
        Str(escrow, "version").Should().Be("1");
        escrow.Should().NotContainKey("disputeId");
        Str(await OrderAsync(trade), "status").Should().Be(closedAs);
        (await EscrowAuditActionsAsync(trade)).Should().BeEmpty();
        // The key is free: a retry is answered from the rows as they are then.
        (await IdempotencyRepo.GetAsync(key)).Should().BeNull();
        var retry = await MoneyService().DisputeAsync(Dispute(trade, key), _buyerId);
        retry.Error!.Code.Should().NotBe("ESCROW_003");
        Str(await EscrowAsync(trade), "status").Should().Be("escrowed");
    }

    [LocalDynamoDbFact]
    public async Task Dispute_OrderClosedAndArmedBetweenItsReadAndItsWrite_GetsTheScheduledRefundAnswer()
    {
        var trade = await SeedAsync("pickup_confirmed");
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (_, status) =>
            {
                if (status == "disputed")
                {
                    Task.Run(async () =>
                    {
                        var arm = EscrowRepo.BuildRefundArm(trade.EscrowId, trade.EscrowCreatedAt, "admin_refund", DateTime.UtcNow.ToString("o"));
                        var write = await OrderRepo.TryUpdateWithAsync(trade.OrderId,
                            new Dictionary<string, object> { { "status", "cancelled" } }, 4, "pickup_confirmed", new[] { arm });
                        write.Outcome.Should().Be(OrderLinkedWriteOutcome.Written);
                    }).GetAwaiter().GetResult();
                }

                return null;
            }
        };

        var result = await MoneyService(racing).DisputeAsync(Dispute(trade, DisputeKey(trade)), _buyerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Message.Should().Be("A refund to the buyer is scheduled for this escrow; it can no longer be disputed.");
        Str(await EscrowAsync(trade), "status").Should().Be("escrowed");
    }

    [LocalDynamoDbFact]
    public async Task Dispute_OfALiveOrder_FreezesAsBefore()
    {
        var trade = await SeedAsync("pickup_confirmed");

        var result = await MoneyService().DisputeAsync(Dispute(trade, DisputeKey(trade)), _buyerId);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        var escrow = await EscrowAsync(trade);
        Str(escrow, "status").Should().Be("disputed");
        Str(escrow, "version").Should().Be("2");
        Str(escrow, "disputedBy").Should().Be(_buyerId);
        (await EscrowAuditActionsAsync(trade)).Should().Contain("dispute");
        // The order is only checked, never written by the freeze.
        Str(await OrderAsync(trade), "version").Should().Be("4");
    }

    [LocalDynamoDbFact]
    public async Task FreezeTransaction_TheEscrowsOwnRefusalWinsOverTheOrders()
    {
        var trade = await SeedAsync("cancelled");
        await EscrowRepo.UpdateStatusAsync(trade.EscrowId, trade.EscrowCreatedAt, "escrowed", "escrowed",
            new Dictionary<string, object> { { "refundReason", "order_cancelled" } });
        var check = OrderRepo.BuildNotClosedCheck(trade.OrderId, CreatedAt);
        var fields = new Dictionary<string, object> { { "disputeId", "dispute-raw" } };

        // Both conditions fail: the escrow's decides.
        (await EscrowRepo.TryUpdateStatusWithAsync(trade.EscrowId, trade.EscrowCreatedAt, "disputed", "escrowed", fields, new[] { check }))
            .Should().Be(EscrowLinkedWriteOutcome.EscrowRefused);

        // Only the order's fails.
        var closed = await SeedAsync("rejected");
        (await EscrowRepo.TryUpdateStatusWithAsync(closed.EscrowId, closed.EscrowCreatedAt, "disputed", "escrowed", fields,
                new[] { OrderRepo.BuildNotClosedCheck(closed.OrderId, CreatedAt) }))
            .Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);
        Str(await EscrowAsync(closed), "status").Should().Be("escrowed");

        // A missing order refuses too; a live one lets the freeze land with every field.
        (await EscrowRepo.TryUpdateStatusWithAsync(closed.EscrowId, closed.EscrowCreatedAt, "disputed", "escrowed", fields,
                new[] { OrderRepo.BuildNotClosedCheck("order-that-is-not-there", CreatedAt) }))
            .Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);
        var live = await SeedAsync("in_transit");
        (await EscrowRepo.TryUpdateStatusWithAsync(live.EscrowId, live.EscrowCreatedAt, "disputed", "escrowed", fields,
                new[] { OrderRepo.BuildNotClosedCheck(live.OrderId, CreatedAt) }))
            .Should().Be(EscrowLinkedWriteOutcome.Written);
        var frozen = await EscrowAsync(live);
        Str(frozen, "status").Should().Be("disputed");
        Str(frozen, "disputeId").Should().Be("dispute-raw");
        Str(frozen, "version").Should().Be("2");
    }

    [LocalDynamoDbFact]
    public async Task HoldFence_LetsAnOrderCloseOnlyWhileTheEscrowIsStillHeld()
    {
        var trade = await SeedAsync();
        var fence = EscrowRepo.BuildStatusCheck(trade.EscrowId, trade.EscrowCreatedAt, EscrowHoldFields.Status);
        var close = new Dictionary<string, object> { { "status", "rejected" } };

        (await OrderRepo.TryUpdateWithAsync(trade.OrderId, close, 4, "funded", new[] { fence })).Outcome
            .Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);
        Str(await OrderAsync(trade), "status").Should().Be("funded");

        await EscrowRepo.UpdateStatusAsync(trade.EscrowId, trade.EscrowCreatedAt, EscrowHoldFields.Status, "escrowed");
        (await OrderRepo.TryUpdateWithAsync(trade.OrderId, close, 4, "funded", new[] { fence })).Outcome
            .Should().Be(OrderLinkedWriteOutcome.Written);
    }
}

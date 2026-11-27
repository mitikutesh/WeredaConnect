using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-06 against real DynamoDB (LocalStack in CI): the admin stuck-order queue's conditional
/// writes (enter once, never bump the version, a stale reader never clears a newer entry), the
/// extension that never overwrites a scheduled refund, and the sweep filter that keeps escrows held
/// to their 21-day cap out of both sweep windows while a releasable and a refund-scheduled escrow
/// behind 150 of them are still settled.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminQueueDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly List<string> _released = new();
    private readonly List<string> _refunded = new();

    public AdminQueueDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            lock (_released) { _released.Add(call.Arg<ReleaseFundsRequest>().TransactionRef); }
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            lock (_refunded) { _refunded.Add(call.Arg<RefundFundsRequest>().TransactionRef); }
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    private EscrowService Service() => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo,
        cropRepo: CropRepo)
    {
        AlarmLog = _ => { }
    };

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> SeedOrderAsync(string status, int version = 3, string? listingId = null,
        string? cancelReason = null)
    {
        var orderId = NewId("order-esc06");
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(CreatedAt),
            ["status"] = S(status),
            ["buyerId"] = S("buyer-esc06"),
            ["farmerId"] = S("farmer-esc06"),
            ["version"] = N(version.ToString())
        };
        if (listingId != null)
        {
            item["listingId"] = S(listingId);
            item["quantity"] = N("500");
        }

        if (cancelReason != null)
        {
            item["cancelReason"] = S(cancelReason);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
        return orderId;
    }

    private Task<string> SeedEscrowAsync(string? orderId, string expiresAt, params (string Key, string Value)[] extra)
        => SeedEscrowAsync(orderId, expiresAt, CreatedAt, extra);

    private async Task<string> SeedEscrowAsync(
        string? orderId, string expiresAt, string fundedAt, params (string Key, string Value)[] extra)
    {
        var escrowId = orderId != null ? $"escrow-order-{orderId}" : NewId("escrow-esc06");
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(fundedAt),
            ["updatedAt"] = S(fundedAt),
            ["status"] = S("escrowed"),
            ["buyerId"] = S("buyer-esc06"),
            ["farmerId"] = S("farmer-esc06"),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-ESC06"),
            ["expiresAt"] = S(expiresAt),
            ["version"] = N("3")
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
            item["source"] = S("order");
        }

        foreach (var (key, value) in extra)
        {
            item[key] = S(value);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private async Task<Dictionary<string, AttributeValue>> RawOrderAsync(string orderId)
        => (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.Orders,
            KeyConditionExpression = "orderId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = S(orderId) },
            ConsistentRead = true
        })).Items.Single();

    private async Task<List<string>> QueuedOrderIdsAsync()
    {
        var ids = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (items, last) = await OrderRepo.QueryAdminQueueAsync(100, start);
            ids.AddRange(items.Select(i => (string)i["orderId"]));
            start = last;
        }
        while (start != null);

        return ids;
    }

    // ===== The queue's conditional writes =====

    [LocalDynamoDbFact]
    public async Task EnterAdminQueue_OnceOnly_InTheReadStatus_WithoutTouchingTheVersion()
    {
        var orderId = await SeedOrderAsync("accepted", version: 7);
        var repo = OrderRepo;

        (await repo.TryEnterAdminQueueAsync(orderId, "hold_cap", $"escrow-order-{orderId}", "2026-10-22T10:00:00.0000000Z", "pickup_confirmed"))
            .Should().BeFalse("the order is not in the status that was read");
        (await repo.TryEnterAdminQueueAsync(orderId, "hold_cap", $"escrow-order-{orderId}", "2026-10-22T10:00:00.0000000Z", "accepted"))
            .Should().BeTrue();
        (await repo.TryEnterAdminQueueAsync(orderId, "hold_cap", $"escrow-order-{orderId}", "2026-10-22T11:00:00.0000000Z", "accepted"))
            .Should().BeFalse("it is already queued");

        var row = await RawOrderAsync(orderId);
        row["adminQueue"].S.Should().Be("open");
        row["adminQueuedAt"].S.Should().Be("2026-10-22T10:00:00.0000000Z");
        row["adminQueueReason"].S.Should().Be("hold_cap");
        row["version"].N.Should().Be("7");
        row["updatedAt"].S.Should().Be(CreatedAt);
        (await QueuedOrderIdsAsync()).Should().Contain(orderId);

        // A buyer's change carrying the version it read still applies.
        (await repo.UpdateAsync(orderId, new Dictionary<string, object> { { "cancelReason", "x" } }, 7)).IsSuccess.Should().BeTrue();
    }

    [LocalDynamoDbFact]
    public async Task LeaveAdminQueue_AStaleReaderNeverClearsANewerEntry()
    {
        var orderId = await SeedOrderAsync("accepted");
        var repo = OrderRepo;
        await repo.TryEnterAdminQueueAsync(orderId, "hold_cap", $"escrow-order-{orderId}", "2026-10-22T10:00:00.0000000Z", "accepted");

        (await repo.TryLeaveAdminQueueAsync(orderId, "2026-10-21T10:00:00.0000000Z")).Should().BeFalse();
        (await QueuedOrderIdsAsync()).Should().Contain(orderId);

        (await repo.TryLeaveAdminQueueAsync(orderId, "2026-10-22T10:00:00.0000000Z")).Should().BeTrue();
        var row = await RawOrderAsync(orderId);
        row.Should().NotContainKeys("adminQueue", "adminQueuedAt", "adminQueueReason", "adminQueueEscrowId");
        row["version"].N.Should().Be("3");
        (await QueuedOrderIdsAsync()).Should().NotContain(orderId);
    }

    [LocalDynamoDbFact]
    public async Task ExtendExpiry_NeverOverwritesAScheduledRefund_ALockOrAQueuedPayout()
    {
        var plain = await SeedEscrowAsync(null, "2026-10-08T10:00:00.0000000Z");
        var scheduled = await SeedEscrowAsync(null, "2026-10-08T10:00:00.0000000Z", ("refundReason", "order_transport_failed"));
        var locked = await SeedEscrowAsync(null, "2026-10-08T10:00:00.0000000Z", ("settlementLock", "refund"));
        var queued = await SeedEscrowAsync(null, "2026-10-08T10:00:00.0000000Z", ("manualPayout", "open"));
        var repo = EscrowRepo;

        (await repo.TryExtendExpiryAsync(plain, CreatedAt, "2026-10-10T10:00:00.0000000Z")).Should().BeTrue();
        (await repo.TryExtendExpiryAsync(scheduled, CreatedAt, "2026-10-10T10:00:00.0000000Z")).Should().BeFalse();
        (await repo.TryExtendExpiryAsync(locked, CreatedAt, "2026-10-10T10:00:00.0000000Z")).Should().BeFalse();
        (await repo.TryExtendExpiryAsync(queued, CreatedAt, "2026-10-10T10:00:00.0000000Z")).Should().BeFalse();

        (await repo.GetByIdAsync(plain, consistentRead: true))!["expiresAt"].Should().Be("2026-10-10T10:00:00.0000000Z");
        (await repo.GetByIdAsync(scheduled, consistentRead: true))!["expiresAt"].Should().Be("2026-10-08T10:00:00.0000000Z");
    }

    // ===== Capped escrows never starve the sweeps =====

    [LocalDynamoDbFact]
    public async Task OneHundredFiftyCappedEscrows_DoNotStarveTheReleaseOrTheRefundBehindThem()
    {
        // Capped rows sit at the very head of the oldest-first window (expiresAt in 1999).
        for (var n = 0; n < 150; n++)
        {
            await SeedEscrowAsync(null, $"1999-01-01T00:{n / 60:D2}:{n % 60:D2}.0000000Z",
                ("holdCapReachedAt", "1999-01-01T00:00:00.0000000Z"));
        }

        // Capped, then delivered: the confirmed delivery brings it back for the release (ESC-10: the
        // delivery row says delivered too).
        var deliveredId = NewId("del-esc06");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveredId), ["tripId"] = S($"trip-{deliveredId}"), ["status"] = S("delivered")
            }
        });
        var releasable = await SeedEscrowAsync(null, "1999-06-01T00:00:00.0000000Z",
            ("holdCapReachedAt", "1999-01-01T00:00:00.0000000Z"),
            ("deliveryConfirmedAt", DateTime.UtcNow.AddHours(-60).ToString("o")),
            ("disputeWindowEndsAt", DateTime.UtcNow.AddHours(-12).ToString("o")),
            ("deliveryId", deliveredId));

        // Capped, then the buyer cancelled after a transport failure: the scheduled refund brings it back.
        var listingId = NewId("crop-esc06");
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
        var cancelledOrder = await SeedOrderAsync("cancelled", listingId: listingId, cancelReason: "transport_failed_buyer_cancelled");
        var refundable = await SeedEscrowAsync(cancelledOrder, "1999-06-01T01:00:00.0000000Z",
            ("holdCapReachedAt", "1999-01-01T00:00:00.0000000Z"), ("refundReason", "order_transport_failed"));

        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);

        _released.Should().Contain($"release:{releasable}");
        _refunded.Should().Contain($"refund:{refundable}");
        (await EscrowRepo.GetByIdAsync(releasable, consistentRead: true))!["status"].Should().Be("completed");
        (await EscrowRepo.GetByIdAsync(refundable, consistentRead: true))!["status"].Should().Be("refunded");

        // The cancelled order's 5 quintals went back on its listing before the refund.
        var listing = await CropRepo.GetByIdAsync(listingId);
        Convert.ToDouble(listing!["quantity"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(5.0);
    }

    [LocalDynamoDbFact]
    public async Task CappedOrderEscrow_EntersTheQueue_StampsLast_AndLeavesTheSweepWindow()
    {
        var orderId = await SeedOrderAsync("in_transit");
        // Funded more than 21 days ago.
        var escrowId = await SeedEscrowAsync(orderId, "1999-02-01T00:00:00.0000000Z", "2026-09-01T10:00:00.0000000Z");

        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);

        // Funding + 21 days has passed: queued, stamped, not extended, not refunded.
        var row = await RawOrderAsync(orderId);
        row["adminQueue"].S.Should().Be("open");
        row["adminQueueReason"].S.Should().Be("hold_cap");
        var escrow = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        escrow!["status"].Should().Be("escrowed");
        escrow.Should().ContainKey("holdCapReachedAt");
        escrow["expiresAt"].Should().Be("1999-02-01T00:00:00.0000000Z");
        _refunded.Should().NotContain($"refund:{escrowId}");

        (await EscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: "2000-01-01T00:00:00Z", limit: 500))
            .Select(e => (string)e["escrowId"]).Should().NotContain(escrowId);
    }

    // ===== FUP-334: the hold-cap stamp lands only on the entry it read =====

    private Task<EscrowLinkedWriteOutcome> StampAsync(string escrowId, string orderId, string queuedAt, string? entryEscrowId)
        => EscrowRepo.TryUpdateStatusWithAsync(escrowId, "2026-09-01T10:00:00.0000000Z", "escrowed", "escrowed",
            new Dictionary<string, object> { { AdminQueueFields.HoldCapReachedAt, queuedAt } },
            new[] { OrderRepo.BuildAdminQueueEntryCheck(orderId, CreatedAt, queuedAt, entryEscrowId) });

    [LocalDynamoDbFact]
    public async Task HoldCapStampTransaction_LandsOnlyWhileTheEntryReadStands()
    {
        const string first = "2026-10-22T10:00:00.0000000Z";
        const string second = "2026-10-23T10:00:00.0000000Z";
        var orderId = await SeedOrderAsync("accepted");
        var escrowId = await SeedEscrowAsync(orderId, "2099-02-01T00:00:00.0000000Z", "2026-09-01T10:00:00.0000000Z");
        await OrderRepo.TryEnterAdminQueueAsync(orderId, PickupJudgement.ReasonBothPresent, escrowId, first, "accepted");

        // Another queue time, another escrow, or no escrow named: not the entry read.
        (await StampAsync(escrowId, orderId, second, escrowId)).Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);
        (await StampAsync(escrowId, orderId, first, "escrow-someone-else")).Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);
        (await StampAsync(escrowId, orderId, first, null)).Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);

        // The entry read, removed before the stamp (the re-open, a clear): refused, nothing written.
        (await OrderRepo.TryLeaveAdminQueueAsync(orderId, first)).Should().BeTrue();
        (await StampAsync(escrowId, orderId, first, escrowId)).Should().Be(EscrowLinkedWriteOutcome.LinkedItemRefused);
        (await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true)).Should().NotContainKey(AdminQueueFields.HoldCapReachedAt);

        // The entry read, still standing: stamped.
        await OrderRepo.TryEnterAdminQueueAsync(orderId, AdminQueueFields.ReasonHoldCap, escrowId, second, "accepted");
        (await StampAsync(escrowId, orderId, second, escrowId)).Should().Be(EscrowLinkedWriteOutcome.Written);
        (await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true))![AdminQueueFields.HoldCapReachedAt].Should().Be(second);

        // An entry without adminQueueEscrowId (it names the order escrow) passes only while it still has none.
        var unnamedOrder = await SeedOrderAsync("accepted");
        var unnamedEscrow = await SeedEscrowAsync(unnamedOrder, "2099-02-01T00:00:00.0000000Z", "2026-09-01T10:00:00.0000000Z");
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Orders,
            Key = new Dictionary<string, AttributeValue> { ["orderId"] = S(unnamedOrder), ["createdAt"] = S(CreatedAt) },
            UpdateExpression = "SET adminQueue = :open, adminQueuedAt = :at, adminQueueReason = :reason",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":open"] = S("open"),
                [":at"] = S(first),
                [":reason"] = S(AdminQueueFields.ReasonHoldCap)
            }
        });
        (await StampAsync(unnamedEscrow, unnamedOrder, first, null)).Should().Be(EscrowLinkedWriteOutcome.Written);
    }

    [LocalDynamoDbFact]
    public async Task HoldCapStamp_EntryRemovedBetweenTheReadAndTheStamp_EscrowNotCapped_NoAlarm_NextRunQueuesItsOwn()
    {
        // The pickup judgement queued the order an hour ago; its escrow reaches the cap now (funded 2026-09-01).
        var judgedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var orderId = await SeedOrderAsync("accepted");
        var escrowId = await SeedEscrowAsync(orderId, "1999-03-01T00:00:00.0000000Z", "2026-09-01T10:00:00.0000000Z");
        (await OrderRepo.TryEnterAdminQueueAsync(orderId, PickupJudgement.ReasonBothPresent, escrowId, judgedAt, "accepted"))
            .Should().BeTrue();

        // Right before the cron's stamp (after it read the judgement's entry and wrote its audit row), the admin's
        // pickup re-open lands: FUP-287's transaction, which removes the judgement's entry while the escrow has no
        // holdCapReachedAt.
        OrderLinkedWriteResult? reopen = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, newStatus) =>
            {
                if (id == escrowId && newStatus == "escrowed" && reopen == null)
                {
                    reopen = Task.Run(() => OrderRepo.TryUpdateGuardedAsync(orderId,
                        new Dictionary<string, object> { { PickupJudgement.Field, PickupJudgement.Outcome.Pending } },
                        AdminQueueFields.All, 3,
                        new Dictionary<string, string> { { AdminQueueFields.QueuedAt, judgedAt } },
                        Array.Empty<string>(),
                        new[]
                        {
                            OrderRepository.EscrowHoldsMoneyCheck(
                                new EscrowRowKey(_db.Escrows, escrowId, "2026-09-01T10:00:00.0000000Z"), notHoldCapped: true)
                        })).GetAwaiter().GetResult();
                }

                return null;
            }
        };
        var firstRun = new TestLambdaContext();
        var response = await new EscrowCronFunction(Service(), racing, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo).FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, firstRun);
        response.StatusCode.Should().Be(200, response.Body);

        reopen.Should().NotBeNull("the re-open ran between the cron's read and its stamp");
        reopen!.Value.Outcome.Should().Be(OrderLinkedWriteOutcome.Written);
        (await RawOrderAsync(orderId)).Should().NotContainKeys("adminQueue", "adminQueuedAt", "adminQueueReason", "adminQueueEscrowId");

        // Not capped: the escrow stays in the sweep window. No money alarm, one info line.
        var escrow = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        escrow!["status"].Should().Be("escrowed");
        escrow.Should().NotContainKey(AdminQueueFields.HoldCapReachedAt);
        (await EscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: "2000-01-01T00:00:00Z", limit: 500))
            .Select(e => (string)e["escrowId"]).Should().Contain(escrowId);
        var logs = ((TestLambdaLogger)firstRun.Logger).Buffer.ToString();
        logs.Should().Contain(
            $"ORDER_ESCROW_HOLD_CAP_STAMP_SKIPPED escrowId={escrowId} orderId={orderId} reason=queue_entry_changed");
        logs.Split('\n').Where(l => l.Contains(escrowId) || l.Contains(orderId))
            .Should().NotContain(l => l.Contains("ESCROW_SETTLEMENT_FAILED") || l.Contains("ADMIN_QUEUE_OVERDUE"));
        _refunded.Should().NotContain($"refund:{escrowId}");

        // The next run decides again: the order is not queued, so it writes its own hold_cap entry and stamps
        // the escrow against that one.
        var secondRun = new TestLambdaContext();
        response = await new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo).FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, secondRun);
        response.StatusCode.Should().Be(200, response.Body);

        var queued = await RawOrderAsync(orderId);
        queued["adminQueue"].S.Should().Be("open");
        queued["adminQueueReason"].S.Should().Be(AdminQueueFields.ReasonHoldCap);
        queued["adminQueueEscrowId"].S.Should().Be(escrowId);
        var capped = await EscrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        capped![AdminQueueFields.HoldCapReachedAt].Should().Be(queued["adminQueuedAt"].S);
        ((TestLambdaLogger)secondRun.Logger).Buffer.ToString().Should().NotContain($"STAMP_SKIPPED escrowId={escrowId}");
        (await EscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: "2000-01-01T00:00:00Z", limit: 500))
            .Select(e => (string)e["escrowId"]).Should().NotContain(escrowId);
        _refunded.Should().NotContain($"refund:{escrowId}");
    }
}

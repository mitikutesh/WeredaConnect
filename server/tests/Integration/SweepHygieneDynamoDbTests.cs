using System.Collections.Concurrent;
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

/// <summary>Own tables for the ESC-10 acceptance runs, so hundreds of seeded rows never reach the sweeps of other suites.</summary>
[CollectionDefinition(Name)]
public sealed class SweepHygieneDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "SweepHygieneDynamoDb";
}

/// <summary>
/// ESC-10 against real DynamoDB (LocalStack in CI): 150 rows a sweep cannot act on no longer hide the
/// rows behind them, a blocked settlement is audited once however many runs see it, the order and
/// delivery check runs on real rows (a disputed trade is frozen, a pending delivery is cancelled
/// before its refund, goods on the road are held), and the new conditional writes behave. The tables
/// are this class's own; its tests share them, so every assertion is about its own ids.
/// </summary>
[Collection(SweepHygieneDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SweepHygieneDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly ConcurrentDictionary<string, int> _paid = new();
    private readonly List<string> _alarms = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];

    public SweepHygieneDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            _paid.AddOrUpdate(call.Arg<ReleaseFundsRequest>().TransactionRef, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            _paid.AddOrUpdate(call.Arg<RefundFundsRequest>().TransactionRef, 1, (_, n) => n + 1);
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private UserRepository UserRepo => new(Ddb, _db.Users);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private static string Ago(double hours) => DateTime.UtcNow.AddHours(-hours).ToString("o");

    private int Paid(string reference) => _paid.TryGetValue(reference, out var n) ? n : 0;

    private EscrowService Service() => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        UserRepo, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo,
        cropRepo: new CropRepository(Ddb, _db.Crops))
    {
        AlarmLog = line =>
        {
            lock (_alarms)
            {
                _alarms.Add(line);
            }
        }
    };

    private async Task<APIGatewayProxyResponse> SweepAsync()
    {
        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
            transactionAuditRepo: TxRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" },
            new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(5) });
        response.StatusCode.Should().Be(200, response.Body);
        return response;
    }

    private async Task<string> SeedUserAsync(string role, bool phoneVerified)
    {
        var id = $"{role}-esc10-{Guid.NewGuid():N}";
        var created = await UserRepo.CreateUserAsync(new UserDto
        {
            UserId = id,
            PhoneNumber = $"+2519{Math.Abs(id.GetHashCode()) % 100_000_000:D8}",
            DisplayName = id,
            Role = role,
            Woreda = "test-woreda",
            IsPhoneVerified = phoneVerified,
            FaydaVerified = true,
            CreatedAt = "2026-10-01T10:00:00.0000000Z",
            UpdatedAt = "2026-10-01T10:00:00.0000000Z",
            Version = 1
        });
        created.IsSuccess.Should().BeTrue();
        return id;
    }

    private async Task BatchPutAsync(string table, IEnumerable<Dictionary<string, AttributeValue>> items)
    {
        foreach (var chunk in items.Chunk(25))
        {
            var request = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [table] = chunk.Select(i => new WriteRequest { PutRequest = new PutRequest { Item = i } }).ToList()
                }
            };
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var response = await Ddb.BatchWriteItemAsync(request);
                if (response.UnprocessedItems == null || response.UnprocessedItems.Count == 0)
                {
                    break;
                }

                request.RequestItems = response.UnprocessedItems;
            }
        }
    }

    /// <summary>An escrowed escrow; confirmed ones are past their dispute window.</summary>
    private Dictionary<string, AttributeValue> EscrowItem(
        string escrowId, string farmerId, string expiresAt, bool confirmed = false, string? deliveryId = null,
        string? orderId = null, bool orderSourced = false, double createdHoursAgo = 24 * 8, string status = "escrowed")
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(Ago(createdHoursAgo)),
            ["updatedAt"] = S(Ago(createdHoursAgo)),
            ["status"] = S(status),
            ["buyerId"] = S($"buyer-esc10-{_run}"),
            ["farmerId"] = S(farmerId),
            ["amount"] = N("5000"),
            ["commission"] = N("125"),
            ["farmerPayout"] = N("4875"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-ESC10"),
            ["expiresAt"] = S(expiresAt),
            ["version"] = N("2")
        };
        if (confirmed)
        {
            item["deliveryConfirmedAt"] = S(Ago(50));
            item["disputeWindowEndsAt"] = S(Ago(2));
        }

        if (deliveryId != null)
        {
            item["deliveryId"] = S(deliveryId);
        }

        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        if (orderSourced)
        {
            item["source"] = S("order");
        }

        return item;
    }

    private static Dictionary<string, AttributeValue> DeliveryItem(string deliveryId, string status, string? orderId = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S($"trip-{deliveryId}"),
            ["status"] = S(status),
            ["version"] = N("2")
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        return item;
    }

    private Dictionary<string, AttributeValue> OrderItem(string orderId, string status) => new()
    {
        ["orderId"] = S(orderId),
        ["createdAt"] = S(Ago(24 * 9)),
        ["updatedAt"] = S(Ago(24 * 9)),
        ["status"] = S(status),
        ["buyerId"] = S($"buyer-esc10-{_run}"),
        ["farmerId"] = S("farmer-esc10"),
        ["version"] = N("4")
    };

    private async Task<List<Dictionary<string, object>>> TrailAsync(string escrowId)
        => await AuditRepo.GetAuditTrailAsync(escrowId);

    private async Task<int> AuditCountAsync(IEnumerable<string> escrowIds, string action)
    {
        var total = 0;
        foreach (var id in escrowIds)
        {
            total += (await TrailAsync(id)).Count(a => (string)a["action"] == action);
        }

        return total;
    }

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    // ===== Acceptance: 150 blocked rows, the rest still processed, audit volume bounded =====

    [LocalDynamoDbFact]
    public async Task OneHundredFiftyBlockedReleasesAhead_TheReleasesBehindThemArePaid_AndEachBlockIsAuditedOnce()
    {
        var blockedFarmer = await SeedUserAsync("farmer", phoneVerified: false);
        var okFarmer = await SeedUserAsync("farmer", phoneVerified: true);

        // 150 unconfirmed order escrows at the very head of the partition (their orders are missing:
        // the refund sweep skips them every run, before the cap), then 150 confirmed escrows whose
        // farmer the payout gate blocks, then the 3 that may be paid.
        var unconfirmed = Enumerable.Range(0, 150).Select(i => $"escrow-esc10-unc-{_run}-{i:D3}").ToList();
        var blocked = Enumerable.Range(0, 150).Select(i => $"escrow-esc10-kyc-{_run}-{i:D3}").ToList();
        var payable = Enumerable.Range(0, 3).Select(i => $"escrow-esc10-pay-{_run}-{i}").ToList();
        await BatchPutAsync(_db.Escrows, unconfirmed.Select((id, i) => EscrowItem(id, okFarmer,
            $"2000-01-01T00:{i / 60:D2}:{i % 60:D2}.0000000Z", orderId: $"ord-missing-{id}", orderSourced: true, createdHoursAgo: 24)));
        await BatchPutAsync(_db.Escrows, blocked.Select((id, i) => EscrowItem(id, blockedFarmer,
            $"2000-02-01T00:{i / 60:D2}:{i % 60:D2}.0000000Z", confirmed: true, deliveryId: $"del-{id}")));
        await BatchPutAsync(_db.Escrows, payable.Select((id, i) => EscrowItem(id, okFarmer,
            $"2000-03-01T00:00:0{i}.0000000Z", confirmed: true, deliveryId: $"del-{id}")));
        await BatchPutAsync(_db.Deliveries, blocked.Concat(payable).Select(id => DeliveryItem($"del-{id}", "delivered")));

        var first = await SweepAsync();

        first.Body.Should().Contain("\"sweepsTruncated\":\"-\"");
        payable.Should().OnlyContain(id => Paid($"release:{id}") == 1);
        blocked.Should().OnlyContain(id => Paid($"release:{id}") == 0);
        (await AuditCountAsync(blocked, "release_blocked_kyc")).Should().Be(150);

        await SweepAsync();
        await SweepAsync();

        payable.Should().OnlyContain(id => Paid($"release:{id}") == 1);
        (await AuditCountAsync(blocked, "release_blocked_kyc")).Should().Be(150, "a block is audited once, not hourly");
        foreach (var id in blocked.Take(3))
        {
            var row = await EscrowRepo.GetByIdAsync(id, consistentRead: true);
            Str(row, "status").Should().Be("escrowed");
            Str(row, SettlementBlockFields.Reason).Should().Be("release:payout:phone_unverified");
            var audit = (await TrailAsync(id)).Single(a => (string)a["action"] == "release_blocked_kyc");
            audit["timestamp"].Should().Be(Str(row, SettlementBlockFields.ReasonAt));
            audit["metadata"].Should().Be("code=ESCROW_029;reason=phone_unverified");
        }

        // The unconfirmed rows were read every run but never written or audited.
        (await AuditCountAsync(unconfirmed.Take(10), "auto_refund_blocked")).Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task OneHundredFiftyBlockedRefundsAhead_TheThreeRefundsBehindThemArePaid_AndEachBlockIsAuditedOnce()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);

        // 150 expired initiate escrows whose delivery row is missing (blocked, audited once), at the head.
        var blocked = Enumerable.Range(0, 150).Select(i => $"escrow-esc10-nodel-{_run}-{i:D3}").ToList();
        await BatchPutAsync(_db.Escrows, blocked.Select((id, i) => EscrowItem(id, farmer,
            $"2000-04-01T00:{i / 60:D2}:{i % 60:D2}.0000000Z", deliveryId: $"del-gone-{id}")));

        // Behind them, three refunds that may go: an order escrow whose order was cancelled, an
        // initiate escrow whose delivery was cancelled, and one whose delivery never left the farm.
        var orderId = $"ord-esc10-cancelled-{_run}";
        var orderEscrow = $"escrow-order-{orderId}";
        var cancelledDelivery = $"escrow-esc10-cd-{_run}";
        var pendingDelivery = $"escrow-esc10-pd-{_run}";
        var orderEscrowItem = EscrowItem(orderEscrow, farmer, "2000-05-01T00:00:00.0000000Z", orderId: orderId, orderSourced: true);
        orderEscrowItem["refundReason"] = S("order_cancelled");
        await BatchPutAsync(_db.Escrows, new[]
        {
            orderEscrowItem,
            EscrowItem(cancelledDelivery, farmer, "2000-05-01T00:00:01.0000000Z", deliveryId: $"del-{cancelledDelivery}"),
            EscrowItem(pendingDelivery, farmer, "2000-05-01T00:00:02.0000000Z", deliveryId: $"del-{pendingDelivery}")
        });
        await BatchPutAsync(_db.Orders, new[] { OrderItem(orderId, "cancelled") });
        await BatchPutAsync(_db.Deliveries, new[]
        {
            DeliveryItem($"del-{cancelledDelivery}", "cancelled"),
            DeliveryItem($"del-{pendingDelivery}", "pending")
        });

        await SweepAsync();

        foreach (var id in new[] { orderEscrow, cancelledDelivery, pendingDelivery })
        {
            Paid($"refund:{id}").Should().Be(1, id);
            Str(await EscrowRepo.GetByIdAsync(id, consistentRead: true), "status").Should().Be("refunded");
        }

        Str(await DeliveryRepo.GetByIdAsync($"del-{pendingDelivery}", consistentRead: true), "status").Should().Be("cancelled");
        (await AuditCountAsync(blocked, "auto_refund_blocked")).Should().Be(150);
        // Other tests of this class leave rows in the same tables: count this test's lines only.
        var ours = $"escrowId=escrow-esc10-nodel-{_run}";
        lock (_alarms)
        {
            _alarms.Count(l => l.Contains(ours) && l.Contains("action=auto_refund_blocked") && l.Contains("code=refund:trade:delivery_missing"))
                .Should().Be(150);
        }

        await SweepAsync();
        await SweepAsync();

        blocked.Should().OnlyContain(id => Paid($"refund:{id}") == 0);
        (await AuditCountAsync(blocked, "auto_refund_blocked")).Should().Be(150, "a block is audited once, not hourly");
        lock (_alarms)
        {
            _alarms.Count(l => l.Contains(ours) && l.Contains("action=auto_refund_blocked"))
                .Should().Be(150, "the alarm line fires when the reason is first stored");
        }

        foreach (var id in new[] { orderEscrow, cancelledDelivery, pendingDelivery })
        {
            Paid($"refund:{id}").Should().Be(1, id);
        }

        var cancel = (await TxRepo.GetByEntityAsync($"del-{pendingDelivery}")).Single(r => (string)r["action"] == "delivery_cancelled");
        ((string)cancel["metadata"]).Should().Contain("reason=escrow_expired");
    }

    [LocalDynamoDbFact]
    public async Task OneHundredFiftyLegacyVerdictsAhead_TheFarmerWinsVerdictBehindThemIsReleased()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var legacy = Enumerable.Range(0, 150).Select(i => $"escrow-esc10-pr-{_run}-{i:D3}").ToList();
        var items = legacy.Select((id, i) =>
        {
            var item = EscrowItem(id, farmer, $"2000-06-01T00:{i / 60:D2}:{i % 60:D2}.0000000Z", status: "dispute_resolved");
            item["disputeResolution"] = S("partial_refund");
            return item;
        }).ToList();
        var verdict = $"escrow-esc10-fw-{_run}";
        var verdictItem = EscrowItem(verdict, farmer, "2000-07-01T00:00:00.0000000Z", status: "dispute_resolved");
        verdictItem["disputeResolution"] = S("farmer_wins");
        verdictItem["disputeResolvedAt"] = S(Ago(1));
        items.Add(verdictItem);
        await BatchPutAsync(_db.Escrows, items);

        await SweepAsync();

        Paid($"release:{verdict}").Should().Be(1);
        legacy.Should().OnlyContain(id => Paid($"release:{id}") == 0 && Paid($"refund:{id}") == 0);
    }

    // ===== The block reason goes with the settlement =====

    [LocalDynamoDbFact]
    public async Task BlockedRelease_FarmerVerifies_TheNextRunPays_AndTheReasonIsGone()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: false);
        var escrowId = $"escrow-esc10-verify-{_run}";
        await BatchPutAsync(_db.Escrows, new[] { EscrowItem(escrowId, farmer, "2000-08-01T00:00:00.0000000Z", confirmed: true, deliveryId: $"del-{escrowId}") });
        await BatchPutAsync(_db.Deliveries, new[] { DeliveryItem($"del-{escrowId}", "delivered") });

        await SweepAsync();
        Str(await EscrowRepo.GetByIdAsync(escrowId, true), SettlementBlockFields.Reason).Should().Be("release:payout:phone_unverified");

        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = S(farmer) },
            UpdateExpression = "SET isPhoneVerified = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } }
        });
        await SweepAsync();

        Paid($"release:{escrowId}").Should().Be(1);
        var row = await EscrowRepo.GetByIdAsync(escrowId, true);
        Str(row, "status").Should().Be("completed");
        row.Should().NotContainKey(SettlementBlockFields.Reason).And.NotContainKey(SettlementBlockFields.ReasonAt);
        var actions = (await TrailAsync(escrowId)).Select(a => (string)a["action"]).ToList();
        actions.Count(a => a == "release_blocked_kyc").Should().Be(1);
        actions.Should().Contain("release");
    }

    // ===== /escrow/initiate escrows and the trade check on real rows =====

    [LocalDynamoDbFact]
    public async Task InitiateEscrow_GoodsOnTheRoad_IsHeld_AndAtTheCapItsOrderEntersTheQueue()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var heldId = $"escrow-esc10-held-{_run}";
        var cappedId = $"escrow-esc10-capped-{_run}";
        var cappedOrder = $"ord-esc10-capped-{_run}";
        await BatchPutAsync(_db.Escrows, new[]
        {
            EscrowItem(heldId, farmer, "2000-09-01T00:00:00.0000000Z", deliveryId: $"del-{heldId}", createdHoursAgo: 24 * 8),
            EscrowItem(cappedId, farmer, "2000-09-01T00:00:01.0000000Z", deliveryId: $"del-{cappedId}", orderId: cappedOrder,
                createdHoursAgo: 24 * 22)
        });
        await BatchPutAsync(_db.Deliveries, new[]
        {
            DeliveryItem($"del-{heldId}", "inTransit"),
            DeliveryItem($"del-{cappedId}", "pickedUp", cappedOrder)
        });
        await BatchPutAsync(_db.Orders, new[] { OrderItem(cappedOrder, "pickup_confirmed") });

        await SweepAsync();

        Paid($"refund:{heldId}").Should().Be(0);
        Paid($"refund:{cappedId}").Should().Be(0);
        var held = await EscrowRepo.GetByIdAsync(heldId, true);
        DateTime.Parse(Str(held, "expiresAt")!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
            .Should().BeAfter(DateTime.UtcNow.AddHours(47));
        var order = await OrderRepo.GetByIdAsync(cappedOrder, consistentRead: true);
        Str(order, AdminQueueFields.Marker).Should().Be("open");
        Str(order, AdminQueueFields.EscrowId).Should().Be(cappedId);
        Str(await EscrowRepo.GetByIdAsync(cappedId, true), AdminQueueFields.HoldCapReachedAt).Should().NotBeNull();
    }

    [LocalDynamoDbFact]
    public async Task AutoRelease_DeliveryDisputedButMoneyNotFrozen_FreezesTheEscrow_AndTheOrderFollows()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var escrowId = $"escrow-esc10-dsp-{_run}";
        var orderId = $"ord-esc10-dsp-{_run}";
        await BatchPutAsync(_db.Escrows, new[]
        {
            EscrowItem(escrowId, farmer, "2000-10-01T00:00:00.0000000Z", confirmed: true, deliveryId: $"del-{escrowId}", orderId: orderId)
        });
        await BatchPutAsync(_db.Deliveries, new[] { DeliveryItem($"del-{escrowId}", "disputed", orderId) });
        await BatchPutAsync(_db.Orders, new[] { OrderItem(orderId, "delivered") });

        var response = await SweepAsync();

        Paid($"release:{escrowId}").Should().Be(0);
        Str(await EscrowRepo.GetByIdAsync(escrowId, true), "status").Should().Be("disputed");
        Str(await OrderRepo.GetByIdAsync(orderId, consistentRead: true), "status").Should().Be("disputed");
        (await TrailAsync(escrowId)).Should().ContainSingle(a => (string)a["action"] == "dispute");
        response.Body.Should().Contain("\"disputesFrozen\":\"1\"");
    }

    // ===== The new conditional writes =====

    [LocalDynamoDbFact]
    public async Task BlockReason_IsACompareAndSet_AndTheSettledStatusWriteRemovesIt()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var escrowId = $"escrow-esc10-cas-{_run}";
        var lockedId = $"escrow-esc10-cas-locked-{_run}";
        var created = Ago(24 * 8);
        var plain = EscrowItem(escrowId, farmer, "2099-01-01T00:00:00.0000000Z");
        plain["createdAt"] = S(created);
        var locked = EscrowItem(lockedId, farmer, "2099-01-01T00:00:00.0000000Z");
        locked["createdAt"] = S(created);
        locked["settlementLock"] = S("release");
        await BatchPutAsync(_db.Escrows, new[] { plain, locked });
        var repo = EscrowRepo;

        (await repo.TrySetSettlementBlockReasonAsync(escrowId, created, "escrowed", "release:trade:a", "2026-10-10T01:00:00Z")).Should().BeTrue();
        (await repo.TrySetSettlementBlockReasonAsync(escrowId, created, "escrowed", "release:trade:a", "2026-10-10T02:00:00Z")).Should().BeFalse();
        (await repo.TrySetSettlementBlockReasonAsync(escrowId, created, "released", "release:trade:b", "2026-10-10T03:00:00Z")).Should().BeFalse();
        (await repo.TrySetSettlementBlockReasonAsync(lockedId, created, "escrowed", "release:trade:a", "2026-10-10T01:00:00Z")).Should().BeFalse();
        Str(await repo.GetByIdAsync(escrowId, true), SettlementBlockFields.ReasonAt).Should().Be("2026-10-10T01:00:00Z");

        (await repo.TryClearSettlementBlockReasonAsync(escrowId, created, "release:trade:other")).Should().BeFalse();
        (await repo.TrySetSettlementBlockReasonAsync(escrowId, created, "escrowed", "refund:trade:b", "2026-10-10T04:00:00Z")).Should().BeTrue();
        (await repo.TryClearSettlementBlockReasonAsync(escrowId, created, "refund:trade:b")).Should().BeTrue();
        (await repo.GetByIdAsync(escrowId, true)).Should().NotContainKey(SettlementBlockFields.Reason);

        (await repo.TrySetSettlementBlockReasonAsync(escrowId, created, "escrowed", "release:payout:fayda_required", "2026-10-10T05:00:00Z")).Should().BeTrue();
        await repo.UpdateStatusAsync(escrowId, created, "refunded", "escrowed", new Dictionary<string, object> { { "refundedAt", "2026-10-10T06:00:00Z" } });
        var settled = await repo.GetByIdAsync(escrowId, true);
        Str(settled, "status").Should().Be("refunded");
        settled.Should().NotContainKey(SettlementBlockFields.Reason).And.NotContainKey(SettlementBlockFields.ReasonAt);
    }

    [LocalDynamoDbFact]
    public async Task CronPage_FiltersConfirmedAndLockedRows_AndPagesThroughFilteredPages()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var prefix = $"escrow-esc10-pg-{_run}";
        var items = new List<Dictionary<string, AttributeValue>>();
        for (var i = 0; i < 120; i++)
        {
            var item = EscrowItem($"{prefix}-{i:D3}", farmer, $"1990-01-01T00:{i / 60:D2}:{i % 60:D2}.0000000Z", confirmed: i % 2 == 0);
            if (i % 3 == 0)
            {
                item["settlementLock"] = S("refund");
            }

            items.Add(item);
        }

        await BatchPutAsync(_db.Escrows, items);
        var repo = EscrowRepo;

        async Task<List<string>> ReadAllAsync(CronSweepQuery query)
        {
            var ids = new List<string>();
            Dictionary<string, AttributeValue>? start = null;
            var pages = 0;
            do
            {
                var (rows, last) = await repo.QueryCronPageAsync(query, start);
                ids.AddRange(rows.Select(r => (string)r["escrowId"]).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)));
                start = last;
                pages++;
            }
            while (start != null && pages < 100);

            return ids;
        }

        var confirmedUnlocked = await ReadAllAsync(new CronSweepQuery("escrowed", ExpiresAtBefore: "1991-01-01T00:00:00Z",
            DeliveryConfirmed: true, ExcludeLocked: true, PageSize: 10));
        var unconfirmedUnlocked = await ReadAllAsync(new CronSweepQuery("escrowed", ExpiresAtBefore: "1991-01-01T00:00:00Z",
            DeliveryConfirmed: false, ExcludeLocked: true, PageSize: 10));

        confirmedUnlocked.Should().HaveCount(Enumerable.Range(0, 120).Count(i => i % 2 == 0 && i % 3 != 0));
        unconfirmedUnlocked.Should().HaveCount(Enumerable.Range(0, 120).Count(i => i % 2 != 0 && i % 3 != 0));
        confirmedUnlocked.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }
}

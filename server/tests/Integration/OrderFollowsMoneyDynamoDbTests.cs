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

/// <summary>
/// ESC-09 against real DynamoDB (LocalStack in CI): the order status follows the money. Every new
/// condition runs for real: the status-conditioned hop with <c>ADD version</c>, the skip-marker
/// filter of the sweep's page query, the unversioned skip write, the once-only escrow-audit row,
/// and Part B's cancel-before-refund. Own collection and tables: the sweep reads whole status
/// partitions, so no other suite's rows may reach it.
/// </summary>
[Collection(OrderMoneyDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderFollowsMoneyDynamoDbTests
{
    private const string FundedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly List<string> _alarms = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];
    private bool _live;
    private bool _refundRefused;
    private int _releases;
    private int _refunds;

    public OrderFollowsMoneyDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(_ =>
        {
            Interlocked.Increment(ref _releases);
            return _live
                ? new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported }
                : new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-REL-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(_ =>
        {
            if (_refundRefused)
            {
                return new RefundFundsResponse { Success = false, ErrorCode = "TELEBIRR_REFUND_FAILED" };
            }

            Interlocked.Increment(ref _refunds);
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-REF-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);
    private UserRepository UserRepo => new(Ddb, _db.Users);
    private CropRepository CropRepo => new(Ddb, _db.Crops);

    private string FarmerId => $"farmer-esc09-{_run}";
    private string TruckerId => $"trucker-esc09-{_run}";
    private const string AdminId = "admin-esc09";

    private EscrowService Service(IOrderRepository? orderRepo = null) => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        UserRepo, orderRepo ?? OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo, cropRepo: CropRepo)
    {
        AlarmLog = line =>
        {
            lock (_alarms)
            {
                _alarms.Add(line);
            }
        }
    };

    private EscrowCronFunction Cron(EscrowService? service = null, IOrderRepository? orderRepo = null)
        => new(service ?? Service(orderRepo), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, orderRepo ?? OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo);

    private static async Task RunCronAsync(EscrowCronFunction cron)
    {
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" },
            new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4) });
        response.StatusCode.Should().Be(200, response.Body);
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private string NewId(string prefix) => $"{prefix}-{_run}-{Guid.NewGuid():N}"[..Math.Min(60, prefix.Length + 44)];

    private async Task SeedUsersAsync(bool truckerPhoneVerified = true)
    {
        foreach (var (id, role, verified) in new[] { (FarmerId, "farmer", true), (TruckerId, "trucker", truckerPhoneVerified) })
        {
            (await UserRepo.CreateUserAsync(new UserDto
            {
                UserId = id,
                PhoneNumber = $"+2519{Math.Abs(id.GetHashCode()) % 100_000_000:D8}",
                DisplayName = id,
                Role = role,
                Woreda = "test-woreda",
                IsPhoneVerified = verified,
                FaydaVerified = true,
                CreatedAt = FundedAt,
                UpdatedAt = FundedAt,
                Version = 1
            })).IsSuccess.Should().BeTrue();
        }
    }

    private async Task<string> SeedOrderAsync(
        string status, int version = 4, string? deliveryId = null, string? listingId = null, string? createdAt = null)
    {
        var orderId = NewId("ord-esc09");
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S(createdAt ?? "2026-10-01T09:00:00.0000000Z"),
            ["status"] = S(status),
            ["version"] = N(version.ToString()),
            ["buyerId"] = S("buyer-esc09"),
            ["farmerId"] = S(FarmerId),
            ["quantity"] = N("500")
        };
        if (deliveryId != null)
        {
            item["activeDeliveryId"] = S(deliveryId);
        }

        if (listingId != null)
        {
            item["listingId"] = S(listingId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
        return orderId;
    }

    private async Task<string> SeedDeliveryAsync(string status, string? orderId = null)
    {
        var deliveryId = NewId("del-esc09");
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S($"trip-{deliveryId}"),
            ["status"] = S(status),
            ["truckerId"] = S(TruckerId),
            ["version"] = N("2")
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = item });
        return deliveryId;
    }

    /// <summary>An order escrow of 8,000 ETB, releasable now unless the extra fields say otherwise.</summary>
    private async Task<string> SeedEscrowAsync(
        string orderId, string status = "escrowed", string? deliveryId = null, bool truckerLeg = false,
        params (string Key, AttributeValue Value)[] extra)
    {
        var escrowId = $"escrow-order-{orderId}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(FundedAt),
            ["updatedAt"] = S(FundedAt),
            ["status"] = S(status),
            ["source"] = S("order"),
            ["orderId"] = S(orderId),
            ["buyerId"] = S("buyer-esc09"),
            ["farmerId"] = S(FarmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N(truckerLeg ? "6800" : "7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-ESC09"),
            ["expiresAt"] = S("2026-10-08T10:00:00.0000000Z"),
            ["deliveryConfirmedAt"] = S(DateTime.UtcNow.AddHours(-50).ToString("o")),
            ["disputeWindowEndsAt"] = S(DateTime.UtcNow.AddHours(-2).ToString("o")),
            ["version"] = N("3")
        };
        if (deliveryId != null)
        {
            item["deliveryId"] = S(deliveryId);
        }

        if (truckerLeg)
        {
            item["truckerId"] = S(TruckerId);
            item["truckerPayout"] = N("1000");
        }

        foreach (var (key, value) in extra)
        {
            item[key] = value;
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private async Task<Dictionary<string, object>> OrderAsync(string orderId) => (await OrderRepo.GetByIdAsync(orderId, true))!;

    private async Task<string?> OrderStatusAsync(string orderId) => Str(await OrderAsync(orderId), "status");

    private async Task<string?> EscrowStatusAsync(string escrowId) => Str((await EscrowRepo.GetByIdAsync(escrowId, true))!, "status");

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<List<string>> OrderHopsAsync(string orderId)
        => (await TxRepo.GetByTransactionAsync(orderId))
            .Where(r => Str(r, "action") == "order_updated")
            .OrderBy(r => Str(r, "timestamp"))
            .Select(r => $"{Str(r, "fromState")}>{Str(r, "toState")}")
            .ToList();

    private static ReleaseEscrowRequest Release(string escrowId, string trigger = "delivery_confirmed") => new()
    {
        EscrowId = escrowId,
        Trigger = trigger,
        DeliveryId = "-",
        IdempotencyKey = $"system:{escrowId}:release:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
    };

    private static RefundEscrowRequest AdminRefund(string escrowId, bool overrideMismatch = false) => new()
    {
        EscrowId = escrowId,
        Trigger = "admin_override",
        Reason = overrideMismatch ? "The truck was robbed on the road." : null,
        OverrideOrderMismatch = overrideMismatch,
        IdempotencyKey = $"admin:{escrowId}:refund:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
    };

    // ──────────────────────────────────────────────
    // Repository conditions
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task TryAdvanceStatus_LandsOnlyFromTheExpectedStatus_AddsOneToTheVersion_AndNeverCreatesARow()
    {
        var orderId = await SeedOrderAsync("delivered", version: 7);

        (await OrderRepo.TryAdvanceStatusAsync(orderId, "in_transit", "delivered")).Should().BeNull();
        var moved = await OrderRepo.TryAdvanceStatusAsync(orderId, "delivered", "disbursing");
        var missing = await OrderRepo.TryAdvanceStatusAsync($"missing-{_run}", "delivered", "disbursing");

        moved.Should().NotBeNull();
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("disbursing");
        Str(order, "version").Should().Be("8");
        Str(order, "updatedAt").Should().NotBeNullOrEmpty();
        missing.Should().BeNull();
        (await OrderRepo.GetByIdAsync($"missing-{_run}", true)).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task TheSkipMarker_HidesARowAtItsStatus_UntilTheStatusMoves_WithoutTouchingTheVersion()
    {
        var orderId = await SeedOrderAsync("disputed", version: 5, createdAt: "1990-01-01T00:00:00.0000000Z");

        (await OrderRepo.TryMarkMoneySweepSkipAsync(orderId, "delivered", "2026-10-10T10:00:00Z")).Should().BeFalse();
        (await OrderRepo.TryMarkMoneySweepSkipAsync(orderId, "disputed", "2026-10-10T10:00:00Z")).Should().BeTrue();

        Str(await OrderAsync(orderId), "version").Should().Be("5");
        (await AllInPartitionAsync("disputed")).Should().NotContain(orderId);

        await OrderRepo.TryAdvanceStatusAsync(orderId, "disputed", "dispute_resolved");
        (await AllInPartitionAsync("dispute_resolved")).Should().Contain(orderId);
    }

    private async Task<List<string>> AllInPartitionAsync(string status)
    {
        var ids = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (items, next) = await OrderRepo.QueryMoneySweepPageAsync(status, 100, start);
            ids.AddRange(items.Select(i => Str(i, "orderId")!));
            start = next;
        }
        while (start != null);

        return ids;
    }

    // ──────────────────────────────────────────────
    // Every writer that completes an escrow moves the order (C2)
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task Release_WithNoTruckerLeg_SettlesTheOrderInTwoHops()
    {
        await SeedUsersAsync();
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId);

        var result = await Service().ReleaseAsync(Release(escrowId), "system");

        result.IsSuccess.Should().BeTrue();
        (await EscrowStatusAsync(escrowId)).Should().Be("completed");
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("settled");
        Str(order, "version").Should().Be("6");
        (await OrderHopsAsync(orderId)).Should().Equal("delivered>disbursing", "disbursing>settled");
    }

    [LocalDynamoDbFact]
    public async Task ABlockedTruckerLeg_KeepsTheOrderDisbursing_UntilTheRetryPaysIt_ThenSettled()
    {
        await SeedUsersAsync(truckerPhoneVerified: false);
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId, truckerLeg: true);

        (await Service().ReleaseAsync(Release(escrowId), "system")).IsSuccess.Should().BeTrue();
        (await EscrowStatusAsync(escrowId)).Should().Be("released");
        (await OrderStatusAsync(orderId)).Should().Be("disbursing");

        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = S(TruckerId) },
            UpdateExpression = "SET isPhoneVerified = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } }
        });
        // The retry skips a leg that went pending in its own run: age the pending time.
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(escrowId), ["createdAt"] = S(FundedAt) },
            UpdateExpression = "SET truckerPayoutPendingSince = :old",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(DateTime.UtcNow.AddHours(-2).ToString("o")) }
        });
        await RunCronAsync(Cron());

        (await EscrowStatusAsync(escrowId)).Should().Be("completed");
        (await OrderStatusAsync(orderId)).Should().Be("settled");
        (await OrderHopsAsync(orderId)).Should().Equal("delivered>disbursing", "disbursing>settled");
    }

    [LocalDynamoDbFact]
    public async Task LiveGateway_TheOrderIsDisbursingWhileManualPayoutsWaits_AndSettledOnceTheLastLegIsRecorded()
    {
        _live = true;
        await SeedUsersAsync();
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId, truckerLeg: true);

        (await Service().ReleaseAsync(Release(escrowId), "system")).Error!.Code.Should().Be("ESCROW_034");
        (await OrderStatusAsync(orderId)).Should().Be("disbursing");

        (await Service().CompleteManualPayoutAsync(escrowId, ManualPayoutFields.LegFarmer, "TB-FARMER-0001", AdminId, true))
            .IsSuccess.Should().BeTrue();
        (await OrderStatusAsync(orderId)).Should().Be("disbursing");

        (await Service().CompleteManualPayoutAsync(escrowId, ManualPayoutFields.LegTrucker, "TB-TRUCKER-0001", AdminId, true))
            .IsSuccess.Should().BeTrue();
        (await EscrowStatusAsync(escrowId)).Should().Be("completed");
        (await OrderStatusAsync(orderId)).Should().Be("settled");

        // A replay of the recorded leg changes nothing.
        (await Service().CompleteManualPayoutAsync(escrowId, ManualPayoutFields.LegTrucker, "TB-TRUCKER-0001", AdminId, true))
            .IsSuccess.Should().BeTrue();
        (await OrderHopsAsync(orderId)).Should().Equal("delivered>disbursing", "disbursing>settled");
    }

    [LocalDynamoDbFact]
    public async Task ALaggingOrderBehindAConfirmedDelivery_IsAdvancedThenSettled_ThreeHops()
    {
        await SeedUsersAsync();
        var orderId = await SeedOrderAsync("in_transit");
        var escrowId = await SeedEscrowAsync(orderId);

        (await Service().ReleaseAsync(Release(escrowId), "system")).IsSuccess.Should().BeTrue();

        (await OrderHopsAsync(orderId)).Should().Equal("in_transit>delivered", "delivered>disbursing", "disbursing>settled");
        Str(await OrderAsync(orderId), "version").Should().Be("7");
    }

    [LocalDynamoDbFact]
    public async Task CrashRecovery_CompletesAReleasedEscrow_AndSettlesItsOrder()
    {
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId, "released",
            extra: new (string, AttributeValue)[] { ("releasedAt", S(DateTime.UtcNow.AddHours(-1).ToString("o"))), ("telebirrReleaseRef", S("GW-OLD")) });

        await RunCronAsync(Cron());

        (await EscrowStatusAsync(escrowId)).Should().Be("completed");
        (await OrderStatusAsync(orderId)).Should().Be("settled");
    }

    [LocalDynamoDbFact]
    public async Task AnInlineFollowThatFails_IsRepairedByTheSameRunsSweep()
    {
        await SeedUsersAsync();
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId);
        var flaky = new FlakyOrderRepository(OrderRepo, failures: 1);

        (await Service(flaky).ReleaseAsync(Release(escrowId), "system")).IsSuccess.Should().BeTrue();
        (await OrderStatusAsync(orderId)).Should().Be("delivered");

        await RunCronAsync(Cron(Service(flaky), flaky));

        (await OrderStatusAsync(orderId)).Should().Be("settled");
    }

    [LocalDynamoDbFact]
    public async Task TheInlineFollowAndTheSweepRacing_WriteEachHopOnce()
    {
        var orderId = await SeedOrderAsync("delivered");
        var escrowId = await SeedEscrowAsync(orderId, "completed",
            extra: new (string, AttributeValue)[] { ("completedAt", S(DateTime.UtcNow.ToString("o"))) });
        var a = Service();
        var b = Service();
        var row = await OrderAsync(orderId);

        await Task.WhenAll(
            a.FollowMoneyAsync(escrowId, "release"),
            b.ReconcileOrderMoneyAsync(row, DateTime.UtcNow),
            a.FollowMoneyAsync(escrowId, "release"));

        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("settled");
        Str(order, "version").Should().Be("6");
        (await OrderHopsAsync(orderId)).Should().Equal("delivered>disbursing", "disbursing>settled");
    }

    // ──────────────────────────────────────────────
    // Verdict escrows (V1)
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task ASettledFarmerWinsVerdictWhoseOrderLegFailed_IsRepairedToDisputeResolved_NeverSettled()
    {
        var deliveryId = await SeedDeliveryAsync("disputed");
        var orderId = await SeedOrderAsync("delivered", deliveryId: deliveryId);
        await SeedEscrowAsync(orderId, "completed", deliveryId,
            extra: new (string, AttributeValue)[]
            {
                ("disputeResolution", S("farmer_wins")), ("disputeResolvedAt", S("2026-10-05T10:00:00.0000000Z")),
                ("completedAt", S(DateTime.UtcNow.ToString("o")))
            });

        await RunCronAsync(Cron());

        (await OrderStatusAsync(orderId)).Should().Be("dispute_resolved");
        (await OrderHopsAsync(orderId)).Should().NotContain(h => h.Contains("disbursing") || h.Contains("settled"));
    }

    // ──────────────────────────────────────────────
    // Backfill and the sweep (V3)
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task TheFirstRun_BackfillsEveryOrderTheMoneyPassed_PastAHeadOfWaitingRows_AndAThirdRunWritesNothing()
    {
        // 150 orders in their dispute window, older than everything behind them.
        var waiting = new List<string>();
        for (var i = 0; i < 150; i++)
        {
            var id = await SeedOrderAsync("delivered", createdAt: $"2000-01-01T00:00:{i % 60:00}.{i:0000000}Z");
            await SeedEscrowAsync(id);
            waiting.Add(id);
        }

        var paid = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = await SeedOrderAsync("delivered", createdAt: "2026-10-02T00:00:00.0000000Z");
            await SeedEscrowAsync(id, "completed");
            paid.Add(id);
        }

        var disbursing = await SeedOrderAsync("disbursing", createdAt: "2026-10-02T00:00:00.0000000Z");
        await SeedEscrowAsync(disbursing, "completed");
        var lagging = await SeedOrderAsync("in_transit", createdAt: "2026-10-02T00:00:00.0000000Z");
        await SeedEscrowAsync(lagging, "completed");
        var refunded = await SeedOrderAsync("delivered", createdAt: "2026-10-02T00:00:00.0000000Z");
        var refundedEscrow = await SeedEscrowAsync(refunded, "refunded");

        await RunCronAsync(Cron());
        await RunCronAsync(Cron());

        foreach (var id in paid.Append(disbursing).Append(lagging))
        {
            (await OrderStatusAsync(id)).Should().Be("settled", id);
        }

        foreach (var id in waiting.Take(5))
        {
            (await OrderStatusAsync(id)).Should().Be("delivered");
        }

        // The order the refund passed cannot follow: one alarm and one audit row over two runs.
        (await OrderStatusAsync(refunded)).Should().Be("delivered");
        _alarms.Count(a => a.Contains("reason=cannot_follow") && a.Contains(refunded[..20])).Should().Be(1);
        (await AuditRepo.GetAuditTrailAsync(refundedEscrow)).Count(a => Str(a, "action") == OrderMoneyMirror.MismatchAuditAction)
            .Should().Be(1);
        Str(await OrderAsync(refunded), OrderMoneyFields.SweepSkip).Should().Be("delivered");

        var versions = new Dictionary<string, string?>();
        foreach (var id in paid.Append(disbursing).Append(lagging).Append(refunded))
        {
            versions[id] = Str(await OrderAsync(id), "version");
        }

        await RunCronAsync(Cron());

        foreach (var (id, version) in versions)
        {
            Str(await OrderAsync(id), "version").Should().Be(version, id);
        }
    }

    [LocalDynamoDbFact]
    public async Task ACycleSpreadOverRuns_ResumesFromTheSavedIndexKey_AndDeletesTheCursorAtTheEnd()
    {
        // The oldest delivered orders of these tables, so the capped runs reach them first.
        var ids = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var id = await SeedOrderAsync("delivered", createdAt: $"1970-01-01T00:00:0{i}.0000000Z");
            await SeedEscrowAsync(id, "completed");
            ids.Add(id);
        }

        var idempotency = new EscrowIdempotencyRepository(Ddb, _db.Idempotency);
        EscrowCronFunction Capped(int rows) => new(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            idempotency, TxRepo)
        { OrderMoneySweepMaxRows = rows };

        await RunCronAsync(Capped(3));
        (await StatusesAsync(ids)).Should().Equal("settled", "settled", "settled", "delivered", "delivered", "delivered", "delivered");
        (await idempotency.GetAsync(EscrowCronFunction.OrderMoneyCursorKey)).Should().NotBeNull();

        await RunCronAsync(Capped(3));
        (await StatusesAsync(ids)).Count(s => s == "settled").Should().Be(6);

        await RunCronAsync(Capped(2000));
        (await StatusesAsync(ids)).Should().OnlyContain(s => s == "settled");
        (await idempotency.GetAsync(EscrowCronFunction.OrderMoneyCursorKey)).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task TheCursorUpdate_NeverRecreatesADeletedRow_AndKeepsTheCycleStartAndTtl()
    {
        var idempotency = new EscrowIdempotencyRepository(Ddb, _db.Idempotency);
        var key = $"cursor:test:{_run}";

        (await idempotency.TryUpdateExistingAsync(key, "open", "{}")).Should().BeFalse();
        (await idempotency.GetAsync(key)).Should().BeNull();

        (await idempotency.StoreAsync(key, "-", "open", "{\"status\":\"delivered\"}")).Should().BeTrue();
        var stored = (await idempotency.GetAsync(key))!;
        (await idempotency.TryUpdateExistingAsync(key, "open", "{\"status\":\"in_transit\"}")).Should().BeTrue();

        var row = (await idempotency.GetAsync(key))!;
        Str(row, "result").Should().Be("{\"status\":\"in_transit\"}");
        Str(row, "createdAt").Should().Be(Str(stored, "createdAt"));
        Str(row, "expiresAt").Should().Be(Str(stored, "expiresAt"));
    }

    private async Task<List<string?>> StatusesAsync(List<string> ids)
    {
        var statuses = new List<string?>();
        foreach (var id in ids)
        {
            statuses.Add(await OrderStatusAsync(id));
        }

        return statuses;
    }

    // ──────────────────────────────────────────────
    // Part B: admin release and refund
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task AnAdminRefund_OfAFundedOrder_CancelsItFirst_RestoresTheStock_ThenRefunds()
    {
        var listingId = NewId("crop-esc09");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new Dictionary<string, AttributeValue>
            {
                ["cropId"] = S(listingId),
                ["createdAt"] = S(FundedAt),
                ["status"] = S("reserved"),
                ["quantity"] = N("0"),
                ["version"] = N("1")
            }
        });
        var orderId = await SeedOrderAsync("funded", listingId: listingId);
        var escrowId = await SeedEscrowAsync(orderId);

        var result = await Service().RefundAsync(AdminRefund(escrowId), AdminId, "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue();
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be("escrow_refunded");
        (await EscrowStatusAsync(escrowId)).Should().Be("refunded");
        Str((await EscrowRepo.GetByIdAsync(escrowId, true))!, "refundReason").Should().Be("admin_refund");
        var listing = await CropRepo.GetByIdAsync(listingId);
        Convert.ToDouble(listing!["quantity"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(5.0);
    }

    [LocalDynamoDbFact]
    public async Task AnAdminRefund_OfGoodsOnTheRoad_IsRefused_AndWithAnOverride_RefundsAndFlagsTheOrder()
    {
        var deliveryId = await SeedDeliveryAsync("inTransit");
        var orderId = await SeedOrderAsync("in_transit", deliveryId: deliveryId);
        var escrowId = await SeedEscrowAsync(orderId, deliveryId: deliveryId);

        var refused = await Service().RefundAsync(AdminRefund(escrowId), AdminId, "admin", adminGroupVerified: true);

        refused.Error!.HttpStatus.Should().Be(422);
        _refunds.Should().Be(0);

        var overridden = await Service().RefundAsync(AdminRefund(escrowId, overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        overridden.IsSuccess.Should().BeTrue();
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("in_transit");
        Str(order, OrderMoneyFields.RefundOverrideBy).Should().Be(AdminId);
        (await EscrowStatusAsync(escrowId)).Should().Be("refunded");
    }

    [LocalDynamoDbFact]
    public async Task AnOverrideRefundThatFailed_LeavesItsFlag_ButALaterReleaseStillSettlesTheOrder()
    {
        await SeedUsersAsync();
        var deliveryId = await SeedDeliveryAsync("delivered");
        var orderId = await SeedOrderAsync("delivered", deliveryId: deliveryId);
        var escrowId = await SeedEscrowAsync(orderId, deliveryId: deliveryId);
        _refundRefused = true;

        var refund = await Service().RefundAsync(AdminRefund(escrowId, overrideMismatch: true), AdminId, "admin", adminGroupVerified: true);

        refund.IsSuccess.Should().BeFalse();
        Str(await OrderAsync(orderId), OrderMoneyFields.RefundOverrideBy).Should().Be(AdminId);

        (await Service().ReleaseAsync(Release(escrowId), "system")).IsSuccess.Should().BeTrue();

        (await OrderStatusAsync(orderId)).Should().Be("settled");
    }

    [LocalDynamoDbFact]
    public async Task AnAdminRelease_OfAnAcceptedOrderWithNoDelivery_IsRefused_NothingPaid()
    {
        var orderId = await SeedOrderAsync("accepted");
        var escrowId = await SeedEscrowAsync(orderId);

        var result = await Service().ReleaseAsync(Release(escrowId, "admin_override"), AdminId, "admin", adminGroupVerified: true);

        result.Error!.HttpStatus.Should().Be(422);
        _releases.Should().Be(0);
        (await EscrowStatusAsync(escrowId)).Should().Be("escrowed");
    }

    /// <summary>The real repository, whose first <paramref name="failures"/> hops throw (a throttle).</summary>
    private sealed class FlakyOrderRepository(OrderRepository inner, int failures) : IOrderRepository
    {
        private int _failures = failures;

        public Task<Dictionary<string, object>?> TryAdvanceStatusAsync(string orderId, string fromStatus, string toStatus, Dictionary<string, object>? extraFields = null)
            => Interlocked.Decrement(ref _failures) >= 0
                ? throw new ProvisionedThroughputExceededException("throttled")
                : inner.TryAdvanceStatusAsync(orderId, fromStatus, toStatus, extraFields);

        public Task<List<Dictionary<string, object>>> QueryByTripAsync(string tripId) => inner.QueryByTripAsync(tripId);
        public Task<bool> TryStampAsync(string orderId, Dictionary<string, object> fields, IReadOnlyDictionary<string, string> expectedStrings) => inner.TryStampAsync(orderId, fields, expectedStrings);

        public Task<AuthResult<Dictionary<string, object>>> CreateAsync(string orderId, Dictionary<string, object> data) => inner.CreateAsync(orderId, data);
        public Task<AuthResult<Dictionary<string, object>>> UpdateAsync(string orderId, Dictionary<string, object> data, int expectedVersion) => inner.UpdateAsync(orderId, data, expectedVersion);
        public Task<OrderLinkedWriteResult> TryUpdateWithAsync(string orderId, Dictionary<string, object> data, int expectedVersion, string expectedStatus, IReadOnlyList<TransactWriteItem> linkedItems) => inner.TryUpdateWithAsync(orderId, data, expectedVersion, expectedStatus, linkedItems);
        public Task<AuthResult<Dictionary<string, object>>> UpdateUnlessEscrowHeldAsync(string orderId, Dictionary<string, object> data, int expectedVersion, EscrowRowKey escrow) => inner.UpdateUnlessEscrowHeldAsync(orderId, data, expectedVersion, escrow);
        public Task<Dictionary<string, object>?> GetByIdAsync(string orderId) => inner.GetByIdAsync(orderId);
        public Task<Dictionary<string, object>?> GetByIdAsync(string orderId, bool consistentRead) => inner.GetByIdAsync(orderId, consistentRead);
        public Task<Dictionary<string, object>?> TryMarkFundedAsync(string orderId, Dictionary<string, object> fields) => inner.TryMarkFundedAsync(orderId, fields);
        public Task<DeliverySlotWriteResult> TryCreateDeliveryWithSlotAsync(string orderId, string deliveriesTable, Dictionary<string, AttributeValue> deliveryItem, DeliverySlotHolder? replacing = null) => inner.TryCreateDeliveryWithSlotAsync(orderId, deliveriesTable, deliveryItem, replacing);
        public Task ReleaseDeliverySlotAsync(string orderId, string deliveryId) => inner.ReleaseDeliverySlotAsync(orderId, deliveryId);
        public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOrdersAsync(string? buyerId = null, string? farmerId = null, string? truckerId = null, string? status = null, string? listingId = null, int limit = 20, Dictionary<string, AttributeValue>? exclusiveStartKey = null) => inner.QueryOrdersAsync(buyerId, farmerId, truckerId, status, listingId, limit, exclusiveStartKey);
        public Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(string status, string? createdBefore = null) => inner.QueryByStatusForCronAsync(status, createdBefore);
        public Task<bool> TryEnterAdminQueueAsync(string orderId, string reason, string escrowId, string queuedAt, string expectedStatus) => inner.TryEnterAdminQueueAsync(orderId, reason, escrowId, queuedAt, expectedStatus);
        public Task<bool> TryLeaveAdminQueueAsync(string orderId, string seenQueuedAt) => inner.TryLeaveAdminQueueAsync(orderId, seenQueuedAt);
        public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryAdminQueueAsync(int limit, Dictionary<string, AttributeValue>? exclusiveStartKey) => inner.QueryAdminQueueAsync(limit, exclusiveStartKey);
        public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryMoneySweepPageAsync(string status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey) => inner.QueryMoneySweepPageAsync(status, limit, exclusiveStartKey);
        public Task<bool> TryMarkMoneySweepSkipAsync(string orderId, string status, string markedAt) => inner.TryMarkMoneySweepSkipAsync(orderId, status, markedAt);
        public Task<bool> TryHoldForPaymentAsync(string orderId, string holdUntil, string? paymentSubmittedAt) => inner.TryHoldForPaymentAsync(orderId, holdUntil, paymentSubmittedAt);
        public Task<bool> TryMarkPaymentFailedAsync(string orderId, int expectedVersion, string nowIso) => inner.TryMarkPaymentFailedAsync(orderId, expectedVersion, nowIso);
    }
}

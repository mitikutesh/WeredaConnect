using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-05 against real DynamoDB (LocalStack in CI): no double pay and no stuck lock. A timeout or
/// an answer that never came parks the escrow in the manual payout queue with its lock, a crash
/// anywhere after the lock is parked by the recovery step once the lock is 15 minutes old, and no
/// later sweep calls the gateway again. The tables are shared with other suites, so every
/// assertion is about one row and the gateway calls are counted per escrow, never per run.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SettlementRecoveryDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string BuyerId = "buyer-esc05";
    private const string FarmerId = "farmer-esc05";
    private const string TruckerId = "trucker-esc05";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();

    /// <summary>Gateway calls per transaction reference (release:{id}, release:{id}:trucker, refund:{id}).</summary>
    private readonly ConcurrentDictionary<string, int> _calls = new();

    private readonly List<string> _alarms = new();

    public SettlementRecoveryDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            var reference = call.Arg<ReleaseFundsRequest>().TransactionRef;
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            _calls.AddOrUpdate(call.Arg<RefundFundsRequest>().TransactionRef, 1, (_, n) => n + 1);
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);

    private EscrowService Service(IEscrowRepository? escrowRepo = null, List<string>? alarms = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null,
        transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit), cropRepo: CropRepo)
    {
        AlarmLog = line =>
        {
            var sink = alarms ?? _alarms;
            lock (sink)
            {
                sink.Add(line);
            }
        }
    };

    private int Calls(string reference) => _calls.TryGetValue(reference, out var n) ? n : 0;

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private static string Ago(double minutes) => DateTime.UtcNow.AddMinutes(-minutes).ToString("o");

    /// <summary>
    /// An escrow in the sweeps' index (expiresAt set: StatusExpiresIndex is sparse). A
    /// <paramref name="releasable"/> escrow has its delivery confirmed and its dispute window over,
    /// so the auto-release sweep takes it and the auto-refund sweep leaves it alone.
    /// </summary>
    private async Task<string> SeedEscrowAsync(
        string status = "escrowed", bool releasable = true, bool withTrucker = false,
        string? lockOp = null, double? lockedMinutesAgo = null, bool legacyLock = false,
        bool queued = false, string? orderId = null, string? expiresAt = null)
    {
        var escrowId = $"escrow-esc05-{Guid.NewGuid():N}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(Ago(1)),
            ["status"] = S(status),
            ["buyerId"] = S(BuyerId),
            ["farmerId"] = S(FarmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N(withTrucker ? "6800" : "7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-ESC05"),
            ["expiresAt"] = S(expiresAt ?? "2026-10-08T10:00:00.0000000Z"),
            ["version"] = N("3")
        };
        if (releasable)
        {
            item["deliveryConfirmedAt"] = S(Ago(60 * 50));
            item["disputeWindowEndsAt"] = S(Ago(60 * 2));
        }

        if (withTrucker)
        {
            item["truckerPayout"] = N("1000");
            item["truckerId"] = S(TruckerId);
        }

        if (orderId != null)
        {
            item["orderId"] = S(orderId);
            item["source"] = S("order");
        }

        if (lockOp != null)
        {
            item["settlementLock"] = S(lockOp);
            if (legacyLock)
            {
                item["updatedAt"] = S(Ago(lockedMinutesAgo ?? 0));
            }
            else
            {
                item["settlementLockAt"] = S(Ago(lockedMinutesAgo ?? 0));
                item["settlementLockId"] = S($"seeded-{Guid.NewGuid():N}");
            }
        }

        if (queued)
        {
            item["manualPayout"] = S("open");
            item["manualPayoutQueuedAt"] = S("2026-10-02T09:00:00.0000000Z");
            item["manualPayoutOp"] = S(lockOp ?? "release");
            item["manualPayoutReason"] = S("PAYOUT_NOT_SUPPORTED");
            item["manualPayoutFromStatus"] = S(status);
            item["manualFarmerStatus"] = S("pending");
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private async Task AgeTheLockAsync(string escrowId, double minutes) => await Ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = _db.Escrows,
        Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(escrowId), ["createdAt"] = S(CreatedAt) },
        UpdateExpression = "SET settlementLockAt = :at",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":at"] = S(Ago(minutes)) }
    });

    private async Task SweepAsync(EscrowService? service = null)
    {
        var cron = new EscrowCronFunction(service ?? Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<List<string>> AuditActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString()!).ToList();

    private List<string> AlarmsFor(string escrowId)
    {
        lock (_alarms)
        {
            return _alarms.Where(l => l.Contains($"escrowId={escrowId.Replace('_', '-')} ")).ToList();
        }
    }

    private static ReleaseEscrowRequest Release(string escrowId, string hour) => new()
    {
        EscrowId = escrowId,
        Trigger = "delivery_confirmed",
        DeliveryId = "",
        IdempotencyKey = $"system:{escrowId}:auto_release:{hour}"
    };

    private static RefundEscrowRequest Refund(string escrowId, string hour) => new()
    {
        EscrowId = escrowId,
        Trigger = "delivery_timeout",
        Reason = "expired",
        IdempotencyKey = $"system:{escrowId}:auto_refund:{hour}"
    };

    private static void AssertParkedUnknown(Dictionary<string, object> row, string op)
    {
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        Str(row, "manualPayoutOp").Should().Be(op);
        Str(row, "settlementLock").Should().Be(op);
    }

    // ──────────────────────────────────────────────
    // The timeout test: no second payout
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task Timeout_IsParked_TwoMoreSweepsNeverPayAgain_AndTheAdminSettlesIt()
    {
        var escrowId = await SeedEscrowAsync();
        var reference = $"release:{escrowId}";
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == reference)).Returns(_ =>
        {
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout };
        });

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        Calls(reference).Should().Be(1, "a timed-out payout is never sent again");
        var row = await EscrowAsync(escrowId);
        AssertParkedUnknown(row, "release");
        Str(row, "status").Should().Be("escrowed");
        AlarmsFor(escrowId).Should().ContainSingle()
            .Which.Should().Contain("action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT");
        (await AuditActionsAsync(escrowId)).Should().Contain(new[] { "release_failed", "manual_payout_queued" });

        // The admin found the payment in the portal and records its id: the escrow completes.
        var complete = await Service().CompleteManualPayoutAsync(escrowId, "farmer", "TB-FOUND-1", "admin-it", adminGroupVerified: true);

        complete.IsSuccess.Should().BeTrue();
        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        await SweepAsync();
        Calls(reference).Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task GatewayCallThrows_IsParked_NotRetried()
    {
        var escrowId = await SeedEscrowAsync();
        var reference = $"release:{escrowId}";
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == reference)).Returns<ReleaseFundsResponse>(_ =>
        {
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            throw new TaskCanceledException("the HTTP call timed out");
        });

        await SweepAsync();
        await SweepAsync();

        Calls(reference).Should().Be(1);
        AssertParkedUnknown(await EscrowAsync(escrowId), "release");
        AlarmsFor(escrowId).Should().ContainSingle().Which.Should().Contain("code=GATEWAY_EXCEPTION:TaskCanceledException");
    }

    // ──────────────────────────────────────────────
    // Crash windows
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task CrashAfterTheGatewayPaid_BeforeTheStatusWrite_IsParkedByRecovery_NoSecondPayout()
    {
        var escrowId = await SeedEscrowAsync();
        var reference = $"release:{escrowId}";
        var crashing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, status) => id == escrowId && status == "released"
                ? throw new AmazonDynamoDBException("connection reset") : null
        };

        var act = () => Service(crashing).ReleaseAsync(Release(escrowId, "2026100910"), EscrowService.SystemActor);

        await act.Should().ThrowAsync<AmazonDynamoDBException>();
        Calls(reference).Should().Be(1);
        var stuck = await EscrowAsync(escrowId);
        Str(stuck, "status").Should().Be("escrowed");
        Str(stuck, "settlementLock").Should().Be("release");
        Str(stuck, "manualPayout").Should().BeNull();

        // Within 15 minutes the lock is left alone (its owner could still be running).
        await SweepAsync();
        Str(await EscrowAsync(escrowId), "manualPayout").Should().BeNull();

        await AgeTheLockAsync(escrowId, 20);
        await SweepAsync();
        await SweepAsync();

        Calls(reference).Should().Be(1, "the farmer was paid once and is never paid again");
        AssertParkedUnknown(await EscrowAsync(escrowId), "release");
        var actions = await AuditActionsAsync(escrowId);
        actions.Count(a => a == "stale_lock_parked").Should().Be(1);
        actions.Count(a => a == "manual_payout_queued").Should().Be(1);
        AlarmsFor(escrowId).Should().ContainSingle().Which.Should().Contain("action=stale_lock_parked outcome=unknown code=release");
    }

    [LocalDynamoDbFact]
    public async Task CrashAfterTheLock_BeforeTheCall_IsParked_AndNoGatewayCallIsEverMade()
    {
        var escrowId = await SeedEscrowAsync(lockOp: "release", lockedMinutesAgo: 20);

        await SweepAsync();
        await SweepAsync();

        Calls($"release:{escrowId}").Should().Be(0);
        AssertParkedUnknown(await EscrowAsync(escrowId), "release");
    }

    [LocalDynamoDbFact]
    public async Task ARunningSettlement_AndAQueuedPayout_AreLeftAlone()
    {
        var inFlight = await SeedEscrowAsync(lockOp: "release", lockedMinutesAgo: 5);
        var queued = await SeedEscrowAsync(lockOp: "release", lockedMinutesAgo: 600, queued: true);

        await SweepAsync();

        Calls($"release:{inFlight}").Should().Be(0);
        Calls($"release:{queued}").Should().Be(0);
        Str(await EscrowAsync(inFlight), "manualPayout").Should().BeNull();
        Str(await EscrowAsync(inFlight), "settlementLock").Should().Be("release");
        var stillQueued = await EscrowAsync(queued);
        Str(stillQueued, "manualPayoutReason").Should().Be("PAYOUT_NOT_SUPPORTED");
        Str(stillQueued, "manualPayoutQueuedAt").Should().Be("2026-10-02T09:00:00.0000000Z");
        (await AuditActionsAsync(queued)).Should().NotContain("stale_lock_parked");
    }

    [LocalDynamoDbFact]
    public async Task ALockFromBeforeEsc05_IsAgedByUpdatedAt_AndParked()
    {
        var escrowId = await SeedEscrowAsync(status: "dispute_resolved", releasable: false, lockOp: "refund",
            lockedMinutesAgo: 120, legacyLock: true);

        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        AssertParkedUnknown(row, "refund");
        Str(row, "manualBuyerStatus").Should().Be("pending");
        Calls($"refund:{escrowId}").Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task ALegacyLockOnADisputedEscrow_IsParked_AndAVerdictIsThenRefused()
    {
        // Before 1.9.0 a release could lock a disputed escrow; such a lock blocked the verdict for
        // good. Parked, the escrow belongs to the admin queue, and the verdict says so (ADMIN_013).
        var escrowId = await SeedEscrowAsync(status: "disputed", releasable: false, lockOp: "release",
            lockedMinutesAgo: 600, legacyLock: true);

        await SweepAsync();

        AssertParkedUnknown(await EscrowAsync(escrowId), "release");
        var admin = new AdminFunction(Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit);
        var verdict = await admin.FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/disputes/{escrowId}/resolve",
            Body = JsonSerializer.Serialize(new { resolution = "buyer_wins", action = "refund" }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-it" }, { "cognito:groups", "admin" } }
            }
        }, new TestLambdaContext());

        verdict.StatusCode.Should().Be(409);
        verdict.Body.Should().Contain("ADMIN_013");
        Str(await EscrowAsync(escrowId), "status").Should().Be("disputed");
    }

    // ──────────────────────────────────────────────
    // Refunds
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task RefundStatusWrite_IsRetried_LikeTheReleaseWrite()
    {
        var escrowId = await SeedEscrowAsync(releasable: false);
        var failures = 0;
        var flaky = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, status) => id == escrowId && status == "refunded" && failures++ < 2
                ? throw new ProvisionedThroughputExceededException("throttled") : null
        };

        var result = await Service(flaky).RefundAsync(Refund(escrowId, "2026100910"), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        failures.Should().Be(3);
        Calls($"refund:{escrowId}").Should().Be(1);
        Str(await EscrowAsync(escrowId), "status").Should().Be("refunded");
    }

    [LocalDynamoDbFact]
    public async Task RefundCrashAfterTheGatewayPaid_IsParked_AndTheAdminCloseRefundsItOnce()
    {
        var escrowId = await SeedEscrowAsync(releasable: false);
        var crashing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, status) => id == escrowId && status == "refunded"
                ? throw new AmazonDynamoDBException("down") : null
        };

        var act = () => Service(crashing).RefundAsync(Refund(escrowId, "2026100910"), EscrowService.SystemActor);
        await act.Should().ThrowAsync<AmazonDynamoDBException>();
        await AgeTheLockAsync(escrowId, 30);
        await SweepAsync();

        AssertParkedUnknown(await EscrowAsync(escrowId), "refund");
        (await Service().CompleteManualPayoutAsync(escrowId, "buyer", "TB-REFUND-FOUND", "admin-it", adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();
        await SweepAsync();

        Str(await EscrowAsync(escrowId), "status").Should().Be("refunded");
        Calls($"refund:{escrowId}").Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // The trucker leg
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task TruckerCallThrowsAfterTheFarmerWasPaid_QueuesOnlyTheTrucker_AndNeitherIsPaidTwice()
    {
        var escrowId = await SeedEscrowAsync(withTrucker: true);
        var truckerRef = $"release:{escrowId}:trucker";
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == truckerRef)).Returns<ReleaseFundsResponse>(_ =>
        {
            _calls.AddOrUpdate(truckerRef, 1, (_, n) => n + 1);
            throw new HttpRequestException("connection reset");
        });

        await SweepAsync();
        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        AssertParkedUnknown(row, "release");
        Str(row, "manualFarmerStatus").Should().Be("paid");
        Str(row, "manualFarmerPaidBy").Should().Be("gateway");
        Str(row, "manualTruckerStatus").Should().Be("pending");
        (await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-TRUCKER-FOUND", "admin-it", adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();

        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        Calls($"release:{escrowId}").Should().Be(1);
        Calls(truckerRef).Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // Races and real conditions
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task TwoRecoveriesAtOnce_ParkTheEscrowOnce_AndTheLoserIsSilent()
    {
        for (var i = 0; i < 5; i++)
        {
            var escrowId = await SeedEscrowAsync(lockOp: "refund", lockedMinutesAgo: 45, releasable: false);
            var row = await EscrowAsync(escrowId);
            var alarmsA = new List<string>();
            var alarmsB = new List<string>();

            var outcomes = await Task.WhenAll(
                Service(alarms: alarmsA).ParkStaleSettlementAsync(row, DateTime.UtcNow),
                Service(alarms: alarmsB).ParkStaleSettlementAsync(row, DateTime.UtcNow));

            outcomes.Count(o => o == EscrowService.StaleSettlementOutcome.Parked).Should().Be(1, $"run {i}");
            outcomes.Count(o => o == EscrowService.StaleSettlementOutcome.Lost).Should().Be(1, $"run {i}");
            (alarmsA.Count + alarmsB.Count).Should().Be(1, $"run {i}: only the winner alarms");
            var actions = await AuditActionsAsync(escrowId);
            actions.Count(a => a == "stale_lock_parked").Should().Be(1);
            actions.Count(a => a == "manual_payout_queued").Should().Be(1);
            AssertParkedUnknown(await EscrowAsync(escrowId), "refund");
        }
    }

    [LocalDynamoDbFact]
    public async Task RealConditions_TheParkIsFencedOnTheLock_TheLockCarriesItsTimeAndId_AndAQueuedLockIsNeverReleased()
    {
        var escrowId = await SeedEscrowAsync(releasable: false);
        (await EscrowRepo.TryAcquireSettlementLockAsync(escrowId, CreatedAt, "refund", "escrowed")).Should().BeTrue();
        var locked = await EscrowAsync(escrowId);
        var lockId = Str(locked, "settlementLockId");
        lockId.Should().NotBeNullOrEmpty();
        DateTime.TryParse(Str(locked, "settlementLockAt"), out _).Should().BeTrue();
        var legs = new List<ManualPayoutLegSeed> { new("buyer", "pending", null, null, null) };

        (await EscrowRepo.TryQueueStaleSettlementAsync(escrowId, CreatedAt, "refund", "escrowed", legs, Ago(0), "another-lock", "p1"))
            .Should().BeFalse("a different lock id is someone else's settlement");
        (await EscrowRepo.TryQueueStaleSettlementAsync(escrowId, CreatedAt, "refund", "escrowed", legs, Ago(0), null, "p2"))
            .Should().BeFalse("the row has a lock id, so it is not a legacy lock");
        (await EscrowRepo.TryQueueStaleSettlementAsync(escrowId, CreatedAt, "refund", "dispute_resolved", legs, Ago(0), lockId, "p3"))
            .Should().BeFalse("the status moved");
        (await EscrowRepo.TryQueueStaleSettlementAsync(escrowId, CreatedAt, "refund", "escrowed", legs, Ago(0), lockId, "p4"))
            .Should().BeTrue();
        Str(await EscrowAsync(escrowId), "manualPayoutParkId").Should().Be("p4");

        await EscrowRepo.ReleaseSettlementLockAsync(escrowId, CreatedAt);

        Str(await EscrowAsync(escrowId), "settlementLock").Should().Be("refund", "the queue owns the lock now");
        Str(await EscrowAsync(escrowId), "settlementLockId").Should().Be(lockId);
    }

    [LocalDynamoDbFact]
    public async Task ReleasingAFreeLock_RemovesItsTimeAndId()
    {
        var escrowId = await SeedEscrowAsync(releasable: false);
        (await EscrowRepo.TryAcquireSettlementLockAsync(escrowId, CreatedAt, "release", "escrowed")).Should().BeTrue();

        await EscrowRepo.ReleaseSettlementLockAsync(escrowId, CreatedAt);

        var row = await EscrowAsync(escrowId);
        Str(row, "settlementLock").Should().BeNull();
        Str(row, "settlementLockAt").Should().BeNull();
        Str(row, "settlementLockId").Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task AutoRefund_LeavesTheOrderAlone_WhileASettlementHoldsTheEscrow()
    {
        var orderId = $"order-esc05-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("funded"),
                ["buyerId"] = S(BuyerId),
                ["farmerId"] = S(FarmerId),
                ["version"] = N("2")
            }
        });
        var escrowId = await SeedEscrowAsync(releasable: false, lockOp: "refund", lockedMinutesAgo: 2, orderId: orderId);

        await SweepAsync();

        var order = (await OrderRepo.GetByIdAsync(orderId, true))!;
        Str(order, "status").Should().Be("funded");
        Calls($"refund:{escrowId}").Should().Be(0);
        Str(await EscrowAsync(escrowId), "manualPayout").Should().BeNull();
    }
}

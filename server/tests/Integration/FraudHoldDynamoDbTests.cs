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

/// <summary>FRD-03: its own tables, because its sweep runs hold or settle every expired row in the partition.</summary>
[CollectionDefinition(Name)]
public sealed class FraudHoldDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "FraudHoldDynamoDb";
}

/// <summary>
/// FRD-03 against real DynamoDB (LocalStack in CI): the conditions the fraud hold depends on, evaluated by
/// DynamoDB itself. The hold write loses to a settlement lock and a queued payout; the auto-refund hold is
/// one transaction fenced on the order's version, so an accept in between holds nothing and the next run
/// pushes the expiry; the lock takes <c>held</c> only for the admin exit; a held escrow is never selected
/// again by the sweeps; an admin exit and a sweep at the same time pay once; the dispute path of a
/// <c>heldFor: release</c> refund writes the real order and delivery hops; a queued admin exit closes from held.
/// </summary>
[Collection(FraudHoldDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FraudHoldDynamoDbTests
{
    private const string Reason = "Seller confirmed as fraudulent by support.";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly ConcurrentDictionary<string, int> _gatewayCalls = new();

    public FraudHoldDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(ci =>
        {
            _gatewayCalls.AddOrUpdate(ci.Arg<ReleaseFundsRequest>().TransactionRef, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-REL-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(ci =>
        {
            _gatewayCalls.AddOrUpdate(ci.Arg<RefundFundsRequest>().TransactionRef, 1, (_, n) => n + 1);
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-REF-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);
    private CropRepository CropRepo => new(Ddb, _db.Crops);
    private UserRepository UserRepo => new(Ddb, _db.Users);

    private EscrowService Service(IEscrowRepository? escrows = null) => new(
        escrows ?? EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        UserRepo, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo, cropRepo: CropRepo);

    private EscrowCronFunction Cron(IEscrowRepository? escrows = null)
    {
        var repo = escrows ?? EscrowRepo;
        return new EscrowCronFunction(Service(repo), repo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo,
            transactionAuditRepo: TxRepo)
        {
            RiskGate = new PartySuspensionGate(UserRepo),
            FraudHoldEnabled = true,
            OrdersTableName = _db.Orders
        };
    }

    private async Task SweepAsync(IEscrowRepository? escrows = null)
    {
        var response = await Cron(escrows).FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private static string Ago(TimeSpan span) => (DateTime.UtcNow - span).ToString("o");

    /// <summary>A buyer and a farmer of their own, phone-verified; <paramref name="suspended"/> names the one suspended.</summary>
    private async Task<(string Buyer, string Farmer)> SeedPartiesAsync(string? suspended = null)
    {
        var buyer = NewId("buyer-it");
        var farmer = NewId("farmer-it");
        foreach (var (id, role) in new[] { (buyer, "buyer"), (farmer, "farmer") })
        {
            await Ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _db.Users,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["userId"] = S(id),
                    ["phoneNumber"] = S("+251900000001"),
                    ["role"] = S(role),
                    ["woreda"] = S("w"),
                    ["isPhoneVerified"] = new() { BOOL = true },
                    ["suspended"] = new() { BOOL = role == suspended },
                    ["createdAt"] = S("2026-01-01T00:00:00Z"),
                    ["updatedAt"] = S("2026-01-01T00:00:00Z"),
                    ["version"] = N("1")
                }
            });
        }

        return (buyer, farmer);
    }

    private async Task<(string EscrowId, string CreatedAt)> SeedEscrowAsync(
        (string Buyer, string Farmer) parties, string status, string? escrowId = null, string? orderId = null,
        string? deliveryId = null, string? createdAt = null, params (string Key, string Value)[] extra)
    {
        escrowId ??= NewId("escrow-it");
        createdAt ??= Ago(TimeSpan.FromDays(8));
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(createdAt),
            ["status"] = S(status),
            ["buyerId"] = S(parties.Buyer),
            ["farmerId"] = S(parties.Farmer),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-IT"),
            ["version"] = N("3"),
            ["expiresAt"] = S(Ago(TimeSpan.FromHours(2)))
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        if (deliveryId != null)
        {
            item["deliveryId"] = S(deliveryId);
        }

        foreach (var (key, value) in extra)
        {
            item[key] = S(value);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return (escrowId, createdAt);
    }

    private async Task<string> SeedOrderAsync(
        (string Buyer, string Farmer) parties, string status, int version, string? activeDeliveryId = null)
    {
        var orderId = NewId("order-it");
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S("2026-10-01T09:00:00.0000000Z"),
            ["status"] = S(status),
            ["buyerId"] = S(parties.Buyer),
            ["farmerId"] = S(parties.Farmer),
            ["version"] = N(version.ToString())
        };
        if (activeDeliveryId != null)
        {
            item["activeDeliveryId"] = S(activeDeliveryId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
        return orderId;
    }

    private async Task<string> SeedDeliveryAsync(string status, string? orderId = null)
    {
        var deliveryId = NewId("delivery-it");
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S("trip-it"),
            ["status"] = S(status),
            ["createdAt"] = S("2026-10-02T09:00:00.0000000Z"),
            ["version"] = N("2")
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = item });
        return deliveryId;
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> OrderAsync(string id) => (await OrderRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> DeliveryAsync(string id) => (await DeliveryRepo.GetByIdAsync(id, true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private int Calls(string transactionRef) => _gatewayCalls.GetValueOrDefault(transactionRef);

    private static EscrowHoldRequest HoldRequest(string from = "escrowed", string heldFor = "refund")
        => new(from, heldFor, "account_suspended", DateTime.UtcNow.ToString("o"));

    private static (string Key, string Value)[] HeldFields(string heldFor, string from = "escrowed") =>
    [
        ("holdReason", "account_suspended"), ("heldAt", "2026-10-20T10:00:00.0000000Z"), ("heldBy", "system"),
        ("heldFromStatus", from), ("heldFor", heldFor)
    ];

    // ──────────────────────────────────────────────
    // The hold write and the lock
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task Hold_HoldsAPlainRow_AndLosesToASettlementLockOrAQueuedPayout()
    {
        var parties = await SeedPartiesAsync();
        var (plain, plainAt) = await SeedEscrowAsync(parties, "escrowed");
        var (locked, lockedAt) = await SeedEscrowAsync(parties, "escrowed", extra: ("settlementLock", "release"));
        var (queued, queuedAt) = await SeedEscrowAsync(parties, "escrowed", extra: ("manualPayout", "open"));
        var (moved, movedAt) = await SeedEscrowAsync(parties, "released");

        var hold = HoldRequest();
        (await EscrowRepo.TryHoldAsync(plain, plainAt, hold)).Should().Be(EscrowHoldWrite.Held);
        (await EscrowRepo.TryHoldAsync(locked, lockedAt, HoldRequest())).Should().Be(EscrowHoldWrite.EscrowChanged);
        (await EscrowRepo.TryHoldAsync(queued, queuedAt, HoldRequest())).Should().Be(EscrowHoldWrite.EscrowChanged);
        (await EscrowRepo.TryHoldAsync(moved, movedAt, HoldRequest())).Should().Be(EscrowHoldWrite.EscrowChanged);
        // Our own hold again (a write whose answer was lost) counts as held.
        (await EscrowRepo.TryHoldAsync(plain, plainAt, hold)).Should().Be(EscrowHoldWrite.Held);

        var row = await EscrowAsync(plain);
        Str(row, "status").Should().Be("held");
        Str(row, "holdReason").Should().Be("account_suspended");
        Str(row, "heldBy").Should().Be("system");
        Str(row, "heldFromStatus").Should().Be("escrowed");
        Str(row, "heldFor").Should().Be("refund");
        Str(row, "heldAt").Should().Be(hold.HeldAt);
        Str(row, "version").Should().Be("4");
        Str(await EscrowAsync(locked), "status").Should().Be("escrowed");
        Str(await EscrowAsync(queued), "status").Should().Be("escrowed");
        Str(await EscrowAsync(moved), "status").Should().Be("released");
    }

    [LocalDynamoDbFact]
    public async Task Lock_TakesHeldOnlyForTheAdminExit()
    {
        var parties = await SeedPartiesAsync();
        var (held, at) = await SeedEscrowAsync(parties, "held", extra: HeldFields("release"));

        (await EscrowRepo.TryAcquireSettlementLockAsync(held, at, "release", "held")).Should().BeFalse();
        Str(await EscrowAsync(held), "settlementLock").Should().BeNull();
        (await EscrowRepo.TryAcquireSettlementLockAsync(held, at, "release", "held", allowHeld: true)).Should().BeTrue();
        Str(await EscrowAsync(held), "settlementLock").Should().Be("release");
    }

    [LocalDynamoDbFact]
    public async Task FencedHold_AnOrderThatMovedOn_CancelsTheTransaction_NothingHeld()
    {
        var parties = await SeedPartiesAsync();
        var orderId = await SeedOrderAsync(parties, "funded", 4);
        var (escrowId, at) = await SeedEscrowAsync(parties, "escrowed", $"escrow-order-{orderId}", orderId);
        (await OrderRepo.UpdateAsync(orderId, new Dictionary<string, object> { { "status", "accepted" } }, 4)).IsSuccess.Should().BeTrue();

        var outcome = await EscrowRepo.TryHoldAsync(escrowId, at, HoldRequest(),
            new OrderFence(_db.Orders, orderId, "2026-10-01T09:00:00.0000000Z", 4, "funded"));

        outcome.Should().Be(EscrowHoldWrite.OrderChanged);
        Str(await EscrowAsync(escrowId), "status").Should().Be("escrowed");

        // Fenced on what is there now, it holds.
        var now = await EscrowRepo.TryHoldAsync(escrowId, at, HoldRequest(),
            new OrderFence(_db.Orders, orderId, "2026-10-01T09:00:00.0000000Z", 5, "accepted"));
        now.Should().Be(EscrowHoldWrite.Held);
    }

    // ──────────────────────────────────────────────
    // The sweeps
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task ASweep_HoldsASuspendedFarmersEscrow_NeverSelectsItAgain_AndTheAdminReleasesItOnce()
    {
        var parties = await SeedPartiesAsync(suspended: "farmer");
        var deliveryId = await SeedDeliveryAsync("delivered");
        var (escrowId, _) = await SeedEscrowAsync(parties, "escrowed", deliveryId: deliveryId,
            extra: [("deliveryConfirmedAt", Ago(TimeSpan.FromDays(3))), ("disputeWindowEndsAt", Ago(TimeSpan.FromDays(1)))]);

        await SweepAsync();
        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("held");
        Str(row, "heldFor").Should().Be("release");
        Str(row, "heldFromStatus").Should().Be("escrowed");
        Calls($"release:{escrowId}").Should().Be(0);
        var trail = await AuditRepo.GetAuditTrailAsync(escrowId);
        trail.Count(a => a["action"].ToString() == "fraud_hold").Should().Be(1);
        (await TxRepo.GetByTransactionAsync(escrowId)).Should().ContainSingle(r => r["action"].ToString() == "fraud_hold");

        var release = await Service().ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            DeliveryId = deliveryId,
            IdempotencyKey = $"admin-it:{escrowId}:release:1",
            Reason = Reason
        }, "admin-it", "admin", adminGroupVerified: true);

        release.IsSuccess.Should().BeTrue(release.Error?.Message);
        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        Calls($"release:{escrowId}").Should().Be(1);
        (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString())
            .Should().ContainInOrder("fraud_hold", "fraud_hold_release", "release", "complete");
    }

    [LocalDynamoDbFact]
    public async Task AnAcceptBetweenTheRefundSweepsReadAndTheHold_HoldsNothing_AndTheNextRunPushesTheExpiry()
    {
        var parties = await SeedPartiesAsync(suspended: "buyer");
        var orderId = await SeedOrderAsync(parties, "funded", 4);
        var (escrowId, _) = await SeedEscrowAsync(parties, "escrowed", $"escrow-order-{orderId}", orderId,
            extra: ("source", "order"));
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            // The farmer's accept lands after the sweep read the order and before the hold write.
            OnHold = async id =>
            {
                if (id == escrowId)
                {
                    (await OrderRepo.UpdateAsync(orderId, new Dictionary<string, object> { { "status", "accepted" } }, 4))
                        .IsSuccess.Should().BeTrue();
                }
            }
        };

        await SweepAsync(racing);

        var escrow = await EscrowAsync(escrowId);
        Str(escrow, "status").Should().Be("escrowed");
        Str(await OrderAsync(orderId), "status").Should().Be("accepted");
        Calls($"refund:{escrowId}").Should().Be(0);
        var expiredAt = Str(escrow, "expiresAt");

        await SweepAsync();

        var after = await EscrowAsync(escrowId);
        Str(after, "status").Should().Be("escrowed");
        DateTime.Parse(Str(after, "expiresAt")!).ToUniversalTime().Should().BeAfter(DateTime.UtcNow);
        Str(after, "expiresAt").Should().NotBe(expiredAt);
        Calls($"refund:{escrowId}").Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task AStaleFundedOrderOfASuspendedBuyer_IsHeldFenced_AndTheAdminRefundCancelsItThenRefundsOnce()
    {
        var parties = await SeedPartiesAsync(suspended: "buyer");
        var orderId = await SeedOrderAsync(parties, "funded", 4);
        var (escrowId, _) = await SeedEscrowAsync(parties, "escrowed", $"escrow-order-{orderId}", orderId,
            extra: ("source", "order"));

        await SweepAsync();

        Str(await EscrowAsync(escrowId), "status").Should().Be("held");
        Str(await OrderAsync(orderId), "status").Should().Be("funded");

        var refund = await Service().RefundAsync(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            Reason = Reason,
            IdempotencyKey = $"admin-it:{escrowId}:refund:1"
        }, "admin-it", "admin", adminGroupVerified: true);

        refund.IsSuccess.Should().BeTrue(refund.Error?.Message);
        Str(await EscrowAsync(escrowId), "status").Should().Be("refunded");
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("cancelled");
        Str(order, "cancelReason").Should().Be("escrow_refunded");
        Calls($"refund:{escrowId}").Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // Admin exits
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task AdminRefund_HeldForRelease_MovesTheOrderAndDeliveryToTheBuyersVerdict_ThenRefundsFromHeld()
    {
        var parties = await SeedPartiesAsync(suspended: "farmer");
        var deliveryId = await SeedDeliveryAsync("delivered");
        var orderId = await SeedOrderAsync(parties, "delivered", 3, deliveryId);
        var (escrowId, _) = await SeedEscrowAsync(parties, "held", $"escrow-order-{orderId}", orderId, deliveryId,
            extra: [.. HeldFields("release"), ("source", "order")]);

        var refund = await Service().RefundAsync(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            Reason = Reason,
            IdempotencyKey = $"admin-it:{escrowId}:refund:1"
        }, "admin-it", "admin", adminGroupVerified: true);

        refund.IsSuccess.Should().BeTrue(refund.Error?.Message);
        var escrow = await EscrowAsync(escrowId);
        Str(escrow, "status").Should().Be("refunded");
        Str(escrow, "disputeResolution").Should().Be("buyer_wins");
        Str(escrow, "disputeResolvedBy").Should().Be("admin-it");
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("dispute_resolved");
        Str(order, "version").Should().Be("5");
        var delivery = await DeliveryAsync(deliveryId);
        Str(delivery, "status").Should().Be("disputed");
        Str(delivery, "disputeResolution").Should().Be("buyer_wins");
        Calls($"refund:{escrowId}").Should().Be(1);
        (await TxRepo.GetByTransactionAsync(orderId)).Select(r => $"{r["entityType"]}:{r["action"]}").Should().Contain(
            ["escrow:fraud_hold_refund", "order:dispute", "order:dispute_resolved", "delivery:dispute", "delivery:dispute_resolved"]);
    }

    [LocalDynamoDbFact]
    public async Task TwoAdminExitsAndASweep_AtOnce_PayOnce()
    {
        var parties = await SeedPartiesAsync(suspended: "farmer");
        var deliveryId = await SeedDeliveryAsync("delivered");
        var (escrowId, _) = await SeedEscrowAsync(parties, "held", deliveryId: deliveryId, extra: HeldFields("release"));

        var release = Service().ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            DeliveryId = deliveryId,
            IdempotencyKey = $"admin-a:{escrowId}:release:1",
            Reason = Reason
        }, "admin-a", "admin", adminGroupVerified: true);
        var refund = Service().RefundAsync(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            Reason = Reason,
            IdempotencyKey = $"admin-b:{escrowId}:refund:1"
        }, "admin-b", "admin", adminGroupVerified: true);
        var sweep = SweepAsync();
        try
        {
            await Task.WhenAll(release, refund, sweep);
        }
        catch (ConditionalCheckFailedException)
        {
            // Not FRD-03: the sweep's crash recovery completes any released row at once, so it can win the
            // completed write of a release it raced (the money is paid and recorded either way). Every
            // other exception fails the test.
        }

        sweep.IsCompletedSuccessfully.Should().BeTrue();
        (Calls($"release:{escrowId}") + Calls($"refund:{escrowId}")).Should().Be(1);
        Str(await EscrowAsync(escrowId), "status").Should().BeOneOf("completed", "refunded");
        // The loser never reached the gateway: a 409 on the lock, or a 422 on the status it found.
        var loserStatuses = new List<int>();
        if (release.IsCompletedSuccessfully && !release.Result.IsSuccess)
        {
            loserStatuses.Add(release.Result.Error!.HttpStatus);
        }

        if (refund.IsCompletedSuccessfully && !refund.Result.IsSuccess)
        {
            loserStatuses.Add(refund.Result.Error!.HttpStatus);
        }

        loserStatuses.Should().ContainSingle().Which.Should().BeOneOf(409, 422);
    }

    [LocalDynamoDbFact]
    public async Task TheFencedAccept_LosesToAHoldWrittenAfterTheRead_AndWritesNothing()
    {
        var parties = await SeedPartiesAsync();
        var acceptedId = await SeedOrderAsync(parties, "funded", 3);
        var (plainEscrow, plainAt) = await SeedEscrowAsync(parties, "escrowed", $"escrow-order-{acceptedId}", acceptedId);
        var heldId = await SeedOrderAsync(parties, "funded", 3);
        var (heldEscrow, heldAt) = await SeedEscrowAsync(parties, "escrowed", $"escrow-order-{heldId}", heldId);
        // The sync Lambda read both escrows as escrowed; the sweep then holds one before the write.
        (await EscrowRepo.TryHoldAsync(heldEscrow, heldAt, HoldRequest())).Should().Be(EscrowHoldWrite.Held);
        var accept = new Dictionary<string, object> { { "status", "accepted" } };

        var refused = await OrderRepo.UpdateUnlessEscrowHeldAsync(heldId, accept, 3, new EscrowRowKey(_db.Escrows, heldEscrow, heldAt));
        var stale = await OrderRepo.UpdateUnlessEscrowHeldAsync(acceptedId, accept, 2, new EscrowRowKey(_db.Escrows, plainEscrow, plainAt));
        var accepted = await OrderRepo.UpdateUnlessEscrowHeldAsync(acceptedId, accept, 3, new EscrowRowKey(_db.Escrows, plainEscrow, plainAt));

        refused.Error!.Code.Should().Be(OrderRepository.EscrowHeldCode);
        var untouched = await OrderAsync(heldId);
        Str(untouched, "status").Should().Be("funded");
        Str(untouched, "version").Should().Be("3");
        stale.Error!.Code.Should().Be("SYNC_CONFLICT");
        accepted.IsSuccess.Should().BeTrue(accepted.Error?.Message);
        Str(accepted.Value!, "status").Should().Be("accepted");
        Str(accepted.Value!, "version").Should().Be("4");
    }

    [LocalDynamoDbFact]
    public async Task AQueuedAdminExit_ClosesFromHeld()
    {
        var parties = await SeedPartiesAsync();
        var (escrowId, at) = await SeedEscrowAsync(parties, "held", extra: HeldFields("refund"));
        (await EscrowRepo.TryAcquireSettlementLockAsync(escrowId, at, "refund", "held", allowHeld: true)).Should().BeTrue();
        (await EscrowRepo.TryQueueManualPayoutAsync(escrowId, at, "refund", "PAYOUT_NOT_SUPPORTED", "held",
            [new ManualPayoutLegSeed("buyer", "pending", null, null, null)], DateTime.UtcNow.ToString("o"))).Should().BeTrue();

        var closed = await EscrowRepo.TryCloseManualPayoutAsync(escrowId, at, "refund", "buyer", "TB-MANUAL-1", "admin-it",
            DateTime.UtcNow.ToString("o"), [], "refunded", new Dictionary<string, object> { { "refundedAt", DateTime.UtcNow.ToString("o") } });

        closed.Should().BeTrue();
        Str(await EscrowAsync(escrowId), "status").Should().Be("refunded");
    }
}

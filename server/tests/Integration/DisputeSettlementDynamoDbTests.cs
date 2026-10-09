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

/// <summary>
/// ESC-03 against real DynamoDB (LocalStack in CI): the condition expressions the money
/// depends on, evaluated by DynamoDB itself instead of mocked. The settlement lock, the
/// verdict write, the order and delivery mirror, the hourly sweep, a manual admin settlement,
/// the live-gateway manual payout close, and a sweep-versus-admin race.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DisputeSettlementDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string BuyerId = "buyer-it";
    private const string FarmerId = "farmer-it";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private int _releases;
    private int _refunds;

    public DisputeSettlementDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(_ =>
        {
            Interlocked.Increment(ref _releases);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-REL-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(_ =>
        {
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

    private EscrowService Service(ITelebirrGateway? gateway = null) => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway ?? _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };

    private async Task<string> SeedEscrowAsync(
        string status, string? orderId = null, string? deliveryId = null, string? resolution = null,
        bool swept = false, (string Key, string Value)[]? extra = null)
    {
        var escrowId = NewId("escrow-it");
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["status"] = S(status),
            ["buyerId"] = S(BuyerId),
            ["farmerId"] = S(FarmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-IT"),
            ["disputedBy"] = S(BuyerId),
            ["version"] = N("3")
        };
        if (orderId != null) item["orderId"] = S(orderId);
        if (deliveryId != null) item["deliveryId"] = S(deliveryId);
        if (resolution != null)
        {
            item["disputeResolution"] = S(resolution);
            item["disputeResolvedAt"] = S("2026-10-05T10:00:00.0000000Z");
        }

        // Only rows meant for the hourly sweep sit in StatusExpiresIndex, so a sweep run never
        // picks up another test's escrow.
        if (swept) item["expiresAt"] = S("2026-10-08T10:00:00.0000000Z");
        foreach (var (key, value) in extra ?? []) item[key] = S(value);

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private async Task<string> SeedOrderAsync(string status, int version, string? activeDeliveryId = null)
    {
        var orderId = NewId("order-it");
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId), ["createdAt"] = S(CreatedAt), ["status"] = S(status),
            ["buyerId"] = S(BuyerId), ["farmerId"] = S(FarmerId), ["version"] = N(version.ToString())
        };
        if (activeDeliveryId != null) item["activeDeliveryId"] = S(activeDeliveryId);
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
        return orderId;
    }

    private async Task<string> SeedDeliveryAsync(string status, string? orderId = null)
    {
        var deliveryId = NewId("delivery-it");
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId), ["tripId"] = S("trip-it"), ["status"] = S(status),
            ["createdAt"] = S(CreatedAt), ["version"] = N("2")
        };
        if (orderId != null) item["orderId"] = S(orderId);
        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = item });
        return deliveryId;
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> OrderAsync(string id) => (await OrderRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> DeliveryAsync(string id) => (await DeliveryRepo.GetByIdAsync(id, true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<List<string>> AuditActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString()!).ToList();

    // ──────────────────────────────────────────────
    // The settlement lock: the exact status, never a set
    // ──────────────────────────────────────────────

    [LocalDynamoDbTheory]
    [InlineData("escrowed", "escrowed", true)]
    [InlineData("dispute_resolved", "dispute_resolved", true)]
    [InlineData("dispute_resolved", "escrowed", false)]
    [InlineData("escrowed", "dispute_resolved", false)]
    [InlineData("disputed", "disputed", false)]
    [InlineData("initiated", "initiated", false)]
    [InlineData("released", "released", false)]
    [InlineData("completed", "completed", false)]
    [InlineData("refunded", "refunded", false)]
    [InlineData("held", "held", false)]
    public async Task Lock_AcquiresOnlyOnTheSettleableStatusTheCallerValidated(
        string status, string expected, bool acquires)
    {
        var escrowId = await SeedEscrowAsync(status);

        var acquired = await EscrowRepo.TryAcquireSettlementLockAsync(escrowId, CreatedAt, "refund", expected);

        acquired.Should().Be(acquires);
        Str(await EscrowAsync(escrowId), "settlementLock").Should().Be(acquires ? "refund" : null);
    }

    [LocalDynamoDbFact]
    public async Task Lock_RefusedWhileAnotherSettlementHoldsIt_OrAManualPayoutIsOpen()
    {
        var locked = await SeedEscrowAsync("dispute_resolved", extra: [("settlementLock", "release")]);
        var queued = await SeedEscrowAsync("dispute_resolved", extra: [("manualPayout", "open")]);

        (await EscrowRepo.TryAcquireSettlementLockAsync(locked, CreatedAt, "refund", "dispute_resolved")).Should().BeFalse();
        (await EscrowRepo.TryAcquireSettlementLockAsync(queued, CreatedAt, "refund", "dispute_resolved")).Should().BeFalse();
        Str(await EscrowAsync(locked), "settlementLock").Should().Be("release");
    }

    [LocalDynamoDbFact]
    public async Task DisputedWrite_RefusedWhileASettlementHoldsTheLock()
    {
        var locked = await SeedEscrowAsync("escrowed", extra: [("settlementLock", "release")]);
        var free = await SeedEscrowAsync("escrowed");

        var act = () => EscrowRepo.UpdateStatusAsync(locked, CreatedAt, "disputed", "escrowed");
        await act.Should().ThrowAsync<ConditionalCheckFailedException>();
        await EscrowRepo.UpdateStatusAsync(free, CreatedAt, "disputed", "escrowed");

        Str(await EscrowAsync(locked), "status").Should().Be("escrowed");
        Str(await EscrowAsync(free), "status").Should().Be("disputed");
    }

    // ──────────────────────────────────────────────
    // The verdict, through the admin Lambda
    // ──────────────────────────────────────────────

    private AdminFunction Admin() => new(
        Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit);

    private static APIGatewayProxyRequest Verdict(string escrowId, string resolution, string action) => new()
    {
        HttpMethod = "POST",
        Path = $"/admin/disputes/{escrowId}/resolve",
        Body = JsonSerializer.Serialize(new { resolution, action }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-it" }, { "cognito:groups", "admin" } }
        }
    };

    [LocalDynamoDbFact]
    public async Task Verdict_MovesEscrowOrderAndDelivery_AndAReplayWritesNothingNew()
    {
        var deliveryId = await SeedDeliveryAsync("delivered");
        var orderId = await SeedOrderAsync("delivered", 3, deliveryId);
        var escrowId = await SeedEscrowAsync("disputed", orderId, deliveryId);

        var response = await Admin().FunctionHandler(Verdict(escrowId, "buyer_wins", "refund"), new TestLambdaContext());

        response.StatusCode.Should().Be(200, response.Body);
        var escrow = await EscrowAsync(escrowId);
        Str(escrow, "status").Should().Be("dispute_resolved");
        Str(escrow, "disputeResolution").Should().Be("buyer_wins");
        Str(escrow, "disputeResolvedBy").Should().Be("admin-it");
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("dispute_resolved");
        Str(order, "version").Should().Be("5");
        var delivery = await DeliveryAsync(deliveryId);
        Str(delivery, "status").Should().Be("disputed");
        Str(delivery, "disputeResolution").Should().Be("buyer_wins");
        var deliveryVersion = Str(delivery, "version");
        var txRows = await TxRepo.GetByTransactionAsync(orderId);
        txRows.Select(r => $"{r["entityType"]}:{r["action"]}").Should().BeEquivalentTo(
            "escrow:admin_dispute_resolved", "order:dispute", "order:dispute_resolved",
            "delivery:dispute", "delivery:dispute_resolved");

        // The same verdict again (a double click): 200, the escrow's real status, nothing new.
        var replay = await Admin().FunctionHandler(Verdict(escrowId, "buyer_wins", "refund"), new TestLambdaContext());

        replay.StatusCode.Should().Be(200, replay.Body);
        replay.Body.Should().Contain("Verdict already recorded.");
        Str(await DeliveryAsync(deliveryId), "version").Should().Be(deliveryVersion);
        (await TxRepo.GetByTransactionAsync(orderId)).Should().HaveCount(txRows.Count);
        (await AuditActionsAsync(escrowId)).Should().Equal("admin_dispute_resolved");

        // Another verdict is refused.
        var other = await Admin().FunctionHandler(Verdict(escrowId, "farmer_wins", "release"), new TestLambdaContext());
        other.StatusCode.Should().Be(409);
        other.Body.Should().Contain("ADMIN_010");
    }

    [LocalDynamoDbFact]
    public async Task Verdict_OrderLeftBehindADeliveredDelivery_IsAdvancedThenResolved()
    {
        var deliveryId = await SeedDeliveryAsync("delivered");
        var orderId = await SeedOrderAsync("in_transit", 4, deliveryId);
        var escrowId = await SeedEscrowAsync("disputed", orderId, deliveryId);

        var response = await Admin().FunctionHandler(Verdict(escrowId, "farmer_wins", "release"), new TestLambdaContext());

        response.StatusCode.Should().Be(200, response.Body);
        var order = await OrderAsync(orderId);
        Str(order, "status").Should().Be("dispute_resolved");
        Str(order, "version").Should().Be("7");
    }

    [LocalDynamoDbFact]
    public async Task Verdict_OrderThatCannotFollow_KeepsTheVerdict_WritesNoLeg_AndIsAudited()
    {
        var deliveryId = await SeedDeliveryAsync("inTransit");
        var orderId = await SeedOrderAsync("in_transit", 4, deliveryId);
        var escrowId = await SeedEscrowAsync("disputed", orderId, deliveryId);

        var response = await Admin().FunctionHandler(Verdict(escrowId, "buyer_wins", "refund"), new TestLambdaContext());

        response.StatusCode.Should().Be(200, response.Body);
        Str(await EscrowAsync(escrowId), "status").Should().Be("dispute_resolved");
        Str(await OrderAsync(orderId), "status").Should().Be("in_transit");
        Str(await DeliveryAsync(deliveryId), "status").Should().Be("inTransit");
        var trail = await AuditRepo.GetAuditTrailAsync(escrowId);
        trail.Should().Contain(a => a["action"].ToString() == "dispute_verdict_propagation_failed"
                                    && a["metadata"].ToString() == "legs=order:mismatch:in_transit,delivery:mismatch:inTransit");
    }

    [LocalDynamoDbFact]
    public async Task Verdict_RefusedWhileASettlementHoldsTheLock_OrAPayoutIsQueued_OrInvalid()
    {
        var locked = await SeedEscrowAsync("disputed", extra: [("settlementLock", "release")]);
        var queued = await SeedEscrowAsync("disputed", extra: [("manualPayout", "open")]);
        var open = await SeedEscrowAsync("disputed");

        var lockedResponse = await Admin().FunctionHandler(Verdict(locked, "buyer_wins", "refund"), new TestLambdaContext());
        var queuedResponse = await Admin().FunctionHandler(Verdict(queued, "buyer_wins", "refund"), new TestLambdaContext());
        var invalid = await Admin().FunctionHandler(Verdict(open, "partial_refund", "refund"), new TestLambdaContext());

        lockedResponse.StatusCode.Should().Be(409);
        lockedResponse.Body.Should().Contain("ADMIN_010").And.Contain("settlement is in progress");
        queuedResponse.StatusCode.Should().Be(409);
        queuedResponse.Body.Should().Contain("ADMIN_013");
        invalid.StatusCode.Should().Be(400);
        invalid.Body.Should().Contain("ADMIN_014");
        foreach (var id in new[] { locked, queued, open })
        {
            var row = await EscrowAsync(id);
            Str(row, "status").Should().Be("disputed");
            Str(row, "disputeResolution").Should().BeNull();
        }
    }

    // ──────────────────────────────────────────────
    // Settlement: the hourly sweep and a manual admin call
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task Sweep_ReleasesFarmerWins_AndRefundsBuyerWins()
    {
        // The verdict could not move this order when it was recorded; the sweep repairs it first.
        var orderId = await SeedOrderAsync("disputed", 6);
        var farmerWins = await SeedEscrowAsync("dispute_resolved", orderId, resolution: "farmer_wins", swept: true);
        var buyerWins = await SeedEscrowAsync("dispute_resolved", resolution: "buyer_wins", swept: true);
        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo);

        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());

        response.StatusCode.Should().Be(200, response.Body);
        Str(await EscrowAsync(farmerWins), "status").Should().Be("completed");
        Str(await EscrowAsync(buyerWins), "status").Should().Be("refunded");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{farmerWins}"));
        await _gateway.Received(1).RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == $"refund:{buyerWins}"));
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Is<RefundFundsRequest>(r => r.TransactionRef == $"refund:{farmerWins}"));
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{buyerWins}"));
        (await AuditActionsAsync(farmerWins)).Should().Contain(new[] { "release", "complete" });
        (await AuditActionsAsync(buyerWins)).Should().Contain("refund");
        Str(await OrderAsync(orderId), "status").Should().Be("dispute_resolved");
        (await TxRepo.GetByTransactionAsync(orderId)).Should().ContainSingle(r =>
            r["action"].ToString() == "dispute_resolved" && r["actor"].ToString() == "system");
    }

    [LocalDynamoDbFact]
    public async Task ManualAdmin_SettlesOnlyTheWayTheVerdictSays()
    {
        var farmerWins = await SeedEscrowAsync("dispute_resolved", resolution: "farmer_wins");
        var buyerWins = await SeedEscrowAsync("dispute_resolved", resolution: "buyer_wins");
        var service = Service();
        var key = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var wrongRefund = await service.RefundAsync(new RefundEscrowRequest
        {
            EscrowId = farmerWins, Trigger = "dispute_resolved", IdempotencyKey = $"admin-it:{farmerWins}:refund:{key}"
        }, "admin-it", "admin", adminGroupVerified: true);
        var wrongRelease = await service.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = buyerWins, Trigger = "dispute_resolved", DeliveryId = "", IdempotencyKey = $"admin-it:{buyerWins}:release:{key}"
        }, "admin-it", "admin", adminGroupVerified: true);

        wrongRefund.Error!.HttpStatus.Should().Be(422);
        wrongRelease.Error!.HttpStatus.Should().Be(422);
        Str(await EscrowAsync(farmerWins), "settlementLock").Should().BeNull();
        Str(await EscrowAsync(buyerWins), "settlementLock").Should().BeNull();

        var release = await service.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = farmerWins, Trigger = "dispute_resolved", DeliveryId = "", IdempotencyKey = $"admin-it:{farmerWins}:release:{key + 1}"
        }, "admin-it", "admin", adminGroupVerified: true);
        var refund = await service.RefundAsync(new RefundEscrowRequest
        {
            EscrowId = buyerWins, Trigger = "dispute_resolved", IdempotencyKey = $"admin-it:{buyerWins}:refund:{key + 1}"
        }, "admin-it", "admin", adminGroupVerified: true);

        release.IsSuccess.Should().BeTrue(release.Error?.Message);
        refund.IsSuccess.Should().BeTrue(refund.Error?.Message);
        Str(await EscrowAsync(farmerWins), "status").Should().Be("completed");
        Str(await EscrowAsync(buyerWins), "status").Should().Be("refunded");
        _releases.Should().Be(1);
        _refunds.Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task LiveGateway_QueuesTheVerdictsPayout_AndTheAdminCloseSettlesIt()
    {
        var liveGateway = Substitute.For<ITelebirrGateway>();
        liveGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse
        {
            Success = false, ErrorCode = ManualPayoutFields.ReasonPayoutNotSupported
        });
        var escrowId = await SeedEscrowAsync("dispute_resolved", resolution: "farmer_wins");
        var service = Service(liveGateway);

        var queued = await service.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId, Trigger = "dispute_resolved", DeliveryId = "",
            IdempotencyKey = $"system:{escrowId}:dispute_release:{DateTime.UtcNow:yyyyMMddHH}"
        }, EscrowService.SystemActor);

        queued.Error!.HttpStatus.Should().Be(202);
        queued.Error.Code.Should().Be("ESCROW_034");
        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("dispute_resolved");
        Str(row, ManualPayoutFields.Marker).Should().Be(ManualPayoutFields.MarkerOpen);
        Str(row, ManualPayoutFields.FromStatus).Should().Be("dispute_resolved");

        var closed = await service.CompleteManualPayoutAsync(escrowId, ManualPayoutFields.LegFarmer, "TB-IT-0001", "admin-it", true);

        closed.IsSuccess.Should().BeTrue(closed.Error?.Message);
        closed.Value!.EscrowStatus.Should().Be("completed");
        Str(await EscrowAsync(escrowId), "telebirrReleaseRef").Should().Be("TB-IT-0001");
    }

    [LocalDynamoDbFact]
    public async Task SweepAndAdminRefundingAtOnce_MoveTheMoneyOnce()
    {
        for (var i = 0; i < 10; i++)
        {
            var escrowId = await SeedEscrowAsync("dispute_resolved", resolution: "buyer_wins");
            var before = _refunds;

            var sweep = Service().RefundAsync(new RefundEscrowRequest
            {
                EscrowId = escrowId, Trigger = "dispute_resolved",
                IdempotencyKey = $"system:{escrowId}:dispute_refund:{DateTime.UtcNow:yyyyMMddHH}"
            }, EscrowService.SystemActor);
            var admin = Service().RefundAsync(new RefundEscrowRequest
            {
                EscrowId = escrowId, Trigger = "dispute_resolved",
                IdempotencyKey = $"admin-it:{escrowId}:refund:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            }, "admin-it", "admin", adminGroupVerified: true);
            await Task.WhenAll(sweep, admin);

            (_refunds - before).Should().Be(1, $"run {i}: exactly one gateway refund");
            Str(await EscrowAsync(escrowId), "status").Should().Be("refunded");
        }
    }
}

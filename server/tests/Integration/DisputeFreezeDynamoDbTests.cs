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
/// ESC-07 against real DynamoDB (LocalStack in CI): every dispute freezes the escrow first and
/// only then moves the delivery and the order. A client order dispute is refused; a delivery
/// dispute over money that is queued, being settled or no longer held writes nothing; an
/// already frozen escrow repairs the legs; <c>POST /escrow/dispute</c> audits every hop and adds
/// to the delivery version; two disputes at once freeze once; a delivery write that fails after
/// the freeze is finished by the retry.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DisputeFreezeDynamoDbTests
{
    private const string CreatedAt = "2026-10-09T08:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly string _truckerId = $"trucker-{Guid.NewGuid():N}";
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DisputeFreezeDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = (string)ci[0],
            PhoneNumber = "+251900100000",
            DisplayName = "Party Name",
            Role = (string)ci[0] == _buyerId ? "buyer" : (string)ci[0] == _farmerId ? "farmer" : "trucker",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    /// <summary>The escrow service as the sync Lambda builds it (ESC-07: with the transaction audit).</summary>
    private EscrowService SyncEscrowService(IEscrowAuditRepository? auditRepo = null) => new(
        EscrowRepo, auditRepo ?? AuditRepo, idempotencyRepo: null, telebirrGateway: null,
        orderRepo: OrderRepo, deliveryRepo: DeliveryRepo, transactionAuditRepo: TxRepo);

    /// <summary>The escrow service as the escrow Lambda builds it, for <c>POST /escrow/dispute</c>.</summary>
    private EscrowService EndpointEscrowService() => new(
        EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), Substitute.For<ITelebirrGateway>(),
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo);

    private SyncFunction Sync(IAmazonDynamoDB? dynamoDb = null, IEscrowAuditRepository? auditRepo = null)
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), dynamoDb ?? Ddb, _db.Trips, _db.Deliveries,
            userRepo: _users, orderRepo: OrderRepo, escrowRepo: EscrowRepo, transactionAuditRepo: TxRepo,
            orderEscrowService: SyncEscrowService(auditRepo));
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(int value) => new() { N = value.ToString() };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    // ──────────────────────────────────────────────
    // Seeding: a delivered order, its delivered delivery (confirmed two hours ago) and its escrow
    // ──────────────────────────────────────────────

    private sealed record Trade(string OrderId, string DeliveryId, string EscrowId);

    private async Task<Trade> SeedTradeAsync(
        string escrowStatus = "escrowed", string orderStatus = "delivered", int deliveryVersion = 1,
        bool linkEscrow = true, (string Key, string Value)[]? escrowExtra = null)
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        var escrowId = $"escrow-order-{orderId}";
        var confirmedAt = DateTime.UtcNow.AddHours(-2).ToString("o");

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
                ["truckerId"] = S(_truckerId),
                ["activeDeliveryId"] = S(deliveryId),
                ["orderTotal"] = new AttributeValue { N = "900" },
                ["version"] = N(1)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId),
                ["tripId"] = S("trip-it"),
                ["orderId"] = S(orderId),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["participantId"] = S(_buyerId),
                ["status"] = S("delivered"),
                ["confirmedAt"] = S(confirmedAt),
                ["createdAt"] = S(CreatedAt),
                ["version"] = N(deliveryVersion)
            }
        });

        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["status"] = S(escrowStatus),
            ["orderId"] = S(orderId),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["amount"] = new AttributeValue { N = "900" },
            ["source"] = S("order"),
            ["deliveryConfirmedAt"] = S(confirmedAt),
            ["version"] = N(2)
        };
        if (linkEscrow)
        {
            escrow["deliveryId"] = S(deliveryId);
        }

        foreach (var (key, value) in escrowExtra ?? [])
        {
            escrow[key] = S(value);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = escrow });

        return new Trade(orderId, deliveryId, escrowId);
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> OrderAsync(string id) => (await OrderRepo.GetByIdAsync(id, true))!;
    private async Task<Dictionary<string, object>> DeliveryAsync(string id) => (await DeliveryRepo.GetByIdAsync(id, true))!;

    private async Task<List<string>> EscrowAuditActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString()!).ToList();

    private async Task<List<string>> TxRowsAsync(string orderId)
        => (await TxRepo.GetByTransactionAsync(orderId))
            .Select(r => $"{r["entityType"]}:{r["action"]}:{Str(r, "fromState")}>{Str(r, "toState")}:{r["actor"]}")
            .ToList();

    // ──────────────────────────────────────────────
    // Sync calls
    // ──────────────────────────────────────────────

    private async Task<JsonElement> SendAsync(SyncFunction sync, SyncOperationRequest operation, string userId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = Guid.NewGuid().ToString(),
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest> { operation }
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

    private static SyncOperationRequest DeliveryDisputeOp(string deliveryId, string userId, int version) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        IdempotencyKey = $"{userId}:delivery:{deliveryId}:update:{Guid.NewGuid():N}",
        EntityType = "delivery",
        EntityId = deliveryId,
        Action = "update",
        Data = new Dictionary<string, object>
        {
            { "status", "disputed" }, { "disputeReason", "Half the sacks were wet." }, { "version", version }
        }
    };

    private static SyncOperationRequest OrderDisputeOp(string orderId, string userId) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        IdempotencyKey = $"{userId}:order:{orderId}:update:{Guid.NewGuid():N}",
        EntityType = "order",
        EntityId = orderId,
        Action = "update",
        Data = new Dictionary<string, object> { { "status", "disputed" }, { "version", 1 } }
    };

    private DisputeEscrowRequest EscrowDisputeRequest(string escrowId, string userId) => new()
    {
        EscrowId = escrowId,
        DisputedBy = userId,
        Reason = "damaged_goods",
        IdempotencyKey = $"{userId}:{escrowId}:dispute:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
    };

    private async Task NothingMovedAsync(Trade trade, string escrowStatus)
    {
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be(escrowStatus);
        var delivery = await DeliveryAsync(trade.DeliveryId);
        Str(delivery, "status").Should().Be("delivered");
        Str(delivery, "version").Should().Be("1");
        var order = await OrderAsync(trade.OrderId);
        Str(order, "status").Should().Be("delivered");
        Str(order, "version").Should().Be("1");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().NotContain("dispute");
    }

    // ──────────────────────────────────────────────
    // The client order path is gone
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task ClientOrderDispute_IsRefused_ForBuyerAndFarmer_AndNothingMoves()
    {
        var trade = await SeedTradeAsync();

        foreach (var userId in new[] { _buyerId, _farmerId })
        {
            var result = await SendAsync(Sync(), OrderDisputeOp(trade.OrderId, userId), userId);

            result.GetProperty("status").GetString().Should().Be("error");
            result.GetProperty("message").GetString().Should().Be("ORDER_UNAUTHORIZED: 'disputed' is a system-managed transition.");
        }

        await NothingMovedAsync(trade, "escrowed");
        (await TxRowsAsync(trade.OrderId)).Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // A delivery dispute freezes first
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_FreezesTheEscrow_ThenMovesTheDeliveryAndTheOrder()
    {
        var trade = await SeedTradeAsync();

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var escrow = await EscrowAsync(trade.EscrowId);
        Str(escrow, "status").Should().Be("disputed");
        Str(escrow, "disputedBy").Should().Be(_buyerId);
        Str(escrow, "disputeReason").Should().Be("other");
        Str(escrow, "disputeDescription").Should().Be("Half the sacks were wet.");
        // Seeded at version 2: the freeze adds to it (never a version from its read).
        Str(escrow, "version").Should().Be("3");
        var delivery = await DeliveryAsync(trade.DeliveryId);
        Str(delivery, "status").Should().Be("disputed");
        Str(delivery, "version").Should().Be("2");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute");
        (await TxRowsAsync(trade.OrderId)).Should().BeEquivalentTo(
            $"delivery:dispute:delivered>disputed:{_buyerId}",
            "order:dispute:delivered>disputed:system");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_OverAQueuedManualPayout_IsRefused_AndNothingMoves()
    {
        var trade = await SeedTradeAsync(escrowExtra: [("manualPayout", "open"), ("manualPayoutOp", "release")]);

        // Clients before sync-protocol 1.12.0 resend a refused dispute on every reconnect: the
        // refusal is audited once a day.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

            result.GetProperty("status").GetString().Should().Be("error");
            result.GetProperty("message").GetString().Should().StartWith("DELIVERY_DISPUTE_REFUSED:");
        }

        await NothingMovedAsync(trade, "escrowed");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute_refused_manual_payout");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_WhileASettlementHoldsTheLock_IsRefused_AndNothingMoves()
    {
        var trade = await SeedTradeAsync(escrowExtra: [("settlementLock", "release")]);

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _farmerId, 1), _farmerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_DISPUTE_REFUSED:");
        await NothingMovedAsync(trade, "escrowed");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute_refused_settlement_in_flight");
    }

    [LocalDynamoDbTheory]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("refunded")]
    public async Task DeliveryDispute_OverMoneyNoLongerHeld_IsRefused_AndNothingMoves(string status)
    {
        var trade = await SeedTradeAsync(escrowStatus: status);

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_DISPUTE_REFUSED:").And.Contain($"status: {status}");
        await NothingMovedAsync(trade, status);
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_OrderEscrowNotLinkedYet_IsFoundByTheOrder_AndFrozen()
    {
        // The escrow never reached the DeliveryIndex (no deliveryId): its deterministic id finds it.
        var trade = await SeedTradeAsync(linkEscrow: false);

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_EscrowAlreadyDisputed_RepairsTheLegs_WithoutASecondDispute()
    {
        var trade = await SeedTradeAsync(escrowStatus: "disputed");

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_StaleVersion_IsAConflict_AndFreezesNothing()
    {
        var trade = await SeedTradeAsync(deliveryVersion: 3);

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 2), _buyerId);

        result.GetProperty("status").GetString().Should().Be("conflict");
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("escrowed");
        Str(await DeliveryAsync(trade.DeliveryId), "version").Should().Be("3");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("delivered");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_OrderLeftBehindItsDelivery_IsAdvancedThenDisputed()
    {
        var trade = await SeedTradeAsync(orderStatus: "in_transit");

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var order = await OrderAsync(trade.OrderId);
        Str(order, "status").Should().Be("disputed");
        Str(order, "version").Should().Be("3");
        (await TxRowsAsync(trade.OrderId)).Should().Contain(new[]
        {
            "order:order_updated:in_transit>delivered:system",
            "order:dispute:delivered>disputed:system"
        });
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_DeliveryWriteFailsAfterTheFreeze_TheRetryFinishesIt_WithOneDispute()
    {
        // The freeze landed, then the delivery write was throttled: the first attempt errors and
        // leaves the escrow disputed with the delivery and order delivered. The retry finds the
        // escrow already disputed and finishes both. One dispute row on the escrow in all.
        var trade = await SeedTradeAsync();
        var flaky = Substitute.For<IAmazonDynamoDB>();
        flaky.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Ddb.QueryAsync(ci.Arg<QueryRequest>(), ci.Arg<CancellationToken>()));
        var deliveryWrites = 0;
        flaky.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref deliveryWrites) == 1
                ? throw new ProvisionedThroughputExceededException("throttled")
                : Ddb.UpdateItemAsync(ci.Arg<UpdateItemRequest>(), ci.Arg<CancellationToken>()));
        var sync = Sync(flaky);

        var first = await SendAsync(sync, DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        first.GetProperty("status").GetString().Should().Be("error");
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("disputed");
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("delivered");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("delivered");

        var retry = await SendAsync(sync, DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        retry.GetProperty("status").GetString().Should().Be("success", retry.ToString());
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_AuditFailsAfterTheFreeze_TheRetryWritesTheRow_AndFinishesTheTrade()
    {
        // The freeze landed, then its audit write failed: the client is asked to retry, nothing
        // else moved. The retry finds the escrow disputed, writes the missing dispute row (one in
        // all, at the freeze's own key) and moves the delivery and order.
        var trade = await SeedTradeAsync();
        var flakyAudit = Substitute.For<IEscrowAuditRepository>();
        var real = AuditRepo;
        var ensures = 0;
        flakyAudit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(ci => Interlocked.Increment(ref ensures) == 1
                ? throw new ProvisionedThroughputExceededException("audit throttled")
                : real.AppendIfAbsentAsync(ci.Arg<Dictionary<string, object>>()));
        flakyAudit.AppendAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(ci => real.AppendAsync(ci.Arg<Dictionary<string, object>>()));
        flakyAudit.GetAuditTrailAsync(Arg.Any<string>()).Returns(ci => real.GetAuditTrailAsync(ci.Arg<string>()));
        var sync = Sync(auditRepo: flakyAudit);

        var first = await SendAsync(sync, DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        first.GetProperty("message").GetString().Should().StartWith("DELIVERY_DISPUTE_RETRY:");
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("disputed");
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("delivered");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().BeEmpty();

        var retry = await SendAsync(sync, DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        retry.GetProperty("status").GetString().Should().Be("success", retry.ToString());
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        var trail = await AuditRepo.GetAuditTrailAsync(trade.EscrowId);
        trail.Select(a => a["action"].ToString()).Should().Equal("dispute");
        Str(trail.Single(), "timestamp").Should().Be(Str(await EscrowAsync(trade.EscrowId), "disputeCreatedAt"));
    }

    [LocalDynamoDbFact]
    public async Task DeliveryDispute_RetryAfterTheDeliveryWrite_FinishesTheOrder_AndLeavesTheDelivery()
    {
        // The Lambda stopped after the delivery write and before the order mirror: escrow and
        // delivery disputed, order still delivered. The client's queued operation (version 1) comes
        // again: the order follows, the delivery is not written again, the answer is the row.
        var trade = await SeedTradeAsync(escrowStatus: "disputed", deliveryVersion: 2,
            escrowExtra: [("disputeCreatedAt", "2026-10-10T07:00:00.0000000Z"), ("disputeAuditAt", "2026-10-10T07:00:00.0000000Z"),
                ("disputedBy", _buyerId)]);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(trade.DeliveryId), ["tripId"] = S("trip-it") },
            UpdateExpression = "SET #s = :d, disputeReason = :r",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":d"] = S("disputed"), [":r"] = S("first reason") }
        });

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        var delivery = await DeliveryAsync(trade.DeliveryId);
        Str(delivery, "version").Should().Be("2");
        Str(delivery, "disputeReason").Should().Be("first reason");
        (await TxRowsAsync(trade.OrderId)).Should().Equal("order:dispute:delivered>disputed:system");
        // The lost dispute row of the first request is written at its own key.
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute");
    }

    /// <summary>
    /// An escrow disputed before ESC-07: no <c>disputeAuditAt</c> marker, and its <c>dispute</c> row at
    /// the old write's own time, a few milliseconds after <c>disputeCreatedAt</c>.
    /// </summary>
    private async Task<Trade> SeedLegacyDisputedTradeAsync()
    {
        var trade = await SeedTradeAsync(escrowStatus: "disputed", deliveryVersion: 2, escrowExtra:
        [
            ("disputeId", "dispute-legacy"), ("disputedBy", _buyerId), ("disputeReason", "other"),
            ("disputeCreatedAt", "2026-10-09T09:00:00.0000000Z")
        ]);
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Deliveries,
            Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = S(trade.DeliveryId), ["tripId"] = S("trip-it") },
            UpdateExpression = "SET #s = :d",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":d"] = S("disputed") }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.EscrowAudit,
            Item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = S(trade.EscrowId),
                ["timestamp"] = S("2026-10-09T09:00:00.0031234Z"),
                ["auditId"] = S($"audit-{Guid.NewGuid():N}"),
                ["action"] = S("dispute"),
                ["fromState"] = S("escrowed"),
                ["toState"] = S("disputed"),
                ["actor"] = S(_buyerId)
            }
        });
        return trade;
    }

    [LocalDynamoDbFact]
    public async Task LegacyDisputedEscrow_EscrowEndpointRetry_Answers409Escrow020_WithNoSecondDisputeRow()
    {
        var trade = await SeedLegacyDisputedTradeAsync();

        var result = await EndpointEscrowService().DisputeAsync(EscrowDisputeRequest(trade.EscrowId, _farmerId), _farmerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_020");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Count(a => a == "dispute").Should().Be(1);
        // The order still follows the frozen escrow.
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
    }

    [LocalDynamoDbFact]
    public async Task LegacyDisputedDelivery_SyncRepeat_AnswersConflict_WithNoSecondDisputeRow()
    {
        var trade = await SeedLegacyDisputedTradeAsync();

        var result = await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _farmerId, 2), _farmerId);

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        (await EscrowAuditActionsAsync(trade.EscrowId)).Count(a => a == "dispute").Should().Be(1);
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
    }

    // ──────────────────────────────────────────────
    // POST /escrow/dispute: freeze, then every hop audited
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task EscrowDispute_FreezesThenMirrors_AuditsEveryHop_AndAddsToTheDeliveryVersion()
    {
        var trade = await SeedTradeAsync(deliveryVersion: 5);

        var result = await EndpointEscrowService().DisputeAsync(EscrowDisputeRequest(trade.EscrowId, _farmerId), _farmerId);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("disputed");
        var delivery = await DeliveryAsync(trade.DeliveryId);
        Str(delivery, "status").Should().Be("disputed");
        Str(delivery, "version").Should().Be("6");
        Str(delivery, "disputedBy").Should().Be(_farmerId);
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
        (await TxRowsAsync(trade.OrderId)).Should().BeEquivalentTo(
            "delivery:dispute:delivered>disputed:system",
            "order:dispute:delivered>disputed:system");
    }

    [LocalDynamoDbFact]
    public async Task EscrowDispute_LostToADeliveryDispute_Is409Escrow020_AndTheKeyIsFreed()
    {
        var trade = await SeedTradeAsync();
        (await SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId))
            .GetProperty("status").GetString().Should().Be("success");

        // The farmer's request was read before the freeze: replay it through the lost-race branch.
        var request = EscrowDisputeRequest(trade.EscrowId, _farmerId);
        var stale = new Dictionary<string, object>(await EscrowAsync(trade.EscrowId)) { ["status"] = "escrowed" };
        stale.Remove("disputeId");
        var raceRepo = Substitute.For<IEscrowRepository>();
        raceRepo.GetByIdAsync(trade.EscrowId).Returns(stale);
        raceRepo.GetByIdAsync(trade.EscrowId, true).Returns(ci => EscrowRepo.GetByIdAsync(trade.EscrowId, true));
        raceRepo.UpdateStatusAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(ci => EscrowRepo.UpdateStatusAsync(
                ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<string?>(3),
                ci.ArgAt<Dictionary<string, object>?>(4)));
        var idempotency = new EscrowIdempotencyRepository(Ddb, _db.Idempotency);
        var service = new EscrowService(raceRepo, AuditRepo, idempotency, Substitute.For<ITelebirrGateway>(),
            userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo);

        var result = await service.DisputeAsync(request, _farmerId);

        result.Error!.HttpStatus.Should().Be(409);
        result.Error.Code.Should().Be("ESCROW_020");
        (await idempotency.GetAsync(request.IdempotencyKey)).Should().BeNull();
        (await EscrowAuditActionsAsync(trade.EscrowId)).Should().Equal("dispute");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryAndEscrowDisputesAtOnce_FreezeOnce_AndEveryRowEndsDisputed()
    {
        var trade = await SeedTradeAsync();

        var syncDispute = SendAsync(Sync(), DeliveryDisputeOp(trade.DeliveryId, _buyerId, 1), _buyerId);
        var endpointDispute = EndpointEscrowService().DisputeAsync(EscrowDisputeRequest(trade.EscrowId, _farmerId), _farmerId);
        await Task.WhenAll(syncDispute, endpointDispute);

        var endpoint = await endpointDispute;
        var sync = (await syncDispute).GetProperty("status").GetString();
        // Whichever froze first, the other one never froze a second time. The endpoint losing,
        // whether it read the escrow before or after the freeze, answers a second dispute: ESCROW_020.
        (endpoint.IsSuccess || endpoint.Error!.Code == "ESCROW_020").Should().BeTrue(endpoint.Error?.Message);
        if (!endpoint.IsSuccess)
        {
            endpoint.Error!.HttpStatus.Should().Be(409);
        }

        sync.Should().BeOneOf("success", "conflict");
        (await EscrowAuditActionsAsync(trade.EscrowId)).Count(a => a == "dispute").Should().Be(1);
        Str(await EscrowAsync(trade.EscrowId), "status").Should().Be("disputed");
        Str(await DeliveryAsync(trade.DeliveryId), "status").Should().Be("disputed");
        Str(await OrderAsync(trade.OrderId), "status").Should().Be("disputed");
    }
}

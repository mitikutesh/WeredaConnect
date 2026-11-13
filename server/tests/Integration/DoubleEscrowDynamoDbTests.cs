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

/// <summary>Own tables: the hourly sweep reads whole status partitions.</summary>
[CollectionDefinition(Name)]
public sealed class DoubleEscrowDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "DoubleEscrowDynamoDb";
}

/// <summary>
/// FUP-41 against real DynamoDB: a release the payout identity gate blocks logs <c>RELEASE_BLOCKED_OVERDUE</c> on
/// every hourly run once its stored reason is a day old (and is still audited once); a legacy double escrow the
/// sweep blocks is refunded by an admin through the existing refund, with its decision written conditionally and
/// audited first, its order and the order's own escrow left as they were, a replay answering the refund made, and
/// the sweeps leaving everything alone afterwards.
/// </summary>
[Collection(DoubleEscrowDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DoubleEscrowDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly List<string> _paid = new();
    private readonly List<string> _alarms = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];

    public DoubleEscrowDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            lock (_paid)
            {
                _paid.Add(call.Arg<ReleaseFundsRequest>().TransactionRef);
            }

            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            lock (_paid)
            {
                _paid.Add(call.Arg<RefundFundsRequest>().TransactionRef);
            }

            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-REFUND-{Guid.NewGuid():N}" };
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

    private int Paid(string reference)
    {
        lock (_paid)
        {
            return _paid.Count(r => r == reference);
        }
    }

    private List<string> Alarms(string token, string escrowId)
    {
        lock (_alarms)
        {
            return _alarms.Where(l => l.StartsWith(token + " ") && l.Contains($"escrowId={escrowId} ")).ToList();
        }
    }

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

    private async Task SweepAsync()
    {
        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
            transactionAuditRepo: TxRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" },
            new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(5) });
        response.StatusCode.Should().Be(200, response.Body);
    }

    private async Task<string> SeedUserAsync(string role, bool phoneVerified)
    {
        var id = $"{role}-fup41-{Guid.NewGuid():N}";
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

    private Task PutAsync(string table, Dictionary<string, AttributeValue> item)
        => Ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = item });

    /// <summary>An escrowed 1.x escrow whose delivery was confirmed and whose dispute window has passed.</summary>
    private Dictionary<string, AttributeValue> ConfirmedEscrow(string escrowId, string farmerId, string buyerId, string deliveryId,
        string? orderId = null, bool orderSourced = false, string status = "escrowed")
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(Ago(24 * 9)),
            ["updatedAt"] = S(Ago(24 * 9)),
            ["status"] = S(status),
            ["buyerId"] = S(buyerId),
            ["farmerId"] = S(farmerId),
            ["deliveryId"] = S(deliveryId),
            ["amount"] = N("5000"),
            ["commission"] = N("125"),
            ["farmerPayout"] = N("4875"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-FUP41"),
            ["expiresAt"] = S("2000-09-01T00:00:00.0000000Z"),
            ["deliveryConfirmedAt"] = S(Ago(50)),
            ["disputeWindowEndsAt"] = S(Ago(2)),
            ["version"] = N("3")
        };
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

    private static Dictionary<string, AttributeValue> Delivery(string deliveryId, string status, string? orderId = null)
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

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<List<string>> ActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => (string)a["action"]).ToList();

    // ===== The overdue alarm of a release the payout gate blocks =====

    [LocalDynamoDbFact]
    public async Task BlockedRelease_NoLineTheFirstDay_ThenOneLineEveryRun_AuditedOnce_GoneOnceThePayeeVerifies()
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: false);
        var fresh = $"escrow-fup41-kyc-new-{_run}";
        var escrowId = $"escrow-fup41-kyc-{_run}";
        await PutAsync(_db.Escrows, ConfirmedEscrow(fresh, farmer, $"buyer-{_run}", $"del-{fresh}"));
        await PutAsync(_db.Deliveries, Delivery($"del-{fresh}", "delivered"));
        // Blocked by the same rule since a run a day and a bit ago (its reason and audit row are that run's).
        var blockedSince = Ago(30);
        var old = ConfirmedEscrow(escrowId, farmer, $"buyer-{_run}", $"del-{escrowId}");
        old[SettlementBlockFields.Reason] = S("release:payout:phone_unverified");
        old[SettlementBlockFields.ReasonAt] = S(blockedSince);
        await PutAsync(_db.Escrows, old);
        await PutAsync(_db.Deliveries, Delivery($"del-{escrowId}", "delivered"));

        await SweepAsync();
        await SweepAsync();

        Str(await EscrowRepo.GetByIdAsync(fresh, true), SettlementBlockFields.Reason).Should().Be("release:payout:phone_unverified");
        Alarms(MoneyAlarmLog.ReleaseBlockedOverdueToken, fresh).Should().BeEmpty("that block is new");
        Alarms(MoneyAlarmLog.ReleaseBlockedOverdueToken, escrowId).Should().HaveCount(2)
            .And.AllSatisfy(l => l.Should().EndWith("leg=farmer reason=phone_unverified ageHours=30"));
        var audits = (await AuditRepo.GetAuditTrailAsync(escrowId)).Where(a => (string)a["action"] == "release_blocked_kyc").ToList();
        audits.Should().ContainSingle("the block is still audited once").Which["timestamp"].Should().Be(blockedSince);
        Str(await EscrowRepo.GetByIdAsync(escrowId, true), SettlementBlockFields.ReasonAt).Should().Be(blockedSince);
        Paid($"release:{escrowId}").Should().Be(0);

        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Users,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = S(farmer) },
            UpdateExpression = "SET isPhoneVerified = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } }
        });
        await SweepAsync();

        Paid($"release:{escrowId}").Should().Be(1);
        Alarms(MoneyAlarmLog.ReleaseBlockedOverdueToken, escrowId).Should().HaveCount(2, "a paid release logs no more lines");
        (await EscrowRepo.GetByIdAsync(escrowId, true)).Should().NotContainKey(SettlementBlockFields.Reason);
    }

    // ===== The double escrow =====

    private async Task<(string InitiateId, string OrderEscrowId, string OrderId, string BuyerId)> SeedDoubleEscrowAsync(string ownStatus)
    {
        var farmer = await SeedUserAsync("farmer", phoneVerified: true);
        var buyer = await SeedUserAsync("buyer", phoneVerified: true);
        var orderId = $"ord-fup41-{Guid.NewGuid():N}";
        var orderEscrowId = $"escrow-order-{orderId}";
        var initiateId = $"escrow-fup41-init-{Guid.NewGuid():N}";
        var deliveryId = $"del-{orderId}";

        await PutAsync(_db.Escrows, ConfirmedEscrow(orderEscrowId, farmer, buyer, deliveryId, orderId, orderSourced: true, status: ownStatus));
        await PutAsync(_db.Escrows, ConfirmedEscrow(initiateId, farmer, buyer, deliveryId, orderId));
        await PutAsync(_db.Deliveries, Delivery(deliveryId, "delivered", orderId));
        await PutAsync(_db.Orders, new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(orderId),
            ["createdAt"] = S(Ago(24 * 10)),
            ["updatedAt"] = S(Ago(24 * 9)),
            ["status"] = S("disbursing"),
            ["buyerId"] = S(buyer),
            ["farmerId"] = S(farmer),
            ["activeDeliveryId"] = S(deliveryId),
            ["version"] = N("6")
        });
        return (initiateId, orderEscrowId, orderId, buyer);
    }

    private Task<EscrowResult<RefundEscrowResponse>> DoubleRefundAsync(string escrowId, string key, string admin = "admin-fup41")
        => Service().RefundAsync(new RefundEscrowRequest
        {
            EscrowId = escrowId,
            Trigger = "admin_override",
            Reason = "The buyer paid this delivery twice; refund the extra payment.",
            IdempotencyKey = $"admin:{escrowId}:double_escrow_refund:{key}",
            DoubleEscrow = true
        }, admin, "admin", new AuditContext { Actor = admin, ActorRole = "admin" }, adminGroupVerified: true);

    [LocalDynamoDbFact]
    public async Task DoubleEscrow_TheSweepBlocksIt_TheAdminRefundsIt_TheOrderAndItsEscrowStayAsTheyWere()
    {
        var (initiateId, orderEscrowId, orderId, _) = await SeedDoubleEscrowAsync("released");

        await SweepAsync();
        Paid($"release:{initiateId}").Should().Be(0);
        Str(await EscrowRepo.GetByIdAsync(initiateId, true), SettlementBlockFields.Reason).Should().Be("release:trade:double_escrow");
        var orderBefore = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        var ownBefore = await EscrowRepo.GetByIdAsync(orderEscrowId, true);

        var result = await DoubleRefundAsync(initiateId, "1763000000000");

        result.IsSuccess.Should().BeTrue(result.Error?.Code + " " + result.Error?.Message);
        Paid($"refund:{initiateId}").Should().Be(1);
        var refunded = await EscrowRepo.GetByIdAsync(initiateId, true);
        Str(refunded, "status").Should().Be("refunded");
        Str(refunded, DoubleEscrowFields.RefundBy).Should().Be("admin-fup41");
        // The sweep completed the released order escrow (nothing owed); the decision records the status it read.
        var ownStatus = Str(ownBefore, "status");
        ownStatus.Should().BeOneOf("released", "completed");
        Str(refunded, DoubleEscrowFields.OrderEscrowStatus).Should().Be(ownStatus);
        refunded.Should().NotContainKey(SettlementBlockFields.Reason);
        var trail = await AuditRepo.GetAuditTrailAsync(initiateId);
        var decision = trail.Single(a => (string)a["action"] == DoubleEscrowFields.AuditAction);
        decision["timestamp"].Should().Be(Str(refunded, DoubleEscrowFields.RefundAt));
        ((string)decision["metadata"]).Should().Be(
            $"orderId={orderId};orderEscrowId={orderEscrowId};orderEscrowStatus={ownStatus};reason=The buyer paid this delivery twice; refund the extra payment.");
        trail.Should().ContainSingle(a => (string)a["action"] == "refund" && (string)a["actor"] == "admin-fup41");
        (await TxRepo.GetByTransactionAsync(orderId)).Should()
            .ContainSingle(r => (string)r["action"] == DoubleEscrowFields.AuditAction && (string)r["entityId"] == initiateId);

        // The order and the order's own escrow are the other payment's: untouched.
        var orderAfter = await OrderRepo.GetByIdAsync(orderId, consistentRead: true);
        Str(orderAfter, "status").Should().Be(Str(orderBefore, "status"));
        Str(orderAfter, "version").Should().Be(Str(orderBefore, "version"));
        var ownAfter = await EscrowRepo.GetByIdAsync(orderEscrowId, true);
        Str(ownAfter, "status").Should().Be(Str(ownBefore, "status"));
        Str(ownAfter, "version").Should().Be(Str(ownBefore, "version"));

        // A replay (a double click, a lost answer) answers the refund made and writes nothing.
        var replay = await DoubleRefundAsync(initiateId, "1763000000001", admin: "admin-two");
        replay.IsSuccess.Should().BeTrue();
        replay.IsDuplicate.Should().BeTrue();
        Paid($"refund:{initiateId}").Should().Be(1);
        (await AuditRepo.GetAuditTrailAsync(initiateId)).Should().HaveCount(trail.Count);

        // The sweeps leave both alone.
        await SweepAsync();
        Paid($"release:{initiateId}").Should().Be(0);
        Paid($"refund:{initiateId}").Should().Be(1);
        Paid($"release:{orderEscrowId}").Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task DoubleEscrow_WhoseOrderEscrowIsOnlyInitiated_IsRefused_AndNothingIsWritten()
    {
        var (initiateId, _, _, _) = await SeedDoubleEscrowAsync("initiated");
        var before = await EscrowRepo.GetByIdAsync(initiateId, true);

        var result = await DoubleRefundAsync(initiateId, "1763000000002");

        result.Error!.Code.Should().Be(EscrowService.DoubleEscrowRefusalCode);
        Paid($"refund:{initiateId}").Should().Be(0);
        var after = await EscrowRepo.GetByIdAsync(initiateId, true);
        after.Should().NotContainKey(DoubleEscrowFields.RefundAt);
        Str(after, "status").Should().Be(Str(before, "status"));
        (await ActionsAsync(initiateId)).Should().NotContain(DoubleEscrowFields.AuditAction);
    }

    [LocalDynamoDbFact]
    public async Task TheDecisionWrite_IsConditionedOnWhatWasRead()
    {
        var (initiateId, _, orderId, _) = await SeedDoubleEscrowAsync("escrowed");
        var created = Str(await EscrowRepo.GetByIdAsync(initiateId, true), "createdAt")!;
        var repo = EscrowRepo;

        // Another order id, then a settlement lock: refused, nothing written.
        (await repo.TryRecordDoubleEscrowRefundAsync(initiateId, created, "ord-other", "a", "reason", Ago(0), "escrowed")).Should().BeFalse();
        (await repo.TryAcquireSettlementLockAsync(initiateId, created, "release", "escrowed")).Should().BeTrue();
        (await repo.TryRecordDoubleEscrowRefundAsync(initiateId, created, orderId, "a", "reason", Ago(0), "escrowed")).Should().BeFalse();
        await repo.ReleaseSettlementLockAsync(initiateId, created);

        // Unlocked: the first decision lands, a second one is refused and leaves the first.
        (await repo.TryRecordDoubleEscrowRefundAsync(initiateId, created, orderId, "admin-a", "first reason", "2026-11-13T09:00:00.0000000Z", "escrowed"))
            .Should().BeTrue();
        (await repo.TryRecordDoubleEscrowRefundAsync(initiateId, created, orderId, "admin-b", "second reason", Ago(0), "escrowed"))
            .Should().BeFalse();
        var row = await repo.GetByIdAsync(initiateId, true);
        Str(row, DoubleEscrowFields.RefundBy).Should().Be("admin-a");
        Str(row, DoubleEscrowFields.RefundReason).Should().Be("first reason");
        Str(row, "version").Should().Be("3", "no read returns the decision, so the version does not move");
    }
}

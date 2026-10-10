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

/// <summary>
/// ESC-04 against real DynamoDB (LocalStack in CI): a trucker leg the release could not pay keeps
/// the escrow released, the hourly run pays it with the stable reference once the trucker is
/// verified, and nothing ever pays the trucker twice. Every new conditional write runs here for
/// real (an unused expression name or a wrong condition fails in DynamoDB, not in a mock). The
/// tables are shared with other suites, so ids are unique per test and gateway payouts are counted
/// per transaction reference.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TruckerLegRetryDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();

    /// <summary>Gateway calls and payouts made, per transaction reference.</summary>
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentDictionary<string, int> _paid = new();

    /// <summary>Answers queued per reference; an empty queue pays.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<ReleaseFundsResponse>>> _answers = new();

    private readonly List<string> _alarms = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];

    public TruckerLegRetryDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            var reference = call.Arg<ReleaseFundsRequest>().TransactionRef;
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            if (_answers.TryGetValue(reference, out var queue) && queue.TryDequeue(out var answer))
            {
                return answer();
            }

            _paid.AddOrUpdate(reference, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{Guid.NewGuid():N}" };
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private UserRepository UserRepo => new(Ddb, _db.Users);
    private EscrowIdempotencyRepository IdempotencyRepo => new(Ddb, _db.Idempotency);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);

    private string FarmerId => $"farmer-esc04-{_run}";
    private string TruckerId => $"trucker-esc04-{_run}";

    private EscrowService Service(IEscrowRepository? escrowRepo = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        UserRepo, OrderRepo, DeliveryRepo, orderCheckoutRepo: null,
        transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit), cropRepo: new CropRepository(Ddb, _db.Crops))
    {
        AlarmLog = line =>
        {
            lock (_alarms)
            {
                _alarms.Add(line);
            }
        }
    };

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private static string Ago(double minutes) => DateTime.UtcNow.AddMinutes(-minutes).ToString("o");

    private int Calls(string reference) => _calls.TryGetValue(reference, out var n) ? n : 0;
    private int Paid(string reference) => _paid.TryGetValue(reference, out var n) ? n : 0;

    private void Answer(string reference, params Func<ReleaseFundsResponse>[] answers)
        => _answers[reference] = new ConcurrentQueue<Func<ReleaseFundsResponse>>(answers);

    private async Task SeedUsersAsync(bool truckerPhoneVerified)
    {
        foreach (var (id, role, verified) in new[] { (FarmerId, "farmer", true), (TruckerId, "trucker", truckerPhoneVerified) })
        {
            var created = await UserRepo.CreateUserAsync(new UserDto
            {
                UserId = id,
                PhoneNumber = $"+2519{Math.Abs(id.GetHashCode()) % 100_000_000:D8}",
                DisplayName = id,
                Role = role,
                Woreda = "test-woreda",
                IsPhoneVerified = verified,
                FaydaVerified = true,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
                Version = 1
            });
            created.IsSuccess.Should().BeTrue();
        }
    }

    private Task VerifyTruckerAsync() => Ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = _db.Users,
        Key = new Dictionary<string, AttributeValue> { ["userId"] = S(TruckerId) },
        UpdateExpression = "SET isPhoneVerified = :t",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":t"] = new() { BOOL = true } }
    });

    /// <summary>An escrowed escrow ready for auto-release, with a 1,000 ETB trucker leg.</summary>
    private async Task<string> SeedReleasableAsync(bool withTruckerId = true, string? orderId = null, string? deliveryId = null)
    {
        var escrowId = $"escrow-esc04-{Guid.NewGuid():N}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(Ago(1)),
            ["status"] = S("escrowed"),
            ["buyerId"] = S($"buyer-esc04-{_run}"),
            ["farmerId"] = S(FarmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("6800"),
            ["truckerPayout"] = N("1000"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-ESC04"),
            ["expiresAt"] = S("2026-10-08T10:00:00.0000000Z"),
            ["deliveryConfirmedAt"] = S(Ago(60 * 50)),
            ["disputeWindowEndsAt"] = S(Ago(60 * 2)),
            ["version"] = N("3")
        };
        if (withTruckerId)
        {
            item["truckerId"] = S(TruckerId);
        }

        if (orderId != null)
        {
            item["orderId"] = S(orderId);
            item["source"] = S("order");
        }

        if (deliveryId != null)
        {
            item["deliveryId"] = S(deliveryId);
        }
        else if (orderId == null)
        {
            // ESC-10: the auto-release pays only a delivery that is delivered.
            item["deliveryId"] = S($"del-{escrowId}");
            await Ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _db.Deliveries,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["deliveryId"] = S($"del-{escrowId}"), ["tripId"] = S($"trip-{escrowId}"), ["status"] = S("delivered")
                }
            });
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    /// <summary>A released escrow whose trucker leg went pending <paramref name="hoursAgo"/> ago.</summary>
    private async Task<string> SeedPendingAsync(
        bool withTruckerId = true, string? orderId = null, string? deliveryId = null, double hoursAgo = 2,
        string status = "released", bool pendingSince = true)
    {
        var escrowId = await SeedReleasableAsync(withTruckerId, orderId, deliveryId);
        var values = new Dictionary<string, AttributeValue>
        {
            [":status"] = S(status),
            [":ref"] = S("GW-FARMER-SEEDED"),
            [":at"] = S(Ago(60 * hoursAgo)),
            [":t"] = new() { BOOL = true },
            [":op"] = S("release")
        };
        var sets = "#status = :status, telebirrReleaseRef = :ref, releasedAt = :at, truckerPayoutPending = :t, settlementLock = :op";
        if (pendingSince)
        {
            sets += ", truckerPayoutPendingSince = :at";
        }

        if (status == "completed")
        {
            sets += ", completedAt = :at";
        }

        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = Key(escrowId),
            UpdateExpression = "SET " + sets,
            ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
            ExpressionAttributeValues = values
        });
        return escrowId;
    }

    private static Dictionary<string, AttributeValue> Key(string escrowId)
        => new() { ["escrowId"] = S(escrowId), ["createdAt"] = S(CreatedAt) };

    private async Task SweepAsync(EscrowService? service = null)
    {
        var cron = new EscrowCronFunction(service ?? Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo);
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" },
            new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4) });
        response.StatusCode.Should().Be(200, response.Body);
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<List<string>> AuditActionsAsync(string escrowId)
        => (await AuditRepo.GetAuditTrailAsync(escrowId)).Select(a => a["action"].ToString()!).ToList();

    private static string TruckerRef(string escrowId) => $"release:{escrowId}:trucker";

    /// <summary>The admin list item of one escrow; the queue is shared with other suites, so it pages.</summary>
    private async Task<ManualPayoutDto> FindManualPayoutAsync(string escrowId)
    {
        string? next = null;
        do
        {
            var page = (await Service().ListManualPayoutsAsync(50, next)).Value!;
            var found = page.Payouts.FirstOrDefault(p => p.EscrowId == escrowId);
            if (found != null)
            {
                return found;
            }

            next = page.NextToken;
        } while (next != null);

        throw new InvalidOperationException($"{escrowId} is not in the manual payout queue.");
    }

    private static ReleaseFundsResponse Refused() => new() { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" };

    // ──────────────────────────────────────────────
    // Done when: a blocked trucker leg is paid after verification
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task BlockedTruckerLeg_IsPaidAfterVerification_AndNobodyIsPaidTwice()
    {
        await SeedUsersAsync(truckerPhoneVerified: false);
        var escrowId = await SeedReleasableAsync();

        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "truckerPayoutPending").Should().Be("True");
        Str(row, "truckerPayoutBlockReason").Should().Be("phone_unverified");
        Paid($"release:{escrowId}").Should().Be(1);
        Calls(TruckerRef(escrowId)).Should().Be(0);

        await SweepAsync();
        Str(await EscrowAsync(escrowId), "status").Should().Be("released");
        (await AuditActionsAsync(escrowId)).Count(a => a == "trucker_release_blocked_kyc").Should().Be(1);

        await VerifyTruckerAsync();
        await SweepAsync();

        row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("completed");
        Str(row, "truckerTelebirrRef").Should().StartWith("GW-");
        Str(row, "truckerPaidAt").Should().NotBeNull();
        Str(row, "truckerPayoutPending").Should().BeNull();
        Str(row, "truckerPayoutBlockReason").Should().BeNull();
        Str(row, "truckerPayoutLockId").Should().BeNull();
        var actions = await AuditActionsAsync(escrowId);
        actions.Should().Contain(new[] { "release", "trucker_release_blocked_kyc", "trucker_release", "complete" });
        actions.Count(a => a == "complete").Should().Be(1);

        await SweepAsync();
        await SweepAsync();
        Paid($"release:{escrowId}").Should().Be(1);
        Paid(TruckerRef(escrowId)).Should().Be(1);
        Calls(TruckerRef(escrowId)).Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // No double pay on retry
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task RetryPaysButTheRecordNeverLands_IsParkedOnceTheLockIsStale_AndTheTruckerIsPaidOnce()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedReleasableAsync();
        Answer(TruckerRef(escrowId), Refused);
        await SweepAsync();
        Str(await EscrowAsync(escrowId), "status").Should().Be("released");

        var crashing = Service(new FaultyEscrowRepository(EscrowRepo)
        {
            OnRecordTruckerPayout = id =>
            {
                if (id == escrowId)
                {
                    throw new AmazonDynamoDBException("write lost");
                }
            }
        });
        await SweepAsync(crashing);
        Paid(TruckerRef(escrowId)).Should().Be(1);
        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "truckerPayoutLockId").Should().NotBeNull();

        await SweepAsync();
        Calls(TruckerRef(escrowId)).Should().Be(2, "a fresh trucker lock is left to its owner");

        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = Key(escrowId),
            UpdateExpression = "SET truckerPayoutLockAt = :at",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":at"] = S(Ago(16)) }
        });
        await SweepAsync();
        await SweepAsync();

        row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        Str(row, "manualPayoutFromStatus").Should().Be("released");
        Str(row, "manualFarmerStatus").Should().Be("paid");
        Str(row, "manualFarmerPaidBy").Should().Be("gateway");
        Str(row, "manualTruckerStatus").Should().Be("pending");
        (await AuditActionsAsync(escrowId)).Should().Contain(new[] { "stale_lock_parked", "manual_payout_queued" });
        Calls(TruckerRef(escrowId)).Should().Be(2);

        var close = await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-FOUND-IN-PORTAL", "admin-esc04", adminGroupVerified: true);
        close.IsSuccess.Should().BeTrue(close.Error?.Message);
        row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("completed");
        Str(row, "truckerTelebirrRef").Should().Be("TB-FOUND-IN-PORTAL");
        Str(row, "truckerPayoutPending").Should().BeNull();

        await SweepAsync();
        Paid(TruckerRef(escrowId)).Should().Be(1);
        Paid($"release:{escrowId}").Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task DefiniteRefusals_ReleaseTheTruckerLock_AndTheNextRunsRetryWithTheSameReference()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedReleasableAsync();
        Answer(TruckerRef(escrowId), Refused, Refused);

        await SweepAsync();
        await SweepAsync();
        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "truckerPayoutLockId").Should().BeNull();

        await SweepAsync();

        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        Calls(TruckerRef(escrowId)).Should().Be(3);
        Paid(TruckerRef(escrowId)).Should().Be(1);
        _calls.Keys.Where(k => k.Contains(escrowId)).Should().BeEquivalentTo(new[] { $"release:{escrowId}", TruckerRef(escrowId) });
    }

    [LocalDynamoDbFact]
    public async Task AnUnknownOutcomeOnRetry_IsQueuedAndNeverRetried_AndTheAdminCloseCompletes()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedPendingAsync();
        Answer(TruckerRef(escrowId), () => throw new HttpRequestException("reset"));

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        Calls(TruckerRef(escrowId)).Should().Be(1);

        (await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-PORTAL-1", "admin-esc04", adminGroupVerified: true))
            .IsSuccess.Should().BeTrue();
        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        Paid(TruckerRef(escrowId)).Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task PayoutNotSupportedOnRetry_IsQueued_AndTheCloseKeepsTheReleaseAndCompletes()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedPendingAsync();
        var releasedAt = Str(await EscrowAsync(escrowId), "releasedAt");
        Answer(TruckerRef(escrowId), () => new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported });

        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("PAYOUT_NOT_SUPPORTED");
        var payout = await FindManualPayoutAsync(escrowId);
        payout.EscrowStatus.Should().Be("released");
        payout.SettlementLockedAt.Should().Be(Str(row, "truckerPayoutLockAt"));

        (await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-PORTAL-2", "admin-esc04", adminGroupVerified: true))
            .Value!.EscrowStatus.Should().Be("completed");

        row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("completed");
        Str(row, "releasedAt").Should().Be(releasedAt);
        Str(row, "telebirrReleaseRef").Should().Be("GW-FARMER-SEEDED");
        Str(row, "truckerTelebirrRef").Should().Be("TB-PORTAL-2");
        Str(row, "truckerPayoutPending").Should().BeNull();
        Str(row, "truckerPayoutLockId").Should().BeNull();
        Str(row, "manualPayout").Should().BeNull();
        var actions = await AuditActionsAsync(escrowId);
        actions.Should().NotContain("release");
        actions.Count(a => a == "complete").Should().Be(1);
    }

    // ──────────────────────────────────────────────
    // Real conditions
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task RealConditions_CompletedIsRefusedWhileALegIsOwedOrQueued_AndTheCrashRecoveryLeavesItReleased()
    {
        await SeedUsersAsync(truckerPhoneVerified: false);
        var pending = await SeedPendingAsync();

        var refused = () => EscrowRepo.UpdateStatusAsync(pending, CreatedAt, "completed", "released",
            new Dictionary<string, object> { { "completedAt", DateTime.UtcNow.ToString("o") } });
        await refused.Should().ThrowAsync<ConditionalCheckFailedException>();

        await SweepAsync();
        Str(await EscrowAsync(pending), "status").Should().Be("released");

        var queued = await SeedReleasableAsync();
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = Key(queued),
            UpdateExpression = "SET #status = :released, manualPayout = :open, manualPayoutQueuedAt = :at",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":released"] = S("released"), [":open"] = S("open"), [":at"] = S(Ago(1))
            }
        });
        var refusedQueued = () => EscrowRepo.UpdateStatusAsync(queued, CreatedAt, "completed", "released");
        await refusedQueued.Should().ThrowAsync<ConditionalCheckFailedException>();
    }

    [LocalDynamoDbFact]
    public async Task RealConditions_TheTruckerLock_IsOneAtATime_AndNeverOnAQueuedOrUnreleasedRow()
    {
        var pending = await SeedPendingAsync();
        var first = await EscrowRepo.TryAcquireTruckerPayoutLockAsync(pending, CreatedAt);
        first.Should().NotBeNull();
        (await EscrowRepo.TryAcquireTruckerPayoutLockAsync(pending, CreatedAt)).Should().BeNull();
        (await EscrowRepo.ReleaseTruckerPayoutLockAsync(pending, CreatedAt, "not-ours")).Should().BeFalse();
        (await EscrowRepo.ReleaseTruckerPayoutLockAsync(pending, CreatedAt, first!)).Should().BeTrue();
        Str(await EscrowAsync(pending), "truckerPayoutLockAt").Should().BeNull();

        var escrowed = await SeedReleasableAsync();
        (await EscrowRepo.TryAcquireTruckerPayoutLockAsync(escrowed, CreatedAt)).Should().BeNull();

        var queued = await SeedPendingAsync();
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Escrows,
            Key = Key(queued),
            UpdateExpression = "SET manualPayout = :open",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":open"] = S("open") }
        });
        (await EscrowRepo.TryAcquireTruckerPayoutLockAsync(queued, CreatedAt)).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task TwoRetriesAtOnce_CallTheGatewayOnce()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedPendingAsync();
        var row = await EscrowAsync(escrowId);

        var results = await Task.WhenAll(
            Service().RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true),
            Service().RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true));

        results.Count(r => r.Outcome == EscrowService.TruckerRetryOutcome.Paid).Should().Be(1);
        Calls(TruckerRef(escrowId)).Should().Be(1);
        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
    }

    [LocalDynamoDbFact]
    public async Task TwoRunsSeeingTheSameBlock_AuditItOnce()
    {
        await SeedUsersAsync(truckerPhoneVerified: false);
        var escrowId = await SeedPendingAsync();
        var row = await EscrowAsync(escrowId);

        await Task.WhenAll(
            Service().RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true),
            Service().RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true));

        (await AuditActionsAsync(escrowId)).Count(a => a == "trucker_release_blocked_kyc").Should().Be(1);
        Str(await EscrowAsync(escrowId), "truckerPayoutBlockReason").Should().Be("phone_unverified");
    }

    // ──────────────────────────────────────────────
    // Trucker backfill (R8)
    // ──────────────────────────────────────────────

    private async Task SeedOrderAndDeliveryAsync(string orderId, string deliveryId, string deliveryTruckerId)
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId), ["createdAt"] = S(CreatedAt), ["status"] = S("delivered"),
                ["truckerId"] = S(TruckerId), ["version"] = N("4")
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId), ["tripId"] = S($"trip-{deliveryId}"), ["status"] = S("delivered"),
                ["truckerId"] = S(deliveryTruckerId)
            }
        });
    }

    [LocalDynamoDbFact]
    public async Task AnEscrowWithoutTruckerId_TakesTheOrdersTrucker_AndPaysIt()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var orderId = $"ord-esc04-{Guid.NewGuid():N}";
        var deliveryId = $"del-esc04-{Guid.NewGuid():N}";
        await SeedOrderAndDeliveryAsync(orderId, deliveryId, TruckerId);
        var escrowId = await SeedPendingAsync(withTruckerId: false, orderId: orderId, deliveryId: deliveryId);

        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "truckerId").Should().Be(TruckerId);
        Str(row, "status").Should().Be("completed");
        (await AuditActionsAsync(escrowId)).Should().Contain("trucker_backfilled");
        Paid(TruckerRef(escrowId)).Should().Be(1);
        // ESC-09: the last leg is paid, so the order follows the money to settled (two hops).
        var order = (await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!;
        Str(order, "status").Should().Be("settled");
        Str(order, "version").Should().Be("6");
    }

    [LocalDynamoDbFact]
    public async Task AnEscrowWithoutTruckerId_WhoseDeliveryNamesAnotherTrucker_IsBlocked_NeverBackfilled()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var orderId = $"ord-esc04-{Guid.NewGuid():N}";
        var deliveryId = $"del-esc04-{Guid.NewGuid():N}";
        await SeedOrderAndDeliveryAsync(orderId, deliveryId, "trucker-somebody-else");
        var escrowId = await SeedPendingAsync(withTruckerId: false, orderId: orderId, deliveryId: deliveryId);

        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "truckerId").Should().BeNull();
        Str(row, "status").Should().Be("released");
        Str(row, "truckerPayoutBlockReason").Should().Be("trucker_mismatch");
        Calls(TruckerRef(escrowId)).Should().Be(0);
        // ESC-09: the escrow stays released while the trucker leg is owed, so the order is disbursing
        // (the hourly order-money sweep in the same run moves it), never settled.
        Str((await OrderRepo.GetByIdAsync(orderId, consistentRead: true))!, "status").Should().Be("disbursing");
    }

    // ──────────────────────────────────────────────
    // Legacy completed rows
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task LegacyCompletedRow_IsReopenedAndQueuedOnce_ThroughTheDeployEnvelope_NeverCalled()
    {
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedPendingAsync(status: "completed", pendingSince: false, hoursAgo: 24 * 20);
        var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
            idempotencyRepo: IdempotencyRepo);
        var request = new APIGatewayProxyRequest { Body = "{\"action\":\"park_legacy_trucker_legs\"}" };

        var first = await cron.FunctionHandler(request, new TestLambdaContext());
        var second = await cron.FunctionHandler(request, new TestLambdaContext());

        first.StatusCode.Should().Be(200, first.Body);
        second.StatusCode.Should().Be(200, second.Body);
        // The second run found nothing: the marker is clean (done only after a later clean run).
        second.Body.Should().Contain("\"found\":\"0\"");
        var marker = await IdempotencyRepo.GetAsync(EscrowCronFunction.LegacyTruckerMarkerKey);
        Str(marker!, "status").Should().BeOneOf("clean", "done");
        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "completedAt").Should().BeNull();
        Str(row, "truckerPayoutPendingSince").Should().NotBeNull();
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        Str(row, "manualFarmerStatus").Should().Be("paid");
        Str(row, "manualTruckerStatus").Should().Be("pending");
        (await AuditActionsAsync(escrowId)).Count(a => a == "legacy_trucker_parked").Should().Be(1);
        _alarms.Should().ContainSingle(l => l.Contains($"escrowId={escrowId.Replace('_', '-')} action=legacy_trucker_parked"));
        Calls(TruckerRef(escrowId)).Should().Be(0);

        // The hourly run leaves the queued leg to the admin, who closes it.
        await SweepAsync();
        Calls(TruckerRef(escrowId)).Should().Be(0);
        (await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-PORTAL-LEGACY", "admin-esc04", adminGroupVerified: true))
            .Value!.EscrowStatus.Should().Be("completed");
    }

    [LocalDynamoDbFact]
    public async Task LegacyAction_ADoneMarker_ReadsNothing()
    {
        await IdempotencyRepo.StoreAsync(EscrowCronFunction.LegacyTruckerMarkerKey, "-", "clean");
        await IdempotencyRepo.UpdateAsync(EscrowCronFunction.LegacyTruckerMarkerKey, "done");
        try
        {
            var escrowId = await SeedPendingAsync(status: "completed", pendingSince: false);
            var cron = new EscrowCronFunction(Service(), EscrowRepo, AuditRepo, _gateway, DeliveryRepo, OrderRepo,
                idempotencyRepo: IdempotencyRepo);

            var response = await cron.FunctionHandler(
                new APIGatewayProxyRequest { Body = "{\"action\":\"park_legacy_trucker_legs\"}" }, new TestLambdaContext());

            response.StatusCode.Should().Be(200, response.Body);
            response.Body.Should().Contain("\"skipped\":\"true\"");
            Str(await EscrowAsync(escrowId), "status").Should().Be("completed");

            // Leave nothing the other legacy test could trip on.
            await Ddb.DeleteItemAsync(new DeleteItemRequest { TableName = _db.Escrows, Key = Key(escrowId) });
        }
        finally
        {
            await IdempotencyRepo.DeleteAsync(EscrowCronFunction.LegacyTruckerMarkerKey);
        }
    }

    [LocalDynamoDbFact]
    public async Task ALegacyReleasedRow_IsParkedByTheHourlyRun_AndNeverPaid()
    {
        // Item 1: pending before ESC-04 (no pending time), when the runbook said to pay by hand.
        await SeedUsersAsync(truckerPhoneVerified: true);
        var escrowId = await SeedPendingAsync(pendingSince: false, hoursAgo: 24 * 3);

        await SweepAsync();
        await SweepAsync();

        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("released");
        Str(row, "manualPayout").Should().Be("open");
        Str(row, "manualPayoutReason").Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        Str(row, "manualPayoutFromStatus").Should().Be("released");
        Str(row, "manualFarmerStatus").Should().Be("paid");
        Str(row, "manualTruckerStatus").Should().Be("pending");
        Str(row, "truckerPayoutPendingSince").Should().NotBeNull();
        Calls(TruckerRef(escrowId)).Should().Be(0);
        (await AuditActionsAsync(escrowId)).Count(a => a == "legacy_trucker_parked").Should().Be(1);

        (await Service().CompleteManualPayoutAsync(escrowId, "trucker", "TB-PORTAL-OLD", "admin-esc04", adminGroupVerified: true))
            .Value!.EscrowStatus.Should().Be("completed");
    }

    [LocalDynamoDbFact]
    public async Task ALostBlockAuditRow_IsReEnsuredByTheNextRun_AtTheSameKey()
    {
        // Item 3: the reason won its compare-and-set but its audit row was lost.
        await SeedUsersAsync(truckerPhoneVerified: false);
        var escrowId = await SeedPendingAsync();
        await SweepAsync();
        var trail = await AuditRepo.GetAuditTrailAsync(escrowId);
        var blocked = trail.Single(a => (string)a["action"] == "trucker_release_blocked_kyc");
        var key = (string)blocked["timestamp"];
        Str(await EscrowAsync(escrowId), "truckerPayoutBlockReasonAt").Should().Be(key);
        await Ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _db.EscrowAudit,
            Key = new Dictionary<string, AttributeValue> { ["escrowId"] = S(escrowId), ["timestamp"] = S(key) }
        });

        await SweepAsync();
        await SweepAsync();

        var again = (await AuditRepo.GetAuditTrailAsync(escrowId)).Where(a => (string)a["action"] == "trucker_release_blocked_kyc").ToList();
        again.Should().ContainSingle();
        again[0]["timestamp"].Should().Be(key);
    }
}

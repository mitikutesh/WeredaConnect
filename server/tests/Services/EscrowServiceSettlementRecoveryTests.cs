using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-05: no double pay, no stuck lock. A gateway answer that does not say whether the money
/// moved (a timeout, a lost or unreadable answer, a call that threw) keeps the settlement lock and
/// parks the escrow in the manual payout queue as GATEWAY_OUTCOME_UNKNOWN, never a blind retry.
/// The refund status write retries like the release write, nothing that can fail sits between the
/// lock and the gateway call, and a lock older than 15 minutes is parked by the recovery step.
/// </summary>
public class EscrowServiceSettlementRecoveryTests
{
    private const string EscrowId = "escrow-order-ord-5";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string FarmerId = "farmer-5";
    private const string TruckerId = "trucker-5";
    private const string BuyerId = "buyer-5";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly List<string> _alarmLines = new();
    private readonly EscrowService _service;

    public EscrowServiceSettlementRecoveryTests()
    {
        _escrowRepo.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        QueueWrites(true);
        _idempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(call => new ReleaseFundsResponse
            {
                Success = true,
                TelebirrRef = $"GW-{call.Arg<ReleaseFundsRequest>().TransactionRef}"
            });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "GW-REFUND-1" });
        _userRepo.GetUserByIdAsync(FarmerId).Returns(User(FarmerId, "+251911000051"));
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "+251911000052"));
        _userRepo.GetUserByIdAsync(BuyerId).Returns(User(BuyerId, "+251911000053"));

        _service = new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway, _userRepo)
        {
            AlarmLog = _alarmLines.Add
        };
    }

    // ──────────────────────────────────────────────
    // Builders
    // ──────────────────────────────────────────────

    private static UserDto User(string id, string phone) => new()
    {
        UserId = id,
        PhoneNumber = phone,
        DisplayName = id,
        Role = "farmer",
        Woreda = "test-woreda",
        IsPhoneVerified = true,
        FaydaVerified = true,
        FaydaId = $"FAYDA-{id}",
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    private static Dictionary<string, object> Escrow(bool withTrucker = false, string status = "escrowed")
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["orderId"] = "ord-5",
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["amount"] = 20_000m,
            ["commission"] = 500m,
            ["farmerPayout"] = withTrucker ? 16_500m : 19_500m,
            ["currency"] = "ETB",
            ["status"] = status,
            ["telebirrHoldRef"] = "HOLD-5",
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["expiresAt"] = "2026-10-08T10:00:00.0000000Z",
            ["version"] = 3
        };
        if (withTrucker)
        {
            item["truckerPayout"] = 3_000m;
            item["truckerId"] = TruckerId;
        }

        return item;
    }

    /// <summary>An escrow as the recovery query returns it: a lock taken <paramref name="minutesAgo"/> ago.</summary>
    private static Dictionary<string, object> Locked(
        string op, double minutesAgo, bool withTrucker = false, string status = "escrowed", bool legacy = false)
    {
        var item = Escrow(withTrucker, status);
        var at = Now.AddMinutes(-minutesAgo).ToString("o");
        item[SettlementLockFields.Lock] = op;
        if (legacy)
        {
            item["updatedAt"] = at;
        }
        else
        {
            item[SettlementLockFields.LockAt] = at;
            item[SettlementLockFields.LockId] = "lock-id-5";
        }

        return item;
    }

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static ReleaseEscrowRequest ReleaseRequest(string hour = "2026100912") => new()
    {
        EscrowId = EscrowId,
        Trigger = "delivery_confirmed",
        DeliveryId = "del-5",
        IdempotencyKey = $"system:{EscrowId}:auto_release:{hour}"
    };

    private static RefundEscrowRequest RefundRequest(string hour = "2026100912") => new()
    {
        EscrowId = EscrowId,
        Trigger = "delivery_timeout",
        Reason = "expired",
        IdempotencyKey = $"system:{EscrowId}:auto_refund:{hour}"
    };

    private void QueueWrites(bool result)
        => _escrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(result);

    private Task<bool> QueuedWith(string op, string reason, Func<IReadOnlyList<ManualPayoutLegSeed>, bool> legs)
        => _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            EscrowId, CreatedAt, op, reason, "escrowed", Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(l => legs(l)), Arg.Any<string>());

    private List<Dictionary<string, object>> AuditEntries()
        => _auditRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is nameof(IEscrowAuditRepository.AppendAsync)
                or nameof(IEscrowAuditRepository.AppendIfAbsentAsync))
            .Select(c => c.GetArguments()[0])
            .OfType<Dictionary<string, object>>()
            .ToList();

    private async Task AssertLockKeptAndNothingSettled()
    {
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _escrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _idempotencyRepo.DidNotReceive().DeleteAsync(Arg.Any<string>());
    }

    private static void AssertParked<T>(EscrowResult<T> result)
    {
        result.IsSuccess.Should().BeFalse();
        result.Error!.HttpStatus.Should().Be(202);
        result.Error.Code.Should().Be("ESCROW_034");
        result.Error.Message.Should().Contain("may already have been made");
    }

    // ──────────────────────────────────────────────
    // Unknown gateway outcomes: parked, never retried
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_Timeout_IsParked_AndTheNextSweepNeverPaysAgain()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout });

        var first = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(first);
        await QueuedWith(ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonGatewayOutcomeUnknown, legs =>
            legs.Count == 2 && legs.All(l => l.Status == ManualPayoutFields.LegPending)
            && legs[0].Leg == "farmer" && legs[1].Leg == "trucker");
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT");

        // The next hourly sweep (a new idempotency key) finds the escrow queued.
        var queued = Escrow(withTrucker: true);
        queued[SettlementLockFields.Lock] = "release";
        queued[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        queued[ManualPayoutFields.Op] = "release";
        queued[ManualPayoutFields.Reason] = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
        _escrowRepo.GetByIdAsync(EscrowId).Returns(queued);

        var second = await _service.ReleaseAsync(ReleaseRequest("2026100913"), EscrowService.SystemActor);

        second.Error!.HttpStatus.Should().Be(202);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.Received(1).TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("TaskCanceledException")]
    [InlineData("HttpRequestException")]
    public async Task Release_GatewayThrows_IsAnUnknownOutcome(string name)
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        Exception thrown = name == "TaskCanceledException" ? new TaskCanceledException() : new HttpRequestException();
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).ThrowsAsync(thrown);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(result);
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            $"ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=release_failed outcome=unknown code=GATEWAY_EXCEPTION:{name}");
    }

    [Fact]
    public async Task Release_NoGatewayAnswer_IsAnUnknownOutcome()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns((ReleaseFundsResponse)null!);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(result);
        _alarmLines.Should().ContainSingle().Which.Should().Contain("outcome=unknown code=TELEBIRR_NETWORK");
    }

    [Fact]
    public async Task Refund_Timeout_ParksTheBuyerLeg_AndKeepsTheLock()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Network });

        var result = await _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        AssertParked(result);
        await QueuedWith(ManualPayoutFields.OpRefund, ManualPayoutFields.ReasonGatewayOutcomeUnknown,
            legs => legs.Count == 1 && legs[0].Leg == "buyer");
        await AssertLockKeptAndNothingSettled();
        await _idempotencyRepo.Received(1).UpdateAsync(RefundRequest().IdempotencyKey, "manual_payout");
    }

    [Fact]
    public async Task Release_UnknownOutcome_FailureAuditFails_StillQueues()
    {
        // O4: the failure row is best-effort; its alarm line is already out.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout });
        _auditRepo.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new AmazonDynamoDBException("down"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(result);
        _alarmLines.Should().ContainSingle();
    }

    [Fact]
    public async Task Release_UnknownOutcome_QueueWriteKeepsFailing_KeepsTheLock_AndIsNotRetried()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout });
        QueueWrites(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.Error!.HttpStatus.Should().Be(502);
        result.Error.Code.Should().Be("ESCROW_013");
        result.Error.Message.Should().Contain("not retried");
        await _escrowRepo.Received(4).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT",
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_OUTCOME_UNKNOWN");
    }

    // ──────────────────────────────────────────────
    // FUP-03: a reference-less "success" plus a failed queue write
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_RefMissing_QueueWriteKeepsFailing_KeepsTheLockAndTheKey()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "" });
        QueueWrites(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        // Before ESC-05 this released the lock and deleted the key, so the next sweep paid again.
        result.Error!.HttpStatus.Should().Be(502);
        await _escrowRepo.Received(4).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), ManualPayoutFields.ReasonGatewayRefMissing,
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING");
    }

    [Fact]
    public async Task Refund_RefMissing_QueueWriteKeepsFailing_KeepsTheLockAndTheKey()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = null });
        QueueWrites(false);

        var result = await _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        result.Error!.HttpStatus.Should().Be(502);
        await AssertLockKeptAndNothingSettled();
    }

    [Fact]
    public async Task Release_PayoutNotSupported_QueueWriteFails_StillUnlocks_OnlyOnce()
    {
        // Nothing moved: one attempt, then the retryable path (unlock first, then the audit row).
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported });
        QueueWrites(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.Error!.HttpStatus.Should().Be(502);
        await _escrowRepo.Received(1).TryQueueManualPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        Received.InOrder(() =>
        {
            _escrowRepo.ReleaseSettlementLockAsync(EscrowId, CreatedAt);
            _auditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "release_failed"));
        });
        _alarmLines.Should().ContainSingle().Which.Should().Contain("outcome=failed");
    }

    // ──────────────────────────────────────────────
    // Definite refusals keep the retry
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_DefiniteRefusal_UnlocksBeforeTheAudit_AndAFailedAuditCannotStrandTheLock()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });
        _auditRepo.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        await _escrowRepo.Received(1).ReleaseSettlementLockAsync(EscrowId, CreatedAt);
        await _idempotencyRepo.Received(1).DeleteAsync(ReleaseRequest().IdempotencyKey);
        // The lock is already gone, so the failure is a plain one, not an unknown outcome.
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
        _alarmLines.Should().ContainSingle().Which.Should().Contain("outcome=failed code=TELEBIRR_RELEASE_FAILED");
    }

    // ──────────────────────────────────────────────
    // The trucker leg
    // ──────────────────────────────────────────────

    private void FarmerPaidTruckerGets(Func<ReleaseFundsResponse> trucker)
    {
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => !r.TransactionRef.EndsWith(":trucker")))
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-FARMER-5" });
        _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef.EndsWith(":trucker")))
            .Returns(_ => trucker());
    }

    [Fact]
    public async Task Release_TruckerTimeoutAfterTheFarmerWasPaid_QueuesTheTruckerLeg_WithTheFarmerPaid()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        FarmerPaidTruckerGets(() => new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout });

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(result);
        await QueuedWith(ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonGatewayOutcomeUnknown, legs =>
            legs.Count == 2
            && legs[0].Leg == "farmer" && legs[0].Status == "paid" && legs[0].Ref == "GW-FARMER-5"
            && legs[0].PaidBy == ManualPayoutFields.PaidByGateway
            && legs[1].Leg == "trucker" && legs[1].Status == "pending");
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=trucker_release_failed outcome=unknown code=TELEBIRR_TIMEOUT");
    }

    [Fact]
    public async Task Release_TruckerCallThrows_IsQueued_NotStranded()
    {
        // Before ESC-05 the exception escaped with the farmer paid and nothing recorded.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        FarmerPaidTruckerGets(() => throw new HttpRequestException("reset"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        AssertParked(result);
        await _gateway.Received(2).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        _alarmLines.Should().ContainSingle().Which.Should().Contain("code=GATEWAY_EXCEPTION:HttpRequestException");
    }

    [Fact]
    public async Task Release_TruckerUnknown_QueueWriteKeepsFailing_NeverCompletesWithTruckerPayoutPending()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        FarmerPaidTruckerGets(() => new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Network });
        QueueWrites(false);

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.Error!.HttpStatus.Should().Be(502);
        await AssertLockKeptAndNothingSettled();
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=trucker_release_failed outcome=unknown code=TELEBIRR_NETWORK",
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=trucker_release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_OUTCOME_UNKNOWN");
    }

    [Fact]
    public async Task Release_TruckerDefiniteRefusal_StillCompletesWithTruckerPayoutPending()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        FarmerPaidTruckerGets(() => new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (bool)f["truckerPayoutPending"]));
        // The leg audit rows follow the status write (ESC-05).
        Received.InOrder(() =>
        {
            _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed", Arg.Any<Dictionary<string, object>?>());
            _auditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "trucker_release_failed"));
        });
    }

    // ──────────────────────────────────────────────
    // Nothing that can fail between the lock and the call
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_BothPayeesAreRead_BeforeTheLock_AndTheIdempotencyRecordAfterTheCall()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _userRepo.GetUserByIdAsync(FarmerId);
            _userRepo.GetUserByIdAsync(TruckerId);
            _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed");
            _gateway.ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{EscrowId}"));
            _idempotencyRepo.StoreAsync(ReleaseRequest().IdempotencyKey, EscrowId, "processing", Arg.Any<string?>());
        });
    }

    [Fact]
    public async Task Refund_PhoneReadFails_BeforeAnyLock()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _userRepo.GetUserByIdAsync(BuyerId).ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var act = () => _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        await _escrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
    }

    [Fact]
    public async Task Release_IdempotencyWritesFail_TheSettlementStillCompletes()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _idempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("down"));
        _idempotencyRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new AmazonDynamoDBException("down"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("completed");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // The settled status write
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReleaseWrite_LostCommitAlreadyCompletedByTheCrashRecovery_CountsAsWritten()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(status: "completed"));
        var attempts = 0;
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .Returns(_ => ++attempts == 1
                ? throw new AmazonDynamoDBException("timeout")
                : throw new ConditionalCheckFailedException("status moved"));

        var result = await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task StatusWrite_ConditionFailsAfterAnError_AndTheReReadFails_Surfaces()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).ThrowsAsync(new AmazonDynamoDBException("down"));
        var attempts = 0;
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .Returns(_ => ++attempts == 1
                ? throw new AmazonDynamoDBException("timeout")
                : throw new ConditionalCheckFailedException("status moved"));

        var act = () => _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<ConditionalCheckFailedException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeUnknown);
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Refund_StatusWrite_TransientFailures_AreRetried()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        var attempts = 0;
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .Returns(_ => ++attempts < 3 ? throw new ProvisionedThroughputExceededException("throttled") : Task.CompletedTask);

        var result = await _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        attempts.Should().Be(3);
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Theory]
    [InlineData("refunded")]
    [InlineData("released")]
    public async Task StatusWrite_ConditionFailsAfterALostCommit_CountsAsWritten(string status)
    {
        // Attempt 1 committed but the client saw an error; attempt 2 then fails its condition.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        var settled = Escrow(status: status);
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(settled);
        var attempts = 0;
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, status, "escrowed", Arg.Any<Dictionary<string, object>?>())
            .Returns(_ => ++attempts == 1
                ? throw new AmazonDynamoDBException("timeout")
                : throw new ConditionalCheckFailedException("status moved"));

        var success = status == "refunded"
            ? (await _service.RefundAsync(RefundRequest(), EscrowService.SystemActor)).IsSuccess
            : (await _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor)).IsSuccess;

        success.Should().BeTrue();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Refund_StatusWrite_ConditionFailsOnTheFirstAttempt_OnItsOwnLostCommit_CountsAsWritten()
    {
        // The SDK retried the write internally after a lost answer, so the condition fails on
        // our own commit even on the service's first attempt (review finding).
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow(status: "refunded"));
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("already refunded"));

        var result = await _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("refunded");
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>());
        AuditEntries().Should().Contain(e => (string)e["action"] == "refund");
    }

    [Fact]
    public async Task Refund_StatusWrite_ConditionFailsOnTheFirstAttempt_RowNotRefunded_SurfacesWithTheLockKept()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Escrow());
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new ConditionalCheckFailedException("anomaly"));

        var act = () => _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<ConditionalCheckFailedException>();
        await _escrowRepo.Received(1).GetByIdAsync(EscrowId, true);
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeUnknown);
    }

    // ──────────────────────────────────────────────
    // After the settled status write: bookkeeping failures are plain failures
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Release_CompletedWriteFailsAfterTheReleasedWrite_IsAPlainFailure()
    {
        // The payout is on record (released); the crash-recovery sweep completes it. Admins must
        // not wait for a queue row that never comes, so this is not an unknown outcome.
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, CreatedAt, "released", "escrowed", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Refund_AuditFailsAfterTheRefundedWrite_IsAPlainFailure()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _auditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "refund"))
            .ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
        await _escrowRepo.Received(1).UpdateStatusAsync(
            EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task Release_TruckerAuditFailsAfterTheReleasedWrite_IsAPlainFailure()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow(withTrucker: true));
        _auditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "trucker_release"))
            .ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
    }

    [Fact]
    public async Task Refund_StatusWriteNeverLands_TheLockStays_AndTheOutcomeIsUnknown()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(Escrow());
        _escrowRepo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.RefundAsync(RefundRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        await _escrowRepo.Received(4).UpdateStatusAsync(
            EscrowId, CreatedAt, "refunded", "escrowed", Arg.Any<Dictionary<string, object>?>());
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeUnknown);
    }

    [Fact]
    public async Task ExceptionBeforeTheLock_IsAPlainFailure()
    {
        _escrowRepo.GetByIdAsync(EscrowId).ThrowsAsync(new AmazonDynamoDBException("down"));

        var act = () => _service.ReleaseAsync(ReleaseRequest(), EscrowService.SystemActor);

        var thrown = await act.Should().ThrowAsync<AmazonDynamoDBException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeFailed);
    }

    // ──────────────────────────────────────────────
    // Outcome classification (ESC-13 alarm, ESC-05 behaviour)
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("TELEBIRR_TIMEOUT", true)]
    [InlineData("TELEBIRR_NETWORK", true)]
    [InlineData("TELEBIRR_PREORDER", true)]
    [InlineData("GATEWAY_EXCEPTION:TaskCanceledException", true)]
    [InlineData("MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING", true)]
    [InlineData("MANUAL_QUEUE_FAILED:GATEWAY_OUTCOME_UNKNOWN", true)]
    [InlineData("MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED", false)]
    [InlineData("TELEBIRR_RELEASE_FAILED", false)]
    [InlineData("TELEBIRR_60200099", false)]
    [InlineData(null, false)]
    public void OutcomeClassification(string? code, bool unknown)
        => EscrowService.IsOutcomeUnknownCode(code).Should().Be(unknown);

    // ──────────────────────────────────────────────
    // Recovery step: stale locks are parked
    // ──────────────────────────────────────────────

    private string? _staleQueuedAt;

    private void StaleQueueWrites(bool result)
        => _escrowRepo.TryQueueStaleSettlementAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Do<string>(q => _staleQueuedAt = q), Arg.Any<string?>(),
                Arg.Any<string>())
            .Returns(result);

    [Fact]
    public async Task Park_ReleaseLockOlderThan15Minutes_QueuesFarmerAndTrucker_AlarmsUnknown_NoGatewayCall()
    {
        StaleQueueWrites(true);

        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", 16, withTrucker: true), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        await _escrowRepo.Received(1).TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "release", "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(l =>
                l.Count == 2 && l[0].Leg == "farmer" && l[1].Leg == "trucker" && l.All(x => x.Status == "pending")),
            Arg.Any<string>(), "lock-id-5", Arg.Any<string>());
        await _gateway.DidNotReceiveWithAnyArgs().ReleaseFundsAsync(default!);
        await _escrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=stale_lock_parked outcome=unknown code=release");
    }

    [Fact]
    public async Task Park_RefundLock_QueuesTheBuyerLeg()
    {
        StaleQueueWrites(true);

        var outcome = await _service.ParkStaleSettlementAsync(Locked("refund", 60, status: "dispute_resolved"), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        await _escrowRepo.Received(1).TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "refund", "dispute_resolved",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(l => l.Count == 1 && l[0].Leg == "buyer"),
            Arg.Any<string>(), "lock-id-5", Arg.Any<string>());
    }

    [Fact]
    public async Task Park_AuditRows_NeverShareATimestamp()
    {
        // O6: the audit table's sort key is the timestamp, so the park row sits one tick before
        // the queued row instead of overwriting it.
        StaleQueueWrites(true);

        await _service.ParkStaleSettlementAsync(Locked("release", 20), Now);

        var rows = AuditEntries();
        var parked = rows.Single(r => (string)r["action"] == "stale_lock_parked");
        var queued = rows.Single(r => (string)r["action"] == "manual_payout_queued");
        queued["timestamp"].Should().Be(_staleQueuedAt);
        DateTime.Parse((string)parked["timestamp"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .Should().Be(DateTime.Parse(_staleQueuedAt!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(-1));
        ((string)queued["metadata"]).Should().Contain("reason=GATEWAY_OUTCOME_UNKNOWN");
        ((string)parked["metadata"]).Should().Contain("op=release").And.Contain("legacyLock=false");
    }

    [Theory]
    [InlineData(14.9)]
    [InlineData(1)]
    public async Task Park_LockYoungerThan15Minutes_IsLeftAlone(double minutesAgo)
    {
        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", minutesAgo), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Skipped);
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryQueueStaleSettlementAsync(
            default!, default!, default!, default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task Park_QueuedRow_IsLeftAlone()
    {
        var row = Locked("release", 120);
        row[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;

        (await _service.ParkStaleSettlementAsync(row, Now)).Should().Be(EscrowService.StaleSettlementOutcome.Skipped);
        _alarmLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("refunded")]
    public async Task Park_SettledRowKeepsItsOldLock_AndIsLeftAlone(string status)
    {
        (await _service.ParkStaleSettlementAsync(Locked("release", 120, status: status), Now))
            .Should().Be(EscrowService.StaleSettlementOutcome.Skipped);
    }

    [Fact]
    public async Task Park_LegacyLock_IsAgedByUpdatedAt_AndFencedOnNoLockId()
    {
        StaleQueueWrites(true);

        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", 90, status: "disputed", legacy: true), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        await _escrowRepo.Received(1).TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "release", "disputed",
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Is<string?>(id => id == null), Arg.Any<string>());
        ((string)AuditEntries().Single(r => (string)r["action"] == "stale_lock_parked")["metadata"])
            .Should().Contain("legacyLock=true");
    }

    [Fact]
    public async Task Park_LostToAnotherRecovery_IsSilent()
    {
        // The other run parked it first (its own queuedAt): no alarm, no log, no audit row here.
        StaleQueueWrites(false);
        var parkedByOther = Locked("release", 30);
        parkedByOther[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        parkedByOther[ManualPayoutFields.Op] = "release";
        parkedByOther[ManualPayoutFields.QueuedAt] = "2026-10-09T11:59:59.0000000Z";
        parkedByOther[ManualPayoutFields.ParkId] = "the-other-run";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(parkedByOther);

        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", 30), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Lost);
        _alarmLines.Should().BeEmpty();
        AuditEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Park_WriteAnswerLost_ButOurWriteLanded_IsClaimed()
    {
        string? parkId = null;
        _escrowRepo.TryQueueStaleSettlementAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Do<string>(q => _staleQueuedAt = q), Arg.Any<string?>(),
                Arg.Do<string>(id => parkId = id))
            .ThrowsAsync(new AmazonDynamoDBException("timeout"));
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(_ =>
        {
            var row = Locked("release", 30);
            row[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
            row[ManualPayoutFields.Op] = "release";
            row[ManualPayoutFields.QueuedAt] = _staleQueuedAt!;
            row[ManualPayoutFields.ParkId] = parkId!;
            return row;
        });

        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", 30), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Parked);
        _alarmLines.Should().ContainSingle();
    }

    [Fact]
    public async Task Park_AnotherLockTakenSinceTheRead_IsSilent()
    {
        StaleQueueWrites(false);
        var relocked = Locked("refund", 1);
        relocked[SettlementLockFields.LockId] = "a-newer-lock";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(relocked);

        (await _service.ParkStaleSettlementAsync(Locked("release", 30), Now))
            .Should().Be(EscrowService.StaleSettlementOutcome.Lost);
        _alarmLines.Should().BeEmpty();
    }
    [Fact]
    public async Task Park_WriteThrows_RowStillHoldsTheSameLock_AlarmsTheRecoveryFailure()
    {
        // The park did not land and the stale lock is still unparked: never silent.
        _escrowRepo.TryQueueStaleSettlementAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(Locked("release", 30));

        var outcome = await _service.ParkStaleSettlementAsync(Locked("release", 30), Now);

        outcome.Should().Be(EscrowService.StaleSettlementOutcome.Failed);
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-5 action=stale_lock_recovery_failed outcome=unknown code=AmazonDynamoDBException");
        AuditEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Park_WriteThrows_ButAnotherLockIsThereNow_IsSilent()
    {
        _escrowRepo.TryQueueStaleSettlementAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        // Same op, newer lock: only the lock id tells it from the stale one the run read.
        var relocked = Locked("release", 1);
        relocked[SettlementLockFields.LockId] = "a-newer-lock";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(relocked);

        (await _service.ParkStaleSettlementAsync(Locked("release", 30), Now))
            .Should().Be(EscrowService.StaleSettlementOutcome.Lost);
        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Park_ALegacyLockAgainstTheVerdict_RecordsTheVerdict()
    {
        // A release locked before ESC-03, then a buyer_wins verdict: parked, but the row says so.
        StaleQueueWrites(true);
        var row = Locked("release", 600, status: "dispute_resolved", legacy: true);
        row["disputeResolution"] = "buyer_wins";

        (await _service.ParkStaleSettlementAsync(row, Now)).Should().Be(EscrowService.StaleSettlementOutcome.Parked);

        ((string)AuditEntries().Single(r => (string)r["action"] == "stale_lock_parked")["metadata"])
            .Should().Contain("op=release").And.Contain("disputeResolution=buyer_wins");
    }

    // ──────────────────────────────────────────────
    // GET /admin/payouts/manual: the ESC-05 fields
    // ──────────────────────────────────────────────

    private static Dictionary<string, object> QueuedRow(string op, bool withTrucker = false, bool legacyLock = false)
    {
        var row = Escrow(withTrucker, status: op == "refund" ? "dispute_resolved" : "escrowed");
        row[SettlementLockFields.Lock] = op;
        if (!legacyLock)
        {
            row[SettlementLockFields.LockAt] = "2026-10-09T10:00:00.0000000Z";
            row[SettlementLockFields.LockId] = "lock-id-5";
        }

        row[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        row[ManualPayoutFields.QueuedAt] = "2026-10-09T10:20:00.0000000Z";
        row[ManualPayoutFields.Op] = op;
        row[ManualPayoutFields.Reason] = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
        row[ManualPayoutFields.FromStatus] = row["status"];
        if (op == "release")
        {
            row[ManualPayoutFields.StatusAttr("farmer")] = "pending";
            if (withTrucker)
            {
                row[ManualPayoutFields.StatusAttr("trucker")] = "pending";
            }
        }
        else
        {
            row[ManualPayoutFields.StatusAttr("buyer")] = "pending";
            row["disputeResolution"] = "farmer_wins";
        }

        return row;
    }

    [Fact]
    public async Task List_EveryLegCarriesTheGatewayReferenceItWasSentUnder_AndTheLockTime()
    {
        _escrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { QueuedRow("release", withTrucker: true), QueuedRow("refund", legacyLock: true) },
                (Dictionary<string, AttributeValue>?)null));

        var result = await _service.ListManualPayoutsAsync(20, null);

        result.IsSuccess.Should().BeTrue();
        var release = result.Value!.Payouts[0];
        release.Legs.Select(l => (l.Ref, l.GatewayRef)).Should().Equal(
            ("farmer", $"release:{EscrowId}"), ("trucker", $"release:{EscrowId}:trucker"));
        release.SettlementLockedAt.Should().Be("2026-10-09T10:00:00.0000000Z");
        release.DisputeResolution.Should().BeNull();
        release.Reason.Should().Be("GATEWAY_OUTCOME_UNKNOWN");

        var refund = result.Value.Payouts[1];
        refund.Legs.Should().ContainSingle().Which.GatewayRef.Should().Be($"refund:{EscrowId}");
        refund.SettlementLockedAt.Should().BeNull("a lock from before ESC-05 has no lock time");
        refund.DisputeResolution.Should().Be("farmer_wins");
    }
}

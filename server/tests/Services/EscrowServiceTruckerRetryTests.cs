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
/// ESC-04: a released escrow whose trucker leg is owed (truckerPayoutPending) is retried by the
/// hourly run with the stable reference once the trucker passes the payout gate, under its own
/// lock, and only then completed. Answers that may have paid are queued with the lock kept, a
/// definite refusal releases the lock for the next run, a stale lock is parked, the block reason
/// is audited once, and a legacy completed row is parked for an admin, never retried.
/// </summary>
public class EscrowServiceTruckerRetryTests
{
    private const string EscrowId = "escrow-order-ord-4";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string ReleasedAt = "2026-10-03T10:00:00.0000000Z";
    private const string FarmerId = "farmer-4";
    private const string TruckerId = "trucker-4";
    private const string TruckerPhone = "+251911000042";
    private const string LockId = "lock-4";
    private const string TruckerRef = "release:escrow-order-ord-4:trucker";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveryRepo = Substitute.For<IDeliveryRepository>();
    private readonly List<string> _alarmLines = new();
    private readonly EscrowService _service;

    public EscrowServiceTruckerRetryTests()
    {
        _escrowRepo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt).Returns(LockId);
        _escrowRepo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, LockId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrowRepo.TryQueueTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        _escrowRepo.TrySetTruckerBlockReasonAsync(EscrowId, CreatedAt, Arg.Any<string>()).Returns(true);
        _escrowRepo.TryBackfillTruckerIdAsync(EscrowId, CreatedAt, Arg.Any<string>()).Returns(true);
        _escrowRepo.ReleaseTruckerPayoutLockAsync(EscrowId, CreatedAt, LockId).Returns(true);
        _escrowRepo.TryReopenLegacyTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-TRUCKER-1" });
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId));
        _service = Service(_userRepo);
    }

    private EscrowService Service(IUserRepository? userRepo) => new(
        _escrowRepo, _auditRepo, _idempotencyRepo, _gateway, userRepo, _orderRepo, _deliveryRepo)
    {
        AlarmLog = _alarmLines.Add
    };

    private static UserDto User(string id, bool phoneVerified = true, bool fayda = true) => new()
    {
        UserId = id,
        PhoneNumber = TruckerPhone,
        DisplayName = id,
        Role = "trucker",
        Woreda = "test-woreda",
        IsPhoneVerified = phoneVerified,
        FaydaVerified = fayda,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Version = 1
    };

    /// <summary>A released escrow with an owed trucker leg of 3,000 ETB, as the retry query returns it.</summary>
    private static Dictionary<string, object> Pending(
        string status = "released", bool withTruckerId = true, string? lockAgeMinutes = null, bool queued = false)
    {
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = EscrowId,
            ["orderId"] = "ord-4",
            ["buyerId"] = "buyer-4",
            ["farmerId"] = FarmerId,
            ["amount"] = 20_000m,
            ["commission"] = 500m,
            ["farmerPayout"] = 16_500m,
            ["truckerPayout"] = 3_000m,
            ["currency"] = "ETB",
            ["status"] = status,
            ["telebirrHoldRef"] = "HOLD-4",
            ["telebirrReleaseRef"] = "GW-FARMER-4",
            ["releasedAt"] = ReleasedAt,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = ReleasedAt,
            ["expiresAt"] = "2026-10-08T10:00:00.0000000Z",
            [TruckerPayoutFields.Pending] = true,
            [TruckerPayoutFields.PendingSince] = ReleasedAt,
            ["version"] = 4
        };
        if (withTruckerId)
        {
            item["truckerId"] = TruckerId;
        }

        if (lockAgeMinutes != null)
        {
            item[TruckerPayoutFields.LockId] = LockId;
            item[TruckerPayoutFields.LockAt] = DateTime.UtcNow.AddMinutes(-double.Parse(lockAgeMinutes)).ToString("o");
        }

        if (queued)
        {
            item[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        }

        return item;
    }

    private void BaseRowIs(Dictionary<string, object> row) => _escrowRepo.GetByIdAsync(EscrowId, true).Returns(row);

    private Task<EscrowService.TruckerRetryResult> RetryAsync(Dictionary<string, object> row, bool mayCallGateway = true)
        => _service.RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway);

    private List<Dictionary<string, object>> AuditEntries()
        => _auditRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is nameof(IEscrowAuditRepository.AppendAsync)
                or nameof(IEscrowAuditRepository.AppendIfAbsentAsync))
            .Select(c => c.GetArguments()[0])
            .OfType<Dictionary<string, object>>()
            .ToList();

    private List<string> AuditActions() => AuditEntries().Select(e => (string)e["action"]).ToList();

    private Task NoQueueWrite() => _escrowRepo.DidNotReceive().TryQueueTruckerLegAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

    // ──────────────────────────────────────────────
    // Paid
    // ──────────────────────────────────────────────

    [Fact]
    public async Task VerifiedTrucker_IsPaidWithTheStableReference_AndTheEscrowCompletes()
    {
        var row = Pending();
        BaseRowIs(row);

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r =>
            r.TransactionRef == TruckerRef && r.HoldRef == "HOLD-4" && r.FarmerPhone == TruckerPhone
            && r.Amount == 3_000m && r.PlatformCommission == 0m));
        await _escrowRepo.Received(1).TryRecordTruckerPayoutAsync(
            EscrowId, CreatedAt, LockId, "GW-TRUCKER-1", Arg.Any<string>(), Arg.Any<string>());
        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Paid_WritesTruckerReleaseAndCompleteAtDeterministicAdjacentKeys()
    {
        var row = Pending();
        BaseRowIs(row);
        string? paidAt = null;
        string? completedAt = null;
        _escrowRepo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, LockId, "GW-TRUCKER-1",
                Arg.Do<string>(p => paidAt = p), Arg.Do<string>(c => completedAt = c))
            .Returns(true);

        await RetryAsync(row);

        // O1: both rows through AppendIfAbsentAsync, keyed on the times the record write stored.
        var ensured = _auditRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IEscrowAuditRepository.AppendIfAbsentAsync))
            .Select(c => (Dictionary<string, object>)c.GetArguments()[0]!)
            .ToList();
        ensured.Should().HaveCount(2);
        ensured[0]["action"].Should().Be("trucker_release");
        ensured[0]["timestamp"].Should().Be(paidAt);
        ensured[0]["telebirrRef"].Should().Be("GW-TRUCKER-1");
        ensured[1]["action"].Should().Be("complete");
        ensured[1]["timestamp"].Should().Be(completedAt);
        ensured[1]["toState"].Should().Be("completed");
        DateTime.Parse(completedAt!).ToUniversalTime().Should().Be(DateTime.Parse(paidAt!).ToUniversalTime().AddTicks(1));
    }

    [Fact]
    public async Task RecordWriteFailsItsCondition_ButOurWriteLanded_CountsAsPaid()
    {
        var row = Pending();
        var landed = Pending(status: "completed");
        landed[TruckerPayoutFields.Ref] = "GW-TRUCKER-1";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(row, landed);
        _escrowRepo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, LockId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task RecordWriteNeverLands_TheExceptionEscapesAsAnUnknownOutcome_AndTheLockStays()
    {
        var row = Pending();
        BaseRowIs(row);
        _escrowRepo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, LockId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new TransientWriteException());

        var act = () => RetryAsync(row);

        var thrown = await act.Should().ThrowAsync<TransientWriteException>();
        MoneyAlarmLog.ExceptionOutcome(thrown.Which).Should().Be(MoneyAlarmLog.OutcomeUnknown);
        await _escrowRepo.Received(4).TryRecordTruckerPayoutAsync(
            EscrowId, CreatedAt, LockId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _escrowRepo.DidNotReceive().ReleaseTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    private sealed class TransientWriteException : Exception;

    // ──────────────────────────────────────────────
    // Answers that send the leg to the manual payout queue
    // ──────────────────────────────────────────────

    [Fact]
    public async Task PayoutNotSupported_QueuesTheTruckerLeg_WithTheFarmerLegPaid_UnderTheTruckerLock()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported
        });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(
            EscrowService.TruckerRetryOutcome.Queued, ManualPayoutFields.ReasonPayoutNotSupported));
        await _escrowRepo.Received(1).TryQueueTruckerLegAsync(EscrowId, CreatedAt, ManualPayoutFields.ReasonPayoutNotSupported,
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs =>
                legs.Count == 2
                && legs[0] == new ManualPayoutLegSeed("farmer", "paid", "GW-FARMER-4", "gateway", ReleasedAt)
                && legs[1] == new ManualPayoutLegSeed("trucker", "pending", null, null, null)),
            Arg.Any<string>(), Arg.Is(LockId), Arg.Is<string?>(p => p == null));
        AuditActions().Should().Contain("manual_payout_queued");
        await _escrowRepo.DidNotReceive().ReleaseTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _escrowRepo.DidNotReceive().TryRecordTruckerPayoutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SuccessWithoutAReference_IsQueuedAsRefMissing_NeverRecorded()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "" });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(
            EscrowService.TruckerRetryOutcome.Queued, ManualPayoutFields.ReasonGatewayRefMissing));
    }

    [Theory]
    [InlineData(TelebirrGatewayErrors.Timeout)]
    [InlineData(TelebirrGatewayErrors.Network)]
    [InlineData("TELEBIRR_PREORDER")]
    public async Task UncertainAnswer_IsAuditedAsUnknown_AndQueuedAsOutcomeUnknown(string code)
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse { Success = false, ErrorCode = code });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(
            EscrowService.TruckerRetryOutcome.Queued, ManualPayoutFields.ReasonGatewayOutcomeUnknown));
        _alarmLines.Should().Equal(
            $"ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=trucker_release_failed outcome=unknown code={code}");
        await _escrowRepo.DidNotReceive().ReleaseTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task GatewayCallThrows_IsQueuedAsOutcomeUnknown()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).ThrowsAsync(new HttpRequestException("reset"));

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Queued);
        _alarmLines.Should().ContainSingle().Which.Should().Contain("code=GATEWAY_EXCEPTION:HttpRequestException");
    }

    [Fact]
    public async Task NotSupportedQueueWriteNeverLands_ReleasesTheLock_ForTheNextRun()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported
        });
        _escrowRepo.TryQueueTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Failed, "queue_failed"));
        await _escrowRepo.Received(4).TryQueueTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Is(LockId), Arg.Is<string?>(p => p == null));
        await _escrowRepo.Received(1).ReleaseTruckerPayoutLockAsync(EscrowId, CreatedAt, LockId);
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=trucker_release_failed outcome=failed code=MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED");
    }

    [Fact]
    public async Task UnknownQueueWriteNeverLands_KeepsTheLock_ForTheRecoveryToPark()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Timeout });
        _escrowRepo.TryQueueTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Failed, "queue_failed"));
        await _escrowRepo.DidNotReceive().ReleaseTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        _alarmLines.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=trucker_release_failed outcome=unknown code=MANUAL_QUEUE_FAILED:GATEWAY_OUTCOME_UNKNOWN");
    }

    // ──────────────────────────────────────────────
    // Definite refusal
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DefiniteRefusal_ReleasesTheTruckerLockFirst_ThenAuditsTheCode()
    {
        var row = Pending();
        BaseRowIs(row);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Failed, "gateway_refused"));
        Received.InOrder(() =>
        {
            _escrowRepo.ReleaseTruckerPayoutLockAsync(EscrowId, CreatedAt, LockId);
            _auditRepo.AppendAsync(Arg.Is<Dictionary<string, object>>(e => (string)e["action"] == "trucker_release_failed"));
        });
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=trucker_release_failed outcome=failed code=TELEBIRR_RELEASE_FAILED");
        await NoQueueWrite();
    }

    // ──────────────────────────────────────────────
    // Gate, backfill, fences
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, "phone_unverified")]
    [InlineData(true, false, "fayda_required")]
    public async Task BlockedTrucker_IsNotCalled_AndItsReasonIsAuditedWhenItChanges(bool phone, bool fayda, string reason)
    {
        var row = Pending();
        row["truckerPayout"] = 12_000m;
        BaseRowIs(row);
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, phone, fayda));

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, reason));
        await _escrowRepo.Received(1).TrySetTruckerBlockReasonAsync(EscrowId, CreatedAt, reason);
        AuditEntries().Should().ContainSingle(e => (string)e["action"] == "trucker_release_blocked_kyc"
                                                   && (string)e["metadata"] == $"code=ESCROW_029;reason={reason}");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        _alarmLines.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockedTrucker_WithTheSameReasonStored_WritesNoSecondAuditRow()
    {
        var row = Pending();
        BaseRowIs(row);
        _userRepo.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, phoneVerified: false));
        _escrowRepo.TrySetTruckerBlockReasonAsync(EscrowId, CreatedAt, Arg.Any<string>()).Returns(false);

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Blocked);
        AuditEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAUserRepository_FailsClosed_NoGatewayCall()
    {
        var row = Pending();
        BaseRowIs(row);

        var result = await Service(userRepo: null).RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "gate_unavailable"));
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task MissingTruckerId_IsBackfilledFromTheOrder_WhenTheDeliveryNamesTheSameTrucker()
    {
        var row = Pending(withTruckerId: false);
        row["deliveryId"] = "del-4";
        BaseRowIs(row);
        _orderRepo.GetByIdAsync("ord-4").Returns(new Dictionary<string, object> { ["orderId"] = "ord-4", ["truckerId"] = TruckerId });
        _deliveryRepo.GetByIdAsync("del-4").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-4", ["truckerId"] = TruckerId });

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        await _escrowRepo.Received(1).TryBackfillTruckerIdAsync(EscrowId, CreatedAt, TruckerId);
        AuditEntries().Should().Contain(e => (string)e["action"] == "trucker_backfilled"
                                             && (string)e["metadata"] == $"source=order;truckerId={TruckerId}");
    }

    [Fact]
    public async Task MissingTruckerId_WithADeliveryForAnotherTrucker_IsBlockedAsMismatch_NeverBackfilled()
    {
        var row = Pending(withTruckerId: false);
        row["deliveryId"] = "del-4";
        BaseRowIs(row);
        _orderRepo.GetByIdAsync("ord-4").Returns(new Dictionary<string, object> { ["orderId"] = "ord-4", ["truckerId"] = TruckerId });
        _deliveryRepo.GetByIdAsync("del-4").Returns(new Dictionary<string, object> { ["deliveryId"] = "del-4", ["truckerId"] = "trucker-other" });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "trucker_mismatch"));
        await _escrowRepo.DidNotReceive().TryBackfillTruckerIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task MissingTruckerId_AndAnOrderWithoutOne_IsBlockedAsNoTrucker()
    {
        var row = Pending(withTruckerId: false);
        BaseRowIs(row);
        _orderRepo.GetByIdAsync("ord-4").Returns(new Dictionary<string, object> { ["orderId"] = "ord-4" });

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Blocked, "no_trucker"));
    }

    [Fact]
    public async Task OutOfBudget_TakesNoLock()
    {
        var row = Pending();
        BaseRowIs(row);

        var result = await RetryAsync(row, mayCallGateway: false);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Deferred);
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task LockLost_MakesNoGatewayCall()
    {
        var row = Pending();
        BaseRowIs(row);
        _escrowRepo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt).Returns((string?)null);

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.InFlight);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("escrowed", false)]
    [InlineData("released", true)]
    public async Task NotARetryableRow_IsSkipped_FromTheBaseRow(string status, bool queued)
    {
        var indexCopy = Pending();
        BaseRowIs(Pending(status: status, queued: queued));

        var result = await RetryAsync(indexCopy);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Skipped);
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task AFreshTruckerLock_IsLeftToItsOwner()
    {
        var row = Pending(lockAgeMinutes: "2");
        BaseRowIs(row);

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.InFlight, "in_flight"));
        await NoQueueWrite();
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task AStaleTruckerLock_IsParkedAsOutcomeUnknown_FencedOnTheSameLock_NeverRetried()
    {
        var row = Pending(lockAgeMinutes: "20");
        BaseRowIs(row);

        var result = await RetryAsync(row);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Queued, "stale_lock"));
        await _escrowRepo.Received(1).TryQueueTruckerLegAsync(EscrowId, CreatedAt, ManualPayoutFields.ReasonGatewayOutcomeUnknown,
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Is(LockId), Arg.Is<string?>(p => p != null));
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=stale_lock_parked outcome=unknown code=release:trucker");
        AuditActions().Should().Contain(new[] { "stale_lock_parked", "manual_payout_queued" });
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().ReleaseTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AStaleTruckerLock_LostToAnotherRecovery_IsLeftAloneSilently()
    {
        var row = Pending(lockAgeMinutes: "20");
        var parkedByOther = Pending(lockAgeMinutes: "20", queued: true);
        parkedByOther[ManualPayoutFields.ParkId] = "someone-else";
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(row, parkedByOther);
        _escrowRepo.TryQueueTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var result = await RetryAsync(row);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Skipped);
        _alarmLines.Should().BeEmpty();
        AuditEntries().Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // Legacy completed rows
    // ──────────────────────────────────────────────

    [Fact]
    public async Task LegacyCompletedRow_IsReopenedAndParked_NeverCalled()
    {
        var row = Pending(status: "completed");
        row["completedAt"] = "2026-10-03T10:00:01.0000000Z";
        row.Remove(TruckerPayoutFields.PendingSince);
        BaseRowIs(row);

        var parked = await _service.ParkLegacyTruckerLegAsync(row);

        parked.Should().BeTrue();
        await _escrowRepo.Received(1).TryReopenLegacyTruckerLegAsync(EscrowId, CreatedAt,
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(legs => legs.Count == 2 && legs[1].Status == "pending"),
            Arg.Any<string>(), "2026-10-03T10:00:01.0000000Z");
        _alarmLines.Should().Equal(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-4 action=legacy_trucker_parked outcome=unknown code=release:trucker");
        AuditEntries().Should().Contain(e => (string)e["action"] == "trucker_leg_reopened"
                                             && (string)e["fromState"] == "completed" && (string)e["toState"] == "released");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task LegacyRow_NoLongerCompleted_IsLeftAlone()
    {
        var row = Pending(status: "completed");
        BaseRowIs(Pending(status: "released", queued: true));

        (await _service.ParkLegacyTruckerLegAsync(row)).Should().BeFalse();
        await _escrowRepo.DidNotReceive().TryReopenLegacyTruckerLegAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task LegacyReopen_WhoseAnswerWasLost_CountsAsParked()
    {
        var row = Pending(status: "completed");
        string? queuedAt = null;
        _escrowRepo.TryReopenLegacyTruckerLegAsync(EscrowId, CreatedAt, Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Do<string>(q => queuedAt = q), Arg.Any<string>())
            .ThrowsAsync(new HttpRequestException("lost"));
        _escrowRepo.GetByIdAsync(EscrowId, true).Returns(_ => queuedAt == null
            ? row
            : new Dictionary<string, object>(Pending(status: "released", queued: true))
            {
                [ManualPayoutFields.QueuedAt] = queuedAt
            });

        (await _service.ParkLegacyTruckerLegAsync(row)).Should().BeTrue();
    }
}

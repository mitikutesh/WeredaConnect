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

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-04 in escrow-cron: the trucker retry runs last (after the crash recovery), takes a lock only
/// with time and calls left in the run, logs TRUCKER_PAYOUT_OVERDUE for every leg owed more than 24
/// hours, and reports a failed or truncated query as an alarm line. The one-off legacy action is
/// reached with the same envelope EventBridge and the deploy step use.
/// </summary>
public class EscrowCronTruckerRetryTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string TruckerId = "trucker-7";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly List<string> _alarmLines = new();
    private readonly EscrowCronFunction _function;
    private readonly TestLambdaContext _context = new() { RemainingTime = TimeSpan.FromMinutes(4) };
    private readonly List<Dictionary<string, object>> _pending = new();

    public EscrowCronTruckerRetryTests()
    {
        _escrowRepo.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _escrowRepo.QueryStaleSettlementLocksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrowRepo.QueryPendingTruckerLegsAsync(Arg.Any<int>()).Returns(_ => _pending);
        _escrowRepo.QueryLegacyTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _escrowRepo.TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => $"lock-{call.ArgAt<string>(0)}");
        _escrowRepo.TryRecordTruckerPayoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrowRepo.TrySetTruckerBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-TRUCKER" });
        _userRepo.GetUserByIdAsync(TruckerId).Returns(new UserDto
        {
            UserId = TruckerId,
            PhoneNumber = "+251911000077",
            DisplayName = "trucker",
            Role = "trucker",
            Woreda = "test-woreda",
            IsPhoneVerified = true,
            FaydaVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });

        var service = new EscrowService(_escrowRepo, _auditRepo, Substitute.For<IEscrowIdempotencyRepository>(), _gateway, _userRepo)
        {
            AlarmLog = _alarmLines.Add
        };
        _function = new EscrowCronFunction(service, _escrowRepo, _auditRepo, _gateway, idempotencyRepo: _idempotencyRepo);
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    /// <summary>A pending trucker leg that went pending <paramref name="hoursAgo"/> ago; the base row reads the same.</summary>
    private Dictionary<string, object> AddPending(string escrowId, double hoursAgo, bool verified = true)
    {
        var row = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["status"] = "released",
            ["farmerId"] = "farmer-7",
            ["truckerId"] = verified ? TruckerId : "trucker-unverified",
            ["truckerPayout"] = 2_000m,
            ["farmerPayout"] = 9_000m,
            ["amount"] = 11_250m,
            ["telebirrHoldRef"] = "HOLD-7",
            ["telebirrReleaseRef"] = "GW-FARMER-7",
            ["releasedAt"] = DateTime.UtcNow.AddHours(-hoursAgo).ToString("o"),
            [TruckerPayoutFields.Pending] = true,
            [TruckerPayoutFields.PendingSince] = DateTime.UtcNow.AddHours(-hoursAgo).ToString("o")
        };
        _pending.Add(row);
        _escrowRepo.GetByIdAsync(escrowId, true).Returns(row);
        return row;
    }

    private static APIGatewayProxyRequest Cron(string action) => new()
    {
        Body = $"{{\"action\":\"{action}\"}}"
    };

    private Task<APIGatewayProxyResponse> RunAsync() => _function.FunctionHandler(Cron("auto_release_and_refund"), _context);

    [Fact]
    public async Task PendingLeg_IsPaid_AndTheRunSummaryCountsIt_AfterTheCrashRecovery()
    {
        AddPending("escrow-t1", hoursAgo: 2);

        var response = await RunAsync();

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"truckerLegsPaid\":\"1\"");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:escrow-t1:trucker"));
        // R2: the trucker step runs last, after the crash recovery's released query.
        var calls = _escrowRepo.ReceivedCalls().Select(c => (c.GetMethodInfo().Name, c.GetArguments())).ToList();
        var crashRecovery = calls.FindLastIndex(c => c.Name == nameof(IEscrowRepository.QueryByStatusForCronAsync)
                                                     && (string)c.Item2[0]! == "released");
        var truckerQuery = calls.FindIndex(c => c.Name == nameof(IEscrowRepository.QueryPendingTruckerLegsAsync));
        crashRecovery.Should().BeGreaterThan(-1);
        truckerQuery.Should().BeGreaterThan(crashRecovery);
        Logs.Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
    }

    [Fact]
    public async Task BlockedLeg_OlderThan24Hours_LogsTheOverdueLine_WithItsReason_EveryRun()
    {
        AddPending("escrow-t2", hoursAgo: 30, verified: false);

        await RunAsync();
        await RunAsync();

        var lines = Logs.Split('\n').Where(l => l.Contains("TRUCKER_PAYOUT_OVERDUE")).ToList();
        lines.Should().HaveCount(2);
        lines.Should().AllSatisfy(l => l.Should().Contain("TRUCKER_PAYOUT_OVERDUE escrowId=escrow-t2 reason=profile_missing ageHours=30"));
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task BlockedLeg_YoungerThan24Hours_IsNotOverdueYet()
    {
        AddPending("escrow-t3", hoursAgo: 5, verified: false);

        var response = await RunAsync();

        response.Body.Should().Contain("\"truckerLegsBlocked\":\"1\"");
        Logs.Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
    }

    [Fact]
    public async Task WithoutTimeForTheGatewayCall_NoLockIsTaken_AndTheLegWaits()
    {
        AddPending("escrow-t4", hoursAgo: 26);
        _context.RemainingTime = EscrowCronFunction.TruckerRetryTimeReserve - TimeSpan.FromSeconds(1);

        var response = await RunAsync();

        response.Body.Should().Contain("\"truckerLegsDeferred\":\"1\"");
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        Logs.Should().Contain("TRUCKER_PAYOUT_OVERDUE escrowId=escrow-t4 reason=deferred ageHours=26");
    }

    [Fact]
    public async Task AtMost25GatewayCallsPerRun()
    {
        for (var i = 0; i < 27; i++)
        {
            AddPending($"escrow-cap-{i:D2}", hoursAgo: 2);
        }

        var response = await RunAsync();

        response.Body.Should().Contain("\"truckerLegsPaid\":\"25\"").And.Contain("\"truckerLegsDeferred\":\"2\"");
        await _gateway.Received(25).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task ALegPendingSinceBeforeEsc04_IsParkedForTheAdmin_NeverPaid()
    {
        var row = AddPending("escrow-legacy-released", hoursAgo: 72);
        row.Remove(TruckerPayoutFields.PendingSince);
        _escrowRepo.TryParkLegacyTruckerLegAsync("escrow-legacy-released", CreatedAt, "released",
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        var response = await RunAsync();

        response.Body.Should().Contain("\"truckerLegsQueued\":\"1\"");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        Logs.Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
    }

    [Fact]
    public async Task ALegThatWentPendingInThisRun_WaitsForTheNextOne()
    {
        var row = AddPending("escrow-t5", hoursAgo: 0);
        row[TruckerPayoutFields.PendingSince] = DateTime.UtcNow.AddMinutes(1).ToString("o");

        await RunAsync();

        await _escrowRepo.DidNotReceive().GetByIdAsync("escrow-t5", true);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task QueryFailure_LogsSweepFailed_AndTheRunStillAnswers200()
    {
        _escrowRepo.QueryPendingTruckerLegsAsync(Arg.Any<int>()).ThrowsAsync(new InvalidOperationException("boom"));

        var response = await RunAsync();

        response.StatusCode.Should().Be(200);
        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=trucker_retry_query:InvalidOperationException");
    }

    [Fact]
    public async Task MoreRowsThanTheReadCap_LogsATruncationAlarmLine_WithoutTheOverdueToken()
    {
        for (var i = 0; i < 501; i++)
        {
            _pending.Add(new Dictionary<string, object>
            {
                ["escrowId"] = $"escrow-many-{i:D3}",
                ["createdAt"] = CreatedAt,
                ["status"] = "released",
                [TruckerPayoutFields.Pending] = true,
                [TruckerPayoutFields.PendingSince] = DateTime.UtcNow.AddHours(-1).ToString("o")
            });
        }

        await RunAsync();

        await _escrowRepo.Received(1).QueryPendingTruckerLegsAsync(501);
        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_truncated outcome=failed code=trucker_retry_query:500");
        Logs.Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
    }

    [Fact]
    public async Task AnExceptionAfterTheTruckerLock_IsLoggedAsAnUnknownOutcome_AndTheBatchGoesOn()
    {
        AddPending("escrow-t6", hoursAgo: 2);
        AddPending("escrow-t7", hoursAgo: 2);
        _escrowRepo.TryRecordTruckerPayoutAsync("escrow-t6", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new TimeoutException("write"));

        var response = await RunAsync();

        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-t6 action=trucker_retry_exception outcome=unknown code=TimeoutException");
        response.Body.Should().Contain("\"truckerLegsPaid\":\"1\"").And.Contain("\"truckerLegsFailed\":\"1\"");
    }

    [Fact]
    public async Task AnExceptionBeforeTheLock_IsLoggedAsAFailure()
    {
        AddPending("escrow-t8", hoursAgo: 2);
        _userRepo.GetUserByIdAsync(TruckerId).ThrowsAsync(new TimeoutException("users"));

        await RunAsync();

        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-t8 action=trucker_retry_exception outcome=failed code=TimeoutException");
    }

    // ──────────────────────────────────────────────
    // Legacy action
    // ──────────────────────────────────────────────

    [Fact]
    public async Task LegacyAction_WithTheDeployStepsEnvelope_ParksEachRow()
    {
        // R1: the deploy step invokes escrow-cron with {"body":"{\"action\":\"park_legacy_trucker_legs\"}"},
        // which the runtime turns into APIGatewayProxyRequest.Body.
        var request = System.Text.Json.JsonSerializer.Deserialize(
            "{\"body\":\"{\\\"action\\\":\\\"park_legacy_trucker_legs\\\"}\"}",
            AppJsonContext.Default.APIGatewayProxyRequest)!;
        var legacy = new Dictionary<string, object>
        {
            ["escrowId"] = "escrow-legacy-1",
            ["createdAt"] = CreatedAt,
            ["status"] = "completed",
            ["truckerId"] = TruckerId,
            ["truckerPayout"] = 2_000m,
            ["completedAt"] = "2026-09-20T10:00:00.0000000Z",
            [TruckerPayoutFields.Pending] = true
        };
        _escrowRepo.QueryLegacyTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { legacy });
        _escrowRepo.GetByIdAsync("escrow-legacy-1", true).Returns(legacy);
        _escrowRepo.TryParkLegacyTruckerLegAsync("escrow-legacy-1", CreatedAt, "completed", Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"found\":\"1\"").And.Contain("\"parked\":\"1\"").And.Contain("\"failed\":\"0\"");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        // A run that found rows never advances the marker.
        await _idempotencyRepo.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _idempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // Item 5: the legacy read stops once two clean runs at least 15 minutes apart found nothing.

    private static Dictionary<string, object> Marker(string status, double minutesAgo) => new()
    {
        ["idempotencyKey"] = EscrowCronFunction.LegacyTruckerMarkerKey,
        ["status"] = status,
        ["createdAt"] = DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("o")
    };

    [Fact]
    public async Task LegacyAction_ADoneMarker_SkipsTheRead()
    {
        _idempotencyRepo.GetAsync(EscrowCronFunction.LegacyTruckerMarkerKey).Returns(Marker("done", 60 * 24));

        var response = await _function.FunctionHandler(Cron("park_legacy_trucker_legs"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"skipped\":\"true\"").And.Contain("\"marker\":\"done\"");
        await _escrowRepo.DidNotReceive().QueryLegacyTruckerLegsAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task LegacyAction_AFirstCleanRun_WritesTheCleanMarker()
    {
        _idempotencyRepo.GetAsync(EscrowCronFunction.LegacyTruckerMarkerKey).Returns((Dictionary<string, object>?)null);

        var response = await _function.FunctionHandler(Cron("park_legacy_trucker_legs"), _context);

        response.Body.Should().Contain("\"found\":\"0\"").And.Contain("\"marker\":\"clean\"");
        await _idempotencyRepo.Received(1).StoreAsync(EscrowCronFunction.LegacyTruckerMarkerKey, "-", "clean", null);
        await _idempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(5, false)]
    public async Task LegacyAction_ASecondCleanRun_IsDoneOnlyAfterTheSettleTime(double minutesAgo, bool done)
    {
        _idempotencyRepo.GetAsync(EscrowCronFunction.LegacyTruckerMarkerKey).Returns(Marker("clean", minutesAgo));

        var response = await _function.FunctionHandler(Cron("park_legacy_trucker_legs"), _context);

        response.StatusCode.Should().Be(200);
        await _escrowRepo.Received(1).QueryLegacyTruckerLegsAsync(Arg.Any<int>());
        if (done)
        {
            await _idempotencyRepo.Received(1).UpdateAsync(EscrowCronFunction.LegacyTruckerMarkerKey, "done", null);
            response.Body.Should().Contain("\"marker\":\"done\"");
        }
        else
        {
            await _idempotencyRepo.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
            response.Body.Should().Contain("\"marker\":\"clean\"");
        }
    }

    [Fact]
    public async Task LegacyAction_AMarkerReadFailure_StillRunsTheRead()
    {
        _idempotencyRepo.GetAsync(EscrowCronFunction.LegacyTruckerMarkerKey).ThrowsAsync(new TimeoutException("read"));

        var response = await _function.FunctionHandler(Cron("park_legacy_trucker_legs"), _context);

        response.StatusCode.Should().Be(200);
        await _escrowRepo.Received(1).QueryLegacyTruckerLegsAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task LegacyAction_ARowThatCannotBeParked_Answers500_SoTheDeployStepFails()
    {
        var legacy = new Dictionary<string, object>
        {
            ["escrowId"] = "escrow-legacy-2",
            ["createdAt"] = CreatedAt,
            ["status"] = "completed",
            ["truckerId"] = TruckerId,
            [TruckerPayoutFields.Pending] = true
        };
        _escrowRepo.QueryLegacyTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>> { legacy });
        _escrowRepo.GetByIdAsync("escrow-legacy-2", true).Returns(legacy);
        _escrowRepo.TryParkLegacyTruckerLegAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new TimeoutException("write"));

        var response = await _function.FunctionHandler(Cron("park_legacy_trucker_legs"), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("\"failed\":\"1\"");
        Logs.Should().Contain(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-legacy-2 action=legacy_trucker_park_failed outcome=failed code=TimeoutException");
    }

    [Fact]
    public async Task UnknownAction_ListsTheLegacyAction()
    {
        var response = await _function.FunctionHandler(Cron("nope"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("park_legacy_trucker_legs");
    }
}

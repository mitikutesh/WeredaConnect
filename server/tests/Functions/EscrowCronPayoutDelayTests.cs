using System.Text.Json;
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

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-13 (fraud-detection-architecture.md 17.5, escrow-protocol.md 1.33.0) in escrow-cron: the auto-release
/// pays nothing before an escrow's <c>releaseNotBefore</c> and pays after it; an escrow stamped before FRD-13
/// settles as before; the dispute sweep is not delayed; the trucker retry waits for the delay and does not count
/// the delay as overdue; the stamp repair writes the date.
/// </summary>
public class EscrowCronPayoutDelayTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new() { RemainingTime = TimeSpan.FromMinutes(4) };
    private readonly List<Dictionary<string, object>> _pending = new();

    public EscrowCronPayoutDelayTests()
    {
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.StubEmptySweeps();
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _escrows.QueryStaleSettlementLocksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.QueryPendingTruckerLegsAsync(Arg.Any<int>()).Returns(_ => _pending);
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _escrows.TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => $"lock-{ci.ArgAt<string>(0)}");
        _escrows.TryRecordTruckerPayoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-REL" });
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);

        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _users.GetUserByIdAsync(id).Returns(new UserDto
            {
                UserId = id,
                PhoneNumber = "+251900000001",
                Role = role,
                Woreda = "w",
                IsPhoneVerified = true,
                FaydaVerified = true,
                CreatedAt = "2025-01-01T00:00:00Z",
                UpdatedAt = "2025-01-01T00:00:00Z",
                Version = 1
            });
        }

        _fraud.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
    }

    private EscrowCronFunction Cron(PayoutDelayPolicy? policy = null)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: _users, deliveryRepo: _deliveries);
        return new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries) { PayoutDelay = policy };
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private async Task<Dictionary<string, string>> RunAsync(EscrowCronFunction cron)
    {
        var response = await cron.FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", "auto_release_and_refund" } })
        }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    /// <summary>A confirmed escrow whose 48 hour window ended an hour ago, no order, the delivery delivered.</summary>
    private Dictionary<string, object> Confirmed(string id, string? releaseNotBefore, string status = "escrowed")
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", id },
            { "createdAt", CreatedAt },
            { "status", status },
            { "deliveryId", $"del-{id}" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "farmerPayout", 7_800m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-1" },
            { "expiresAt", DateTime.UtcNow.AddDays(3).ToString("o") },
            { "deliveryConfirmedAt", DateTime.UtcNow.AddHours(-49).ToString("o") },
            { "disputeWindowEndsAt", DateTime.UtcNow.AddHours(-1).ToString("o") },
            { "version", 2 }
        };
        if (releaseNotBefore != null)
        {
            escrow[PayoutDelayPolicy.ReleaseNotBeforeField] = releaseNotBefore;
        }

        _escrows.GetByIdAsync(id).Returns(escrow);
        _escrows.GetByIdAsync(id, Arg.Any<bool>()).Returns(escrow);
        _deliveries.GetByIdAsync($"del-{id}", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", $"del-{id}" }, { "status", "delivered" }
        });
        return escrow;
    }

    private Task ReleasedAsync(string escrowId) => _gateway.Received(1).ReleaseFundsAsync(
        Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == $"release:{escrowId}"));

    // ── Auto-release ──────────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_BeforeReleaseNotBefore_PaysNothing()
    {
        // Window over, payout delay still running (a new or flagged payee): nothing moves.
        var escrow = Confirmed("esc-wait", DateTime.UtcNow.AddHours(23).ToString("o"));
        var swept = _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        swept.AssertServed();
        summary["released"].Should().Be("0");
        summary["payoutsDelayed"].Should().Be("1");
        await _escrows.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        // Skipped before the order and delivery check and the payout gate: no read at all.
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        _users.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task AutoRelease_AfterReleaseNotBefore_Pays()
    {
        var escrow = Confirmed("esc-due", DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        summary["payoutsDelayed"].Should().Be("0");
        await ReleasedAsync("esc-due");
    }

    [Fact]
    public async Task AutoRelease_EscrowStampedBeforeFrd13_SettlesAsBefore()
    {
        var escrow = Confirmed("esc-old", releaseNotBefore: null);
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        await ReleasedAsync("esc-old");
    }

    [Fact]
    public async Task AutoRelease_UnreadableReleaseNotBefore_WaitsTheCap()
    {
        // Read as deliveryConfirmedAt + 168 hours: later, never forever.
        var escrow = Confirmed("esc-bad", "not-a-date");
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("0");
        summary["payoutsDelayed"].Should().Be("1");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task AutoRelease_AFarFutureReleaseNotBefore_IsCappedAt168HoursAfterConfirm()
    {
        // A bad row can never hold the payout for good: 170 hours after the confirm it is paid.
        var escrow = Confirmed("esc-far", "2099-01-01T00:00:00.0000000Z");
        escrow["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-170).ToString("o");
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        await ReleasedAsync("esc-far");
    }

    [Fact]
    public async Task AutoRelease_DelayedRowDoesNotUseTheRowBudget()
    {
        // ESC-10: only rows that needed a read or a write count; a delayed row needs neither.
        var waiting = Enumerable.Range(1, 3)
            .Select(i => Confirmed($"esc-w{i}", DateTime.UtcNow.AddHours(10).ToString("o")))
            .ToList();
        var due = Confirmed("esc-paid", DateTime.UtcNow.AddHours(-1).ToString("o"));
        _escrows.StubReleaseSweep(waiting.Append(due).ToArray());
        var cron = new EscrowCronFunction(
            new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: _users, deliveryRepo: _deliveries),
            _escrows, _audit, _gateway, _deliveries)
        {
            SweepMaxRows = 1
        };

        var summary = await RunAsync(cron);

        summary["released"].Should().Be("1");
        summary["payoutsDelayed"].Should().Be("3");
        await ReleasedAsync("esc-paid");
    }

    // ── Not delayed ───────────────────────────────────────────────────

    [Fact]
    public async Task DisputeSweep_FarmerWinsVerdict_IsNotDelayed()
    {
        // An admin verdict is an admin decision: it settles even inside the payout delay.
        var escrow = Confirmed("esc-verdict", DateTime.UtcNow.AddHours(30).ToString("o"), status: "dispute_resolved");
        escrow.Remove("deliveryId");
        escrow["disputeResolution"] = "farmer_wins";
        var swept = _escrows.StubSweep("dispute_resolved", escrow);

        var summary = await RunAsync(Cron());

        swept.AssertServed();
        summary["disputesSettled"].Should().Be("1");
        await ReleasedAsync("esc-verdict");
    }

    // ── Trucker retry ─────────────────────────────────────────────────

    /// <summary>A released escrow (an admin release inside the delay) whose trucker leg went pending.</summary>
    private Dictionary<string, object> PendingLeg(string escrowId, double pendingHoursAgo, double releaseNotBeforeInHours)
    {
        var row = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["status"] = "released",
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["truckerPayout"] = 2_000m,
            ["farmerPayout"] = 9_000m,
            ["amount"] = 11_250m,
            ["telebirrHoldRef"] = "HOLD-7",
            ["telebirrReleaseRef"] = "GW-FARMER-7",
            ["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-60).ToString("o"),
            ["releasedAt"] = DateTime.UtcNow.AddHours(-pendingHoursAgo).ToString("o"),
            [PayoutDelayPolicy.ReleaseNotBeforeField] = DateTime.UtcNow.AddHours(releaseNotBeforeInHours).ToString("o"),
            [TruckerPayoutFields.Pending] = true,
            [TruckerPayoutFields.PendingSince] = DateTime.UtcNow.AddHours(-pendingHoursAgo).ToString("o")
        };
        _pending.Add(row);
        _escrows.GetByIdAsync(escrowId, true).Returns(row);
        return row;
    }

    [Fact]
    public async Task TruckerRetry_BeforeReleaseNotBefore_WaitsAndIsNotOverdue()
    {
        // Pending for 30 hours, the delay ends in 6: not paid, and the wait is not an overdue leg.
        PendingLeg("esc-t1", pendingHoursAgo: 30, releaseNotBeforeInHours: 6);

        var summary = await RunAsync(Cron());

        summary["truckerLegsDeferred"].Should().Be("1");
        summary["truckerLegsPaid"].Should().Be("0");
        await _escrows.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        Logs.Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
    }

    [Fact]
    public async Task TruckerRetry_AfterReleaseNotBefore_Pays()
    {
        PendingLeg("esc-t2", pendingHoursAgo: 30, releaseNotBeforeInHours: -1);

        var summary = await RunAsync(Cron());

        summary["truckerLegsPaid"].Should().Be("1");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:esc-t2:trucker"));
    }

    [Fact]
    public async Task TruckerRetry_OverdueCountsFromTheEndOfTheDelay()
    {
        // Pending 40 hours, the delay ended 25 hours ago, and no time is left for a gateway call.
        PendingLeg("esc-t3", pendingHoursAgo: 40, releaseNotBeforeInHours: -25);
        _context.RemainingTime = EscrowCronFunction.TruckerRetryTimeReserve - TimeSpan.FromSeconds(1);

        await RunAsync(Cron());

        Logs.Should().Contain("TRUCKER_PAYOUT_OVERDUE escrowId=esc-t3 reason=deferred ageHours=25");
    }

    [Fact]
    public async Task TruckerRetry_AFarFutureReleaseNotBefore_NeverSilencesTheOverdueAlarm()
    {
        // Confirmed 200 hours ago: the stored 2099 is read as confirm + 168 hours, 32 hours ago.
        var row = PendingLeg("esc-t4", pendingHoursAgo: 190, releaseNotBeforeInHours: 0);
        row[PayoutDelayPolicy.ReleaseNotBeforeField] = "2099-01-01T00:00:00.0000000Z";
        row["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-200).ToString("o");
        _context.RemainingTime = EscrowCronFunction.TruckerRetryTimeReserve - TimeSpan.FromSeconds(1);

        await RunAsync(Cron());

        Logs.Should().Contain("TRUCKER_PAYOUT_OVERDUE escrowId=esc-t4 reason=deferred ageHours=32");
    }

    // ── The stamp repair ──────────────────────────────────────────────

    [Fact]
    public async Task StampRepair_WritesReleaseNotBefore()
    {
        // An expired /escrow/initiate escrow whose delivery was confirmed but never stamped: the refund sweep
        // stamps it instead, with the payout delay (a 10-day-old farmer account: new, 72 hours).
        var confirmedAt = DateTime.UtcNow.AddHours(-1);
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "esc-repair" }, { "createdAt", CreatedAt }, { "status", "escrowed" },
            { "deliveryId", "del-repair" }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "amount", 8_000m }, { "commission", 200m }, { "farmerPayout", 7_800m }, { "currency", "ETB" },
            { "expiresAt", DateTime.UtcNow.AddDays(-1).ToString("o") }, { "version", 1 }
        };
        _escrows.StubRefundSweep(escrow);
        _escrows.GetByIdAsync("esc-repair", true).Returns(escrow);
        _deliveries.GetByIdAsync("del-repair", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-repair" }, { "status", "delivered" }, { "confirmedAt", confirmedAt.ToString("o") }
        });
        _users.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            CreatedAt = DateTime.UtcNow.AddDays(-10).ToString("o"),
            UpdatedAt = CreatedAt,
            Version = 1
        });

        await RunAsync(Cron(new PayoutDelayPolicy(_fraud, _users, _escrows, PayoutDelaySettings.Default)));

        var stamp = _escrows.ReceivedCalls().Single(c =>
            c.GetMethodInfo().Name == nameof(IEscrowRepository.UpdateStatusAsync)
            && (string)c.GetArguments()[0]! == "esc-repair");
        stamp.GetArguments()[3].Should().Be("escrowed");
        var stamped = (Dictionary<string, object>)stamp.GetArguments()[4]!;
        stamped["deliveryConfirmedAt"].Should().Be(confirmedAt.ToString("o"));
        stamped[PayoutDelayPolicy.ReleaseNotBeforeField].Should().Be(confirmedAt.AddHours(72).ToString("o"));
        Logs.Should().Contain("PAYOUT_DELAY_SET escrowId=esc-repair").And.Contain("farmer=new trucker=-");
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }
}

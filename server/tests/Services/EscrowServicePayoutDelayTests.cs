using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-13 (escrow-protocol.md 1.32.0) in the escrow service: an admin release is not delayed; the trucker
/// retry waits for <c>releaseNotBefore</c> after the payout gate has spoken; the farmer and the trucker read
/// the expected payout date on their escrow and history, the buyer never does.
/// </summary>
public class EscrowServicePayoutDelayTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string ReleaseNotBefore = "2026-10-26T12:00:00.0000000Z";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly EscrowService _service;

    public EscrowServicePayoutDelayTests()
    {
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-REL" });
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _escrows.TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("lock-1");
        _escrows.TryRecordTruckerPayoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrows.TrySetTruckerBlockReasonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker"), ("admin-7", "admin") })
        {
            _users.GetUserByIdAsync(id).Returns(User(id, role, verified: true));
        }

        _service = new EscrowService(_escrows, _audit, _idempotency, _gateway, _users);
    }

    private static UserDto User(string id, string role, bool verified) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000001",
        Role = role,
        Woreda = "w",
        IsPhoneVerified = verified,
        FaydaVerified = verified,
        CreatedAt = "2025-01-01T00:00:00Z",
        UpdatedAt = "2025-01-01T00:00:00Z",
        Version = 1
    };

    /// <summary>A confirmed escrow inside its payout delay (the window has ended, the delay has not).</summary>
    private Dictionary<string, object> Escrow(string? releaseNotBefore = ReleaseNotBefore)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "esc-1" },
            { "createdAt", CreatedAt },
            { "updatedAt", CreatedAt },
            { "status", "escrowed" },
            { "deliveryId", "del-1" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "truckerId", TruckerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 7_300m },
            { "truckerPayout", 500m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-1" },
            { "expiresAt", "2026-10-30T10:00:00.0000000Z" },
            { "deliveryConfirmedAt", "2026-10-23T12:00:00.0000000Z" },
            { "disputeWindowEndsAt", "2026-10-25T12:00:00.0000000Z" },
            { "version", 2 }
        };
        if (releaseNotBefore != null)
        {
            escrow[PayoutDelayPolicy.ReleaseNotBeforeField] = releaseNotBefore;
        }

        _escrows.GetByIdAsync("esc-1").Returns(escrow);
        _escrows.GetByIdAsync("esc-1", Arg.Any<bool>()).Returns(escrow);
        _escrows.GetByDeliveryIdAsync("del-1").Returns(escrow);
        return escrow;
    }

    // ── Not delayed ───────────────────────────────────────────────────

    [Fact]
    public async Task AdminRelease_InsideThePayoutDelay_PaysAtOnce()
    {
        Escrow(DateTime.UtcNow.AddHours(30).ToString("o"));

        var result = await _service.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = "esc-1",
            Trigger = "admin_override",
            DeliveryId = "del-1",
            IdempotencyKey = "admin:esc-1:release:1761220800000"
        }, "admin-7", role: "admin", adminGroupVerified: true);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:esc-1"));
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:esc-1:trucker"));
    }

    // ── Trucker retry ─────────────────────────────────────────────────

    private Dictionary<string, object> PendingLeg(string releaseNotBefore, bool truckerVerified = true)
    {
        var row = Escrow(releaseNotBefore);
        row["status"] = "released";
        row["releasedAt"] = DateTime.UtcNow.AddHours(-2).ToString("o");
        row[TruckerPayoutFields.Pending] = true;
        row[TruckerPayoutFields.PendingSince] = DateTime.UtcNow.AddHours(-2).ToString("o");
        if (!truckerVerified)
        {
            _users.GetUserByIdAsync(TruckerId).Returns(User(TruckerId, "trucker", verified: false));
        }

        return row;
    }

    [Fact]
    public async Task TruckerRetry_BeforeReleaseNotBefore_Defers_NoLockNoCall()
    {
        var row = PendingLeg(DateTime.UtcNow.AddHours(5).ToString("o"));

        var result = await _service.RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true);

        result.Should().Be(new EscrowService.TruckerRetryResult(EscrowService.TruckerRetryOutcome.Deferred, "payout_delay"));
        await _escrows.DidNotReceive().TryAcquireTruckerPayoutLockAsync(Arg.Any<string>(), Arg.Any<string>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task TruckerRetry_ABlockedTruckerHearsTheBlockDuringTheDelay()
    {
        // The gate runs first, so the trucker learns what to fix while the delay runs.
        var row = PendingLeg(DateTime.UtcNow.AddHours(5).ToString("o"), truckerVerified: false);

        var result = await _service.RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Blocked);
        result.Reason.Should().Be("phone_unverified");
    }

    [Fact]
    public async Task TruckerRetry_AfterReleaseNotBefore_Pays()
    {
        var row = PendingLeg(DateTime.UtcNow.AddMinutes(-1).ToString("o"));

        var result = await _service.RetryTruckerLegAsync(row, DateTime.UtcNow, mayCallGateway: true);

        result.Outcome.Should().Be(EscrowService.TruckerRetryOutcome.Paid);
        await _escrows.Received(1).TryAcquireTruckerPayoutLockAsync("esc-1", CreatedAt);
    }

    // ── What each party reads ─────────────────────────────────────────

    [Theory]
    [InlineData(FarmerId)]
    [InlineData(TruckerId)]
    public async Task GetById_ThePayeesSeeThePayoutDate(string reader)
    {
        Escrow();

        var byId = await _service.GetByIdAsync("esc-1", reader);
        var byDelivery = await _service.GetByDeliveryAsync("del-1", reader);

        byId.Value!.ReleaseNotBefore.Should().Be(ReleaseNotBefore);
        byDelivery.Value!.ReleaseNotBefore.Should().Be(ReleaseNotBefore);
        JsonSerializer.Serialize(byId.Value, AppJsonContext.Default.EscrowDto)
            .Should().Contain($"\"releaseNotBefore\":\"{ReleaseNotBefore}\"");
    }

    [Fact]
    public async Task GetById_TheBuyerDoesNot()
    {
        Escrow();

        var result = await _service.GetByIdAsync("esc-1", BuyerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ReleaseNotBefore.Should().BeNull();
        JsonSerializer.Serialize(result.Value, AppJsonContext.Default.EscrowDto).Should().NotContain("releaseNotBefore");
    }

    [Fact]
    public async Task GetById_AnEscrowConfirmedBeforeFrd13_ShowsItsWindowEnd()
    {
        Escrow(releaseNotBefore: null);

        var result = await _service.GetByIdAsync("esc-1", FarmerId);

        result.Value!.ReleaseNotBefore.Should().Be("2026-10-25T12:00:00.0000000Z");
    }

    [Theory]
    [InlineData("farmer", ReleaseNotBefore)]
    [InlineData("trucker", ReleaseNotBefore)]
    [InlineData("buyer", null)]
    public async Task History_OnlyThePayeesRowsCarryTheDate(string role, string? expected)
    {
        var page = (new List<Dictionary<string, object>> { Escrow() }, (Dictionary<string, AttributeValue>?)null);
        var userId = role switch { "farmer" => FarmerId, "trucker" => TruckerId, _ => BuyerId };
        _escrows.QueryByFarmerAsync(FarmerId, null, 20, null).Returns(page);
        _escrows.QueryByTruckerAsync(TruckerId, null, 20, null).Returns(page);
        _escrows.QueryByBuyerAsync(BuyerId, null, 20, null).Returns(page);

        var result = await _service.GetHistoryAsync(userId, null, role, null, null, 20, null);

        result.Value!.Escrows.Should().ContainSingle().Which.ReleaseNotBefore.Should().Be(expected);
    }
}

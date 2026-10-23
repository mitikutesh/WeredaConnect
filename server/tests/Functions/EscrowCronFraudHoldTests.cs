using System.Text.Json;
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
/// FRD-03 (fraud-detection-architecture.md sections 17.2, 18.1, 18.5, 18.7): the risk gate in each settlement
/// sweep of escrow-cron. A suspended party's escrow is held instead of settled, before any money-related side
/// effect (no payout resolution, no lock, no Telebirr, no order cancel, no restock); the auto-refund hold of an
/// order escrow is fenced on the order as read; with <c>FRAUD_HOLD_ENABLED</c> off, or when a party row cannot
/// be read, the escrow is only skipped for the run with <c>RISK_SETTLEMENT_SKIPPED</c>; no sweep ever selects
/// <c>held</c>.
/// </summary>
public class EscrowCronFraudHoldTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string OrdersTable = "orders-test";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly List<(string EscrowId, EscrowHoldRequest Hold, OrderFence? Fence)> _holds = new();

    public EscrowCronFraudHoldTests()
    {
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.StubEmptySweeps();
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _escrows.QueryStaleSettlementLocksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _escrows.TryExtendExpiryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _escrows.TryHoldAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EscrowHoldRequest>(), Arg.Any<OrderFence?>())
            .Returns(ci =>
            {
                _holds.Add((ci.ArgAt<string>(0), ci.ArgAt<EscrowHoldRequest>(2), ci.ArgAt<OrderFence?>(3)));
                return EscrowHoldWrite.Held;
            });
        _orders.QueryAdminQueueAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-REL" });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "GW-REF" });
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        _crops.RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(StockRestoreResult.Restored);

        PartiesAre();
    }

    private EscrowCronFunction Cron(bool flagOn = true, bool withGate = true, bool withTruckerLookup = false)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: null,
            orderRepo: _orders, deliveryRepo: _deliveries, transactionAuditRepo: _txAudit, cropRepo: _crops);
        // The Lambda wires the payout's trucker lookup into the gate the same way.
        Func<Dictionary<string, object>, Task<string?>>? lookup = withTruckerLookup
            ? async escrow => (await service.ResolveTruckerIdAsync(escrow)).TruckerId
            : null;
        return new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries, _orders, _crops,
            transactionAuditRepo: _txAudit)
        {
            RiskGate = withGate ? new PartySuspensionGate(_users, lookup) : null,
            FraudHoldEnabled = flagOn,
            OrdersTableName = OrdersTable
        };
    }

    private void PartiesAre(string? suspended = null)
    {
        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _users.GetUserByIdAsync(id).Returns(new UserDto
            {
                UserId = id,
                PhoneNumber = "+251900000001",
                Role = role,
                Woreda = "w",
                IsPhoneVerified = true,
                Suspended = id == suspended,
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                Version = 1
            });
        }
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

    private static Dictionary<string, object> Escrow(string id, string status = "escrowed", bool withTrucker = false)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", id },
            { "createdAt", DateTime.UtcNow.AddDays(-8).ToString("o") },
            { "status", status },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "farmerPayout", 7_800m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-1" },
            { "expiresAt", DateTime.UtcNow.AddHours(-1).ToString("o") },
            { "version", 2 }
        };
        if (withTrucker)
        {
            escrow["truckerId"] = TruckerId;
        }

        return escrow;
    }

    /// <summary>An escrow the auto-release sweep would pay: delivery confirmed, window passed, no order.</summary>
    private Dictionary<string, object> Releasable(string id, bool withTrucker = false)
    {
        var escrow = Escrow(id, withTrucker: withTrucker);
        escrow["deliveryId"] = $"del-{id}";
        escrow["deliveryConfirmedAt"] = DateTime.UtcNow.AddDays(-3).ToString("o");
        escrow["disputeWindowEndsAt"] = DateTime.UtcNow.AddDays(-1).ToString("o");
        _deliveries.GetByIdAsync($"del-{id}", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", $"del-{id}" }, { "status", "delivered" }
        });
        _escrows.GetByIdAsync(id).Returns(escrow);
        _escrows.GetByIdAsync(id, Arg.Any<bool>()).Returns(escrow);
        return escrow;
    }

    /// <summary>An expired order escrow whose order is still funded (stale): the refund sweep would cancel and refund it.</summary>
    private Dictionary<string, object> StaleOrderEscrow(string orderId, string orderStatus = "funded", int version = 4)
    {
        var escrow = Escrow($"escrow-order-{orderId}");
        escrow["source"] = "order";
        escrow["orderId"] = orderId;
        _escrows.GetByIdAsync($"escrow-order-{orderId}", true).Returns(escrow);
        _escrows.GetByIdAsync($"escrow-order-{orderId}").Returns(escrow);
        OrderIs(orderId, orderStatus, version);
        return escrow;
    }

    private void OrderIs(string orderId, string status, int version)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId }, { "createdAt", "2026-10-01T09:00:00.0000000Z" }, { "status", status },
            { "version", version }, { "listingId", "listing-1" }, { "quantity", "500" }
        };
        _orders.GetByIdAsync(orderId).Returns(order);
        _orders.GetByIdAsync(orderId, true).Returns(order);
    }

    private async Task NoMoneyMovedAsync()
    {
        await _escrows.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Auto-release
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_ASuspendedFarmer_IsHeldForRelease_BeforeThePayoutGateTheLockAndTelebirr()
    {
        PartiesAre(suspended: FarmerId);
        var sweep = _escrows.StubReleaseSweep(Releasable("esc-r1"));

        var summary = await RunAsync(Cron());

        sweep.AssertServed();
        _holds.Should().ContainSingle();
        _holds[0].EscrowId.Should().Be("esc-r1");
        _holds[0].Hold.FromStatus.Should().Be("escrowed");
        _holds[0].Hold.HeldFor.Should().Be("release");
        _holds[0].Hold.HoldReason.Should().Be("account_suspended");
        _holds[0].Fence.Should().BeNull();
        summary["fraudHolds"].Should().Be("1");
        await NoMoneyMovedAsync();
        // No payout resolution and so no release_blocked_kyc row for the held escrow.
        await _audit.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e.ContainsKey("action") && e["action"].ToString() == "release_blocked_kyc"));
        await _audit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e.ContainsKey("action") && e["action"].ToString() == "fraud_hold"
            && e["metadata"].ToString() == "holdReason=account_suspended;heldFor=release;party=farmer;sweep=auto-release"));
        Logs.Should().Contain("ESCROW_FRAUD_HOLD escrowId=esc-r1");
    }

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(TruckerId)]
    public async Task AutoRelease_ASuspendedBuyerOrTrucker_HoldsTheEscrowToo(string suspended)
    {
        PartiesAre(suspended);
        _escrows.StubReleaseSweep(Releasable("esc-r2", withTrucker: true));

        await RunAsync(Cron());

        _holds.Should().ContainSingle(h => h.EscrowId == "esc-r2");
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRelease_NoSuspendedParty_PaysAsBefore()
    {
        _escrows.StubReleaseSweep(Releasable("esc-r3"));

        var summary = await RunAsync(Cron());

        _holds.Should().BeEmpty();
        summary["fraudHolds"].Should().Be("0");
        summary["riskSettlementsSkipped"].Should().Be("0");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task AutoRelease_FlagOff_SkipsTheEscrow_WithTheAlarmToken_NothingWritten()
    {
        PartiesAre(suspended: FarmerId);
        _escrows.StubReleaseSweep(Releasable("esc-r4"));

        var summary = await RunAsync(Cron(flagOn: false));

        _holds.Should().BeEmpty();
        summary["riskSettlementsSkipped"].Should().Be("1");
        Logs.Should().Contain("RISK_SETTLEMENT_SKIPPED escrowId=esc-r4 sweep=auto-release reason=flag_off holdReason=account_suspended");
        await NoMoneyMovedAsync();
        await _escrows.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task AutoRelease_APartyRowThatCannotBeRead_SkipsTheEscrowForTheRun()
    {
        _users.GetUserByIdAsync(FarmerId).ThrowsAsync(new InvalidOperationException("ddb"));
        _escrows.StubReleaseSweep(Releasable("esc-r5"));

        var summary = await RunAsync(Cron());

        _holds.Should().BeEmpty();
        summary["riskSettlementsSkipped"].Should().Be("1");
        Logs.Should().Contain("RISK_SETTLEMENT_SKIPPED escrowId=esc-r5 sweep=auto-release reason=user_read_failed holdReason=-");
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRelease_AHoldThatLosesToASettlement_HoldsAndPaysNothing_OnThisRun()
    {
        PartiesAre(suspended: FarmerId);
        _escrows.TryHoldAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EscrowHoldRequest>(), Arg.Any<OrderFence?>())
            .Returns(EscrowHoldWrite.EscrowChanged);
        _escrows.StubReleaseSweep(Releasable("esc-r6"));

        var summary = await RunAsync(Cron());

        summary["fraudHolds"].Should().Be("0");
        Logs.Should().Contain("ESCROW_FRAUD_HOLD_LOST escrowId=esc-r6 outcome=EscrowChanged");
        await NoMoneyMovedAsync();
        await _audit.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e.ContainsKey("action") && e["action"].ToString() == "fraud_hold"));
    }

    [Fact]
    public async Task AutoRelease_WithoutAGate_SettlesAsBefore()
    {
        PartiesAre(suspended: FarmerId);
        _escrows.StubReleaseSweep(Releasable("esc-r7"));

        await RunAsync(Cron(withGate: false));

        _holds.Should().BeEmpty();
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Auto-refund
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRefund_AStaleFundedOrderOfASuspendedBuyer_IsHeld_FencedOnTheOrderAsRead_NoCancelNoRestock()
    {
        PartiesAre(suspended: BuyerId);
        var sweep = _escrows.StubRefundSweep(StaleOrderEscrow("ord-f1", version: 7));

        await RunAsync(Cron());

        sweep.AssertServed();
        _holds.Should().ContainSingle();
        _holds[0].Hold.FromStatus.Should().Be("escrowed");
        _holds[0].Hold.HeldFor.Should().Be("refund");
        _holds[0].Fence.Should().Be(new OrderFence(OrdersTable, "ord-f1", "2026-10-01T09:00:00.0000000Z", 7, "funded"));
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _crops.DidNotReceiveWithAnyArgs().TryRestoreStockAsync(default!, default!, default);
        await _crops.DidNotReceiveWithAnyArgs().RestoreStockAsync(default!, default!, default);
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_AnAcceptBetweenTheReadAndTheHold_HoldsNothing_AndTheNextRunPushesTheExpiry()
    {
        PartiesAre(suspended: BuyerId);
        _escrows.TryHoldAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EscrowHoldRequest>(), Arg.Any<OrderFence?>())
            .Returns(EscrowHoldWrite.OrderChanged);
        var escrow = StaleOrderEscrow("ord-f2");
        _escrows.StubRefundSweep(escrow);

        await RunAsync(Cron());

        Logs.Should().Contain("ESCROW_FRAUD_HOLD_LOST escrowId=escrow-order-ord-f2 outcome=OrderChanged");
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await NoMoneyMovedAsync();

        // The farmer's accept landed: the next run reads an accepted order and pushes the expiry, as today.
        OrderIs("ord-f2", "accepted", 5);
        await RunAsync(Cron());

        await _escrows.Received(1).TryExtendExpiryAsync("escrow-order-ord-f2", Arg.Any<string>(), Arg.Any<string>());
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_FlagOff_SkipsTheStaleOrder_LeavingItFundedAndStocked()
    {
        PartiesAre(suspended: FarmerId);
        _escrows.StubRefundSweep(StaleOrderEscrow("ord-f3"));

        await RunAsync(Cron(flagOn: false));

        Logs.Should().Contain("RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-f3 sweep=auto-refund reason=flag_off");
        _holds.Should().BeEmpty();
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_ADeliveryInitiatedEscrow_IsHeldAfterItsDeliveryCheck_WithoutAFence()
    {
        PartiesAre(suspended: FarmerId);
        var escrow = Escrow("esc-i1");
        escrow["deliveryId"] = "del-i1";
        _escrows.GetByIdAsync("esc-i1", true).Returns(escrow);
        _deliveries.GetByIdAsync("del-i1", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-i1" }, { "status", "cancelled" }
        });
        _escrows.StubRefundSweep(escrow);

        await RunAsync(Cron());

        _holds.Should().ContainSingle();
        _holds[0].Hold.HeldFor.Should().Be("refund");
        _holds[0].Fence.Should().BeNull();
        await NoMoneyMovedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoRefund_ADeliveryInitiatedEscrowWithAPendingDelivery_IsGatedBeforeTheDeliveryIsCancelled(bool flagOn)
    {
        PartiesAre(suspended: FarmerId);
        var escrow = Escrow("esc-i2");
        escrow["deliveryId"] = "del-i2";
        _escrows.GetByIdAsync("esc-i2", true).Returns(escrow);
        _deliveries.GetByIdAsync("del-i2", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-i2" }, { "status", "pending" }
        });
        _escrows.StubRefundSweep(escrow);

        await RunAsync(Cron(flagOn));

        // The delivery stays pending for a refund the gate held or skipped.
        await _deliveries.DidNotReceiveWithAnyArgs().TryCancelPendingAsync(default!);
        _holds.Count.Should().Be(flagOn ? 1 : 0);
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_ADeliveryInitiatedEscrowWithAPendingDelivery_NoSuspendedParty_CancelsItThenRefunds()
    {
        var escrow = Escrow("esc-i3");
        escrow["deliveryId"] = "del-i3";
        _escrows.GetByIdAsync("esc-i3", true).Returns(escrow);
        _escrows.GetByIdAsync("esc-i3").Returns(escrow);
        _deliveries.GetByIdAsync("del-i3", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-i3" }, { "status", "pending" }
        });
        _deliveries.TryCancelPendingAsync("del-i3").Returns(true);
        _escrows.StubRefundSweep(escrow);

        await RunAsync(Cron());

        _holds.Should().BeEmpty();
        await _deliveries.Received(1).TryCancelPendingAsync("del-i3");
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AutoRelease_ALegacyEscrowWithoutTruckerId_FindsItsTruckerAsThePayoutDoes_AndHoldsForASuspendedOne()
    {
        // Minted before its order had a truck: no truckerId on the escrow, a trucker leg, the order's
        // trucker named by the delivery too. The payout would pay that trucker, so the gate checks them.
        PartiesAre(suspended: TruckerId);
        var escrow = Releasable("esc-t1");
        escrow["truckerPayout"] = 500m;
        escrow["orderId"] = "ord-t1";
        _deliveries.GetByIdAsync("del-esc-t1", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-esc-t1" }, { "status", "delivered" }, { "truckerId", TruckerId }
        });
        _deliveries.GetByIdAsync("del-esc-t1").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-esc-t1" }, { "status", "delivered" }, { "truckerId", TruckerId }
        });
        var order = new Dictionary<string, object>
        {
            { "orderId", "ord-t1" }, { "status", "delivered" }, { "version", 6 }, { "truckerId", TruckerId }
        };
        _orders.GetByIdAsync("ord-t1").Returns(order);
        _orders.GetByIdAsync("ord-t1", true).Returns(order);
        _escrows.StubReleaseSweep(escrow);

        await RunAsync(Cron(withTruckerLookup: true));

        _holds.Should().ContainSingle(h => h.EscrowId == "esc-t1");
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AHoldMeetingAConcurrentTransaction_IsATransientSkip_NotASettlementFailure()
    {
        PartiesAre(suspended: BuyerId);
        _escrows.TryHoldAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EscrowHoldRequest>(), Arg.Any<OrderFence?>())
            .Returns(EscrowHoldWrite.Conflict);
        _escrows.StubRefundSweep(StaleOrderEscrow("ord-f9"));

        await RunAsync(Cron());

        Logs.Should().Contain("ESCROW_FRAUD_HOLD_LOST escrowId=escrow-order-ord-f9 outcome=Conflict");
        Logs.Should().NotContain("ESCROW_SETTLEMENT_FAILED");
        await NoMoneyMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_NoSuspendedParty_CancelsAndRefundsAsBefore()
    {
        _escrows.StubRefundSweep(StaleOrderEscrow("ord-f4"));
        _orders.UpdateAsync("ord-f4", Arg.Any<Dictionary<string, object>>(), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object> { { "status", "cancelled" } }));

        await RunAsync(Cron());

        _holds.Should().BeEmpty();
        await _orders.Received(1).UpdateAsync("ord-f4", Arg.Any<Dictionary<string, object>>(), 4);
        await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // Dispute settlement
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("farmer_wins", "release")]
    [InlineData("buyer_wins", "refund")]
    public async Task DisputeSettlement_ASuspendedParty_HoldsTheVerdict_BeforeItsOrderHopsAndTheMoney(string verdict, string heldFor)
    {
        PartiesAre(suspended: verdict == "farmer_wins" ? FarmerId : BuyerId);
        var escrow = Escrow("esc-d1", "dispute_resolved");
        escrow["disputeResolution"] = verdict;
        escrow["orderId"] = "ord-d1";
        var sweep = _escrows.StubSweep("dispute_resolved", escrow);

        await RunAsync(Cron());

        sweep.AssertServed();
        _holds.Should().ContainSingle();
        _holds[0].Hold.FromStatus.Should().Be("dispute_resolved");
        _holds[0].Hold.HeldFor.Should().Be(heldFor);
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _orders.DidNotReceive().GetByIdAsync("ord-d1", Arg.Any<bool>());
        await NoMoneyMovedAsync();
    }

    // ──────────────────────────────────────────────
    // The run as a whole
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NoSweepEverSelectsHeld_OnlyTheStaleLockRecoveryLooksAtIt()
    {
        var pageQueries = new List<string>();
        _escrows.QueryCronPageAsync(Arg.Do<CronSweepQuery>(q => pageQueries.Add(q.Status)), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        var statusQueries = new List<string>();
        _escrows.QueryByStatusForCronAsync(Arg.Do<string>(statusQueries.Add), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        await RunAsync(Cron());
        await Cron().FunctionHandler(new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", "reconciliation" } })
        }, _context);

        pageQueries.Should().NotBeEmpty().And.NotContain("held");
        statusQueries.Should().NotContain("held");
        // The recovery step may park an admin exit that died under its lock (section 18.4).
        await _escrows.Received().QueryStaleSettlementLocksAsync("held", Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task EachPartyRow_IsReadOncePerRun_AndAgainOnTheNextRun()
    {
        _escrows.StubReleaseSweep(Releasable("esc-m1"), Releasable("esc-m2"));
        var cron = Cron();

        await RunAsync(cron);
        await _users.Received(1).GetUserByIdAsync(FarmerId);
        await _users.Received(1).GetUserByIdAsync(BuyerId);

        await RunAsync(cron);
        await _users.Received(2).GetUserByIdAsync(FarmerId);
    }
}

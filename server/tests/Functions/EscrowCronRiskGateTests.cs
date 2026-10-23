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
/// FRD-04 (fraud-detection-architecture.md 14.4, 17.2, 17.4, 18.1): the risk tier in each settlement sweep of
/// escrow-cron. A party at risk tier CRITICAL holds the escrow (<c>risk_critical</c>) with the flag on and skips
/// it with the flag off; a party whose score could not be read (<c>Source = Unavailable</c>, which carries Tier
/// Low) skips it in both cases. Either way, before any money-related side effect: no order cancel, no stock
/// restore, no payout resolution and so no <c>release_blocked_kyc</c> row, no lock, no Telebirr call. MEDIUM,
/// HIGH and an unconfigured signals table settle as before.
/// </summary>
public class EscrowCronRiskGateTests
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

    /// <summary>The gate's user rows (suspension).</summary>
    private readonly IUserRepository _gateUsers = Substitute.For<IUserRepository>();

    /// <summary>The escrow service's user rows: only the payout resolution reads them.</summary>
    private readonly IUserRepository _payoutUsers = Substitute.For<IUserRepository>();

    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();
    private readonly List<(string EscrowId, EscrowHoldRequest Hold, OrderFence? Fence)> _holds = new();

    public EscrowCronRiskGateTests()
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

        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            var user = new UserDto
            {
                UserId = id,
                PhoneNumber = "+251900000001",
                Role = role,
                Woreda = "w",
                IsPhoneVerified = true,
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                Version = 1
            };
            _gateUsers.GetUserByIdAsync(id).Returns(user);
            _payoutUsers.GetUserByIdAsync(id).Returns(user);
        }

        // Every party LOW unless a test says otherwise.
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(Scored(RiskTier.Low, 0));
    }

    private EscrowCronFunction Cron(bool flagOn = true, bool readsTier = true)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: _payoutUsers,
            orderRepo: _orders, deliveryRepo: _deliveries, transactionAuditRepo: _txAudit, cropRepo: _crops);
        return new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries, _orders, _crops,
            transactionAuditRepo: _txAudit)
        {
            // As the Lambda wires it: the suspension gate, with the risk tier when FRAUD_SIGNALS_TABLE is set.
            RiskGate = new PartySuspensionGate(_gateUsers, fraudService: readsTier ? _fraud : null),
            FraudHoldEnabled = flagOn,
            OrdersTableName = OrdersTable
        };
    }

    private static RiskScoreResult Scored(RiskTier tier, int score, bool truncated = false)
        => new() { Score = score, EnforcementScore = score, Tier = tier, Truncated = truncated };

    private static string PartyId(string party) => party switch
    {
        "buyer" => BuyerId,
        "farmer" => FarmerId,
        _ => TruckerId
    };

    private void ScoreIs(string party, RiskScoreResult score) => _fraud.GetUserRiskScoreAsync(PartyId(party)).Returns(score);

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

    /// <summary>An escrow of the buyer, the farmer and the trucker, 8,000 ETB (below the Fayda threshold).</summary>
    private static Dictionary<string, object> Escrow(string id, string status = "escrowed") => new()
    {
        { "escrowId", id },
        { "createdAt", DateTime.UtcNow.AddDays(-8).ToString("o") },
        { "status", status },
        { "buyerId", BuyerId },
        { "farmerId", FarmerId },
        { "truckerId", TruckerId },
        { "amount", 8_000m },
        { "commission", 200m },
        { "farmerPayout", 7_800m },
        { "currency", "ETB" },
        { "telebirrHoldRef", "HOLD-1" },
        { "expiresAt", DateTime.UtcNow.AddHours(-1).ToString("o") },
        { "version", 2 }
    };

    /// <summary>The sweeps, as <see cref="EscrowCronFunction"/> names them in its alarm lines.</summary>
    public static readonly string[] Sweeps = { "auto-release", "auto-refund", "dispute" };

    /// <summary>Stubs one escrow due in <paramref name="sweep"/>; returns its id and the sweep stub.</summary>
    private (string EscrowId, SweepStub Stub) DueIn(string sweep, int n = 1)
    {
        switch (sweep)
        {
            case "auto-release":
                {
                    // Delivery confirmed, dispute window passed, no order: the release sweep pays it.
                    var escrows = Enumerable.Range(1, n).Select(i =>
                    {
                        var e = Escrow($"esc-r{i}");
                        e["deliveryId"] = $"del-r{i}";
                        e["deliveryConfirmedAt"] = DateTime.UtcNow.AddDays(-3).ToString("o");
                        e["disputeWindowEndsAt"] = DateTime.UtcNow.AddDays(-1).ToString("o");
                        _deliveries.GetByIdAsync($"del-r{i}", true).Returns(new Dictionary<string, object>
                        {
                            { "deliveryId", $"del-r{i}" }, { "status", "delivered" }
                        });
                        _escrows.GetByIdAsync($"esc-r{i}").Returns(e);
                        _escrows.GetByIdAsync($"esc-r{i}", Arg.Any<bool>()).Returns(e);
                        return e;
                    }).ToArray();
                    return ("esc-r1", _escrows.StubReleaseSweep(escrows));
                }
            case "auto-refund":
                {
                    // Expired order escrow whose order is still funded (stale): cancel, restock, refund.
                    var escrow = Escrow("escrow-order-ord-1");
                    escrow["source"] = "order";
                    escrow["orderId"] = "ord-1";
                    _escrows.GetByIdAsync("escrow-order-ord-1", true).Returns(escrow);
                    _escrows.GetByIdAsync("escrow-order-ord-1").Returns(escrow);
                    OrderIs("ord-1", "funded", 4);
                    _orders.UpdateAsync("ord-1", Arg.Any<Dictionary<string, object>>(), 4)
                        .Returns(AuthResult<Dictionary<string, object>>.Success(
                            new Dictionary<string, object> { { "status", "cancelled" } }));
                    return ("escrow-order-ord-1", _escrows.StubRefundSweep(escrow));
                }
            default:
                {
                    // A farmer_wins verdict of a legacy dispute (no order, no delivery): the dispute sweep releases it.
                    var escrow = Escrow("esc-d1", "dispute_resolved");
                    escrow["disputeResolution"] = "farmer_wins";
                    _escrows.GetByIdAsync("esc-d1").Returns(escrow);
                    _escrows.GetByIdAsync("esc-d1", Arg.Any<bool>()).Returns(escrow);
                    return ("esc-d1", _escrows.StubSweep("dispute_resolved", escrow));
                }
        }
    }

    private void OrderIs(string orderId, string status, int version)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId }, { "createdAt", "2026-10-01T09:00:00.0000000Z" }, { "status", status },
            { "version", version }, { "listingId", "listing-1" }, { "quantity", "500" },
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "truckerId", TruckerId }
        };
        _orders.GetByIdAsync(orderId).Returns(order);
        _orders.GetByIdAsync(orderId, true).Returns(order);
    }

    private static string HeldFor(string sweep) => sweep == "auto-refund" ? "refund" : "release";

    private static string FromStatus(string sweep) => sweep == "dispute" ? "dispute_resolved" : "escrowed";

    /// <summary>Nothing money-related happened: section 17.4's list, plus no write to an order, a listing or a delivery.</summary>
    private async Task NothingMovedAsync()
    {
        await _escrows.DidNotReceive().TryAcquireSettlementLockAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _escrows.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default!, default!, default!, default, default);
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _gateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        // No payout resolution (it reads the payee's profile), so no release_blocked_kyc row either.
        _payoutUsers.ReceivedCalls().Should().BeEmpty();
        await _audit.DidNotReceive().AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e.ContainsKey("action") && e["action"].ToString() == "release_blocked_kyc"));
        await _audit.DidNotReceive().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e.ContainsKey("action") && e["action"].ToString() == "release_blocked_kyc"));
        // No order cancel, no stock restore, no pending delivery cancelled: reads only.
        OnlyReads(_orders);
        OnlyReads(_crops);
        OnlyReads(_deliveries);
    }

    private static void OnlyReads(object substitute)
        => substitute.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => !name.StartsWith("Get") && !name.StartsWith("Query"))
            .Should().BeEmpty();

    public static IEnumerable<object[]> CriticalCases()
        => from sweep in Sweeps
           from party in new[] { "buyer", "farmer", "trucker" }
           from flagOn in new[] { true, false }
           select new object[] { sweep, party, flagOn };

    [Theory]
    [MemberData(nameof(CriticalCases))]
    public async Task ACriticalParty_IsHeldWithTheFlagOn_SkippedWithItOff_AndNothingMoves(string sweep, string party, bool flagOn)
    {
        ScoreIs(party, Scored(RiskTier.Critical, 105));
        var (escrowId, stub) = DueIn(sweep);

        var summary = await RunAsync(Cron(flagOn));

        stub.AssertServed();
        if (flagOn)
        {
            _holds.Should().ContainSingle();
            _holds[0].EscrowId.Should().Be(escrowId);
            _holds[0].Hold.HoldReason.Should().Be("risk_critical");
            _holds[0].Hold.FromStatus.Should().Be(FromStatus(sweep));
            _holds[0].Hold.HeldFor.Should().Be(HeldFor(sweep));
            summary["fraudHolds"].Should().Be("1");
            // The tier and the score go to the audit metadata only (section 18.2).
            await _audit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
                e["action"].ToString() == "fraud_hold"
                && e["metadata"].ToString() == $"holdReason=risk_critical;heldFor={HeldFor(sweep)};party={party};sweep={sweep}"
                + ";tier=critical;score=105;enforcementScore=105;truncated=false"));
        }
        else
        {
            _holds.Should().BeEmpty();
            summary["riskSettlementsSkipped"].Should().Be("1");
            Logs.Should().Contain(
                $"RISK_SETTLEMENT_SKIPPED escrowId={escrowId} sweep={sweep} reason=flag_off holdReason=risk_critical");
        }

        await NothingMovedAsync();
    }

    public static IEnumerable<object[]> UnavailableCases()
        => from sweep in Sweeps
           from flagOn in new[] { true, false }
           select new object[] { sweep, flagOn };

    [Theory]
    [MemberData(nameof(UnavailableCases))]
    public async Task AnUnscoredParty_SkipsTheEscrowForTheRun_FlagOnOrOff_AndNothingMoves(string sweep, bool flagOn)
    {
        // Unavailable carries Tier Low: a sweep that read only the tier would settle it (section 14.4).
        ScoreIs("farmer", RiskScoreResult.Unscored(RiskScoreSource.Unavailable));
        var (escrowId, stub) = DueIn(sweep);

        var summary = await RunAsync(Cron(flagOn));

        stub.AssertServed();
        _holds.Should().BeEmpty();
        summary["riskSettlementsSkipped"].Should().Be("1");
        Logs.Should().Contain(
            $"RISK_SETTLEMENT_SKIPPED escrowId={escrowId} sweep={sweep} reason=score_unavailable holdReason=-");
        await NothingMovedAsync();
    }

    public static IEnumerable<object[]> SettlingCases()
        => from sweep in Sweeps
           from tier in new[] { RiskTier.Low, RiskTier.Medium, RiskTier.High }
           select new object[] { sweep, tier };

    [Theory]
    [MemberData(nameof(SettlingCases))]
    public async Task LowMediumAndHigh_SettleAsBefore(string sweep, RiskTier tier)
    {
        foreach (var party in new[] { "buyer", "farmer", "trucker" })
        {
            ScoreIs(party, Scored(tier, tier switch { RiskTier.Medium => 30, RiskTier.High => 60, _ => 0 }));
        }

        DueIn(sweep);

        var summary = await RunAsync(Cron());

        _holds.Should().BeEmpty();
        summary["riskSettlementsSkipped"].Should().Be("0");
        if (sweep == "auto-refund")
        {
            await _gateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
            await _orders.Received(1).UpdateAsync("ord-1", Arg.Any<Dictionary<string, object>>(), 4);
        }
        else
        {
            await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
            // The positive control of NothingMovedAsync: a settlement resolves the payee's profile.
            await _payoutUsers.Received().GetUserByIdAsync(FarmerId);
        }
    }

    [Fact]
    public async Task ASuspendedParty_TakesPrecedenceOverACriticalOne()
    {
        _gateUsers.GetUserByIdAsync(FarmerId).Returns(new UserDto
        {
            UserId = FarmerId,
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            Suspended = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        ScoreIs("buyer", Scored(RiskTier.Critical, 120));
        DueIn("auto-release");

        await RunAsync(Cron());

        _holds.Should().ContainSingle();
        _holds[0].Hold.HoldReason.Should().Be("account_suspended");
        await NothingMovedAsync();
    }

    [Fact]
    public async Task ATruncatedRead_HoldsAsCritical_AndSaysSoInTheAuditMetadata()
    {
        ScoreIs("buyer", Scored(RiskTier.Critical, 20, truncated: true));
        DueIn("auto-refund");

        await RunAsync(Cron());

        _holds.Should().ContainSingle(h => h.Hold.HoldReason == "risk_critical");
        // The auto-refund hold is fenced on the order as read, like FRD-03's.
        _holds[0].Fence.Should().Be(new OrderFence(OrdersTable, "ord-1", "2026-10-01T09:00:00.0000000Z", 4, "funded"));
        await _audit.Received(1).AppendIfAbsentAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "fraud_hold" && e["metadata"].ToString()!.EndsWith(";tier=critical;score=20;enforcementScore=20;truncated=true")));
        await NothingMovedAsync();
    }

    [Fact]
    public async Task AutoRefund_ACriticalBuyersAcceptedOrder_PushesTheExpiryAsBefore_TheGateComesAfter()
    {
        // Section 17.2: the gate sits after the progressing-order expiry extension.
        ScoreIs("buyer", Scored(RiskTier.Critical, 150));
        DueIn("auto-refund");
        OrderIs("ord-1", "accepted", 5);

        await RunAsync(Cron());

        _holds.Should().BeEmpty();
        await _escrows.Received(1).TryExtendExpiryAsync("escrow-order-ord-1", Arg.Any<string>(), Arg.Any<string>());
        await _fraud.DidNotReceiveWithAnyArgs().GetUserRiskScoreAsync(default!);
        await NothingMovedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoRefund_ADeliveryInitiatedEscrowOfACriticalFarmer_IsGatedBeforeItsPendingDeliveryIsCancelled(bool flagOn)
    {
        // An /escrow/initiate escrow (no order) whose 7-day timeout passed with its delivery still pending.
        ScoreIs("farmer", Scored(RiskTier.Critical, 110));
        var escrow = Escrow("esc-i1");
        escrow["deliveryId"] = "del-i1";
        _escrows.GetByIdAsync("esc-i1", true).Returns(escrow);
        _escrows.GetByIdAsync("esc-i1").Returns(escrow);
        _deliveries.GetByIdAsync("del-i1", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-i1" }, { "status", "pending" }
        });
        _deliveries.TryCancelPendingAsync("del-i1").Returns(true);
        var stub = _escrows.StubRefundSweep(escrow);

        await RunAsync(Cron(flagOn));

        stub.AssertServed();
        _holds.Count.Should().Be(flagOn ? 1 : 0);
        if (flagOn)
        {
            _holds[0].Hold.HoldReason.Should().Be("risk_critical");
            _holds[0].Hold.HeldFor.Should().Be("refund");
            _holds[0].Fence.Should().BeNull();
        }

        // The delivery stays pending for a refund the gate held or skipped.
        await NothingMovedAsync();
    }

    [Fact]
    public async Task AnUnconfiguredSignalsTable_IsLow_AndSettles()
    {
        _fraud.GetUserRiskScoreAsync(Arg.Any<string>()).Returns(RiskScoreResult.Unscored(RiskScoreSource.TableNotConfigured));
        DueIn("auto-release");

        var summary = await RunAsync(Cron());

        summary["riskSettlementsSkipped"].Should().Be("0");
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task EachPartyScore_IsReadOncePerRun_AndAgainOnTheNextRun()
    {
        DueIn("auto-release", n: 2);
        var cron = Cron();

        await RunAsync(cron);
        await _fraud.Received(1).GetUserRiskScoreAsync(FarmerId);
        await _fraud.Received(1).GetUserRiskScoreAsync(BuyerId);
        await _fraud.Received(1).GetUserRiskScoreAsync(TruckerId);

        await RunAsync(cron);
        await _fraud.Received(2).GetUserRiskScoreAsync(FarmerId);
    }

    // ── RISK_TABLE_UNCONFIGURED (section 14.4) ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGateThatReadsNoTier_LogsTheTokenOncePerRun_FlagOnOrOff(bool flagOn)
    {
        // With the flag off the flag_off skip is what keeps a CRITICAL party unpaid, so a missing table
        // matters as much as with it on.
        ScoreIs("farmer", Scored(RiskTier.Critical, 150));
        DueIn("auto-release", n: 2);

        await RunAsync(Cron(flagOn, readsTier: false));

        Logs.Split('\n').Count(l => l.Contains("RISK_TABLE_UNCONFIGURED action=auto_release_and_refund")).Should().Be(1);
        // Without the tier the gate is FRD-03's: both escrows of the CRITICAL farmer settle, which is what
        // the token warns about.
        await _gateway.Received(2).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _fraud.DidNotReceiveWithAnyArgs().GetUserRiskScoreAsync(default!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoToken_WhenTheGateReadsTheTier_FlagOnOrOff(bool flagOn)
    {
        DueIn("auto-release");

        await RunAsync(Cron(flagOn, readsTier: true));

        Logs.Should().NotContain("RISK_TABLE_UNCONFIGURED");
    }
}

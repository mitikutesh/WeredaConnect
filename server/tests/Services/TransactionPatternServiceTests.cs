using System.Globalization;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-09 (fraud-detection-architecture.md 15.1): the bounded history reads behind the pattern rules. Each
/// check reads one user's newest rows on an existing GSI through the repositories, newest first, stops at
/// the window or the page cap, never counts the row it was called for twice, and targets the right party.
/// </summary>
public class TransactionPatternServiceTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";

    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();

    private TransactionPatternService Service(TransactionPatternSettings? settings = null) =>
        new(_orders, _escrows, settings ?? TransactionPatternSettings.Default);

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static Dictionary<string, object> Order(
        string id, decimal total, int daysAgo, bool verified = true, string status = "settled",
        string? cancelledBy = null, string? faultParty = null, string? truckerResponse = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", id },
            { "createdAt", Iso(Now.AddDays(-daysAgo)) },
            { "status", status },
            // As DynamoDbHelper.FromItem returns a number: its text.
            { "orderTotal", total.ToString(CultureInfo.InvariantCulture) },
            { "paymentSubmittedAt", Iso(Now.AddDays(-daysAgo).AddMinutes(5)) }
        };
        if (verified)
        {
            order["paymentVerifiedAt"] = Iso(Now.AddDays(-daysAgo).AddMinutes(6));
        }

        if (cancelledBy != null)
        {
            order[OrderCommitment.CancelledByField] = cancelledBy;
        }

        if (faultParty != null)
        {
            order[OrderCommitment.FaultPartyField] = faultParty;
        }

        if (truckerResponse != null)
        {
            order[OrderCommitment.TruckerResponseField] = truckerResponse;
        }

        return order;
    }

    private static (List<Dictionary<string, object>>, Dictionary<string, AttributeValue>?) Page(
        IEnumerable<Dictionary<string, object>> rows, bool more = false) =>
        (rows.ToList(), more ? new Dictionary<string, AttributeValue> { { "orderId", new AttributeValue { S = "next" } } } : null);

    private void BuyerOrders(params Dictionary<string, object>[] rows) =>
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null).Returns(Page(rows));

    // ── payment rules (buyer) ─────────────────────────────

    [Fact]
    public async Task Payment_ThirdInBandPayment_HitsStructuringOnly()
    {
        BuyerOrders(Order("o-1", 9_700m, 3), Order("o-2", 9_900m, 12, verified: false), Order("o-3", 1_500m, 20));

        var hits = await Service().CheckPaymentAsync(BuyerId, "o-now", 9_800m, Iso(Now.AddYears(-1)), Now);

        hits.Select(h => h.SignalType).Should().Equal("KYC_THRESHOLD_STRUCTURING");
        // The buyer's own GSI, one page: no Scan and no other partition.
        await _orders.Received(1).QueryOrdersAsync(BuyerId, null, null, null, null, 50, null);
    }

    [Fact]
    public async Task Payment_CurrentOrderIsNotItsOwnHistory()
    {
        // A retried payment of o-now: its own row (already payment_submitted) must not count as a repeat.
        BuyerOrders(Order("o-now", 9_800m, 0), Order("o-1", 9_700m, 3));

        var hits = await Service().CheckPaymentAsync(BuyerId, "o-now", 9_800m, Iso(Now.AddYears(-1)), Now);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Payment_AllThreeRulesCanHitTogether_InOrder()
    {
        var settings = TransactionPatternSettings.Default with { NewAccountLargeEtb = 5_000m };
        BuyerOrders(Order("o-1", 1_000m, 1), Order("o-2", 1_100m, 1), Order("o-3", 900m, 2), Order("o-4", 1_000m, 2),
            Order("o-5", 1_050m, 3), Order("o-6", 9_500m, 2, verified: false), Order("o-7", 9_600m, 3, verified: false));

        var hits = await Service(settings).CheckPaymentAsync(BuyerId, "o-now", 9_900m, Iso(Now.AddDays(-3)), Now);

        hits.Select(h => h.SignalType).Should().Equal(
            "AMOUNT_ANOMALY", "NEW_ACCOUNT_LARGE_AMOUNT", "KYC_THRESHOLD_STRUCTURING");
    }

    [Fact]
    public async Task Payment_OrdinaryPayment_Misses()
    {
        BuyerOrders(Order("o-1", 2_000m, 3), Order("o-2", 2_500m, 12), Order("o-3", 1_800m, 20));

        var hits = await Service().CheckPaymentAsync(BuyerId, "o-now", 2_200m, Iso(Now.AddYears(-1)), Now);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Payment_ReadStopsAtTheWindowAndThePageCap()
    {
        // Page 1 is full and ends inside the window; page 2 reaches past it: no third page is asked for.
        var page1 = Enumerable.Range(0, 50).Select(i => Order($"a-{i}", 9_500m, 1)).ToList();
        var page2 = new[] { Order("b-1", 9_500m, 10), Order("b-2", 9_500m, 100) };
        var key = new Dictionary<string, AttributeValue> { { "orderId", new AttributeValue { S = "a-49" } } };
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null).Returns((page1, key));
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, key).Returns(Page(page2, more: true));

        var hits = await Service().CheckPaymentAsync(BuyerId, "o-now", 9_500m, null, Now);

        hits.Select(h => h.SignalType).Should().Contain("KYC_THRESHOLD_STRUCTURING");
        await _orders.ReceivedWithAnyArgs(2).QueryOrdersAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task Payment_PageCapOfOne_ReadsOnePage()
    {
        var key = new Dictionary<string, AttributeValue> { { "orderId", new AttributeValue { S = "x" } } };
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null)
            .Returns((new List<Dictionary<string, object>> { Order("o-1", 9_500m, 1) }, key));

        await Service(TransactionPatternSettings.Default with { MaxPages = 1 })
            .CheckPaymentAsync(BuyerId, "o-now", 9_500m, null, Now);

        await _orders.ReceivedWithAnyArgs(1).QueryOrdersAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task Payment_NewAccountRuleNeedsNoHistory()
    {
        var hits = await new TransactionPatternService(null, null, TransactionPatternSettings.Default)
            .CheckPaymentAsync(BuyerId, "o-now", 6_000m, Iso(Now.AddDays(-1)), Now);

        hits.Select(h => h.SignalType).Should().Equal("NEW_ACCOUNT_LARGE_AMOUNT");
    }

    [Fact]
    public async Task Payment_FailedRead_Throws_ForTheCallerToLog()
    {
        _orders.QueryOrdersAsync(BuyerId, null, null, null, null, 50, null).ThrowsAsync(new InvalidOperationException("ddb down"));

        Func<Task> check = () => Service().CheckPaymentAsync(BuyerId, "o-now", 9_500m, null, Now);

        await check.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── cancel after fund (farmer, trucker) ──────────────

    [Fact]
    public async Task CancelAfterFund_Farmer_OwnCancelsOverFundedOrders_Hits()
    {
        _orders.QueryOrdersAsync(null, FarmerId, null, null, null, 50, null).Returns(Page(new[]
        {
            Order("o-1", 5_000m, 5, status: "cancelled", cancelledBy: "farmer", faultParty: "farmer"),
            Order("o-2", 5_000m, 10),
            Order("o-3", 5_000m, 20),
            Order("o-4", 5_000m, 30),
            Order("o-5", 5_000m, 40, verified: false, status: "cancelled") // never funded: not in the base
        }));

        var hits = await Service().CheckCancelAfterFundAsync(FarmerId, TransactionPatternService.Farmer, new[] { "o-now" }, Now);

        hits.Should().ContainSingle().Which.Description.Should().StartWith("The farmer cancelled 2 of 5 funded orders");
    }

    [Fact]
    public async Task CancelAfterFund_Farmer_DeclinesAndNoFaultCancelsAreNotCancels_Misses()
    {
        _orders.QueryOrdersAsync(null, FarmerId, null, null, null, 50, null).Returns(Page(new[]
        {
            // A decline before accepting is an answer; a cancel after the trucker failed is no fault.
            Order("o-1", 5_000m, 5, status: "rejected", cancelledBy: "farmer", faultParty: "farmer"),
            Order("o-2", 5_000m, 6, status: "cancelled", cancelledBy: "farmer", faultParty: "none"),
            Order("o-3", 5_000m, 7, status: "cancelled", cancelledBy: "system", faultParty: "farmer"),
            Order("o-4", 5_000m, 8)
        }));

        var hits = await Service().CheckCancelAfterFundAsync(FarmerId, TransactionPatternService.Farmer, new[] { "o-now" }, Now);

        hits.Should().BeEmpty(); // 1 of 5
    }

    [Fact]
    public async Task CancelAfterFund_NewFarmerFirstCancel_Misses()
    {
        _orders.QueryOrdersAsync(null, FarmerId, null, null, null, 50, null).Returns(Page(Array.Empty<Dictionary<string, object>>()));

        var hits = await Service().CheckCancelAfterFundAsync(FarmerId, TransactionPatternService.Farmer, new[] { "o-now" }, Now);

        hits.Should().BeEmpty(); // 1 of 1: below the minimum sample
    }

    [Fact]
    public async Task CancelAfterFund_Trucker_DropOutsAndTripCancelsCount_OnTheTruckersIndex()
    {
        _orders.QueryOrdersAsync(null, null, TruckerId, null, null, 50, null).Returns(Page(new[]
        {
            Order("o-1", 5_000m, 5, status: "awaiting_transport", truckerResponse: "cancelled"),
            Order("o-2", 5_000m, 6, status: "settled", truckerResponse: "accepted"),
            Order("o-3", 5_000m, 7, status: "awaiting_transport", truckerResponse: "declined"), // an answer
            // The current cancel's own rows, which the index may already show: counted once.
            Order("t-1", 5_000m, 1, status: "awaiting_transport", truckerResponse: "cancelled")
        }));

        var hits = await Service().CheckCancelAfterFundAsync(
            TruckerId, TransactionPatternService.Trucker, new[] { "t-1", "t-2" }, Now);

        hits.Should().ContainSingle().Which.Description.Should().StartWith("The trucker cancelled 3 of 5 funded orders");
        await _orders.DidNotReceive().QueryOrdersAsync(Arg.Any<string?>(), FarmerId, Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    [Fact]
    public async Task CancelAfterFund_BuyerIsNotAParty_ReadsNothing()
    {
        var hits = await Service().CheckCancelAfterFundAsync(BuyerId, "buyer", new[] { "o-now" }, Now);

        hits.Should().BeEmpty();
        _orders.ReceivedCalls().Should().BeEmpty();
    }

    // ── dispute rate (buyer) ─────────────────────────────

    private static Dictionary<string, object> Escrow(string id, string status, int daysAgo, string? disputedBy = null)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", id }, { "buyerId", BuyerId }, { "status", status }, { "createdAt", Iso(Now.AddDays(-daysAgo)) }
        };
        if (disputedBy != null)
        {
            escrow["disputedBy"] = disputedBy;
        }

        return escrow;
    }

    [Fact]
    public async Task DisputeRate_BuyerDisputesTooOften_Hits()
    {
        _escrows.QueryByBuyerAsync(BuyerId, null, 50, null).Returns(Page(new[]
        {
            Escrow("e-1", "dispute_resolved", 3, disputedBy: BuyerId),
            Escrow("e-2", "completed", 10),
            Escrow("e-3", "refunded", 15),
            Escrow("e-4", "released", 20)
        }));

        var hits = await Service().CheckDisputeRateAsync(BuyerId, "e-now", Now);

        hits.Should().ContainSingle().Which.SignalType.Should().Be("DISPUTE_RATE");
        hits[0].Description.Should().StartWith("The buyer disputed 2 of 5 paid orders");
    }

    [Fact]
    public async Task DisputeRate_FarmerDisputesAndUnpaidEscrowsDoNotCount_Misses()
    {
        _escrows.QueryByBuyerAsync(BuyerId, null, 50, null).Returns(Page(new[]
        {
            Escrow("e-1", "disputed", 3, disputedBy: FarmerId), // the farmer's dispute is not the buyer's
            Escrow("e-2", "initiated", 4), // never held money
            Escrow("e-3", "cancelled", 5),
            Escrow("e-4", "completed", 10),
            Escrow("e-5", "completed", 11),
            Escrow("e-6", "completed", 12),
            Escrow("e-7", "completed", 13),
            Escrow("e-now", "disputed", 2, disputedBy: BuyerId), // the current one, counted once
            Escrow("e-old", "dispute_resolved", 120, disputedBy: BuyerId) // outside the window: the read stops
        }));

        var hits = await Service().CheckDisputeRateAsync(BuyerId, "e-now", Now);

        hits.Should().BeEmpty(); // 1 of 6
    }

    [Fact]
    public async Task DisputeRate_NewBuyerFirstDispute_Misses()
    {
        _escrows.QueryByBuyerAsync(BuyerId, null, 50, null).Returns(Page(new[] { Escrow("e-1", "completed", 3) }));

        var hits = await Service().CheckDisputeRateAsync(BuyerId, "e-now", Now);

        hits.Should().BeEmpty(); // 1 of 2: below the minimum sample
    }

    [Fact]
    public async Task DisputeRate_WithoutEscrowRepository_Skips()
    {
        var hits = await new TransactionPatternService(_orders, null, TransactionPatternSettings.Default)
            .CheckDisputeRateAsync(BuyerId, "e-now", Now);

        hits.Should().BeEmpty();
    }
}

using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-03: the order and delivery behind an escrow follow the admin's dispute verdict.
/// Hop rules from fraud-detection-architecture.md section 18.3; both legs are decided before
/// any write, so a leg that cannot follow leaves everything untouched (FRD-03 relies on that).
/// </summary>
public class DisputeVerdictMirrorTests
{
    private const string OrderId = "order-1";
    private const string DeliveryId = "delivery-1";
    private const string ResolvedAt = "2026-10-09T10:00:00.0000000Z";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly DisputeVerdictMirror _mirror;
    private readonly List<string> _orderWrites = new();

    public DisputeVerdictMirrorTests()
    {
        _mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit);
        _deliveries.TryRecordDisputeResolutionAsync(DeliveryId, Arg.Any<string>(), ResolvedAt).Returns(true);
        _deliveries.TryMarkDisputedAsync(DeliveryId, Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
    }

    private static Dictionary<string, object> Escrow(bool withOrder = true, bool withDelivery = true)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "escrow-1" }, { "status", "dispute_resolved" }, { "disputedBy", "buyer-1" }
        };
        if (withOrder)
        {
            escrow["orderId"] = OrderId;
        }

        if (withDelivery)
        {
            escrow["deliveryId"] = DeliveryId;
        }

        return escrow;
    }

    private static Dictionary<string, object> Order(string status, int version, string? activeDeliveryId = null)
    {
        var order = new Dictionary<string, object> { { "orderId", OrderId }, { "status", status }, { "version", version } };
        if (activeDeliveryId != null)
        {
            order["activeDeliveryId"] = activeDeliveryId;
        }

        return order;
    }

    private void OrderIs(string status, int version, string? activeDeliveryId = null)
        => _orders.GetByIdAsync(OrderId, true).Returns(Order(status, version, activeDeliveryId));

    private void DeliveryIs(string status, string? resolution = null)
    {
        var delivery = new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", status }, { "orderId", OrderId } };
        if (resolution != null)
        {
            delivery["disputeResolution"] = resolution;
        }

        _deliveries.GetByIdAsync(DeliveryId, true).Returns(delivery);
    }

    /// <summary>Every order write succeeds and returns the order at the new status and version.</summary>
    private void OrderWritesSucceed()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(call =>
            {
                var status = ((Dictionary<string, object>)call[1])["status"].ToString()!;
                _orderWrites.Add(status);
                return AuthResult<Dictionary<string, object>>.Success(Order(status, (int)call[2] + 1));
            });
    }

    private Task TxRow(string entityType, string action, string from, string to)
        => _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == entityType && e["action"].ToString() == action
            && e["fromState"].ToString() == from && e["toState"].ToString() == to
            && e["actor"].ToString() == "system" && e["metadata"].ToString() == "resolution=buyer_wins"
            && e["transactionId"].ToString() == OrderId));

    [Fact]
    public async Task DisputedOrder_MovesToDisputeResolved_AndDeliveryIsStamped()
    {
        OrderIs("disputed", 5);
        DeliveryIs("disputed");
        OrderWritesSucceed();

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
        result.FailedLegs.Should().BeNull();
        _orderWrites.Should().Equal("dispute_resolved");
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5);
        await _deliveries.DidNotReceive().TryMarkDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _deliveries.Received(1).TryRecordDisputeResolutionAsync(DeliveryId, "buyer_wins", ResolvedAt);
        await TxRow("order", "dispute_resolved", "disputed", "dispute_resolved");
        await TxRow("delivery", "dispute_resolved", "disputed", "disputed");
    }

    [Fact]
    public async Task DeliveredOrder_TakesTheDisputePath_AndTheDeliveryIsDisputedFirst()
    {
        OrderIs("delivered", 3);
        DeliveryIs("delivered");
        OrderWritesSucceed();

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
        _orderWrites.Should().Equal("disputed", "dispute_resolved");
        Received.InOrder(() =>
        {
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 3);
            _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4);
        });
        await _deliveries.Received(1).TryMarkDisputedAsync(DeliveryId, "buyer-1", null);
        await TxRow("order", "dispute", "delivered", "disputed");
        await TxRow("order", "dispute_resolved", "disputed", "dispute_resolved");
        await TxRow("delivery", "dispute", "delivered", "disputed");
    }

    [Theory]
    [InlineData("accepted", "delivered")]
    [InlineData("pickup_confirmed", "delivered")]
    [InlineData("in_transit", "disputed")]
    public async Task OrderLeftBehindItsDelivery_IsAdvancedFirst(string orderStatus, string deliveryStatus)
    {
        OrderIs(orderStatus, 2);
        DeliveryIs(deliveryStatus);
        OrderWritesSucceed();

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        _orderWrites.Should().Equal("delivered", "disputed", "dispute_resolved");
        await TxRow("order", "order_updated", orderStatus, "delivered");
    }

    [Theory]
    [InlineData("in_transit", "inTransit")]
    [InlineData("accepted", "pending")]
    [InlineData("funded", "delivered")]
    [InlineData("cancelled", "delivered")]
    [InlineData("settled", "delivered")]
    public async Task OrderThatCannotFollow_IsAMismatch_AndNothingIsWritten(string orderStatus, string deliveryStatus)
    {
        OrderIs(orderStatus, 2);
        DeliveryIs(deliveryStatus);

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Mismatch);
        result.IsMismatch.Should().BeTrue();
        result.FailedLegs.Should().StartWith($"order:mismatch:{orderStatus}");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _deliveries.DidNotReceive().TryMarkDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _deliveries.DidNotReceive().TryRecordDisputeResolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task DeliveryThatCannotFollow_BlocksTheOrderToo()
    {
        OrderIs("disputed", 5);
        DeliveryIs("cancelled");

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Skipped, "the order leg was clean but nothing moves");
        result.Delivery.Should().Be(VerdictLegOutcome.Mismatch);
        result.FailedLegs.Should().Be("delivery:mismatch:cancelled");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task MissingOrder_IsReported_AndNothingIsWritten()
    {
        _orders.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);
        DeliveryIs("disputed");

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Missing);
        result.FailedLegs.Should().Be("order:missing");
        await _deliveries.DidNotReceive().TryRecordDisputeResolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AlreadyResolved_WritesNothing()
    {
        OrderIs("dispute_resolved", 9);
        DeliveryIs("disputed", resolution: "buyer_wins");

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.AlreadyResolved);
        result.Delivery.Should().Be(VerdictLegOutcome.AlreadyResolved);
        result.FailedLegs.Should().BeNull();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _deliveries.DidNotReceive().TryRecordDisputeResolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task DeliveryWithAnotherVerdict_IsAMismatch()
    {
        OrderIs("disputed", 5);
        DeliveryIs("disputed", resolution: "farmer_wins");

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Delivery.Should().Be(VerdictLegOutcome.Mismatch);
        result.FailedLegs.Should().Be("delivery:verdict:farmer_wins");
    }

    [Fact]
    public async Task VersionConflict_ReReadsAndRetries()
    {
        _orders.GetByIdAsync(OrderId, true).Returns(Order("disputed", 5), Order("disputed", 6));
        DeliveryIs("disputed");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5)
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "conflict"));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 6)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("dispute_resolved", 7)));

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        await _orders.Received(2).GetByIdAsync(OrderId, true);
    }

    [Fact]
    public async Task ConcurrentWriterFinishedTheJob_IsAlreadyResolved()
    {
        _orders.GetByIdAsync(OrderId, true).Returns(Order("disputed", 5), Order("dispute_resolved", 6));
        DeliveryIs("disputed");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5)
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "conflict"));

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.AlreadyResolved);
        result.FailedLegs.Should().BeNull();
    }

    [Fact]
    public async Task ConflictsOnEveryAttempt_AreReportedAsConflict()
    {
        OrderIs("disputed", 5);
        DeliveryIs("disputed");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "conflict"));

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Conflict);
        result.FailedLegs.Should().Be("order:conflict");
        await _orders.Received(3).UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved, "the legs are written independently once decided");
    }

    [Fact]
    public async Task WriteException_IsReportedAsFailed_NeverThrown()
    {
        OrderIs("disputed", 5);
        DeliveryIs("disputed");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .ThrowsAsync(new TimeoutException("boom"));
        _deliveries.TryRecordDisputeResolutionAsync(DeliveryId, Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.FailedLegs.Should().Be("order:[TimeoutException],delivery:[InvalidOperationException]");
    }

    [Fact]
    public async Task LostStampRace_WithTheSameVerdict_IsAlreadyResolved()
    {
        OrderIs("dispute_resolved", 9);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "disputed" } },
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "disputed" }, { "disputeResolution", "buyer_wins" } });
        _deliveries.TryRecordDisputeResolutionAsync(DeliveryId, "buyer_wins", ResolvedAt).Returns(false);

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Delivery.Should().Be(VerdictLegOutcome.AlreadyResolved);
        result.FailedLegs.Should().BeNull();
    }

    [Fact]
    public async Task EscrowWithoutOrderId_TakesItFromTheDelivery()
    {
        DeliveryIs("disputed");
        OrderIs("disputed", 5);
        OrderWritesSucceed();

        var result = await _mirror.ApplyAsync(Escrow(withOrder: false), "buyer_wins", ResolvedAt);

        result.OrderId.Should().Be(OrderId);
        result.Order.Should().Be(VerdictLegOutcome.Resolved);
    }

    [Fact]
    public async Task EscrowWithoutDeliveryId_UsesTheOrdersActiveDelivery()
    {
        OrderIs("disputed", 5, activeDeliveryId: DeliveryId);
        DeliveryIs("disputed");
        OrderWritesSucceed();

        var result = await _mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt);

        result.DeliveryId.Should().Be(DeliveryId);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
    }

    [Fact]
    public async Task NoIdsOrNoRepositories_IsNothingToDo()
    {
        var bare = new DisputeVerdictMirror(null, null, null);

        var noIds = await _mirror.ApplyAsync(Escrow(withOrder: false, withDelivery: false), "buyer_wins", ResolvedAt);
        var noRepos = await bare.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        noIds.Order.Should().Be(VerdictLegOutcome.None);
        noIds.Delivery.Should().Be(VerdictLegOutcome.None);
        noIds.FailedLegs.Should().BeNull();
        noRepos.FailedLegs.Should().BeNull();
    }

    [Fact]
    public async Task WithoutATransactionAuditRepository_StillMovesTheOrder()
    {
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, null);
        OrderIs("disputed", 5);
        DeliveryIs("disputed");
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
    }
}

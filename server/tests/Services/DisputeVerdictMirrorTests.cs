using Amazon.DynamoDBv2.Model;
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
    // buyer_wins with the goods already moving.
    [InlineData("buyer_wins", "in_transit", "inTransit")]
    [InlineData("buyer_wins", "pickup_confirmed", "pickedUp")]
    [InlineData("buyer_wins", "accepted", "pickedUp")]
    [InlineData("buyer_wins", "settled", "delivered")]
    // farmer_wins before delivery, or on a closed order.
    [InlineData("farmer_wins", "accepted", "pending")]
    [InlineData("farmer_wins", "funded", "pending")]
    [InlineData("farmer_wins", "cancelled", "cancelled")]
    [InlineData("farmer_wins", "in_transit", "inTransit")]
    public async Task OrderThatCannotFollow_IsAMismatch_AndNothingIsWritten(
        string resolution, string orderStatus, string deliveryStatus)
    {
        OrderIs(orderStatus, 2);
        DeliveryIs(deliveryStatus);

        var result = await _mirror.ApplyAsync(Escrow(), resolution, ResolvedAt, cancelBeforePickup: true);

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
    public async Task ConflictOnTheLastHop_AfterTwoSuccessfulHops_IsStillRetried()
    {
        // From in_transit the order takes three hops; successful hops must not use up the
        // conflict retries, so one conflict on the last hop (at v6) is retried and resolves.
        _orders.GetByIdAsync(OrderId, true).Returns(Order("in_transit", 4), Order("disputed", 7));
        DeliveryIs("delivered");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 4)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("delivered", 5)));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 5)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("disputed", 6)));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 6)
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "conflict"));
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 7)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("dispute_resolved", 8)));

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.FailedLegs.Should().BeNull();
        await _orders.Received(1).UpdateAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "dispute_resolved"), 7);
    }

    [Fact]
    public void VerdictAuditTimestamp_IsTheVerdictTimePlusOneTickPerAction()
    {
        // Right after the verdict's own row, and the same key on every run.
        DisputeVerdictMirror.VerdictAuditTimestamp("2026-10-09T14:30:00.0000000Z", DisputeVerdictMirror.MismatchAuditAction)
            .Should().Be("2026-10-09T14:30:00.0000001Z");
        DisputeVerdictMirror.VerdictAuditTimestamp("2026-10-09T14:30:00.0000000Z", DisputeVerdictMirror.PropagationFailedAuditAction)
            .Should().Be("2026-10-09T14:30:00.0000002Z");
        DisputeVerdictMirror.VerdictAuditTimestamp("2026-10-09T14:30:00Z", DisputeVerdictMirror.MismatchAuditAction)
            .Should().Be("2026-10-09T14:30:00.0000001Z");
        // Unreadable: the start of the current UTC day.
        DisputeVerdictMirror.VerdictAuditTimestamp("not-a-time", DisputeVerdictMirror.MismatchAuditAction)
            .Should().Be(DateTime.UtcNow.Date.AddTicks(1).ToString("o"));
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

    [Theory]
    [InlineData("order")]
    [InlineData("delivery")]
    public async Task ReadFailure_IsUndecided_NothingWritten_NeverAMismatch(string failingRead)
    {
        // MAJOR 2: a throttled read must never look like "nothing to block" to the settlement.
        OrderIs("disputed", 5);
        DeliveryIs("disputed");
        if (failingRead == "order")
        {
            _orders.GetByIdAsync(OrderId, true).ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));
        }
        else
        {
            _deliveries.GetByIdAsync(DeliveryId, true).ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));
        }

        var result = await _mirror.ApplyAsync(Escrow(), "farmer_wins", ResolvedAt);

        result.IsUndecided.Should().BeTrue();
        result.IsMismatch.Should().BeFalse();
        result.Order.Should().Be(VerdictLegOutcome.Unknown);
        result.FailedLegs.Should().Be("order:read:[ProvisionedThroughputExceededException],delivery:read:[ProvisionedThroughputExceededException]");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _deliveries.DidNotReceive().TryRecordDisputeResolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task BuyerWins_OnAClosedOrder_HasNothingToMove(string status)
    {
        OrderIs(status, 4);

        var result = await _mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Closed);
        result.IsMismatch.Should().BeFalse();
        result.FailedLegs.Should().BeNull();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    private Dictionary<string, object> ListedOrder(string status, int version, string? activeDeliveryId = null)
    {
        var order = Order(status, version, activeDeliveryId);
        order["listingId"] = "listing-1";
        order["quantity"] = "250";
        return order;
    }

    [Fact]
    public async Task BuyerWins_BeforePickup_CancelsDeliveryThenOrder_AndRestoresStock()
    {
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("accepted", 3, DeliveryId));
        DeliveryIs("pending");
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(true);
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        result.Delivery.Should().Be(VerdictLegOutcome.Cancelled);
        result.FailedLegs.Should().BeNull();
        Received.InOrder(() =>
        {
            _deliveries.TryCancelPendingAsync(DeliveryId);
            _orders.UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(d =>
                d["status"].ToString() == "cancelled" && d["cancelReason"].ToString() == "escrow_refunded"), 3);
            _orders.ReleaseDeliverySlotAsync(OrderId, DeliveryId);
            crops.RestoreStockAsync("listing-1", OrderId, 2.5);
        });
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "order" && e["action"].ToString() == "order_cancelled"
            && e["fromState"].ToString() == "accepted" && e["toState"].ToString() == "cancelled"));
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "delivery" && e["toState"].ToString() == "cancelled"));
    }

    private static ICropRepository RestoringCrops(StockRestoreResult result = StockRestoreResult.Restored)
    {
        var crops = Substitute.For<ICropRepository>();
        crops.RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(result);
        return crops;
    }

    [Theory]
    [InlineData("500")]
    [InlineData("409")]
    [InlineData("throw")]
    public async Task BuyerWins_OrderCancelThatDoesNotLand_BlocksTheRefund(string failure)
    {
        // The reviewer's probe: the cancel fails, so the order may still be live; refunding now
        // would pay the buyer back while the farmer can still act on the order.
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("funded", 2));
        var cancel = _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        if (failure == "throw")
        {
            cancel.ThrowsAsync(new TimeoutException("boom"));
        }
        else
        {
            cancel.Returns(AuthResult<Dictionary<string, object>>.Failure(int.Parse(failure), "SYNC_X", "nope"));
        }

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Blocked);
        result.BlocksSettlement.Should().BeTrue();
        result.IsMismatch.Should().BeFalse();
        result.FailedLegs.Should().StartWith("order:cancel:");
        await crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task BuyerWins_RestockThatDoesNotLand_BlocksTheRefund_AndTheNextRunRetriesIt()
    {
        var crops = RestoringCrops(StockRestoreResult.Failed);
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("funded", 2));
        OrderWritesSucceed();

        var first = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        first.Order.Should().Be(VerdictLegOutcome.Blocked);
        first.FailedLegs.Should().Be("order:restock:conflict");
        _orderWrites.Should().Equal("cancelled");

        // Next run: the order is closed now; the restore is retried before the refund.
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("cancelled", 3));
        crops.RestoreStockAsync("listing-1", OrderId, 2.5).Returns(StockRestoreResult.Restored);

        var second = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        second.Order.Should().Be(VerdictLegOutcome.Closed);
        second.BlocksSettlement.Should().BeFalse();
        await crops.Received(2).RestoreStockAsync("listing-1", OrderId, 2.5);
        _orderWrites.Should().Equal("cancelled");
    }

    [Fact]
    public async Task BuyerWins_TheOrdersActiveDeliveryCounts_NotTheEscrowsEarlierOne()
    {
        // The escrow still names a delivery cancelled earlier; the order's active delivery is
        // already picked up, so the order must not be cancelled.
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("accepted", 3, "delivery-2"));
        DeliveryIs("cancelled");
        _deliveries.GetByIdAsync("delivery-2", true).Returns(
            new Dictionary<string, object> { { "deliveryId", "delivery-2" }, { "status", "pickedUp" } });
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, RestoringCrops());

        var result = await mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.IsMismatch.Should().BeTrue();
        result.DeliveryId.Should().Be("delivery-2");
        result.FailedLegs.Should().Be("order:mismatch:accepted,delivery:mismatch:pickedUp");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task BuyerWins_FundedOrder_IsCancelled_WithNoDelivery()
    {
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("funded", 2));
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        _orderWrites.Should().Equal("cancelled");
        await crops.Received(1).RestoreStockAsync("listing-1", OrderId, 2.5);
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task BuyerWins_BeforePickup_IsDeferred_WhenTheCallerDoesNotCancel()
    {
        // The admin verdict leaves the cancel to the refund settlement.
        OrderIs("accepted", 3, DeliveryId);
        DeliveryIs("pending");

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Deferred);
        result.Delivery.Should().Be(VerdictLegOutcome.Deferred);
        result.FailedLegs.Should().BeNull();
        result.IsMismatch.Should().BeFalse();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _deliveries.DidNotReceive().TryCancelPendingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task BuyerWins_PickupRacesTheCancel_LeavesTheOrder_AndIsAMismatch()
    {
        OrderIs("accepted", 3, DeliveryId);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "pending" } },
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "pickedUp" } });
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(false);

        var result = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.IsMismatch.Should().BeTrue();
        result.FailedLegs.Should().Be("delivery:mismatch:pickedUp");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task AnUnreadableDelivery_IsNeverTakenForNoDelivery()
    {
        // The order names a delivery but no delivery repository is wired: cancelling would be blind.
        var mirror = new DisputeVerdictMirror(_orders, null, _txAudit, Substitute.For<ICropRepository>());
        OrderIs("accepted", 3, DeliveryId);

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.IsMismatch.Should().BeTrue();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
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

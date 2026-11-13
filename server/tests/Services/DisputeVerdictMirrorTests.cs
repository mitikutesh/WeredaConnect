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
        DisputeVerdictMirror.VerdictAuditTimestamp("2026-10-09T14:30:00.0000000Z", DisputeVerdictMirror.SettlementBlockedAuditAction)
            .Should().Be("2026-10-09T14:30:00.0000003Z");
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
    public async Task BuyerWins_AwaitingTransportOrder_IsCancelled_LikeAFundedOne()
    {
        // ESC-15: the transport failed and the money is still held; nothing left the farm.
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("awaiting_transport", 4));
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        _orderWrites.Should().Equal("cancelled");
        await crops.Received(1).RestoreStockAsync("listing-1", OrderId, 2.5);
    }

    // ──────────────────────────────────────────────
    // FUP-123: the cancel releases the order's trip booking
    // ──────────────────────────────────────────────

    private const string TripId = "trip-1";

    /// <summary>A listed order (250 kg, so 2.5 quintals) booked on <see cref="TripId"/>.</summary>
    private Dictionary<string, object> TripOrder(string status, int version, string? activeDeliveryId = null)
    {
        var order = ListedOrder(status, version, activeDeliveryId);
        order["tripId"] = TripId;
        return order;
    }

    private static ITripRepository ReleasingTrips()
    {
        var trips = Substitute.For<ITripRepository>();
        trips.TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>()).Returns(true);
        return trips;
    }

    [Theory]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    public async Task BuyerWins_CancelBeforePickup_ReleasesTheTripBooking(string status)
    {
        var crops = RestoringCrops();
        var trips = ReleasingTrips();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops, trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder(status, 2));
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        result.BlocksSettlement.Should().BeFalse();
        await trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 2.5);
        Received.InOrder(() =>
        {
            _orders.UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(d =>
                d["status"].ToString() == "cancelled" && d["cancelReason"].ToString() == "escrow_refunded"), 2);
            trips.TryReleaseCapacityAsync(TripId, OrderId, 2.5);
        });
    }

    [Fact]
    public async Task BuyerWins_AcceptedWithPendingDelivery_ReleasesTheTripBooking()
    {
        var trips = ReleasingTrips();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, RestoringCrops(), trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder("accepted", 3, DeliveryId));
        DeliveryIs("pending");
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(true);
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        result.Delivery.Should().Be(VerdictLegOutcome.Cancelled);
        await trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 2.5);
    }

    [Fact]
    public async Task BuyerWins_TripReleaseThatThrows_NeitherBlocksTheRefundNorTheRestock()
    {
        var crops = RestoringCrops();
        var trips = Substitute.For<ITripRepository>();
        trips.TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .ThrowsAsync(new TimeoutException("boom"));
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops, trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder("funded", 2));
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        result.BlocksSettlement.Should().BeFalse();
        result.FailedLegs.Should().BeNull();
        _orderWrites.Should().Equal("cancelled");
        await crops.Received(1).RestoreStockAsync("listing-1", OrderId, 2.5);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("409")]
    [InlineData("throw")]
    public async Task BuyerWins_CancelThatDoesNotLand_ReleasesNothing(string failure)
    {
        var trips = ReleasingTrips();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, RestoringCrops(), trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder("funded", 2));
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
        await trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Theory]
    [InlineData("cancelled", true)]
    [InlineData("funded", false)]
    public async Task BuyerWins_WithoutACancelThisRun_ReleasesNothing(string status, bool cancelBeforePickup)
    {
        // Already closed (the cancel that closed it released the booking), or the admin verdict
        // that defers the cancel to the refund settlement.
        var trips = ReleasingTrips();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, RestoringCrops(), trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder(status, 3));

        await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: cancelBeforePickup);

        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await trips.DidNotReceive().TryReleaseCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task AdminRefundCancel_ReleasesTheTripBooking()
    {
        // ESC-09 Part B goes through the same cancel, so it releases the booking too.
        var trips = ReleasingTrips();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, RestoringCrops(), trips);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder("funded", 2));
        OrderWritesSucceed();

        var result = await mirror.CancelBeforeRefundAsync(Escrow(withDelivery: false));

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        await trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 2.5);
    }

    [Fact]
    public async Task BuyerWins_CancelWithoutATripRepository_StillCancelsAndRestocks()
    {
        // COMMITMENTS_ENABLED off (or no trips table): nothing is booked, so nothing is released.
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(TripOrder("funded", 2));
        OrderWritesSucceed();

        var result = await mirror.ApplyAsync(Escrow(withDelivery: false), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        result.Order.Should().Be(VerdictLegOutcome.Cancelled);
        await crops.Received(1).RestoreStockAsync("listing-1", OrderId, 2.5);
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

    // ──────────────────────────────────────────────
    // FUP-16: dispositions for a verdict the legs cannot follow as they are
    // ──────────────────────────────────────────────

    private void DeliveryClosesFor(string resolution)
        => _deliveries.TryCloseForVerdictAsync(DeliveryId, Arg.Any<string>(), Arg.Any<string>(), resolution, ResolvedAt, Arg.Any<string?>())
            .Returns(true);

    private void OrderAdvances(string from)
        => _orders.TryAdvanceStatusAsync(OrderId, from, "dispute_resolved").Returns(Order("dispute_resolved", 9));

    private Task NoLegWritten()
    {
        _deliveries.DidNotReceive().TryCloseForVerdictAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        return _orders.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PlanDisposition_LegsThatFollow_NeedNone()
    {
        OrderIs("disputed", 5);
        DeliveryIs("disputed");

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");

        plan.Case.Should().Be(DispositionCase.NotNeeded);
        plan.Legs.Should().BeNull();
    }

    [Theory]
    [InlineData("in_transit", "inTransit")]
    [InlineData("pickup_confirmed", "pickedUp")]
    [InlineData("accepted", "pickedUp")]
    public async Task PlanDisposition_BuyerWinsWithTheGoodsMoving_OffersTheGoodsChoices(string orderStatus, string deliveryStatus)
    {
        OrderIs(orderStatus, 4);
        DeliveryIs(deliveryStatus);

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");

        plan.Case.Should().Be(DispositionCase.GoodsMoving);
        plan.Fits(DisputeDisposition.ReturnToFarmer).Should().BeTrue();
        plan.Fits(DisputeDisposition.DeliveredAnyway).Should().BeTrue();
        plan.Fits(DisputeDisposition.SettleNow).Should().BeFalse();
        plan.Legs.Should().Contain("mismatch:");
        await NoLegWritten();
    }

    [Fact]
    public async Task PlanDisposition_FarmerWinsAfterPickup_OffersSettleNowAndWait()
    {
        OrderIs("pickup_confirmed", 4);
        DeliveryIs("pickedUp");

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "farmer_wins");

        plan.Case.Should().Be(DispositionCase.BeforeDelivery);
        plan.GoodsHandedOver.Should().BeTrue();
        plan.Fits(DisputeDisposition.SettleNow).Should().BeTrue();
        plan.Fits(DisputeDisposition.WaitForDelivery).Should().BeTrue();
        plan.Fits(DisputeDisposition.ReturnToFarmer).Should().BeFalse();
    }

    [Theory]
    [InlineData("accepted", "pending")]
    [InlineData("funded", null)]
    public async Task PlanDisposition_FarmerWinsBeforePickup_CanOnlyWait(string orderStatus, string? deliveryStatus)
    {
        // The farmer keeps the money only for goods already handed over.
        OrderIs(orderStatus, 2);
        if (deliveryStatus != null)
        {
            DeliveryIs(deliveryStatus);
        }

        var plan = await _mirror.PlanDispositionAsync(Escrow(withDelivery: deliveryStatus != null), "farmer_wins");

        plan.Case.Should().Be(DispositionCase.BeforeDelivery);
        plan.GoodsHandedOver.Should().BeFalse();
        plan.Fits(DisputeDisposition.SettleNow).Should().BeFalse();
        plan.Fits(DisputeDisposition.WaitForDelivery).Should().BeTrue();
    }

    [Theory]
    [InlineData("farmer_wins", "cancelled", "cancelled", null)]
    [InlineData("farmer_wins", "disbursing", "delivered", null)]
    [InlineData("buyer_wins", "in_transit", "disputed", "farmer_wins")]
    public async Task PlanDisposition_NothingFits_WhenTheOrderIsClosedOrTheDeliveryHasAnotherVerdict(
        string resolution, string orderStatus, string deliveryStatus, string? deliveryVerdict)
    {
        OrderIs(orderStatus, 3);
        DeliveryIs(deliveryStatus, deliveryVerdict);

        var plan = await _mirror.PlanDispositionAsync(Escrow(), resolution);

        plan.Case.Should().Be(DispositionCase.NoFit);
        plan.Legs.Should().NotBeNull();
        foreach (var disposition in new[] { "return_to_farmer", "delivered_anyway", "settle_now", "wait_for_delivery" })
        {
            plan.Fits(disposition).Should().BeFalse(disposition);
        }
    }

    [Fact]
    public async Task PlanDisposition_MissingOrder_NothingFits()
    {
        _orders.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);
        DeliveryIs("inTransit");

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");

        plan.Case.Should().Be(DispositionCase.NoFit);
    }

    [Fact]
    public async Task PlanDisposition_ReadFailure_IsUnknown_AndApplyWritesNothing()
    {
        _orders.GetByIdAsync(OrderId, true).ThrowsAsync(new InvalidOperationException("orders down"));

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        plan.Case.Should().Be(DispositionCase.Unknown);
        result.IsUndecided.Should().BeTrue();
        await NoLegWritten();
    }

    [Fact]
    public async Task ReturnToFarmer_CancelsTheDeliveryFirst_ThenResolvesTheOrder_WithoutRestock()
    {
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("in_transit", 4));
        DeliveryIs("inTransit");
        DeliveryClosesFor("buyer_wins");
        OrderAdvances("in_transit");

        var result = await mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
        result.FailedLegs.Should().BeNull();
        Received.InOrder(() =>
        {
            _deliveries.TryCloseForVerdictAsync(DeliveryId, "inTransit", "cancelled", "buyer_wins", ResolvedAt, "dispute_buyer_wins");
            _orders.TryAdvanceStatusAsync(OrderId, "in_transit", "dispute_resolved");
        });
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "delivery" && e["action"].ToString() == "delivery_cancelled"
            && e["fromState"].ToString() == "inTransit" && e["toState"].ToString() == "cancelled"
            && e["actor"].ToString() == "system"
            && e["metadata"].ToString() == "resolution=buyer_wins;disposition=return_to_farmer;cancelReason=dispute_buyer_wins"));
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "order" && e["action"].ToString() == "dispute_resolved"
            && e["fromState"].ToString() == "in_transit" && e["toState"].ToString() == "dispute_resolved"
            && e["metadata"].ToString() == "resolution=buyer_wins;disposition=return_to_farmer"));
        // The goods may come back damaged: the farmer relists what is sellable.
        await crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        // The status-conditioned hop, never the versioned dispute path.
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task DeliveredAnyway_DisputesTheDeliveryUnderTheVerdict_AndResolvesTheOrder()
    {
        OrderIs("pickup_confirmed", 4);
        DeliveryIs("pickedUp");
        DeliveryClosesFor("buyer_wins");
        OrderAdvances("pickup_confirmed");

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.DeliveredAnyway, ResolvedAt);

        result.FailedLegs.Should().BeNull();
        await _deliveries.Received(1).TryCloseForVerdictAsync(DeliveryId, "pickedUp", "disputed", "buyer_wins", ResolvedAt, null);
        await _orders.Received(1).TryAdvanceStatusAsync(OrderId, "pickup_confirmed", "dispute_resolved");
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "delivery" && e["action"].ToString() == "dispute_resolved"
            && e["fromState"].ToString() == "pickedUp" && e["toState"].ToString() == "disputed"));
    }

    [Fact]
    public async Task SettleNow_CancelsTheDelivery_AndResolvesTheOrder_WithoutRestock()
    {
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("in_transit", 4));
        DeliveryIs("inTransit");
        DeliveryClosesFor("farmer_wins");
        OrderAdvances("in_transit");

        var result = await mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleNow, ResolvedAt);

        result.FailedLegs.Should().BeNull();
        await _deliveries.Received(1).TryCloseForVerdictAsync(
            DeliveryId, "inTransit", "cancelled", "farmer_wins", ResolvedAt, "dispute_farmer_wins");
        await _orders.Received(1).TryAdvanceStatusAsync(OrderId, "in_transit", "dispute_resolved");
        // The farmer keeps the money for goods already handed over: no stock comes back.
        await crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task LegsThatFollowTheVerdictMeanwhile_AreNotTouched_AndSayNotNeeded()
    {
        // The handoff landed between the admin's decision and the apply.
        OrderIs("delivered", 5);
        DeliveryIs("delivered");

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        result.DispositionNotNeeded.Should().BeTrue();
        result.FailedLegs.Should().BeNull();
        await NoLegWritten();
    }

    [Fact]
    public async Task WaitForDelivery_WritesNothing()
    {
        OrderIs("in_transit", 4);
        DeliveryIs("inTransit");

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.WaitForDelivery, ResolvedAt);

        result.FailedLegs.Should().BeNull();
        await NoLegWritten();
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task ADispositionThatDoesNotFit_WritesNothing()
    {
        OrderIs("accepted", 2);
        DeliveryIs("pending");

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleNow, ResolvedAt);

        result.IsMismatch.Should().BeTrue();
        result.FailedLegs.Should().Be("order:disposition:settle_now");
        await NoLegWritten();
    }

    [Fact]
    public async Task AHandoffThatWinsTheRace_LeavesTheOrderAlone()
    {
        OrderIs("in_transit", 4);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "inTransit" } },
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "delivered" } });

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        result.Delivery.Should().Be(VerdictLegOutcome.Mismatch);
        result.Order.Should().Be(VerdictLegOutcome.Skipped);
        result.FailedLegs.Should().Be("delivery:mismatch:delivered");
        await _orders.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AReplay_FinishesARunThatStoppedAfterTheDelivery()
    {
        OrderIs("in_transit", 4);
        DeliveryIs("cancelled", "buyer_wins");
        OrderAdvances("in_transit");

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        plan.Started.Should().BeTrue();
        plan.Fits(DisputeDisposition.ReturnToFarmer).Should().BeTrue();
        // The delivery is closed as cancelled: the other goods choice no longer fits.
        plan.Fits(DisputeDisposition.DeliveredAnyway).Should().BeFalse();
        result.Delivery.Should().Be(VerdictLegOutcome.AlreadyResolved);
        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        await _deliveries.DidNotReceive().TryCloseForVerdictAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnOrderThatMovedOn_IsAConflict_ForTheReplay()
    {
        OrderIs("in_transit", 4);
        DeliveryIs("inTransit");
        DeliveryClosesFor("buyer_wins");
        _orders.TryAdvanceStatusAsync(OrderId, "in_transit", "dispute_resolved").Returns((Dictionary<string, object>?)null);
        _orders.GetByIdAsync(OrderId, true).Returns(Order("in_transit", 4), Order("delivered", 5));

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Conflict);
        result.FailedLegs.Should().Be("order:mismatch:delivered");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("disputed")]
    public async Task AFullyAppliedDisposition_IsFollowedBy_TheSettlementMirror(string deliveryStatus)
    {
        // The sweep never applies a disposition; it only has to see the legs following the verdict.
        OrderIs("dispute_resolved", 9);
        DeliveryIs(deliveryStatus, "buyer_wins");

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        var settlement = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        plan.Case.Should().Be(DispositionCase.NotNeeded);
        settlement.Order.Should().Be(VerdictLegOutcome.AlreadyResolved);
        settlement.Delivery.Should().Be(VerdictLegOutcome.AlreadyResolved);
        settlement.FailedLegs.Should().BeNull();
    }

    [Fact]
    public async Task ADeliveryCancelledUnderTheOtherVerdict_StillBlocksTheSettlement()
    {
        OrderIs("dispute_resolved", 9);
        DeliveryIs("cancelled", "farmer_wins");

        var settlement = await _mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);

        settlement.IsMismatch.Should().BeTrue();
        settlement.FailedLegs.Should().Be("delivery:mismatch:cancelled");
    }

    [Fact]
    public async Task ADeliveryCancelledByADisposition_IsNotBeforePickup_SoTheRefundNeverCancelsAndRestocks()
    {
        // FUP-16 review: return_to_farmer on an order at accepted cancelled the picked-up delivery, then
        // the order write failed. The refund must wait for the admin's replay, not take the
        // cancel-before-pickup path (which would restore stock and cancel the order).
        var crops = RestoringCrops();
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit, crops);
        _orders.GetByIdAsync(OrderId, true).Returns(ListedOrder("accepted", 4));
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", "cancelled" },
            { "cancelReason", "dispute_buyer_wins" }, { "disputeResolution", "buyer_wins" }
        });
        OrderAdvances("accepted");

        var settlement = await mirror.ApplyAsync(Escrow(), "buyer_wins", ResolvedAt, cancelBeforePickup: true);
        var plan = await mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        var replay = await mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.ReturnToFarmer, ResolvedAt);

        settlement.IsMismatch.Should().BeTrue();
        settlement.FailedLegs.Should().Be("order:mismatch:accepted");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await crops.DidNotReceive().RestoreStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
        plan.Case.Should().Be(DispositionCase.GoodsMoving);
        plan.Fits(DisputeDisposition.ReturnToFarmer).Should().BeTrue();
        replay.FailedLegs.Should().BeNull();
        await _orders.Received(1).TryAdvanceStatusAsync(OrderId, "accepted", "dispute_resolved");
    }

    [Fact]
    public async Task EscrowWithoutAnOrder_SettlesNowOnTheDeliveryAlone()
    {
        // A legacy /escrow/initiate escrow names only its delivery.
        var mirror = new DisputeVerdictMirror(_orders, _deliveries, _txAudit);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(
            new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", "inTransit" } });
        DeliveryClosesFor("farmer_wins");

        var result = await mirror.ApplyDispositionAsync(
            Escrow(withOrder: false), "farmer_wins", DisputeDisposition.SettleNow, ResolvedAt);

        result.FailedLegs.Should().BeNull();
        result.Order.Should().Be(VerdictLegOutcome.None);
        result.Delivery.Should().Be(VerdictLegOutcome.Resolved);
    }

    // ──────────────────────────────────────────────
    // FUP-64: settle_as_is, the exit of a verdict no goods disposition fits
    // ──────────────────────────────────────────────

    [Theory]
    // buyer_wins: the refund returns the money to the buyer who paid it, whatever the legs show.
    [InlineData("buyer_wins", "in_transit", "disputed", "farmer_wins", null, null, true)]
    // A refund leaves a settled order with its contact open, so the exit flags it first (FUP-64 review).
    [InlineData("buyer_wins", "settled", "delivered", null, null, null, true)]
    // farmer_wins: only for goods that left the farm, did not come back, and an order that shows no payout.
    [InlineData("farmer_wins", "cancelled", "delivered", null, null, null, false)]
    [InlineData("farmer_wins", "accepted", "disputed", "buyer_wins", null, null, true)]
    [InlineData("farmer_wins", "cancelled", "cancelled", null, null, "goods_not_handed_over", false)]
    [InlineData("farmer_wins", "disbursing", "delivered", null, null, "order_paid_out", false)]
    [InlineData("farmer_wins", "dispute_resolved", "cancelled", "buyer_wins", "dispute_buyer_wins", "goods_returned", false)]
    public async Task PlanDisposition_NoFit_SettleAsIsFitsOnlyWhereItIsSafe(
        string resolution, string orderStatus, string deliveryStatus, string? deliveryVerdict, string? cancelReason,
        string? refusal, bool orderOpen)
    {
        OrderIs(orderStatus, 3);
        var delivery = new Dictionary<string, object> { { "deliveryId", DeliveryId }, { "status", deliveryStatus }, { "orderId", OrderId } };
        if (deliveryVerdict != null) delivery["disputeResolution"] = deliveryVerdict;
        if (cancelReason != null) delivery["cancelReason"] = cancelReason;
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(delivery);

        var plan = await _mirror.PlanDispositionAsync(Escrow(), resolution);

        plan.Case.Should().Be(DispositionCase.NoFit);
        plan.SettleAsIsRefusal.Should().Be(refusal);
        plan.OrderOpen.Should().Be(orderOpen);
        plan.Fits(DisputeDisposition.SettleAsIs).Should().Be(refusal == null);
        plan.SettlesAsIs.Should().Be(refusal == null && !orderOpen);
        await NoLegWritten();
    }

    private DisputeVerdictMirror MirrorWithEscrows(IEscrowRepository escrows) => new(_orders, _deliveries, _txAudit, escrowRepo: escrows);

    [Theory]
    [InlineData("completed", null, "order_paid_out")]
    [InlineData("released", null, "order_paid_out")]
    [InlineData("refunded", null, null)]
    [InlineData("dispute_resolved", null, null)]
    // A release decided on a ledger escrow (its crop leg released) is a payout too.
    [InlineData("escrowed", "released", "order_paid_out")]
    public async Task PlanDisposition_NoFit_FarmerWins_AnotherEscrowOfTheOrderThatPaidOut_RefusesSettleAsIs(
        string otherStatus, string? otherCropLeg, string? refusal)
    {
        // FUP-64 review: the order shows no payout, but its own escrow (another payment for the same trade) paid out.
        OrderIs("cancelled", 3);
        DeliveryIs("delivered");
        var escrows = Substitute.For<IEscrowRepository>();
        var other = new Dictionary<string, object> { { "escrowId", "escrow-order-order-1" }, { "status", otherStatus } };
        if (otherCropLeg != null)
        {
            other["settlementModel"] = "ledger";
            other["legStatus"] = new Dictionary<string, object> { { "crop", otherCropLeg }, { "transport", "held" }, { "commission", "held" } };
        }

        escrows.GetByIdAsync("escrow-order-order-1", true).Returns(other);
        // The order index names this escrow too: it never counts against itself.
        escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(new List<Dictionary<string, object>>
        {
            new() { { "escrowId", "escrow-1" } }
        });
        escrows.GetByIdAsync("escrow-1", true).Returns(new Dictionary<string, object> { { "escrowId", "escrow-1" }, { "status", "released" } });

        var plan = await MirrorWithEscrows(escrows).PlanDispositionAsync(Escrow(), "farmer_wins");

        plan.Case.Should().Be(DispositionCase.NoFit);
        plan.SettleAsIsRefusal.Should().Be(refusal);
    }

    [Fact]
    public async Task PlanDisposition_NoFit_FarmerWins_TheDeliveryEscrowPaidOut_RefusesSettleAsIs()
    {
        OrderIs("cancelled", 3);
        DeliveryIs("delivered");
        var escrows = Substitute.For<IEscrowRepository>();
        escrows.GetByDeliveryIdAsync(DeliveryId).Returns(new Dictionary<string, object> { { "escrowId", "escrow-legacy" } });
        escrows.GetByIdAsync("escrow-legacy", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", "escrow-legacy" }, { "status", "completed" }
        });

        var plan = await MirrorWithEscrows(escrows).PlanDispositionAsync(Escrow(), "farmer_wins");

        plan.SettleAsIsRefusal.Should().Be("order_paid_out");
    }

    [Fact]
    public async Task PlanDisposition_NoFit_OtherEscrowsUnreadable_IsUnknown()
    {
        OrderIs("cancelled", 3);
        DeliveryIs("delivered");
        var escrows = Substitute.For<IEscrowRepository>();
        escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).ThrowsAsync(new InvalidOperationException("escrows down"));

        var plan = await MirrorWithEscrows(escrows).PlanDispositionAsync(Escrow(), "farmer_wins");
        var buyer = await MirrorWithEscrows(escrows).PlanDispositionAsync(Escrow(), "buyer_wins");

        plan.Case.Should().Be(DispositionCase.Unknown);
        plan.Legs.Should().Be("escrows:read:[InvalidOperationException]");
        // A buyer_wins refund never reads them: it pays nobody but the buyer.
        buyer.Case.Should().NotBe(DispositionCase.Unknown);
    }

    [Theory]
    // Another escrow's verdict may have closed the order before any pickup: not a pickup by itself.
    [InlineData(null, "goods_not_handed_over")]
    [InlineData("wait_for_delivery", "goods_not_handed_over")]
    // This escrow's own settle_as_is closed it, after the pickup was shown at the time it was recorded.
    [InlineData("settle_as_is", null)]
    public async Task PlanDisposition_NoFit_FarmerWins_AnOrderAtDisputeResolvedShowsAPickupOnlyAfterItsOwnSettleAsIs(
        string? recorded, string? refusal)
    {
        // FUP-64 review: the delivery row is gone, so only the order could show a pickup.
        OrderIs("dispute_resolved", 9);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns((Dictionary<string, object>?)null);
        var escrow = Escrow();
        if (recorded != null)
        {
            escrow["disputeDisposition"] = recorded;
        }

        var plan = await _mirror.PlanDispositionAsync(escrow, "farmer_wins");

        plan.Case.Should().Be(DispositionCase.NoFit);
        plan.SettleAsIsRefusal.Should().Be(refusal);
    }

    [Fact]
    public async Task PlanDisposition_NoFit_FarmerWins_ADeliveryCancelledAfterPickup_ShowsThePickup()
    {
        // An admin cancelled the delivery on the road (FUP-19): its pickedUpAt stays.
        OrderIs("cancelled", 3);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", "cancelled" }, { "cancelReason", "admin_cancelled" },
            { "pickedUpAt", "2026-10-08T09:00:00Z" }
        });

        var plan = await _mirror.PlanDispositionAsync(Escrow(), "farmer_wins");

        plan.SettleAsIsRefusal.Should().BeNull();
        plan.SettlesAsIs.Should().BeTrue();
    }

    [Fact]
    public async Task SettleAsIs_BuyerWins_FlagsASettledOrder_SoItsContactCloses_ThenTheSettlementMayGoAhead()
    {
        // FUP-64 review: the refund leaves the order at settled, whose contact is open; the removed override flagged it.
        _orders.GetByIdAsync(OrderId, true).Returns(Order("settled", 6));
        DeliveryIs("delivered");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 6)
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("settled", 7)));

        var result = await _mirror.ApplyDispositionAsync(
            Escrow(), "buyer_wins", DisputeDisposition.SettleAsIs, ResolvedAt, actor: "admin-7", adminReason: "Paid twice; refund this one.");

        result.FailedLegs.Should().BeNull();
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(d =>
            !d.ContainsKey("status") && d["disputeOverrideBy"].ToString() == "admin-7"
            && d["disputeOverrideReason"].ToString() == "Paid twice; refund this one."
            && d["disputeOverrideEscrowId"].ToString() == "escrow-1" && d.ContainsKey("disputeOverrideAt")), 6);
        await _orders.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

        var flagged = Order("settled", 7);
        flagged["disputeOverrideAt"] = "2026-10-09T11:00:00Z";
        _orders.GetByIdAsync(OrderId, true).Returns(flagged);
        var after = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        after.OrderOpen.Should().BeFalse();
        after.SettlesAsIs.Should().BeTrue();
    }

    [Fact]
    public async Task SettleAsIs_AFlagThatDoesNotLand_IsAFailedLeg_TheReplayFinishesIt()
    {
        _orders.GetByIdAsync(OrderId, true).Returns(Order("disbursing", 6));
        DeliveryIs("delivered");
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(500, "DB_ERROR", "down"));

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);

        result.FailedLegs.Should().Be("order:flag_failed");
    }

    [Fact]
    public async Task PlanDisposition_NoFit_FarmerWinsWithAMissingOrder_ReadsThePickupFromTheDeliveryOrTheEscrow()
    {
        _orders.GetByIdAsync(OrderId, true).Returns((Dictionary<string, object>?)null);
        DeliveryIs("inTransit");
        var picked = await _mirror.PlanDispositionAsync(Escrow(), "farmer_wins");

        DeliveryIs("pending");
        var atTheFarm = await _mirror.PlanDispositionAsync(Escrow(), "farmer_wins");
        var escrow = Escrow();
        escrow["deliveryConfirmedAt"] = "2026-10-08T10:00:00Z";
        var confirmed = await _mirror.PlanDispositionAsync(escrow, "farmer_wins");

        picked.SettlesAsIs.Should().BeTrue();
        atTheFarm.SettleAsIsRefusal.Should().Be("goods_not_handed_over");
        confirmed.SettlesAsIs.Should().BeTrue();
    }

    [Theory]
    [InlineData("buyer_wins", "in_transit", "inTransit", null)]
    [InlineData("farmer_wins", "pickup_confirmed", "pickedUp", null)]
    [InlineData("buyer_wins", "disputed", "disputed", null)]
    public async Task SettleAsIs_DoesNotFit_WhereAnotherChoiceDoesOrTheLegsFollow(
        string resolution, string orderStatus, string deliveryStatus, string? deliveryVerdict)
    {
        OrderIs(orderStatus, 4);
        DeliveryIs(deliveryStatus, deliveryVerdict);

        var plan = await _mirror.PlanDispositionAsync(Escrow(), resolution);
        var result = await _mirror.ApplyDispositionAsync(Escrow(), resolution, DisputeDisposition.SettleAsIs, ResolvedAt);

        plan.Fits(DisputeDisposition.SettleAsIs).Should().BeFalse();
        plan.SettlesAsIs.Should().BeFalse();
        if (plan.Case == DispositionCase.NotNeeded)
        {
            result.DispositionNotNeeded.Should().BeTrue();
        }
        else
        {
            result.FailedLegs.Should().Be("order:disposition:settle_as_is");
        }

        await NoLegWritten();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SettleAsIs_ClosesALiveOrderStraight_AndLeavesTheDelivery()
    {
        // farmer_wins, but the delivery carries buyer_wins from elsewhere: no goods choice fits.
        OrderIs("accepted", 4);
        DeliveryIs("disputed", "buyer_wins");
        OrderAdvances("accepted");

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);

        result.FailedLegs.Should().BeNull();
        result.Order.Should().Be(VerdictLegOutcome.Resolved);
        result.Delivery.Should().Be(VerdictLegOutcome.None);
        await _orders.Received(1).TryAdvanceStatusAsync(OrderId, "accepted", "dispute_resolved");
        await _txAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["entityType"].ToString() == "order" && e["action"].ToString() == "dispute_resolved"
            && e["fromState"].ToString() == "accepted" && e["actor"].ToString() == "system"
            && e["metadata"].ToString() == "resolution=farmer_wins;disposition=settle_as_is"));
        await _deliveries.DidNotReceive().TryCloseForVerdictAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _deliveries.DidNotReceive().TryRecordDisputeResolutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SettleAsIs_TakesADeliveredOrderThroughTheVerdictHops_ThenTheSettlementMayGoAhead()
    {
        // buyer_wins on a delivered order whose delivery row is gone.
        _orders.GetByIdAsync(OrderId, true).Returns(Order("delivered", 5));
        _deliveries.GetByIdAsync(DeliveryId, true).Returns((Dictionary<string, object>?)null);
        OrderWritesSucceed();

        var before = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");
        var result = await _mirror.ApplyDispositionAsync(Escrow(), "buyer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);
        _orders.GetByIdAsync(OrderId, true).Returns(Order("dispute_resolved", 7));
        var after = await _mirror.PlanDispositionAsync(Escrow(), "buyer_wins");

        before.OrderOpen.Should().BeTrue();
        before.SettlesAsIs.Should().BeFalse();
        result.FailedLegs.Should().BeNull();
        _orderWrites.Should().Equal("disputed", "dispute_resolved");
        after.Case.Should().Be(DispositionCase.NoFit);
        after.SettlesAsIs.Should().BeTrue();
    }

    [Fact]
    public async Task SettleAsIs_LeavesAClosedOrder_AndAReplayWritesNothing()
    {
        OrderIs("cancelled", 6);
        DeliveryIs("delivered");

        var first = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);
        var again = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);

        first.FailedLegs.Should().BeNull();
        again.FailedLegs.Should().BeNull();
        await NoLegWritten();
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
        await _txAudit.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task SettleAsIs_AnOrderThatMovedMeanwhile_IsAConflict_ForTheReplay()
    {
        OrderIs("accepted", 4);
        DeliveryIs("disputed", "buyer_wins");
        _orders.TryAdvanceStatusAsync(OrderId, "accepted", "dispute_resolved").Returns((Dictionary<string, object>?)null);
        _orders.GetByIdAsync(OrderId, true).Returns(Order("accepted", 4), Order("cancelled", 5));

        var result = await _mirror.ApplyDispositionAsync(Escrow(), "farmer_wins", DisputeDisposition.SettleAsIs, ResolvedAt);

        result.Order.Should().Be(VerdictLegOutcome.Conflict);
        result.FailedLegs.Should().Be("order:mismatch:cancelled");
    }

    [Fact]
    public void DispositionNames_AndTheirVerdicts()
    {
        DisputeDisposition.VerdictOf("return_to_farmer").Should().Be("buyer_wins");
        DisputeDisposition.VerdictOf("delivered_anyway").Should().Be("buyer_wins");
        DisputeDisposition.VerdictOf("settle_now").Should().Be("farmer_wins");
        DisputeDisposition.VerdictOf("wait_for_delivery").Should().Be("farmer_wins");
        DisputeDisposition.VerdictOf("override").Should().BeNull();
        // FUP-64: settle_as_is goes with both verdicts, so it names neither.
        DisputeDisposition.VerdictOf("settle_as_is").Should().BeNull();
        DisputeDisposition.AuditAction("settle_as_is").Should().Be("dispute_resolved_settled_as_is");
        DisputeDisposition.DeliveryTarget("settle_as_is").Should().BeNull();
        DisputeVerdictMirror.VerdictAuditTimestamp(ResolvedAt, "dispute_resolved_settled_early")
            .Should().Be(DateTime.Parse(ResolvedAt).ToUniversalTime().AddTicks(4).ToString("o"));
        DisputeVerdictMirror.VerdictAuditTimestamp(ResolvedAt, "dispute_resolved_settled_as_is")
            .Should().Be(DateTime.Parse(ResolvedAt).ToUniversalTime().AddTicks(4).ToString("o"));
    }
}

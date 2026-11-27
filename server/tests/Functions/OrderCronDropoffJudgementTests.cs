using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-36 (spec 4.5 T8 and T9, 4.8, E3 and E4): the order cron's drop-off judgement through
/// <c>commitment_deadlines</c>. T8 (no drop-off check-in by the deadline) and each T9 outcome: the
/// auto-confirm for a silent buyer and every fallback that turns it into a dispute, the money that can
/// be neither confirmed nor frozen, a party who acted first, a lost order write, the flag turned off, and
/// FUP-204 (the buyer's silence after the trucker's check-in never makes an overdue delivery).
/// </summary>
public class OrderCronDropoffJudgementTests
{
    private const string OrderId = "ord-dj";
    private const string TripId = "trip-dj";
    private const string DeliveryId = "del-dj";
    private const string TruckerId = "trucker-1";
    private const string EscrowId = "escrow-order-ord-dj";
    private const string EscrowCreatedAt = "2026-11-01T00:00:00.0000000Z";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly DateTime _now = DateTime.UtcNow;

    private Dictionary<string, object> _escrow;

    public OrderCronDropoffJudgementTests()
    {
        _escrow = EscrowedRow();
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new List<Dictionary<string, object>>());
        _orders.TrySetDeadlineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OrderDeadline?>()).Returns(true);
        _orders.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object> { { "orderId", ci.Arg<string>() } }));
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(true);
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("inTransit"));
        _deliveries.TryAutoConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(true);
        _deliveries.TryMarkDropoffDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => _escrow);
        _escrows.QueryByOrderIdAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(_ => new List<Dictionary<string, object>> { _escrow });
        _escrowAudit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);
        _users.GetUserByIdAsync(TruckerId).Returns(Trucker(null));
    }

    // ===== fixture =====

    private OrderCronFunction Sut(bool flag = true)
        => new(_orders, _txAudit, inboxRepo: null,
            escrowService: new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                orderRepo: _orders, deliveryRepo: _deliveries),
            cropRepo: _crops, tripRepo: _trips, deliveryRepo: _deliveries)
        {
            CommitmentsEnabled = true,
            DropoffClassificationEnabled = flag,
            EscrowRepo = _escrows,
            UserRepo = _users,
            PayoutDelay = new PayoutDelayPolicy(null, null, null, settings: PayoutDelaySettings.Default)
        };

    private Task<APIGatewayProxyResponse> RunAsync(bool flag = true)
        => Sut(flag).FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    /// <summary>An order on the road, its T8 <paramref name="t8"/> from now.</summary>
    private Dictionary<string, object> Order(TimeSpan t8, Dictionary<string, object>? checkIn = null, string status = "in_transit")
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "status", status }, { "version", "9" },
            { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" }, { "truckerId", TruckerId }, { "tripId", TripId },
            { "createdAt", Iso(_now.AddDays(-6)) }, { "activeDeliveryId", DeliveryId },
            { OrderCommitment.FulfillmentField, OrderCommitment.Fulfillment.Trucker },
            { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending },
            { DropoffJudgement.DeadlineField, Iso(_now + t8) }
        };
        if (checkIn != null)
        {
            order[DropoffJudgement.CheckInField] = checkIn;
        }

        return order;
    }

    /// <summary>A drop-off check-in received <paramref name="hoursAgo"/> hours ago.</summary>
    private Dictionary<string, object> CheckIn(double hoursAgo = 49, bool measured = true, bool late = false) => new()
    {
        { PickupJudgement.EntryReceivedAt, Iso(_now.AddHours(-hoursAgo)) },
        { PickupJudgement.EntryDistanceChecked, measured },
        { PickupJudgement.EntryLate, late },
        { PickupJudgement.EntryBy, TruckerId }
    };

    /// <summary>T9 due: the trucker checked in 49 hours ago, T8 still a day off.</summary>
    private Dictionary<string, object> SilentBuyerOrder(bool measured = true)
        => Order(TimeSpan.FromHours(20), CheckIn(49, measured));

    private void Serve(Dictionary<string, object> order)
    {
        _orders.QueryDueDeadlinesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { order }, (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync(OrderId, true).Returns(order);
    }

    private static Dictionary<string, object> Delivery(string status, bool photo = true)
    {
        var delivery = new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "tripId", TripId }, { "truckerId", TruckerId },
            { "buyerId", "buyer-1" }, { "status", status }, { "version", "3" }
        };
        if (photo)
        {
            delivery["arrivalPhotoRef"] = $"uploads/photos/{DeliveryId}/arrival-1763000000000.webp";
        }

        return delivery;
    }

    private static Dictionary<string, object> EscrowedRow() => new()
    {
        { "escrowId", EscrowId }, { "orderId", OrderId }, { "createdAt", EscrowCreatedAt },
        { "status", "escrowed" }, { "buyerId", "buyer-1" }, { "farmerId", "farmer-1" }, { "amount", "1000" },
        { "deliveryId", DeliveryId }
    };

    private static UserDto Trucker(ReputationSnapshot? reputation) => new()
    {
        UserId = TruckerId,
        Role = "trucker",
        Reputation = reputation,
        PhoneNumber = "+251900100002",
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private Task AssertNoOrderWrite()
        => _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());

    private Task AssertNotFrozen()
        => _escrows.DidNotReceive().UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", Arg.Any<string?>(),
            Arg.Any<Dictionary<string, object>?>());

    private Task AssertNotConfirmed()
        => _deliveries.DidNotReceive().TryAutoConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>());

    private async Task AssertDisputed(string reason, string deliveryFrom = "inTransit")
    {
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null && (string)f["disputedBy"] == DropoffJudgement.SystemActor));
        await _deliveries.Received(1).TryMarkDropoffDisputedAsync(DeliveryId, TripId, deliveryFrom, reason, Arg.Any<string>());
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "disputed"
            && (string)f[DropoffJudgement.DisputeReasonField] == reason
            && (string)f[DropoffJudgement.Field] == reason
            && f.ContainsKey(DropoffJudgement.JudgedAtField)), 9);
    }

    // ===== T8: no drop-off check-in =====

    [Fact]
    public async Task T8_NoCheckIn_FreezesTheMoney_DisputesTheDeliveryAndTheOrder_AndAlarms()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"1\"", actual.Body);
        await AssertDisputed(DropoffJudgement.OverdueReason);
        await AssertNotConfirmed();
        Assert.Contains($"{OrderCronFunction.DeliveryOverdueToken} orderId=", Logs);
        Assert.Contains("outcome=disputed", Logs);
        await _txAudit.Received().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == DeliveryId && (string)e["action"] == "dispute" && (string)e["toState"] == "disputed"));
    }

    [Fact]
    public async Task T8_InsideTheSettleMargin_WaitsForTheNextRun()
    {
        Serve(Order(TimeSpan.FromSeconds(-20)));

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"0\"", actual.Body);
        await AssertNotFrozen();
        await AssertNoOrderWrite();
    }

    [Fact]
    public async Task T8_ALateCheckIn_DoesNotStopIt()
    {
        Serve(Order(TimeSpan.FromMinutes(-30), CheckIn(hoursAgo: 0.1, late: true)));

        await RunAsync();

        await AssertDisputed(DropoffJudgement.OverdueReason);
    }

    [Fact]
    public async Task T8_MoneyThatCannotBeFrozen_MovesNothing_QueuesTheOrder_AndAlarms()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));
        _escrow["refundReason"] = "order_cancelled";

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"1\"", actual.Body);
        await AssertNotFrozen();
        await _deliveries.DidNotReceive().TryMarkDropoffDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
        await AssertNoOrderWrite();
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Queued),
            Arg.Is<IReadOnlyDictionary<string, string>>(e =>
                e["status"] == "in_transit" && e[DropoffJudgement.Field] == DropoffJudgement.Outcome.Pending));
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, DropoffJudgement.OverdueReason, EscrowId, Arg.Any<string>(), "in_transit");
        Assert.Contains("outcome=queued", Logs);
    }

    [Fact]
    public async Task T8_TheQueueEntryFailing_StampsNothing_AndTriesAgainLater()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));
        _escrow["refundReason"] = "order_cancelled";
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new AmazonDynamoDBExceptionStub());

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    [Fact]
    public async Task T8_AnOrderQueuedForAnotherReason_IsStamped_AndTheMissingEntryIsAlarmed()
    {
        var order = Order(TimeSpan.FromMinutes(-30));
        Serve(order);
        _escrow["refundReason"] = "order_cancelled";
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        order[AdminQueueFields.Marker] = AdminQueueFields.MarkerOpen;
        order[AdminQueueFields.Reason] = AdminQueueFields.ReasonHoldCap;

        await RunAsync();

        Assert.Contains($"{OrderCronFunction.DropoffQueueNotWrittenToken} orderId=", Logs);
        Assert.Contains($"queued={MoneyAlarmLog.LogId(AdminQueueFields.ReasonHoldCap)}", Logs);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Queued),
            Arg.Any<IReadOnlyDictionary<string, string>>());
        Assert.Contains("outcome=queued", Logs);
    }

    // ===== FUP-204: the buyer's pin the only point, the trucker came out of range =====

    [Fact]
    public async Task FUP204_AnOutOfRangeAttemptByT8_IsTheSystemsDispute_WithItsOwnReason_NeverDeliveryOverdue()
    {
        Serve(OutOfRangeOrder());

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"1\"", actual.Body);
        // Frozen and disputed as the overdue path, but with the reason R12 does not read: an admin decides the verdict.
        await AssertDisputed(DropoffJudgement.OutOfRangeReason);
        await _orders.DidNotReceive().UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            f.ContainsKey(DropoffJudgement.DisputeReasonField) && (string)f[DropoffJudgement.DisputeReasonField] == DropoffJudgement.OverdueReason),
            Arg.Any<int>());
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
        Assert.Contains("outcome=disputed", Logs);
        Assert.Contains($"reason={DropoffJudgement.OutOfRangeReason}", Logs);
    }

    [Fact]
    public async Task FUP204_AnOutOfRangeAttempt_WithMoneyThatCannotBeFrozen_GoesToTheAdminQueue()
    {
        Serve(OutOfRangeOrder());
        _escrow["refundReason"] = "order_cancelled";

        await RunAsync();

        await AssertNotFrozen();
        await AssertNoOrderWrite();
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, DropoffJudgement.OutOfRangeReason, EscrowId, Arg.Any<string>(), "in_transit");
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Queued),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    [Fact]
    public async Task ASystemOutOfRangeDisputeWhoseOrderWriteWasLost_IsFinished()
    {
        Serve(OutOfRangeOrder());
        var disputed = Delivery("disputed");
        disputed["disputedBy"] = DropoffJudgement.SystemActor;
        disputed["disputeReason"] = DropoffJudgement.OutOfRangeReason;
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(disputed);

        await RunAsync();

        await AssertNotFrozen();
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "disputed" && (string)f[DropoffJudgement.DisputeReasonField] == DropoffJudgement.OutOfRangeReason), 9);
    }

    [Fact]
    public async Task TheOrdersOwnQueueEntryAlreadyStanding_IsNoAlarm()
    {
        // An earlier run wrote the entry, then its stamp failed: the entry is the order's own.
        var order = Order(TimeSpan.FromMinutes(-30));
        Serve(order);
        _escrow["refundReason"] = "order_cancelled";
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        order[AdminQueueFields.Marker] = AdminQueueFields.MarkerOpen;
        order[AdminQueueFields.Reason] = DropoffJudgement.OverdueReason;

        await RunAsync();

        Assert.DoesNotContain(OrderCronFunction.DropoffQueueNotWrittenToken, Logs);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Queued),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    /// <summary>T8 passed; the trucker came 5 hours before it, out of range of every point (FUP-204).</summary>
    private Dictionary<string, object> OutOfRangeOrder()
    {
        var order = Order(TimeSpan.FromMinutes(-30));
        var attempt = CheckIn(hoursAgo: 5, measured: false);
        attempt[PickupJudgement.EntryOutOfRange] = true;
        order[DropoffJudgement.AttemptField] = attempt;
        return order;
    }

    [Fact]
    public async Task T8_AnUnreadableEscrow_WritesNothing_AndTriesAgainLater()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));
        _escrows.GetByIdAsync(EscrowId, true).ThrowsAsync(new AmazonDynamoDBExceptionStub());

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        await AssertNoOrderWrite();
        await _deliveries.DidNotReceive().TryMarkDropoffDisputedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    // ===== T9: the confirm for a silent buyer =====

    [Fact]
    public async Task T9_CompleteEvidence_ConfirmsTheDelivery_TheOrderFollows_AndTheEscrowIsStampedAsABuyersConfirm()
    {
        Serve(SilentBuyerOrder());

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"1\"", actual.Body);
        await _deliveries.Received(1).TryAutoConfirmAsync(DeliveryId, TripId, "inTransit", 3, Arg.Any<string>());
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "delivered"
            && (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.AutoConfirmed
            && !f.ContainsKey(DropoffJudgement.DisputeReasonField)), 9);
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, EscrowCreatedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null
                && f.ContainsKey("deliveryConfirmedAt")
                && f.ContainsKey("disputeWindowEndsAt")
                && f.ContainsKey(PayoutDelayPolicy.ReleaseNotBeforeField)));
        await AssertNotFrozen();
        await _txAudit.Received().AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            (string)e["entityId"] == DeliveryId && (string)e["action"] == "delivery_auto_confirmed" && (string)e["actor"] == "system"));
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    public static IEnumerable<object[]> Fallbacks() => new[]
    {
        new object[] { "unmeasured" },
        new object[] { "no_photo" },
        new object[] { "trucker_low" },
        new object[] { "trucker_restricted" },
        new object[] { "trucker_unknown" }
    };

    [Theory]
    [MemberData(nameof(Fallbacks))]
    public async Task T9_EachFallback_IsASilentBuyerDispute_NeverAnOverdueDelivery(string fallback)
    {
        Serve(SilentBuyerOrder(measured: fallback != "unmeasured"));
        if (fallback == "no_photo")
        {
            _deliveries.GetByIdAsync(DeliveryId, true).Returns(Delivery("inTransit", photo: false));
        }

        _users.GetUserByIdAsync(TruckerId).Returns(fallback switch
        {
            "trucker_low" => Trucker(new ReputationSnapshot { Tier = ReputationTiers.Low }),
            "trucker_restricted" => Trucker(new ReputationSnapshot
            {
                Tier = ReputationTiers.Warning,
                HasRestriction = true,
                RestrictionCause = ReputationCauses.Points,
                RestrictionSince = _now.AddDays(-1),
                RestrictionUntil = _now.AddDays(3)
            }),
            "trucker_unknown" => null,
            _ => Trucker(new ReputationSnapshot { Tier = ReputationTiers.Good })
        });

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"1\"", actual.Body);
        await AssertNotConfirmed();
        await AssertDisputed(DropoffJudgement.SilentBuyerReason);
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    [Fact]
    public async Task T9_MoneyNotPlainlyHeld_IsNeitherConfirmedNorFrozen_TheAdminDecides()
    {
        Serve(SilentBuyerOrder());
        _escrow["refundReason"] = "admin_refund";

        await RunAsync();

        await AssertNotConfirmed();
        await AssertNotFrozen();
        await AssertNoOrderWrite();
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, DropoffJudgement.SilentBuyerReason, EscrowId, Arg.Any<string>(), "in_transit");
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    [Fact]
    public async Task T9_AnEscrowTheBuyerAlreadyDisputed_IsNeverConfirmed_TheOrderFollowsTheDispute()
    {
        Serve(SilentBuyerOrder());
        _escrow["status"] = "disputed";

        await RunAsync();

        await AssertNotConfirmed();
        await _deliveries.Received(1).TryMarkDropoffDisputedAsync(DeliveryId, TripId, "inTransit", DropoffJudgement.SilentBuyerReason, Arg.Any<string>());
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f => (string)f["status"] == "disputed"), 9);
    }

    [Fact]
    public async Task T9_TheTruckersRowUnreadable_WritesNothing_AndTriesAgainLater()
    {
        Serve(SilentBuyerOrder());
        _users.GetUserByIdAsync(TruckerId).ThrowsAsync(new AmazonDynamoDBExceptionStub());

        var actual = await RunAsync();

        Assert.Contains("\"retried\":\"1\"", actual.Body);
        await AssertNotConfirmed();
        await AssertNotFrozen();
        await AssertNoOrderWrite();
    }

    [Fact]
    public async Task T9_TheBuyersConfirmLandingFirst_IsAConflict_NothingElseWritten()
    {
        Serve(SilentBuyerOrder());
        _deliveries.TryAutoConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(false);

        var actual = await RunAsync();

        Assert.Contains("\"conflicts\":\"1\"", actual.Body);
        await AssertNoOrderWrite();
        await _escrows.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task T9_NotDueYet_NothingHappens()
    {
        Serve(Order(TimeSpan.FromHours(20), CheckIn(hoursAgo: 47)));

        var actual = await RunAsync();

        Assert.Contains("\"dropoffJudged\":\"0\"", actual.Body);
        await AssertNotConfirmed();
        await AssertNotFrozen();
    }

    // ===== the silent buyer's reminder (FUP-338) =====

    [Fact]
    public async Task FUP338_TheReminder_IsStampedOnce_TheLeadBeforeT9_NothingIsJudged()
    {
        // Checked in 43 hours ago: T9 in 5 hours, the reminder due an hour ago.
        Serve(Order(TimeSpan.FromHours(20), CheckIn(hoursAgo: 43)));

        var actual = await RunAsync();

        Assert.Contains("\"dropoffReminders\":\"1\"", actual.Body);
        Assert.Contains("\"dropoffJudged\":\"0\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.Count == 1 && f.ContainsKey(DropoffJudgement.ReminderAtField)),
            Arg.Is<IReadOnlyDictionary<string, string>>(e =>
                e["status"] == "in_transit" && e[DropoffJudgement.Field] == DropoffJudgement.Outcome.Pending));
        await AssertNotConfirmed();
        await AssertNotFrozen();
        await AssertNoOrderWrite();
    }

    [Theory]
    [InlineData(41, false, true)] // the reminder not due yet
    [InlineData(43, true, true)] // already stamped
    [InlineData(0, false, false)] // no check-in: T8 is the trucker's, nobody is reminded
    public async Task FUP338_NoReminder_BeforeItIsDue_OnceStamped_OrWithoutACheckIn(double hoursAgo, bool stamped, bool checkedIn)
    {
        var order = Order(TimeSpan.FromHours(20), checkedIn ? CheckIn(hoursAgo) : null);
        if (stamped)
        {
            order[DropoffJudgement.ReminderAtField] = Iso(_now.AddHours(-1));
        }

        Serve(order);

        var actual = await RunAsync();

        Assert.Contains("\"dropoffReminders\":\"0\"", actual.Body);
        await _orders.DidNotReceive().TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    [Fact]
    public async Task FUP338_WithTheFlagOff_TheReminderIsNeverSent_TheStampIsClearedWhenItFallsDue()
    {
        Serve(Order(TimeSpan.FromHours(20), CheckIn(hoursAgo: 43)));

        var actual = await RunAsync(flag: false);

        Assert.Contains("\"dropoffReminders\":\"0\"", actual.Body);
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f =>
                f.Count == 1 && f.ContainsKey(DropoffJudgement.Field) && f[DropoffJudgement.Field] == null),
            Arg.Is<IReadOnlyDictionary<string, string>>(e => e[DropoffJudgement.Field] == DropoffJudgement.Outcome.Pending));
        await AssertNotConfirmed();
        await AssertNotFrozen();
    }

    // ===== a party acted first =====

    [Fact]
    public async Task TheBuyerConfirmedFirst_TheLaggingOrderFollows_Closed()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));
        var delivered = Delivery("delivered");
        delivered["confirmedAt"] = Iso(_now.AddHours(-2));
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(delivered);

        await RunAsync();

        await AssertNotFrozen();
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "delivered" && (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Closed), 9);
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    [Fact]
    public async Task APartysDisputeFirst_OnlyEndsTheJudgement()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));
        var disputed = Delivery("disputed");
        disputed["disputedBy"] = "buyer-1";
        disputed["disputeReason"] = "Half the sacks are missing.";
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(disputed);

        await RunAsync();

        await AssertNotFrozen();
        await AssertNoOrderWrite();
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.Closed),
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    [Fact]
    public async Task ASystemDisputeWhoseOrderWriteWasLost_IsFinished_WithoutASecondFreeze()
    {
        Serve(Order(TimeSpan.FromMinutes(-90)));
        var disputed = Delivery("disputed");
        disputed["disputedBy"] = DropoffJudgement.SystemActor;
        disputed["disputeReason"] = DropoffJudgement.OverdueReason;
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(disputed);

        await RunAsync();

        await AssertNotFrozen();
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f["status"] == "disputed" && (string)f[DropoffJudgement.DisputeReasonField] == DropoffJudgement.OverdueReason), 9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRepair_WalksTheOrdersOnTheRoad_OnlyWithTheFlagOn(bool flag)
    {
        _orders.QueryDeadlinePartitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

        await Sut(flag).FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"deadline_repair\"}" }, _context);

        await _orders.Received(1).QueryByStatusForCronAsync("accepted", Arg.Any<string?>());
        await _orders.Received(flag ? 1 : 0).QueryByStatusForCronAsync("pickup_confirmed", Arg.Any<string?>());
        await _orders.Received(flag ? 1 : 0).QueryByStatusForCronAsync("in_transit", Arg.Any<string?>());
    }

    [Fact]
    public async Task AnAutoConfirmWhoseOrderWriteWasLost_IsFollowed_AsAutoConfirmed()
    {
        Serve(SilentBuyerOrder());
        var delivered = Delivery("delivered");
        delivered["confirmedAt"] = Iso(_now.AddMinutes(-20));
        delivered[DropoffJudgement.AutoConfirmedAtField] = Iso(_now.AddMinutes(-20));
        _deliveries.GetByIdAsync(DeliveryId, true).Returns(delivered);

        await RunAsync();

        await AssertNotConfirmed();
        await _orders.Received(1).UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f[DropoffJudgement.Field] == DropoffJudgement.Outcome.AutoConfirmed), 9);
        await _escrows.Received(1).UpdateStatusAsync(EscrowId, EscrowCreatedAt, "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>?>(f => f != null && (string)f["deliveryConfirmedAt"] == Iso(_now.AddMinutes(-20))));
    }

    // ===== the flag =====

    [Fact]
    public async Task WithTheFlagOff_ADueOrderOnlyLosesItsStamp()
    {
        Serve(Order(TimeSpan.FromMinutes(-30)));

        await RunAsync(flag: false);

        await AssertNotFrozen();
        await AssertNoOrderWrite();
        await _orders.Received(1).TryStampAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(f => f.ContainsKey(DropoffJudgement.Field) && f[DropoffJudgement.Field] == null),
            Arg.Is<IReadOnlyDictionary<string, string>>(e => e[DropoffJudgement.Field] == DropoffJudgement.Outcome.Pending));
    }

    [Fact]
    public async Task AnOrderPickedUpBeforeTheFlag_IsNotJudged()
    {
        var order = Order(TimeSpan.FromMinutes(-30));
        order.Remove(DropoffJudgement.Field);
        Serve(order);

        await RunAsync();

        await AssertNotFrozen();
        await AssertNoOrderWrite();
        await _deliveries.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    // ===== FUP-204 =====

    [Fact]
    public async Task FUP204_ABuyerWhoWithholdsTheScanAfterTheTruckersCheckIn_NeverMakesAnOverdueDelivery()
    {
        // T8 is long past, but the trucker checked in before it: the buyer's silence is judged at T9.
        Serve(Order(TimeSpan.FromHours(-30), CheckIn(hoursAgo: 49)));
        _users.GetUserByIdAsync(TruckerId).Returns(Trucker(new ReputationSnapshot { Tier = ReputationTiers.Low }));

        await RunAsync();

        await _orders.DidNotReceive().UpdateAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            f.ContainsKey(DropoffJudgement.DisputeReasonField) && (string)f[DropoffJudgement.DisputeReasonField] == DropoffJudgement.OverdueReason),
            Arg.Any<int>());
        await AssertDisputed(DropoffJudgement.SilentBuyerReason);
        Assert.DoesNotContain(OrderCronFunction.DeliveryOverdueToken, Logs);
    }

    /// <summary>A storage failure, as a read that throws.</summary>
    private sealed class AmazonDynamoDBExceptionStub() : Exception("throttled");
}

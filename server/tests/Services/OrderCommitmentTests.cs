using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-15: the sequential commitment rules as a table. The farmer is asked first; the trucker
/// only after the farmer accepted; a failed transport parks the order with the buyer.
/// </summary>
public class OrderCommitmentTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, object> Order(
        string status, string? farmer = null, string? trucker = null, bool trip = true,
        DateTime? farmerDeadline = null, DateTime? truckerDeadline = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "order-1" }, { "status", status },
            { "paymentVerifiedAt", Now.AddHours(-2).ToString("o") }
        };
        if (trip)
        {
            order["tripId"] = "trip-1";
            order["truckerId"] = "trucker-1";
        }

        if (farmer != null)
        {
            order[OrderCommitment.FarmerResponseField] = farmer;
        }

        if (trucker != null)
        {
            order[OrderCommitment.TruckerResponseField] = trucker;
        }

        if (farmerDeadline != null)
        {
            order[OrderCommitment.FarmerDeadlineAtField] = farmerDeadline.Value.ToString("o");
        }

        if (truckerDeadline != null)
        {
            order[OrderCommitment.TruckerDeadlineAtField] = truckerDeadline.Value.ToString("o");
        }

        return order;
    }

    private static CommitmentDecision Farmer(Dictionary<string, object> order, string command, DateTime? departure = null, string? reason = null)
        => OrderCommitment.Decide(order, CommitmentActor.Farmer, command, Now, departure, reason, freshSignIn: false);

    private static CommitmentDecision Trucker(Dictionary<string, object> order, string command, bool fresh = false, string? reason = null)
        => OrderCommitment.Decide(order, CommitmentActor.Trucker, command, Now, Now.AddDays(3), reason, fresh);

    // ===== deadlines =====

    [Fact]
    public void ResponseDeadline_Is24Hours_UnlessTheTripLeavesSooner()
    {
        OrderCommitment.ResponseDeadline(Now, null).Should().Be(Now.AddHours(24));
        OrderCommitment.ResponseDeadline(Now, Now.AddDays(3)).Should().Be(Now.AddHours(24));
        OrderCommitment.ResponseDeadline(Now, Now.AddHours(20)).Should().Be(Now.AddHours(8));
    }

    [Fact]
    public void ResponseDeadline_NeverLessThanOneHour()
    {
        OrderCommitment.ResponseDeadline(Now, Now.AddHours(6)).Should().Be(Now.AddHours(1));
        OrderCommitment.ResponseDeadline(Now, Now.AddHours(-5)).Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void FundingStamps_AskTheFarmerFirst_AndHoldTheTruckerBack()
    {
        var stamps = OrderCommitment.FundingStamps(Order("funded"), Now, Now.AddDays(2));

        stamps[OrderCommitment.FulfillmentField].Should().Be(OrderCommitment.Fulfillment.Trucker);
        stamps[OrderCommitment.FarmerResponseField].Should().Be(OrderCommitment.Response.Pending);
        stamps[OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.NotAsked);
        stamps[OrderCommitment.FarmerDeadlineAtField].Should().Be(OrderCommitment.Iso(Now.AddHours(24)));
        stamps[OrderCommitment.PickupDateField].Should().Be(OrderCommitment.Iso(Now.AddDays(2)));
    }

    [Fact]
    public void FundingStamps_SelfPickup_HasNoTrucker()
    {
        var stamps = OrderCommitment.FundingStamps(Order("funded", trip: false), Now, null);

        stamps[OrderCommitment.FulfillmentField].Should().Be(OrderCommitment.Fulfillment.SelfPickup);
        stamps[OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.None);
        stamps.Should().NotContainKey(OrderCommitment.PickupDateField);
    }

    [Fact]
    public void LegacyRows_DeriveTheirResponses()
    {
        OrderCommitment.FarmerResponseOf(Order("funded")).Should().Be(OrderCommitment.Response.Pending);
        OrderCommitment.TruckerResponseOf(Order("funded")).Should().Be(OrderCommitment.Response.NotAsked);
        OrderCommitment.TruckerResponseOf(Order("funded", trip: false)).Should().Be(OrderCommitment.Response.None);
        // Spec K2: an accepted trip order from before the feature counts as trucker-accepted.
        OrderCommitment.FarmerResponseOf(Order("accepted")).Should().Be(OrderCommitment.Response.Accepted);
        OrderCommitment.TruckerResponseOf(Order("accepted")).Should().Be(OrderCommitment.Response.Accepted);
        OrderCommitment.FarmerDeadlineOf(Order("funded")).Should().Be(Now.AddHours(22));
    }

    // ===== farmer =====

    [Fact]
    public void FarmerAccept_TripOrder_AsksTheTrucker_AndKeepsTheOrderFunded()
    {
        var decision = Farmer(Order("funded", "pending", "not_asked"), OrderCommitment.AcceptCommand, Now.AddDays(3));

        decision.IsError.Should().BeFalse();
        decision.NewStatus.Should().BeNull();
        decision.Fields.Should().NotContainKey("status");
        decision.Fields![OrderCommitment.FarmerResponseField].Should().Be(OrderCommitment.Response.Accepted);
        decision.Fields[OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.Pending);
        decision.Fields[OrderCommitment.TruckerDeadlineAtField].Should().Be(OrderCommitment.Iso(Now.AddHours(24)));
        decision.Fields.Should().ContainKey("respondedAt");
        decision.Effect.Should().Be(CommitmentEffect.None);
        decision.AuditAction.Should().Be(OrderCommitment.FarmerAuditAction);
    }

    [Fact]
    public void FarmerAccept_TripLeavingSoon_ShortensTheTruckerWindow()
    {
        var decision = Farmer(Order("funded", "pending", "not_asked"), OrderCommitment.AcceptCommand, Now.AddHours(20));

        decision.Fields![OrderCommitment.TruckerDeadlineAtField].Should().Be(OrderCommitment.Iso(Now.AddHours(8)));
    }

    [Fact]
    public void FarmerAccept_SelfPickup_AcceptsTheOrder()
    {
        var decision = Farmer(Order("funded", "pending", "none", trip: false), OrderCommitment.AcceptCommand);

        decision.NewStatus.Should().Be("accepted");
        decision.Fields!["status"].Should().Be("accepted");
        decision.Fields.Should().NotContainKey(OrderCommitment.TruckerResponseField);
    }

    [Theory]
    [InlineData(OrderCommitment.Response.FailedCapacity, OrderCommitment.TransportFailure.TripFull)]
    [InlineData(OrderCommitment.Response.Cancelled, OrderCommitment.TransportFailure.TripCancelled)]
    public void FarmerAccept_WhenTheTransportAlreadyFailed_LandsWithTheBuyer(string trucker, string failure)
    {
        var decision = Farmer(Order("funded", "pending", trucker), OrderCommitment.AcceptCommand);

        decision.NewStatus.Should().Be(OrderCommitment.AwaitingTransportStatus);
        decision.Fields![OrderCommitment.TransportFailureField].Should().Be(failure);
        decision.Fields[OrderCommitment.BuyerChoiceDeadlineAtField].Should().Be(OrderCommitment.Iso(Now.AddHours(48)));
        decision.Fields[OrderCommitment.FarmerResponseField].Should().Be(OrderCommitment.Response.Accepted);
    }

    [Fact]
    public void FarmerAccept_Twice_IsANoOp()
    {
        Farmer(Order("funded", "accepted", "pending"), OrderCommitment.AcceptCommand).IsNoOp.Should().BeTrue();
        Farmer(Order("accepted", "accepted", "accepted"), OrderCommitment.AcceptCommand).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void FarmerAccept_AfterTheDeadline_IsRefused()
    {
        var decision = Farmer(Order("funded", "pending", "not_asked", farmerDeadline: Now.AddMinutes(-1)), OrderCommitment.AcceptCommand);

        decision.ErrorCode.Should().Be(OrderCommitment.DeadlinePassedCode);
    }

    [Fact]
    public void FarmerAccept_LegacyRowWithoutTheStamp_IsNotRefusedForTime()
    {
        // No farmerDeadlineAt on the row: the cron decides, the accept goes through.
        Farmer(Order("funded"), OrderCommitment.AcceptCommand).IsError.Should().BeFalse();
    }

    [Fact]
    public void FarmerDecline_RejectsWithRefund_AndReleasesTheBooking()
    {
        var decision = Farmer(Order("funded", "pending", "not_asked"), OrderCommitment.DeclineCommand, reason: "crop_not_ready");

        decision.NewStatus.Should().Be("rejected");
        decision.Fields![OrderCommitment.FarmerResponseField].Should().Be(OrderCommitment.Response.Declined);
        decision.Fields[OrderCommitment.CancelledByField].Should().Be(OrderCommitment.Party.Farmer);
        decision.Fields[OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.Farmer);
        decision.Fields[OrderCommitment.DeclineReasonField].Should().Be("crop_not_ready");
        decision.Effect.Should().Be(CommitmentEffect.ReleaseBooking);
    }

    [Fact]
    public void FarmerDecline_AfterAccepting_IsRefused()
    {
        Farmer(Order("funded", "accepted", "pending"), OrderCommitment.DeclineCommand).ErrorCode
            .Should().Be(OrderCommitment.ResponseNotPendingCode);
    }

    [Fact]
    public void UnknownDeclineReason_IsAValidationError()
    {
        Farmer(Order("funded"), OrderCommitment.DeclineCommand, reason: "lazy").ErrorCode.Should().Be("ORDER_VALIDATION");
    }

    // ===== trucker =====

    [Fact]
    public void TruckerAccept_BeforeTheFarmer_IsRefused()
    {
        var decision = Trucker(Order("funded", "pending", "not_asked"), OrderCommitment.AcceptCommand);

        decision.ErrorCode.Should().Be(OrderCommitment.ResponseNotPendingCode);
        decision.ErrorMessage.Should().Contain("farmer has not accepted");
    }

    [Fact]
    public void TruckerAccept_AfterTheFarmer_AcceptsTheOrder_AndConfirmsTheBooking()
    {
        var decision = Trucker(Order("funded", "accepted", "pending", truckerDeadline: Now.AddHours(10)), OrderCommitment.AcceptCommand);

        decision.NewStatus.Should().Be("accepted");
        decision.Fields![OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.Accepted);
        decision.Effect.Should().Be(CommitmentEffect.ConfirmBooking);
        decision.AuditAction.Should().Be(OrderCommitment.TruckerAuditAction);
    }

    [Fact]
    public void TruckerAccept_AfterTheDeadline_IsRefused()
    {
        Trucker(Order("funded", "accepted", "pending", truckerDeadline: Now.AddMinutes(-1)), OrderCommitment.AcceptCommand)
            .ErrorCode.Should().Be(OrderCommitment.DeadlinePassedCode);
    }

    [Fact]
    public void TruckerDecline_ParksTheOrderWithTheBuyer()
    {
        var decision = Trucker(Order("funded", "accepted", "pending"), OrderCommitment.DeclineCommand, reason: "date");

        decision.NewStatus.Should().Be(OrderCommitment.AwaitingTransportStatus);
        decision.Fields![OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.Declined);
        decision.Fields[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TruckerDeclined);
        decision.Fields[OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.Trucker);
        decision.Fields[OrderCommitment.BuyerChoiceDeadlineAtField].Should().Be(OrderCommitment.Iso(Now.AddHours(48)));
        decision.Effect.Should().Be(CommitmentEffect.ReleaseBooking);
    }

    [Fact]
    public void TruckerDecline_AfterTheDeadline_StillCounts()
    {
        // The cron would expire it anyway; a late decline records the reason instead.
        Trucker(Order("funded", "accepted", "pending", truckerDeadline: Now.AddMinutes(-1)), OrderCommitment.DeclineCommand)
            .IsError.Should().BeFalse();
    }

    [Fact]
    public void TruckerDropOut_NeedsAFreshSignIn_ThenParksTheOrder()
    {
        var committed = Order("accepted", "accepted", "accepted");

        Trucker(committed, OrderCommitment.DeclineCommand, fresh: false).ErrorCode.Should().Be(StepUpPolicy.ErrorCode);

        var decision = Trucker(committed, OrderCommitment.DeclineCommand, fresh: true);
        decision.NewStatus.Should().Be(OrderCommitment.AwaitingTransportStatus);
        decision.Fields![OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.Cancelled);
        decision.Fields[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TruckerCancelled);
        decision.Effect.Should().Be(CommitmentEffect.ReleaseBookingAndCancelDelivery);
    }

    [Fact]
    public void TruckerAccept_OnAnAcceptedOrder_IsANoOp()
    {
        Trucker(Order("accepted", "accepted", "accepted"), OrderCommitment.AcceptCommand).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void Trucker_OnASelfPickupOrder_IsUnauthorized()
    {
        Trucker(Order("funded", "accepted", "none", trip: false), OrderCommitment.AcceptCommand).ErrorCode.Should().Be("ORDER_UNAUTHORIZED");
    }

    [Fact]
    public void Trucker_OnAClosedRequest_IsRefused()
    {
        Trucker(Order("funded", "accepted", "declined"), OrderCommitment.AcceptCommand).ErrorCode
            .Should().Be(OrderCommitment.ResponseNotPendingCode);
        Trucker(Order(OrderCommitment.AwaitingTransportStatus, "accepted", "declined"), OrderCommitment.AcceptCommand)
            .ErrorCode.Should().Be("ORDER_VALIDATION");
    }

    [Fact]
    public void UnknownCommand_IsAValidationError()
    {
        Farmer(Order("funded"), "maybe").ErrorCode.Should().Be("ORDER_VALIDATION");
    }
}

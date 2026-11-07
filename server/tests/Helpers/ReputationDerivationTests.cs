using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-28: the stream derivation rules R1 to R13 (reputation-architecture.md 7.2) from recorded
/// order images, and the order changes that are not events.
/// </summary>
public class ReputationDerivationTests
{
    private const string Updated = "2026-11-07T09:00:00.0000000Z";

    private static Dictionary<string, object> Order(params (string Key, object Value)[] fields)
    {
        var image = new Dictionary<string, object>
        {
            ["orderId"] = "order-1",
            ["farmerId"] = "farmer-1",
            ["buyerId"] = "buyer-1",
            ["truckerId"] = "trucker-1",
            ["tripId"] = "trip-1",
            ["fulfillment"] = "trucker",
            ["pickupDate"] = "2026-11-10T06:00:00.000Z",
            ["paymentVerifiedAt"] = "2026-11-06T09:00:00.000Z",
            ["farmerDeadlineAt"] = "2026-11-07T09:00:00.000Z",
            ["truckerAskedAt"] = "2026-11-06T12:00:00.000Z",
            ["truckerDeadlineAt"] = "2026-11-07T12:00:00.000Z",
            ["orderTotal"] = 9640.5m,
            ["updatedAt"] = Updated
        };
        foreach (var (key, value) in fields)
        {
            image[key] = value;
        }

        return image;
    }

    private static IReadOnlyList<ReputationCandidate> Derive(Dictionary<string, object> old, Dictionary<string, object> row)
    {
        using var doc = JsonDocument.Parse(OrderRecords.Modify(old, row, sequence: "seq-1"));
        var dynamodb = doc.RootElement.GetProperty("dynamodb");
        return ReputationDerivation.Derive(
            new StreamImage(dynamodb.GetProperty("OldImage").Clone()),
            new StreamImage(dynamodb.GetProperty("NewImage").Clone()),
            new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc),
            "seq-1");
    }

    private static ReputationCandidate Single(Dictionary<string, object> old, Dictionary<string, object> row)
    {
        var candidates = Derive(old, row);
        candidates.Should().ContainSingle();
        return candidates[0];
    }

    [Fact]
    public void R1_FarmerDeclined()
    {
        var c = Single(
            Order(("status", "funded"), ("farmerResponse", "pending")),
            Order(("status", "cancelled"), ("farmerResponse", "declined")));

        c.Code.Should().Be(ReputationCodes.DeclinedRequest);
        c.UserId.Should().Be("farmer-1");
        c.Party.Should().Be("farmer");
        c.EventId.Should().Be("REQUEST#order-1");
        c.BuyerId.Should().Be("buyer-1");
        c.OccurredAt.Should().Be(new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc));
        c.SourceSeq.Should().Be("seq-1");
    }

    [Fact]
    public void R2_FarmerExpired_CarriesTheRequestWindow()
    {
        var c = Single(
            Order(("status", "funded"), ("farmerResponse", "pending")),
            Order(("status", "cancelled"), ("farmerResponse", "expired"), ("cancelReason", "farmer_response_expired")));

        c.Code.Should().Be(ReputationCodes.ResponseExpired);
        c.EventId.Should().Be("REQUEST#order-1");
        c.RequestWindow.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void R2_MissingTimestamp_LeavesNoWindow()
    {
        var old = Order(("farmerResponse", "pending"));
        var row = Order(("farmerResponse", "expired"));
        row.Remove("paymentVerifiedAt");
        old.Remove("paymentVerifiedAt");

        Single(old, row).RequestWindow.Should().BeNull();
    }

    [Fact]
    public void R3_TruckerDeclined_UsesTheOldTruckerId()
    {
        var row = Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "declined"),
            ("transportFailure", "trucker_declined"), ("truckerId", ""));
        var c = Single(Order(("status", "funded"), ("farmerResponse", "accepted"), ("truckerResponse", "pending")), row);

        c.Code.Should().Be(ReputationCodes.DeclinedRequest);
        c.UserId.Should().Be("trucker-1");
        c.Party.Should().Be("trucker");
        c.EventId.Should().Be("REQUEST#order-1");
    }

    [Fact]
    public void R4_TruckerExpired_WindowFromAskedToDeadline()
    {
        var c = Single(
            Order(("status", "funded"), ("farmerResponse", "accepted"), ("truckerResponse", "pending")),
            Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "expired"), ("transportFailure", "trucker_expired")));

        c.Code.Should().Be(ReputationCodes.ResponseExpired);
        c.UserId.Should().Be("trucker-1");
        c.RequestWindow.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void R5_PendingRequestClosedByATripCancel_IsADecline()
    {
        var c = Single(
            Order(("status", "funded"), ("farmerResponse", "accepted"), ("truckerResponse", "pending")),
            Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "cancelled"), ("transportFailure", "trip_cancelled")));

        c.Code.Should().Be(ReputationCodes.DeclinedRequest);
        c.EventId.Should().Be("REQUEST#order-1");
    }

    [Fact]
    public void R6_TruckerDropOutAfterAccept_IsACancel()
    {
        var c = Single(
            Order(("status", "accepted"), ("farmerResponse", "accepted"), ("truckerResponse", "accepted")),
            Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "cancelled"), ("transportFailure", "trucker_cancelled")));

        c.Code.Should().Be(ReputationCodes.CancelledAfterAccept);
        c.UserId.Should().Be("trucker-1");
        c.EventId.Should().Be("CANCEL#order-1");
        c.PickupDate.Should().Be("2026-11-10T06:00:00.000Z");
    }

    [Fact]
    public void R7_TripCancelWithAnAcceptedOrder_IsOneEventPerTrip()
    {
        var oldA = Order(("status", "accepted"), ("farmerResponse", "accepted"), ("truckerResponse", "accepted"));
        var newA = Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "cancelled"), ("transportFailure", "trip_cancelled"));
        var oldB = new Dictionary<string, object>(oldA) { ["orderId"] = "order-2" };
        var newB = new Dictionary<string, object>(newA) { ["orderId"] = "order-2" };

        var a = Single(oldA, newA);
        var b = Single(oldB, newB);

        a.Code.Should().Be(ReputationCodes.TripCancelledWithBookings);
        a.EventId.Should().Be("TRIP_CANCEL#trip-1");
        a.TripId.Should().Be("trip-1");
        b.EventId.Should().Be(a.EventId, "the second order of the same cancel hits the existing slot");
    }

    [Fact]
    public void R8_FarmerCancelsAnAcceptedOrder()
    {
        var c = Single(
            Order(("status", "accepted"), ("farmerResponse", "accepted"), ("truckerResponse", "accepted")),
            Order(("status", "cancelled"), ("farmerResponse", "accepted"), ("truckerResponse", "accepted"),
                ("cancelReason", "farmer_cancelled"), ("faultParty", "farmer")));

        c.Code.Should().Be(ReputationCodes.CancelledAfterAccept);
        c.UserId.Should().Be("farmer-1");
        c.EventId.Should().Be("CANCEL#order-1");
    }

    [Fact]
    public void R9_FarmerCancelsDuringAwaitingTransport_KeepsTheTruckerFault_NoPenaltyCode()
    {
        var c = Single(
            Order(("status", "awaiting_transport"), ("farmerResponse", "accepted"), ("truckerResponse", "declined"), ("faultParty", "trucker")),
            Order(("status", "cancelled"), ("farmerResponse", "accepted"), ("truckerResponse", "declined"),
                ("cancelReason", "farmer_cancelled"), ("faultParty", "trucker")));

        c.Code.Should().Be(ReputationCodes.CancelledTransportFailed);
        c.UserId.Should().Be("farmer-1");
    }

    [Fact]
    public void R10_TruckerNoShow()
    {
        var c = Single(
            Order(("status", "accepted"), ("truckerResponse", "accepted")),
            Order(("status", "awaiting_transport"), ("truckerResponse", "accepted"), ("transportFailure", "trucker_no_show")));

        c.Code.Should().Be(ReputationCodes.NoShow);
        c.UserId.Should().Be("trucker-1");
        c.EventId.Should().Be("NOSHOW#order-1");
    }

    [Fact]
    public void R11_FarmerNoShow()
    {
        var c = Single(
            Order(("status", "accepted"), ("farmerResponse", "accepted")),
            Order(("status", "cancelled"), ("farmerResponse", "accepted"), ("cancelReason", "farmer_no_show")));

        c.Code.Should().Be(ReputationCodes.NoShow);
        c.UserId.Should().Be("farmer-1");
        c.EventId.Should().Be("NOSHOW#order-1");
    }

    [Fact]
    public void R12_DeliveryOverdueDispute()
    {
        var c = Single(
            Order(("status", "in_transit")),
            Order(("status", "disputed"), ("disputeReason", "delivery_overdue")));

        c.Code.Should().Be(ReputationCodes.DeliveryOverdue);
        c.UserId.Should().Be("trucker-1");
        c.EventId.Should().Be("OVERDUE#order-1");
    }

    [Fact]
    public void R13_Settled_CompletesForTheFarmerAndTheTruckerWhoCarriedIt()
    {
        var candidates = Derive(
            Order(("status", "disbursing"), ("truckerResponse", "accepted")),
            Order(("status", "settled"), ("truckerResponse", "accepted")));

        candidates.Select(c => (c.UserId, c.Code, c.EventId)).Should().BeEquivalentTo(new[]
        {
            ("farmer-1", ReputationCodes.OrderCompleted, "COMPLETED#order-1"),
            ("trucker-1", ReputationCodes.OrderCompleted, "COMPLETED#order-1")
        });
        candidates.Should().OnlyContain(c => c.OrderTotal == 9640.5m);
    }

    [Fact]
    public void R13_SelfPickup_CompletesOnlyForTheFarmer()
    {
        var c = Single(
            Order(("status", "disbursing"), ("fulfillment", "self_pickup"), ("truckerResponse", "none")),
            Order(("status", "settled"), ("fulfillment", "self_pickup"), ("truckerResponse", "none")));

        c.UserId.Should().Be("farmer-1");
    }

    public static IEnumerable<object[]> NonEvents()
    {
        // A trip full or gone at funding: a race the system caused.
        yield return new object[]
        {
            Order(("status", "funded"), ("farmerResponse", "pending"), ("truckerResponse", "not_asked")),
            Order(("status", "awaiting_transport"), ("farmerResponse", "pending"), ("truckerResponse", "failed_capacity"), ("transportFailure", "trip_full"))
        };
        // A trucker who was never asked when the trip was cancelled.
        yield return new object[]
        {
            Order(("status", "funded"), ("farmerResponse", "pending"), ("truckerResponse", "not_asked")),
            Order(("status", "funded"), ("farmerResponse", "pending"), ("truckerResponse", "cancelled"))
        };
        // The buyer's and the system's cancels.
        foreach (var reason in new[] { "listing_out_of_stock", "transport_not_arranged", "buyer_withdrew", "unpaid_expired", "buyer_no_show" })
        {
            yield return new object[]
            {
                Order(("status", "accepted"), ("farmerResponse", "accepted")),
                Order(("status", "cancelled"), ("farmerResponse", "accepted"), ("cancelReason", reason))
            };
        }

        // An order from before COMMITMENTS_ENABLED: no response field in the old image (R25).
        yield return new object[]
        {
            Order(("status", "funded")),
            Order(("status", "cancelled"), ("farmerResponse", "declined"))
        };
        yield return new object[]
        {
            Order(("status", "accepted")),
            Order(("status", "cancelled"), ("cancelReason", "farmer_cancelled"))
        };
        // A later change of a row that keeps the matched value.
        yield return new object[]
        {
            Order(("status", "cancelled"), ("farmerResponse", "declined")),
            Order(("status", "cancelled"), ("farmerResponse", "declined"), ("cancelNote", "edited"))
        };
        yield return new object[]
        {
            Order(("status", "settled")),
            Order(("status", "settled"), ("ratedAt", "x"))
        };
        // A dispute for another reason; a buyer cannot set the order's disputeReason.
        yield return new object[]
        {
            Order(("status", "delivered")),
            Order(("status", "disputed"))
        };
    }

    [Theory]
    [MemberData(nameof(NonEvents))]
    public void NotEvents(Dictionary<string, object> old, Dictionary<string, object> row)
        => Derive(old, row).Should().BeEmpty();

    [Fact]
    public void OccurredAt_FallsBackToTheRecordTime_WithoutUpdatedAt()
    {
        var old = Order(("farmerResponse", "pending"));
        var row = Order(("farmerResponse", "declined"));
        row.Remove("updatedAt");

        Single(old, row).OccurredAt.Should().Be(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));
    }
}

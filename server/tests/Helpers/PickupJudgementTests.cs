using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-34 (spec 4.8, delivery-verification-api.md 2.6.0): the pure rules of the arrival check-ins and the
/// pickup judgement. Every row of the spec's table, the self-pickup column, a late offline sync, the
/// check-in payload, the window and what the parties read.
/// </summary>
public class PickupJudgementTests
{
    private static readonly DateTime Deadline = new(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc);

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static Dictionary<string, object> Order(bool selfPickup = false, params (string Kind, Dictionary<string, object> Entry)[] checkIns)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", "ord-1" },
            { "status", "accepted" },
            { "buyerId", "buyer-1" },
            { "farmerId", "farmer-1" },
            { PickupJudgement.Field, PickupJudgement.Outcome.Pending },
            { PickupDeadline.Field, Iso(Deadline) },
            { OrderCommitment.FulfillmentField, selfPickup ? OrderCommitment.Fulfillment.SelfPickup : OrderCommitment.Fulfillment.Trucker }
        };
        if (!selfPickup)
        {
            order["tripId"] = "trip-1";
            order["truckerId"] = "trucker-1";
        }

        var map = new Dictionary<string, object>();
        foreach (var (kind, entry) in checkIns)
        {
            map[kind] = entry;
        }

        if (map.Count > 0)
        {
            order[PickupJudgement.CheckInsField] = map;
        }

        return order;
    }

    /// <summary>A stored entry as <c>DynamoDbHelper.FromItem</c> returns it.</summary>
    private static Dictionary<string, object> Entry(DateTime receivedAt, bool distanceChecked = true, bool late = false)
        => new()
        {
            { PickupJudgement.EntryReceivedAt, Iso(receivedAt) },
            { PickupJudgement.EntryBy, "someone" },
            { PickupJudgement.EntryDistanceChecked, distanceChecked },
            { PickupJudgement.EntryLate, late },
            { PickupJudgement.EntryLatitude, "8.75" },
            { PickupJudgement.EntryLongitude, "38.97" }
        };

    private static (string, Dictionary<string, object>) Arrived(DateTime at, bool distanceChecked = true, bool late = false)
        => (PickupJudgement.Kind.ArrivedPickup, Entry(at, distanceChecked, late));

    private static (string, Dictionary<string, object>) Ready(DateTime at, bool late = false)
        => (PickupJudgement.Kind.CropsReady, Entry(at, distanceChecked: false, late));

    // ── the table of spec 4.8 ───────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AScan_IsTheNormalFlow_WhateverTheCheckIns(bool trucker, bool farmer)
    {
        var checkIns = new List<(string, Dictionary<string, object>)>();
        if (trucker)
        {
            checkIns.Add(Arrived(Deadline.AddHours(-30)));
        }

        if (farmer)
        {
            checkIns.Add(Ready(Deadline.AddHours(-31)));
        }

        PickupJudgement.Decide(Order(false, checkIns.ToArray()), pickedUp: true, Deadline)
            .Should().Be(PickupJudgement.Outcome.Scanned);
    }

    [Fact]
    public void NoTruckerCheckIn_NoScan_IsATruckerNoShow()
        => PickupJudgement.Decide(Order(), pickedUp: false, Deadline).Should().Be(PickupJudgement.Outcome.TruckerNoShow);

    [Fact]
    public void FUP204_TheFarmersCropsReadyAlone_DoesNotChangeTheTruckerNoShow()
    {
        // The trucker no-show rests on the trucker's own missing check-in: the farmer's report is
        // neither needed nor able to cause it ("no | any | no").
        var without = PickupJudgement.Decide(Order(), pickedUp: false, Deadline);
        var with = PickupJudgement.Decide(Order(false, Ready(Deadline.AddHours(-2))), pickedUp: false, Deadline);

        with.Should().Be(PickupJudgement.Outcome.TruckerNoShow).And.Be(without);
    }

    [Fact]
    public void TruckerCheckedIn_FarmerDidNot_NoScan_IsAFarmerNoShow()
        => PickupJudgement.Decide(Order(false, Arrived(Deadline.AddHours(-30))), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.FarmerNoShow);

    [Fact]
    public void BothCheckedIn_NoScan_GoesToTheAdminQueue()
        => PickupJudgement.Decide(
                Order(false, Arrived(Deadline.AddHours(-30)), Ready(Deadline.AddHours(-31))), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.BothPresentNoScan);

    [Fact]
    public void AFarmerNoShow_ThatRestsOnAnUnmeasuredCheckIn_GoesToTheAdminQueue()
        => PickupJudgement.Decide(Order(false, Arrived(Deadline.AddHours(-30), distanceChecked: false)), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.UncheckedCheckIn);

    [Fact]
    public void SelfPickup_TheBuyerTakesTheTruckersColumn()
    {
        PickupJudgement.Decide(Order(selfPickup: true), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.BuyerNoShow);
        PickupJudgement.Decide(Order(true, Arrived(Deadline.AddHours(-3))), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.FarmerNoShow);
        PickupJudgement.Decide(Order(true, Arrived(Deadline.AddHours(-3)), Ready(Deadline.AddHours(-4))), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.BothPresentNoScan);
        PickupJudgement.Decide(Order(true, Ready(Deadline.AddHours(-4))), pickedUp: true, Deadline)
            .Should().Be(PickupJudgement.Outcome.Scanned);
    }

    [Fact]
    public void ALateOfflineSync_DoesNotCount_ButIsLateEvidenceAgainstTheJudgement()
    {
        // The trucker tapped "I have arrived" on time while offline; the phone synced after the deadline.
        // Receipt time decides: a trucker no-show, with the late check-in as evidence for the admin.
        var order = Order(false, Arrived(Deadline.AddMinutes(20), late: true));

        var outcome = PickupJudgement.Decide(order, pickedUp: false, Deadline);

        outcome.Should().Be(PickupJudgement.Outcome.TruckerNoShow);
        PickupJudgement.HasLateEvidenceAgainst(order, outcome).Should().BeTrue();
    }

    [Fact]
    public void AnEntryReceivedAfterTheDeadline_NeverCounts_EvenUnflagged()
        => PickupJudgement.Decide(Order(false, Arrived(Deadline.AddSeconds(1))), pickedUp: false, Deadline)
            .Should().Be(PickupJudgement.Outcome.TruckerNoShow);

    [Fact]
    public void AFarmersLateCropsReady_IsLateEvidenceAgainstAFarmerNoShow()
    {
        var order = Order(false, Arrived(Deadline.AddHours(-30)), Ready(Deadline.AddHours(2), late: true));

        var outcome = PickupJudgement.Decide(order, pickedUp: false, Deadline);

        outcome.Should().Be(PickupJudgement.Outcome.FarmerNoShow);
        PickupJudgement.HasLateEvidenceAgainst(order, outcome).Should().BeTrue();
        PickupJudgement.Contradicts(outcome, PickupJudgement.Kind.CropsReady).Should().BeTrue();
        PickupJudgement.Contradicts(outcome, PickupJudgement.Kind.ArrivedPickup).Should().BeFalse();
    }

    // ── who, when, where ────────────────────────────────────────────────────

    [Fact]
    public void ThePartyOfEachKind()
    {
        PickupJudgement.PartyOf(Order(), PickupJudgement.Kind.ArrivedPickup).Should().Be("trucker-1");
        PickupJudgement.PartyOf(Order(selfPickup: true), PickupJudgement.Kind.ArrivedPickup).Should().Be("buyer-1");
        PickupJudgement.PartyOf(Order(), PickupJudgement.Kind.CropsReady).Should().Be("farmer-1");
        PickupJudgement.PartyOf(Order(), PickupJudgement.Kind.ArrivedDropoff).Should().BeEmpty();
    }

    [Fact]
    public void TheWindowOpens24HoursBeforeThePickupDay_AndSelfPickupIsOpenFromTheStart()
    {
        var order = Order();
        order[OrderCommitment.PickupDateField] = Iso(Deadline.AddHours(-48));
        PickupJudgement.OpensAt(order).Should().Be(Deadline.AddHours(-72));
        PickupJudgement.OpensAt(Order(selfPickup: true)).Should().BeNull();
    }

    [Fact]
    public void TheJudgementWaitsAMinutePastTheDeadline()
        => PickupJudgement.JudgeAt(Order()).Should().Be(Deadline + PickupJudgement.SettleMargin);

    [Fact]
    public void ThePointStampedAtAcceptance_WinsOverTheLiveListing()
    {
        var order = Order();
        order[PickupJudgement.PickupLatitudeField] = "8.75";
        order[PickupJudgement.PickupLongitudeField] = "38.97";
        var moved = new Dictionary<string, object> { { "latitude", "9.60" }, { "longitude", "41.85" } };

        PickupJudgement.PointFor(order, moved).Should().Be(new TrackPoint(8.75, 38.97));
        PickupJudgement.PointFor(Order(), moved).Should().Be(new TrackPoint(9.60, 41.85));
        PickupJudgement.PointFor(Order(), null).Should().BeNull();
    }

    [Fact]
    public void ThePhonesCaptureTime_IsKeptOnlyWhenPlausible()
    {
        var received = Deadline;
        PickupJudgement.PlausibleCapturedAt(received.AddHours(-3), received).Should().Be(received.AddHours(-3));
        PickupJudgement.PlausibleCapturedAt(received.AddMinutes(4), received).Should().Be(received.AddMinutes(4));
        PickupJudgement.PlausibleCapturedAt(received.AddMinutes(6), received).Should().BeNull();
        PickupJudgement.PlausibleCapturedAt(received.AddDays(-31), received).Should().BeNull();
        PickupJudgement.PlausibleCapturedAt(null, received).Should().BeNull();
    }

    // ── the payload ─────────────────────────────────────────────────────────

    private static Dictionary<string, object> Payload(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    [Fact]
    public void AValidArrival_Parses()
    {
        var errors = PickupJudgement.Parse("ord-1#arrived_pickup", Payload(
            "{\"orderId\":\"ord-1\",\"kind\":\"arrived_pickup\",\"capturedAt\":\"2026-11-20T06:40:00.000Z\","
            + "\"latitude\":8.7523,\"longitude\":38.9785,\"accuracyM\":35,\"version\":3}"), out var checkIn);

        errors.Should().BeEmpty();
        checkIn.Should().NotBeNull();
        checkIn!.Kind.Should().Be(PickupJudgement.Kind.ArrivedPickup);
        checkIn.Latitude.Should().Be(8.7523);
        checkIn.AccuracyM.Should().Be(35);
    }

    [Fact]
    public void CropsReady_TakesNoGps()
    {
        PickupJudgement.Parse("ord-1#crops_ready", Payload("{\"orderId\":\"ord-1\",\"kind\":\"crops_ready\"}"), out var checkIn)
            .Should().BeEmpty();
        checkIn!.Latitude.Should().BeNull();
    }

    [Theory]
    [InlineData("ord-1#arrived_pickup", "{\"orderId\":\"ord-1\",\"kind\":\"arrived_pickup\"}", "required for arrived_pickup")]
    [InlineData("ord-1#arrived_dropoff", "{\"orderId\":\"ord-1\",\"kind\":\"arrived_dropoff\",\"latitude\":8.7,\"longitude\":38.9}", "not taken yet")]
    [InlineData("ord-1#crops_ready", "{\"orderId\":\"ord-2\",\"kind\":\"crops_ready\"}", "entityId must be")]
    [InlineData("ord-1#crops_ready", "{\"orderId\":\"ord-1\",\"kind\":\"crops_ready\",\"latitude\":8.7}", "come together")]
    [InlineData("ord-1#crops_ready", "{\"orderId\":\"ord-1\",\"kind\":\"crops_ready\",\"latitude\":51.5,\"longitude\":0.1}", "between")]
    [InlineData("ord-1#crops_ready", "{\"orderId\":\"ord-1\",\"kind\":\"crops_ready\",\"status\":\"accepted\"}", "Unknown fields: status")]
    [InlineData("ord-1#crops_ready", "{\"orderId\":\"ord-1\",\"kind\":\"crops_ready\",\"capturedAt\":\"yesterday\"}", "ISO 8601")]
    [InlineData("ord-1#ready", "{\"orderId\":\"ord-1\",\"kind\":\"ready\"}", "kind must be")]
    public void AnInvalidPayload_IsRefused(string entityId, string json, string problem)
    {
        var errors = PickupJudgement.Parse(entityId, Payload(json), out var checkIn);

        checkIn.Should().BeNull();
        string.Join(" ", errors).Should().Contain(problem);
    }

    // ── what is stored and what is read ─────────────────────────────────────

    [Fact]
    public void TheEntry_KeepsTheReceiptTime_AndTheFixRounded()
    {
        var checkIn = new ParsedCheckIn("ord-1", PickupJudgement.Kind.ArrivedPickup, Deadline.AddDays(-40), 8.752345, 38.978512, 35.04);

        var entry = PickupJudgement.Entry(checkIn, "trucker-1", Deadline.AddHours(-2), 1.2345, late: false);

        entry[PickupJudgement.EntryReceivedAt].Should().Be(Iso(Deadline.AddHours(-2)));
        entry.Should().NotContainKey(PickupJudgement.EntryCapturedAt, "a capture time 40 days old is not plausible");
        entry[PickupJudgement.EntryLatitude].Should().Be(8.7523);
        entry[PickupJudgement.EntryDistanceChecked].Should().Be(true);
        entry[PickupJudgement.EntryDistanceKm].Should().Be(1.23);
        entry[PickupJudgement.EntryBy].Should().Be("trucker-1");
        entry[PickupJudgement.EntryLate].Should().Be(false);
    }

    [Fact]
    public void ThePartiesRead_NoCoordinatesNoSenderNoPickupPoint()
    {
        var order = Order(false, Arrived(Deadline.AddHours(-30)), Ready(Deadline.AddHours(1), late: true));
        order[PickupJudgement.PickupLatitudeField] = "8.75";
        order[PickupJudgement.PickupLongitudeField] = "38.97";

        var view = PickupJudgement.ViewOf(order)!;
        view.Keys.Should().BeEquivalentTo(PickupJudgement.Kind.ArrivedPickup, PickupJudgement.Kind.CropsReady);
        view[PickupJudgement.Kind.ArrivedPickup].DistanceChecked.Should().BeTrue();
        view[PickupJudgement.Kind.CropsReady].Late.Should().BeTrue();

        var result = PickupJudgement.ForClient(new SyncOperationResult { OperationId = "op", Status = "conflict", ServerData = order });
        var row = result.ServerData!;
        row.Should().NotContainKey(PickupJudgement.PickupLatitudeField).And.NotContainKey(PickupJudgement.PickupLongitudeField);
        var map = (Dictionary<string, object>)row[PickupJudgement.CheckInsField];
        var arrived = (Dictionary<string, object>)map[PickupJudgement.Kind.ArrivedPickup];
        arrived.Keys.Should().BeEquivalentTo(
            PickupJudgement.EntryReceivedAt, PickupJudgement.EntryDistanceChecked, PickupJudgement.EntryLate);
        order.Should().ContainKey(PickupJudgement.PickupLatitudeField, "the row itself is not changed");
    }

    [Fact]
    public void TheClientView_SerializesThroughTheSourceGeneratedContext()
    {
        // AOT: the sync answer is written with AppJsonContext only (no reflection), nested maps and bools included.
        var order = Order(false, Arrived(Deadline.AddHours(-30)));
        var result = PickupJudgement.ForClient(new SyncOperationResult { OperationId = "op", Status = "conflict", ServerData = order });

        var json = JsonSerializer.Serialize(
            new SyncResponse { SyncBatchId = "b", Results = new List<SyncOperationResult> { result } },
            AppJsonContext.Default.SyncResponse);

        using var doc = JsonDocument.Parse(json);
        var arrived = doc.RootElement.GetProperty("results")[0].GetProperty("serverData")
            .GetProperty(PickupJudgement.CheckInsField).GetProperty(PickupJudgement.Kind.ArrivedPickup);
        arrived.GetProperty(PickupJudgement.EntryDistanceChecked).GetBoolean().Should().BeTrue();
        arrived.TryGetProperty(PickupJudgement.EntryLatitude, out _).Should().BeFalse();

        var dto = JsonSerializer.Serialize(
            WeredaConnect.Server.Functions.OrderFunction.MapToOrderDto(order), AppJsonContext.Default.OrderDto);
        dto.Should().Contain("\"checkIns\":{\"arrived_pickup\":{\"receivedAt\":");
    }

    [Fact]
    public void AnOrderWithoutCheckIns_ReadsNone()
    {
        PickupJudgement.ViewOf(Order()).Should().BeNull();
        var plain = new SyncOperationResult { OperationId = "op", Status = "success", ServerData = new Dictionary<string, object> { { "orderId", "o" } } };
        PickupJudgement.ForClient(plain).Should().BeSameAs(plain);
    }

    // ── the deadline index ──────────────────────────────────────────────────

    [Fact]
    public void AJudgeableOrder_IsDueForItsJudgement_AMinuteAfterItsDeadline()
    {
        var next = OrderDeadlines.Next(Order());

        next.Should().NotBeNull();
        next!.Value.Kind.Should().Be(OrderDeadlines.Kind.PickupJudgement);
        next.Value.At.Should().Be(Deadline + PickupJudgement.SettleMargin);
    }

    [Fact]
    public void AnOrderWithoutTheStamp_KeepsThePickupOverdueMarker()
    {
        var order = Order();
        order.Remove(PickupJudgement.Field);

        OrderDeadlines.Next(order)!.Value.Should().Be(new OrderDeadline(Deadline, OrderDeadlines.Kind.PickupOverdue));
    }

    [Fact]
    public void AJudgedOrder_HasNothingDue()
    {
        var order = Order();
        order[PickupJudgement.Field] = PickupJudgement.Outcome.BothPresentNoScan;
        order[PickupJudgement.JudgedAtField] = Iso(Deadline.AddMinutes(5));
        order[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(Deadline.AddMinutes(5));

        OrderDeadlines.Next(order).Should().BeNull();
    }
}

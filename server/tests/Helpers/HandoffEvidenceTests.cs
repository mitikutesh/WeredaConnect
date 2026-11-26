using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// FUP-440, GCR-01 and GCR-02 (delivery-verification-api.md 2.19.0): the handoff evidence a leg's operation stamps.
/// Each value is checked on its own and a bad one is left out, never refused, so evidence can never block a handoff.
/// </summary>
public sealed class HandoffEvidenceTests
{
    private static readonly DateTime Arrived = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement Point(string extra = "")
        => Json("{\"latitude\":9.0,\"longitude\":38.7" + (extra.Length > 0 ? "," + extra : "") + "}");

    private static Dictionary<string, object> Data(string? gpsStatus, string field = "gpsDropoff", JsonElement? point = null)
    {
        var data = new Dictionary<string, object> { ["status"] = Json("\"delivered\"") };
        if (gpsStatus != null)
        {
            data["gpsStatus"] = Json(JsonSerializer.Serialize(gpsStatus));
        }

        if (point is { } p)
        {
            data[field] = p;
        }

        return data;
    }

    private static Dictionary<string, string> Stamps(
        string leg, Dictionary<string, object>? data, string? method = null, string? platform = null, string? timestamp = null)
        => HandoffEvidence.Stamps(leg, data, method, platform, timestamp, Arrived)
            .ToDictionary(kv => kv.Key, kv => kv.Value.S ?? kv.Value.N);

    [Fact]
    public void AFullPickup_StampsEveryPickupField_AndNoDropoffOne()
    {
        var data = Data("fix", "gpsPickup", Point($"\"capturedAt\":\"{Iso(Arrived.AddMinutes(-2))}\",\"accuracyM\":12.5"));

        var stamps = Stamps(HandoffEvidence.PickupLeg, data, "qr", "android", Iso(Arrived.AddMinutes(-1)));

        stamps.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["pickupGpsStatus"] = "fix",
            ["pickupGpsCapturedAt"] = Iso(Arrived.AddMinutes(-2)),
            ["pickupGpsAccuracyM"] = "12.5",
            ["pickupMethod"] = "qr",
            ["pickupPlatform"] = "android",
        });
    }

    [Fact]
    public void ADropoffWithoutAFix_StampsTheCause_AndThePhoneTime()
    {
        var stamps = Stamps(HandoffEvidence.DropoffLeg, Data("timeout"), "code", "web", Iso(Arrived.AddHours(-6)));

        stamps.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["dropoffGpsStatus"] = "timeout",
            ["dropoffMethod"] = "code",
            ["dropoffPlatform"] = "web",
            ["dropoffCapturedAt"] = Iso(Arrived.AddHours(-6)),
        });
    }

    [Theory]
    [InlineData("fix")]
    [InlineData("approximate")]
    public void AStatusWithAPoint_IsStamped_OnlyWithThePoint(string status)
    {
        Stamps(HandoffEvidence.DropoffLeg, Data(status, point: Point())).Should().ContainKey("dropoffGpsStatus")
            .WhoseValue.Should().Be(status);
        Stamps(HandoffEvidence.DropoffLeg, Data(status)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("services_off")]
    [InlineData("denied")]
    [InlineData("denied_forever")]
    [InlineData("timeout")]
    public void AStatusWithoutAPoint_IsStamped_OnlyWithoutOne(string status)
    {
        Stamps(HandoffEvidence.PickupLeg, Data(status, "gpsPickup")).Should().ContainKey("pickupGpsStatus")
            .WhoseValue.Should().Be(status);
        Stamps(HandoffEvidence.PickupLeg, Data(status, "gpsPickup", Point())).Should().BeEmpty();
    }

    [Theory]
    [InlineData("gps_off")]
    [InlineData("FIX")]
    [InlineData("")]
    public void AStatusOutsideTheList_IsDropped(string status)
        => Stamps(HandoffEvidence.DropoffLeg, Data(status)).Should().BeEmpty();

    [Fact]
    public void AStatusThatIsNotAString_IsDropped()
    {
        var data = Data(null);
        data["gpsStatus"] = Json("3");

        Stamps(HandoffEvidence.DropoffLeg, data).Should().BeEmpty();
    }

    [Fact]
    public void TheOtherLegsPoint_IsNotThisLegs()
    {
        // A drop-off judged by its own point: a gpsPickup riding along (dropped by FUP-246) is no fix here.
        var data = Data("fix", "gpsPickup", Point("\"accuracyM\":5"));

        Stamps(HandoffEvidence.DropoffLeg, data).Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"accuracyM\":-1")]
    [InlineData("\"accuracyM\":100000.5")]
    [InlineData("\"accuracyM\":1e400")]
    [InlineData("\"accuracyM\":\"12\"")]
    [InlineData("\"accuracyM\":null")]
    [InlineData("\"capturedAt\":\"not a time\"")]
    [InlineData("\"capturedAt\":1764147600")]
    [InlineData("\"capturedAt\":\"2026-11-26T09:10:00Z\"")] // 10 minutes after it reached the server
    [InlineData("\"capturedAt\":\"2026-10-20T09:00:00Z\"")] // 37 days old
    public void ABadFixTimeOrAccuracy_IsDropped_AndTheStatusStays(string extra)
    {
        var stamps = Stamps(HandoffEvidence.DropoffLeg, Data("fix", point: Point(extra)));

        stamps.Should().BeEquivalentTo(new Dictionary<string, string> { ["dropoffGpsStatus"] = "fix" });
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(100000.0, "100000")]
    [InlineData(1500.25, "1500.25")]
    public void AnAccuracyInRange_IsStamped(double metres, string stored)
    {
        var point = Point("\"accuracyM\":" + metres.ToString(CultureInfo.InvariantCulture));

        Stamps(HandoffEvidence.PickupLeg, Data(null, "gpsPickup", point))["pickupGpsAccuracyM"].Should().Be(stored);
    }

    [Fact]
    public void AFixTimeWithAnOffset_IsStoredInUtc()
    {
        var point = Point("\"capturedAt\":\"2026-11-26T11:58:00+03:00\"");

        Stamps(HandoffEvidence.PickupLeg, Data(null, "gpsPickup", point))["pickupGpsCapturedAt"]
            .Should().Be("2026-11-26T08:58:00.0000000Z");
    }

    [Theory]
    [InlineData("android", true)]
    [InlineData("web", true)]
    [InlineData(null, false)]
    [InlineData("ios", false)]
    [InlineData("Android", false)]
    [InlineData("", false)]
    public void OnlyAKnownPlatform_IsStamped(string? platform, bool stamped)
        => Stamps(HandoffEvidence.PickupLeg, Data(null), platform: platform).ContainsKey("pickupPlatform").Should().Be(stamped);

    [Theory]
    [InlineData("qr", true)]
    [InlineData("code", true)]
    [InlineData(null, false)]
    [InlineData("typed", false)]
    public void OnlyAProvenMethod_IsStamped(string? method, bool stamped)
        => Stamps(HandoffEvidence.DropoffLeg, Data(null), method).ContainsKey("dropoffMethod").Should().Be(stamped);

    [Theory]
    [InlineData(null)]
    [InlineData("yesterday")]
    [InlineData("2026-11-26T09:06:00Z")] // 6 minutes ahead of the server
    [InlineData("2026-10-01T09:00:00Z")] // older than 30 days
    public void AnImplausibleDropoffPhoneTime_IsDropped(string? timestamp)
        => Stamps(HandoffEvidence.DropoffLeg, Data(null), timestamp: timestamp).Should().BeEmpty();

    [Fact]
    public void ThePickup_StampsNoPhoneTimeOfItsOwn()
    {
        // The pickup's phone time is pickupCapturedAt (FRD-09), stamped by the transition itself.
        Stamps(HandoffEvidence.PickupLeg, Data(null), timestamp: Iso(Arrived.AddMinutes(-1))).Should().BeEmpty();
    }

    [Fact]
    public void AnOldClient_StampsOnlyWhatTheServerProved()
    {
        Stamps(HandoffEvidence.PickupLeg, Data(null, "gpsPickup", Point()), "qr")
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["pickupMethod"] = "qr" });
        Stamps(HandoffEvidence.PickupLeg, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("pickedUp", "pickup")]
    [InlineData("delivered", "dropoff")]
    [InlineData("inTransit", null)]
    [InlineData("disputed", null)]
    [InlineData(null, null)]
    public void TheLegOfAStatus(string? status, string? leg) => HandoffEvidence.LegOf(status).Should().Be(leg);

    [Fact]
    public void TheStoredMethodAndAccuracy_AreReadBack()
    {
        var row = new Dictionary<string, object>
        {
            ["pickupMethod"] = "code",
            ["dropoffMethod"] = "scanned",
            ["pickupGpsAccuracyM"] = "35.5",
            ["dropoffGpsAccuracyM"] = "wide"
        };

        HandoffEvidence.MethodOf(row, HandoffEvidence.PickupLeg).Should().Be("code");
        HandoffEvidence.MethodOf(row, HandoffEvidence.DropoffLeg).Should().BeNull();
        HandoffEvidence.AccuracyOf(row, HandoffEvidence.PickupLeg).Should().Be(35.5);
        HandoffEvidence.AccuracyOf(row, HandoffEvidence.DropoffLeg).Should().BeNull();
        HandoffEvidence.StoredText(row, "pickupPlatform").Should().BeNull();
    }

    [Fact]
    public void EveryStampedField_IsServerOnly()
    {
        var data = Data("fix", point: Point($"\"capturedAt\":\"{Iso(Arrived)}\",\"accuracyM\":3"));
        var names = HandoffEvidence.Stamps(HandoffEvidence.DropoffLeg, data, "qr", "web", Iso(Arrived), Arrived).Keys
            .Concat(HandoffEvidence.Stamps(HandoffEvidence.PickupLeg, Data("fix", "gpsPickup", Point(
                $"\"capturedAt\":\"{Iso(Arrived)}\",\"accuracyM\":3")), "qr", "web", Iso(Arrived), Arrived).Keys);

        HandoffEvidence.ServerOnlyFields.Should().BeEquivalentTo(names);
    }
}

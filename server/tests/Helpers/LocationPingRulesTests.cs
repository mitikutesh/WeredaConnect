using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>LT-05: the pure parts of the location rules (the active-trip rule edges, keys and TTL).</summary>
public class LocationPingRulesTests
{
    private static readonly DateTime Captured = new(2026, 10, 27, 8, 42, 0, DateTimeKind.Utc);

    private static Dictionary<string, object> Row(string status, params (string Key, string Value)[] fields)
    {
        var row = new Dictionary<string, object> { ["status"] = status };
        foreach (var (key, value) in fields)
        {
            row[key] = value;
        }

        return row;
    }

    [Fact]
    public void Coverage_AnEndedDeliveryWithoutAReadableTime_LetsThePingIn()
    {
        var coverage = LocationPingRules.Coverage(
            new[] { Row("delivered", ("pickedUpAt", "2026-10-27T05:00:00Z"), ("confirmedAt", "garbled")) }, Captured);

        coverage.Should().Be(LocationCoverage.Active);
    }

    [Fact]
    public void Coverage_ADeliveredRowWithoutPickedUpAt_StillCountsAsHavingBeenOnTheRoad()
    {
        var coverage = LocationPingRules.Coverage(
            new[] { Row("delivered", ("confirmedAt", "2026-10-27T08:00:00Z")) }, Captured);

        coverage.Should().Be(LocationCoverage.Ended);
    }

    [Fact]
    public void Coverage_AVerdictCloseFallsBackToItsResolutionTime()
    {
        var coverage = LocationPingRules.Coverage(
            new[]
            {
                Row("cancelled", ("pickedUpAt", "2026-10-27T05:00:00Z"), ("disputeResolvedAt", "2026-10-27T09:00:00Z"))
            },
            Captured);

        coverage.Should().Be(LocationCoverage.Active);
    }

    [Fact]
    public void Coverage_AnUnknownStatus_CountsForNothing()
    {
        LocationPingRules.Coverage(new[] { Row("lost") }, Captured).Should().Be(LocationCoverage.NotActive);
    }

    [Fact]
    public void Keys_AndTtl_UseUtcWithMilliseconds()
    {
        var at = new DateTime(2026, 10, 27, 8, 42, 5, 123, DateTimeKind.Utc);

        LocationPingRules.FormatUtc(at).Should().Be("2026-10-27T08:42:05.123Z");
        LocationPingRules.HourPrefix(at).Should().Be("2026-10-27T08:");
        LocationPingRules.ExpiresAt(Captured).Should().Be(new DateTimeOffset(Captured).AddDays(60).ToUnixTimeSeconds());
        LocationPingRules.IsFixSortKey("2026-10-27T08:42:05.123Z#fix").Should().BeTrue();
        LocationPingRules.IsFixSortKey("2026-10-27T08:42:05.123Z#paused").Should().BeFalse();
    }

    [Theory]
    [InlineData(8.75236, 8.7524)]
    [InlineData(38.97854, 38.9785)]
    public void RoundCoordinate_KeepsFourDecimals(double value, double expected)
    {
        LocationPingRules.RoundCoordinate(value).Should().Be(expected);
    }

    [Fact]
    public void Parse_SubMillisecondDigits_AreDropped_SoTheEntityIdStillMatches()
    {
        var data = new Dictionary<string, object>
        {
            ["tripId"] = "trip-101",
            ["kind"] = "resumed",
            ["capturedAt"] = "2026-10-27T08:42:00.1239Z"
        };
        var ms = new DateTimeOffset(Captured).ToUnixTimeMilliseconds() + 123;

        var errors = LocationPingRules.Parse($"trip-101#{ms}", data, out var ping);

        errors.Should().BeEmpty();
        ping!.SortKey.Should().Be("2026-10-27T08:42:00.123Z#resumed");
    }
}

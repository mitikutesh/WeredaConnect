using System.Globalization;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// L-7: a trip counts for its whole day in Ethiopia (UTC+3) and has left from the next
/// Ethiopia midnight (21:00 UTC). The browse cutoff and the order guard both use it.
/// </summary>
public class TripDepartureTests
{
    // A trip picked for 4 Nov is stored as Ethiopia midnight of 4 Nov.
    private static readonly DateTime TripFor4Nov = new(2026, 11, 3, 21, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2026-11-03T21:00:00Z", "2026-11-03T21:00:00Z")] // 00:00 in Addis Ababa
    [InlineData("2026-11-04T00:00:00Z", "2026-11-03T21:00:00Z")] // 03:00, the UTC date is ahead
    [InlineData("2026-11-04T20:59:59Z", "2026-11-03T21:00:00Z")] // 23:59:59, still 4 Nov
    [InlineData("2026-11-04T21:00:00Z", "2026-11-04T21:00:00Z")] // midnight: 5 Nov begins
    [InlineData("2026-12-31T22:00:00Z", "2026-12-31T21:00:00Z")] // 1 Jan in Ethiopia
    public void StartOfEthiopiaDay_IsTheLastEthiopiaMidnight(string now, string expected)
    {
        var start = TripDeparture.StartOfEthiopiaDayUtc(Utc(now));

        start.Should().Be(Utc(expected));
        start.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void StartOfEthiopiaDay_ReadsAnUnspecifiedTimeAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 4, 20, 59, 59, DateTimeKind.Unspecified);

        TripDeparture.StartOfEthiopiaDayUtc(unspecified).Should().Be(TripFor4Nov);
    }

    [Fact]
    public void Cutoff_IsInTheStoredDepartureForm()
    {
        TripDeparture.CutoffIso(new DateTime(2026, 11, 4, 8, 30, 0, DateTimeKind.Utc))
            .Should().Be("2026-11-03T21:00:00.000Z");
    }

    [Fact]
    public void SameDayTrip_HasNotLeft_UntilEthiopiaMidnight()
    {
        TripDeparture.HasDeparted(TripFor4Nov, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc))
            .Should().BeFalse("the truck may run late on its day");
        TripDeparture.HasDeparted(TripFor4Nov, new DateTime(2026, 11, 4, 20, 59, 59, DateTimeKind.Utc))
            .Should().BeFalse();
    }

    [Fact]
    public void Trip_HasLeft_FromTheNextEthiopiaMidnight()
    {
        TripDeparture.HasDeparted(TripFor4Nov, new DateTime(2026, 11, 4, 21, 0, 0, DateTimeKind.Utc))
            .Should().BeTrue();
    }

    [Fact]
    public void FutureTrip_HasNotLeft()
    {
        TripDeparture.HasDeparted(TripFor4Nov.AddDays(2), new DateTime(2026, 11, 4, 21, 0, 0, DateTimeKind.Utc))
            .Should().BeFalse();
    }
}

using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>ESC-06: when an accepted order with no pickup counts as a transport failure.</summary>
public class PickupDeadlineTests
{
    private static readonly DateTime AcceptedAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TripOrder_Is48HoursAfterTheTripDeparts()
    {
        var departure = AcceptedAt.AddDays(3);

        PickupDeadline.Compute(departure, AcceptedAt).Should().Be(departure.AddHours(48));
    }

    [Fact]
    public void TripThatLeftBeforeTheAccept_CountsFromTheAccept()
    {
        PickupDeadline.Compute(AcceptedAt.AddDays(-2), AcceptedAt).Should().Be(AcceptedAt.AddHours(48));
    }

    [Fact]
    public void SelfPickupOrMissingTrip_IsSevenDaysAfterTheAccept()
    {
        PickupDeadline.Compute(null, AcceptedAt).Should().Be(AcceptedAt.AddDays(7));
    }

    [Fact]
    public void AcceptedAt_PrefersRespondedAt_ThenTheEarlierTimes()
    {
        var order = new Dictionary<string, object>
        {
            { "respondedAt", "2026-10-03T08:00:00.0000000Z" },
            { "paymentVerifiedAt", "2026-10-02T08:00:00.0000000Z" },
            { "createdAt", "2026-10-01T08:00:00.0000000Z" }
        };
        PickupDeadline.AcceptedAt(order).Should().Be(new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc));

        order.Remove("respondedAt");
        PickupDeadline.AcceptedAt(order).Should().Be(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));

        order.Remove("paymentVerifiedAt");
        PickupDeadline.AcceptedAt(order).Should().Be(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

        order["createdAt"] = "not a date";
        PickupDeadline.AcceptedAt(order).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10/15/2026x")]
    public void TryParse_RefusesMissingOrUnreadableTimes(string? raw)
    {
        PickupDeadline.TryParse(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void FormatAndTryParse_RoundTripInUtc()
    {
        var deadline = PickupDeadline.Compute(AcceptedAt.AddDays(1), AcceptedAt);

        PickupDeadline.TryParse(PickupDeadline.Format(deadline), out var parsed).Should().BeTrue();
        parsed.Should().Be(deadline);
        parsed.Kind.Should().Be(DateTimeKind.Utc);
    }
}

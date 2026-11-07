using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-16 (F9): the order timings read from SSM. Every value has the default the code used before;
/// a parameter overrides it only when it is a number of hours from 1 to 720, and a failed read keeps
/// every default. The loader is tested through its fetch delegate; the process-wide
/// <see cref="OrderTimings.Current"/> is never changed here (other tests run in parallel with it).
/// </summary>
public class OrderTimingsTests
{
    private const string Path = "/wereda-connect-dev/orders/";

    private readonly List<string> _log = new();
    private readonly List<IReadOnlyList<string>> _asked = new();

    private OrderTimings Load(Dictionary<string, string> values, string path = Path)
        => OrderTimings.Load(path, names =>
        {
            _asked.Add(names);
            return values;
        }, _log.Add);

    [Fact]
    public void Defaults_AreTheValuesTheCodeUsedBefore()
    {
        var d = OrderTimings.Defaults;
        d.UnpaidExpiry.Should().Be(TimeSpan.FromHours(24));
        d.PaymentVerification.Should().Be(TimeSpan.FromHours(24));
        d.ResponseWindow.Should().Be(TimeSpan.FromHours(24));
        d.DepartureMargin.Should().Be(TimeSpan.FromHours(12));
        d.BuyerChoiceWindow.Should().Be(TimeSpan.FromHours(48));
        d.ReminderLead.Should().Be(TimeSpan.FromHours(6));
        d.PickupGrace.Should().Be(TimeSpan.FromHours(48));
        d.SelfPickupWindow.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void WithoutThePathEnv_TheProcessUsesTheDefaults()
    {
        // The test run sets no ORDER_TIMINGS_PARAM_PATH, so nothing is read from SSM.
        Environment.GetEnvironmentVariable(OrderTimings.PathEnv).Should().BeNullOrEmpty();
        OrderTimings.Current.Should().Be(OrderTimings.Defaults);
        OrderCommitment.ResponseWindow.Should().Be(TimeSpan.FromHours(24));
        PickupDeadline.TripGrace.Should().Be(TimeSpan.FromHours(48));
    }

    [Fact]
    public void Load_AsksForEveryNameInOneCall_AndAppliesTheValues()
    {
        var values = OrderTimings.Names.All.ToDictionary(n => Path + n, _ => "10");
        values[Path + OrderTimings.Names.ReminderLead] = "2.5";

        var timings = Load(values);

        _asked.Should().ContainSingle().Which.Should().BeEquivalentTo(OrderTimings.Names.All.Select(n => Path + n));
        timings.UnpaidExpiry.Should().Be(TimeSpan.FromHours(10));
        // Below the checkout floor: raised to it (see Load_PaymentVerification_NeverBelowTheLastCheckout).
        timings.PaymentVerification.Should().Be(OrderTimings.PaymentVerificationFloor);
        timings.ResponseWindow.Should().Be(TimeSpan.FromHours(10));
        timings.DepartureMargin.Should().Be(TimeSpan.FromHours(10));
        timings.BuyerChoiceWindow.Should().Be(TimeSpan.FromHours(10));
        timings.ReminderLead.Should().Be(TimeSpan.FromHours(2.5));
        timings.PickupGrace.Should().Be(TimeSpan.FromHours(10));
        timings.SelfPickupWindow.Should().Be(TimeSpan.FromHours(10));
        _log.Should().NotContain(l => l.Contains(OrderTimings.DefaultedToken));
        _log.Should().Contain(l => l.Contains("reminder-lead-hours=2.5"));
    }

    [Theory]
    [InlineData("6")]
    [InlineData("20")]
    [InlineData("22")]
    public void Load_PaymentVerification_NeverBelowTheLastCheckout(string raw)
    {
        // T2 (spec 4.5 says 6 hours) may not fail an order whose checkout can still take the money:
        // checkouts open until 20 hours after paymentSubmittedAt and the last stays payable 2h10m more.
        var timings = Load(new Dictionary<string, string> { { Path + OrderTimings.Names.PaymentVerification, raw } });

        OrderTimings.PaymentVerificationFloor.Should().Be(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(10));
        timings.PaymentVerification.Should().Be(OrderTimings.PaymentVerificationFloor);
        _log.Should().ContainSingle(l => l.Contains(OrderTimings.ClampedToken))
            .Which.Should().Contain($"payment-verification-hours value={raw}h floor=22.17h");
    }

    [Theory]
    [InlineData("23", 23)]
    [InlineData("24", 24)]
    [InlineData("48", 48)]
    public void Load_PaymentVerification_AtOrAboveTheFloor_IsTaken(string raw, double hours)
    {
        var timings = Load(new Dictionary<string, string> { { Path + OrderTimings.Names.PaymentVerification, raw } });

        timings.PaymentVerification.Should().Be(TimeSpan.FromHours(hours));
        _log.Should().NotContain(l => l.Contains(OrderTimings.ClampedToken));
    }

    [Fact]
    public void Defaults_PaymentVerification_IsAboveTheFloor()
        => OrderTimings.Defaults.PaymentVerification.Should().BeGreaterThan(OrderTimings.PaymentVerificationFloor);

    [Fact]
    public void Load_PathWithoutTrailingSlash_IsTheSamePath()
    {
        var timings = Load(new Dictionary<string, string> { { Path + OrderTimings.Names.ResponseWindow, "30" } },
            path: "/wereda-connect-dev/orders");

        timings.ResponseWindow.Should().Be(TimeSpan.FromHours(30));
    }

    [Fact]
    public void Load_MissingParameters_KeepTheirDefaults_AndSaySo()
    {
        var timings = Load(new Dictionary<string, string> { { Path + OrderTimings.Names.BuyerChoiceWindow, "72" } });

        timings.BuyerChoiceWindow.Should().Be(TimeSpan.FromHours(72));
        (timings with { BuyerChoiceWindow = OrderTimings.Defaults.BuyerChoiceWindow }).Should().Be(OrderTimings.Defaults);
        _log.Count(l => l.Contains($"{OrderTimings.DefaultedToken} name=") && l.Contains("reason=missing")).Should().Be(7);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("-24")]
    [InlineData("721")]
    [InlineData("twenty")]
    [InlineData("")]
    [InlineData("NaN")]
    public void Load_UnusableValue_KeepsTheDefault(string raw)
    {
        var values = OrderTimings.Names.All.ToDictionary(n => Path + n, _ => "24");
        values[Path + OrderTimings.Names.DepartureMargin] = raw;

        var timings = Load(values);

        timings.DepartureMargin.Should().Be(TimeSpan.FromHours(12));
        _log.Should().ContainSingle(l => l.Contains(OrderTimings.DefaultedToken))
            .Which.Should().Contain("departure-margin-hours").And.Contain("reason=invalid");
    }

    [Fact]
    public void Load_FailedRead_KeepsEveryDefault()
    {
        var timings = OrderTimings.Load(Path, _ => throw new InvalidOperationException("ssm down"), _log.Add);

        timings.Should().BeSameAs(OrderTimings.Defaults);
        _log.Should().ContainSingle().Which.Should().Contain(OrderTimings.DefaultedToken).And.Contain("[InvalidOperationException]");
    }

    [Theory]
    [InlineData(24, "24")]
    [InlineData(2.5, "2.5")]
    [InlineData(168, "168")]
    public void HoursText_IsTheNumberForMessages(double hours, string expected)
    {
        OrderTimings.HoursText(TimeSpan.FromHours(hours)).Should().Be(expected);
    }
}

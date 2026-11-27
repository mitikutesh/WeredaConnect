using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-25 (sync-protocol.md 1.41.0): the <c>MIN_ORDER_APP_BUILD</c> setting and the answer to an order
/// operation for each build. The environment variable itself is not set here: every SyncFunction test
/// reads it, so a process-wide value would gate their orders.
/// </summary>
public class OrderAppBuildGateTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("0", 0)]
    [InlineData("-3", 0)]
    [InlineData("abc", 0)]
    [InlineData("3.5", 0)]
    [InlineData("3", 3)]
    [InlineData(" 4 ", 4)]
    public void Parse_APositiveWholeNumberIsTheMinimum_AnythingElseTurnsTheGateOff(string? value, int expected)
    {
        OrderAppBuildGate.Parse(value).Should().Be(expected);
    }

    [Fact]
    public void TheEnvironmentVariable_IsMinOrderAppBuild()
    {
        OrderAppBuildGate.EnvVar.Should().Be("MIN_ORDER_APP_BUILD");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Refusal_BelowTheMinimum_IsAppUpdateRequired(int build)
    {
        OrderAppBuildGate.Refusal(3, build).Should().Be(
            "APP_UPDATE_REQUIRED: This version of the app is too old for orders. Update WeredaConnect to continue.");
    }

    [Fact]
    public void Refusal_WithNoReadableBuild_IsOrderValidation_WithTheSameText()
    {
        OrderAppBuildGate.Refusal(3, null).Should().Be(
            "ORDER_VALIDATION: This version of the app is too old for orders. Update WeredaConnect to continue.");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Refusal_AtOrAboveTheMinimum_IsNone(int build)
    {
        OrderAppBuildGate.Refusal(3, build).Should().BeNull();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 1)]
    [InlineData(-1, null)]
    public void Refusal_GateOff_IsNone(int minimum, int? build)
    {
        OrderAppBuildGate.Refusal(minimum, build).Should().BeNull();
    }
}

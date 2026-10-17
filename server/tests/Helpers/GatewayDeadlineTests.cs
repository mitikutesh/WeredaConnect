using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>ESC-45 (D6): the per-invocation deadline every Telebirr HTTP call honours.</summary>
public class GatewayDeadlineTests
{
    [Fact]
    public void A_context_without_remaining_time_sets_no_deadline()
    {
        using var scope = GatewayDeadline.Begin(new TestLambdaContext(), TimeSpan.FromSeconds(5));

        GatewayDeadline.Remaining.Should().BeNull();
    }

    [Fact]
    public void The_deadline_is_the_remaining_time_minus_the_reserve()
    {
        using var scope = GatewayDeadline.Begin(
            new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(30) }, TimeSpan.FromSeconds(5));

        GatewayDeadline.Remaining.Should().BeCloseTo(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_reserve_larger_than_the_remaining_time_leaves_a_passed_deadline()
    {
        using var scope = GatewayDeadline.Begin(
            new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(3) }, TimeSpan.FromSeconds(5));

        GatewayDeadline.Remaining.Should().BeLessThan(TimeSpan.Zero);
    }

    [Fact]
    public void A_nested_scope_restores_the_outer_deadline()
    {
        GatewayDeadline.Remaining.Should().BeNull();
        using (GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(20)))
        {
            using (GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(5)))
            {
                GatewayDeadline.Remaining.Should().BeLessThan(TimeSpan.FromSeconds(6));
            }

            GatewayDeadline.Remaining.Should().BeGreaterThan(TimeSpan.FromSeconds(15));
        }

        GatewayDeadline.Remaining.Should().BeNull();
    }

    [Fact]
    public async Task The_deadline_flows_across_await()
    {
        using var scope = GatewayDeadline.Begin(DateTimeOffset.UtcNow.AddSeconds(20));

        var seen = await Task.Run(async () =>
        {
            await Task.Yield();
            return GatewayDeadline.Remaining;
        });

        seen.Should().NotBeNull();
    }
}

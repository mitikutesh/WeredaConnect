using FluentAssertions;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-09: the transaction monitor's coarse stage per order status. Must stay identical to
/// <c>deriveStage</c> in admin/src/lib/transaction.ts (its test pins the same table).
/// </summary>
public class AdminDeriveStageTests
{
    [Theory]
    [InlineData("created", "awaiting_payment")]
    [InlineData("pending_payment", "awaiting_payment")]
    [InlineData("payment_submitted", "in_progress")]
    [InlineData("funded", "in_progress")]
    [InlineData("accepted", "in_progress")]
    [InlineData("pickup_confirmed", "in_progress")]
    [InlineData("in_transit", "in_progress")]
    [InlineData("delivered", "delivered")]
    [InlineData("disbursing", "paying_out")]
    [InlineData("settled", "completed")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("rejected", "cancelled")]
    [InlineData("dispute_resolved", "resolved")]
    [InlineData("payment_failed", "problem")]
    [InlineData("disputed", "problem")]
    [InlineData("something_new", "unknown")]
    public void EveryOrderStatus_HasItsStage(string status, string stage)
        => AdminFunction.DeriveStage(status).Should().Be(stage);
}

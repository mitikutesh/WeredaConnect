using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-02: the rule that decides which captured payments of an order still wait for a manual
/// refund. The admin queue and the record call use it, and account-deletion.md 1.1.0 makes it
/// the payment_review blocker of ACC-02 (not built yet), so these tests also pin that blocker.
/// The rows are shaped exactly as TelebirrWebhookFunction writes them.
/// </summary>
public class UnappliedPaymentsTests
{
    private const string Merch = "WCO0123456789ABCDEF01234567001";

    private static Dictionary<string, object> Row(
        string action, string timestamp, string? telebirrRef = null, string? metadata = null, string actor = "telebirr")
    {
        var row = new Dictionary<string, object>
        {
            ["escrowId"] = "escrow-order-ord-1",
            ["action"] = action,
            ["timestamp"] = timestamp,
            ["actor"] = actor,
            ["fromState"] = "",
            ["toState"] = "",
        };
        if (telebirrRef != null)
        {
            row["telebirrRef"] = telebirrRef;
        }

        if (metadata != null)
        {
            row["metadata"] = metadata;
        }

        return row;
    }

    private static Dictionary<string, object> Unmatched(
        string paymentOrderId, string timestamp = "2026-10-20T08:00:00.0000000Z", string reason = "status_cancelled",
        string amount = "1500", string merch = Merch)
        => Row(UnappliedPayments.Unmatched, timestamp, paymentOrderId,
            $"orderId=ord-1,merchOrderId={merch},amount={amount},reason={reason}");

    private static Dictionary<string, object> Refund(
        string paymentOrderId, string telebirrRef = "TB-REFUND-1", string timestamp = "2026-10-21T09:00:00.0000000Z",
        string amount = "1500", string? merch = Merch)
        => Row(UnappliedPayments.RefundedManual, timestamp, telebirrRef,
            UnappliedPayments.RefundMetadata(
                paymentOrderId, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), merch), actor: "admin-1");

    [Fact]
    public void UnappliedPayment_IsOpen_WithItsIdsAndCapturedAmount()
    {
        var payments = UnappliedPayments.Evaluate(new[] { Unmatched("TB-PAY-1") }, null, null);

        payments.Should().ContainSingle();
        var payment = payments[0];
        payment.IsOpen.Should().BeTrue();
        payment.PaymentOrderId.Should().Be("TB-PAY-1");
        payment.MerchOrderId.Should().Be(Merch);
        payment.CapturedAmount.Should().Be(1500m);
        payment.HasFinalVerdict.Should().BeTrue();
        payment.Reasons.Should().Equal("status_cancelled");
        UnappliedPayments.HasOpen(new[] { Unmatched("TB-PAY-1") }, null, null).Should().BeTrue();
    }

    [Fact]
    public void RecordedRefund_LiftsThePaymentReviewBlocker()
    {
        var trail = new[] { Unmatched("TB-PAY-1"), Refund("TB-PAY-1") };

        UnappliedPayments.HasOpen(trail, null, null).Should().BeFalse();
        var payment = UnappliedPayments.Evaluate(trail, null, null).Single();
        payment.Refund.Should().NotBeNull();
        payment.Refund!.TelebirrRef.Should().Be("TB-REFUND-1");
        payment.Refund.Amount.Should().Be(1500m);
        payment.Refund.RecordedBy.Should().Be("admin-1");
    }

    [Fact]
    public void RefundOfAnotherPayment_KeepsTheBlocker()
    {
        // Two captured payments on one order (a duplicate): one refund lifts only its own.
        var trail = new[]
        {
            Row(UnappliedPayments.Duplicate, "2026-10-20T08:00:00.0000000Z", "TB-PAY-1",
                $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason=duplicate"),
            Row(UnappliedPayments.Duplicate, "2026-10-20T09:00:00.0000000Z", "TB-PAY-2",
                "orderId=ord-1,merchOrderId=WCO0123456789ABCDEF01234567002,amount=1500,reason=duplicate"),
            Refund("TB-PAY-1"),
        };

        var open = UnappliedPayments.Evaluate(trail, null, null).Where(p => p.IsOpen).ToList();

        open.Should().ContainSingle().Which.PaymentOrderId.Should().Be("TB-PAY-2");
        UnappliedPayments.HasOpen(trail, null, null).Should().BeTrue();
    }

    [Fact]
    public void RefundRow_ResolvesTheSamePaymentSeenAgainLater()
    {
        // A later row about the same payment (same payment_order_id) is not new money.
        var trail = new[]
        {
            Unmatched("TB-PAY-1"),
            Refund("TB-PAY-1", timestamp: "2026-10-21T09:00:00.0000000Z"),
            Unmatched("TB-PAY-1", timestamp: "2026-10-22T10:00:00.0000000Z"),
        };

        UnappliedPayments.HasOpen(trail, null, null).Should().BeFalse();
    }

    [Fact]
    public void NamelessPayment_IsResolvedByItsMerchOrderId()
    {
        var trail = new List<Dictionary<string, object>>
        {
            Row(UnappliedPayments.Unmatched, "2026-10-20T08:00:00.0000000Z", null,
                $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason=no_payment_order_id"),
        };

        var payment = UnappliedPayments.Evaluate(trail, null, null).Single();
        payment.PaymentOrderId.Should().BeNull();
        payment.MerchOrderId.Should().Be(Merch);
        payment.IsOpen.Should().BeTrue();
        // Not final: a later notice for the same checkout that names the payment is a new inbox
        // row and may still fund the order.
        payment.HasFinalVerdict.Should().BeFalse();

        // The admin found the payment in the portal by its merch_order_id.
        trail.Add(Refund("TB-PAY-FROM-PORTAL"));
        UnappliedPayments.HasOpen(trail, null, null).Should().BeFalse();
    }

    [Fact]
    public void RefundOfTheNamedPayment_AlsoResolvesANamelessRowOfTheSameCheckout()
    {
        // The inbox gave up before reading the body, then a redelivery named the payment.
        var trail = new[]
        {
            Row(UnappliedPayments.Unprocessed, "2026-10-20T08:00:00.0000000Z", null,
                $"orderId=ord-1,merchOrderId={Merch},reason=replay_exhausted:InvalidOperationException,attempts=8"),
            Unmatched("TB-PAY-1", timestamp: "2026-10-20T09:00:00.0000000Z"),
            Refund("TB-PAY-1"),
        };

        UnappliedPayments.Evaluate(trail, null, null).Should().HaveCount(2).And.OnlyContain(p => !p.IsOpen);
    }

    [Fact]
    public void PaymentThatFundedTheOrder_IsNotARefundCase()
    {
        // An unprocessed row whose payment the inbox replay applied later.
        var trail = new[]
        {
            Row(UnappliedPayments.Unprocessed, "2026-10-20T08:00:00.0000000Z", "TB-PAY-1",
                $"orderId=ord-1,merchOrderId={Merch},amount=1500,reason=TimeoutException"),
        };

        var payment = UnappliedPayments.Evaluate(trail, "TB-PAY-1", Merch).Single();
        payment.FundedOrder.Should().BeTrue();
        payment.IsOpen.Should().BeFalse();
        payment.HasFinalVerdict.Should().BeFalse();
        UnappliedPayments.HasOpen(trail, "TB-PAY-1", Merch).Should().BeFalse();
        UnappliedPayments.HasOpen(trail, "TB-PAY-OTHER", "WCO-OTHER").Should().BeTrue();
    }

    [Fact]
    public void NamelessPaymentOfTheFundingCheckout_IsNotARefundCase()
    {
        var trail = new[]
        {
            Row(UnappliedPayments.Unprocessed, "2026-10-20T08:00:00.0000000Z", null,
                $"orderId=ord-1,merchOrderId={Merch},reason=replay_exhausted:x,attempts=8"),
        };

        UnappliedPayments.HasOpen(trail, "TB-PAY-1", Merch).Should().BeFalse();
    }

    [Fact]
    public void AmountRefusal_TakesTheCapturedAmountFromTheNotice()
    {
        // The unmatched row carries the checkout amount; the notice said 1600.
        var trail = new[]
        {
            Row(UnappliedPayments.AmountMismatch, "2026-10-20T08:00:00.0000000Z", null, "checkout=1500,webhook=1600.00"),
            Unmatched("TB-PAY-1", timestamp: "2026-10-20T08:00:00.0100000Z", reason: "amount"),
        };

        UnappliedPayments.Evaluate(trail, null, null).Single().CapturedAmount.Should().Be(1600m);
    }

    [Fact]
    public void CurrencyRefusal_HasNoKnownCapturedAmount()
    {
        var trail = new[] { Unmatched("TB-PAY-1", reason: "currency") };

        UnappliedPayments.Evaluate(trail, null, null).Single().CapturedAmount.Should().BeNull();
    }

    [Fact]
    public void GiveUpRow_HasNoAmount_AndConflictingAmountsAreUnknown()
    {
        var giveUp = new[]
        {
            Row(UnappliedPayments.Unprocessed, "2026-10-20T08:00:00.0000000Z", "TB-PAY-1",
                $"orderId=ord-1,merchOrderId={Merch},reason=replay_exhausted:x,attempts=8"),
        };
        UnappliedPayments.Evaluate(giveUp, null, null).Single().CapturedAmount.Should().BeNull();

        var conflicting = new[]
        {
            Unmatched("TB-PAY-1", amount: "1500"),
            Unmatched("TB-PAY-1", timestamp: "2026-10-20T09:00:00.0000000Z", amount: "1400"),
        };
        UnappliedPayments.Evaluate(conflicting, null, null).Single().CapturedAmount.Should().BeNull();
    }

    [Fact]
    public void OtherAuditRows_AreIgnored()
    {
        var trail = new[]
        {
            Row("order_payment_failed", "2026-10-20T08:00:00.0000000Z", null, "orderId=ord-1,tradeStatus=Failed"),
            Row("hold", "2026-10-20T09:00:00.0000000Z", "TB-PAY-9"),
        };

        UnappliedPayments.Evaluate(trail, null, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("1500", null, "paymentOrderId=TB-PAY-1;amount=1500")]
    [InlineData("1500.50", Merch, "paymentOrderId=TB-PAY-1;amount=1500.5;merchOrderId=" + Merch)]
    [InlineData("0.07", null, "paymentOrderId=TB-PAY-1;amount=0.07")]
    public void RefundMetadata_KeepsTheReservedFieldsFirst(string amount, string? merch, string expected)
    {
        UnappliedPayments.RefundMetadata("TB-PAY-1", decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), merch)
            .Should().Be(expected);
    }
}

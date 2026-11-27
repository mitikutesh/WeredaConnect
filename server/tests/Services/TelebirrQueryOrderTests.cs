using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-37: the queryOrder dialect (ESC-45 F5). <c>order_status</c> is read before <c>trade_status</c>,
/// <c>PAY_SUCCESS</c> means what <c>Completed</c> means on the notify, and only a successful answer
/// about the order asked decides anything.
/// </summary>
public class TelebirrQueryOrderTests
{
    private const string Merch = "WCO39D3159FE171BAD33894DB64001";

    [Theory]
    [InlineData("PAY_SUCCESS", TelebirrPaymentState.Paid)]
    [InlineData("pay_success", TelebirrPaymentState.Paid)]
    [InlineData("Completed", TelebirrPaymentState.Paid)]
    [InlineData("PAY_FAILED", TelebirrPaymentState.NotPaid)]
    [InlineData("ORDER_CLOSED", TelebirrPaymentState.NotPaid)]
    [InlineData("Expired", TelebirrPaymentState.NotPaid)]
    [InlineData("Failure", TelebirrPaymentState.NotPaid)]
    [InlineData("WAIT_PAY", TelebirrPaymentState.Pending)]
    [InlineData("PAYING", TelebirrPaymentState.Pending)]
    [InlineData("ACCEPTED", TelebirrPaymentState.Pending)]
    [InlineData("Pending", TelebirrPaymentState.Pending)]
    [InlineData("REFUNDING", TelebirrPaymentState.Refunded)]
    [InlineData("REFUND_SUCCESS", TelebirrPaymentState.Refunded)]
    [InlineData("REFUND_FAILED", TelebirrPaymentState.Refunded)]
    [InlineData("SOMETHING_NEW", TelebirrPaymentState.Unknown)]
    public void Classify_maps_each_documented_status(string status, TelebirrPaymentState expected)
    {
        TelebirrQueryOrder.Classify(status, null).State.Should().Be(expected);
        TelebirrQueryOrder.Classify(null, status).State.Should().Be(expected, "trade_status is read when order_status is absent");
    }

    [Fact]
    public void Classify_reads_order_status_before_trade_status()
    {
        TelebirrQueryOrder.Classify("WAIT_PAY", "Completed").Should().Be((TelebirrPaymentState.Pending, "WAIT_PAY"));
        TelebirrQueryOrder.Classify(" ", "PAY_SUCCESS").Should().Be((TelebirrPaymentState.Paid, "PAY_SUCCESS"));
        TelebirrQueryOrder.Classify(null, null).Should().Be((TelebirrPaymentState.Unknown, (string?)null));
    }

    [Fact]
    public void ReadAnswer_takes_a_paid_answer_with_a_numeric_amount()
    {
        var body = $$$"""
            {"result":"SUCCESS","code":"0","msg":"success","sign":"x","biz_content":{
              "merch_order_id":"{{{Merch}}}","order_status":"PAY_SUCCESS","trade_status":"Completed",
              "payment_order_id":"00112233","trans_id":"TXN-9","trans_currency":"ETB","total_amount":15000.00}}
            """;

        var result = TelebirrQueryOrder.ReadAnswer(body, Merch);

        result.State.Should().Be(TelebirrPaymentState.Paid);
        result.Status.Should().Be("PAY_SUCCESS");
        result.PaymentOrderId.Should().Be("00112233");
        result.TransId.Should().Be("TXN-9");
        result.TotalAmount.Should().Be("15000.00");
        result.TransCurrency.Should().Be("ETB");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void ReadAnswer_reads_the_transId_alias()
        => TelebirrQueryOrder.ReadAnswer(
                $$$"""{"result":"SUCCESS","biz_content":{"merch_order_id":"{{{Merch}}}","order_status":"PAY_SUCCESS","transId":"T1"}}""",
                Merch)
            .TransId.Should().Be("T1");

    [Theory]
    [InlineData("""{"result":"FAIL","code":"49401024995","msg":"bad"}""", "TELEBIRR_49401024995")]
    [InlineData("""{"code":"0"}""", "TELEBIRR_NETWORK")]
    [InlineData("""{"result":"SUCCESS"}""", TelebirrQueryOrder.NoBizContent)]
    [InlineData("""{"result":"SUCCESS","biz_content":"{}"}""", TelebirrQueryOrder.NoBizContent)]
    [InlineData("""{"result":"SUCCESS","biz_content":{"merch_order_id":"WCOOTHER","order_status":"PAY_SUCCESS"}}""", TelebirrQueryOrder.ForeignAnswer)]
    [InlineData("""["SUCCESS"]""", "TELEBIRR_NETWORK")]
    public void ReadAnswer_never_decides_on_a_refusal_or_a_foreign_answer(string body, string error)
    {
        var result = TelebirrQueryOrder.ReadAnswer(body, Merch);

        result.State.Should().Be(TelebirrPaymentState.Unknown);
        result.ErrorCode.Should().Be(error);
    }

    [Fact]
    public void ReadAnswer_throws_for_a_body_that_is_not_json()
    {
        var act = () => TelebirrQueryOrder.ReadAnswer("<html>", Merch);
        act.Should().Throw<JsonException>();
    }
}

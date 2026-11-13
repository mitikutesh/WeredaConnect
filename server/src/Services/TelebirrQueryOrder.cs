using System.Text.Json;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-37: what Telebirr's queryOrder says about one checkout, reduced to the decisions the
/// reconciliation makes (escrow-protocol.md §Additions in 2.23.0).
/// </summary>
public enum TelebirrPaymentState
{
    /// <summary>No usable answer (gateway error, timeout, unreadable or foreign answer, unknown status). Never funds, never fails an order.</summary>
    Unknown,

    /// <summary><c>PAY_SUCCESS</c>: what <c>Completed</c> means on the notify. The payment funds its order through the inbox.</summary>
    Paid,

    /// <summary><c>PAY_FAILED</c> or <c>ORDER_CLOSED</c> (or the notify's <c>Failure</c>, <c>Expired</c>): no money was taken.</summary>
    NotPaid,

    /// <summary><c>WAIT_PAY</c>, <c>PAYING</c>, <c>ACCEPTED</c> (or <c>Paying</c>, <c>Pending</c>): the payment exists and is not final.</summary>
    Pending,

    /// <summary><c>REFUNDING</c>, <c>REFUND_SUCCESS</c>, <c>REFUND_FAILED</c>: money was taken and a refund started outside this flow.</summary>
    Refunded,
}

/// <summary>
/// FUP-37: one queryOrder answer. <see cref="Status"/> is the status as read (<c>order_status</c>
/// first, then <c>trade_status</c>); the payment fields are kept as raw text (an amount may come as a
/// number). <see cref="ErrorCode"/> is set for every <see cref="TelebirrPaymentState.Unknown"/> that
/// came from the gateway rather than from a status.
/// </summary>
public sealed record TelebirrQueryOrderResult
{
    public required TelebirrPaymentState State { get; init; }
    public string? Status { get; init; }
    public string? MerchOrderId { get; init; }
    public string? PaymentOrderId { get; init; }
    public string? TransId { get; init; }
    public string? TotalAmount { get; init; }
    public string? TransCurrency { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>An answer that decides nothing, with the gateway error that caused it.</summary>
    public static TelebirrQueryOrderResult Failed(string errorCode)
        => new() { State = TelebirrPaymentState.Unknown, ErrorCode = errorCode };
}

/// <summary>
/// FUP-37: the queryOrder dialect (ESC-45 F5). Ethio Telecom's table names <c>trade_status</c>, its
/// sample and a live sandbox run use <c>order_status</c>, so <c>order_status</c> is read first.
/// <c>PAY_SUCCESS</c> is normalized to the notify's <c>Completed</c>.
/// </summary>
public static class TelebirrQueryOrder
{
    /// <summary>The notify's paid status; a paid queryOrder answer is recorded in the inbox with it.</summary>
    public const string CompletedStatus = "Completed";

    /// <summary>The queryOrder answer's error code when it is about another merch_order_id than the one asked.</summary>
    public const string ForeignAnswer = "TELEBIRR_QUERY_FOREIGN";

    /// <summary>The queryOrder answer's error code when it carries no <c>biz_content</c> object.</summary>
    public const string NoBizContent = "TELEBIRR_QUERY_NO_BIZ";

    private static readonly Dictionary<string, TelebirrPaymentState> States = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAY_SUCCESS"] = TelebirrPaymentState.Paid,
        ["Completed"] = TelebirrPaymentState.Paid,
        ["PAY_FAILED"] = TelebirrPaymentState.NotPaid,
        ["ORDER_CLOSED"] = TelebirrPaymentState.NotPaid,
        ["Failure"] = TelebirrPaymentState.NotPaid,
        ["Failed"] = TelebirrPaymentState.NotPaid,
        ["Expired"] = TelebirrPaymentState.NotPaid,
        ["WAIT_PAY"] = TelebirrPaymentState.Pending,
        ["PAYING"] = TelebirrPaymentState.Pending,
        ["ACCEPTED"] = TelebirrPaymentState.Pending,
        ["Pending"] = TelebirrPaymentState.Pending,
        ["REFUNDING"] = TelebirrPaymentState.Refunded,
        ["REFUND_SUCCESS"] = TelebirrPaymentState.Refunded,
        ["REFUND_FAILED"] = TelebirrPaymentState.Refunded,
    };

    /// <summary>
    /// The state of a status pair: <paramref name="orderStatus"/> when it is not empty, else
    /// <paramref name="tradeStatus"/>. Anything not listed (or nothing) is <see cref="TelebirrPaymentState.Unknown"/>.
    /// </summary>
    public static (TelebirrPaymentState State, string? Status) Classify(string? orderStatus, string? tradeStatus)
    {
        var status = !string.IsNullOrWhiteSpace(orderStatus) ? orderStatus.Trim()
            : !string.IsNullOrWhiteSpace(tradeStatus) ? tradeStatus.Trim()
            : null;
        return status != null && States.TryGetValue(status, out var state)
            ? (state, status)
            : (TelebirrPaymentState.Unknown, status);
    }

    /// <summary>
    /// Read a 2xx queryOrder answer body for <paramref name="merchOrderId"/>. Only <c>result: SUCCESS</c>
    /// with a <c>biz_content</c> object about the same merch_order_id decides anything; a refusal is
    /// <c>TELEBIRR_{code}</c> (never "not paid": Telebirr's code for an order it never created is not
    /// documented), an answer for another order is <see cref="ForeignAnswer"/>. Throws
    /// <see cref="JsonException"/> for a body that is not JSON.
    /// </summary>
    public static TelebirrQueryOrderResult ReadAnswer(string body, string merchOrderId)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return TelebirrQueryOrderResult.Failed(TelebirrGatewayErrors.Network);
        }

        var result = Text(root, "result");
        if (!string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return TelebirrQueryOrderResult.Failed(
                string.IsNullOrEmpty(result) ? TelebirrGatewayErrors.Network : $"TELEBIRR_{Text(root, "code") ?? "ERR"}");
        }

        if (!root.TryGetProperty("biz_content", out var biz) || biz.ValueKind != JsonValueKind.Object)
        {
            return TelebirrQueryOrderResult.Failed(NoBizContent);
        }

        var answered = Text(biz, "merch_order_id");
        if (!string.Equals(answered, merchOrderId, StringComparison.Ordinal))
        {
            return TelebirrQueryOrderResult.Failed(ForeignAnswer);
        }

        var (state, status) = Classify(Text(biz, "order_status"), Text(biz, "trade_status"));
        return new TelebirrQueryOrderResult
        {
            State = state,
            Status = status,
            MerchOrderId = answered,
            PaymentOrderId = Text(biz, "payment_order_id"),
            TransId = Text(biz, "trans_id") ?? Text(biz, "transId"),
            TotalAmount = Text(biz, "total_amount"),
            TransCurrency = Text(biz, "trans_currency"),
        };
    }

    /// <summary>A string or number property as its raw text; null when absent, null or of another kind.</summary>
    private static string? Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}

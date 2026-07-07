using System.Text.Json.Serialization;

namespace WeredaConnect.Server.Models;

// ──────────────────────────────────────────────
// Telebirr H5/C2B API DTOs (SHA256WithRSA, fabric-token auth).
// All registered in AppJsonContext for AOT source-gen serialization.
// Field names are snake_case to match the Telebirr wire protocol.
// ──────────────────────────────────────────────

/// <summary>POST /apply/fabric-token request body.</summary>
public sealed record TelebirrTokenRequest
{
    [JsonPropertyName("appSecret")]
    public required string AppSecret { get; init; }
}

/// <summary>POST /apply/fabric-token response.</summary>
public sealed record TelebirrTokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }
}

/// <summary>biz_content of the preOrder request (pre-signature).</summary>
public sealed record TelebirrPreOrderBiz
{
    [JsonPropertyName("notify_url")]
    public required string NotifyUrl { get; init; }

    [JsonPropertyName("appid")]
    public required string AppId { get; init; }

    [JsonPropertyName("merch_code")]
    public required string MerchCode { get; init; }

    [JsonPropertyName("merch_order_id")]
    public required string MerchOrderId { get; init; }

    [JsonPropertyName("trade_type")]
    public required string TradeType { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("total_amount")]
    public required string TotalAmount { get; init; }

    [JsonPropertyName("trans_currency")]
    public required string TransCurrency { get; init; }

    [JsonPropertyName("timeout_express")]
    public required string TimeoutExpress { get; init; }

    [JsonPropertyName("business_type")]
    public required string BusinessType { get; init; }

    [JsonPropertyName("callback_info")]
    public string? CallbackInfo { get; init; }

    [JsonPropertyName("redirect_url")]
    public required string RedirectUrl { get; init; }
}

/// <summary>POST /payment/v1/merchant/preOrder request body (with signature).</summary>
public sealed record TelebirrPreOrderRequest
{
    [JsonPropertyName("nonce_str")]
    public required string NonceStr { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("biz_content")]
    public required TelebirrPreOrderBiz BizContent { get; init; }

    [JsonPropertyName("sign_type")]
    public required string SignType { get; init; }

    [JsonPropertyName("sign")]
    public required string Sign { get; init; }
}

/// <summary>biz_content of the preOrder response.</summary>
public sealed record TelebirrPreOrderRespBiz
{
    [JsonPropertyName("prepay_id")]
    public string? PrepayId { get; init; }

    [JsonPropertyName("merch_order_id")]
    public string? MerchOrderId { get; init; }
}

/// <summary>POST /payment/v1/merchant/preOrder response.</summary>
public sealed record TelebirrPreOrderResponse
{
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("msg")]
    public string? Msg { get; init; }

    [JsonPropertyName("biz_content")]
    public TelebirrPreOrderRespBiz? BizContent { get; init; }
}

/// <summary>biz_content of an inbound webhook notification.</summary>
public sealed record TelebirrWebhookBiz
{
    [JsonPropertyName("merch_order_id")]
    public string? MerchOrderId { get; init; }

    [JsonPropertyName("payment_order_id")]
    public string? PaymentOrderId { get; init; }

    [JsonPropertyName("total_amount")]
    public string? TotalAmount { get; init; }

    [JsonPropertyName("trans_currency")]
    public string? TransCurrency { get; init; }

    [JsonPropertyName("trade_status")]
    public string? TradeStatus { get; init; }

    [JsonPropertyName("merch_code")]
    public string? MerchCode { get; init; }

    [JsonPropertyName("trans_id")]
    public string? TransId { get; init; }
}

/// <summary>Inbound Telebirr payment notification (POST to notify_url).</summary>
public sealed record TelebirrWebhookNotification
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("nonce_str")]
    public string? NonceStr { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("notify_time")]
    public string? NotifyTime { get; init; }

    [JsonPropertyName("sign_type")]
    public string? SignType { get; init; }

    [JsonPropertyName("sign")]
    public string? Sign { get; init; }

    [JsonPropertyName("biz_content")]
    public TelebirrWebhookBiz? BizContent { get; init; }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Real Telebirr gateway for the H5/C2B "web checkout" flow, per the verified
/// protocol (fabric-token auth + SHA256WithRSA signing + preOrder + built H5
/// URL + async webhook). Works against BOTH the local sandbox simulator
/// (tools/telebirr-sandbox) and the live developerportal.ethiotelebirr.et
/// sandbox — only TelebirrConfig differs.
///
/// C2B is asynchronous: HoldFundsAsync returns a ToPayUrl for the buyer to pay;
/// the escrow only becomes 'escrowed' when TelebirrWebhookFunction receives the
/// signed notification. Release/Refund (B2B disbursement) are NOT integrated:
/// they fail closed with PAYOUT_NOT_SUPPORTED and the escrow service queues the
/// settlement for a manual payout in the merchant portal (ESC-02).
///
/// AOT-safe: System.Text.Json source-gen + System.Security.Cryptography only.
/// </summary>
public sealed class TelebirrSandboxGateway : ITelebirrGateway
{
    // Endpoint paths as Ethio Telecom documents them ("Apply Fabric Token", "Request Create
    // Order"); the local simulator serves the same paths (ESC-45, F9).
    private const string TokenPath = "/payment/v1/token";
    private const string PreOrderPath = "/payment/v1/merchant/preOrder";

    /// <summary>
    /// ESC-45 (D6): no request starts with less than this before the invocation's deadline; the
    /// call then fails with the definite <see cref="TelebirrGatewayErrors.NoTime"/> (nothing sent).
    /// </summary>
    internal static readonly TimeSpan MinCallBudget = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly ITelebirrSignatureService _signer;
    private readonly TelebirrConfig _config;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public TelebirrSandboxGateway(
        HttpClient http, ITelebirrSignatureService signer, TelebirrConfig config)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// "Hold" = create a C2B preOrder and return the H5 checkout URL. Funds are
    /// NOT confirmed here — the webhook flips the escrow to 'escrowed' later.
    /// </summary>
    public async Task<HoldFundsResponse> HoldFundsAsync(HoldFundsRequest request)
    {
        // A failed token call cannot have created a payment (preOrder was never sent), so it is
        // a definite failure, never an unknown outcome that blocks the buyer's checkout (ESC-45).
        string token;
        try
        {
            token = await AcquireTokenAsync();
        }
        catch (NoTimeLeftException)
        {
            return Fail(TelebirrGatewayErrors.NoTime, "Not enough time left in this run to call Telebirr.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Fail(TelebirrGatewayErrors.TokenFailed, $"Telebirr fabric token not obtained [{ex.GetType().Name}].");
        }

        try
        {
            var nonce = GenerateNonce();
            var timestamp = UnixSecondsNow();

            // Build the signable view (top-level + biz_content) then sign it.
            var signable = new Dictionary<string, object?>
            {
                ["nonce_str"] = nonce,
                ["method"] = "payment.preorder",
                ["timestamp"] = timestamp,
                ["version"] = "1.0",
                ["biz_content"] = new Dictionary<string, object?>
                {
                    ["notify_url"] = _config.NotifyUrl,
                    ["appid"] = _config.MerchantAppId,
                    ["merch_code"] = _config.MerchantCode,
                    ["merch_order_id"] = request.TransactionRef,
                    ["trade_type"] = "Checkout",
                    ["title"] = SafeTitle(request.Description),
                    ["total_amount"] = request.Amount.ToString("F2"),
                    ["trans_currency"] = "ETB",
                    ["timeout_express"] = "120m",
                    ["business_type"] = "BuyGoods",
                    ["callback_info"] = "From web",
                    ["redirect_url"] = _config.ReturnUrl,
                },
            };
            var sign = _signer.SignObject(signable);

            var preOrder = new TelebirrPreOrderRequest
            {
                NonceStr = nonce,
                Method = "payment.preorder",
                Timestamp = timestamp,
                Version = "1.0",
                BizContent = new TelebirrPreOrderBiz
                {
                    NotifyUrl = _config.NotifyUrl,
                    AppId = _config.MerchantAppId,
                    MerchCode = _config.MerchantCode,
                    MerchOrderId = request.TransactionRef,
                    TradeType = "Checkout",
                    Title = SafeTitle(request.Description),
                    TotalAmount = request.Amount.ToString("F2"),
                    TransCurrency = "ETB",
                    TimeoutExpress = "120m",
                    BusinessType = "BuyGoods",
                    CallbackInfo = "From web",
                    RedirectUrl = _config.ReturnUrl,
                },
                SignType = "SHA256WithRSA",
                Sign = sign,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AbsoluteUrl(PreOrderPath))
            {
                Content = JsonContent.Create(preOrder, AppJsonContext.Default.TelebirrPreOrderRequest),
            };
            req.Headers.TryAddWithoutValidation("Authorization", token);
            req.Headers.TryAddWithoutValidation("X-APP-Key", _config.FabricAppId);

            var (status, body) = await SendBoundedAsync(req);
            if ((int)status is < 200 or > 299)
            {
                return Fail("TELEBIRR_PREORDER", $"preOrder HTTP {(int)status}");
            }

            // ESC-05: an answer we cannot read, or a success without its prepay_id, does not say
            // whether Telebirr created the payment, so it is TELEBIRR_NETWORK (outcome unknown),
            // never a definite refusal. Only an explicit non-SUCCESS result is one.
            TelebirrPreOrderResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.TelebirrPreOrderResponse);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed == null || string.IsNullOrEmpty(parsed.Result))
            {
                return Fail(TelebirrGatewayErrors.Network, "Unreadable preOrder answer from Telebirr.");
            }

            if (!string.Equals(parsed.Result, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"TELEBIRR_{parsed.Code ?? "ERR"}", parsed.Msg ?? "preOrder failed");
            }

            var prepayId = parsed.BizContent?.PrepayId;
            if (string.IsNullOrEmpty(prepayId))
            {
                return Fail(TelebirrGatewayErrors.Network, "preOrder answer without a prepay_id.");
            }

            var toPayUrl = BuildH5Url(nonce, timestamp, prepayId);
            return new HoldFundsResponse
            {
                Success = true,
                TelebirrRef = $"PENDING-WEBHOOK-{ShortHash(request.TransactionRef)}",
                ToPayUrl = toPayUrl,
                PrepayId = prepayId,
            };
        }
        catch (NoTimeLeftException)
        {
            // Raised before the request was sent: Telebirr has seen nothing.
            return Fail(TelebirrGatewayErrors.NoTime, "Not enough time left in this run to call Telebirr.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(TelebirrGatewayErrors.Network, $"Network error contacting Telebirr: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return Fail(TelebirrGatewayErrors.Timeout, "Telebirr request timed out.");
        }
    }

    /// <summary>
    /// Release (B2B disbursement): not integrated. Fails closed with
    /// <see cref="TelebirrGatewayErrors.PayoutNotSupported"/> and makes no HTTP call, so no
    /// escrow can complete on a payout that never happened (ESC-02). The escrow service
    /// queues the legs for a manual payout.
    /// </summary>
    public Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request)
        => Task.FromResult(new ReleaseFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported,
            ErrorMessage = PayoutNotSupportedMessage,
        });

    /// <summary>Refund (B2B reverse): not integrated, same fail-closed rule as release.</summary>
    public Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request)
        => Task.FromResult(new RefundFundsResponse
        {
            Success = false,
            ErrorCode = TelebirrGatewayErrors.PayoutNotSupported,
            ErrorMessage = PayoutNotSupportedMessage,
        });

    private const string PayoutNotSupportedMessage =
        "Telebirr payouts are not integrated yet. An admin pays this in the merchant portal.";

    /// <summary>
    /// Not wired yet. Telebirr documents <c>/payment/v1/merchant/queryOrder</c> ("Step 5: queryOrder")
    /// for a payment whose notification did not arrive; using it for reconciliation is an ESC-45
    /// follow-up. Until then reconciliation relies on webhooks and the inbox replay.
    /// </summary>
    public Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef)
        => Task.FromResult(new TransactionStatusResponse
        {
            TransactionRef = transactionRef,
            Status = "unknown",
            Amount = 0,
            UpdatedAt = DateTime.UtcNow.ToString("o"),
        });

    /// <summary>H5 C2B is a hosted checkout: the webhook confirms the payment later.</summary>
    public bool SupportsHostedCheckout => true;

    /// <summary>
    /// Sign a fresh H5 checkout URL for an existing prepay_id (same payment, new nonce and
    /// timestamp). Built locally like the original URL; no Telebirr call is made.
    /// </summary>
    public string? BuildCheckoutUrl(string prepayId)
        => string.IsNullOrEmpty(prepayId)
            ? null
            : BuildH5Url(GenerateNonce(), UnixSecondsNow(), prepayId);

    // ── Internals ──────────────────────────────────────────────────────────

    private async Task<string> AcquireTokenAsync()
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, AbsoluteUrl(TokenPath))
        {
            Content = JsonContent.Create(
                new TelebirrTokenRequest { AppSecret = _config.AppSecret },
                AppJsonContext.Default.TelebirrTokenRequest),
        };
        req.Headers.TryAddWithoutValidation("X-APP-Key", _config.FabricAppId);

        var (status, body) = await SendBoundedAsync(req);
        if ((int)status is < 200 or > 299)
        {
            throw new HttpRequestException($"Telebirr fabric-token HTTP {(int)status}.");
        }

        var parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.TelebirrTokenResponse);
        if (parsed?.Token is not { Length: > 0 } token)
        {
            throw new HttpRequestException("Telebirr fabric-token response missing token.");
        }

        _cachedToken = token;
        var ttl = parsed.ExpiresIn is > 120 ? parsed.ExpiresIn.Value - 60 : 300;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(ttl);
        return token;
    }

    /// <summary>
    /// Build the H5 checkout URL: sign exactly the 5 fields, then append the
    /// unsigned fields (version/trade_type/sign_type/sign), URL-encoding values.
    /// </summary>
    private string BuildH5Url(string nonce, string timestamp, string prepayId)
    {
        var signed = new Dictionary<string, object?>
        {
            ["appid"] = _config.MerchantAppId,
            ["merch_code"] = _config.MerchantCode,
            ["nonce_str"] = nonce,
            ["prepay_id"] = prepayId,
            ["timestamp"] = timestamp,
        };
        var sign = _signer.SignObject(signed);

        var ordered = new (string Key, string Value)[]
        {
            ("appid", _config.MerchantAppId),
            ("merch_code", _config.MerchantCode),
            ("nonce_str", nonce),
            ("prepay_id", prepayId),
            ("timestamp", timestamp),
            ("sign_type", "SHA256WithRSA"),
            ("version", "1.0"),
            ("trade_type", "Checkout"),
            ("sign", sign),
        };
        var query = string.Join("&", ordered.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var web = _config.WebUrl.TrimEnd('/');
        return $"{web}?{query}";
    }

    /// <summary>
    /// ESC-45 (D6): every Telebirr HTTP call goes through here. With less than
    /// <see cref="MinCallBudget"/> before the invocation's <see cref="GatewayDeadline"/>, nothing is
    /// sent (<see cref="NoTimeLeftException"/>); otherwise the send and the body read are cancelled
    /// at the deadline (an <see cref="OperationCanceledException"/>, mapped to an unknown-outcome
    /// timeout like the client's own 30 s timeout). Any future payout call must use it too.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> SendBoundedAsync(HttpRequestMessage request)
    {
        using var cts = new CancellationTokenSource();
        if (GatewayDeadline.Remaining is { } remaining)
        {
            if (remaining < MinCallBudget)
            {
                throw new NoTimeLeftException();
            }

            cts.CancelAfter(remaining);
        }

        using var resp = await _http.SendAsync(request, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        return (resp.StatusCode, body);
    }

    /// <summary>The deadline left no time for a call; raised before anything is sent.</summary>
    private sealed class NoTimeLeftException : Exception
    {
    }

    /// <summary>Request and checkout-URL timestamps are UTC epoch seconds, as Ethio Telecom documents (ESC-45, F10).</summary>
    private static string UnixSecondsNow()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string AbsoluteUrl(string path)
        => $"{_config.BaseUrl.TrimEnd('/')}{path}";

    private static HoldFundsResponse Fail(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static string GenerateNonce()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string ShortHash(string input)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..12];

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    /// <summary>
    /// The order title sent to Telebirr: at most 120 characters, without <c>&amp;</c> or <c>=</c>, so a
    /// client-chosen id inside it can never change how the signed <c>key=value&amp;...</c> string splits
    /// (ESC-45).
    /// </summary>
    private static string SafeTitle(string description)
        => Truncate(description.Replace('&', ' ').Replace('=', ' '), 120);
}

using System.Text.Json;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Telebirr merchant configuration. Loaded from the local simulator's
/// credentials.local.json (camelCase) or an AWS Secrets Manager secret of the
/// same shape. AOT-safe: parsed via a Dictionary&lt;string,string&gt; source-gen path.
/// </summary>
public sealed class TelebirrConfig
{
    public required string FabricAppId { get; init; }
    public required string AppSecret { get; init; }
    public required string MerchantAppId { get; init; }
    public required string MerchantCode { get; init; }
    public required string ShortCode { get; init; }
    public required string BaseUrl { get; init; }
    public required string WebUrl { get; init; }
    public required string MerchantPrivateKeyPem { get; init; }
    public required string TelebirrPublicKeyPem { get; init; }
    public required string ReturnUrl { get; init; }
    public required string NotifyUrl { get; init; }
    public TelebirrSignaturePadding Padding { get; init; } = TelebirrSignaturePadding.Pss;
    public List<string> AllowedSourceIps { get; init; } = new();

    /// <summary>
    /// Optional outbound HTTP(S) proxy for the gateway's calls to Telebirr
    /// (fabric-token + preOrder). Telebirr's sandbox is reachable only from an
    /// Ethiopian egress, so from AWS the backend routes through an Ethiopian
    /// proxy: `http[s]://[user:pass@]host:port`. Null/empty = direct connection.
    /// Only affects OUTBOUND calls; the inbound webhook is unaffected.
    /// </summary>
    public string? HttpProxy { get; init; }

    /// <summary>
    /// Parse the credentials JSON. <paramref name="notifyUrl"/> and
    /// <paramref name="allowedSourceIps"/> come from the deployment (env/params),
    /// not the credentials blob; pass overrides for local testing.
    /// </summary>
    public static TelebirrConfig FromJson(
        string json, string? notifyUrl = null, IEnumerable<string>? allowedSourceIps = null,
        string? httpProxy = null)
    {
        var d = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("Telebirr credentials JSON is empty/invalid.");

        string Req(string key) => d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new InvalidOperationException($"Telebirr credentials missing required key '{key}'.");

        var ips = new List<string>();
        if (allowedSourceIps != null)
        {
            ips.AddRange(allowedSourceIps.Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim()));
        }
        else if (d.TryGetValue("allowedSourceIps", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            ips.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new TelebirrConfig
        {
            FabricAppId = Req("fabricAppId"),
            AppSecret = Req("appSecret"),
            MerchantAppId = Req("merchantAppId"),
            MerchantCode = Req("merchantCode"),
            ShortCode = d.GetValueOrDefault("shortCode") ?? Req("merchantCode"),
            BaseUrl = Req("baseUrl"),
            WebUrl = Req("webUrl"),
            MerchantPrivateKeyPem = Req("merchantPrivateKeyPem"),
            TelebirrPublicKeyPem = Req("telebirrPublicKeyPem"),
            ReturnUrl = d.GetValueOrDefault("returnUrl") ?? "weredaconnect://payment/complete",
            NotifyUrl = notifyUrl ?? d.GetValueOrDefault("notifyUrl")
                ?? throw new InvalidOperationException("Telebirr notifyUrl not provided (env/param or credentials)."),
            Padding = TelebirrSignatureService.ParsePadding(d.GetValueOrDefault("signPadding")),
            AllowedSourceIps = ips,
            HttpProxy = FirstNonBlank(httpProxy, d.GetValueOrDefault("httpProxy")),
        };
    }

    /// <summary>First non-blank of the candidates, or null. Env value wins over the blob.</summary>
    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}

using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// Runs only when the environment carries a fresh sandbox authorization code; skipped otherwise.
/// </summary>
public sealed class FaydaSandboxLiveFactAttribute : FactAttribute
{
    public FaydaSandboxLiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAYDA_SANDBOX_CODE")))
        {
            Skip = "Manual live check: run `eval \"$(node tools/fayda-sandbox-relay/sandbox-login.js)\"` and set FAYDA_SANDBOX_PRIVATE_KEY_B64 first.";
        }
    }
}

/// <summary>
/// The server's real Fayda client against the real VeriFayda sandbox: discovery, the
/// private_key_jwt token request (no kid), the PS256 ID token with its /v1/esignet issuer,
/// the signed userinfo and its sub binding. Needs a code minted seconds earlier by
/// tools/fayda-sandbox-relay/sandbox-login.js (codes are single use and short-lived) and
/// the shared test client's private key in FAYDA_SANDBOX_PRIVATE_KEY_B64. Never runs in CI.
/// Runbook: specs/features/fayda-mock-server.md, "Sandbox mode".
/// </summary>
[Collection("FaydaOidcProvider")]
public class FaydaSandboxLiveTests
{
    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set.");

    [FaydaSandboxLiveFact]
    public async Task ExchangeCode_AgainstTheLiveSandbox_ReturnsTheVerifiedPersona()
    {
        var issuer = Environment.GetEnvironmentVariable("FAYDA_SANDBOX_ISSUER") ?? "https://esignet.ida.fayda.et";
        var redirect = Environment.GetEnvironmentVariable("FAYDA_SANDBOX_REDIRECT_URI") ?? "http://localhost:3000/callback";
        FaydaOidcService.ResetCaches();
        var service = new FaydaOidcService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            Env("FAYDA_SANDBOX_CLIENT_ID"),
            Env("FAYDA_SANDBOX_PRIVATE_KEY_B64"),
            issuer,
            "fayda-sandbox-live-check-signing-secret-32-chars!");

        var result = await service.ExchangeCodeAsync(
            Env("FAYDA_SANDBOX_CODE"), redirect, Env("FAYDA_SANDBOX_CODE_VERIFIER"), Env("FAYDA_SANDBOX_NONCE"));

        Assert.True(result.IsSuccess, $"exchange failed: {result.Error?.Code} {result.Error?.Message}");
        var identity = result.Value!;
        // eSignet's pairwise subject (36 characters for this deployment), never a FIN.
        Assert.True(identity.FaydaId.Length >= 20, "sub too short");
        Assert.DoesNotContain(identity.FaydaId, new[] { "3126894653473958", "6230247319356120" });
        Assert.False(string.IsNullOrWhiteSpace(identity.FullName) || identity.FullName == "Unknown", "name not released");
        // The sandbox personas carry a local-format phone (09XXXXXXXX).
        Assert.NotNull(identity.PhoneNumber);
        Assert.Matches("^(\\+?251|0)9\\d{8}$", identity.PhoneNumber!);

        // The proof registration consumes: phone-bound, single use, round-trips.
        var token = service.GenerateVerificationToken(identity.FaydaId, identity.FullName, identity.PhoneNumber);
        var proof = service.ValidateVerificationToken(token);
        Assert.True(proof.IsSuccess);
        Assert.Equal(identity.FaydaId, proof.Value!.FaydaId);
        Assert.Equal(identity.PhoneNumber, proof.Value.PhoneNumber);
    }
}

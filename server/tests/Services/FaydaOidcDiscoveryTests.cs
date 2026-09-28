using System.Net;
using System.Text;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// The OIDC client against a scripted provider: discovery drives the endpoints,
/// the token request carries the PKCE verifier, and the ID token's nonce must
/// match the one the server issued.
/// </summary>
[Collection("FaydaOidcProvider")]
public class FaydaOidcDiscoveryTests
{
    private const string Issuer = "https://fayda.test";
    private const string SigningSecret = "test-signing-secret-must-be-at-least-32-chars-long!";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> Routes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (Routes.TryGetValue(path, out var handler))
            {
                return await handler(request);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static ScriptedHandler ProviderWithDiscovery()
    {
        var handler = new ScriptedHandler();
        handler.Routes["/.well-known/openid-configuration"] = _ => Task.FromResult(Json(
            "{\"issuer\":\"https://fayda.test\",\"authorization_endpoint\":\"https://fayda.test/v1/esignet/authorize\","
            + "\"token_endpoint\":\"https://fayda.test/v1/esignet/oauth/v2/token\",\"jwks_uri\":\"https://fayda.test/v1/esignet/oauth/.well-known/jwks.json\"}"));
        return handler;
    }

    private static FaydaOidcService Service(ScriptedHandler handler)
    {
        FaydaOidcService.ResetCaches();
        return new FaydaOidcService(new HttpClient(handler), "client-1", "secret-1", Issuer, SigningSecret,
            tokenRetryDelays: [TimeSpan.Zero, TimeSpan.Zero]);
    }

    [Fact]
    public async Task BuildAuthorizationUrl_UsesDiscoveredEndpointAndCarriesPkceStateNonce()
    {
        var service = Service(ProviderWithDiscovery());

        var result = await service.BuildAuthorizationUrlAsync(
            "weredaconnect://fayda-callback", "state-1", "nonce-1", "challenge-1");

        Assert.True(result.IsSuccess);
        var url = result.Value!;
        Assert.StartsWith("https://fayda.test/v1/esignet/authorize?", url);
        Assert.Contains("client_id=client-1", url);
        Assert.Contains("redirect_uri=weredaconnect%3A%2F%2Ffayda-callback", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=openid%20profile%20phone", url);
        Assert.Contains("state=state-1", url);
        Assert.Contains("nonce=nonce-1", url);
        Assert.Contains("code_challenge=challenge-1", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.DoesNotContain("secret-1", url);
    }

    [Fact]
    public async Task BuildAuthorizationUrl_WithoutDiscovery_FallsBackToIssuerAuthorize()
    {
        var service = Service(new ScriptedHandler());

        var result = await service.BuildAuthorizationUrlAsync(
            "weredaconnect://fayda-callback", "s", "n", "c");

        Assert.True(result.IsSuccess);
        Assert.StartsWith("https://fayda.test/authorize?", result.Value);
    }

    [Fact]
    public async Task ExchangeCode_PostsVerifierToDiscoveredTokenEndpoint()
    {
        var handler = ProviderWithDiscovery();
        string? tokenForm = null;
        handler.Routes["/v1/esignet/oauth/v2/token"] = async request =>
        {
            tokenForm = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        };
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
        Assert.NotNull(tokenForm);
        Assert.Contains("code_verifier=verifier-1", tokenForm);
        Assert.Contains("grant_type=authorization_code", tokenForm);
        Assert.Contains("client_id=client-1", tokenForm);
    }

    [Fact]
    public async Task ExchangeCode_ProviderDown_ReturnsUnavailable()
    {
        var handler = ProviderWithDiscovery();
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ => throw new HttpRequestException("down");
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_003", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_ConnectionRefused_RetriesThenReportsUnavailable()
    {
        var handler = ProviderWithDiscovery();
        var calls = 0;
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ =>
        {
            calls++;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        };
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.Equal("FAYDA_003", result.Error!.Code);
        Assert.Equal(503, result.Error.HttpStatus);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExchangeCode_GatewayUnavailableOnce_RetriesAndUsesTheNextAnswer()
    {
        var handler = ProviderWithDiscovery();
        var calls = 0;
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        // The second answer (a real 400 from the token service) is what the user sees.
        Assert.Equal("FAYDA_001", result.Error!.Code);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExchangeCode_Fayda5xx_IsUnavailableNotInvalidCode(HttpStatusCode status)
    {
        var handler = ProviderWithDiscovery();
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ => Task.FromResult(new HttpResponseMessage(status));
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.Equal("FAYDA_003", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_500OrUnknownFailure_IsNotRepeated()
    {
        // The code may already be redeemed: never send it twice.
        var handler = ProviderWithDiscovery();
        var calls = 0;
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : throw new HttpRequestException("reset mid-response");
        };
        var service = Service(handler);

        await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");
        Assert.Equal(1, calls);

        FaydaOidcService.ResetCaches();
        await service.ExchangeCodeAsync("code-2", "weredaconnect://fayda-callback", "v", "n");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExchangeCode_TokenResponseWithoutIdToken_IsRejected()
    {
        var handler = ProviderWithDiscovery();
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ => Task.FromResult(Json("{\"access_token\":\"opaque\"}"));
        var service = Service(handler);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    // ===== Signed ID tokens: nonce binding =====

    private static (System.Security.Cryptography.RSA rsa, string jwks) SigningKey()
    {
        var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var n = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Modulus);
        var e = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Exponent);
        var jwks = "{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"k1\",\"use\":\"sig\",\"alg\":\"RS256\",\"n\":\"" + n + "\",\"e\":\"" + e + "\"}]}";
        return (rsa, jwks);
    }

    private static string IdToken(System.Security.Cryptography.RSA rsa, string nonce, string? phone)
    {
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "k1" };
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", "FIN-1111"),
            new("name", "Abebe Kebede"),
            new("nonce", nonce)
        };
        if (phone != null)
        {
            claims.Add(new System.Security.Claims.Claim("phone_number", phone));
        }
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: Issuer, audience: "client-1", claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ScriptedHandler ProviderIssuing(string idToken, string jwks)
    {
        var handler = ProviderWithDiscovery();
        handler.Routes["/v1/esignet/oauth/.well-known/jwks.json"] = _ => Task.FromResult(Json(jwks));
        handler.Routes["/v1/esignet/oauth/v2/token"] = _ => Task.FromResult(Json("{\"id_token\":\"" + idToken + "\",\"access_token\":\"opaque\"}"));
        return handler;
    }

    [Fact]
    public async Task ExchangeCode_NonceMismatch_IsRejectedAsForeignAttempt()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "someone-elses-nonce", "+251912345678"), jwks));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_002", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_MatchingNonce_ReturnsIdentityAndPhone()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1", "+251912345678"), jwks));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("FIN-1111", result.Value!.FaydaId);
        Assert.Equal("Abebe Kebede", result.Value.FullName);
        Assert.Equal("+251912345678", result.Value.PhoneNumber);
    }

    [Fact]
    public async Task ExchangeCode_TokenSignedByAnotherKey_IsRejected()
    {
        var (rsa, jwks) = SigningKey();
        var (otherRsa, _) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(otherRsa, "nonce-1", null), jwks));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public async Task Discovery_ForAnotherIssuer_IsNotTrusted()
    {
        var handler = new ScriptedHandler();
        handler.Routes["/.well-known/openid-configuration"] = _ => Task.FromResult(Json(
            "{\"issuer\":\"https://evil.example\",\"authorization_endpoint\":\"https://evil.example/authorize\","
            + "\"token_endpoint\":\"https://evil.example/token\",\"jwks_uri\":\"https://evil.example/jwks\"}"));
        var service = Service(handler);

        var result = await service.BuildAuthorizationUrlAsync("weredaconnect://fayda-callback", "s", "n", "c");
        await service.BuildAuthorizationUrlAsync("weredaconnect://fayda-callback", "s2", "n2", "c2");

        Assert.True(result.IsSuccess);
        Assert.StartsWith("https://fayda.test/authorize?", result.Value);
        // The rejected document is not refetched on the next call.
        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath == "/.well-known/openid-configuration");
    }

    [Fact]
    public async Task ExchangeCode_IdTokenWithAlgorithmOutsideAllowList_IsRejected()
    {
        // Signed by the provider's own key, but with RS512: only the alg allow-list rejects it.
        var (rsa, jwks) = SigningKey();
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "k1" };
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: Issuer, audience: "client-1",
                claims: [new("sub", "FIN-1111"), new("nonce", "nonce-1")],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha512)));
        var service = Service(ProviderIssuing(token, jwks.Replace("\"alg\":\"RS256\",", "")));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }
}

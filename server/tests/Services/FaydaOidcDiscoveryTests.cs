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
    private const string TokenPath = "/v1/esignet/oauth/v2/token";
    private const string UserinfoPath = "/v1/esignet/oidc/userinfo";
    private const string JwksPath = "/v1/esignet/oauth/.well-known/jwks.json";

    // The relying party key the service signs its private_key_jwt assertions with.
    private static readonly System.Security.Cryptography.RSA ClientRsa = System.Security.Cryptography.RSA.Create(2048);
    private static readonly string ClientKeyPem = ClientRsa.ExportPkcs8PrivateKeyPem();

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
        return new FaydaOidcService(new HttpClient(handler), "client-1", ClientKeyPem, Issuer, SigningSecret,
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
        // eSignet releases name and phone only when the claims parameter asks for them.
        var claims = Uri.UnescapeDataString(url[(url.IndexOf("claims=", StringComparison.Ordinal) + 7)..].Split('&')[0]);
        Assert.Equal("{\"userinfo\":{\"name\":{\"essential\":true},\"phone_number\":{\"essential\":true}},\"id_token\":{}}", claims);
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
        Assert.DoesNotContain("client_secret", tokenForm);
    }

    private static Dictionary<string, string> ParseForm(string form) =>
        form.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => Uri.UnescapeDataString(kv[1].Replace('+', ' ')));

    [Fact]
    public async Task ExchangeCode_AuthenticatesWithPrivateKeyJwtAssertion()
    {
        var handler = ProviderWithDiscovery();
        string? tokenForm = null;
        handler.Routes[TokenPath] = async request =>
        {
            tokenForm = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        };

        await Service(handler).ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        var form = ParseForm(tokenForm!);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        var principal = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler { MapInboundClaims = false }
            .ValidateToken(form["client_assertion"], new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidIssuer = "client-1",
                ValidAudience = "https://fayda.test/v1/esignet/oauth/v2/token",
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(ClientRsa.ExportParameters(false)),
                ValidAlgorithms = [Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256]
            }, out var validated);
        Assert.Equal("client-1", principal.FindFirst("sub")!.Value);
        Assert.False(string.IsNullOrEmpty(principal.FindFirst("jti")?.Value));
        Assert.NotNull(principal.FindFirst("iat"));
        Assert.True(validated.ValidTo <= DateTime.UtcNow.AddMinutes(3));
    }

    [Fact]
    public async Task ExchangeCode_RetriedTokenRequest_CarriesAFreshAssertion()
    {
        var handler = ProviderWithDiscovery();
        var assertions = new List<string>();
        handler.Routes[TokenPath] = async request =>
        {
            assertions.Add(ParseForm(await request.Content!.ReadAsStringAsync())["client_assertion"]);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        await Service(handler).ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.Equal(3, assertions.Count);
        Assert.Equal(3, assertions.Distinct().Count());
    }

    [Fact]
    public async Task ExchangeCode_UnreadableClientKey_IsUnavailableWithoutCallingFayda()
    {
        var handler = ProviderWithDiscovery();
        FaydaOidcService.ResetCaches();
        var service = new FaydaOidcService(new HttpClient(handler), "client-1", "PLACEHOLDER", Issuer, SigningSecret);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_003", result.Error!.Code);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == TokenPath);
    }

    [Fact]
    public void ParseClientPrivateKey_AcceptsPemJwkAndBase64Jwk()
    {
        var p = ClientRsa.ExportParameters(true);
        string B(byte[]? b) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(b);
        var jwk = "{\"kty\":\"RSA\",\"kid\":\"rp-1\",\"n\":\"" + B(p.Modulus) + "\",\"e\":\"" + B(p.Exponent) + "\",\"d\":\"" + B(p.D)
                  + "\",\"p\":\"" + B(p.P) + "\",\"q\":\"" + B(p.Q) + "\",\"dp\":\"" + B(p.DP) + "\",\"dq\":\"" + B(p.DQ) + "\",\"qi\":\"" + B(p.InverseQ) + "\"}";

        foreach (var value in new[] { ClientKeyPem, jwk, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jwk)) })
        {
            var key = FaydaOidcService.ParseClientPrivateKey(value);
            Assert.Equal(p.Modulus, key.Rsa.ExportParameters(false).Modulus);
        }
        Assert.Equal("rp-1", FaydaOidcService.ParseClientPrivateKey(jwk).KeyId);

        var publicOnly = "{\"kty\":\"RSA\",\"n\":\"" + B(p.Modulus) + "\",\"e\":\"" + B(p.Exponent) + "\"}";
        Assert.ThrowsAny<Exception>(() => FaydaOidcService.ParseClientPrivateKey(publicOnly));
        Assert.ThrowsAny<Exception>(() => FaydaOidcService.ParseClientPrivateKey("PLACEHOLDER"));
        // A public PEM imports without error, so it must be refused explicitly.
        Assert.ThrowsAny<Exception>(() => FaydaOidcService.ParseClientPrivateKey(ClientRsa.ExportSubjectPublicKeyInfoPem()));
        using var weak = System.Security.Cryptography.RSA.Create(1024);
        Assert.ThrowsAny<Exception>(() => FaydaOidcService.ParseClientPrivateKey(weak.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public async Task ExchangeCode_PublicOnlyClientKey_IsUnavailableWithoutCallingFayda()
    {
        var handler = ProviderWithDiscovery();
        FaydaOidcService.ResetCaches();
        var service = new FaydaOidcService(new HttpClient(handler), "client-1", ClientRsa.ExportSubjectPublicKeyInfoPem(), Issuer, SigningSecret);

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "v", "n");

        Assert.Equal("FAYDA_003", result.Error!.Code);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == TokenPath);
    }

    [Fact]
    public async Task ExchangeCode_KeyStoredAfterFirstAttempt_IsPickedUpWithoutRestart()
    {
        var (rsa, jwks) = SigningKey();
        var stored = "PLACEHOLDER";
        FaydaOidcService.ResetCaches();
        var service = new FaydaOidcService(
            new HttpClient(ProviderIssuing(IdToken(rsa, "nonce-1"), jwks, SignedUserinfo(rsa, null))),
            "client-1", () => stored, Issuer, SigningSecret, [TimeSpan.Zero, TimeSpan.Zero]);

        var before = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");
        stored = ClientKeyPem;
        var after = await service.ExchangeCodeAsync("code-2", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.Equal("FAYDA_003", before.Error!.Code);
        Assert.True(after.IsSuccess);
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

    /// <summary>An eSignet-style ID token: identity only, no name or phone.</summary>
    private static string IdToken(System.Security.Cryptography.RSA rsa, string nonce, string sub = "FIN-1111")
    {
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "k1" };
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: Issuer, audience: "client-1",
            claims: [new("sub", sub), new("nonce", nonce)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>eSignet signed userinfo: sub plus the requested claims, no exp.</summary>
    private static string SignedUserinfo(System.Security.Cryptography.RSA rsa, string? phone, string sub = "FIN-1111",
        string iss = Issuer, string aud = "client-1", string alg = Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256)
    {
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "k1" };
        var payload = new System.IdentityModel.Tokens.Jwt.JwtPayload
        {
            { "iss", iss }, { "aud", aud }, { "sub", sub }, { "name", "Abebe Kebede" }
        };
        if (phone != null)
        {
            payload["phone_number"] = phone;
        }
        var header = new System.IdentityModel.Tokens.Jwt.JwtHeader(new Microsoft.IdentityModel.Tokens.SigningCredentials(key, alg));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(header, payload));
    }

    private static HttpResponseMessage Jwt(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/jwt")
    };

    private static ScriptedHandler ProviderIssuing(string idToken, string jwks, string? userinfo = null)
    {
        var handler = ProviderWithDiscovery();
        handler.Routes[JwksPath] = _ => Task.FromResult(Json(jwks));
        handler.Routes[TokenPath] = _ => Task.FromResult(Json("{\"id_token\":\"" + idToken + "\",\"access_token\":\"opaque\"}"));
        if (userinfo != null)
        {
            handler.Routes[UserinfoPath] = request =>
                Task.FromResult(request.Headers.Authorization?.ToString() == "Bearer opaque"
                    ? Jwt(userinfo)
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
        return handler;
    }

    [Fact]
    public async Task ExchangeCode_NonceMismatch_IsRejectedAsForeignAttempt()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "someone-elses-nonce"), jwks, SignedUserinfo(rsa, "+251912345678")));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_002", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_MatchingNonce_ReturnsIdentityAndPhoneFromSignedUserinfo()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1"), jwks, SignedUserinfo(rsa, "+251912345678")));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("FIN-1111", result.Value!.FaydaId);
        Assert.Equal("Abebe Kebede", result.Value.FullName);
        Assert.Equal("+251912345678", result.Value.PhoneNumber);
    }

    [Fact]
    public async Task ExchangeCode_UserinfoForAnotherSubject_IsRejected()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1"), jwks, SignedUserinfo(rsa, "+251912345678", sub: "FIN-9999")));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("alg")]
    public async Task ExchangeCode_UserinfoForeignIssuerAudienceOrAlgorithm_IsRejected(string tamper)
    {
        var (rsa, jwks) = SigningKey();
        var userinfo = tamper switch
        {
            "iss" => SignedUserinfo(rsa, "+251912345678", iss: "https://evil.example"),
            "aud" => SignedUserinfo(rsa, "+251912345678", aud: "another-client"),
            _ => SignedUserinfo(rsa, "+251912345678", alg: Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha512)
        };
        // RS512 with the provider's own key: only the algorithm allow-list rejects it.
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1"), tamper == "alg" ? jwks.Replace("\"alg\":\"RS256\",", "") : jwks, userinfo));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_UserinfoSignedByAnotherKey_IsRejected()
    {
        var (rsa, jwks) = SigningKey();
        var (otherRsa, _) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1"), jwks, SignedUserinfo(otherRsa, "+251912345678")));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_UserinfoDown_ReturnsUnavailable()
    {
        var (rsa, jwks) = SigningKey();
        var handler = ProviderIssuing(IdToken(rsa, "nonce-1"), jwks);
        handler.Routes[UserinfoPath] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Service(handler).ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_003", result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCode_PlainJsonUserinfo_IsAccepted()
    {
        var (rsa, jwks) = SigningKey();
        var handler = ProviderIssuing(IdToken(rsa, "nonce-1"), jwks);
        handler.Routes[UserinfoPath] = _ => Task.FromResult(Json("{\"sub\":\"FIN-1111\",\"name\":\"Sara Haile\",\"phone_number\":\"+251900100003\"}"));

        var result = await Service(handler).ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("Sara Haile", result.Value!.FullName);
        Assert.Equal("+251900100003", result.Value.PhoneNumber);
    }

    [Fact]
    public async Task ExchangeCode_EncryptedUserinfo_ReturnsUnavailable()
    {
        var (rsa, jwks) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(rsa, "nonce-1"), jwks, "a.b.c.d.e"));

        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_003", result.Error!.Code);
    }

    /// <summary>
    /// Live Fayda's layout: discovery only under /v1/esignet (the root answers 404), and
    /// the advertised jwks_uri answers 404 too; the keys sit at the eSignet JWKS path.
    /// </summary>
    [Fact]
    public async Task ExchangeCode_LiveFaydaLayout_FindsDiscoveryAndKeysUnderEsignetPaths()
    {
        var (rsa, jwks) = SigningKey();
        var handler = new ScriptedHandler();
        handler.Routes["/v1/esignet/oidc/.well-known/openid-configuration"] = _ => Task.FromResult(Json(
            "{\"issuer\":\"https://fayda.test\",\"authorization_endpoint\":\"https://fayda.test/authorize\","
            + "\"token_endpoint\":\"https://fayda.test/v1/esignet/oauth/v2/token\","
            + "\"userinfo_endpoint\":\"https://fayda.test/v1/esignet/oidc/userinfo\","
            + "\"jwks_uri\":\"https://fayda.test/.well-known/jwks.json\"}"));
        handler.Routes[JwksPath] = _ => Task.FromResult(Json(jwks));
        handler.Routes[TokenPath] = _ => Task.FromResult(Json("{\"id_token\":\"" + IdToken(rsa, "nonce-1") + "\",\"access_token\":\"opaque\"}"));
        handler.Routes[UserinfoPath] = _ => Task.FromResult(Jwt(SignedUserinfo(rsa, "+251912345678")));
        var service = Service(handler);

        var url = await service.BuildAuthorizationUrlAsync("weredaconnect://fayda-callback", "s", "n", "c");
        var result = await service.ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.StartsWith("https://fayda.test/authorize?", url.Value);
        Assert.True(result.IsSuccess);
        Assert.Equal("+251912345678", result.Value!.PhoneNumber);
    }

    [Fact]
    public async Task ExchangeCode_WithoutDiscovery_UsesEsignetTokenAndUserinfoPaths()
    {
        var (rsa, jwks) = SigningKey();
        var handler = new ScriptedHandler();
        handler.Routes[JwksPath] = _ => Task.FromResult(Json(jwks));
        handler.Routes[TokenPath] = _ => Task.FromResult(Json("{\"id_token\":\"" + IdToken(rsa, "nonce-1") + "\",\"access_token\":\"opaque\"}"));
        handler.Routes[UserinfoPath] = _ => Task.FromResult(Jwt(SignedUserinfo(rsa, null)));

        var result = await Service(handler).ExchangeCodeAsync("code-1", "weredaconnect://fayda-callback", "verifier-1", "nonce-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("Abebe Kebede", result.Value!.FullName);
        Assert.Null(result.Value.PhoneNumber);
    }

    [Fact]
    public async Task ExchangeCode_TokenSignedByAnotherKey_IsRejected()
    {
        var (rsa, jwks) = SigningKey();
        var (otherRsa, _) = SigningKey();
        var service = Service(ProviderIssuing(IdToken(otherRsa, "nonce-1"), jwks, SignedUserinfo(rsa, null)));

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

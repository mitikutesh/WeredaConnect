using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// Runs the mock Fayda eSignet provider (tools/fayda-mock, needs node on PATH) on a
/// free local port for the tests in <see cref="FaydaMockPaymentFlowTests"/>.
/// </summary>
public sealed class FaydaMockProvider : IAsyncLifetime
{
    public const string ClientId = "weredaconnect-flowtest";
    public const string LinkRedirect = "weredaconnect://fayda-link-callback";

    private Process? _process;
    // Stand-in for the /{prefix}/fayda/client-private-key parameter, which both the
    // mock and the server read, exactly as deployed.
    private readonly string _clientKeyFile = Path.Combine(Path.GetTempPath(), $"fayda-client-key-{Guid.NewGuid():N}");

    public string IssuerUrl { get; private set; } = "";

    /// <summary>
    /// The server's read of the client key parameter. Starts as the PLACEHOLDER the
    /// seed script writes; the mock replaces it with a base64 JWK on the first
    /// authorize, the same PRIVATE_KEY_BASE64 form a live Fayda key comes in.
    /// </summary>
    public string ReadClientPrivateKey() => File.ReadAllText(_clientKeyFile);

    public async Task InitializeAsync()
    {
        var mockDir = FindMockDir();
        var port = FreePort();
        IssuerUrl = $"http://localhost:{port}";

        var start = new ProcessStartInfo("node", "server-local.js")
        {
            WorkingDirectory = mockDir,
            UseShellExecute = false
        };
        start.Environment["PORT"] = port.ToString();
        start.Environment["CLIENT_ID"] = ClientId;
        File.WriteAllText(_clientKeyFile, "PLACEHOLDER");
        start.Environment["CLIENT_KEY_FILE"] = _clientKeyFile;
        start.Environment["ALLOWED_REDIRECT_URIS"] = $"weredaconnect://fayda-callback,{LinkRedirect}";
        start.Environment["REQUIRE_PKCE"] = "true";

        try
        {
            _process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException(
                "The Fayda full-flow tests need node (>= 20) on PATH to run tools/fayda-mock.", e);
        }

        using var http = new HttpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var health = await http.GetAsync($"{IssuerUrl}/health");
                if (health.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"fayda-mock did not start on {IssuerUrl}.");
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }
        _process?.Dispose();
        File.Delete(_clientKeyFile);
        return Task.CompletedTask;
    }

    private static string FindMockDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "fayda-mock", "server-local.js");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }
        throw new InvalidOperationException("tools/fayda-mock not found above the test output directory.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Full flow of the payment Fayda check against the mock provider: a real OIDC round
/// trip (authorize → code → token with a private_key_jwt client assertion → RS256 ID
/// token with PKCE and nonce → signed userinfo with the name and phone) through
/// /user/fayda-start and /user/fayda-link, with the buyer's payment submitted to /sync
/// before and after. Only the storage is in memory.
/// Verified as the account's owner → the same payment goes through. Any other
/// identity → the link fails, the account is unchanged and the payment stays blocked.
/// </summary>
// FaydaOidcService caches provider metadata statically: never run beside other provider tests.
[Collection("FaydaOidcProvider")]
public class FaydaMockPaymentFlowTests : IClassFixture<FaydaMockProvider>
{
    // Mock identities (tools/fayda-mock/src/users.js)
    private const string SaraFin = "3333333333333333";   // phone +251900100003 (the buyer's)
    private const string AbebeFin = "1111111111111111";  // phone +251900100001 (the farmer's)
    private const string NoPhoneFin = "5555555555555555";
    private const string SyntheticFin = "9876543210987654"; // synthetic identity, no phone

    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string OrderId = "order-pay";
    private const decimal OrderTotal = 94_500m;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FaydaMockProvider _provider;
    private readonly Dictionary<string, UserDto> _users = new();
    private readonly Dictionary<string, AuthStateEntry> _authState = new();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly FaydaVerifyFunction _fayda;
    private readonly SyncFunction _sync;
    private readonly TestLambdaContext _context = new();

    public FaydaMockPaymentFlowTests(FaydaMockProvider provider)
    {
        _provider = provider;
        FaydaOidcService.ResetCaches();

        _userRepo.GetUserByIdAsync(Arg.Any<string>())
            .Returns(ci => _users.GetValueOrDefault(ci.Arg<string>()));
        _userRepo.GetUserByFaydaIdAsync(Arg.Any<string>())
            .Returns(ci => _users.Values.FirstOrDefault(u => u.FaydaId == ci.Arg<string>()));
        _userRepo.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>()).Returns(ci =>
        {
            var user = ci.Arg<UserDto>();
            var expected = ci.ArgAt<int>(1);
            if (_users[user.UserId].Version != expected)
            {
                return AuthResult<UserDto>.Failure(409, "CONFLICT", "version mismatch");
            }
            var saved = user with { Version = expected + 1 };
            _users[user.UserId] = saved;
            return AuthResult<UserDto>.Success(saved);
        });

        var authState = Substitute.For<IAuthStateRepository>();
        authState.PutAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<int>())
            .Returns(ci =>
            {
                var key = ci.ArgAt<string>(0);
                _authState[key] = new AuthStateEntry
                {
                    Key = key,
                    Attributes = ci.ArgAt<IReadOnlyDictionary<string, string>>(1).ToDictionary(kv => kv.Key, kv => kv.Value),
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ci.ArgAt<int>(2)).ToUnixTimeSeconds()
                };
                return Task.CompletedTask;
            });
        authState.ConsumeAsync(Arg.Any<string>()).Returns(ci =>
            _authState.Remove(ci.Arg<string>(), out var entry) ? entry : null);

        _orderRepo.GetByIdAsync(OrderId, true).Returns(_ => new Dictionary<string, object>
        {
            { "orderId", OrderId },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "status", "created" },
            { "orderTotal", OrderTotal },
            { "version", "1" }
        });
        _orderRepo.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", OrderId },
                { "status", "payment_submitted" },
                { "version", "2" }
            }));

        _fayda = new FaydaVerifyFunction(
            new FaydaOidcService(new HttpClient(), FaydaMockProvider.ClientId, provider.ReadClientPrivateKey,
                provider.IssuerUrl, "flowtest-signing-secret-at-least-32-bytes-long"),
            _userRepo, authState);

        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _sync = new SyncFunction(syncMeta, Substitute.For<ICropRepository>(), Substitute.For<IAmazonDynamoDB>(),
            "test-trips-table", "test-deliveries-table", userRepo: _userRepo, orderRepo: _orderRepo);

        // The dev buyer test user without a Fayda ID yet; the farmer is fully verified.
        _users[BuyerId] = Party(BuyerId, "buyer", "+251900100003");
        _users[FarmerId] = Party(FarmerId, "farmer", "+251900100001") with
        {
            FaydaId = "farmer-fayda-sub",
            FaydaVerified = true,
            FaydaVerifiedAt = "2026-09-01T00:00:00Z"
        };
    }

    private static UserDto Party(string id, string role, string phone) => new()
    {
        UserId = id,
        PhoneNumber = phone,
        Role = role,
        Woreda = "Adama",
        IsPhoneVerified = true,
        CreatedAt = "2026-09-01T00:00:00Z",
        UpdatedAt = "2026-09-01T00:00:00Z",
        Version = 1
    };

    // ── steps ───────────────────────────────────────────

    private async Task<string> SubmitPaymentAsync()
    {
        var sync = new SyncRequest
        {
            SyncBatchId = $"batch-{Guid.NewGuid():N}",
            ClientId = "web-flowtest",
            Operations =
            [
                new SyncOperationRequest
                {
                    OperationId = $"op-{Guid.NewGuid():N}",
                    IdempotencyKey = $"{BuyerId}:order:{OrderId}:update:{Guid.NewGuid():N}",
                    EntityType = "order",
                    EntityId = OrderId,
                    Action = "update",
                    // TELEBIRR_MOCK is unset here, so the live gates run: only the Telebirr rail
                    // is fundable (FUP-20) and a live payment carries no CBE reference (ESC-44).
                    Data = new Dictionary<string, object>
                    {
                        { "status", "payment_submitted" },
                        { "paymentMethod", "telebirr" },
                        { "version", 1 }
                    }
                }
            ]
        };
        var request = new APIGatewayProxyRequest
        {
            Path = "/sync",
            Body = JsonSerializer.Serialize(sync, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", BuyerId },
                    { "custom:role", "buyer" },
                    // A fresh sign-in. Above 10,000 ETB the step-up is the Fayda login
                    // (AUTH_041), which this password time does not satisfy.
                    { "auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
                }
            }
        };
        var response = await _sync.FunctionHandler(request, _context);
        Assert.Equal(200, response.StatusCode);
        return response.Body;
    }

    /// <summary>
    /// What the app does after "Verify with Fayda": fayda-start, the user signs in
    /// as <paramref name="fin"/> at the provider, then fayda-link with the callback.
    /// </summary>
    private Task<APIGatewayProxyResponse> VerifyWithFaydaAsync(string fin) =>
        VerifyAtProviderAsync($"fin={fin}");

    /// <summary>The mock's "Identity for my phone number": that phone's Fayda record.</summary>
    private Task<APIGatewayProxyResponse> VerifyWithFaydaPhoneAsync(string phone) =>
        VerifyAtProviderAsync($"phone={Uri.EscapeDataString(phone)}");

    private async Task<APIGatewayProxyResponse> VerifyAtProviderAsync(string identity)
    {
        var start = await _fayda.FunctionHandler(
            AuthedRequest("/user/fayda-start", new { redirectUri = FaydaMockProvider.LinkRedirect }), _context);
        Assert.Equal(200, start.StatusCode);
        var started = JsonSerializer.Deserialize<FaydaStartResponse>(start.Body, Json)!;

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorize = await browser.GetAsync($"{started.AuthorizeUrl}&auto=1&{identity}");
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        var callback = authorize.Headers.Location!;
        Assert.StartsWith(FaydaMockProvider.LinkRedirect, callback.OriginalString);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(callback.OriginalString).Query);
        Assert.Equal(started.State, query["state"]);

        return await _fayda.FunctionHandler(AuthedRequest("/user/fayda-link", new
        {
            authorizationCode = query["code"],
            state = query["state"],
            redirectUri = FaydaMockProvider.LinkRedirect
        }), _context);
    }

    private static APIGatewayProxyRequest AuthedRequest(string path, object body) => new()
    {
        Path = path,
        Body = JsonSerializer.Serialize(body, Json),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", BuyerId } }
        }
    };

    private void AssertPaymentBlocked(string syncBody, string reason)
    {
        Assert.True(syncBody.Contains($"ORDER_KYC_REQUIRED: {reason}:"), syncBody);
        Assert.DoesNotContain("\"status\":\"success\"", syncBody);
    }

    // ── flows ───────────────────────────────────────────

    [Fact]
    public async Task OwnFaydaIdentity_VerifiesTheAccount_AndThePaymentGoesThrough()
    {
        AssertPaymentBlocked(await SubmitPaymentAsync(), "buyer_fayda");

        var link = await VerifyWithFaydaAsync(SaraFin);

        Assert.Equal(200, link.StatusCode);
        Assert.Contains("Sara Haile", link.Body);
        var buyer = _users[BuyerId];
        Assert.True(buyer.FaydaVerified);
        Assert.False(string.IsNullOrEmpty(buyer.FaydaId));
        Assert.NotEqual(SaraFin, buyer.FaydaId); // sub is a hash of the FIN, never the FIN

        var retry = await SubmitPaymentAsync();
        Assert.Contains("\"status\":\"success\"", retry);
        Assert.DoesNotContain("ORDER_KYC_REQUIRED", retry);
        Assert.DoesNotContain("AUTH_041", retry);
    }

    [Fact]
    public async Task VerifiedBuyer_WithAnOldFaydaLogin_MustLogInWithFaydaAgain_ThenPays()
    {
        Assert.Equal(200, (await VerifyWithFaydaAsync(SaraFin)).StatusCode);
        _users[BuyerId] = _users[BuyerId] with { FaydaAuthenticatedAt = DateTime.UtcNow.AddHours(-1).ToString("o") };

        var blocked = await SubmitPaymentAsync();
        Assert.Contains("AUTH_041:", blocked);
        Assert.DoesNotContain("\"status\":\"success\"", blocked);

        // Same person, fresh Fayda login: the payment goes through without a password prompt.
        Assert.Equal(200, (await VerifyWithFaydaAsync(SaraFin)).StatusCode);
        var retry = await SubmitPaymentAsync();
        Assert.Contains("\"status\":\"success\"", retry);
    }

    [Fact]
    public async Task AnotherPersonsFaydaIdentity_FailsOnPhone_AndThePaymentStaysBlocked()
    {
        var link = await VerifyWithFaydaAsync(AbebeFin);

        Assert.Equal(403, link.StatusCode);
        Assert.Contains("FAYDA_006", link.Body);
        Assert.False(_users[BuyerId].FaydaVerified);
        Assert.Null(_users[BuyerId].FaydaId);
        AssertPaymentBlocked(await SubmitPaymentAsync(), "buyer_fayda");
    }

    [Theory]
    [InlineData(SyntheticFin)]
    [InlineData(NoPhoneFin)]
    public async Task IdentityWithNothingTyingItToTheAccount_FailsClosed(string fin)
    {
        var link = await VerifyWithFaydaAsync(fin);

        Assert.Equal(403, link.StatusCode);
        Assert.Contains("FAYDA_007", link.Body);
        Assert.False(_users[BuyerId].FaydaVerified);
        AssertPaymentBlocked(await SubmitPaymentAsync(), "buyer_fayda");
    }

    [Fact]
    public async Task AccountWithFaydaOnFile_OnlyAcceptsThatSameIdentity()
    {
        Assert.Equal(200, (await VerifyWithFaydaAsync(SaraFin)).StatusCode);
        var boundId = _users[BuyerId].FaydaId;

        // Re-verifying as the owner still works.
        Assert.Equal(200, (await VerifyWithFaydaAsync(SaraFin)).StatusCode);

        // A stranger's identity (even one with no phone to compare) is refused.
        var stranger = await VerifyWithFaydaAsync(SyntheticFin);
        Assert.Equal(403, stranger.StatusCode);
        Assert.Contains("FAYDA_005", stranger.Body);
        Assert.Equal(boundId, _users[BuyerId].FaydaId);
    }

    [Fact]
    public async Task UnverifiedFarmer_BlocksThePayment_EvenAfterTheBuyerVerifies()
    {
        _users[FarmerId] = _users[FarmerId] with { FaydaVerified = false, FaydaId = null };
        Assert.Equal(200, (await VerifyWithFaydaAsync(SaraFin)).StatusCode);

        // The buyer cannot fix this one: the client must not send them to Fayda again.
        AssertPaymentBlocked(await SubmitPaymentAsync(), "farmer_fayda");
    }

    [Fact]
    public async Task AccountWithAnyPhone_GeneratedIdentityFails_PhoneIdentityPasses_AndPaymentGoesThrough()
    {
        // A buyer whose phone matches no seeded mock identity.
        _users[BuyerId] = _users[BuyerId] with { PhoneNumber = "+251911223344" };

        // "Generate new identity": random FIN, no phone → nothing ties it to the account.
        var generated = await VerifyWithFaydaAsync("new");
        Assert.Equal(403, generated.StatusCode);
        Assert.Contains("FAYDA_007", generated.Body);
        AssertPaymentBlocked(await SubmitPaymentAsync(), "buyer_fayda");

        // "Identity for my phone number" with another phone → phone mismatch.
        var otherPhone = await VerifyWithFaydaPhoneAsync("0911000000");
        Assert.Equal(403, otherPhone.StatusCode);
        Assert.Contains("FAYDA_006", otherPhone.Body);

        // The account's own phone (typed in local format) → verified, payment goes through.
        var own = await VerifyWithFaydaPhoneAsync("0911223344");
        Assert.Equal(200, own.StatusCode);
        Assert.True(_users[BuyerId].FaydaVerified);
        Assert.Contains("\"status\":\"success\"", await SubmitPaymentAsync());
    }

    [Fact]
    public async Task PhoneIdentity_VerifiesAgainLater_ButAGeneratedOneDoesNot()
    {
        _users[BuyerId] = _users[BuyerId] with { PhoneNumber = "+251911223344" };
        Assert.Equal(200, (await VerifyWithFaydaPhoneAsync("+251911223344")).StatusCode);
        var boundId = _users[BuyerId].FaydaId;

        // Fayda again at payment time (or from the profile): same phone, same person.
        Assert.Equal(200, (await VerifyWithFaydaPhoneAsync("0911223344")).StatusCode);
        Assert.Equal(boundId, _users[BuyerId].FaydaId);

        // A freshly generated identity is someone else.
        var generated = await VerifyWithFaydaAsync("new");
        Assert.Equal(403, generated.StatusCode);
        Assert.Contains("FAYDA_005", generated.Body);
        Assert.Equal(boundId, _users[BuyerId].FaydaId);
    }

    [Fact]
    public async Task ReplayedCallback_IsRefused()
    {
        var start = await _fayda.FunctionHandler(
            AuthedRequest("/user/fayda-start", new { redirectUri = FaydaMockProvider.LinkRedirect }), _context);
        var started = JsonSerializer.Deserialize<FaydaStartResponse>(start.Body, Json)!;
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var callback = (await browser.GetAsync($"{started.AuthorizeUrl}&auto=1&fin={SaraFin}")).Headers.Location!;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(callback.OriginalString).Query);
        var body = new { authorizationCode = query["code"], state = query["state"], redirectUri = FaydaMockProvider.LinkRedirect };

        Assert.Equal(200, (await _fayda.FunctionHandler(AuthedRequest("/user/fayda-link", body), _context)).StatusCode);
        var replay = await _fayda.FunctionHandler(AuthedRequest("/user/fayda-link", body), _context);

        Assert.Equal(400, replay.StatusCode);
        Assert.Contains("FAYDA_002", replay.Body);
    }
}

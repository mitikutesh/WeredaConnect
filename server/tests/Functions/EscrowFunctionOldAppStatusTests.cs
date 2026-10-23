using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;
using static Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-61 (fraud-detection-architecture.md 18.7): an app before build 2 (no <c>X-App-Build</c>, or a lower
/// one) reads a <c>held</c> escrow as <c>escrowed</c> and a <c>dispute_resolved</c> one as <c>disputed</c> on
/// every escrow read; build 2 and later read the stored value. Only the answer changes: nothing is written.
/// </summary>
public sealed class EscrowFunctionOldAppStatusTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string EscrowId = "escrow-order-order-1";
    private const string DeliveryId = "del-1";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly TestLambdaContext _context = new();

    private EscrowFunction Function() => new(new EscrowService(_escrowRepo, _auditRepo, _idempotencyRepo, _gateway));

    private static readonly (string UserId, string Role)[] Parties =
    {
        (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker")
    };

    /// <summary>Each party, with the build it sends (null: no header) and the status it must read for a held escrow.</summary>
    public static TheoryData<string, string, string?, string> HeldReads()
    {
        var data = new TheoryData<string, string, string?, string>();
        foreach (var (userId, role) in Parties)
        {
            data.Add(userId, role, null, "escrowed");
            data.Add(userId, role, "1", "escrowed");
            data.Add(userId, role, "2", "held");
            data.Add(userId, role, "17", "held");
        }

        return data;
    }

    private static Dictionary<string, object> Row(string status, string escrowId = EscrowId)
    {
        var row = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", DeliveryId },
            { "orderId", "order-1" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "truckerId", TruckerId },
            { "amount", 5_000m },
            { "commission", 125m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 4_375m },
            { "truckerPayout", 500m },
            { "currency", "ETB" },
            { "status", status },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-10-20T10:00:00Z" },
            { "updatedAt", "2026-10-22T10:00:00Z" },
            { "expiresAt", "2026-10-27T10:00:00Z" },
            { "version", 3 }
        };
        if (status == "held")
        {
            row["holdReason"] = "account_suspended";
            row["heldAt"] = "2026-10-22T10:00:00Z";
            row["heldBy"] = "system";
            row["heldFromStatus"] = "escrowed";
            row["heldFor"] = "release";
        }

        return row;
    }

    private static APIGatewayProxyRequest Request(
        string method, string path, string userId, string role, string? build,
        Dictionary<string, string>? pathParams = null, string? body = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId }, { "custom:role", role } };
        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = path,
            PathParameters = pathParams,
            Body = body,
            Headers = build == null ? null : new Dictionary<string, string> { { "X-App-Build", build } },
            RequestContext = new ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static string StatusOf(APIGatewayProxyResponse response)
    {
        response.StatusCode.Should().BeInRange(200, 201, response.Body);
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>Reads only: no update, put or delete on any table, and the stored row still says what it said.</summary>
    private void NothingWritten(Dictionary<string, object> row, string storedStatus)
    {
        row["status"].Should().Be(storedStatus);
        _escrowRepo.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => !name.StartsWith("Get") && !name.StartsWith("Query")).Should().BeEmpty();
        _auditRepo.ReceivedCalls().Should().BeEmpty();
        _idempotencyRepo.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => !name.StartsWith("Get")).Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // The three reads, every party
    // ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(HeldReads))]
    public async Task GetById_HeldEscrow_OldAppsReadEscrowed_NewAppsReadHeld(
        string userId, string role, string? build, string expected)
    {
        var row = Row("held");
        _escrowRepo.GetByIdAsync(EscrowId).Returns(row);

        var response = await Function().FunctionHandler(
            Request("GET", $"/escrow/{EscrowId}", userId, role, build,
                new Dictionary<string, string> { { "id", EscrowId } }), _context);

        StatusOf(response).Should().Be(expected);
        // Party reads never carry the admin-only hold fields, whatever the build.
        response.Body.Should().NotContain("holdReason").And.NotContain("heldFor");
        NothingWritten(row, "held");
    }

    [Theory]
    [MemberData(nameof(HeldReads))]
    public async Task GetByDelivery_HeldEscrow_OldAppsReadEscrowed_NewAppsReadHeld(
        string userId, string role, string? build, string expected)
    {
        var row = Row("held");
        _escrowRepo.GetByDeliveryIdAsync(DeliveryId).Returns(row);

        var response = await Function().FunctionHandler(
            Request("GET", $"/escrow/by-delivery/{DeliveryId}", userId, role, build,
                new Dictionary<string, string> { { "deliveryId", DeliveryId } }), _context);

        StatusOf(response).Should().Be(expected);
        NothingWritten(row, "held");
    }

    [Theory]
    [MemberData(nameof(HeldReads))]
    public async Task History_MapsEachRow_ForOldApps_AndKeepsPartyScoping(
        string userId, string role, string? build, string expectedForHeld)
    {
        var rows = new List<Dictionary<string, object>>
        {
            Row("held", "e-held"),
            Row("dispute_resolved", "e-verdict"),
            Row("escrowed", "e-escrowed"),
            Row("released", "e-released")
        };
        var page = (rows, (Dictionary<string, AttributeValue>?)null);
        _escrowRepo.QueryByBuyerAsync(default!, default, default, default).ReturnsForAnyArgs(page);
        _escrowRepo.QueryByFarmerAsync(default!, default, default, default).ReturnsForAnyArgs(page);
        _escrowRepo.QueryByTruckerAsync(default!, default, default, default).ReturnsForAnyArgs(page);

        var response = await Function().FunctionHandler(
            Request("GET", "/escrow/history", userId, role, build), _context);

        response.StatusCode.Should().Be(200, response.Body);
        using var doc = JsonDocument.Parse(response.Body);
        var statuses = doc.RootElement.GetProperty("escrows").EnumerateArray()
            .ToDictionary(e => e.GetProperty("escrowId").GetString()!, e => e.GetProperty("status").GetString());
        var oldApp = expectedForHeld == "escrowed";
        statuses.Should().Equal(new Dictionary<string, string?>
        {
            { "e-held", expectedForHeld },
            { "e-verdict", oldApp ? "disputed" : "dispute_resolved" },
            { "e-escrowed", "escrowed" },
            { "e-released", "released" }
        });

        // Each role still reads its own index with its own id.
        switch (role)
        {
            case "buyer":
                await _escrowRepo.Received(1).QueryByBuyerAsync(BuyerId, null, 20, null);
                break;
            case "farmer":
                await _escrowRepo.Received(1).QueryByFarmerAsync(FarmerId, null, 20, null);
                break;
            default:
                await _escrowRepo.Received(1).QueryByTruckerAsync(TruckerId, null, 20, null);
                break;
        }

        rows.Select(r => r["status"]).Should().Equal("held", "dispute_resolved", "escrowed", "released");
        NothingWritten(rows[0], "held");
    }

    [Fact]
    public async Task History_StatusFilter_StillMatchesTheStoredValue()
    {
        _escrowRepo.QueryByBuyerAsync(BuyerId, "held", 50, null)
            .Returns((new List<Dictionary<string, object>> { Row("held") }, (Dictionary<string, AttributeValue>?)null));
        var request = Request("GET", "/escrow/history", BuyerId, "buyer", build: null);
        request.QueryStringParameters = new Dictionary<string, string> { { "status", "held" }, { "limit", "50" } };

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"status\":\"escrowed\"");
        await _escrowRepo.Received(1).QueryByBuyerAsync(BuyerId, "held", 50, null);
    }

    /// <summary>What a shipped app really sends: headers, none of them X-App-Build.</summary>
    [Theory]
    [InlineData(BuyerId, "buyer")]
    [InlineData(FarmerId, "farmer")]
    [InlineData(TruckerId, "trucker")]
    public async Task OtherHeaders_WithoutTheBuild_AreAnOldApp(string userId, string role)
    {
        var row = Row("held");
        _escrowRepo.GetByIdAsync(EscrowId).Returns(row);
        var request = Request("GET", $"/escrow/{EscrowId}", userId, role, build: null,
            new Dictionary<string, string> { { "id", EscrowId } });
        request.Headers = new Dictionary<string, string>
        {
            { "Authorization", "Bearer header.payload.signature" },
            { "Content-Type", "application/json" },
            { "Accept", "application/json" },
            { "X-Device-Id", "0f8fad5b-d9cb-469f-a165-70867728950e" },
            { "X-App-Version", "9" }
        };

        var response = await Function().FunctionHandler(request, _context);

        ClientAppBuild.Read(request).Should().BeNull();
        StatusOf(response).Should().Be("escrowed");
        NothingWritten(row, "held");
    }

    // ──────────────────────────────────────────────
    // Other statuses
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, "disputed")]
    [InlineData("1", "disputed")]
    [InlineData("2", "dispute_resolved")]
    public async Task GetById_DisputeResolved_OldAppsReadDisputed(string? build, string expected)
    {
        var row = Row("dispute_resolved");
        _escrowRepo.GetByIdAsync(EscrowId).Returns(row);

        var response = await Function().FunctionHandler(
            Request("GET", $"/escrow/{EscrowId}", FarmerId, "farmer", build,
                new Dictionary<string, string> { { "id", EscrowId } }), _context);

        StatusOf(response).Should().Be(expected);
        NothingWritten(row, "dispute_resolved");
    }

    [Theory]
    [InlineData("initiated")]
    [InlineData("escrowed")]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("disputed")]
    [InlineData("refunded")]
    public async Task GetById_AnyOtherStatus_IsTheStoredValue_ForOldAndNewApps(string status)
    {
        foreach (var build in new string?[] { null, "1", "2" })
        {
            var row = Row(status);
            _escrowRepo.GetByIdAsync(EscrowId).Returns(row);

            var response = await Function().FunctionHandler(
                Request("GET", $"/escrow/{EscrowId}", BuyerId, "buyer", build,
                    new Dictionary<string, string> { { "id", EscrowId } }), _context);

            StatusOf(response).Should().Be(status, $"build {build ?? "none"}");
        }
    }

    // ──────────────────────────────────────────────
    // Replays that re-read the row
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, "escrowed")]
    [InlineData("1", "escrowed")]
    [InlineData("2", "held")]
    public async Task InitiateReplay_OfAnEscrowHeldSince_IsMappedForOldApps(string? build, string expected)
    {
        const string key = BuyerId + ":" + DeliveryId + ":initiate:1708084800000";
        var row = Row("held");
        _idempotencyRepo.GetAsync(key).Returns(new Dictionary<string, object> { { "escrowId", EscrowId } });
        _escrowRepo.GetByIdAsync(EscrowId).Returns(row);
        var body = JsonSerializer.Serialize(new InitiateEscrowRequest
        {
            DeliveryId = DeliveryId,
            BuyerId = BuyerId,
            FarmerId = FarmerId,
            Amount = 5_000m,
            Currency = "ETB",
            IdempotencyKey = key
        }, AppJsonContext.Default.InitiateEscrowRequest);
        var request = Request("POST", "/escrow/initiate", BuyerId, "buyer", build, body: body);
        request.RequestContext.Authorizer["auth_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200, response.Body);
        StatusOf(response).Should().Be(expected);
        NothingWritten(row, "held");
        await _gateway.DidNotReceiveWithAnyArgs().HoldFundsAsync(default!);
    }

    [Theory]
    [InlineData(null, "disputed")]
    [InlineData("2", "dispute_resolved")]
    public async Task DisputeReplay_AfterTheVerdict_IsMappedForOldApps(string? build, string expected)
    {
        const string key = BuyerId + ":" + EscrowId + ":dispute:1708084800000";
        var row = Row("dispute_resolved");
        row["disputeId"] = "dsp-1";
        row["disputedBy"] = BuyerId;
        row["disputeReason"] = "damaged_goods";
        row["disputeWindowEndsAt"] = "2026-10-22T10:00:00Z";
        row["disputeCreatedAt"] = "2026-10-21T10:00:00Z";
        _idempotencyRepo.GetAsync(key)
            .Returns(new Dictionary<string, object> { { "escrowId", EscrowId }, { "status", "completed" } });
        _escrowRepo.GetByIdAsync(EscrowId).Returns(row);
        var body = JsonSerializer.Serialize(new DisputeEscrowRequest
        {
            EscrowId = EscrowId,
            DisputedBy = BuyerId,
            Reason = "damaged_goods",
            IdempotencyKey = key
        }, AppJsonContext.Default.DisputeEscrowRequest);

        var response = await Function().FunctionHandler(
            Request("POST", "/escrow/dispute", BuyerId, "buyer", build, body: body), _context);

        response.StatusCode.Should().Be(200, response.Body);
        StatusOf(response).Should().Be(expected);
        NothingWritten(row, "dispute_resolved");
    }

    // ──────────────────────────────────────────────
    // The header
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("X-App-Build", "2", 2)]
    [InlineData("x-app-build", " 5 ", 5)]
    [InlineData("X-App-Build", "0", null)]
    [InlineData("X-App-Build", "-3", null)]
    [InlineData("X-App-Build", "2.0", null)]
    [InlineData("X-App-Build", "abc", null)]
    [InlineData("X-App-Build", "", null)]
    [InlineData("X-App-Build", "99999999999", null)]
    [InlineData("X-App-Version", "2", null)]
    public void ClientAppBuild_ReadsOnlyAPositiveWholeNumber(string header, string value, int? expected)
    {
        var request = new APIGatewayProxyRequest { Headers = new Dictionary<string, string> { { header, value } } };

        ClientAppBuild.Read(request).Should().Be(expected);
        EscrowStatusCompat.ShowsStoredStatus(request).Should().Be(expected >= 2);
    }

    [Fact]
    public void ClientAppBuild_NoHeaders_IsAnOldApp()
    {
        var request = new APIGatewayProxyRequest();

        ClientAppBuild.Read(request).Should().BeNull();
        EscrowStatusCompat.ShowsStoredStatus(request).Should().BeFalse();
    }
}

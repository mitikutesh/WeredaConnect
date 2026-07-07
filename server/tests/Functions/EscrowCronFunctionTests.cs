using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class EscrowCronFunctionTests
{
    private readonly IEscrowRepository _mockEscrowRepo;
    private readonly IEscrowAuditRepository _mockAuditRepo;
    private readonly IEscrowIdempotencyRepository _mockIdempotencyRepo;
    private readonly ITelebirrGateway _mockTelebirrGateway;
    private readonly EscrowCronFunction _function;
    private readonly TestLambdaContext _context;

    public EscrowCronFunctionTests()
    {
        _mockEscrowRepo = Substitute.For<IEscrowRepository>();
        _mockAuditRepo = Substitute.For<IEscrowAuditRepository>();
        _mockIdempotencyRepo = Substitute.For<IEscrowIdempotencyRepository>();
        _mockTelebirrGateway = Substitute.For<ITelebirrGateway>();

        // Settlement lock acquired by default so the cron's release/refund path proceeds.
        _mockEscrowRepo
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "MOCK-RELEASE-REF" });
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = true, TelebirrRef = "MOCK-REFUND-REF" });
        _mockTelebirrGateway.GetTransactionStatusAsync(Arg.Any<string>())
            .Returns(new TransactionStatusResponse
            {
                TransactionRef = "ref",
                Status = "completed",
                TelebirrRef = "MOCK-STATUS-REF",
                Amount = 0,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        _function = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway);
        _context = new TestLambdaContext();
    }

    private static APIGatewayProxyRequest MakeCronRequest(string action)
    {
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", action } })
        };
    }

    private static Dictionary<string, object> MakeEscrowItem(
        string escrowId,
        string status = "escrowed",
        string? deliveryConfirmedAt = null,
        string? disputeWindowEndsAt = null,
        string? expiresAt = null,
        string? telebirrRef = null)
    {
        var item = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", $"del-{escrowId}" },
            { "buyerId", "buyer-456" },
            { "farmerId", "farmer-123" },
            { "amount", 15_000m },
            { "commission", 375m },
            { "commissionRate", 0.025m },
            { "farmerPayout", 14_625m },
            { "currency", "ETB" },
            { "status", status },
            { "telebirrHoldRef", "MOCK-HOLD-REF" },
            { "buyerFaydaVerified", false },
            { "farmerFaydaVerified", false },
            { "createdAt", "2026-02-10T10:00:00.0000000Z" },
            { "updatedAt", "2026-02-10T10:00:00.0000000Z" },
            { "expiresAt", expiresAt ?? "2026-02-24T10:00:00.0000000Z" },
            { "version", 1 }
        };

        if (deliveryConfirmedAt != null)
        {
            item["deliveryConfirmedAt"] = deliveryConfirmedAt;
        }

        if (disputeWindowEndsAt != null)
        {
            item["disputeWindowEndsAt"] = disputeWindowEndsAt;
        }

        if (telebirrRef != null)
        {
            item["telebirrRef"] = telebirrRef;
        }

        return item;
    }

    // ──────────────────────────────────────────────
    // EventBridge payload shape (regression guard for the cron silent no-op, Review C3)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_EventBridgePayloadShape_DispatchesAction()
    {
        // EventBridge sends {"body":"{...action...}"}. Verify that exact shape deserializes into
        // APIGatewayProxyRequest.Body via the Lambda source-gen serializer AND that the handler
        // reads the action and dispatches (returns 200, not a 400 no-op). Prior to the fix the
        // action sat at the event root, Body was null, and every trigger silently 400'd.
        _mockEscrowRepo
            .QueryByStatusForCronAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        const string eventJson =
            "{\"body\":\"{\\\"action\\\":\\\"auto_release_and_refund\\\"}\"}";
        var request = JsonSerializer.Deserialize(
            eventJson, WeredaConnect.Server.AppJsonContext.Default.APIGatewayProxyRequest);

        Assert.NotNull(request);
        Assert.False(string.IsNullOrEmpty(request!.Body));

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Auto-Release
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_EligibleEscrow_ReleasesSuccessfully()
    {
        var pastWindow = DateTime.UtcNow.AddHours(-49).ToString("o");
        var deliveryConfirmed = DateTime.UtcNow.AddHours(-50).ToString("o");

        var escrow = MakeEscrowItem("escrow-release-001",
            deliveryConfirmedAt: deliveryConfirmed,
            disputeWindowEndsAt: pastWindow);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        // The release will look up the escrow by ID
        _mockEscrowRepo.GetByIdAsync("escrow-release-001").Returns(escrow);

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // Assert the escrow was actually released (count = 1), not just that the key exists.
        Assert.Contains("\"released\":\"1\"", response.Body);
    }

    [Fact]
    public async Task AutoRelease_DisputeWindowOpen_DoesNotRelease()
    {
        // Dispute window ends in the future
        var futureWindow = DateTime.UtcNow.AddHours(24).ToString("o");
        var deliveryConfirmed = DateTime.UtcNow.AddHours(-20).ToString("o");

        var escrow = MakeEscrowItem("escrow-no-release",
            deliveryConfirmedAt: deliveryConfirmed,
            disputeWindowEndsAt: futureWindow);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"released\":\"0\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Auto-Refund
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRefund_ExpiredEscrow_RefundsSuccessfully()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");

        var escrow = MakeEscrowItem("escrow-refund-001", expiresAt: expiredAt);

        // First query (for auto-release) returns empty
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        // Second query (for auto-refund with expiresAtBefore) returns the expired escrow
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        _mockEscrowRepo.GetByIdAsync("escrow-refund-001").Returns(escrow);

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task AutoRefund_NoExpiredEscrows_NoOp()
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"released\":\"0\"", response.Body);
        Assert.Contains("\"refunded\":\"0\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Crash-recovery: released → completed
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompleteReleased_StuckInReleasedState_CompletesSuccessfully()
    {
        var escrow = MakeEscrowItem("escrow-stuck-released", status: "released");
        escrow["releasedAt"] = DateTime.UtcNow.AddMinutes(-5).ToString("o");
        escrow["telebirrReleaseRef"] = "MOCK-RELEASE-REF";

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("released", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"completed\":\"1\"", response.Body);
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-stuck-released",
            "2026-02-10T10:00:00.0000000Z",
            "completed",
            "released",
            Arg.Any<Dictionary<string, object>>());
        await _mockAuditRepo.Received(1).AppendAsync(
            Arg.Is<Dictionary<string, object>>(d =>
                d.ContainsKey("action") && d["action"].ToString() == "complete" &&
                d.ContainsKey("fromState") && d["fromState"].ToString() == "released" &&
                d.ContainsKey("toState") && d["toState"].ToString() == "completed"));
    }

    [Fact]
    public async Task CompleteReleased_NoStuckEscrows_NoOp()
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("released", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"completed\":\"0\"", response.Body);
    }

    [Fact]
    public async Task CompleteReleased_UpdateFails_LogsErrorAndContinues()
    {
        var escrow1 = MakeEscrowItem("escrow-stuck-001", status: "released");
        var escrow2 = MakeEscrowItem("escrow-stuck-002", status: "released");

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("released", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow1, escrow2 });

        // First escrow update throws (e.g. conditional check failure due to race)
        _mockEscrowRepo.UpdateStatusAsync("escrow-stuck-001", Arg.Any<string>(), "completed", "released", Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new Amazon.DynamoDBv2.Model.ConditionalCheckFailedException("Already updated"));

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        // Should not 500 — continues and completes the second escrow
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"completed\":\"1\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Reconciliation
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_StuckInitiated_WithTelebirrConfirm_UpdatesToEscrowed()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrow = MakeEscrowItem("escrow-recon-001", status: "initiated",
            telebirrRef: "TBR-REF-001");
        escrow["createdAt"] = staleCreatedAt;

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"reconciledToEscrowed\":\"1\"", response.Body);
    }

    [Fact]
    public async Task Reconciliation_StuckInitiated_NoTelebirrRef_CancelsEscrow()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrow = MakeEscrowItem("escrow-recon-002", status: "initiated");
        escrow["createdAt"] = staleCreatedAt;
        // No telebirrRef → should be cancelled

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"reconciledToCancelled\":\"1\"", response.Body);

        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-recon-002", staleCreatedAt, "cancelled", "initiated", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task Reconciliation_RecentlyInitiated_Skips()
    {
        // Created just 5 minutes ago — too recent to reconcile
        var recentCreatedAt = DateTime.UtcNow.AddMinutes(-5).ToString("o");
        var escrow = MakeEscrowItem("escrow-recon-003", status: "initiated",
            telebirrRef: "TBR-REF-003");
        escrow["createdAt"] = recentCreatedAt;

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"skipped\":\"1\"", response.Body);
        Assert.Contains("\"reconciledToEscrowed\":\"0\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Action routing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_MissingAction_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = null
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_UnknownAction_Returns400()
    {
        var request = MakeCronRequest("unknown_action");

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
    }

    // ──────────────────────────────────────────────
    // S5: Invalid JSON body returns 400
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_InvalidJsonBody_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = "{{this is not valid json"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_EmptyBody_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = ""
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
        Assert.Contains("action", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_JsonWithoutAction_Returns400()
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = "{\"notAction\": \"something\"}"
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
    }

    // ──────────────────────────────────────────────
    // S5: Unknown action returns specific error (not catch-all)
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("delete_all_data")]
    [InlineData("")]
    [InlineData("reconcile")]
    public async Task FunctionHandler_InvalidActionStrings_Returns400(string action)
    {
        var request = MakeCronRequest(action);

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ESCROW_CRON_VALIDATION", response.Body);
    }

    // ──────────────────────────────────────────────
    // Action from query string
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_ActionInQueryString_Routes()
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());

        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/escrow-cron",
            Body = null,
            QueryStringParameters = new Dictionary<string, string>
            {
                { "action", "auto_release_and_refund" }
            }
        };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Reconciliation: Telebirr returns "failed"
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_TelebirrFailed_CancelsEscrow()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrow = MakeEscrowItem("escrow-recon-004", status: "initiated",
            telebirrRef: "TBR-FAIL-REF");
        escrow["createdAt"] = staleCreatedAt;

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        _mockTelebirrGateway.GetTransactionStatusAsync("TBR-FAIL-REF")
            .Returns(new TransactionStatusResponse
            {
                TransactionRef = "TBR-FAIL-REF",
                Status = "failed",
                Amount = 0,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"reconciledToCancelled\":\"1\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Reconciliation: Telebirr query throws exception
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_TelebirrQueryFails_SkipsEscrow()
    {
        var staleCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o");
        var escrow = MakeEscrowItem("escrow-recon-005", status: "initiated",
            telebirrRef: "TBR-EXCEPTION-REF");
        escrow["createdAt"] = staleCreatedAt;

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        _mockTelebirrGateway.GetTransactionStatusAsync("TBR-EXCEPTION-REF")
            .ThrowsAsync(new Exception("Network error"));

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"skipped\":\"1\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // S4: Cron exception logs don't leak sensitive data
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FunctionHandler_CronException_Returns500WithGenericMessage()
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("ESCROW_CRON_ERROR", response.Body);
        // Response should NOT contain the sensitive exception message
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", response.Body);
    }

    // ──────────────────────────────────────────────
    // Auto-release: delivery not confirmed, skip
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_DeliveryNotConfirmed_DoesNotRelease()
    {
        // No deliveryConfirmedAt set
        var escrow = MakeEscrowItem("escrow-no-confirm");

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"released\":\"0\"", response.Body);
    }

    // ──────────────────────────────────────────────
    // Auto-refund: with confirmed delivery, skip
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRefund_WithConfirmedDelivery_DoesNotRefund()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var confirmed = DateTime.UtcNow.AddDays(-2).ToString("o");

        var escrow = MakeEscrowItem("escrow-confirmed-expired",
            expiresAt: expiredAt,
            deliveryConfirmedAt: confirmed);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // Delivery confirmed → should not be auto-refunded
        Assert.Contains("\"refunded\":\"0\"", response.Body);
    }
}

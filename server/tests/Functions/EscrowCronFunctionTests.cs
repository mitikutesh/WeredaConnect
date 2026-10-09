using System.Text.Json;
using Amazon.DynamoDBv2.Model;
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
            .TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
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

        // Default: every status sweep sees an empty page (tests override the ones they exercise).
        _mockEscrowRepo.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        // Default: the manual payout queue is empty.
        _mockEscrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));

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
    // Manual payout queue (ESC-02)
    // ──────────────────────────────────────────────

    private static ReleaseFundsResponse PayoutNotSupportedRelease() => new()
    {
        Success = false,
        ErrorCode = TelebirrGatewayErrors.PayoutNotSupported,
        ErrorMessage = "not integrated"
    };

    [Fact]
    public async Task AutoRelease_ManualPayoutWaiting_IsCountedNotFailed_AndNothingSettles()
    {
        var pastWindow = DateTime.UtcNow.AddHours(-49).ToString("o");
        var deliveryConfirmed = DateTime.UtcNow.AddHours(-50).ToString("o");
        var escrow = MakeEscrowItem("escrow-q-1", deliveryConfirmedAt: deliveryConfirmed, disputeWindowEndsAt: pastWindow);
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-q-1").Returns(escrow);
        _mockEscrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(true);
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(PayoutNotSupportedRelease());

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"released\":\"0\"", response.Body);
        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        await _mockEscrowRepo.Received(1).TryQueueManualPayoutAsync(
            "escrow-q-1", Arg.Any<string>(), "release", "PAYOUT_NOT_SUPPORTED", "escrowed",
            Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>());
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _mockEscrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AutoRelease_AlreadyQueuedEscrow_SecondRunIsQuiet()
    {
        var pastWindow = DateTime.UtcNow.AddHours(-49).ToString("o");
        var deliveryConfirmed = DateTime.UtcNow.AddHours(-50).ToString("o");
        var queued = MakeEscrowItem("escrow-q-2", deliveryConfirmedAt: deliveryConfirmed, disputeWindowEndsAt: pastWindow);
        queued["settlementLock"] = "release";
        queued[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        queued[ManualPayoutFields.Op] = "release";
        queued[ManualPayoutFields.QueuedAt] = DateTime.UtcNow.AddHours(-1).ToString("o");
        queued[ManualPayoutFields.StatusAttr("farmer")] = "pending";
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { queued });
        _mockEscrowRepo.GetByIdAsync("escrow-q-2").Returns(queued);

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockEscrowRepo.DidNotReceive().TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task AutoRefund_OrderFunded_PayoutNotSupported_CancelsOrderRestoresStockThenQueuesRefund()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-9", "ord-9", expiresAt: expiredAt);
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-order-ord-9").Returns(escrow);
        _mockEscrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(true);
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported });

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-9").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-9" }, { "status", "funded" }, { "version", "2" }, { "listingId", "crop-9" }, { "quantity", "500" }
        });
        orderRepo.UpdateAsync("ord-9", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "ord-9" }, { "status", "cancelled" }, { "version", "3" }
            }));
        var cropRepo = Substitute.For<ICropRepository>();

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway,
            orderRepo: orderRepo, cropRepo: cropRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"refunded\":\"0\"", response.Body);
        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        // The order side effects still run first; only the money waits for the admin.
        await orderRepo.Received(1).UpdateAsync("ord-9",
            Arg.Is<Dictionary<string, object>>(d => (string)d["status"] == "cancelled"), 2);
        await cropRepo.ReceivedWithAnyArgs(1).TryRestoreStockAsync(default!, default!, default);
        await _mockEscrowRepo.Received(1).TryQueueManualPayoutAsync(
            "escrow-order-ord-9", Arg.Any<string>(), "refund", "PAYOUT_NOT_SUPPORTED", "escrowed",
            Arg.Is<IReadOnlyList<ManualPayoutLegSeed>>(l => l.Count == 1 && l[0].Leg == "buyer"), Arg.Any<string>());
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), "refunded", Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
    }

    [Fact]
    public async Task AutoRefund_AlreadyQueuedOrderFundedRefund_SkipsOrderCancelRestockAndService()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-9", "ord-9", expiresAt: expiredAt);
        escrow["settlementLock"] = "refund";
        escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRefund;
        escrow[ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending;
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        var orderRepo = Substitute.For<IOrderRepository>();
        var cropRepo = Substitute.For<ICropRepository>();
        var deliveryRepo = Substitute.For<IDeliveryRepository>();

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway,
            deliveryRepo: deliveryRepo, orderRepo: orderRepo, cropRepo: cropRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        await orderRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await orderRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await cropRepo.DidNotReceiveWithAnyArgs().TryRestoreStockAsync(default!, default!, default);
        await deliveryRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await _mockEscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task AutoRelease_150QueuedRowsAhead_FreshEscrowStillReleased()
    {
        // Review finding: queued escrows keep status escrowed and their old expiresAt, so they
        // sat at the head of the oldest-first StatusExpiresIndex window (limit 100) and no new
        // escrow was ever released. With the real repository over a DynamoDB that applies
        // Limit before the filter, the fresh escrow behind 150 queued rows is released.
        var pastWindow = DateTime.UtcNow.AddHours(-49).ToString("o");
        var index = Enumerable.Range(0, 150)
            .Select(n => new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = $"escrow-queued-{n:D3}" } },
                { "createdAt", new AttributeValue { S = "2026-09-01T00:00:00Z" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = $"2026-09-01T00:{n / 60:D2}:{n % 60:D2}Z" } },
                { "deliveryConfirmedAt", new AttributeValue { S = pastWindow } },
                { "disputeWindowEndsAt", new AttributeValue { S = pastWindow } },
                { "settlementLock", new AttributeValue { S = "release" } },
                { "manualPayout", new AttributeValue { S = "open" } }
            })
            .ToList();
        var fresh = new Dictionary<string, AttributeValue>
        {
            { "escrowId", new AttributeValue { S = "escrow-fresh" } },
            { "createdAt", new AttributeValue { S = "2026-10-01T00:00:00Z" } },
            { "deliveryId", new AttributeValue { S = "del-fresh" } },
            { "buyerId", new AttributeValue { S = "buyer-1" } },
            { "farmerId", new AttributeValue { S = "farmer-1" } },
            { "status", new AttributeValue { S = "escrowed" } },
            { "amount", new AttributeValue { N = "1000" } },
            { "commission", new AttributeValue { N = "25" } },
            { "farmerPayout", new AttributeValue { N = "975" } },
            { "telebirrHoldRef", new AttributeValue { S = "HOLD-FRESH" } },
            { "expiresAt", new AttributeValue { S = "2026-10-08T00:00:00Z" } },
            { "deliveryConfirmedAt", new AttributeValue { S = pastWindow } },
            { "disputeWindowEndsAt", new AttributeValue { S = pastWindow } },
            { "version", new AttributeValue { N = "1" } }
        };
        index.Add(fresh);
        var statusQueries = new List<QueryRequest>();
        var db = Substitute.For<Amazon.DynamoDBv2.IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var r = call.Arg<QueryRequest>();
            if (r.IndexName == "StatusExpiresIndex")
            {
                statusQueries.Add(r);
                if (r.ExpressionAttributeValues[":status"].S != "escrowed" || r.KeyConditionExpression.Contains("expiresAt"))
                {
                    return new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() };
                }

                var start = r.ExclusiveStartKey is { Count: > 0 } key
                    ? index.FindIndex(i => i["escrowId"].S == key["escrowId"].S) + 1
                    : 0;
                var evaluated = index.Skip(start).Take(r.Limit).ToList();
                return new QueryResponse
                {
                    Items = evaluated.Where(i => !(r.FilterExpression ?? "").Contains("attribute_not_exists(#manualPayout)")
                                                 || !i.ContainsKey("manualPayout")).ToList(),
                    LastEvaluatedKey = start + evaluated.Count < index.Count
                        ? new Dictionary<string, AttributeValue> { { "escrowId", evaluated[^1]["escrowId"] } }
                        : new Dictionary<string, AttributeValue>()
                };
            }

            if (r.IndexName == null && r.ExpressionAttributeValues[":id"].S == "escrow-fresh")
            {
                return new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { fresh } };
            }

            return new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() };
        });
        db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        var repo = new EscrowRepository(db, "escrows");
        _mockIdempotencyRepo.StoreAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
        var service = new EscrowService(repo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowCronFunction(service, repo, _mockAuditRepo, _mockTelebirrGateway);

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(
            Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:escrow-fresh"));
        Assert.Contains("\"released\":\"1\"", response.Body);
        // The ESC-05 lock recovery reads the same partition with its own filter (on #lock).
        var releaseSweep = statusQueries
            .Where(q => q.ExpressionAttributeValues[":status"].S == "escrowed" && !q.KeyConditionExpression.Contains("expiresAt")
                        && !q.FilterExpression.Contains("#lock"))
            .ToList();
        Assert.Equal(2, releaseSweep.Count); // the first page held only queued rows; paging went on
        Assert.All(statusQueries, q => Assert.Contains("attribute_not_exists(#manualPayout)", q.FilterExpression));
    }

    // ──────────────────────────────────────────────
    // ESC-02 producer of the ESC-13 MANUAL_PAYOUT_OVERDUE alarm
    // ──────────────────────────────────────────────

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static Dictionary<string, object> QueuedRow(
        string escrowId, double hoursAgo, string op = "release", params (string Leg, string Status)[] legs)
    {
        var row = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "createdAt", "2026-10-01T10:00:00.0000000Z" },
            { "status", "escrowed" },
            { ManualPayoutFields.Marker, ManualPayoutFields.MarkerOpen },
            { ManualPayoutFields.Op, op },
            { ManualPayoutFields.QueuedAt, DateTime.UtcNow.AddHours(-hoursAgo).ToString("o") }
        };
        foreach (var (leg, status) in legs)
        {
            row[ManualPayoutFields.StatusAttr(leg)] = status;
        }

        return row;
    }

    private void StubQueue(params (List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? Next)[] pages)
    {
        var first = pages[0];
        var rest = pages.Skip(1).ToArray();
        _mockEscrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(first, rest);
    }

    [Fact]
    public async Task ManualPayoutOverdue_LogsOneLinePerPendingLegOlderThan24h()
    {
        StubQueue((new List<Dictionary<string, object>>
        {
            QueuedRow("escrow-old", 26.5, "release", ("farmer", "pending"), ("trucker", "paid")),
            QueuedRow("escrow-young", 2, "refund", ("buyer", "pending"))
        }, null));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("MANUAL_PAYOUT_OVERDUE escrowId=escrow-old ref=release:farmer ageHours=26", Logs);
        Assert.DoesNotContain("ref=release:trucker", Logs);
        Assert.DoesNotContain("escrowId=escrow-young", Logs);
        Assert.Contains("\"manualPayoutsOverdue\":\"1\"", response.Body);
    }

    [Fact]
    public async Task ManualPayoutOverdue_EveryPendingLegGetsALine()
    {
        StubQueue((new List<Dictionary<string, object>>
        {
            QueuedRow("escrow-old", 30, "release", ("farmer", "pending"), ("trucker", "pending"))
        }, null));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("MANUAL_PAYOUT_OVERDUE escrowId=escrow-old ref=release:farmer ageHours=30", Logs);
        Assert.Contains("MANUAL_PAYOUT_OVERDUE escrowId=escrow-old ref=release:trucker ageHours=30", Logs);
        Assert.Contains("\"manualPayoutsOverdue\":\"2\"", response.Body);
    }

    [Fact]
    public async Task ManualPayoutOverdue_OpenRowWithoutPendingLeg_StillLogsALine()
    {
        // Every leg paid but the marker is still open: the admin page cannot close it.
        StubQueue((new List<Dictionary<string, object>>
        {
            QueuedRow("escrow-stuck", 40, "refund", ("buyer", "paid"))
        }, null));

        await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("MANUAL_PAYOUT_OVERDUE escrowId=escrow-stuck ref=refund:no_pending_leg ageHours=40", Logs);
    }

    [Fact]
    public async Task ManualPayoutOverdue_PagesWhileTheOldestAreOverdue_StopsAtTheFirstYoungRow()
    {
        var next = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "escrow-a" } } };
        StubQueue(
            (new List<Dictionary<string, object>> { QueuedRow("escrow-a", 50, "refund", ("buyer", "pending")) }, next),
            (new List<Dictionary<string, object>>
            {
                QueuedRow("escrow-b", 25, "refund", ("buyer", "pending")),
                QueuedRow("escrow-c", 1, "refund", ("buyer", "pending"))
            }, next));

        await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("escrowId=escrow-a ref=refund:buyer", Logs);
        Assert.Contains("escrowId=escrow-b ref=refund:buyer", Logs);
        Assert.DoesNotContain("escrowId=escrow-c", Logs);
        // Two pages read; the young row ended the report before a third page.
        await _mockEscrowRepo.Received(2).QueryOpenManualPayoutsAsync(50, Arg.Any<Dictionary<string, AttributeValue>?>());
        await _mockEscrowRepo.Received(1).QueryOpenManualPayoutsAsync(50, next);
    }

    [Fact]
    public async Task ManualPayoutOverdue_QueryFails_LogsSettlementFailedAndTheSweepsStillRun()
    {
        _mockEscrowRepo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new InvalidOperationException("index missing"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=manual_payout_query:InvalidOperationException",
            Logs);
        await _mockEscrowRepo.Received().QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ManualPayoutOverdue_RunsEvenWhenALaterSweepThrows()
    {
        StubQueue((new List<Dictionary<string, object>>
        {
            QueuedRow("escrow-old", 26, "refund", ("buyer", "pending"))
        }, null));
        _mockEscrowRepo.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new InvalidOperationException("query failed"));

        await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("MANUAL_PAYOUT_OVERDUE escrowId=escrow-old ref=refund:buyer ageHours=26", Logs);
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
    public async Task AutoRefund_DeliveredButUnstampedEscrow_IsNotRefunded_AndGetsStamped()
    {
        // Money-safety (review B): if the inline escrow stamp at delivery
        // confirmation failed transiently, an escrow can reach expiry with an
        // empty deliveryConfirmedAt even though the delivery WAS confirmed. The
        // refund sweep must verify the delivery's real status and NEVER refund a
        // delivered order — instead it stamps the escrow so the release cron pays
        // the farmer.
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeEscrowItem("refund-safety-001", expiresAt: expiredAt); // no deliveryConfirmedAt

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        // The linked delivery was actually confirmed (delivered).
        var mockDeliveryRepo = Substitute.For<IDeliveryRepository>();
        mockDeliveryRepo.GetByIdAsync("del-refund-safety-001").Returns(new Dictionary<string, object>
        {
            { "deliveryId", "del-refund-safety-001" },
            { "status", "delivered" },
            { "confirmedAt", DateTime.UtcNow.AddHours(-1).ToString("o") }
        });

        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowCronFunction(
            service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway, mockDeliveryRepo);

        var request = MakeCronRequest("auto_release_and_refund");
        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        // No money returned to the buyer for a delivered order.
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        // Escrow stamped so the auto-release cron will pay the farmer.
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "refund-safety-001", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                f.ContainsKey("deliveryConfirmedAt") && f.ContainsKey("disputeWindowEndsAt")));
    }

    /// <summary>Escrow item for the order-funded leg (source=order + orderId).</summary>
    private static Dictionary<string, object> MakeOrderSourcedEscrowItem(
        string escrowId, string orderId, string? expiresAt = null)
    {
        var item = MakeEscrowItem(escrowId, expiresAt: expiresAt);
        item.Remove("deliveryId"); // unlinked until a delivery is created
        item["source"] = "order";
        item["orderId"] = orderId;
        return item;
    }

    private EscrowCronFunction MakeOrderAwareFunction(IOrderRepository? orderRepo, ICropRepository? cropRepo = null)
    {
        var service = new EscrowService(
            _mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        return new EscrowCronFunction(
            service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway,
            deliveryRepo: null, orderRepo: orderRepo, cropRepo: cropRepo);
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedProgressingOrder_SkipsRefundAndExtendsExpiry()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-1", "ord-1", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-1").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-1" }, { "status", "in_transit" }, { "version", "2" }
        });

        var function = MakeOrderAwareFunction(orderRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        // The trade is live: never refund; push the expiry forward instead.
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockEscrowRepo.Received(1).UpdateStatusAsync(
            "escrow-order-ord-1", Arg.Any<string>(), "escrowed", "escrowed",
            Arg.Is<Dictionary<string, object>>(f =>
                DateTime.Parse((string)f["expiresAt"], null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal) > DateTime.UtcNow));
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedStaleFundedOrder_RefundsAndCancelsOrder()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-2", "ord-2", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-order-ord-2").Returns(escrow);

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-2").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-2" }, { "status", "funded" }, { "version", "2" }
        });
        orderRepo.UpdateAsync("ord-2", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "ord-2" }, { "status", "cancelled" }, { "version", "3" }
            }));

        var function = MakeOrderAwareFunction(orderRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        // Farmer never responded within the expiry window: the stale-funded order is
        // cancelled FIRST (the CAS is the race fence), then the buyer gets the money.
        await orderRepo.Received(1).UpdateAsync("ord-2",
            Arg.Is<Dictionary<string, object>>(d =>
                (string)d["status"] == "cancelled"
                && (string)d["cancelReason"] == "escrow_refunded"),
            2);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedStaleFundedOrder_RestoresListingStock()
    {
        // SIM-13: the cancelled order's quintals go back on the listing (kg / 100).
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-3", "ord-3", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-order-ord-3").Returns(escrow);

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-3").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-3" }, { "listingId", "crop-3" }, { "quantity", 500.0 },
            { "status", "funded" }, { "version", "2" }
        });
        orderRepo.UpdateAsync("ord-3", Arg.Any<Dictionary<string, object>>(), 2)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", "ord-3" }, { "status", "cancelled" }, { "version", "3" }
            }));
        var cropRepo = Substitute.For<ICropRepository>();
        cropRepo.TryRestoreStockAsync("crop-3", "ord-3", 5.0).Returns(true);

        var function = MakeOrderAwareFunction(orderRepo, cropRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        await cropRepo.Received(1).TryRestoreStockAsync("crop-3", "ord-3", 5.0);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedCancelConflict_SkipsRefund()
    {
        // A farmer accepting in the read→refund window bumps the order version;
        // the CAS cancel conflicts and the refund must NOT proceed this sweep.
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-5", "ord-5", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-5").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-5" }, { "status", "funded" }, { "version", "2" }
        });
        orderRepo.UpdateAsync("ord-5", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Version conflict."));

        var function = MakeOrderAwareFunction(orderRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedRejectedOrder_RefundsWithoutTouchingOrder()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-3", "ord-3", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-order-ord-3").Returns(escrow);

        var orderRepo = Substitute.For<IOrderRepository>();
        orderRepo.GetByIdAsync("ord-3").Returns(new Dictionary<string, object>
        {
            { "orderId", "ord-3" }, { "status", "rejected" }, { "version", "3" }
        });

        var function = MakeOrderAwareFunction(orderRepo);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        // Rejected is already terminal — the order must not be rewritten.
        await orderRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task AutoRefund_OrderSourcedWithoutOrderRepo_SkipsRefund()
    {
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-4", "ord-4", expiresAt: expiredAt);

        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var function = MakeOrderAwareFunction(orderRepo: null);
        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        // Fail-safe: the order can't be checked, so the money stays put.
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
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
        // ESC-02: keyed on the completedAt it wrote, the key a manual payout replay re-ensures.
        var completedAt = (string)((Dictionary<string, object>)_mockEscrowRepo.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IEscrowRepository.UpdateStatusAsync))
            .GetArguments()[4]!)["completedAt"];
        await _mockAuditRepo.Received(1).AppendIfAbsentAsync(
            Arg.Is<Dictionary<string, object>>(d =>
                d.ContainsKey("action") && d["action"].ToString() == "complete" &&
                d.ContainsKey("fromState") && d["fromState"].ToString() == "released" &&
                d.ContainsKey("toState") && d["toState"].ToString() == "completed" &&
                (string)d["timestamp"] == completedAt &&
                (string)d["auditId"] == EscrowAuditRepository.DeterministicAuditId("complete", completedAt)));
        await _mockAuditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
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
    public async Task Reconciliation_StuckInitiated_NoTelebirrRef_PastPaymentWindow_CancelsEscrow()
    {
        // Older than the no-ref grace window (150m) with no ref → the buyer never
        // completed payment, so it is safe to cancel.
        var staleCreatedAt = DateTime.UtcNow.AddHours(-3).ToString("o");
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
    public async Task Reconciliation_StuckInitiated_NoTelebirrRef_WithinPaymentWindow_Skips()
    {
        // Async H5/C2B: a no-ref escrow inside the payment window (120m timeout) is
        // simply awaiting the buyer's payment — the webhook will flip it to escrowed.
        // Reconciliation must NOT cancel it: cancelling here and then receiving the
        // webhook would strand a real payment in "cancelled" (webhook CAS fails).
        var recentCreatedAt = DateTime.UtcNow.AddHours(-1).ToString("o"); // 60m < 150m grace
        var escrow = MakeEscrowItem("escrow-recon-006", status: "initiated");
        escrow["createdAt"] = recentCreatedAt;
        // No telebirrRef (payment not completed yet).

        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });

        var request = MakeCronRequest("reconciliation");
        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"skipped\":\"1\"", response.Body);
        Assert.Contains("\"reconciledToCancelled\":\"0\"", response.Body);

        // Must NOT cancel — the buyer may still be paying.
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            "escrow-recon-006", Arg.Any<string>(), "cancelled", "initiated", Arg.Any<Dictionary<string, object>?>());
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
        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var request = MakeCronRequest("reconciliation");
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

    // ──────────────────────────────────────────────
    // Resolved disputes are settled by the sweep
    // ──────────────────────────────────────────────

    private Dictionary<string, object> ResolvedDispute(string escrowId, string resolution) => new()
    {
        { "escrowId", escrowId },
        { "deliveryId", "del-dr" },
        { "buyerId", "buyer-dr" },
        { "farmerId", "farmer-dr" },
        { "amount", 8000m },
        { "commission", 200m },
        { "farmerPayout", 7800m },
        { "currency", "ETB" },
        { "status", "dispute_resolved" },
        { "disputeResolution", resolution },
        { "telebirrHoldRef", "HOLD-dr" },
        { "createdAt", "2026-09-20T10:00:00.0000000Z" },
        { "expiresAt", "2026-09-27T10:00:00.0000000Z" },
        { "version", 3 }
    };

    private void SweepFinds(Dictionary<string, object> escrow)
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("dispute_resolved", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync((string)escrow["escrowId"]).Returns(escrow);
    }

    [Fact]
    public async Task ResolvedDispute_FarmerWins_IsReleased()
    {
        SweepFinds(ResolvedDispute("escrow-dr-1", "farmer_wins"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"disputesSettled\":\"1\"", response.Body);
        await _mockTelebirrGateway.Received(1).ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.Amount == 7800m));
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    [Fact]
    public async Task ResolvedDispute_BuyerWins_IsRefunded()
    {
        SweepFinds(ResolvedDispute("escrow-dr-2", "buyer_wins"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"disputesSettled\":\"1\"", response.Body);
        await _mockTelebirrGateway.Received(1).RefundFundsAsync(Arg.Any<RefundFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
    }

    [Fact]
    public async Task ResolvedDispute_PartialRefund_IsLeftForAdmin()
    {
        SweepFinds(ResolvedDispute("escrow-dr-3", "partial_refund"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("\"disputesSettled\":\"0\"", response.Body);
        await _mockTelebirrGateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _mockTelebirrGateway.DidNotReceive().RefundFundsAsync(Arg.Any<RefundFundsRequest>());
    }

    // ──────────────────────────────────────────────
    // ESC-13: money alarm lines from the sweeps
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_ExceptionBeforeTheLock_LogsSettlementFailedFailed()
    {
        var escrow = MakeEscrowItem("escrow-boom",
            deliveryConfirmedAt: DateTime.UtcNow.AddHours(-50).ToString("o"),
            disputeWindowEndsAt: DateTime.UtcNow.AddHours(-49).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-boom")
            .ThrowsAsync(new InvalidOperationException("Sensitive DynamoDB ARN info"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-boom action=auto_release_exception outcome=failed code=InvalidOperationException",
            Logs);
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", Logs);
    }

    [Fact]
    public async Task AutoRefund_ExceptionBeforeTheLock_LogsSettlementFailedFailed()
    {
        var escrow = MakeEscrowItem("escrow-refund-boom", expiresAt: DateTime.UtcNow.AddDays(-1).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-refund-boom").ThrowsAsync(new TimeoutException("boom"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-refund-boom action=auto_refund_exception outcome=failed code=TimeoutException",
            Logs);
    }

    [Fact]
    public async Task ResolvedDispute_ExceptionBeforeTheLock_LogsSettlementFailedFailed()
    {
        var escrow = ResolvedDispute("escrow-dr-boom", "farmer_wins");
        _mockEscrowRepo.QueryByStatusForCronAsync("dispute_resolved", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-dr-boom").ThrowsAsync(new TimeoutException("boom"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-dr-boom action=dispute_settlement_exception outcome=failed code=TimeoutException",
            Logs);
    }

    [Fact]
    public async Task FunctionHandler_CronException_LogsSweepFailedToken()
    {
        // ESC-05: the hourly sweeps catch their own query failures now, so the run that dies is
        // the reconciliation, and it settles nothing: a definite failure.
        _mockEscrowRepo.QueryByStatusForCronAsync("initiated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var response = await _function.FunctionHandler(MakeCronRequest("reconciliation"), _context);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=reconciliation:Exception",
            Logs);
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", Logs);
    }

    [Fact]
    public async Task SweepQueryFailures_LogADefiniteFailure_AndTheRestOfTheRunGoesOn()
    {
        // ESC-05 (O5): the release, refund and crash-recovery queries are wrapped like the dispute
        // query, so one failing query skips only its own sweep.
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));
        _mockEscrowRepo.QueryByStatusForCronAsync("released", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new TimeoutException("boom"));
        var resolved = ResolvedDispute("escrow-dr-ok", "farmer_wins");
        _mockEscrowRepo.QueryByStatusForCronAsync("dispute_resolved", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { resolved });
        _mockEscrowRepo.GetByIdAsync("escrow-dr-ok").Returns(resolved);

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=release_query:Exception", Logs);
        Assert.Contains("ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=refund_query:Exception", Logs);
        Assert.Contains("ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=complete_query:TimeoutException", Logs);
        Assert.Contains("\"disputesSettled\":\"1\"", response.Body);
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", Logs);
    }

    [Fact]
    public async Task DisputeQueryFailure_LogsSweepFailedToken()
    {
        _mockEscrowRepo.QueryByStatusForCronAsync("dispute_resolved", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        // The run is skipped (200), no gateway call happened, so the outcome is a definite failure.
        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=dispute_query:Exception",
            Logs);
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", Logs);
    }

    // ──────────────────────────────────────────────
    // ESC-05: the stale lock recovery step
    // ──────────────────────────────────────────────

    private static Dictionary<string, object> StaleLock(string escrowId, string op, double minutesAgo)
    {
        var item = MakeEscrowItem(escrowId);
        item[SettlementLockFields.Lock] = op;
        item[SettlementLockFields.LockAt] = DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("o");
        item[SettlementLockFields.LockId] = $"id-{escrowId}";
        return item;
    }

    [Fact]
    public async Task Recovery_ParksStaleLocks_BeforeTheSweeps_AndCountsThem()
    {
        var stale = StaleLock("escrow-stale-1", "release", 40);
        _mockEscrowRepo.QueryStaleSettlementLocksAsync("escrowed", Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { stale });
        _mockEscrowRepo.TryQueueStaleSettlementAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(true);

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"staleLocksParked\":\"1\"", response.Body);
        await _mockEscrowRepo.Received(1).TryQueueStaleSettlementAsync("escrow-stale-1", Arg.Any<string>(), "release",
            "escrowed", Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), "id-escrow-stale-1", Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceiveWithAnyArgs().ReleaseFundsAsync(default!);
        var calls = _mockEscrowRepo.ReceivedCalls().Select(c => c.GetMethodInfo().Name).ToList();
        Assert.True(calls.LastIndexOf(nameof(IEscrowRepository.QueryStaleSettlementLocksAsync))
                    < calls.IndexOf(nameof(IEscrowRepository.QueryByStatusForCronAsync)));
        foreach (var status in new[] { "escrowed", "dispute_resolved", "disputed" })
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            await _mockEscrowRepo.Received(1).QueryStaleSettlementLocksAsync(status,
                Arg.Is<string>(s => Math.Abs((DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() - cutoff).TotalSeconds) < 60),
                Arg.Any<int>());
        }
    }

    [Fact]
    public async Task Recovery_QueryFailure_LogsADefiniteFailure_AndTheRunGoesOn()
    {
        _mockEscrowRepo.QueryStaleSettlementLocksAsync("escrowed", Arg.Any<string>(), Arg.Any<int>())
            .ThrowsAsync(new Exception("Sensitive DynamoDB ARN info"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=lock_recovery_query:escrowed:Exception", Logs);
        await _mockEscrowRepo.Received(1).QueryStaleSettlementLocksAsync("dispute_resolved", Arg.Any<string>(), Arg.Any<int>());
        Assert.DoesNotContain("Sensitive DynamoDB ARN info", Logs);
    }

    [Fact]
    public async Task Recovery_ParkThatThrows_LogsAnUnknownRecoveryFailure()
    {
        _mockEscrowRepo.QueryStaleSettlementLocksAsync("dispute_resolved", Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { StaleLock("escrow-stale-2", "refund", 90) });
        _mockEscrowRepo.TryQueueStaleSettlementAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .ThrowsAsync(new TimeoutException("boom"));
        _mockEscrowRepo.GetByIdAsync("escrow-stale-2", true).ThrowsAsync(new TimeoutException("boom"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-stale-2 action=stale_lock_recovery_failed outcome=unknown code=TimeoutException", Logs);
        Assert.Contains("\"staleLocksParked\":\"0\"", response.Body);
    }

    [Fact]
    public async Task AutoRefund_LockedRowWithoutTheQueueMarker_IsSkipped_BeforeAnyDeliveryOrOrderWrite()
    {
        // A settlement holds this escrow (in flight, or parked while this index copy still lacks
        // the marker): no delivery stamp, no order cancel, no restock, no refund attempt.
        var expiredAt = DateTime.UtcNow.AddDays(-1).ToString("o");
        var escrow = MakeOrderSourcedEscrowItem("escrow-order-ord-11", "ord-11", expiresAt: expiredAt);
        escrow[SettlementLockFields.Lock] = "refund";
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        var orderRepo = Substitute.For<IOrderRepository>();
        var cropRepo = Substitute.For<ICropRepository>();
        var deliveryRepo = Substitute.For<IDeliveryRepository>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway);
        var function = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway,
            deliveryRepo: deliveryRepo, orderRepo: orderRepo, cropRepo: cropRepo);

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("ESCROW_SETTLEMENT_IN_FLIGHT_SKIPPED escrowId=escrow-order-ord-11 sweep=auto-refund", Logs);
        await deliveryRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await orderRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default!);
        await orderRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await cropRepo.DidNotReceiveWithAnyArgs().TryRestoreStockAsync(default!, default!, default);
        await _mockEscrowRepo.DidNotReceive().UpdateStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Dictionary<string, object>?>());
        await _mockEscrowRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await _mockTelebirrGateway.DidNotReceiveWithAnyArgs().RefundFundsAsync(default!);
    }

    [Fact]
    public async Task AutoRelease_ExceptionAfterTheGatewayPaid_LogsSettlementFailedUnknown()
    {
        // ESC-05: the released write never lands, so the exception escapes after the lock.
        var escrow = MakeEscrowItem("escrow-paid-boom",
            deliveryConfirmedAt: DateTime.UtcNow.AddHours(-50).ToString("o"),
            disputeWindowEndsAt: DateTime.UtcNow.AddHours(-49).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-paid-boom").Returns(escrow);
        _mockEscrowRepo.UpdateStatusAsync("escrow-paid-boom", Arg.Any<string>(), "released", Arg.Any<string?>(),
                Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new Amazon.DynamoDBv2.AmazonDynamoDBException("down"));

        var response = await _function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "ESCROW_SETTLEMENT_FAILED escrowId=escrow-paid-boom action=auto_release_exception outcome=unknown code=AmazonDynamoDBException",
            Logs);
        await _mockEscrowRepo.DidNotReceive().ReleaseSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AutoRelease_GatewayFailure_TokenComesFromServiceOnly()
    {
        var escrow = MakeEscrowItem("escrow-release-001",
            deliveryConfirmedAt: DateTime.UtcNow.AddHours(-50).ToString("o"),
            disputeWindowEndsAt: DateTime.UtcNow.AddHours(-49).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-release-001").Returns(escrow);
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED" });
        var serviceLines = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway)
        {
            AlarmLog = serviceLines.Add
        };
        var function = new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway);

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        // One failed leg, one line: the service logs it, the cron's failure-result branch does not add a second.
        Assert.Equal(200, response.StatusCode);
        Assert.Single(serviceLines);
        Assert.Contains("Auto-release failed for escrow-release-001: ESCROW_013", Logs);
        Assert.DoesNotContain("ESCROW_SETTLEMENT_FAILED", Logs);
    }

    // A payout the gateway cannot make waits in the manual payout queue (202 ESCROW_034):
    // not a settlement failure, so no sweep and no service line may count it.

    private (EscrowCronFunction Function, List<string> ServiceLines) FunctionCapturingServiceAlarms()
    {
        var serviceLines = new List<string>();
        var service = new EscrowService(_mockEscrowRepo, _mockAuditRepo, _mockIdempotencyRepo, _mockTelebirrGateway)
        {
            AlarmLog = serviceLines.Add
        };
        return (new EscrowCronFunction(service, _mockEscrowRepo, _mockAuditRepo, _mockTelebirrGateway), serviceLines);
    }

    private void QueueWritesSucceed()
        => _mockEscrowRepo.TryQueueManualPayoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ManualPayoutLegSeed>>(), Arg.Any<string>())
            .Returns(true);

    [Fact]
    public async Task AutoRelease_PayoutQueued_LogsNoSettlementFailure()
    {
        var escrow = MakeEscrowItem("escrow-q-rel",
            deliveryConfirmedAt: DateTime.UtcNow.AddHours(-50).ToString("o"),
            disputeWindowEndsAt: DateTime.UtcNow.AddHours(-49).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync("escrow-q-rel").Returns(escrow);
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(PayoutNotSupportedRelease());
        QueueWritesSucceed();
        var (function, serviceLines) = FunctionCapturingServiceAlarms();

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        Assert.Empty(serviceLines);
        Assert.Contains("ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-q-rel sweep=auto-release", Logs);
        Assert.DoesNotContain("ESCROW_SETTLEMENT_FAILED", Logs);
    }

    [Fact]
    public async Task ResolvedDispute_RefundQueued_LogsNoSettlementFailure()
    {
        SweepFinds(ResolvedDispute("escrow-dr-q", "buyer_wins"));
        _mockTelebirrGateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>())
            .Returns(new RefundFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.PayoutNotSupported });
        QueueWritesSucceed();
        var (function, serviceLines) = FunctionCapturingServiceAlarms();

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        Assert.Empty(serviceLines);
        Assert.Contains("ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-dr-q sweep=dispute refund", Logs);
        Assert.DoesNotContain("ESCROW_SETTLEMENT_FAILED", Logs);
    }

    [Fact]
    public async Task AutoRefund_AlreadyQueued_LogsNoSettlementFailure()
    {
        var escrow = MakeEscrowItem("escrow-q-ref", expiresAt: DateTime.UtcNow.AddDays(-1).ToString("o"));
        escrow["settlementLock"] = "refund";
        escrow[ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen;
        escrow[ManualPayoutFields.Op] = ManualPayoutFields.OpRefund;
        escrow[ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending;
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", expiresAtBefore: Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        var (function, serviceLines) = FunctionCapturingServiceAlarms();

        var response = await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        Assert.Contains("\"manualPayoutsWaiting\":\"1\"", response.Body);
        Assert.Empty(serviceLines);
        Assert.Contains("ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-q-ref sweep=auto-refund", Logs);
        Assert.DoesNotContain("ESCROW_SETTLEMENT_FAILED", Logs);
    }

    // An order-funded escrow id embeds the client-chosen order id, and gateway messages may
    // echo it: none of that may spell a token in this log group.

    [Fact]
    public async Task HostileOrderId_InTheEscrowIdAndMessages_LogsOnlyTheRealTokens()
    {
        var hostile = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.Hostile;
        var escrowId = $"escrow-order-{hostile}";
        var escrow = MakeEscrowItem(escrowId,
            deliveryConfirmedAt: DateTime.UtcNow.AddHours(-50).ToString("o"),
            disputeWindowEndsAt: DateTime.UtcNow.AddHours(-49).ToString("o"));
        _mockEscrowRepo.QueryByStatusForCronAsync("escrowed", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { escrow });
        _mockEscrowRepo.GetByIdAsync(escrowId).Returns(escrow);
        _mockTelebirrGateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "TELEBIRR_RELEASE_FAILED", ErrorMessage = $"rejected release:{escrowId}" });
        StubQueue((new List<Dictionary<string, object>> { QueuedRow(escrowId, 26, "refund", ("buyer", "pending")) }, null));
        var (function, serviceLines) = FunctionCapturingServiceAlarms();

        await function.FunctionHandler(MakeCronRequest("auto_release_and_refund"), _context);

        var tokens = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens;
        Assert.Single(serviceLines);
        Assert.Equal(new[] { "ESCROW_SETTLEMENT_FAILED" }, tokens.Where(serviceLines[0].Contains));
        // The overdue line is the only token in the cron log: one per line, never one from the id.
        var cronLines = Logs.Split(new[] { (char)10 }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(cronLines, l => l.Contains("Auto-release failed for escrow-order-ESCROW-SETTLEMENT-FAILED"));
        Assert.All(cronLines, l => Assert.True(
            tokens.Count(l.Contains) == (l.Contains("MANUAL_PAYOUT_OVERDUE escrowId=") ? 1 : 0), l));
    }
}

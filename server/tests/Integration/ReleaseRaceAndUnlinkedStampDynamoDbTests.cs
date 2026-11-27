using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-65 and FUP-66 against real DynamoDB (LocalStack in CI).
/// FUP-65: the hourly crash recovery completes a <c>released</c> row, so it can win the
/// <c>completed</c> write of a release still running. The two writers are driven in the losing order
/// (a hook just before the release's own <c>completed</c> write runs the recovery), and the release
/// answers its normal success with one payout. FUP-66: an order escrow whose link to its delivery
/// never landed (no <c>deliveryId</c>, so the <c>DeliveryIndex</c> does not know it) is stamped at
/// the delivery confirmation, and the refund sweep never refunds it once its delivery is delivered.
/// The tables are shared with other suites, so every assertion is about one row and the gateway
/// calls are counted per escrow.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReleaseRaceAndUnlinkedStampDynamoDbTests
{
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LocalDynamoDbFixture _db;
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly string _buyerId = $"buyer-{Guid.NewGuid():N}";
    private readonly string _farmerId = $"farmer-{Guid.NewGuid():N}";
    private readonly string _truckerId = $"trucker-{Guid.NewGuid():N}";

    /// <summary>Gateway calls per transaction reference (release:{id}, refund:{id}).</summary>
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public ReleaseRaceAndUnlinkedStampDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(call =>
        {
            var reference = call.Arg<ReleaseFundsRequest>().TransactionRef;
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            return new ReleaseFundsResponse { Success = true, TelebirrRef = $"GW-{reference}" };
        });
        _gateway.RefundFundsAsync(Arg.Any<RefundFundsRequest>()).Returns(call =>
        {
            var reference = call.Arg<RefundFundsRequest>().TransactionRef;
            _calls.AddOrUpdate(reference, 1, (_, n) => n + 1);
            return new RefundFundsResponse { Success = true, TelebirrRef = $"GW-{reference}" };
        });
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = (string)ci[0],
            PhoneNumber = "+251900100000",
            DisplayName = "Party Name",
            Role = (string)ci[0] == _buyerId ? "buyer" : (string)ci[0] == _farmerId ? "farmer" : "trucker",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository EscrowRepo => new(Ddb, _db.Escrows);
    private EscrowAuditRepository AuditRepo => new(Ddb, _db.EscrowAudit);
    private OrderRepository OrderRepo => new(Ddb, _db.Orders);
    private DeliveryRepository DeliveryRepo => new(Ddb, _db.Deliveries);
    private CropRepository CropRepo => new(Ddb, _db.Crops);
    private TransactionAuditRepository TxRepo => new(Ddb, _db.TransactionAudit);

    private EscrowService Service(IEscrowRepository? escrowRepo = null) => new(
        escrowRepo ?? EscrowRepo, AuditRepo, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), _gateway,
        userRepo: null, OrderRepo, DeliveryRepo, orderCheckoutRepo: null, transactionAuditRepo: TxRepo, cropRepo: CropRepo);

    private EscrowCronFunction Cron(TimeSpan? grace = null, IEscrowRepository? escrowRepo = null)
    {
        var repo = escrowRepo ?? EscrowRepo;
        var cron = new EscrowCronFunction(Service(repo), repo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo);
        return grace == null ? cron : new EscrowCronFunction(Service(repo), repo, AuditRepo, _gateway, DeliveryRepo, OrderRepo, CropRepo)
        {
            ReleasedRecoveryGrace = grace.Value
        };
    }

    private static async Task<Dictionary<string, string>> SweepAsync(EscrowCronFunction cron)
    {
        var response = await cron.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"auto_release_and_refund\"}" }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    private int Calls(string reference) => _calls.TryGetValue(reference, out var n) ? n : 0;

    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(string value) => new() { N = value };
    private static string Ago(double minutes) => DateTime.UtcNow.AddMinutes(-minutes).ToString("o");

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private async Task<Dictionary<string, object>> EscrowAsync(string id) => (await EscrowRepo.GetByIdAsync(id, true))!;

    private async Task<List<Dictionary<string, object>>> AuditAsync(string escrowId) => await AuditRepo.GetAuditTrailAsync(escrowId);

    // ──────────────────────────────────────────────
    // FUP-65: the release against the crash recovery
    // ──────────────────────────────────────────────

    /// <summary>An <c>/escrow/initiate</c> escrow of a delivered delivery, waiting to be released.</summary>
    private async Task<string> SeedReleasableEscrowAsync(string status = "escrowed", string? releasedAt = null)
    {
        var escrowId = $"escrow-fup65-{Guid.NewGuid():N}";
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S($"del-{escrowId}"),
                ["tripId"] = S($"trip-{escrowId}"),
                ["status"] = S("delivered")
            }
        });
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(Ago(1)),
            ["status"] = S(status),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["amount"] = N("8000"),
            ["commission"] = N("200"),
            ["farmerPayout"] = N("7800"),
            ["currency"] = S("ETB"),
            ["telebirrHoldRef"] = S("HOLD-FUP65"),
            ["expiresAt"] = S(DateTime.UtcNow.AddDays(5).ToString("o")),
            ["deliveryId"] = S($"del-{escrowId}"),
            ["deliveryConfirmedAt"] = S(Ago(60 * 20)),
            // The window is still open, so the auto-release sweep of the recovery run leaves it alone:
            // only the admin's call releases it.
            ["disputeWindowEndsAt"] = S(DateTime.UtcNow.AddHours(28).ToString("o")),
            ["version"] = N("3")
        };
        if (releasedAt != null)
        {
            item["releasedAt"] = S(releasedAt);
            item["telebirrReleaseRef"] = S($"GW-release:{escrowId}");
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Escrows, Item = item });
        return escrowId;
    }

    private static ReleaseEscrowRequest AdminRelease(string escrowId, long key) => new()
    {
        EscrowId = escrowId,
        Trigger = "admin_override",
        DeliveryId = "",
        Reason = "Delivery confirmed by phone with both parties.",
        IdempotencyKey = $"admin-fup65:{escrowId}:release:{key}"
    };

    private Task<EscrowResult<ReleaseEscrowResponse>> ReleaseAsAdminAsync(EscrowService service, ReleaseEscrowRequest request)
        => service.ReleaseAsync(request, "admin-fup65", "admin", adminGroupVerified: true);

    [LocalDynamoDbFact]
    public async Task AdminRelease_TheRecoveryCompletesTheRowFirst_AnswersCompleted_WithOnePayout()
    {
        var escrowId = await SeedReleasableEscrowAsync();
        var reference = $"release:{escrowId}";
        var recoveryRuns = 0;
        Dictionary<string, string>? recovery = null;
        // The losing order: the release wrote `released` (money sent); just before its own `completed`
        // write the hourly recovery runs and completes the row. A recovery without the grace, as before
        // FUP-65, so the answer of the release is proven on its own.
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, newStatus) =>
            {
                if (id == escrowId && newStatus == "completed" && recoveryRuns++ == 0)
                {
                    recovery = SweepAsync(Cron(grace: TimeSpan.Zero)).GetAwaiter().GetResult();
                }

                return null;
            }
        };
        var request = AdminRelease(escrowId, 1);

        var result = await ReleaseAsAdminAsync(Service(racing), request);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        recoveryRuns.Should().Be(1);
        int.Parse(recovery!["completed"]).Should().BeGreaterThanOrEqualTo(1, "the recovery won the completed write");
        result.Value!.Status.Should().Be("completed");
        result.Value.TelebirrRef.Should().Be($"GW-{reference}");
        Calls(reference).Should().Be(1);

        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("completed");
        Str(row, "telebirrReleaseRef").Should().Be($"GW-{reference}");
        Str(row, "settlementLock").Should().Be("release");
        var audit = await AuditAsync(escrowId);
        audit.Count(a => (string)a["action"] == "release").Should().Be(1);
        audit.Where(a => (string)a["action"] == "complete").Should().ContainSingle()
            .Which["metadata"].Should().Be("crash_recovery");

        // The replay of the same call answers the same success, and nothing pays again.
        var replay = await ReleaseAsAdminAsync(Service(), request);
        replay.IsSuccess.Should().BeTrue();
        replay.IsDuplicate.Should().BeTrue();
        replay.Value!.Status.Should().Be("completed");
        // A new call for the settled escrow is refused as for any completed escrow, before the gateway.
        var otherKey = await ReleaseAsAdminAsync(Service(), AdminRelease(escrowId, 2));
        otherKey.Error!.Code.Should().Be("ESCROW_INVALID_TRANSITION");
        await SweepAsync(Cron(grace: TimeSpan.Zero));
        Calls(reference).Should().Be(1);
        (await AuditAsync(escrowId)).Count(a => (string)a["action"] == "complete").Should().Be(1);
    }

    [LocalDynamoDbFact]
    public async Task AdminRelease_TheRecoveryRunsInBetween_WithTheGrace_LeavesTheRowToItsRelease()
    {
        var escrowId = await SeedReleasableEscrowAsync();
        var reference = $"release:{escrowId}";
        var recoveryRuns = 0;
        string? statusSeenByTheRecovery = null;
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, newStatus) =>
            {
                if (id == escrowId && newStatus == "completed" && recoveryRuns++ == 0)
                {
                    SweepAsync(Cron()).GetAwaiter().GetResult();
                    statusSeenByTheRecovery = Str(EscrowAsync(escrowId).GetAwaiter().GetResult(), "status");
                }

                return null;
            }
        };

        var result = await ReleaseAsAdminAsync(Service(racing), AdminRelease(escrowId, 1));

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        recoveryRuns.Should().Be(1);
        statusSeenByTheRecovery.Should().Be("released", "a row released seconds ago is not the recovery's to complete");
        result.Value!.Status.Should().Be("completed");
        Calls(reference).Should().Be(1);
        Str(await EscrowAsync(escrowId), "status").Should().Be("completed");
        // The release completed it itself: its own complete row, none from the recovery.
        (await AuditAsync(escrowId)).Where(a => (string)a["action"] == "complete").Should().ContainSingle()
            .Which.ContainsKey("metadata").Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task Recovery_ARowReleasedLongAgo_IsCompleted_AndOneReleasedJustNow_IsNot()
    {
        var stuck = await SeedReleasableEscrowAsync("released", releasedAt: Ago(30));
        var fresh = await SeedReleasableEscrowAsync("released", releasedAt: Ago(0.2));

        await SweepAsync(Cron());

        Str(await EscrowAsync(stuck), "status").Should().Be("completed");
        (await AuditAsync(stuck)).Should().ContainSingle(a => (string)a["action"] == "complete");
        Str(await EscrowAsync(fresh), "status").Should().Be("released");
        (await AuditAsync(fresh)).Should().BeEmpty();
        Calls($"release:{stuck}").Should().Be(0, "the recovery never calls the gateway");
    }

    [LocalDynamoDbFact]
    public async Task Recovery_TheReleaseCompletesTheRowBetweenItsQueryAndItsWrite_WritesNothing()
    {
        var escrowId = await SeedReleasableEscrowAsync("released", releasedAt: Ago(30));
        var completedByTheRelease = Ago(0);
        // The other losing order: the recovery read the row as released, then its release completed it.
        var racing = new FaultyEscrowRepository(EscrowRepo)
        {
            OnUpdateStatus = (id, newStatus) =>
            {
                if (id == escrowId && newStatus == "completed")
                {
                    EscrowRepo.UpdateStatusAsync(escrowId, CreatedAt, "completed", "released",
                        new Dictionary<string, object> { { "completedAt", completedByTheRelease } }).GetAwaiter().GetResult();
                }

                return null;
            }
        };

        await SweepAsync(Cron(escrowRepo: racing));

        var row = await EscrowAsync(escrowId);
        Str(row, "status").Should().Be("completed");
        Str(row, "completedAt").Should().Be(completedByTheRelease);
        (await AuditAsync(escrowId)).Should().BeEmpty("the writer of the completed status writes its complete row");
    }

    // ──────────────────────────────────────────────
    // FUP-66: an order escrow the DeliveryIndex does not know
    // ──────────────────────────────────────────────

    private sealed record Trade(string OrderId, string DeliveryId, string EscrowId);

    /// <summary>
    /// A funded order, its delivery and its order escrow with no <c>deliveryId</c> (the link write failed).
    /// </summary>
    private async Task<Trade> SeedUnlinkedTradeAsync(
        string orderStatus, string deliveryStatus, bool expired, string? confirmedAt = null, string? deliveryOrderId = null)
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var deliveryId = $"delivery-{Guid.NewGuid():N}";
        var escrowId = $"escrow-order-{orderId}";

        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S(orderStatus),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["activeDeliveryId"] = S(deliveryId),
                ["paymentVerifiedAt"] = S(CreatedAt),
                ["orderTotal"] = N("900"),
                ["version"] = N("1")
            }
        });
        var delivery = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S("trip-it"),
            ["orderId"] = S(deliveryOrderId ?? orderId),
            ["listingId"] = S("listing-it"),
            ["buyerId"] = S(_buyerId),
            ["farmerId"] = S(_farmerId),
            ["truckerId"] = S(_truckerId),
            ["participantId"] = S(_buyerId),
            ["qrToken"] = S("qr-token-it"),
            ["qrNonce"] = S("nonce-it"),
            ["pickupPhotoVersionId"] = S("photo-version-pickup-1"),
            ["dropoffPhotoVersionId"] = S("photo-version-dropoff-1"),
            ["status"] = S(deliveryStatus),
            ["createdAt"] = S(CreatedAt),
            ["updatedAt"] = S(CreatedAt),
            ["version"] = N("1")
        };
        if (confirmedAt != null)
        {
            delivery["confirmedAt"] = S(confirmedAt);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = delivery });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Escrows,
            Item = new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = S(escrowId),
                ["createdAt"] = S(CreatedAt),
                ["updatedAt"] = S(CreatedAt),
                ["status"] = S("escrowed"),
                ["orderId"] = S(orderId),
                ["source"] = S("order"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["amount"] = N("900"),
                ["commission"] = N("45"),
                ["farmerPayout"] = N("855"),
                ["currency"] = S("ETB"),
                ["telebirrHoldRef"] = S("HOLD-FUP66"),
                ["expiresAt"] = S(expired ? Ago(60) : DateTime.UtcNow.AddDays(5).ToString("o")),
                ["version"] = N("2")
            }
        });

        return new Trade(orderId, deliveryId, escrowId);
    }

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, CropRepo, Ddb, _db.Trips, _db.Deliveries,
            userRepo: _users, orderRepo: OrderRepo, escrowRepo: EscrowRepo, transactionAuditRepo: TxRepo,
            orderEscrowService: Service());
    }

    private async Task<JsonElement> ConfirmDeliveredAsync(string deliveryId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = Guid.NewGuid().ToString(),
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = Guid.NewGuid().ToString(),
                        IdempotencyKey = $"{_buyerId}:delivery:{deliveryId}:update:{Guid.NewGuid():N}",
                        EntityType = "delivery",
                        EntityId = deliveryId,
                        Action = "update",
                        Data = new Dictionary<string, object>
                        {
                            { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-it" }
                        }
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", _buyerId }, { "role", "buyer" } }
            }
        };

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    [LocalDynamoDbFact]
    public async Task DeliveryConfirmed_UnlinkedOrderEscrow_IsStampedThroughTheOrderKey()
    {
        var trade = await SeedUnlinkedTradeAsync("in_transit", "pickedUp", expired: false);
        (await EscrowRepo.GetByDeliveryIdAsync(trade.DeliveryId)).Should().BeNull("the DeliveryIndex does not know an unlinked escrow");

        var result = await ConfirmDeliveredAsync(trade.DeliveryId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await EscrowAsync(trade.EscrowId);
        Str(row, "status").Should().Be("escrowed");
        var confirmedAt = Str(row, "deliveryConfirmedAt");
        confirmedAt.Should().NotBeNullOrEmpty();
        DateTime.Parse(Str(row, "disputeWindowEndsAt")!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().BeCloseTo(
                DateTime.Parse(confirmedAt!, null, System.Globalization.DateTimeStyles.RoundtripKind).AddHours(48),
                TimeSpan.FromSeconds(1));
        Str(row, "deliveryId").Should().BeNullOrEmpty("the stamp does not link");
        Str(row, "version").Should().Be("3");
    }

    [LocalDynamoDbFact]
    public async Task DeliveryConfirmed_OrderEscrowLinkedToAnotherDelivery_IsLeftAlone()
    {
        var trade = await SeedUnlinkedTradeAsync("in_transit", "pickedUp", expired: false);
        await EscrowRepo.UpdateStatusAsync(trade.EscrowId, CreatedAt, "escrowed", "escrowed",
            new Dictionary<string, object> { { "deliveryId", "delivery-of-another-handoff" } });

        var result = await ConfirmDeliveredAsync(trade.DeliveryId);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        Str(await EscrowAsync(trade.EscrowId), "deliveryConfirmedAt").Should().BeNullOrEmpty();
    }

    [LocalDynamoDbTheory]
    [InlineData("cancelled")]
    [InlineData("funded")]
    public async Task RefundSweep_UnlinkedOrderEscrow_WhoseDeliveryIsDelivered_IsStamped_NeverRefunded(string orderStatus)
    {
        var confirmedAt = Ago(60 * 3);
        var trade = await SeedUnlinkedTradeAsync(orderStatus, "delivered", expired: true, confirmedAt: confirmedAt);

        await SweepAsync(Cron());
        await SweepAsync(Cron());

        Calls($"refund:{trade.EscrowId}").Should().Be(0, "a confirmed delivery is never refunded");
        var row = await EscrowAsync(trade.EscrowId);
        Str(row, "status").Should().Be("escrowed");
        Str(row, "deliveryConfirmedAt").Should().Be(confirmedAt);
        Str(row, "disputeWindowEndsAt").Should().NotBeNullOrEmpty();
        Str(row, "settlementLock").Should().BeNullOrEmpty();
    }

    [LocalDynamoDbFact]
    public async Task RefundSweep_UnlinkedOrderEscrow_WhoseActiveDeliveryNamesAnotherOrder_IsNotStamped()
    {
        var trade = await SeedUnlinkedTradeAsync("in_transit", "delivered", expired: true,
            confirmedAt: Ago(60 * 3), deliveryOrderId: "order-of-someone-else");

        await SweepAsync(Cron());

        // An in-flight order is never refunded by the sweep; the point here is that the foreign
        // delivery's confirmation is not taken for this escrow's.
        Calls($"refund:{trade.EscrowId}").Should().Be(0);
        Str(await EscrowAsync(trade.EscrowId), "deliveryConfirmedAt").Should().BeNullOrEmpty();
    }
}

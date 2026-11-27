using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-174 (escrow-protocol.md 2.15.0) against real DynamoDB: a split verdict replayed after the sweep decided its
/// legs. The escrow is still <c>dispute_resolved</c> (no farmer payout done yet) and its crop leg is no longer held,
/// so the verdict's conditional write is refused by DynamoDB and the request goes to the replay: the disposition the
/// order needs is asked for and then recorded, and no money is decided again. Other amounts are still refused.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SplitVerdictReplayDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";
    private const string ResolvedAt = "2026-11-02T10:00:00.0000000Z";
    private const string Reason = "The truck broke down; the farmer agreed to settle now.";

    private readonly LocalDynamoDbFixture _db;

    public SplitVerdictReplayDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static AttributeValue S(string value) => new() { S = value };

    private AdminFunction Admin() => new(
        Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit);

    private static APIGatewayProxyRequest Split(
        string escrowId, decimal farmerAmount, string? disposition = null, string? reason = null) => new()
        {
            HttpMethod = "POST",
            Path = $"/admin/disputes/{escrowId}/resolve",
            Body = JsonSerializer.Serialize(new
            {
                resolution = "partial_refund",
                action = "split",
                farmerAmount,
                truckerAmount = 1500m,
                buyerRefund = 16_500m - 1500m - farmerAmount,
                disposition,
                reason
            }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-it" }, { "cognito:groups", "admin" } }
            }
        };

    /// <summary>
    /// A ledger escrow whose split (9,000 / 1,500 / 6,000) is recorded and whose legs the sweep decided (rows written,
    /// none paid yet, so it stays <c>dispute_resolved</c>), behind an order and a delivery still on the road.
    /// </summary>
    private async Task<(string EscrowId, string OrderId, string DeliveryId)> SeedDecidedSplitAsync()
    {
        var deliveryId = NewId("delivery-split");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new Dictionary<string, AttributeValue>
            {
                ["deliveryId"] = S(deliveryId),
                ["tripId"] = S("trip-split"),
                ["status"] = S("inTransit"),
                ["createdAt"] = S(CreatedAt),
                ["version"] = new AttributeValue { N = "2" }
            }
        });
        var orderId = NewId("order-split");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("in_transit"),
                ["buyerId"] = S("buyer-s"),
                ["farmerId"] = S("farmer-s"),
                ["activeDeliveryId"] = S(deliveryId),
                ["version"] = new AttributeValue { N = "4" }
            }
        });

        var escrowId = $"escrow-order-{orderId}";
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = ResolvedAt,
            ["status"] = "dispute_resolved",
            ["orderId"] = orderId,
            ["deliveryId"] = deliveryId,
            ["buyerId"] = "buyer-s",
            ["farmerId"] = "farmer-s",
            ["truckerId"] = "trucker-s",
            ["disputedBy"] = "buyer-s",
            ["amount"] = 17_250m,
            ["farmerPayout"] = 15_000m,
            ["truckerPayout"] = 1_500m,
            ["commission"] = 750m,
            ["currency"] = "ETB",
            ["source"] = "order",
            ["telebirrHoldRef"] = "HOLD-S",
            ["version"] = 5,
            ["disputeResolution"] = DisputeVerdicts.PartialRefund,
            ["disputeResolvedAt"] = ResolvedAt,
            ["disputeResolvedBy"] = "admin-it",
            [LedgerEscrowFields.VerdictAmounts] = new VerdictAmounts(9000m, 1500m, 6000m).ToMap(),
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        });

        // The sweep's decision, without the gateway call: the legs leave held and the rows wait.
        var service = new EscrowService(Escrows, new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            new EscrowIdempotencyRepository(Ddb, _db.Idempotency), new ScriptedGateway())
        {
            AlarmLog = _ => { },
            LedgerRepo = Ledger
        };
        var decided = await service.DecideLedgerAsync((await Escrows.GetByIdAsync(escrowId, true))!, "dispute_resolved",
            s => EscrowLedgerBook.PlanSplit(s, 9000m, 1500m, 6000m, $"verdict:{DisputeVerdicts.PartialRefund}").Plan,
            "system", callNow: false);
        decided.Outcome.Should().Be(EscrowService.LedgerDecideOutcome.Written);
        return (escrowId, orderId, deliveryId);
    }

    private async Task<Dictionary<string, object>> EscrowAsync(string escrowId) => (await Escrows.GetByIdAsync(escrowId, true))!;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    [LocalDynamoDbFact]
    public async Task TheRecordedSplit_ReplayedAfterTheSweepDecidedItsLegs_RecordsItsDisposition_AndDecidesNoMoneyAgain()
    {
        var (escrowId, orderId, deliveryId) = await SeedDecidedSplitAsync();
        var decided = LedgerEscrow.From(await EscrowAsync(escrowId));
        decided.IsHeld(LedgerEscrowFields.LegCrop).Should().BeFalse();
        var rowsBefore = (await Ledger.GetRowsAsync(escrowId)).Select(r => (r.Ref, r.Status, r.Amount)).ToList();
        rowsBefore.Should().HaveCount(3);

        // Without a disposition: the order cannot follow the verdict, so the admin is asked for one (before
        // FUP-174 this was 400 ADMIN_021, "the crop leg is no longer held").
        var ask = await Admin().FunctionHandler(Split(escrowId, 9000m), new TestLambdaContext());

        ask.StatusCode.Should().Be(422, ask.Body);
        ask.Body.Should().Contain("ADMIN_015");

        // Other amounts are a new split, checked against the legs held: still refused.
        var other = await Admin().FunctionHandler(Split(escrowId, 8000m, "settle_now", Reason), new TestLambdaContext());

        other.StatusCode.Should().Be(400, other.Body);
        other.Body.Should().Contain("ADMIN_021");
        Str(await EscrowAsync(escrowId), DisputeDispositionFields.Disposition).Should().BeNull();

        // The recorded split with the disposition: recorded and applied.
        var settle = await Admin().FunctionHandler(Split(escrowId, 9000m, "settle_now", Reason), new TestLambdaContext());

        settle.StatusCode.Should().Be(200, settle.Body);
        var escrow = await EscrowAsync(escrowId);
        Str(escrow, "status").Should().Be("dispute_resolved");
        Str(escrow, DisputeDispositionFields.Disposition).Should().Be("settle_now");
        Str(escrow, DisputeDispositionFields.Reason).Should().Be(Reason);
        Str((await new DeliveryRepository(Ddb, _db.Deliveries).GetByIdAsync(deliveryId, true))!, "cancelReason")
            .Should().Be("dispute_farmer_wins");
        Str((await new OrderRepository(Ddb, _db.Orders).GetByIdAsync(orderId, true))!, "status").Should().Be("dispute_resolved");

        // No money decided twice: the same rows, the same legs and totals, the same split.
        (await Ledger.GetRowsAsync(escrowId)).Select(r => (r.Ref, r.Status, r.Amount)).Should().BeEquivalentTo(rowsBefore);
        var after = LedgerEscrow.From(escrow);
        after.Legs.Should().BeEquivalentTo(decided.Legs);
        (after.FarmerPayout, after.TruckerPayout, after.RefundedTotal, after.ReleasedTotal)
            .Should().Be((decided.FarmerPayout, decided.TruckerPayout, decided.RefundedTotal, decided.ReleasedTotal));
        VerdictAmounts.From(escrow).Should().Be(new VerdictAmounts(9000m, 1500m, 6000m));
        var actions = (await new EscrowAuditRepository(Ddb, _db.EscrowAudit).GetAuditTrailAsync(escrowId, consistentRead: true))
            .Select(r => (string)r["action"]).ToList();
        actions.Should().Contain("dispute_resolved_settled_early").And.NotContain("admin_dispute_resolved");
    }
}

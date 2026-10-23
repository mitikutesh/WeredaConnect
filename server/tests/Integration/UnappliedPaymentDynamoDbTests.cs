using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-02 against real DynamoDB (LocalStack in CI): the filtered Scan of escrow-audit with its
/// reserved-word expressions, paging through pages of two rows so partitions are cut by page
/// boundaries both before and after their unapplied row, and the record call's conditional put,
/// consistent re-read and replay.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UnappliedPaymentDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public UnappliedPaymentDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private UnappliedPaymentService Service(
        int pageSize = UnappliedPaymentService.ScanPageSize,
        int maxTrailQueries = UnappliedPaymentService.MaxTrailQueriesPerCall) => new(
        new EscrowAuditRepository(Ddb, _db.EscrowAudit),
        new OrderRepository(Ddb, _db.Orders),
        new TransactionAuditRepository(Ddb, _db.TransactionAudit),
        _db.Idempotency,
        pageSize,
        maxTrailQueries);

    private static AttributeValue S(string value) => new() { S = value };

    private string Order(string name) => $"ord-fup02-{_run}-{name}";

    private async Task PutAuditAsync(string orderId, string action, int second, string? telebirrRef = null, string metadata = "")
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S($"escrow-order-{orderId}"),
            ["timestamp"] = S($"2026-10-20T08:00:{second:00}.0000000Z"),
            ["auditId"] = S($"audit-{Guid.NewGuid():N}"),
            ["action"] = S(action),
            ["actor"] = S("telebirr"),
            ["fromState"] = S(""),
            ["toState"] = S(""),
            ["metadata"] = S(metadata),
        };
        if (telebirrRef != null)
        {
            item["telebirrRef"] = S(telebirrRef);
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.EscrowAudit, Item = item });
    }

    private async Task PutUnappliedAsync(string orderId, string action, int second, string paymentOrderId, string amount = "1500")
        => await PutAuditAsync(orderId, action, second, paymentOrderId,
            $"orderId={orderId},merchOrderId=WCO{_run}000001,amount={amount},reason=status_cancelled");

    /// <summary>Rows the filter drops, so the order's partition spans several two-row pages.</summary>
    private async Task PutFillerAsync(string orderId, params int[] seconds)
    {
        foreach (var second in seconds)
        {
            await PutAuditAsync(orderId, "order_payment_failed", second, metadata: $"orderId={orderId},tradeStatus=Failed");
        }
    }

    private async Task PutOrderAsync(string orderId, string status)
        => await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(orderId),
                ["createdAt"] = S("2026-10-19T08:00:00.0000000Z"),
                ["status"] = S(status),
                ["buyerId"] = S("buyer-fup02"),
                ["version"] = new AttributeValue { N = "3" },
            },
        });

    [LocalDynamoDbTheory]
    [InlineData(2, UnappliedPaymentService.MaxTrailQueriesPerCall)]
    [InlineData(500, 1)]
    public async Task Paging_lists_each_open_payment_exactly_once(int pageSize, int maxTrailQueries)
    {
        // (2, 50): partitions cut by two-row Scan pages. (500, 1): every call stops after one
        // order's trail and resumes right after that order's last row.
        // A: the unapplied row sits after two filler rows. B: refunded, never listed. C: two
        // duplicates between filler rows. D: funded by its payment, never listed.
        var a = Order("a");
        var b = Order("b");
        var c = Order("c");
        var d = Order("d");
        await PutFillerAsync(a, 1, 2);
        await PutUnappliedAsync(a, UnappliedPayments.Unmatched, 3, $"TBA{_run}");
        await PutFillerAsync(a, 4, 5, 6);
        await PutOrderAsync(a, "cancelled");

        await PutUnappliedAsync(b, UnappliedPayments.Unmatched, 1, $"TBB{_run}");
        await PutAuditAsync(b, UnappliedPayments.RefundedManual, 2, $"TBREF{_run}", $"paymentOrderId=TBB{_run};amount=1500");
        await PutOrderAsync(b, "cancelled");

        await PutFillerAsync(c, 1);
        await PutUnappliedAsync(c, UnappliedPayments.Duplicate, 2, $"TBC1{_run}");
        await PutFillerAsync(c, 3, 4);
        await PutUnappliedAsync(c, UnappliedPayments.Duplicate, 5, $"TBC2{_run}");
        await PutFillerAsync(c, 6, 7);
        await PutOrderAsync(c, "funded");

        await PutUnappliedAsync(d, UnappliedPayments.Unprocessed, 1, $"TBD{_run}");
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _db.Orders,
            Key = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S(d),
                ["createdAt"] = S("2026-10-19T08:00:00.0000000Z"),
            },
            UpdateExpression = "SET #s = :s, telebirrPaymentOrderId = :p",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = S("funded"), [":p"] = S($"TBD{_run}") },
        });

        var service = Service(pageSize, maxTrailQueries);
        var seen = new List<UnappliedPaymentDto>();
        string? token = null;
        var calls = 0;
        do
        {
            var result = await service.ListAsync(limit: 1, token);
            result.IsSuccess.Should().BeTrue(result.Error?.Message);
            seen.AddRange(result.Value!.Payments.Where(p => p.OrderId.StartsWith($"ord-fup02-{_run}-", StringComparison.Ordinal)));
            token = result.Value.NextToken;
            calls++;
        } while (token != null && calls < 20_000);

        token.Should().BeNull("the scan must reach the end of the table");
        seen.Select(p => p.PaymentOrderId).Should().BeEquivalentTo(new[] { $"TBA{_run}", $"TBC1{_run}", $"TBC2{_run}" });
        seen.Single(p => p.PaymentOrderId == $"TBA{_run}").OrderStatus.Should().Be("cancelled");
        seen.Single(p => p.PaymentOrderId == $"TBA{_run}").CapturedAmount.Should().Be(1500m);
    }

    [LocalDynamoDbFact]
    public async Task Recording_writes_one_row_a_replay_sees_it_and_the_payment_leaves_the_queue()
    {
        var orderId = Order("rec");
        var paymentOrderId = $"TBR{_run}";
        await PutUnappliedAsync(orderId, UnappliedPayments.Unmatched, 1, paymentOrderId);
        await PutOrderAsync(orderId, "cancelled");
        var request = new RecordManualRefundRequest
        {
            PaymentOrderId = paymentOrderId,
            TelebirrRef = $"TBREFUND{_run}",
            Amount = 1500m,
            Reason = "Buyer paid a cancelled order; refunded in the portal",
        };
        var audit = new AuditContext { Actor = "admin-fup02", ActorRole = "admin" };

        var first = await Service().RecordRefundAsync(orderId, request, "admin-fup02", true, audit, null);
        var replay = await Service().RecordRefundAsync(orderId, request, "admin-fup02", true, audit, null);
        var otherRef = await Service().RecordRefundAsync(
            orderId, request with { TelebirrRef = $"TBOTHER{_run}" }, "admin-fup02", true, audit, null);

        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        first.Value!.AlreadyRecorded.Should().BeFalse();
        replay.IsSuccess.Should().BeTrue();
        replay.Value!.AlreadyRecorded.Should().BeTrue();
        replay.Value.RecordedAt.Should().Be(first.Value.RecordedAt);
        otherRef.Error!.Code.Should().Be("ESCROW_036");

        var trail = await new EscrowAuditRepository(Ddb, _db.EscrowAudit)
            .GetAuditTrailAsync($"escrow-order-{orderId}", consistentRead: true);
        var refunds = trail.Where(r => (string)r["action"] == UnappliedPayments.RefundedManual).ToList();
        refunds.Should().ContainSingle();
        refunds[0]["telebirrRef"].Should().Be($"TBREFUND{_run}");
        refunds[0]["metadata"].Should().Be($"paymentOrderId={paymentOrderId};amount=1500;merchOrderId=WCO{_run}000001");
        refunds[0]["actor"].Should().Be("admin-fup02");
        UnappliedPayments.HasOpen(trail, null, null).Should().BeFalse("the payment_review blocker is lifted");

        var txRows = await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(orderId);
        txRows.Where(r => (string)r["action"] == "admin_payment_refunded_manual").Should().ContainSingle();

        var order = await new OrderRepository(Ddb, _db.Orders).GetByIdAsync(orderId, consistentRead: true);
        order!["status"].Should().Be("cancelled", "recording a refund moves nothing");
        order["version"].ToString().Should().Be("3");

        var claim = await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S($"refund-claim#{orderId}#{paymentOrderId}") },
            ConsistentRead = true,
        });
        claim.Item["telebirrRef"].S.Should().Be($"TBREFUND{_run}");
        // The table's TTL, as its checkout rows: epoch seconds, 400 days out.
        var expiresIn = DateTimeOffset.FromUnixTimeSeconds(long.Parse(claim.Item["expiresAt"].N)) - DateTimeOffset.UtcNow;
        expiresIn.TotalDays.Should().BeInRange(399.9, 400.1);
    }

    [LocalDynamoDbFact]
    public async Task Two_admins_recording_one_payment_at_once_write_one_row()
    {
        var orderId = Order("race");
        var paymentOrderId = $"TBRACE{_run}";
        await PutUnappliedAsync(orderId, UnappliedPayments.Duplicate, 1, paymentOrderId);
        await PutOrderAsync(orderId, "funded");
        var audit = new AuditContext { Actor = "admin-fup02", ActorRole = "admin" };
        RecordManualRefundRequest Request(string reference) => new()
        {
            PaymentOrderId = paymentOrderId,
            TelebirrRef = reference,
            Amount = 1500m,
            Reason = "Second payment for a funded order; refunded",
        };

        // Both read the trail before either writes: the claim decides.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Service().RecordRefundAsync(orderId, Request($"TBREF{i}{_run}"), $"admin-{i}", true, audit, null)));

        results.Count(r => r.IsSuccess && !r.Value!.AlreadyRecorded).Should().Be(1);
        results.Where(r => !r.IsSuccess).Should().OnlyContain(r => r.Error!.Code == "ESCROW_036");
        var trail = await new EscrowAuditRepository(Ddb, _db.EscrowAudit)
            .GetAuditTrailAsync($"escrow-order-{orderId}", consistentRead: true);
        trail.Count(r => (string)r["action"] == UnappliedPayments.RefundedManual).Should().Be(1);
    }
}

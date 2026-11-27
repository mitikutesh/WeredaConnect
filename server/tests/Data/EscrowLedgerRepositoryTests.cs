using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-20: the shape of every ledger write as DynamoDB receives it. Each decision is one TransactWriteItems
/// call: the rows (conditional puts), the escrow update (version, status, no lock or queue, moving legs held)
/// and the audit rows (conditional puts). A row's status write is conditioned on its version and status and
/// keeps the open-row index keys in step. Every placeholder is defined and used (DynamoDB refuses either
/// mistake); the real expressions run against DynamoDB in <c>EscrowLedgerDynamoDbTests</c>.
/// </summary>
public class EscrowLedgerRepositoryTests
{
    private const string Ledger = "ledger-test";
    private const string Escrows = "escrows-test";
    private const string Audit = "audit-test";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowLedgerRepository _repo;
    private TransactWriteItemsRequest? _transaction;

    public EscrowLedgerRepositoryTests()
    {
        _db.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(t => _transaction = t), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _repo = new EscrowLedgerRepository(_db, Ledger, Escrows, Audit);
    }

    private static LedgerEscrow Escrow() => new()
    {
        EscrowId = "escrow-order-o1", CreatedAt = "2026-11-01T10:00:00.0000000Z", Version = 3, Status = "escrowed",
        Amount = 17_250m, FarmerPayout = 15_000m, TruckerPayout = 1_500m, Commission = 750m, RefundedTotal = 0m, ReleasedTotal = 0m,
        Legs = new Dictionary<string, string> { ["crop"] = "held", ["transport"] = "held", ["commission"] = "held" },
        BuyerId = "b", FarmerId = "f", TruckerId = "t", OrderId = "o1", HoldRef = "H"
    };

    private static LedgerRow Row(string reference = "refund#all", string status = "pending", int version = 1) => new()
    {
        EscrowId = "escrow-order-o1", Ref = reference, Kind = "refund", Legs = new[] { "crop", "commission", "transport" },
        PayeeRole = "buyer", PayeeId = "b", Amount = 17_250m, Status = status, GatewayRef = "WCLABC", HoldRef = "H",
        Reason = "rejected", Actor = "system", OrderId = "o1", CreatedAt = "2026-11-07T10:00:00.0000000Z",
        UpdatedAt = "2026-11-07T10:00:00.0000000Z", DueAt = "2026-11-07T10:15:00.0000000Z", Version = version
    };

    private static readonly Regex Placeholder = new(@"[#:][A-Za-z0-9_]+", RegexOptions.Compiled);

    /// <summary>Every name and value placeholder is defined and used, as DynamoDB requires.</summary>
    private static void AssertPlaceholders(string? update, string? condition,
        Dictionary<string, string>? names, Dictionary<string, AttributeValue>? values)
    {
        var text = $"{update} {condition}";
        var used = Placeholder.Matches(text).Select(m => m.Value).ToHashSet();
        foreach (var token in used)
        {
            if (token.StartsWith('#'))
            {
                (names ?? new()).Should().ContainKey(token, text);
            }
            else
            {
                (values ?? new()).Should().ContainKey(token, text);
            }
        }

        foreach (var name in (names ?? new()).Keys)
        {
            used.Should().Contain(name, $"{name} is defined but unused in: {text}");
        }

        foreach (var value in (values ?? new()).Keys)
        {
            used.Should().Contain(value, $"{value} is defined but unused in: {text}");
        }
    }

    private static void AssertPlaceholders(TransactWriteItem item)
    {
        if (item.Update is { } u)
        {
            AssertPlaceholders(u.UpdateExpression, u.ConditionExpression, u.ExpressionAttributeNames, u.ExpressionAttributeValues);
        }

        if (item.Put is { } p)
        {
            AssertPlaceholders(null, p.ConditionExpression, p.ExpressionAttributeNames, p.ExpressionAttributeValues);
        }
    }

    [Fact]
    public async Task ADecision_IsOneTransaction_RowsEscrowAndAuditRows_EachConditional()
    {
        var plan = EscrowLedgerBook.PlanRefundAll(Escrow(), "rejected")!;

        var write = await _repo.DecideAsync(new LedgerDecision(plan.Before, plan.After, new[] { Row() },
            new[] { new LedgerAudit("escrow-order-o1", "2026-11-07T10:00:00.0000123Z", "ledger_pending", "escrowed", "escrowed", "system", "ref=refund#all") },
            new Dictionary<string, object> { ["refundTrigger"] = "delivery_timeout" }));

        write.Should().Be(LedgerWrite.Written);
        var items = _transaction!.TransactItems;
        items.Should().HaveCount(3);
        items.ForEach(AssertPlaceholders);

        var put = items[0].Put;
        put.TableName.Should().Be(Ledger);
        put.ConditionExpression.Should().Be("attribute_not_exists(#id)");
        put.Item["openStatus"].S.Should().Be("pending");
        put.Item["dueAt"].S.Should().Be("2026-11-07T10:15:00.0000000Z");
        put.Item["legs"].SS.Should().BeEquivalentTo("crop", "commission", "transport");
        put.Item["version"].N.Should().Be("1");

        var escrow = items[1].Update;
        escrow.TableName.Should().Be(Escrows);
        escrow.Key["escrowId"].S.Should().Be("escrow-order-o1");
        escrow.ConditionExpression.Should().StartWith(
            "#version = :v AND #status = :status AND attribute_not_exists(#lock) AND attribute_not_exists(#manual)");
        escrow.ConditionExpression.Should().Contain("#legs.#leg0 = :held").And.Contain("#legs.#leg2 = :held");
        escrow.ExpressionAttributeValues[":v"].N.Should().Be("3");
        escrow.ExpressionAttributeValues[":legs"].M["crop"].S.Should().Be("refunded");
        escrow.ExpressionAttributeValues[":refunded"].N.Should().Be("17250");
        escrow.UpdateExpression.Should().Contain("#x0 = :x0").And.EndWith(" ADD #version :one");

        var audit = items[2].Put;
        audit.TableName.Should().Be(Audit);
        audit.ConditionExpression.Should().Be("attribute_not_exists(#id)");
        audit.Item["auditId"].S.Should().StartWith("audit-ledger_pending-");
    }

    [Fact]
    public async Task ARowThatBecomesDone_LeavesTheOpenIndex_AndMovesTheEscrowOnItsVersion()
    {
        var before = Row(status: "sent", version: 3) with { CallingSince = null, SentAt = "2026-11-07T10:01:00.0000000Z" };
        var after = before with
        {
            Status = "done", TelebirrRef = "TB-1", PaidBy = "gateway", DoneAt = "2026-11-07T10:20:00.0000000Z",
            UpdatedAt = "2026-11-07T10:20:00.0000000Z", DueAt = null, Version = 4, Attempts = 1
        };

        await _repo.TransitionAsync(new LedgerTransition
        {
            Before = before,
            After = after,
            Audits = new[] { new LedgerAudit("escrow-order-o1", "2026-11-07T10:20:00.0000456Z", "ledger_done", "", "", "system", "ref=refund#all") },
            Escrow = ("escrow-order-o1", "2026-11-01T10:00:00.0000000Z", 5),
            EscrowStatus = "refunded",
            EscrowFields = new Dictionary<string, object> { ["refundedAt"] = "2026-11-07T10:20:00.0000000Z" }
        });

        var items = _transaction!.TransactItems;
        items.ForEach(AssertPlaceholders);
        var row = items[0].Update;
        row.ConditionExpression.Should().Be("#version = :v AND #status = :was");
        row.ExpressionAttributeValues[":v"].N.Should().Be("3");
        row.ExpressionAttributeValues[":was"].S.Should().Be("sent");
        row.UpdateExpression.Should().Contain("REMOVE").And.Contain("#open").And.Contain("#due").And.Contain("#calling");
        row.UpdateExpression.Should().MatchRegex(@"REMOVE .*#open");
        row.UpdateExpression.Should().MatchRegex(@"REMOVE .*#due");
        var escrow = items[1].Update;
        escrow.ConditionExpression.Should().Be("#version = :v");
        escrow.ExpressionAttributeValues[":v"].N.Should().Be("5");
        escrow.ExpressionAttributeValues[":status"].S.Should().Be("refunded");
        escrow.UpdateExpression.Should().Contain("REMOVE #blockReason, #blockReasonAt");
    }

    [Fact]
    public async Task ARowThatStaysOpen_KeepsItsIndexKeys_InStepWithItsStatus()
    {
        var before = Row(status: "pending", version: 2) with { CallingSince = "2026-11-07T10:00:00.0000000Z" };
        var after = before with { Status = "sent", CallingSince = null, SentAt = "2026-11-07T10:00:30.0000000Z",
            DueAt = "2026-11-07T10:15:30.0000000Z", Attempts = 1, LastCode = "TELEBIRR_TIMEOUT", Version = 3 };

        await _repo.TransitionAsync(new LedgerTransition { Before = before, After = after, Audits = Array.Empty<LedgerAudit>() });

        var row = _transaction!.TransactItems.Single().Update;
        AssertPlaceholders(row.UpdateExpression, row.ConditionExpression, row.ExpressionAttributeNames, row.ExpressionAttributeValues);
        row.ExpressionAttributeValues[":open"].S.Should().Be("sent");
        row.ExpressionAttributeValues[":due"].S.Should().Be("2026-11-07T10:15:30.0000000Z");
        row.UpdateExpression.Should().MatchRegex(@"REMOVE .*#calling");
    }

    [Theory]
    [InlineData(0, "RowExists")]
    [InlineData(1, "EscrowConflict")]
    [InlineData(2, "EscrowConflict")]
    public async Task ACancelledDecision_SaysWhichConditionFailed(int failedIndex, string expected)
    {
        var reasons = Enumerable.Range(0, 3)
            .Select(i => new CancellationReason { Code = i == failedIndex ? "ConditionalCheckFailed" : "None" })
            .ToList();
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionCanceledException("cancelled") { CancellationReasons = reasons });
        var plan = EscrowLedgerBook.PlanRefundAll(Escrow(), "rejected")!;

        var write = await _repo.DecideAsync(new LedgerDecision(plan.Before, plan.After, new[] { Row() },
            new[] { new LedgerAudit("escrow-order-o1", "t", "ledger_pending", "", "", "system", "m") }));

        write.ToString().Should().Be(expected);
    }

    [Fact]
    public async Task TheCallingStamp_IsConditionedOnTheVersionTheStatusAndNoStamp()
    {
        UpdateItemRequest? stamp = null;
        _db.UpdateItemAsync(Arg.Do<UpdateItemRequest>(u => stamp = u), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());

        (await _repo.TryStampCallingAsync(Row(version: 2), "2026-11-07T10:00:00.0000000Z", "2026-11-07T10:15:00.0000000Z")).Should().BeTrue();

        AssertPlaceholders(stamp!.UpdateExpression, stamp.ConditionExpression, stamp.ExpressionAttributeNames, stamp.ExpressionAttributeValues);
        stamp.ConditionExpression.Should().Be("#version = :v AND (#status = :pending OR #status = :failed) AND attribute_not_exists(#calling)");
        stamp.ExpressionAttributeValues[":v"].N.Should().Be("2");

        _db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("taken"));
        (await _repo.TryStampCallingAsync(Row(version: 2), "t", "t")).Should().BeFalse();
    }

    [Fact]
    public void ARow_RoundTripsThroughItsItem()
    {
        var row = Row() with { Attempts = 2, LastCode = "X", ManualReason = "GATEWAY_REFUSED_3", Status = "manual" };

        var back = EscrowLedgerRepository.FromItem(EscrowLedgerRepository.ToItem(row));

        back.Should().BeEquivalentTo(row, o => o.Excluding(r => r.Legs));
        back.Legs.Should().BeEquivalentTo(row.Legs);
    }

    [Fact]
    public async Task TheOpenQuery_ReadsTheIndexOldestFirst_UpToNow()
    {
        QueryRequest? query = null;
        _db.QueryAsync(Arg.Do<QueryRequest>(q => query = q), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        await _repo.QueryOpenAsync("sent", "2026-11-07T10:00:00.0000000Z", 50, null);

        query!.IndexName.Should().Be("LedgerOpenIndex");
        query.KeyConditionExpression.Should().Be("#open = :open AND #due <= :due");
        query.ScanIndexForward.Should().BeTrue();
        AssertPlaceholders(query.KeyConditionExpression, null, query.ExpressionAttributeNames, query.ExpressionAttributeValues);
    }
}

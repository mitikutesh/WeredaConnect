using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-215: the one-off <c>backfill_payout_references</c> action over a small in-memory DynamoDB (Scan pages,
/// conditional puts and the reference reads), so each decision is visible: which legs get an item, which are a
/// no-op, which conflict, the marker and the cursor. The real conditions run in
/// <c>PayoutReferenceBackfillDynamoDbTests</c>.
/// </summary>
public sealed class PayoutReferenceBackfillTests
{
    private const string EscrowsTable = "escrows-test";
    private const string ReferenceTable = "escrow-idempotency-test";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly InMemoryLedger _ledger = new();
    private readonly List<Dictionary<string, AttributeValue>> _escrows = new();
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _references = new(StringComparer.Ordinal);
    private readonly List<ScanRequest> _scans = new();
    private readonly TestLambdaContext _context = new();
    private TimeSpan _elapsed = TimeSpan.Zero;
    private Func<PutItemRequest, Exception?> _putFails = _ => null;

    public PayoutReferenceBackfillTests()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>()).Returns(call => Scan(call.Arg<ScanRequest>()));
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(call => Put(call.Arg<PutItemRequest>()));
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var key = call.Arg<GetItemRequest>().Key["idempotencyKey"].S;
            return new GetItemResponse
            {
                Item = _references.TryGetValue(key, out var item) ? item : new Dictionary<string, AttributeValue>()
            };
        });
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<UpdateItemRequest>();
            var key = request.Key["idempotencyKey"].S;
            if (!_references.TryGetValue(key, out var item))
            {
                item = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = S(key) };
                _references[key] = item;
            }

            item["status"] = request.ExpressionAttributeValues[":status"];
            return new UpdateItemResponse();
        });
    }

    private static AttributeValue S(string value) => new() { S = value };

    private PayoutReferenceBackfill Backfill(bool withLedger = true, int pageSize = 2)
        => new(_ddb, EscrowsTable, ReferenceTable, withLedger ? _ledger : null) { PageSize = pageSize };

    /// <summary>A 4 minute slice on a hand-driven clock; each Scan page moves it by <paramref name="perPage"/>.</summary>
    private CronRunSlice Slice() => new CronRunBudget(() => _elapsed).Slice(TimeSpan.FromMinutes(4));

    private TimeSpan _perPage = TimeSpan.Zero;

    private Task<ScanResponse> Scan(ScanRequest request)
    {
        // A copy: the backfill sends the same request object again with the next start key.
        _scans.Add(new ScanRequest
        {
            TableName = request.TableName,
            FilterExpression = request.FilterExpression,
            ExpressionAttributeNames = new Dictionary<string, string>(request.ExpressionAttributeNames),
            ExpressionAttributeValues = request.ExpressionAttributeValues == null
                ? null
                : new Dictionary<string, AttributeValue>(request.ExpressionAttributeValues),
            ExclusiveStartKey = request.ExclusiveStartKey == null ? null : new Dictionary<string, AttributeValue>(request.ExclusiveStartKey),
            Limit = request.Limit
        });
        _elapsed += _perPage;
        var start = 0;
        if (request.ExclusiveStartKey is { Count: > 0 } after)
        {
            start = _escrows.FindIndex(e => e["escrowId"].S == after["escrowId"].S && e["createdAt"].S == after["createdAt"].S) + 1;
        }

        var page = _escrows.Skip(start).Take(request.Limit > 0 ? request.Limit : 100).ToList();
        var last = start + page.Count < _escrows.Count && page.Count > 0
            ? new Dictionary<string, AttributeValue> { ["escrowId"] = page[^1]["escrowId"], ["createdAt"] = page[^1]["createdAt"] }
            : new Dictionary<string, AttributeValue>();
        // The filter as DynamoDB applies it: a recorded 1.x reference, or (when asked) a ledger escrow.
        var matching = page.Where(e => e.Keys.Any(k => k.StartsWith("manual", StringComparison.Ordinal) && k.EndsWith("Ref", StringComparison.Ordinal))
                                       || (request.FilterExpression.Contains(":ledger", StringComparison.Ordinal)
                                           && e.TryGetValue(LedgerEscrowFields.SettlementModel, out var m) && m.S == LedgerEscrowFields.ModelLedger))
            .ToList();
        return Task.FromResult(new ScanResponse { Items = matching, LastEvaluatedKey = last });
    }

    private Task<PutItemResponse> Put(PutItemRequest request)
    {
        if (_putFails(request) is { } failure)
        {
            throw failure;
        }

        var key = request.Item["idempotencyKey"].S;
        if (request.ConditionExpression != null && _references.ContainsKey(key))
        {
            throw new ConditionalCheckFailedException("taken");
        }

        _references[key] = request.Item;
        return Task.FromResult(new PutItemResponse());
    }

    private void Escrow(string escrowId, params (string Leg, string Ref, string PaidBy, string? PaidAt)[] legs)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S("2026-10-01T08:00:00.0000000Z")
        };
        foreach (var (leg, reference, paidBy, paidAt) in legs)
        {
            item[ManualPayoutFields.RefAttr(leg)] = S(reference);
            item[ManualPayoutFields.PaidByAttr(leg)] = S(paidBy);
            if (paidAt != null)
            {
                item[ManualPayoutFields.PaidAtAttr(leg)] = S(paidAt);
            }
        }

        _escrows.Add(item);
    }

    private void LedgerEscrow(string escrowId, params LedgerRow[] rows)
    {
        _escrows.Add(new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(escrowId),
            ["createdAt"] = S("2026-10-02T08:00:00.0000000Z"),
            [LedgerEscrowFields.SettlementModel] = S(LedgerEscrowFields.ModelLedger)
        });
        foreach (var row in rows)
        {
            _ledger.Rows[(row.EscrowId, row.Ref)] = row;
        }
    }

    private static LedgerRow Row(string escrowId, string reference, string status, string? paidBy, string? telebirrRef) => new()
    {
        EscrowId = escrowId,
        Ref = reference,
        Kind = LedgerFields.KindRelease,
        Amount = 100m,
        Status = status,
        GatewayRef = "WCL0",
        Reason = "test",
        Actor = "system",
        PaidBy = paidBy,
        TelebirrRef = telebirrRef,
        CreatedAt = "2026-10-02T08:00:00.0000000Z",
        UpdatedAt = "2026-10-02T09:00:00.0000000Z",
        DoneAt = status == LedgerFields.StatusDone ? "2026-10-02T09:00:00.0000000Z" : null
    };

    private void ReferenceHeldBy(string reference, string escrowId, string leg) => _references[PayoutReferenceFields.Key(reference)] =
        new Dictionary<string, AttributeValue>
        {
            ["idempotencyKey"] = S(PayoutReferenceFields.Key(reference)),
            [PayoutReferenceFields.EscrowId] = S(escrowId),
            [PayoutReferenceFields.Ref] = S(leg),
            [PayoutReferenceFields.RecordedBy] = S("admin-b")
        };

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private Dictionary<string, AttributeValue>? Item(string reference)
        => _references.TryGetValue(PayoutReferenceFields.Key(reference), out var item) ? item : null;

    private string? MarkerStatus()
        => _references.TryGetValue(PayoutReferenceBackfill.MarkerKey, out var marker) && marker.TryGetValue("status", out var s) ? s.S : null;

    [Fact]
    public async Task AdminRecordedLegs_GetTheRecordsOwnItem_GatewayLegsNone()
    {
        Escrow("escrow-a",
            ("farmer", " tb-001 ", "admin-a", "2026-10-03T10:00:00.0000000Z"),
            ("trucker", "GW-REF-1", ManualPayoutFields.PaidByGateway, "2026-10-03T09:00:00.0000000Z"));
        Escrow("escrow-b", ("buyer", "TB-002", "admin-b", "2026-10-04T10:00:00.0000000Z"));

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.Legs.Should().Be(2);
        counts.Written.Should().Be(2);
        counts.Conflicts.Should().Be(0);
        counts.Failed.Should().Be(0);
        counts.ScanDone.Should().BeTrue();
        counts.Outcome.Should().Be("ok");
        // The record path's own item: key trimmed and upper case, the reference as stored, 400 days from the record.
        var item = Item("TB-001")!;
        item["idempotencyKey"].S.Should().Be("payout-ref#TB-001");
        item["kind"].S.Should().Be(PayoutReferenceFields.Kind);
        item[PayoutReferenceFields.EscrowId].S.Should().Be("escrow-a");
        item[PayoutReferenceFields.Ref].S.Should().Be("farmer");
        item[PayoutReferenceFields.ExternalRef].S.Should().Be("tb-001");
        item[PayoutReferenceFields.RecordedBy].S.Should().Be("admin-a");
        item[PayoutReferenceFields.RecordedAt].S.Should().Be("2026-10-03T10:00:00.0000000Z");
        item["expiresAt"].N.Should().Be(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero).AddDays(400).ToUnixTimeSeconds().ToString());
        Item("GW-REF-1").Should().BeNull();
        Item("TB-002")![PayoutReferenceFields.Ref].S.Should().Be("buyer");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
        Logs.Should().Contain("PAYOUT_REF_BACKFILL escrows=2 legs=2 ledgerRows=0 written=2 present=0 conflicts=0 failed=0");
    }

    [Fact]
    public async Task ReferenceAlreadyHeldByTheSameLeg_IsANoOp()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        ReferenceHeldBy("tb-001", "escrow-a", "farmer");
        var before = Item("TB-001");

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.Present.Should().Be(1);
        counts.Written.Should().Be(0);
        counts.Conflicts.Should().Be(0);
        Item("TB-001").Should().BeSameAs(before);
        counts.Outcome.Should().Be("ok");
    }

    [Fact]
    public async Task ReferenceHeldByAnotherLeg_IsLoggedWithBothPlaces_NeverOverwritten()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        Escrow("escrow-b", ("buyer", "tb-001", "admin-b", "2026-10-05T10:00:00.0000000Z"));
        LedgerEscrow("escrow-order-l1", Row("escrow-order-l1", "release#farmer", LedgerFields.StatusDone, "admin-c", "TB-001"));

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.Written.Should().Be(1);
        counts.Conflicts.Should().Be(2);
        var item = Item("TB-001")!;
        item[PayoutReferenceFields.EscrowId].S.Should().Be("escrow-a");
        item[PayoutReferenceFields.Ref].S.Should().Be("farmer");
        Logs.Should().Contain(
            "PAYOUT_REF_BACKFILL_CONFLICT reference=TB-001 heldBy=escrow-a/farmer recordedBy=admin-a alsoOn=escrow-b/buyer alsoRecordedBy=admin-b");
        Logs.Should().Contain(
            "PAYOUT_REF_BACKFILL_CONFLICT reference=TB-001 heldBy=escrow-a/farmer recordedBy=admin-a alsoOn=escrow-order-l1/release:farmer alsoRecordedBy=admin-c");
        // A conflict is for a person, not for a rerun: the read is complete.
        counts.Outcome.Should().Be("ok");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
    }

    [Fact]
    public async Task LedgerRowsDoneByAnAdmin_GetAnItem_OtherRowsNone()
    {
        LedgerEscrow("escrow-order-l1",
            Row("escrow-order-l1", "release#farmer", LedgerFields.StatusDone, "admin-a", "TBL-1"),
            Row("escrow-order-l1", "release#trucker#t-1", LedgerFields.StatusDone, LedgerFields.PaidByGateway, "GW-2"),
            Row("escrow-order-l1", "refund#duplicate#p-1", LedgerFields.StatusManual, null, null),
            Row("escrow-order-l1", "refund#all", LedgerFields.StatusDone, "admin-b", null));

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.LedgerRows.Should().Be(1);
        counts.Written.Should().Be(1);
        var item = Item("TBL-1")!;
        item[PayoutReferenceFields.Ref].S.Should().Be("release#farmer");
        item[PayoutReferenceFields.RecordedBy].S.Should().Be("admin-a");
        item[PayoutReferenceFields.RecordedAt].S.Should().Be("2026-10-02T09:00:00.0000000Z");
        Item("GW-2").Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheLedgerTable_TheScanAsksFor1xLegsOnly()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        LedgerEscrow("escrow-order-l1", Row("escrow-order-l1", "release#farmer", LedgerFields.StatusDone, "admin-a", "TBL-1"));

        var counts = await Backfill(withLedger: false).RunAsync(null, Slice(), _context);

        _scans.Should().OnlyContain(s => !s.FilterExpression.Contains(":ledger"));
        counts.Escrows.Should().Be(1);
        Item("TBL-1").Should().BeNull();
    }

    [Fact]
    public async Task TheScanReadsTheEscrowsTableOnce_WithTheRecordedLegFilter()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));

        await Backfill().RunAsync(null, Slice(), _context);

        var scan = _scans.Should().ContainSingle().Subject;
        scan.TableName.Should().Be(EscrowsTable);
        scan.FilterExpression.Should().Be("attribute_exists(#r0) OR attribute_exists(#r1) OR attribute_exists(#r2) OR #model = :ledger");
        scan.ExpressionAttributeNames["#r0"].Should().Be("manualFarmerRef");
        scan.ExpressionAttributeNames["#r1"].Should().Be("manualTruckerRef");
        scan.ExpressionAttributeNames["#r2"].Should().Be("manualBuyerRef");
        scan.ExpressionAttributeValues[":ledger"].S.Should().Be("ledger");
    }

    [Fact]
    public async Task ALegWithoutARecordTime_IsSkippedAndLogged_WithoutHoldingTheMarker()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", null));

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.Skipped.Should().Be(1);
        Item("TB-001").Should().BeNull();
        Logs.Should().Contain("PAYOUT_REF_BACKFILL_SKIPPED escrowId=escrow-a ref=farmer reason=no_record_time");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
    }

    [Fact]
    public async Task ADoneMarker_ReadsNothing()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        _references[PayoutReferenceBackfill.MarkerKey] = new Dictionary<string, AttributeValue>
        {
            ["idempotencyKey"] = S(PayoutReferenceBackfill.MarkerKey),
            ["status"] = S(PayoutReferenceBackfill.MarkerDone)
        };

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.SkippedRun.Should().BeTrue();
        counts.Outcome.Should().Be("ok");
        _scans.Should().BeEmpty();
        Item("TB-001").Should().BeNull();
    }

    [Fact]
    public async Task AFailedWrite_KeepsTheMarkerFromDone_AndARerunFromTheStartFinishes()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        Escrow("escrow-b", ("buyer", "TB-002", "admin-b", "2026-10-04T10:00:00.0000000Z"));
        _putFails = put => put.Item["idempotencyKey"].S == "payout-ref#TB-002"
            ? new ProvisionedThroughputExceededException("slow down")
            : null;

        var first = await Backfill().RunAsync(null, Slice(), _context);

        first.Failed.Should().Be(1);
        first.Written.Should().Be(1);
        first.Outcome.Should().Be("partial");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerRetry);
        Logs.Should().Contain("PAYOUT_REF_BACKFILL_FAILED step=put escrowId=escrow-b ref=buyer reason=[ProvisionedThroughputExceededException]");

        _putFails = _ => null;
        var second = await Backfill().RunAsync(null, Slice(), _context);

        second.Written.Should().Be(1);
        second.Present.Should().Be(1);
        second.Outcome.Should().Be("ok");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
    }

    [Fact]
    public async Task ARunOutOfTime_AnswersACursor_AndTheResumedRunNeitherSkipsNorRepeats()
    {
        for (var i = 1; i <= 5; i++)
        {
            Escrow($"escrow-{i}", ("farmer", $"TB-00{i}", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        }

        // Two escrows per page, and each page takes 2.5 minutes of the 4 minute slice: the first run reads one page.
        _perPage = TimeSpan.FromMinutes(2.5);
        var first = await Backfill().RunAsync(null, Slice(), _context);

        first.ScanDone.Should().BeFalse();
        first.Written.Should().Be(2);
        first.Cursor.Should().Be("escrow-2|2026-10-01T08:00:00.0000000Z");
        first.Outcome.Should().Be("partial");
        MarkerStatus().Should().BeNull();

        _elapsed = TimeSpan.Zero;
        _perPage = TimeSpan.Zero;
        var second = await Backfill().RunAsync(first.Cursor, Slice(), _context);

        second.ScanDone.Should().BeTrue();
        second.Written.Should().Be(3);
        second.Present.Should().Be(0);
        second.Outcome.Should().Be("ok");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
        // The resumed run starts right after the last escrow the first one handled.
        _scans[2].ExclusiveStartKey["escrowId"].S.Should().Be("escrow-2");
    }

    [Fact]
    public async Task AResumedChainAfterAFailure_DoesNotWriteDone_OnlyARunFromTheStartDoes()
    {
        Escrow("escrow-1", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        Escrow("escrow-2", ("farmer", "TB-002", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        _references[PayoutReferenceBackfill.MarkerKey] = new Dictionary<string, AttributeValue>
        {
            ["idempotencyKey"] = S(PayoutReferenceBackfill.MarkerKey),
            ["status"] = S(PayoutReferenceBackfill.MarkerRetry)
        };

        var resumed = await Backfill().RunAsync("escrow-1|2026-10-01T08:00:00.0000000Z", Slice(), _context);

        resumed.ScanDone.Should().BeTrue();
        resumed.Written.Should().Be(1);
        resumed.Outcome.Should().Be("partial");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerRetry);

        var fromStart = await Backfill().RunAsync(null, Slice(), _context);

        fromStart.Outcome.Should().Be("ok");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerDone);
    }

    [Fact]
    public async Task AScanPageThatFails_StopsWithTheCursor_AndNeverThrows()
    {
        Escrow("escrow-1", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        Escrow("escrow-2", ("farmer", "TB-002", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        Escrow("escrow-3", ("farmer", "TB-003", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        var pages = 0;
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            ++pages == 2 ? throw new ProvisionedThroughputExceededException("slow down") : Scan(call.Arg<ScanRequest>()));

        var counts = await Backfill().RunAsync(null, Slice(), _context);

        counts.Failed.Should().Be(1);
        counts.Written.Should().Be(2);
        counts.Cursor.Should().Be("escrow-2|2026-10-01T08:00:00.0000000Z");
        MarkerStatus().Should().Be(PayoutReferenceBackfill.MarkerRetry);
        Logs.Should().Contain("PAYOUT_REF_BACKFILL_FAILED step=scan reason=[ProvisionedThroughputExceededException]");
    }

    [Theory]
    [InlineData("escrow-1|2026-10-01T08:00:00Z", true)]
    [InlineData("escrow-1", false)]
    [InlineData("|2026-10-01T08:00:00Z", false)]
    [InlineData("escrow-1|", false)]
    [InlineData("a|b|c", false)]
    public void TheCursorIsTheEscrowsKey(string cursor, bool valid)
    {
        var key = PayoutReferenceBackfill.KeyOf(cursor);
        (key != null).Should().Be(valid);
        if (valid)
        {
            PayoutReferenceBackfill.CursorOf(key).Should().Be(cursor);
        }
    }

    // ── The escrow-cron action ──────────────────────────────────────────────

    private EscrowCronFunction Cron(PayoutReferenceBackfill? backfill)
    {
        var escrows = Substitute.For<IEscrowRepository>();
        var audit = Substitute.For<IEscrowAuditRepository>();
        var gateway = Substitute.For<ITelebirrGateway>();
        var service = new EscrowService(escrows, audit, Substitute.For<IEscrowIdempotencyRepository>(), gateway);
        return new EscrowCronFunction(service, escrows, audit, gateway) { PayoutReferences = backfill };
    }

    private async Task<APIGatewayProxyResponse> InvokeAsync(EscrowCronFunction cron, Dictionary<string, string> body)
        => await cron.FunctionHandler(new APIGatewayProxyRequest { Body = JsonSerializer.Serialize(body) }, _context);

    [Fact]
    public async Task TheAction_AnswersTheCounts()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));

        var response = await InvokeAsync(Cron(Backfill()), new() { ["action"] = "backfill_payout_references" });

        response.StatusCode.Should().Be(200, response.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
        body["action"].Should().Be("backfill_payout_references");
        body["outcome"].Should().Be("ok");
        body["written"].Should().Be("1");
        body["conflicts"].Should().Be("0");
        body["marker"].Should().Be("done");
        body["skipped"].Should().Be("false");
        body.Should().NotContainKey("scanCursor");
        // It moves no money: never a settlement-failure line.
        Logs.Should().NotContain("ESCROW_SETTLEMENT_FAILED");
    }

    [Fact]
    public async Task TheAction_RefusesACursorItCannotRead()
    {
        var response = await InvokeAsync(Cron(Backfill()),
            new() { ["action"] = "backfill_payout_references", ["scanFrom"] = "not-a-cursor" });

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_CRON_VALIDATION");
        _scans.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAction_WithoutTheBackfillWired_Answers503()
    {
        var response = await InvokeAsync(Cron(null), new() { ["action"] = "backfill_payout_references" });

        response.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task TheAction_AFailureOfItsOwn_IsNeverASettlementFailure()
    {
        Escrow("escrow-a", ("farmer", "TB-001", "admin-a", "2026-10-03T10:00:00.0000000Z"));
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await InvokeAsync(Cron(Backfill()), new() { ["action"] = "backfill_payout_references" });

        response.StatusCode.Should().Be(200, response.Body);
        JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!["outcome"].Should().Be("partial");
        Logs.Should().NotContain("ESCROW_SETTLEMENT_FAILED");
    }
}

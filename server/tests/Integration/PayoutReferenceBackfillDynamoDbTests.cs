using System.Globalization;
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

/// <summary>Own tables: the backfill Scans the whole escrows table.</summary>
[CollectionDefinition(Name)]
public sealed class PayoutReferenceBackfillDynamoDbCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "PayoutReferenceBackfillDynamoDb";
}

/// <summary>
/// FUP-215 against real DynamoDB (LocalStack in CI): the escrow-cron action <c>backfill_payout_references</c> writes
/// the FUP-05 reference item of each manual payout recorded before escrow-protocol 2.12.0, through the filtered Scan,
/// the conditional put and the strongly consistent read of a taken key as DynamoDB evaluates them. A legacy record
/// gets the record path's own item and a new record of its reference is then refused (<c>ESCROW_047</c>); gateway legs
/// get none; a key already held by the same leg is left as it is; a reference on two legs keeps its first holder and is
/// logged; the marker bounds the action; a run out of time resumes from its cursor. Each test reads only its own rows:
/// the others share the table.
/// </summary>
[Collection(PayoutReferenceBackfillDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PayoutReferenceBackfillDynamoDbTests
{
    private const string CreatedAt = "2026-09-20T10:00:00.0000000Z";
    private const string AdminA = "admin-fup215-a";
    private const string AdminB = "admin-fup215-b";

    private readonly LocalDynamoDbFixture _db;

    public PayoutReferenceBackfillDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowAuditRepository Audit => new(Ddb, _db.EscrowAudit);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private static string NewId(string label) => $"escrow-order-f215{label}-{Guid.NewGuid():N}"[..48];

    private static string NewRef(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>An escrow whose 1.x legs were recorded before 2.12.0: the leg attributes, no reference item.</summary>
    private async Task<string> SeedLegacyRecordAsync(string label, string status, string op,
        params (string Leg, string Ref, string PaidBy, string PaidAt)[] legs)
    {
        var escrowId = NewId(label);
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = status,
            ["buyerId"] = "buyer-f215",
            ["farmerId"] = "farmer-f215",
            ["amount"] = 2_000m,
            ["farmerPayout"] = 1_950m,
            ["commission"] = 50m,
            ["currency"] = "ETB",
            ["version"] = 5,
            [ManualPayoutFields.Op] = op,
            [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported
        };
        foreach (var (leg, reference, paidBy, paidAt) in legs)
        {
            item[ManualPayoutFields.StatusAttr(leg)] = ManualPayoutFields.LegPaid;
            item[ManualPayoutFields.RefAttr(leg)] = reference;
            item[ManualPayoutFields.PaidByAttr(leg)] = paidBy;
            item[ManualPayoutFields.PaidAtAttr(leg)] = paidAt;
        }

        await Escrows.CreateAsync(item);
        return escrowId;
    }

    /// <summary>A ledger escrow with its rows as they were left: <paramref name="rows"/> (ref, paidBy, telebirrRef).</summary>
    private async Task<string> SeedLedgerAsync(string label, params (string Ref, string Status, string? PaidBy, string? TelebirrRef)[] rows)
    {
        var escrowId = NewId(label);
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "completed",
            ["buyerId"] = "buyer-f215",
            ["farmerId"] = "farmer-f215",
            ["amount"] = 17_250m,
            ["currency"] = "ETB",
            ["version"] = 7,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger
        });
        foreach (var (reference, status, paidBy, telebirrRef) in rows)
        {
            await Ddb.PutItemAsync(new PutItemRequest
            {
                TableName = _db.EscrowLedger,
                Item = EscrowLedgerRepository.ToItem(new LedgerRow
                {
                    EscrowId = escrowId,
                    Ref = reference,
                    Kind = LedgerFields.KindRelease,
                    Amount = 1_000m,
                    Status = status,
                    GatewayRef = LedgerRefs.GatewayRef(escrowId, reference),
                    Reason = "delivery_confirmed",
                    Actor = "system",
                    PaidBy = paidBy,
                    TelebirrRef = telebirrRef,
                    CreatedAt = "2026-09-21T10:00:00.0000000Z",
                    UpdatedAt = "2026-09-22T10:00:00.0000000Z",
                    DoneAt = status == LedgerFields.StatusDone ? "2026-09-22T10:00:00.0000000Z" : null,
                    Version = 3
                })
            });
        }

        return escrowId;
    }

    /// <summary>A 1.x refund waiting in the manual queue: one buyer leg, pending (as FUP-05's tests seed it).</summary>
    private async Task<string> SeedQueuedRefundAsync(string label)
    {
        var escrowId = NewId(label);
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = CreatedAt,
            ["status"] = "escrowed",
            ["buyerId"] = "buyer-f215",
            ["farmerId"] = "farmer-f215",
            ["amount"] = 2_000m,
            ["farmerPayout"] = 1_950m,
            ["commission"] = 50m,
            ["currency"] = "ETB",
            ["version"] = 3,
            ["settlementLock"] = "refund",
            [ManualPayoutFields.Marker] = ManualPayoutFields.MarkerOpen,
            [ManualPayoutFields.QueuedAt] = "2026-11-02T09:00:00.0000000Z",
            [ManualPayoutFields.Op] = ManualPayoutFields.OpRefund,
            [ManualPayoutFields.Reason] = ManualPayoutFields.ReasonPayoutNotSupported,
            [ManualPayoutFields.FromStatus] = "escrowed",
            [ManualPayoutFields.StatusAttr("buyer")] = ManualPayoutFields.LegPending
        });
        return escrowId;
    }

    private EscrowService RecordService() => new(
        Escrows, Audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), Substitute.For<ITelebirrGateway>())
    {
        AlarmLog = _ => { },
        LedgerRepo = Ledger,
        ManualPayoutClaims = new ManualPayoutClaimRepository(Ddb, _db.Escrows, _db.EscrowLedger, _db.Idempotency),
        PayoutReferenceTable = _db.Idempotency
    };

    private EscrowCronFunction Cron(int pageSize = 100, Func<CronRunBudget>? budget = null)
    {
        var gateway = Substitute.For<ITelebirrGateway>();
        var service = new EscrowService(Escrows, Audit, new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway);
        return new EscrowCronFunction(service, Escrows, Audit, gateway,
            idempotencyRepo: new EscrowIdempotencyRepository(Ddb, _db.Idempotency))
        {
            PayoutReferences = new PayoutReferenceBackfill(Ddb, _db.Escrows, _db.Idempotency, Ledger) { PageSize = pageSize },
            BudgetFactory = budget ?? CronRunBudget.StartNew
        };
    }

    private static async Task<(Dictionary<string, string> Body, string Logs)> RunAsync(EscrowCronFunction cron, string? scanFrom = null)
    {
        var payload = new Dictionary<string, string> { ["action"] = PayoutReferenceBackfill.Action };
        if (scanFrom != null)
        {
            payload["scanFrom"] = scanFrom;
        }

        var context = new TestLambdaContext();
        var response = await cron.FunctionHandler(new APIGatewayProxyRequest { Body = JsonSerializer.Serialize(payload) }, context);
        response.StatusCode.Should().Be(200, response.Body);
        return (JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!, ((TestLambdaLogger)context.Logger).Buffer.ToString());
    }

    /// <summary>The marker removed, as the runbook does to make the next run read again.</summary>
    private Task DeleteMarkerAsync() => Ddb.DeleteItemAsync(new DeleteItemRequest
    {
        TableName = _db.Idempotency,
        Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = new() { S = PayoutReferenceBackfill.MarkerKey } }
    });

    private async Task<Dictionary<string, AttributeValue>?> ItemAsync(string key)
    {
        var response = await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Idempotency,
            Key = new Dictionary<string, AttributeValue> { ["idempotencyKey"] = new() { S = key } },
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private Task<Dictionary<string, AttributeValue>?> ReferenceItemAsync(string externalRef) => ItemAsync(PayoutReferenceFields.Key(externalRef));

    /// <summary>An item's scalar values, to compare two reads of it.</summary>
    private static Dictionary<string, string?> Values(Dictionary<string, AttributeValue>? item)
        => item?.ToDictionary(kv => kv.Key, kv => kv.Value.S ?? kv.Value.N) ?? new Dictionary<string, string?>();

    private static long TtlOf(string recordedAt)
        => new DateTimeOffset(DateTime.Parse(recordedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime())
            .AddDays(PayoutReferenceFields.TtlDays).ToUnixTimeSeconds();

    [LocalDynamoDbFact]
    public async Task LegacyRecords_GetTheRecordsOwnItem_GatewayLegsNone_AndANewRecordOfOneIsRefused()
    {
        var farmerRef = NewRef("TBF");
        var gatewayRef = NewRef("GWT");
        var ledgerRef = NewRef("TBL");
        var gatewayLedgerRef = NewRef("GWL");
        var paidAt = Iso(DateTime.UtcNow.AddDays(-40));
        var released = await SeedLegacyRecordAsync("a", "completed", ManualPayoutFields.OpRelease,
            ("farmer", farmerRef, AdminA, paidAt),
            ("trucker", gatewayRef, ManualPayoutFields.PaidByGateway, paidAt));
        var ledger = await SeedLedgerAsync("l",
            (LedgerRefs.ReleaseFarmer, LedgerFields.StatusDone, AdminB, ledgerRef),
            (LedgerRefs.ReleaseTrucker("trucker-f215"), LedgerFields.StatusDone, LedgerFields.PaidByGateway, gatewayLedgerRef));
        await DeleteMarkerAsync();

        var (body, logs) = await RunAsync(Cron());

        body["outcome"].Should().Be("ok");
        body["marker"].Should().Be(PayoutReferenceBackfill.MarkerDone);
        body["failed"].Should().Be("0");
        logs.Should().Contain("PAYOUT_REF_BACKFILL escrows=");

        var item = (await ReferenceItemAsync(farmerRef))!;
        item["kind"].S.Should().Be(PayoutReferenceFields.Kind);
        item[PayoutReferenceFields.EscrowId].S.Should().Be(released);
        item[PayoutReferenceFields.Ref].S.Should().Be("farmer");
        item[PayoutReferenceFields.ExternalRef].S.Should().Be(farmerRef);
        item[PayoutReferenceFields.RecordedBy].S.Should().Be(AdminA);
        item[PayoutReferenceFields.RecordedAt].S.Should().Be(paidAt);
        item["expiresAt"].N.Should().Be(TtlOf(paidAt).ToString(CultureInfo.InvariantCulture));
        var ledgerItem = (await ReferenceItemAsync(ledgerRef))!;
        ledgerItem[PayoutReferenceFields.EscrowId].S.Should().Be(ledger);
        ledgerItem[PayoutReferenceFields.Ref].S.Should().Be(LedgerRefs.ReleaseFarmer);
        ledgerItem[PayoutReferenceFields.RecordedBy].S.Should().Be(AdminB);
        (await ReferenceItemAsync(gatewayRef)).Should().BeNull();
        (await ReferenceItemAsync(gatewayLedgerRef)).Should().BeNull();
        (await ItemAsync(PayoutReferenceBackfill.MarkerKey))!["status"].S.Should().Be(PayoutReferenceBackfill.MarkerDone);

        // The point of it: a new record of the legacy reference (another case) on another leg is refused, naming it.
        var queued = await SeedQueuedRefundAsync("q");
        var refused = await RecordService().CompleteManualPayoutAsync(queued, "buyer", farmerRef.ToLowerInvariant(), AdminB,
            adminGroupVerified: true);
        refused.Error!.Code.Should().Be("ESCROW_047");
        refused.Error.Message.Should().Contain($"escrow {released}, leg farmer");
        (await Escrows.GetByIdAsync(queued, consistentRead: true))![ManualPayoutFields.StatusAttr("buyer")]
            .Should().Be(ManualPayoutFields.LegPending);

        // Bounded: with the marker done a run reads nothing.
        var (again, _) = await RunAsync(Cron());
        again["skipped"].Should().Be("true");
        again["escrows"].Should().Be("0");
    }

    [LocalDynamoDbFact]
    public async Task AKeyAlreadyHeldByTheSameLeg_IsLeftAsItIs()
    {
        // Recorded since 2.12.0: the record wrote its own item in its transaction.
        var reference = NewRef("TBS");
        var paidAt = Iso(DateTime.UtcNow.AddDays(-2));
        var escrowId = await SeedLegacyRecordAsync("s", "refunded", ManualPayoutFields.OpRefund, ("buyer", reference, AdminA, paidAt));
        var put = PayoutReferenceFields.BuildPut(new PayoutReferenceRecord(_db.Idempotency, reference, escrowId, "buyer", AdminA, paidAt)).Put;
        await Ddb.PutItemAsync(new PutItemRequest { TableName = put.TableName, Item = put.Item });
        await DeleteMarkerAsync();

        var (body, logs) = await RunAsync(Cron());

        int.Parse(body["present"], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
        Values(await ReferenceItemAsync(reference)).Should().Equal(Values(put.Item));
        logs.Should().NotContain($"alsoOn={escrowId}");
    }

    [LocalDynamoDbFact]
    public async Task AReferenceOnTwoLegsBefore212_KeepsItsFirstHolder_AndIsLoggedWithBothPlaces()
    {
        var reference = NewRef("TBD");
        var first = await SeedLegacyRecordAsync("d1", "completed", ManualPayoutFields.OpRelease,
            ("farmer", reference, AdminA, Iso(DateTime.UtcNow.AddDays(-30))));
        var second = await SeedLegacyRecordAsync("d2", "refunded", ManualPayoutFields.OpRefund,
            ("buyer", reference.ToLowerInvariant(), AdminB, Iso(DateTime.UtcNow.AddDays(-20))));
        await DeleteMarkerAsync();

        var (body, logs) = await RunAsync(Cron());

        int.Parse(body["conflicts"], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
        // The Scan meets the two in key-hash order: whichever came first holds the reference, the other is logged.
        var holder = (await ReferenceItemAsync(reference))!;
        var (heldBy, heldLeg, other, otherLeg) = holder[PayoutReferenceFields.EscrowId].S == first
            ? (first, "farmer", second, "buyer")
            : (second, "buyer", first, "farmer");
        holder[PayoutReferenceFields.Ref].S.Should().Be(heldLeg);
        logs.Should().Contain($"PAYOUT_REF_BACKFILL_CONFLICT reference={reference} heldBy={heldBy}/{heldLeg}")
            .And.Contain($"alsoOn={other}/{otherLeg}");

        // A rerun never overwrites it, and says so again.
        await DeleteMarkerAsync();
        var (_, rerunLogs) = await RunAsync(Cron());
        Values(await ReferenceItemAsync(reference)).Should().Equal(Values(holder));
        rerunLogs.Should().Contain($"heldBy={heldBy}/{heldLeg}").And.Contain($"alsoOn={other}/{otherLeg}");
    }

    [LocalDynamoDbFact]
    public async Task ARunOutOfTime_AnswersACursor_AndTheResumedRunFinishes()
    {
        var references = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var reference = NewRef($"TBR{i}");
            references.Add(reference);
            await SeedLegacyRecordAsync($"r{i}", "completed", ManualPayoutFields.OpRelease,
                ("farmer", reference, AdminA, Iso(DateTime.UtcNow.AddDays(-10))));
        }

        await DeleteMarkerAsync();

        // A clock that moves a minute at every look: the 4 minute slice allows three, so at most two escrows.
        var ticks = 0;
        var (stopped, _) = await RunAsync(Cron(pageSize: 1, budget: () => new CronRunBudget(() => TimeSpan.FromMinutes(ticks++))));

        stopped["scanDone"].Should().Be("false");
        stopped["outcome"].Should().Be("partial");
        stopped.Should().ContainKey("scanCursor");
        (await ItemAsync(PayoutReferenceBackfill.MarkerKey)).Should().BeNull();

        var (resumed, _) = await RunAsync(Cron(pageSize: 1), stopped["scanCursor"]);

        resumed["scanDone"].Should().Be("true");
        resumed["outcome"].Should().Be("ok");
        resumed["marker"].Should().Be(PayoutReferenceBackfill.MarkerDone);
        foreach (var reference in references)
        {
            (await ReferenceItemAsync(reference)).Should().NotBeNull("the two runs together read the whole table");
        }
    }
}

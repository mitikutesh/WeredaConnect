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
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-20 against real DynamoDB (LocalStack in CI): the ledger writes' conditions as DynamoDB evaluates them.
/// A decision lands with its rows, its audit rows and the escrow update in one transaction or not at all; a
/// stale escrow version, a leg no longer held or a ref already written refuses it; the calling stamp is taken
/// once; a done row leaves <c>LedgerOpenIndex</c>; the existing escrow writes skip a ledger escrow whose crop
/// leg is decided; and the admin split verdict, end to end, from the resolve endpoint to a completed escrow.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EscrowLedgerDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;

    public EscrowLedgerDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private EscrowRepository Escrows => new(Ddb, _db.Escrows);
    private EscrowLedgerRepository Ledger => new(Ddb, _db.EscrowLedger, _db.Escrows, _db.EscrowAudit);

    private static string NewId(string label) => $"escrow-order-{label}-{Guid.NewGuid():N}"[..48];

    private async Task<string> SeedAsync(string label, string status = "escrowed", Action<Dictionary<string, object>>? change = null)
    {
        var escrowId = NewId(label);
        var item = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId, ["createdAt"] = CreatedAt, ["updatedAt"] = CreatedAt, ["status"] = status,
            ["buyerId"] = "buyer-l", ["farmerId"] = "farmer-l", ["truckerId"] = "trucker-l",
            ["amount"] = 17_250m, ["farmerPayout"] = 15_000m, ["truckerPayout"] = 1_500m, ["commission"] = 750m,
            ["currency"] = "ETB", ["source"] = "order", ["telebirrHoldRef"] = "HOLD-L",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z", ["version"] = 3,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m,
            [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        change?.Invoke(item);
        await Escrows.CreateAsync(item);
        return escrowId;
    }

    private EscrowService Service(ScriptedGateway gateway, DateTime? now = null) => new(
        Escrows, new EscrowAuditRepository(Ddb, _db.EscrowAudit), new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway)
    {
        AlarmLog = _ => { },
        LedgerRepo = Ledger,
        LedgerClock = () => now ?? DateTime.UtcNow
    };

    private async Task<LedgerEscrow> StateAsync(string escrowId)
        => LedgerEscrow.From((await Escrows.GetByIdAsync(escrowId, consistentRead: true))!);

    private async Task<List<Dictionary<string, object>>> AuditAsync(string escrowId)
        => await new EscrowAuditRepository(Ddb, _db.EscrowAudit).GetAuditTrailAsync(escrowId, consistentRead: true);

    [LocalDynamoDbFact]
    public async Task ADecision_LandsWithItsRowsAndAuditRows_InOneTransaction()
    {
        var escrowId = await SeedAsync("decide");
        var service = Service(new ScriptedGateway());

        var decided = await service.DecideLedgerAsync((await Escrows.GetByIdAsync(escrowId, true))!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, true, "delivery_confirmed"), "system", callNow: false);

        decided.Outcome.Should().Be(EscrowService.LedgerDecideOutcome.Written);
        var state = await StateAsync(escrowId);
        state.Version.Should().Be(4);
        state.Legs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
        });
        state.ReleasedTotal.Should().Be(16_500m);
        (await Ledger.GetRowsAsync(escrowId)).Select(r => (r.Ref, r.Status, r.Amount)).Should().BeEquivalentTo(new[]
        {
            ("release#farmer", "pending", 15_000m), ("release#trucker#trucker-l", "pending", 1_500m)
        });
        (await AuditAsync(escrowId)).Count(r => (string)r["action"] == "ledger_pending").Should().Be(2);
    }

    [LocalDynamoDbFact]
    public async Task AStaleVersion_ALegNoLongerHeld_OrARefAlreadyWritten_RefusesTheWholeDecision()
    {
        var escrowId = await SeedAsync("refuse");
        var before = await StateAsync(escrowId);
        var plan = EscrowLedgerBook.PlanRelease(before, true, false, "x")!;
        LedgerRow Row(string reference) => new()
        {
            EscrowId = escrowId, Ref = reference, Kind = "release", Amount = 15_000m, Status = "pending",
            GatewayRef = LedgerRefs.GatewayRef(escrowId, reference), Reason = "x", Actor = "system",
            CreatedAt = CreatedAt, UpdatedAt = CreatedAt, DueAt = CreatedAt, Version = 1
        };
        LedgerAudit Audit(string ts) => new(escrowId, ts, "ledger_pending", "escrowed", "escrowed", "system", "ref=x");

        // A stale version: nothing is written, not even the audit row.
        (await Ledger.DecideAsync(new LedgerDecision(plan.Before with { Version = 2 }, plan.After,
                new[] { Row("release#farmer") }, new[] { Audit("2026-11-07T10:00:00.0000001Z") })))
            .Should().Be(LedgerWrite.EscrowConflict);
        (await Ledger.GetRowsAsync(escrowId)).Should().BeEmpty();
        (await AuditAsync(escrowId)).Should().BeEmpty();

        // The real decision.
        (await Ledger.DecideAsync(new LedgerDecision(plan.Before, plan.After,
                new[] { Row("release#farmer") }, new[] { Audit("2026-11-07T10:00:00.0000002Z") })))
            .Should().Be(LedgerWrite.Written);

        // The same ref again (another worker that read the same row).
        var replay = EscrowLedgerBook.PlanRelease(before with { Version = 4 }, true, false, "x")!;
        (await Ledger.DecideAsync(new LedgerDecision(replay.Before, replay.After,
                new[] { Row("release#farmer") }, new[] { Audit("2026-11-07T10:00:00.0000003Z") })))
            .Should().Be(LedgerWrite.RowExists);

        // The crop leg is no longer held: a different ref that moves it is refused by the leg condition.
        (await Ledger.DecideAsync(new LedgerDecision(replay.Before, replay.After,
                new[] { Row("refund#all") }, new[] { Audit("2026-11-07T10:00:00.0000004Z") })))
            .Should().Be(LedgerWrite.EscrowConflict);
        (await Ledger.GetRowsAsync(escrowId)).Select(r => r.Ref).Should().Equal("release#farmer");
    }

    [LocalDynamoDbFact]
    public async Task TheCallingStamp_IsTakenOnce_AndADoneRowLeavesTheOpenIndex()
    {
        var escrowId = await SeedAsync("stamp");
        var gateway = new ScriptedGateway();
        var service = Service(gateway);
        var decided = await service.DecideLedgerAsync((await Escrows.GetByIdAsync(escrowId, true))!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, false, "x"), "system", callNow: false);
        var row = decided.Rows.Single();
        var future = DateTime.UtcNow.AddHours(1).ToString("o");

        (await Ledger.QueryOpenAsync("pending", future, 500, null)).Rows.Should().Contain(r => r.EscrowId == escrowId);
        (await Ledger.TryStampCallingAsync(row, DateTime.UtcNow.ToString("o"), future)).Should().BeTrue();
        (await Ledger.TryStampCallingAsync(row, DateTime.UtcNow.ToString("o"), future)).Should().BeFalse();

        var stamped = (await Ledger.GetRowAsync(escrowId, row.Ref))!;
        var done = await service.CompleteLedgerRowAsync(stamped, "TB-L-1", "gateway", 1, "system");

        done.EscrowStatus.Should().Be("released");
        (await StateAsync(escrowId)).Status.Should().Be("released");
        var after = (await Ledger.GetRowAsync(escrowId, row.Ref))!;
        (after.Status, after.CallingSince, after.TelebirrRef).Should().Be(("done", null, "TB-L-1"));
        foreach (var status in new[] { "pending", "sent", "failed", "manual" })
        {
            (await Ledger.QueryOpenAsync(status, future, 500, null)).Rows.Should().NotContain(r => r.EscrowId == escrowId);
        }
    }

    [LocalDynamoDbFact]
    public async Task FUP29_APayoutRowOfASuspendedPayee_IsDeferredAnHour_NeverCalled_AndPaidOnceUnsuspended()
    {
        // The deferral write and its audit row as DynamoDB evaluates them: the row keeps its status, takes
        // the risk reason and the new due time under its version condition, and LedgerOpenIndex hands it
        // back only once the hour has passed.
        var escrowId = await SeedAsync("riskgate", change: e =>
        {
            e["truckerPayout"] = 0m;
            e["amount"] = 15_750m;
            e[LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 0m, 750m);
        });
        var now = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var suspended = true;
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("farmer-l").Returns(_ => new UserDto
        {
            UserId = "farmer-l",
            PhoneNumber = "+251911000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            FaydaVerified = true,
            Suspended = suspended,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
        var gateway = new ScriptedGateway();
        var alarms = new List<string>();
        var service = new EscrowService(Escrows, new EscrowAuditRepository(Ddb, _db.EscrowAudit),
            new EscrowIdempotencyRepository(Ddb, _db.Idempotency), gateway, users)
        {
            AlarmLog = alarms.Add,
            LedgerRepo = Ledger,
            LedgerClock = () => now
        };
        var decided = await service.DecideLedgerAsync((await Escrows.GetByIdAsync(escrowId, true))!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, false, "delivery_confirmed"), "system", callNow: false);
        var row = decided.Rows.Single();

        var blocked = await service.CallLedgerRowAsync(row, "system");

        blocked.Outcome.Should().Be(EscrowService.LedgerCallOutcome.Blocked);
        gateway.Calls.Should().BeEmpty();
        var stored = (await Ledger.GetRowAsync(escrowId, row.Ref))!;
        (stored.Status, stored.LastCode, stored.CallingSince, stored.Attempts, stored.Version)
            .Should().Be(("pending", "PAYEE_BLOCKED:account_suspended", null, 0, row.Version + 1));
        stored.DueAt.Should().Be(now.AddHours(1).ToString("o"));
        (await AuditAsync(escrowId)).Count(r => (string)r["action"] == "ledger_payee_blocked").Should().Be(1);
        alarms.Should().ContainSingle().Which.Should().StartWith("RISK_SETTLEMENT_SKIPPED");
        (await Escrows.GetByIdAsync(escrowId, true))!.Should().NotContainKey(SettlementBlockFields.Reason);
        (await Ledger.QueryOpenAsync("pending", now.AddMinutes(59).ToString("o"), 500, null)).Rows
            .Should().NotContain(r => r.EscrowId == escrowId);
        (await Ledger.QueryOpenAsync("pending", now.AddMinutes(61).ToString("o"), 500, null)).Rows
            .Should().Contain(r => r.EscrowId == escrowId);

        // A second blocked run writes the new due time and no second audit row; a stale copy of the row loses.
        now = now.AddMinutes(61);
        (await service.CallLedgerRowAsync(row, "system")).Row.Version.Should().Be(row.Version); // stale: nothing written
        (await service.CallLedgerRowAsync(stored, "system")).Outcome.Should().Be(EscrowService.LedgerCallOutcome.Blocked);
        (await AuditAsync(escrowId)).Count(r => (string)r["action"] == "ledger_payee_blocked").Should().Be(1);

        suspended = false;
        var paid = await service.CallLedgerRowAsync((await Ledger.GetRowAsync(escrowId, row.Ref))!, "system");

        paid.Outcome.Should().Be(EscrowService.LedgerCallOutcome.Done);
        gateway.Calls.Should().ContainSingle(c => c.Amount == 15_000m && c.Destination == "+251911000001");
        (await StateAsync(escrowId)).Status.Should().Be("completed");
    }

    [LocalDynamoDbFact]
    public async Task AReleaseThroughTheService_PaysEachLegOnce_AndCompletes()
    {
        var escrowId = await SeedAsync("release");
        var gateway = new ScriptedGateway();

        var result = await Service(gateway).ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = escrowId, Trigger = "delivery_confirmed", DeliveryId = "", IdempotencyKey = $"system:{escrowId}:release:1"
        }, "system");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("completed");
        gateway.Calls.Should().HaveCount(2);
        var state = await StateAsync(escrowId);
        EscrowLedgerBook.CheckInvariants(state, (await Ledger.GetRowsAsync(escrowId)).Select(r => (r.Ref, r.Amount))).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task ADisputeOrAHold_NeverLandsOnALedgerEscrowWhoseCropLegIsDecided()
    {
        var decidedId = await SeedAsync("guard", change: e => e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
        {
            ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
        });
        var heldId = await SeedAsync("guardheld");
        var legacyId = NewId("guardlegacy");
        await Escrows.CreateAsync(new Dictionary<string, object>
        {
            ["escrowId"] = legacyId, ["createdAt"] = CreatedAt, ["status"] = "escrowed", ["expiresAt"] = CreatedAt, ["version"] = 1
        });

        var refused = async () => await Escrows.UpdateStatusAsync(decidedId, CreatedAt, "disputed", "escrowed");
        await refused.Should().ThrowAsync<ConditionalCheckFailedException>();
        await Escrows.UpdateStatusAsync(heldId, CreatedAt, "disputed", "escrowed");
        await Escrows.UpdateStatusAsync(legacyId, CreatedAt, "disputed", "escrowed");

        (await Escrows.TryHoldAsync(decidedId, CreatedAt,
                new EscrowHoldRequest("escrowed", EscrowHoldFields.ForRelease, "account_suspended", DateTime.UtcNow.ToString("o"))))
            .Should().NotBe(EscrowHoldWrite.Held);
    }

    [LocalDynamoDbFact]
    public async Task TheOwedTransportQuery_FindsOnlyLedgerEscrowsWithTheCropDecidedAndTheTransportHeld()
    {
        var owed = await SeedAsync("owed", "released", e => e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
        {
            ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
        });
        var settled = await SeedAsync("settled", "released", e => e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
        {
            ["crop"] = "released", ["transport"] = "released", ["commission"] = "kept"
        });

        var found = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (items, next) = await Ledger.QueryOwedTransportLegsAsync("released", start);
            found.AddRange(items.Select(i => (string)i["escrowId"]));
            start = next;
        } while (start != null);

        found.Should().Contain(owed).And.NotContain(settled);
    }

    [LocalDynamoDbFact]
    public async Task TheAdminSplitVerdict_EndToEnd_FromTheResolveEndpointToACompletedEscrow()
    {
        var escrowId = await SeedAsync("split", "disputed", e => e["disputedBy"] = "buyer-l");
        var admin = new AdminFunction(Ddb, _db.Users, ordersTable: "", escrowsTable: _db.Escrows, deliveriesTable: "",
            escrowAuditTable: _db.EscrowAudit, transactionAuditTable: _db.TransactionAudit);
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/disputes/{escrowId}/resolve",
            Body = JsonSerializer.Serialize(new
            {
                resolution = "partial_refund", action = "split", farmerAmount = 9000m, truckerAmount = 1500m, buyerRefund = 6000m
            }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
            }
        };

        var resolved = await admin.FunctionHandler(request, new TestLambdaContext());

        resolved.StatusCode.Should().Be(200, resolved.Body);
        var recorded = (await Escrows.GetByIdAsync(escrowId, true))!;
        recorded["status"].Should().Be("dispute_resolved");
        VerdictAmounts.From(recorded).Should().Be(new VerdictAmounts(9000m, 1500m, 6000m));

        var gateway = new ScriptedGateway();
        var settled = await Service(gateway).SettleSplitVerdictAsync(escrowId);

        settled.IsSuccess.Should().BeTrue();
        var state = await StateAsync(escrowId);
        state.Status.Should().Be("completed");
        (state.FarmerPayout, state.TruckerPayout, state.Commission, state.RefundedTotal, state.ReleasedTotal)
            .Should().Be((9000m, 1500m, 750m, 6000m, 10_500m));
        gateway.Calls.Select(c => (c.Kind, c.Amount)).Should().BeEquivalentTo(new[]
        {
            ("release", 9000m), ("release", 1500m), ("refund", 6000m)
        });
        var rows = await Ledger.GetRowsAsync(escrowId);
        rows.Should().OnlyContain(r => r.Status == "done");
        EscrowLedgerBook.CheckInvariants(state, rows.Select(r => (r.Ref, r.Amount))).Should().BeNull();
        var actions = (await AuditAsync(escrowId)).Select(r => (string)r["action"]).ToList();
        actions.Should().Contain("admin_dispute_resolved");
        actions.Count(a => a == "ledger_done").Should().Be(3);
    }
}

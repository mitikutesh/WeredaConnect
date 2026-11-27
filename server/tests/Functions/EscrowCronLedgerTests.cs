using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services.Ledger;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-20: the escrow cron and the ledger. <c>ledger_sweep</c> calls, checks and retries open rows; the
/// hourly verdict sweep settles a split (<c>partial_refund</c>) leg by leg; the hourly run pays a transport
/// leg the identity gate held back once the trucker is verified; and the status sweeps never touch a
/// ledger escrow whose crop leg is decided.
/// </summary>
public class EscrowCronLedgerTests
{
    private const string EscrowId = "escrow-order-o5";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";

    private readonly InMemoryLedger _ledger = new();
    private readonly ScriptedGateway _gateway = new();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly List<string> _alarms = new();
    private readonly TestLambdaContext _context = new() { RemainingTime = TimeSpan.FromMinutes(4) };
    private DateTime _now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private readonly EscrowService _service;
    private readonly EscrowCronFunction _function;

    public EscrowCronLedgerTests()
    {
        var repo = _ledger.EscrowRepo;
        repo.StubEmptySweeps();
        repo.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        repo.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        repo.QueryStaleSettlementLocksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        repo.QueryPendingTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        repo.QueryLegacyTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _auditRepo.GetAuditTrailAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(new List<Dictionary<string, object>>());
        foreach (var (id, phone) in new[] { ("farmer-5", "+251911000051"), ("trucker-5", "+251911000052"), ("buyer-5", "+251911000053") })
        {
            Verified(id, phone, true);
        }

        _service = new EscrowService(repo, _auditRepo, Substitute.For<IEscrowIdempotencyRepository>(), _gateway, _users)
        {
            AlarmLog = _alarms.Add,
            LedgerRepo = _ledger,
            LedgerClock = () => _now
        };
        _function = new EscrowCronFunction(_service, repo, _auditRepo, _gateway);
    }

    private void Verified(string id, string phone, bool verified)
        => _users.GetUserByIdAsync(id).Returns(new UserDto
        {
            UserId = id, PhoneNumber = phone, DisplayName = id, Role = "farmer", Woreda = "w",
            IsPhoneVerified = verified, FaydaVerified = true, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, Version = 1
        });

    private void Seed(string status, Action<Dictionary<string, object>>? change = null)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["escrowId"] = EscrowId, ["createdAt"] = CreatedAt, ["updatedAt"] = CreatedAt,
            ["buyerId"] = "buyer-5", ["farmerId"] = "farmer-5", ["truckerId"] = "trucker-5",
            ["amount"] = 17_250m, ["farmerPayout"] = 15_000m, ["truckerPayout"] = 1_500m, ["commission"] = 750m,
            ["currency"] = "ETB", ["status"] = status, ["source"] = "order", ["telebirrHoldRef"] = "HOLD-5",
            ["expiresAt"] = "2026-11-08T10:00:00.0000000Z", ["version"] = 2,
            [LedgerEscrowFields.SettlementModel] = LedgerEscrowFields.ModelLedger,
            [LedgerEscrowFields.LegStatus] = EscrowLedgerBook.BirthLegs(15_000m, 1_500m, 750m),
            [LedgerEscrowFields.RefundedTotal] = 0m, [LedgerEscrowFields.ReleasedTotal] = 0m
        };
        change?.Invoke(item);
        _ledger.Escrows[EscrowId] = item;
    }

    private static APIGatewayProxyRequest Cron(string action) => new() { Body = $"{{\"action\":\"{action}\"}}" };

    private string Status => (string)_ledger.Escrow(EscrowId)["status"];

    [Fact]
    public async Task LedgerSweep_CallsTheDueRows_AndSummarisesTheRun()
    {
        Seed("escrowed");
        await _service.DecideLedgerAsync(_ledger.Copy(EscrowId)!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, true, "delivery_confirmed"), "system", callNow: false);

        var response = await _function.FunctionHandler(Cron("ledger_sweep"), _context);

        response.StatusCode.Should().Be(200);
        var summary = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.DictionaryStringString)!;
        summary["action"].Should().Be("ledger_sweep");
        summary["done"].Should().Be("2");
        summary["truncated"].Should().Be("false");
        Status.Should().Be("completed");
        _gateway.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task LedgerSweep_OutOfTime_StopsBeforeTheNextRow_AndSaysSo()
    {
        Seed("escrowed");
        await _service.DecideLedgerAsync(_ledger.Copy(EscrowId)!, "escrowed",
            s => EscrowLedgerBook.PlanRelease(s, true, true, "x"), "system", callNow: false);
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(5), Logger = new TestLambdaLogger() };

        var response = await _function.FunctionHandler(Cron("ledger_sweep"), context);

        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.DictionaryStringString)!["truncated"].Should().Be("true");
        _gateway.Calls.Should().BeEmpty();
        ((TestLambdaLogger)context.Logger).Buffer.ToString().Should().Contain("sweep_truncated");
    }

    [Fact]
    public async Task TheHourlyVerdictSweep_SettlesASplit_LegByLeg()
    {
        Seed("dispute_resolved", e =>
        {
            e["disputeResolution"] = "partial_refund";
            e["disputeResolvedAt"] = "2026-11-06T10:00:00.0000000Z";
            e[LedgerEscrowFields.VerdictAmounts] = new VerdictAmounts(9_000m, 1_500m, 6_000m).ToMap();
        });
        _ledger.EscrowRepo.StubSweep("dispute_resolved", _ledger.Copy(EscrowId)!);

        var response = await _function.FunctionHandler(Cron("auto_release_and_refund"), _context);

        response.StatusCode.Should().Be(200);
        Status.Should().Be("completed");
        _ledger.RowsOf(EscrowId).Select(r => (r.Ref, r.Amount, r.Status)).Should().BeEquivalentTo(new[]
        {
            ("verdict#buyer", 6_000m, "done"), ("verdict#farmer", 9_000m, "done"), ("verdict#trucker", 1_500m, "done")
        });
        var state = _ledger.State(EscrowId);
        EscrowLedgerBook.CheckInvariants(state, _ledger.RowsOf(EscrowId).Select(r => (r.Ref, r.Amount))).Should().BeNull();
    }

    [Fact]
    public async Task TheHourlyRun_PaysATransportLegHeldBackByTheIdentityGate_OnceTheTruckerIsVerified()
    {
        Seed("escrowed");
        Verified("trucker-5", "+251911000052", false);
        await _service.ReleaseAsync(new ReleaseEscrowRequest
        {
            EscrowId = EscrowId, Trigger = "delivery_confirmed", DeliveryId = "", IdempotencyKey = $"system:{EscrowId}:release:1"
        }, "system");
        Status.Should().Be("released");

        var blocked = await _function.FunctionHandler(Cron("auto_release_and_refund"), _context);
        JsonSerializer.Deserialize(blocked.Body, AppJsonContext.Default.DictionaryStringString)!["ledgerTransportLegsBlocked"].Should().Be("1");

        Verified("trucker-5", "+251911000052", true);
        var paid = await _function.FunctionHandler(Cron("auto_release_and_refund"), _context);

        JsonSerializer.Deserialize(paid.Body, AppJsonContext.Default.DictionaryStringString)!["ledgerTransportLegsDecided"].Should().Be("1");
        Status.Should().Be("completed");
        _gateway.Calls.Count(c => c.Amount == 1_500m).Should().Be(1);
    }

    [Fact]
    public async Task ATransportLegBlockedForADayAfterTheFarmerWasPaid_LogsTheTruckerOverdueLine()
    {
        Seed("released", e =>
        {
            e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
            {
                ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
            };
            e[LedgerEscrowFields.ReleasedTotal] = 15_000m;
            e["releasedAt"] = DateTime.UtcNow.AddHours(-30).ToString("o");
        });
        Verified("trucker-5", "+251911000052", false);
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };

        var response = await _function.FunctionHandler(Cron("auto_release_and_refund"), context);

        JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.DictionaryStringString)!["ledgerTransportLegsOverdue"].Should().Be("1");
        ((TestLambdaLogger)context.Logger).Buffer.ToString().Should()
            .Contain($"TRUCKER_PAYOUT_OVERDUE escrowId={EscrowId} reason=phone_unverified ageHours=30");
    }

    [Fact]
    public async Task ATransportLegBlockedForLessThanADay_IsNotOverdue()
    {
        Seed("released", e =>
        {
            e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
            {
                ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
            };
            e[LedgerEscrowFields.ReleasedTotal] = 15_000m;
            e["releasedAt"] = DateTime.UtcNow.AddHours(-3).ToString("o");
        });
        Verified("trucker-5", "+251911000052", false);
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };

        var response = await _function.FunctionHandler(Cron("auto_release_and_refund"), context);

        var summary = JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.DictionaryStringString)!;
        (summary["ledgerTransportLegsBlocked"], summary["ledgerTransportLegsOverdue"]).Should().Be(("1", "0"));
        ((TestLambdaLogger)context.Logger).Buffer.ToString().Should().NotContain("TRUCKER_PAYOUT_OVERDUE");
        ((TestLambdaLogger)context.Logger).Buffer.ToString().Should().NotContain("TRUCKER_PAYOUT_NEWLY_OVERDUE");
        _ledger.TransportOverdueMarkWrites.Should().Be(0);
    }

    // FUP-436: the edge-triggered line, so a standing trucker-payouts-overdue alarm cannot mask a new transport leg.

    private string SeedOwedTransportLeg(double releasedHoursAgo)
    {
        var releasedAt = DateTime.UtcNow.AddHours(-releasedHoursAgo).ToString("o");
        Seed("released", e =>
        {
            e[LedgerEscrowFields.LegStatus] = new Dictionary<string, object>
            {
                ["crop"] = "released", ["transport"] = "held", ["commission"] = "kept"
            };
            e[LedgerEscrowFields.ReleasedTotal] = 15_000m;
            e["releasedAt"] = releasedAt;
        });
        Verified("trucker-5", "+251911000052", false);
        return releasedAt;
    }

    private static (Dictionary<string, string> Summary, string Logs) Ran(APIGatewayProxyResponse response, TestLambdaContext context)
        => (JsonSerializer.Deserialize(response.Body, AppJsonContext.Default.DictionaryStringString)!,
            ((TestLambdaLogger)context.Logger).Buffer.ToString());

    private static int Count(string logs, string token) => logs.Split('\n').Count(l => l.Contains(token + " "));

    [Fact]
    public async Task ATransportLegThatBecomesOverdue_IsAnnouncedOnce_OnItsReleaseTime_WhileTheOverdueLineRepeats()
    {
        var releasedAt = SeedOwedTransportLeg(30);
        var versionBefore = _ledger.Escrow(EscrowId)["version"];
        var first = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };

        var (summary, logs) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), first), first);

        summary["ledgerTransportLegsNewlyOverdue"].Should().Be("1");
        logs.Should().Contain($"TRUCKER_PAYOUT_NEWLY_OVERDUE escrowId={EscrowId} reason=phone_unverified ageHours=30");
        _ledger.Escrow(EscrowId)[TruckerPayoutFields.OverdueLoggedFor].Should().Be(releasedAt);
        _ledger.Escrow(EscrowId)["version"].Should().Be(versionBefore); // the marker moves no version

        var second = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };
        var (again, againLogs) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), second), second);

        (again["ledgerTransportLegsOverdue"], again["ledgerTransportLegsNewlyOverdue"]).Should().Be(("1", "0"));
        Count(againLogs, "TRUCKER_PAYOUT_OVERDUE").Should().Be(1);
        Count(againLogs, "TRUCKER_PAYOUT_NEWLY_OVERDUE").Should().Be(0);
        _ledger.TransportOverdueMarkWrites.Should().Be(1); // the marked escrow is skipped without a write
    }

    [Fact]
    public async Task ATransportMarkerOfAnEarlierRelease_DoesNotSilenceThisOne()
    {
        SeedOwedTransportLeg(26);
        _ledger.Escrow(EscrowId)[TruckerPayoutFields.OverdueLoggedFor] = "2026-01-01T00:00:00.0000000Z";
        var context = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };

        var (summary, logs) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), context), context);

        summary["ledgerTransportLegsNewlyOverdue"].Should().Be("1");
        Count(logs, "TRUCKER_PAYOUT_NEWLY_OVERDUE").Should().Be(1);
    }

    [Fact]
    public async Task ATransportMarkerThatCannotBeWritten_AnnouncesAnyway_AndTheNextRunAnnouncesItAgain()
    {
        SeedOwedTransportLeg(30);
        _ledger.ThrowOnNextTransportOverdueMark = new ProvisionedThroughputExceededException("throttled");
        var first = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };

        var (summary, logs) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), first), first);

        summary["ledgerTransportLegsNewlyOverdue"].Should().Be("1");
        logs.Should().Contain(
            $"Ledger transport retry: the newly overdue marker of {EscrowId} was not written: [ProvisionedThroughputExceededException]");
        _ledger.Escrow(EscrowId).Should().NotContainKey(TruckerPayoutFields.OverdueLoggedFor);

        var second = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };
        var (again, _) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), second), second);
        again["ledgerTransportLegsNewlyOverdue"].Should().Be("1");

        var third = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };
        var (quiet, _) = Ran(await _function.FunctionHandler(Cron("auto_release_and_refund"), third), third);
        quiet["ledgerTransportLegsNewlyOverdue"].Should().Be("0");
    }

    [Fact]
    public async Task TheReconciliationActionList_NamesTheLedgerSweep()
    {
        var response = await _function.FunctionHandler(Cron("nope"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ledger_sweep");
    }

    // FUP-155: the clean-delivery credit journal (FUP-137) and a ledger escrow. A 1.x escrow queued for a manual
    // payout leaves the journal at once (the admin's close credits it). A ledger escrow answered the same way
    // keeps its entry: a row the ledger sweep finishes is credited only by the recovery of the next hourly run.

    [Fact]
    public async Task AnAutoReleaseTheGatewayDidNotConfirm_KeepsItsJournalEntry_AndTheNextRunCreditsWhatTheLedgerSweepPaid()
    {
        Seed("escrowed", e =>
        {
            e["deliveryId"] = "del-5";
            e["deliveryConfirmedAt"] = DateTime.UtcNow.AddHours(-49).ToString("o");
            e["disputeWindowEndsAt"] = DateTime.UtcNow.AddHours(-1).ToString("o");
        });
        _ledger.EscrowRepo.StubReleaseSweep(_ledger.Copy(EscrowId)!);
        var deliveries = Substitute.For<IDeliveryRepository>();
        deliveries.GetByIdAsync("del-5", true)
            .Returns(new Dictionary<string, object> { ["deliveryId"] = "del-5", ["status"] = "delivered" });

        // One table double for both: the journal item (escrow idempotency table) and the credit rows (fraud signals).
        var db = Substitute.For<IAmazonDynamoDB>();
        var journal = new SortedSet<string>(StringComparer.Ordinal);
        var credits = new List<PutItemRequest>();
        db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<UpdateItemRequest>();
            var id = request.ExpressionAttributeValues[":ids"].SS.Single();
            _ = request.UpdateExpression.StartsWith("ADD ", StringComparison.Ordinal) ? journal.Add(id) : journal.Remove(id);
            return Task.FromResult(new UpdateItemResponse());
        });
        db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var item = new Dictionary<string, AttributeValue>();
            if (journal.Count > 0)
            {
                item[CleanDeliveryCreditJournal.PendingAttribute] = new AttributeValue { SS = journal.ToList() };
            }

            return Task.FromResult(new GetItemResponse { Item = item });
        });
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new(), LastEvaluatedKey = new() });
        db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            credits.Add(ci.Arg<PutItemRequest>());
            return Task.FromResult(new PutItemResponse());
        });
        var cron = new EscrowCronFunction(_service, _ledger.EscrowRepo, _auditRepo, _gateway, deliveries)
        {
            CleanDeliveryCredits = new CleanDeliveryCreditService(db, "test-fraud-signals"),
            CleanDeliveryJournal = new CleanDeliveryCreditJournal(db, "test-escrow-idempotency")
        };

        // Neither payout is answered: both rows are sent, so the money may have moved (202 ESCROW_034).
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.Timeout, null));
        _gateway.Script.Enqueue((false, TelebirrGatewayErrors.Timeout, null));
        var first = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };
        var (summary, _) = Ran(await cron.FunctionHandler(Cron("auto_release_and_refund"), first), first);

        summary["manualPayoutsWaiting"].Should().Be("1");
        Status.Should().Be("escrowed");
        journal.Should().Equal(EscrowId);
        credits.Should().BeEmpty();

        // The ledger sweep asks the gateway after the recheck time: both payouts were made.
        _now += TimeSpan.FromMinutes(16);
        _gateway.StatusAnswer = r => new TransactionStatusResponse
        {
            TransactionRef = r,
            Status = "completed",
            TelebirrRef = $"TB-LATE-{_gateway.StatusChecks.Count}",
            Amount = 0m,
            UpdatedAt = ""
        };
        await _function.FunctionHandler(Cron("ledger_sweep"), _context);
        Status.Should().Be("completed");

        // The next hourly run: the escrow has left the release sweep, and its recovery credits each party once.
        _ledger.EscrowRepo.StubReleaseSweep();
        var second = new TestLambdaContext { RemainingTime = TimeSpan.FromMinutes(4), Logger = new TestLambdaLogger() };
        var (_, logs) = Ran(await cron.FunctionHandler(Cron("auto_release_and_refund"), second), second);

        var releasedAt = (string)_ledger.Escrow(EscrowId)["releasedAt"];
        credits.Select(p => p.Item["userId"].S).Should().BeEquivalentTo("buyer-5", "farmer-5", "trucker-5");
        credits.Should().OnlyContain(p => p.Item["signalKey"].S == $"{releasedAt}#CLEAN_DELIVERY#{EscrowId}");
        logs.Should().Contain("CLEAN_DELIVERY_CREDIT_RECOVERY pending=1 read=1 creditsWritten=3 cleared=1 kept=0");
        journal.Should().BeEmpty();
    }
}

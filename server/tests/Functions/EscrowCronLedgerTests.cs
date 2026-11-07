using System.Text.Json;
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
    public async Task TheReconciliationActionList_NamesTheLedgerSweep()
    {
        var response = await _function.FunctionHandler(Cron("nope"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ledger_sweep");
    }
}

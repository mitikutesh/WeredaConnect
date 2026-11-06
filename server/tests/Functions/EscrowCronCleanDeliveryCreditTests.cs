using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-10 (fraud-detection-architecture.md sections 15 and 20) in escrow-cron: after the auto-release pays a
/// clean trade, each party gets one <c>CLEAN_DELIVERY</c> credit keyed on the escrow's stored release time and
/// its id. A trade with a signal earns none, and a credit that cannot be read or written never fails or undoes
/// the release.
/// </summary>
public class EscrowCronCleanDeliveryCreditTests
{
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string SignalsTable = "test-fraud-signals";

    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new() { RemainingTime = TimeSpan.FromMinutes(4) };

    private readonly List<QueryRequest> _queries = new();
    private readonly List<PutItemRequest> _puts = new();
    private readonly Dictionary<string, QueryResponse> _partitions = new();
    private Exception? _readFailure;
    private readonly Dictionary<string, Exception> _writeFailures = new();
    private readonly Dictionary<string, string> _releasedAt = new();

    public EscrowCronCleanDeliveryCreditTests()
    {
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.StubEmptySweeps();
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _escrows.QueryStaleSettlementLocksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.QueryPendingTruckerLegsAsync(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        _escrows.TryAcquireSettlementLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(true);
        _escrows.When(r => r.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), "released", Arg.Any<string?>(),
                Arg.Any<Dictionary<string, object>?>()))
            .Do(ci => _releasedAt[ci.ArgAt<string>(0)] = ci.ArgAt<Dictionary<string, object>>(4)["releasedAt"].ToString()!);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "GW-REL" });
        _audit.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>()).Returns(true);

        foreach (var (id, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _users.GetUserByIdAsync(id).Returns(new UserDto
            {
                UserId = id,
                PhoneNumber = "+251900000001",
                Role = role,
                Woreda = "w",
                IsPhoneVerified = true,
                FaydaVerified = true,
                CreatedAt = "2025-01-01T00:00:00Z",
                UpdatedAt = "2025-01-01T00:00:00Z",
                Version = 1
            });
        }

        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            _queries.Add(request);
            if (_readFailure != null)
            {
                return Task.FromException<QueryResponse>(_readFailure);
            }

            return Task.FromResult(_partitions.GetValueOrDefault(request.ExpressionAttributeValues[":uid"].S)
                ?? new QueryResponse { Items = new(), LastEvaluatedKey = new() });
        });
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<PutItemRequest>();
            _puts.Add(request);
            return _writeFailures.TryGetValue(request.Item["userId"].S, out var failure)
                ? Task.FromException<PutItemResponse>(failure)
                : Task.FromResult(new PutItemResponse());
        });
    }

    private EscrowCronFunction Cron(bool withCredits = true)
    {
        var service = new EscrowService(_escrows, _audit, _idempotency, _gateway, userRepo: _users, deliveryRepo: _deliveries);
        return new EscrowCronFunction(service, _escrows, _audit, _gateway, _deliveries)
        {
            CleanDeliveryCredits = withCredits ? new CleanDeliveryCreditService(_ddb, SignalsTable) : null
        };
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private async Task<Dictionary<string, string>> RunAsync(EscrowCronFunction cron)
    {
        var response = await cron.FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", "auto_release_and_refund" } })
        }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    /// <summary>A confirmed escrow whose dispute window ended an hour ago, the delivery delivered, no order.</summary>
    private Dictionary<string, object> Confirmed(string id, bool withTrucker = true)
    {
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", id },
            { "createdAt", CreatedAt },
            { "status", "escrowed" },
            { "deliveryId", $"del-{id}" },
            { "buyerId", BuyerId },
            { "farmerId", FarmerId },
            { "amount", 8_000m },
            { "commission", 200m },
            { "farmerPayout", withTrucker ? 6_800m : 7_800m },
            { "currency", "ETB" },
            { "telebirrHoldRef", "HOLD-1" },
            { "expiresAt", DateTime.UtcNow.AddDays(3).ToString("o") },
            { "deliveryConfirmedAt", DateTime.UtcNow.AddHours(-49).ToString("o") },
            { "disputeWindowEndsAt", DateTime.UtcNow.AddHours(-1).ToString("o") },
            { "version", 2 }
        };
        if (withTrucker)
        {
            escrow["truckerId"] = TruckerId;
            escrow["truckerPayout"] = 1_000m;
        }

        _escrows.GetByIdAsync(id).Returns(escrow);
        _escrows.GetByIdAsync(id, Arg.Any<bool>()).Returns(escrow);
        _deliveries.GetByIdAsync($"del-{id}", true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", $"del-{id}" }, { "status", "delivered" }
        });
        return escrow;
    }

    private static QueryResponse SignalPage(string signalKey) => new()
    {
        Items = new() { new() { ["signalKey"] = new AttributeValue { S = signalKey } } },
        LastEvaluatedKey = new()
    };

    [Fact]
    public async Task CleanRelease_CreditsTheBuyerTheFarmerAndTheTruckerOnce()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-clean"));

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        _queries.Select(q => q.ExpressionAttributeValues[":uid"].S).Should().Equal(BuyerId, FarmerId, TruckerId);
        // The trade's signals are looked up on the escrow and its delivery (this escrow has no order).
        _queries[0].FilterExpression.Should().StartWith("#e IN (:e0, :e1)");
        _queries[0].ExpressionAttributeValues[":e0"].S.Should().Be("esc-clean");
        _queries[0].ExpressionAttributeValues[":e1"].S.Should().Be("del-esc-clean");

        _puts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId, TruckerId);
        // The key is the release time the escrow stores, so a replay builds the same one.
        var releasedAt = _releasedAt["esc-clean"];
        _puts.Should().OnlyContain(p => p.Item["signalKey"].S == $"{releasedAt}#CLEAN_DELIVERY#esc-clean"
                                        && p.Item["recordType"].S == "credit"
                                        && p.Item["weight"].N == "-5"
                                        && p.ConditionExpression == "attribute_not_exists(signalKey)"
                                        && p.TableName == SignalsTable);
        Logs.Should().Contain("CLEAN_DELIVERY_CREDITED escrowId=esc-clean credited=3 alreadyCredited=0 failed=0");
    }

    [Fact]
    public async Task TruckerLegWithoutAPayableTrucker_CreditsTheBuyerAndTheFarmer()
    {
        // The escrow pays a trucker but names none, and no order names one: the payout's lookup finds nobody,
        // so the leg stays owed and only the two known parties are credited.
        var escrow = Confirmed("esc-no-trucker");
        escrow.Remove("truckerId");
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        _puts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId);
    }

    [Fact]
    public async Task SelfPickupRelease_CreditsTheBuyerAndTheFarmer()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-pickup", withTrucker: false));

        await RunAsync(Cron());

        _puts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId);
    }

    [Fact]
    public async Task SignalOnTheTrade_CreditsNobodyAndTheReleaseStands()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-flagged"));
        _partitions[FarmerId] = SignalPage("2026-11-01T10:00:00.0000000Z#CANCEL_AFTER_FUND_RATE");

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        await _gateway.Received().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:esc-flagged"));
        _puts.Should().BeEmpty();
        Logs.Should().Contain("CLEAN_DELIVERY_NOT_CREDITED escrowId=esc-flagged reason=signal");
    }

    [Fact]
    public async Task FailedCreditWrite_IsLoggedAndTheReleaseStands()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-write"));
        _writeFailures[FarmerId] = new ProvisionedThroughputExceededException("throttled");

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        _puts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId, TruckerId);
        Logs.Should().Contain(
            $"CLEAN_DELIVERY_CREDIT_FAILED escrowId=esc-write userId=farmer-1 error={MoneyAlarmLog.LogId(nameof(ProvisionedThroughputExceededException))}");
        Logs.Should().Contain("CLEAN_DELIVERY_CREDITED escrowId=esc-write credited=2 alreadyCredited=0 failed=1");
        // A credit is not a settlement: no settlement alarm.
        Logs.Should().NotContain("auto_release_exception");
        Logs.Should().NotContain("SETTLEMENT_FAILED");
    }

    [Fact]
    public async Task FailedSignalRead_IsLoggedAndTheReleaseStands()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-read"));
        _readFailure = new ProvisionedThroughputExceededException("throttled");

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        _puts.Should().BeEmpty();
        Logs.Should().Contain(
            $"CLEAN_DELIVERY_CREDIT_FAILED escrowId=esc-read error={MoneyAlarmLog.LogId(nameof(ProvisionedThroughputExceededException))}");
        Logs.Should().NotContain("auto_release_exception");
        Logs.Should().NotContain("SETTLEMENT_FAILED");
    }

    [Fact]
    public async Task FailedRelease_WritesNoCredit()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-refused"));
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>())
            .Returns(new ReleaseFundsResponse { Success = false, ErrorCode = "GW_REFUSED" });

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("0");
        await _gateway.Received().ReleaseFundsAsync(Arg.Is<ReleaseFundsRequest>(r => r.TransactionRef == "release:esc-refused"));
        _ddb.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RowWithADisputeField_WritesNoCredit()
    {
        // Nothing moves a disputed escrow back to escrowed today; a row an older code path left so is not clean.
        var escrow = Confirmed("esc-legacy");
        escrow["disputedBy"] = BuyerId;
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        summary["released"].Should().Be("1");
        _ddb.ReceivedCalls().Should().BeEmpty();
        Logs.Should().Contain("CLEAN_DELIVERY_NOT_CREDITED escrowId=esc-legacy reason=dispute");
    }

    [Fact]
    public async Task ReleaseAnsweredAsDuplicate_LeavesTheCreditToTheRunThatReleased()
    {
        // Another run took the settlement lock first and released it: this run's answer is a duplicate.
        var escrow = Confirmed("esc-race");
        var released = new Dictionary<string, object>(escrow) { ["status"] = "released", ["releasedAt"] = "2026-11-07T09:00:00.0000000Z" };
        _escrows.GetByIdAsync("esc-race").Returns(escrow, released);
        _escrows.TryAcquireSettlementLockAsync("esc-race", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(false);
        _escrows.StubReleaseSweep(escrow);

        var summary = await RunAsync(Cron());

        // Counted as released (the money did move), with no gateway call of its own and no credit.
        summary["released"].Should().Be("1");
        await _gateway.DidNotReceive().ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        _ddb.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheSignalsTable_NothingIsCredited()
    {
        _escrows.StubReleaseSweep(Confirmed("esc-untabled"));

        var summary = await RunAsync(Cron(withCredits: false));

        summary["released"].Should().Be("1");
        _ddb.ReceivedCalls().Should().BeEmpty();
        Logs.Should().NotContain("CLEAN_DELIVERY");
    }
}

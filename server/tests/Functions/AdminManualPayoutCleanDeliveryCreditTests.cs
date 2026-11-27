using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-137 (fraud-detection-architecture.md section 20): in live mode every auto-release waits in the manual
/// payout queue, so the clean-delivery credit of FRD-10 is written when an admin records the payout and the
/// close writes the escrow's <c>releasedAt</c>. Exactly once per party (a double click, a second admin and a
/// retry write nothing more), and only for a release the hourly auto-release queued: never an admin release,
/// a dispute verdict, a held exit or a refund.
/// </summary>
public class AdminManualPayoutCleanDeliveryCreditTests
{
    private const string UsersTable = "test-users";
    private const string EscrowsTable = "test-escrows";
    private const string AuditTable = "test-escrow-audit";
    private const string TxAuditTable = "test-transaction-audit";
    private const string SignalsTable = "test-fraud-signals";
    private const string IdempotencyTable = "test-escrow-idempotency";
    private const string EscrowId = "escrow-live-1";
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    // The escrow row as the table holds it: every update of the escrows table is applied to it.
    private readonly Dictionary<string, AttributeValue> _escrow;
    private readonly List<PutItemRequest> _creditPuts = new();
    private readonly HashSet<string> _creditRows = new();
    private readonly List<QueryRequest> _signalQueries = new();
    private readonly Dictionary<string, QueryResponse> _signalPartitions = new();

    // FUP-05: the reference items the record transactions wrote.
    private readonly HashSet<string> _references = new();
    private Exception? _signalReadFailure;

    public AdminManualPayoutCleanDeliveryCreditTests()
    {
        _escrow = QueuedRelease();
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            if (request.TableName == EscrowsTable)
            {
                return Task.FromResult(new QueryResponse { Items = new() { new(_escrow) } });
            }

            if (request.TableName != SignalsTable)
            {
                return Task.FromResult(new QueryResponse { Items = new() });
            }

            _signalQueries.Add(request);
            if (_signalReadFailure != null)
            {
                return Task.FromException<QueryResponse>(_signalReadFailure);
            }

            return Task.FromResult(_signalPartitions.GetValueOrDefault(request.ExpressionAttributeValues[":uid"].S)
                ?? new QueryResponse { Items = new(), LastEvaluatedKey = new() });
        });
        _db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<GetItemRequest>();
            return new GetItemResponse
            {
                Item = request.TableName == UsersTable ? User(request.Key["userId"].S) : new()
            };
        });
        _db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<UpdateItemRequest>();
            if (request.TableName == EscrowsTable)
            {
                Apply(request);
            }

            return new UpdateItemResponse();
        });
        // FUP-05: a record is one transaction: the escrow update and its reference item.
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var items = ci.Arg<TransactWriteItemsRequest>().TransactItems;
            var key = items[1].Put.Item["idempotencyKey"].S;
            if (_references.Contains(key))
            {
                return Task.FromException<TransactWriteItemsResponse>(new TransactionCanceledException("canceled")
                {
                    CancellationReasons = new() { new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" } }
                });
            }

            _references.Add(key);
            var update = items[0].Update;
            Apply(new UpdateItemRequest
            {
                TableName = update.TableName,
                UpdateExpression = update.UpdateExpression,
                ExpressionAttributeNames = update.ExpressionAttributeNames,
                ExpressionAttributeValues = update.ExpressionAttributeValues
            });
            return Task.FromResult(new TransactWriteItemsResponse());
        });
        _db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<PutItemRequest>();
            if (request.TableName != SignalsTable)
            {
                return Task.FromResult(new PutItemResponse());
            }

            _creditPuts.Add(request);
            // The conditional put of a row that is already there fails, as in DynamoDB.
            return _creditRows.Add($"{request.Item["userId"].S}|{request.Item["signalKey"].S}")
                ? Task.FromResult(new PutItemResponse())
                : Task.FromException<PutItemResponse>(new ConditionalCheckFailedException("exists"));
        });
    }

    private static AttributeValue S(string value) => new() { S = value };

    /// <summary>
    /// An escrow the hourly auto-release queued in live mode (<c>PAYOUT_NOT_SUPPORTED</c>): still
    /// <c>escrowed</c>, the release lock held, the farmer leg pending, and the origin the queue write stored.
    /// </summary>
    private static Dictionary<string, AttributeValue> QueuedRelease() => new()
    {
        ["escrowId"] = S(EscrowId),
        ["createdAt"] = S("2026-11-01T10:00:00.0000000Z"),
        ["deliveryId"] = S("del-1"),
        ["buyerId"] = S(BuyerId),
        ["farmerId"] = S(FarmerId),
        ["status"] = S("escrowed"),
        ["amount"] = new AttributeValue { N = "8000" },
        ["farmerPayout"] = new AttributeValue { N = "7800" },
        ["commission"] = new AttributeValue { N = "200" },
        ["currency"] = S("ETB"),
        ["settlementLock"] = S("release"),
        ["manualPayout"] = S("open"),
        ["manualPayoutQueuedAt"] = S("2026-11-04T09:00:00.0000000Z"),
        ["manualPayoutOp"] = S("release"),
        ["manualPayoutReason"] = S("PAYOUT_NOT_SUPPORTED"),
        ["manualPayoutFromStatus"] = S("escrowed"),
        ["manualFarmerStatus"] = S("pending"),
        ["releaseOrigin"] = S("auto_release"),
    };

    private void WithTruckerLeg()
    {
        _escrow["truckerId"] = S(TruckerId);
        _escrow["truckerPayout"] = new AttributeValue { N = "1000" };
        _escrow["farmerPayout"] = new AttributeValue { N = "6800" };
        _escrow["manualTruckerStatus"] = S("pending");
    }

    private static Dictionary<string, AttributeValue> User(string userId) => new()
    {
        ["userId"] = S(userId),
        ["phoneNumber"] = S("+251911000001"),
        ["displayName"] = S("Test"),
        ["role"] = S(userId.Split('-')[0]),
        ["woreda"] = S("w"),
        ["isPhoneVerified"] = new AttributeValue { BOOL = true },
        ["faydaVerified"] = new AttributeValue { BOOL = true },
        ["createdAt"] = S("2026-01-01T00:00:00Z"),
        ["updatedAt"] = S("2026-01-01T00:00:00Z"),
    };

    /// <summary>Applies the SET and REMOVE clauses of an escrow update to the row (enough for the queue writes).</summary>
    private void Apply(UpdateItemRequest request)
    {
        var expression = request.UpdateExpression;
        var set = Regex.Match(expression, @"SET (.*?)(?= REMOVE | ADD |$)");
        foreach (Match pair in Regex.Matches(set.Groups[1].Value, @"(#?\w+) = (:\w+)"))
        {
            _escrow[Name(request, pair.Groups[1].Value)] = request.ExpressionAttributeValues[pair.Groups[2].Value];
        }

        var remove = Regex.Match(expression, @"REMOVE (.*?)(?= SET | ADD |$)");
        foreach (var name in remove.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _escrow.Remove(Name(request, name));
        }
    }

    private static string Name(UpdateItemRequest request, string token) =>
        token.StartsWith('#') ? request.ExpressionAttributeNames[token] : token;

    private AdminFunction Function(bool withSignalsTable = true) =>
        new(_db, UsersTable, "", EscrowsTable, "", AuditTable, TxAuditTable, escrowIdempotencyTable: IdempotencyTable,
            fraudSignalsTable: withSignalsTable ? SignalsTable : "");

    private Task<APIGatewayProxyResponse> CompleteAsync(
        string leg, string externalRef, string admin = "admin-1", bool withSignalsTable = true) =>
        Function(withSignalsTable).FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/payouts/{EscrowId}/{leg}/complete",
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "externalRef", externalRef } }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", admin }, { "cognito:groups", "admin" } }
            },
        }, _context);

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private string CreditKey => $"{_escrow["releasedAt"].S}#CLEAN_DELIVERY#{EscrowId}";

    [Fact]
    public async Task ClosingAnAutoReleasesPayout_CreditsEachPartyOnce_KeyedOnTheReleasedAtTheCloseWrote()
    {
        var response = await CompleteAsync("farmer", "TB-REF-0001");

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"escrowStatus\":\"completed\"");
        _escrow["status"].S.Should().Be("completed");
        _escrow.Should().ContainKey("releasedAt");
        _creditPuts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId);
        _creditPuts.Should().OnlyContain(p => p.Item["signalKey"].S == CreditKey
                                              && p.Item["recordType"].S == "credit"
                                              && p.Item["signalType"].S == "CLEAN_DELIVERY"
                                              && p.Item["weight"].N == "-5"
                                              && p.ConditionExpression == "attribute_not_exists(signalKey)");
        // The trade's signals are read on the escrow and its delivery, in each party's partition.
        _signalQueries.Select(q => q.ExpressionAttributeValues[":uid"].S).Should().Equal(BuyerId, FarmerId);
        Logs.Should().Contain($"CLEAN_DELIVERY_CREDITED escrowId={EscrowId} credited=2 alreadyCredited=0 failed=0");
    }

    [Fact]
    public async Task ADoubleClick_ASecondAdminAndARetry_WriteNothingMore()
    {
        (await CompleteAsync("farmer", "TB-REF-0001")).StatusCode.Should().Be(200);
        var key = CreditKey;

        // The same admin clicks again, and another admin records the same reference: both are replays.
        var again = await CompleteAsync("farmer", "TB-REF-0001");
        var other = await CompleteAsync("farmer", "TB-REF-0001", admin: "admin-2");
        // A second admin with another reference is refused.
        var conflict = await CompleteAsync("farmer", "TB-REF-0002", admin: "admin-2");

        again.StatusCode.Should().Be(200);
        again.Body.Should().Contain("\"alreadyRecorded\":true");
        other.StatusCode.Should().Be(200);
        conflict.StatusCode.Should().Be(409);
        // The replays try the same key and fail its condition: one row per party, whatever was clicked.
        _creditPuts.Should().OnlyContain(p => p.Item["signalKey"].S == key);
        _creditRows.Should().BeEquivalentTo(new[] { $"{BuyerId}|{key}", $"{FarmerId}|{key}" });
        Logs.Should().Contain($"CLEAN_DELIVERY_CREDITED escrowId={EscrowId} credited=0 alreadyCredited=2 failed=0");
    }

    [Fact]
    public async Task AReplay_WritesTheCreditAFailedFirstAttemptLost()
    {
        _signalReadFailure = new ProvisionedThroughputExceededException("throttled");
        (await CompleteAsync("farmer", "TB-REF-0001")).StatusCode.Should().Be(200);
        _creditRows.Should().BeEmpty();
        Logs.Should().Contain($"CLEAN_DELIVERY_CREDIT_FAILED escrowId={EscrowId}");

        _signalReadFailure = null;
        (await CompleteAsync("farmer", "TB-REF-0001")).StatusCode.Should().Be(200);

        _creditRows.Should().HaveCount(2);
    }

    [Fact]
    public async Task WithTwoLegs_TheCloseThatWritesReleasedAtCredits_NotTheFirstLeg()
    {
        WithTruckerLeg();

        var first = await CompleteAsync("farmer", "TB-REF-0001");

        // The trucker leg still waits: the escrow is not released, so nobody is credited yet.
        first.StatusCode.Should().Be(200, first.Body);
        _escrow["status"].S.Should().Be("escrowed");
        _escrow.Should().NotContainKey("releasedAt");
        _signalQueries.Should().BeEmpty();
        _creditPuts.Should().BeEmpty();

        var second = await CompleteAsync("trucker", "TB-REF-0002");

        second.StatusCode.Should().Be(200, second.Body);
        _escrow["status"].S.Should().Be("completed");
        _creditPuts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId, TruckerId);
        _creditPuts.Should().OnlyContain(p => p.Item["signalKey"].S == CreditKey);
    }

    [Theory]
    [InlineData("admin_release")] // an admin's release of a plain escrowed escrow: the queue write stored no origin
    [InlineData("dispute_verdict")] // farmer_wins settled by the dispute sweep: no origin, and a dispute on the row
    [InlineData("held_exit")] // an admin's release of a held escrow
    [InlineData("marked_but_disputed")] // even a marked row is not clean with a dispute field
    [InlineData("marked_but_held")]
    [InlineData("ledger_other")] // a ledger escrow an admin decided
    public async Task APayoutThatDidNotComeFromACleanAutoRelease_IsNotCredited(string origin)
    {
        switch (origin)
        {
            case "admin_release":
                _escrow.Remove("releaseOrigin");
                break;
            case "dispute_verdict":
                _escrow.Remove("releaseOrigin");
                _escrow["status"] = S("dispute_resolved");
                _escrow["manualPayoutFromStatus"] = S("dispute_resolved");
                _escrow["disputedBy"] = S(BuyerId);
                _escrow["disputeResolution"] = S("farmer_wins");
                break;
            case "held_exit":
                _escrow.Remove("releaseOrigin");
                _escrow["heldAt"] = S("2026-11-03T09:00:00.0000000Z");
                _escrow["holdReason"] = S("risk_critical");
                break;
            case "marked_but_disputed":
                _escrow["disputeResolution"] = S("farmer_wins");
                break;
            case "marked_but_held":
                _escrow["holdReason"] = S("account_suspended");
                break;
            case "ledger_other":
                _escrow["releaseOrigin"] = S("other");
                break;
        }

        var response = await CompleteAsync("farmer", "TB-REF-0001");

        // The payout itself is recorded as before.
        response.StatusCode.Should().Be(200, response.Body);
        _escrow["status"].S.Should().Be("completed");
        _signalQueries.Should().BeEmpty();
        _creditPuts.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefundPayout_IsNotCredited()
    {
        _escrow["settlementLock"] = S("refund");
        _escrow["manualPayoutOp"] = S("refund");
        _escrow.Remove("manualFarmerStatus");
        _escrow["manualBuyerStatus"] = S("pending");
        // A marker a refund never writes: even so, a refunded escrow is not a clean delivery.

        var response = await CompleteAsync("buyer", "TB-REF-0001");

        response.StatusCode.Should().Be(200, response.Body);
        _escrow["status"].S.Should().Be("refunded");
        _signalQueries.Should().BeEmpty();
        _creditPuts.Should().BeEmpty();
    }

    [Fact]
    public async Task ASignalOnTheTrade_CreditsNobody_AndThePayoutStands()
    {
        _signalPartitions[FarmerId] = new QueryResponse
        {
            Items = new() { new() { ["signalKey"] = S("2026-11-02T10:00:00.0000000Z#IMPOSSIBLE_DELIVERY_TIME") } },
            LastEvaluatedKey = new()
        };

        var response = await CompleteAsync("farmer", "TB-REF-0001");

        response.StatusCode.Should().Be(200, response.Body);
        _creditPuts.Should().BeEmpty();
        Logs.Should().Contain($"CLEAN_DELIVERY_NOT_CREDITED escrowId={EscrowId} reason=signal");
    }

    [Fact]
    public async Task WithoutTheSignalsTable_ThePayoutIsRecordedAndNothingIsCredited()
    {
        var response = await CompleteAsync("farmer", "TB-REF-0001", withSignalsTable: false);

        response.StatusCode.Should().Be(200, response.Body);
        _escrow["status"].S.Should().Be("completed");
        _creditPuts.Should().BeEmpty();
        Logs.Should().NotContain("CLEAN_DELIVERY");
    }
}

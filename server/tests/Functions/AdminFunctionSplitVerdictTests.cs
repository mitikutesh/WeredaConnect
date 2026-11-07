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
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-20 (escrow-protocol.md 2.2.0, P.8): <c>POST /admin/disputes/{id}/resolve</c> with
/// <c>partial_refund</c> and <c>action: split</c>. The split is checked against the legs still held (each
/// amount 0 or more with 2 decimals, the farmer at most the crop leg, the trucker at most the transport
/// leg, the three adding up to the legs held with the commission kept when the farmer is paid), refused
/// with <c>400 ADMIN_021</c> naming the field, and recorded with the verdict while the crop leg is held.
/// </summary>
public class AdminFunctionSplitVerdictTests
{
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly AdminFunction _function;

    public AdminFunctionSplitVerdictTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        _function = new AdminFunction(_dynamoDb, "users-test");
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    /// <summary>A disputed ledger escrow: 15,000 crop, 1,500 transport, 750 commission, every leg held.</summary>
    private static Dictionary<string, AttributeValue> LedgerEscrow(bool ledger = true, params (string Key, AttributeValue Value)[] extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-9"),
            ["createdAt"] = S("2026-09-20T10:00:00Z"),
            ["status"] = S("disputed"),
            ["disputedBy"] = S("buyer-9"),
            ["buyerId"] = S("buyer-9"),
            ["farmerId"] = S("farmer-9"),
            ["truckerId"] = S("trucker-9"),
            ["amount"] = N("17250"),
            ["farmerPayout"] = N("15000"),
            ["truckerPayout"] = N("1500"),
            ["commission"] = N("750"),
            ["version"] = N("4")
        };
        if (ledger)
        {
            item["settlementModel"] = S("ledger");
            item["legStatus"] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue> { ["crop"] = S("held"), ["transport"] = S("held"), ["commission"] = S("held") }
            };
            item["refundedTotal"] = N("0");
            item["releasedTotal"] = N("0");
        }

        foreach (var (key, value) in extra)
        {
            item[key] = value;
        }

        return item;
    }

    private void EscrowReads(params Dictionary<string, AttributeValue>[] items)
    {
        var responses = items.Select(i => new QueryResponse { Items = [i] }).ToArray();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(responses[0], responses.Skip(1).ToArray());
    }

    private static APIGatewayProxyRequest Resolve(object body) => new()
    {
        HttpMethod = "POST",
        Path = "/admin/disputes/escrow-9/resolve",
        Body = JsonSerializer.Serialize(body),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
        }
    };

    [Fact]
    public async Task AValidSplit_IsRecordedWithTheVerdict_WhileTheCropLegIsHeld()
    {
        EscrowReads(LedgerEscrow());

        var response = await _function.FunctionHandler(Resolve(new
        {
            resolution = "partial_refund", action = "split", farmerAmount = 9000.00m, truckerAmount = 1500.00m, buyerRefund = 6000.00m
        }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"verdictAmounts\"").And.Contain("pays the split");
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u => u.ExpressionAttributeValues[":resolution"].S == "partial_refund"
                                           && u.ExpressionAttributeNames["#verdictAmounts"] == "verdictAmounts"
                                           && u.ExpressionAttributeValues[":verdictAmounts"].M["farmerAmount"].N == "9000.00"
                                           && u.ExpressionAttributeValues[":verdictAmounts"].M["truckerAmount"].N == "1500.00"
                                           && u.ExpressionAttributeValues[":verdictAmounts"].M["buyerRefund"].N == "6000.00"
                                           && u.ConditionExpression.EndsWith(" AND " + EscrowRepository.LedgerCropHeldCondition)
                                           && u.ExpressionAttributeValues[":ledgerHeld"].S == "held"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASplitOnA1xEscrow_IsRefused_Admin014()
    {
        EscrowReads(LedgerEscrow(ledger: false));

        var response = await _function.FunctionHandler(Resolve(new
        {
            resolution = "partial_refund", action = "split", farmerAmount = 9000m, truckerAmount = 1500m, buyerRefund = 6000m
        }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014").And.Contain("ledger");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, 1500.0, 6000.0, "farmerAmount")]
    [InlineData(9000.0, 1500.0, 6750.0, "buyerRefund")]
    [InlineData(15001.0, 0.0, 2249.0, "farmerAmount")]
    [InlineData(9000.0, 1600.0, 5900.0, "truckerAmount")]
    [InlineData(9000.005, 1500.0, 5999.995, "farmerAmount")]
    public async Task ASplitThatBreaksARule_IsRefused_Admin021_NamingTheField(
        double? farmer, double trucker, double buyer, string field)
    {
        EscrowReads(LedgerEscrow());

        var response = await _function.FunctionHandler(Resolve(new
        {
            resolution = "partial_refund", action = "split",
            farmerAmount = (decimal?)farmer, truckerAmount = (decimal)trucker, buyerRefund = (decimal)buyer
        }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_021").And.Contain($"\"field\":\"{field}\"");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APartialRefundWithAnotherAction_IsRefused_Admin014_BeforeAnyRead()
    {
        var response = await _function.FunctionHandler(Resolve(new
        {
            resolution = "partial_refund", action = "refund", farmerAmount = 9000m, truckerAmount = 1500m, buyerRefund = 6000m
        }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014").And.Contain("split");
        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADifferentSplitAgain_IsRefused_Admin010()
    {
        var recorded = LedgerEscrow(true,
            ("disputeResolution", S("partial_refund")),
            ("verdictAmounts", new AttributeValue
            {
                M = new Dictionary<string, AttributeValue> { ["farmerAmount"] = N("9000"), ["truckerAmount"] = N("1500"), ["buyerRefund"] = N("6000") }
            }));
        recorded["status"] = S("dispute_resolved");
        EscrowReads(LedgerEscrow(), recorded);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("already resolved"));

        var response = await _function.FunctionHandler(Resolve(new
        {
            resolution = "partial_refund", action = "split", farmerAmount = 8000m, truckerAmount = 1500m, buyerRefund = 7000m
        }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_010").And.Contain("different split");
    }
}

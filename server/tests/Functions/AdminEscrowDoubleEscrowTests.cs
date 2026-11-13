using System.Text.Json;
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
/// FUP-41: the admin escrow detail says why the sweeps leave an escrow alone (<c>settlementBlockReason</c> and its
/// time) and, for an escrow that names an order but is not that order's own, the status of the order's own escrow
/// and whether this one is therefore a second payment, so the page offers the double escrow refund only where the
/// server would accept it. One extra read on the detail route; nothing is written.
/// </summary>
public class AdminEscrowDoubleEscrowTests
{
    private const string UsersTable = "users-test";
    private const string EscrowsTable = "escrows-test";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _escrows = new();

    public AdminEscrowDoubleEscrowTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var id = ci.ArgAt<QueryRequest>(0).ExpressionAttributeValues[":id"].S;
            return Task.FromResult(new QueryResponse
            {
                Items = _escrows.TryGetValue(id, out var row) ? new List<Dictionary<string, AttributeValue>> { row } : new(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });
        });
        _ddb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new BatchGetItemResponse
        {
            Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>> { { UsersTable, new() } },
            UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
        });
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private void Escrow(string id, string status, string? orderId, params (string Key, AttributeValue Value)[] extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(id),
            ["createdAt"] = S("2026-10-01T10:00:00Z"),
            ["updatedAt"] = S("2026-10-01T10:00:00Z"),
            ["expiresAt"] = S("2026-10-08T10:00:00Z"),
            ["status"] = S(status),
            ["deliveryId"] = S("del-41"),
            ["buyerId"] = S("buyer-41"),
            ["farmerId"] = S("farmer-41"),
            ["amount"] = N("1500"),
            ["farmerPayout"] = N("1462.5"),
            ["commission"] = N("37.5"),
            ["commissionRate"] = N("0.025"),
            ["currency"] = S("ETB"),
            ["version"] = N("2")
        };
        if (orderId != null)
        {
            item["orderId"] = S(orderId);
        }

        foreach (var (key, value) in extra)
        {
            item[key] = value;
        }

        _escrows[id] = item;
    }

    private async Task<JsonElement> DetailAsync(string escrowId)
    {
        var response = await new AdminFunction(_ddb, UsersTable, "orders-test", EscrowsTable, "deliveries-test", "escrow-audit-test")
            .FunctionHandler(new APIGatewayProxyRequest
            {
                HttpMethod = "GET",
                Path = $"/admin/escrows/{escrowId}",
                RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
                }
            }, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("escrow");
    }

    private IEnumerable<string> QueriedIds => _ddb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<QueryRequest>()
        .Select(q => q.ExpressionAttributeValues[":id"].S);

    [Theory]
    [InlineData("escrowed")]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("held")]
    public async Task ASecondEscrowOfAnOrderWhoseEscrowHoldsOrPaidTheMoney_IsMarkedDouble(string ownStatus)
    {
        Escrow("escrow-del-41", "escrowed", "ord-41",
            ("settlementBlockReason", S("release:trade:double_escrow")),
            ("settlementBlockReasonAt", S("2026-10-09T10:00:00Z")));
        Escrow("escrow-order-ord-41", ownStatus, "ord-41", ("source", S("order")));

        var escrow = await DetailAsync("escrow-del-41");

        escrow.GetProperty("doubleEscrow").GetBoolean().Should().BeTrue();
        escrow.GetProperty("orderEscrowStatus").GetString().Should().Be(ownStatus);
        escrow.GetProperty("settlementBlockReason").GetString().Should().Be("release:trade:double_escrow");
        escrow.GetProperty("settlementBlockReasonAt").GetString().Should().Be("2026-10-09T10:00:00Z");
        _ddb.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Should().OnlyContain(n => n == "QueryAsync" || n == "BatchGetItemAsync");
    }

    [Theory]
    [InlineData("initiated")]
    [InlineData("refunded")]
    [InlineData("cancelled")]
    public async Task AnOrderEscrowThatDoesNotHoldTheMoney_IsNotDouble(string ownStatus)
    {
        Escrow("escrow-del-41", "escrowed", "ord-41");
        Escrow("escrow-order-ord-41", ownStatus, "ord-41", ("source", S("order")));

        var escrow = await DetailAsync("escrow-del-41");

        escrow.GetProperty("doubleEscrow").GetBoolean().Should().BeFalse();
        escrow.GetProperty("orderEscrowStatus").GetString().Should().Be(ownStatus);
    }

    [Fact]
    public async Task NoOrderEscrow_IsNotDouble()
    {
        Escrow("escrow-del-41", "escrowed", "ord-41");

        var escrow = await DetailAsync("escrow-del-41");

        escrow.GetProperty("doubleEscrow").GetBoolean().Should().BeFalse();
        escrow.TryGetProperty("orderEscrowStatus", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheOrdersOwnEscrow_AndAnEscrowWithoutAnOrder_ReadNothingMore()
    {
        Escrow("escrow-order-ord-41", "escrowed", "ord-41",
            ("source", S("order")), ("settlementBlockReason", S("release:payout:phone_unverified")));
        Escrow("escrow-del-42", "escrowed", null);

        var own = await DetailAsync("escrow-order-ord-41");
        var plain = await DetailAsync("escrow-del-42");

        own.TryGetProperty("doubleEscrow", out _).Should().BeFalse();
        own.GetProperty("settlementBlockReason").GetString().Should().Be("release:payout:phone_unverified");
        plain.TryGetProperty("doubleEscrow", out _).Should().BeFalse();
        QueriedIds.Should().Equal("escrow-order-ord-41", "escrow-del-42");
    }
}

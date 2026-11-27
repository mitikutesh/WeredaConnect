using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// SIM-56 against real DynamoDB (LocalStack in CI): the buyer's requests written the way
/// <c>/sync</c> writes them (<see cref="IntentRepository.CreateAsync"/>, an update to
/// <c>fulfilled</c>) and read back with <see cref="IntentRepository.QueryByBuyerAsync"/> and
/// <c>GET /intents</c>: the key condition on the real partition key returns one buyer's rows only,
/// the stored numbers come back as numbers, and a fulfilled row is left out of the list.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BuyerIntentListDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public BuyerIntentListDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IntentRepository Intents => new(_db.Client, _db.Intents);

    private string Id(string name) => $"{name}-sim56-{_run}";

    private static Dictionary<string, object> Payload(string intentId, string buyerId, string cropType) => new()
    {
        ["intentId"] = intentId,
        ["buyerId"] = buyerId,
        ["cropType"] = cropType,
        ["quantityNeeded"] = 12.5,
        ["maxPricePerKg"] = 95,
        ["deliveryWoreda"] = "Adama",
        ["deliveryLatitude"] = 8.54,
        ["deliveryLongitude"] = 39.27,
        ["neededByDate"] = "2026-12-01T00:00:00.000Z",
        ["status"] = "active",
    };

    [LocalDynamoDbFact]
    public async Task QueryByBuyer_ReturnsOnlyThatBuyersRows_WhateverTheirStatus()
    {
        var buyer = Id("buyer");
        var other = Id("other");
        (await Intents.CreateAsync(Id("teff"), buyer, Payload(Id("teff"), buyer, "teff"))).IsSuccess.Should().BeTrue();
        (await Intents.CreateAsync(Id("maize"), buyer, Payload(Id("maize"), buyer, "maize"))).IsSuccess.Should().BeTrue();
        (await Intents.CreateAsync(Id("theirs"), other, Payload(Id("theirs"), other, "teff"))).IsSuccess.Should().BeTrue();
        (await Intents.UpdateAsync(Id("maize"), buyer, new Dictionary<string, object> { ["status"] = "fulfilled" }, 1))
            .IsSuccess.Should().BeTrue();

        var rows = await Intents.QueryByBuyerAsync(buyer);

        rows.Select(r => r["intentId"].ToString()).Should().BeEquivalentTo(new[] { Id("teff"), Id("maize") });
        rows.Should().OnlyContain(r => r["buyerId"].ToString() == buyer);
        (await Intents.QueryByBuyerAsync(Id("nobody"))).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task GetIntents_ReturnsTheCallersActiveRequests_WithTheirFields()
    {
        var buyer = Id("caller");
        var other = Id("someone");
        await Intents.CreateAsync(Id("wheat"), buyer, Payload(Id("wheat"), buyer, "wheat"));
        await Intents.CreateAsync(Id("done"), buyer, Payload(Id("done"), buyer, "barley"));
        await Intents.UpdateAsync(Id("done"), buyer, new Dictionary<string, object> { ["status"] = "fulfilled" }, 1);
        await Intents.CreateAsync(Id("not-mine"), other, Payload(Id("not-mine"), other, "wheat"));

        var function = new MatchFunction(Substitute.For<ICropRepository>(), Intents);
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Resource = "/intents",
            Path = "/intents",
            QueryStringParameters = new Dictionary<string, string> { ["buyerId"] = other },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    ["sub"] = buyer,
                    ["custom:role"] = "buyer",
                },
            },
        };

        var response = await function.FunctionHandler(request, new TestLambdaContext());

        response.StatusCode.Should().Be(200);
        var intents = JsonDocument.Parse(response.Body).RootElement.GetProperty("intents");
        intents.GetArrayLength().Should().Be(1);
        var intent = intents[0];
        intent.GetProperty("intentId").GetString().Should().Be(Id("wheat"));
        intent.GetProperty("cropType").GetString().Should().Be("wheat");
        intent.GetProperty("quantityNeeded").GetDouble().Should().Be(12.5);
        intent.GetProperty("maxPricePerKg").GetDouble().Should().Be(95);
        intent.GetProperty("deliveryWoreda").GetString().Should().Be("Adama");
        intent.GetProperty("deliveryLatitude").GetDouble().Should().Be(8.54);
        intent.GetProperty("neededByDate").GetString().Should().Be("2026-12-01T00:00:00.000Z");
        intent.GetProperty("version").GetInt32().Should().Be(1);
        intent.GetProperty("createdAt").GetString().Should().NotBeNullOrEmpty();
    }
}

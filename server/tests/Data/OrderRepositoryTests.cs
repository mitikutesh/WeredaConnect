using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// Verifies the status filter is actually applied. Regression for the web bug
/// where <see cref="OrderRepository.QueryOrdersAsync"/> dropped the status
/// parameter whenever a role id (buyerId/farmerId/truckerId) was present, so
/// every status tab returned the user's full order list.
/// </summary>
public class OrderRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly OrderRepository _repository;

    private const string TableName = "test-orders";

    public OrderRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new OrderRepository(_mockDynamoDb, TableName);

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });
    }

    [Fact]
    public async Task QueryOrdersAsync_BuyerIdWithStatus_AppliesStatusFilterOnBuyerIndex()
    {
        await _repository.QueryOrdersAsync(buyerId: "buyer-1", status: "accepted");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "BuyerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "buyer-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeNames["#status"] == "status" &&
                r.ExpressionAttributeValues[":status"].S == "accepted"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_FarmerIdWithStatus_AppliesStatusFilterOnFarmerIndex()
    {
        await _repository.QueryOrdersAsync(farmerId: "farmer-1", status: "funded");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "FarmerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "farmer-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeValues[":status"].S == "funded"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_TruckerIdWithStatus_AppliesStatusFilterOnTruckerIndex()
    {
        await _repository.QueryOrdersAsync(truckerId: "trucker-1", status: "inTransit");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "TruckerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "trucker-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeValues[":status"].S == "inTransit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_BuyerIdWithoutStatus_DoesNotApplyFilter()
    {
        await _repository.QueryOrdersAsync(buyerId: "buyer-1");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "BuyerIdCreatedAtIndex" &&
                r.FilterExpression == null &&
                !r.ExpressionAttributeValues.ContainsKey(":status")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_StatusOnly_QueriesStatusIndexWithoutFilter()
    {
        // No role id → status IS the partition key, so no FilterExpression.
        await _repository.QueryOrdersAsync(status: "delivered");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "StatusCreatedAtIndex" &&
                r.FilterExpression == null &&
                r.ExpressionAttributeValues[":val"].S == "delivered"),
            Arg.Any<CancellationToken>());
    }
}

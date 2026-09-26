using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>DynamoDB-backed <see cref="IDeviceTokenRepository"/> (PK userId, SK endpointArn).</summary>
public class DeviceTokenRepository : IDeviceTokenRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DeviceTokenRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    /// <inheritdoc />
    public async Task PutAsync(string userId, string endpointArn, string platform, string token)
    {
        var now = DateTime.UtcNow.ToString("o");
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } },
                { "endpointArn", new AttributeValue { S = endpointArn } },
                { "platform", new AttributeValue { S = platform } },
                { "token", new AttributeValue { S = token } },
                { "updatedAt", new AttributeValue { S = now } },
            },
        });
    }

    /// <inheritdoc />
    public async Task<List<string>> GetEndpointArnsByUserAsync(string userId)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "userId = :uid",
            ProjectionExpression = "endpointArn",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":uid", new AttributeValue { S = userId } },
            },
        });

        return response.Items
            .Where(i => i.TryGetValue("endpointArn", out var v) && v.S != null)
            .Select(i => i["endpointArn"].S)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> FindEndpointArnByTokenAsync(string userId, string token)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "userId = :uid",
            FilterExpression = "#token = :token",
            ProjectionExpression = "endpointArn",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#token", "token" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":uid", new AttributeValue { S = userId } },
                { ":token", new AttributeValue { S = token } },
            },
        });

        return response.Items
            .Where(i => i.TryGetValue("endpointArn", out var v) && v.S != null)
            .Select(i => i["endpointArn"].S)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string userId, string endpointArn)
    {
        await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } },
                { "endpointArn", new AttributeValue { S = endpointArn } },
            },
        });
    }
}

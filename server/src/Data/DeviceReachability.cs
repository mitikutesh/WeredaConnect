using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-28: whether a user can be reached by push (decision R12, reputation-architecture.md 7.3):
/// one keys-only Query of the device tokens table, <c>Limit 1</c>. A failed read throws, so the
/// stream parks the record instead of silently giving an expiry 0 points.
/// </summary>
public sealed class DeviceReachability
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DeviceReachability(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>True when the user has at least one registered device.</summary>
    public async Task<bool> HasDeviceAsync(string userId)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "userId = :uid",
            ProjectionExpression = "userId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":uid"] = new() { S = userId } },
            Limit = 1
        });

        return response.Items.Count > 0;
    }
}

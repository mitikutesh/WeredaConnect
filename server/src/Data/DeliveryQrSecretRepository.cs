using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB repository for per-delivery QR handoff secrets, using the low-level API
/// for AOT compatibility (no reflection, manual marshalling).
///
/// Table schema (DeliveryQrSecrets):
///   PK: deliveryId (STRING)
///   nonce (STRING), issuedAt (STRING ISO 8601),
///   consumedPickedUp / consumedDelivered (STRING operationId, optional),
///   TTL: expiresAt (NUMBER — Unix epoch seconds; cleanup only, freshness is code-enforced)
///
/// The secret is NEVER returned to any client — it lives here precisely so it cannot
/// ride along on a delivery sync-down, GSI projection, DTO, or conflict echo.
/// </summary>
public sealed class DeliveryQrSecretRepository : IDeliveryQrSecretRepository
{
    // Retain well past the code-enforced freshness window so a legitimately slow
    // (rural, offline) handoff still finds its secret; TTL is only bulk cleanup.
    private const int TtlDays = 30;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DeliveryQrSecretRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task PutNonceAsync(string deliveryId, string nonce)
    {
        var now = DateTime.UtcNow.ToString("o");
        var expiresAt = DateTimeOffset.UtcNow.AddDays(TtlDays).ToUnixTimeSeconds();

        // SET the nonce + TTL, but anchor issuedAt to the FIRST persist (if_not_exists)
        // and never touch the consumed markers — so a stable re-persist (the "regenerate"
        // button re-rendering the same token) preserves freshness and single-use state.
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } }
            },
            UpdateExpression = "SET #nonce = :nonce, #issuedAt = if_not_exists(#issuedAt, :now), #exp = :exp",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#nonce", "nonce" },
                { "#issuedAt", "issuedAt" },
                { "#exp", "expiresAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":nonce", new AttributeValue { S = nonce } },
                { ":now", new AttributeValue { S = now } },
                { ":exp", new AttributeValue { N = expiresAt.ToString() } }
            }
        };

        await _dynamoDb.UpdateItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<DeliveryQrSecret?> GetAsync(string deliveryId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } }
            }
        });

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        var item = response.Item;
        var nonce = item.TryGetValue("nonce", out var n) ? n.S : null;
        if (string.IsNullOrEmpty(nonce))
        {
            return null;
        }

        return new DeliveryQrSecret
        {
            DeliveryId = deliveryId,
            Nonce = nonce,
            IssuedAt = item.TryGetValue("issuedAt", out var ia) ? ia.S : null,
            ConsumedPickedUpOp = item.TryGetValue("consumedPickedUp", out var cp) ? cp.S : null,
            ConsumedDeliveredOp = item.TryGetValue("consumedDelivered", out var cd) ? cd.S : null
        };
    }

    /// <inheritdoc />
    public async Task MarkConsumedAsync(string deliveryId, string targetStatus, string operationId)
    {
        var attr = targetStatus switch
        {
            "pickedUp" => "consumedPickedUp",
            "delivered" => "consumedDelivered",
            _ => null
        };

        // Only pickup/delivery consume the nonce; ignore any other status defensively.
        if (attr == null)
        {
            return;
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } }
            },
            UpdateExpression = "SET #c = :op",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#c", attr } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":op", new AttributeValue { S = operationId } }
            }
        });
    }
}

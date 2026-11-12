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
///   codeMissesPickedUp / codeMissesDelivered (STRING SET of attempt ids, "{operationId}:{first
///   16 hex of SHA-256 of the code}", taken before the comparison and removed again for a right
///   code, so it holds the wrong codes; optional, cleared when the nonce changes),
///   dropoffNonce (STRING, ESC-35: the trucker's drop-off nonce, optional), dropoffIssuedAt
///   (STRING ISO 8601, when the current drop-off nonce was stored),
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
        UpdateItemRequest Request(bool newNonce)
        {
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
            if (newNonce)
            {
                // T-1: a new nonce has a new typed code, so the misses of the old one go
                // (the farmer's Regenerate is how a locked code is unlocked).
                request.UpdateExpression += " REMOVE #missP, #missD";
                request.ConditionExpression = "attribute_not_exists(#nonce) OR #nonce <> :nonce";
                request.ExpressionAttributeNames["#missP"] = CodeMissesAttribute("pickedUp")!;
                request.ExpressionAttributeNames["#missD"] = CodeMissesAttribute("delivered")!;
            }

            return request;
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(Request(newNonce: true));
        }
        catch (ConditionalCheckFailedException)
        {
            // The same nonce again: refresh the TTL, keep the misses.
            await _dynamoDb.UpdateItemAsync(Request(newNonce: false));
        }
    }

    /// <inheritdoc />
    public async Task<bool> PutDropoffNonceAsync(string deliveryId, string nonce)
    {
        var key = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = deliveryId } }
        };
        var expiresAt = new AttributeValue
        {
            N = DateTimeOffset.UtcNow.AddDays(TtlDays).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        try
        {
            // A new drop-off nonce: its freshness starts now, and the drop-off misses of the old
            // one go (the trucker's new code is how a locked or expired drop-off code is renewed).
            // Only next to the farmer's nonce: a row without one is no pickup this store proved.
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = key,
                UpdateExpression = "SET #dn = :dn, #dIssuedAt = :now, #exp = :exp REMOVE #missD",
                ConditionExpression = "attribute_exists(#nonce) AND (attribute_not_exists(#dn) OR #dn <> :dn)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#nonce", "nonce" },
                    { "#dn", "dropoffNonce" },
                    { "#dIssuedAt", "dropoffIssuedAt" },
                    { "#exp", "expiresAt" },
                    { "#missD", CodeMissesAttribute("delivered")! }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":dn", new AttributeValue { S = nonce } },
                    { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
                    { ":exp", expiresAt }
                },
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD
            });
            return true;
        }
        catch (ConditionalCheckFailedException ex)
        {
            if (ex.Item != null
                && (!ex.Item.TryGetValue("nonce", out var farmerNonce) || string.IsNullOrEmpty(farmerNonce.S)))
            {
                return false;
            }
        }

        try
        {
            // The same drop-off nonce again (the trucker opened the code again): refresh the TTL,
            // keep its freshness and its misses.
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = key,
                UpdateExpression = "SET #exp = :exp",
                ConditionExpression = "#dn = :dn",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#dn", "dropoffNonce" },
                    { "#exp", "expiresAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":dn", new AttributeValue { S = nonce } },
                    { ":exp", expiresAt }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // No row with the farmer's nonce (the first write could not say why), or another new
            // code replaced this one in between: that one stands.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<CodeAttempt> TryTakeCodeAttemptAsync(
        string deliveryId, string targetStatus, string attemptId, string nonce, int maxAttempts,
        bool dropoffNonce = false)
    {
        var attr = CodeMissesAttribute(targetStatus);
        if (attr == null)
        {
            return new CodeAttempt(CodeAttemptOutcome.Locked, 0);
        }

        // ESC-35: a drop-off code typed for the trucker's nonce counts only while that nonce is stored.
        var nonceAttr = dropoffNonce ? "dropoffNonce" : "nonce";

        try
        {
            // ADD to a string set, conditional on the set's size: atomic, so parallel guesses
            // cannot pass the cap. The element names the operation and the code, so a retry of
            // one refused operation with the same code is admitted again and never locks the
            // code by itself, while a new code under a reused operation id always counts.
            var response = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = deliveryId } }
                },
                UpdateExpression = "ADD #m :ops",
                ConditionExpression =
                    "#nonce = :nonce AND (attribute_not_exists(#m) OR size(#m) < :max OR contains(#m, :op))",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#m", attr },
                    { "#nonce", nonceAttr }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":ops", new AttributeValue { SS = new List<string> { attemptId } } },
                    { ":op", new AttributeValue { S = attemptId } },
                    { ":nonce", new AttributeValue { S = nonce } },
                    { ":max", new AttributeValue { N = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
                },
                ReturnValues = ReturnValue.UPDATED_NEW,
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD
            });
            var count = response.Attributes != null
                && response.Attributes.TryGetValue(attr, out var taken)
                && taken.SS != null
                ? taken.SS.Count
                : 1;
            return new CodeAttempt(CodeAttemptOutcome.Taken, count);
        }
        catch (ConditionalCheckFailedException ex)
        {
            // Regenerated meanwhile (or the row is gone): the code was typed for a nonce that
            // is no longer stored. Otherwise the leg's attempts are used up.
            var stored = ex.Item != null && ex.Item.TryGetValue(nonceAttr, out var n) ? n.S : null;
            return stored == nonce
                ? new CodeAttempt(CodeAttemptOutcome.Locked, maxAttempts)
                : new CodeAttempt(CodeAttemptOutcome.NonceChanged, 0);
        }
    }

    /// <inheritdoc />
    public async Task ReturnCodeAttemptAsync(string deliveryId, string targetStatus, string attemptId)
    {
        var attr = CodeMissesAttribute(targetStatus);
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
            UpdateExpression = "DELETE #m :ops",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#m", attr } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":ops", new AttributeValue { SS = new List<string> { attemptId } } }
            }
        });
    }

    /// <summary>The string set of operation ids that typed a wrong code for a leg.</summary>
    private static string? CodeMissesAttribute(string targetStatus) => targetStatus switch
    {
        "pickedUp" => "codeMissesPickedUp",
        "delivered" => "codeMissesDelivered",
        _ => null
    };

    /// <inheritdoc />
    public async Task<DeliveryQrSecret?> GetAsync(string deliveryId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } }
            },
            // The typed-code lock and the single-use markers must be current: a lagging
            // read could take one more guess after the lock.
            ConsistentRead = true
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
            DropoffNonce = item.TryGetValue("dropoffNonce", out var dn) && !string.IsNullOrEmpty(dn.S) ? dn.S : null,
            DropoffIssuedAt = item.TryGetValue("dropoffIssuedAt", out var dia) ? dia.S : null,
            ConsumedPickedUpOp = item.TryGetValue("consumedPickedUp", out var cp) ? cp.S : null,
            ConsumedDeliveredOp = item.TryGetValue("consumedDelivered", out var cd) ? cd.S : null,
            CodeMissesPickedUp = item.TryGetValue("codeMissesPickedUp", out var mp) ? mp.SS?.Count ?? 0 : 0,
            CodeMissesDelivered = item.TryGetValue("codeMissesDelivered", out var md) ? md.SS?.Count ?? 0 : 0
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

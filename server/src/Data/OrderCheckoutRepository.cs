using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB implementation of <see cref="IOrderCheckoutRepository"/> over the escrow
/// idempotency table. Low-level API with manual marshalling (AOT-safe).
///
/// Rows:
///   Slot:    idempotencyKey = "order-checkout:{orderId}", kind = "order_checkout_slot"
///   Mapping: idempotencyKey = merch_order_id, kind = "order_checkout",
///            escrowId = "escrow-order-{orderId}" (the escrow the payment will fund)
///   TTL: expiresAt (NUMBER, Unix epoch seconds), 400 days: far past any checkout window,
///        so a late notification still finds its order and a merch_order_id is never reused
/// </summary>
public sealed class OrderCheckoutRepository : IOrderCheckoutRepository
{
    private const int TtlDays = 400;
    private const string SlotKind = "order_checkout_slot";
    private const string MappingKind = "order_checkout";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public OrderCheckoutRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>The slot row key for an order.</summary>
    public static string SlotKey(string orderId) => $"order-checkout:{orderId}";

    /// <inheritdoc />
    public async Task<OrderCheckoutSlot?> GetSlotAsync(string orderId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(SlotKey(orderId)),
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0
            || GetS(response.Item, "kind") != SlotKind)
        {
            return null;
        }

        var item = response.Item;
        return new OrderCheckoutSlot
        {
            OrderId = GetS(item, "orderId") ?? orderId,
            BuyerId = GetS(item, "buyerId") ?? "",
            Attempt = int.TryParse(GetN(item, "attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0,
            MerchOrderId = GetS(item, "merchOrderId") ?? "",
            Amount = decimal.TryParse(GetN(item, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : 0m,
            Outcome = GetS(item, "outcome") ?? OrderCheckoutOutcome.Unknown,
            OpenedAtUtc = ParseUtc(GetS(item, "openedAt")),
            OpenUntilUtc = ParseUtc(GetS(item, "openUntil")),
            PrepayId = GetS(item, "prepayId")
        };
    }

    /// <inheritdoc />
    public async Task<bool> TryOpenSlotAsync(OrderCheckoutSlot slot, int? previousAttempt)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "idempotencyKey", new AttributeValue { S = SlotKey(slot.OrderId) } },
            { "kind", new AttributeValue { S = SlotKind } },
            { "orderId", new AttributeValue { S = slot.OrderId } },
            { "buyerId", new AttributeValue { S = slot.BuyerId } },
            { "attempt", new AttributeValue { N = slot.Attempt.ToString(CultureInfo.InvariantCulture) } },
            { "merchOrderId", new AttributeValue { S = slot.MerchOrderId } },
            { "amount", new AttributeValue { N = slot.Amount.ToString(CultureInfo.InvariantCulture) } },
            { "outcome", new AttributeValue { S = slot.Outcome } },
            { "openedAt", new AttributeValue { S = slot.OpenedAtUtc.ToString("o", CultureInfo.InvariantCulture) } },
            { "openUntil", new AttributeValue { S = slot.OpenUntilUtc.ToString("o", CultureInfo.InvariantCulture) } },
            { "expiresAt", new AttributeValue { N = TtlEpoch() } }
        };
        if (!string.IsNullOrEmpty(slot.PrepayId))
        {
            item["prepayId"] = new AttributeValue { S = slot.PrepayId };
        }

        var request = new PutItemRequest { TableName = _tableName, Item = item };
        if (previousAttempt == null)
        {
            request.ConditionExpression = "attribute_not_exists(idempotencyKey)";
        }
        else
        {
            request.ConditionExpression = "#attempt = :previous";
            request.ExpressionAttributeNames = new Dictionary<string, string> { { "#attempt", "attempt" } };
            request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":previous", new AttributeValue { N = previousAttempt.Value.ToString(CultureInfo.InvariantCulture) } }
            };
        }

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task UpdateSlotOutcomeAsync(
        string orderId, int attempt, string outcome, string? prepayId = null, DateTime? openUntilUtc = null)
    {
        var names = new Dictionary<string, string> { { "#outcome", "outcome" }, { "#attempt", "attempt" } };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":outcome", new AttributeValue { S = outcome } },
            { ":attempt", new AttributeValue { N = attempt.ToString(CultureInfo.InvariantCulture) } }
        };
        var set = "SET #outcome = :outcome";
        if (!string.IsNullOrEmpty(prepayId))
        {
            names["#prepay"] = "prepayId";
            values[":prepay"] = new AttributeValue { S = prepayId };
            set += ", #prepay = :prepay";
        }

        if (openUntilUtc != null)
        {
            names["#openUntil"] = "openUntil";
            values[":openUntil"] = new AttributeValue
            {
                S = openUntilUtc.Value.ToString("o", CultureInfo.InvariantCulture)
            };
            set += ", #openUntil = :openUntil";
        }

        await TryUpdateSlotAsync(orderId, set, "#attempt = :attempt", names, values);
    }

    /// <inheritdoc />
    public Task CloseSlotForMerchOrderAsync(string orderId, string merchOrderId, string outcome)
        => TryUpdateSlotAsync(
            orderId,
            "SET #outcome = :outcome",
            "#merch = :merch",
            new Dictionary<string, string> { { "#outcome", "outcome" }, { "#merch", "merchOrderId" } },
            new Dictionary<string, AttributeValue>
            {
                { ":outcome", new AttributeValue { S = outcome } },
                { ":merch", new AttributeValue { S = merchOrderId } }
            });

    /// <inheritdoc />
    public async Task<bool> StoreMappingAsync(
        string merchOrderId, string orderId, string buyerId, decimal amount, string? paymentSubmittedAt)
    {
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                { "idempotencyKey", new AttributeValue { S = merchOrderId } },
                { "kind", new AttributeValue { S = MappingKind } },
                { "escrowId", new AttributeValue { S = $"escrow-order-{orderId}" } },
                { "status", new AttributeValue { S = "processing" } },
                { "orderId", new AttributeValue { S = orderId } },
                { "buyerId", new AttributeValue { S = buyerId } },
                { "amount", new AttributeValue { N = amount.ToString(CultureInfo.InvariantCulture) } },
                { "createdAt", new AttributeValue { S = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } },
                { "expiresAt", new AttributeValue { N = TtlEpoch() } }
            },
            ConditionExpression = "attribute_not_exists(idempotencyKey)"
        };
        if (!string.IsNullOrEmpty(paymentSubmittedAt))
        {
            request.Item["paymentSubmittedAt"] = new AttributeValue { S = paymentSubmittedAt };
        }

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<OrderCheckoutMapping?> GetMappingAsync(string merchOrderId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(merchOrderId),
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0
            || GetS(response.Item, "kind") != MappingKind)
        {
            return null;
        }

        var orderId = GetS(response.Item, "orderId");
        var buyerId = GetS(response.Item, "buyerId");
        if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(buyerId)
            || !decimal.TryParse(GetN(response.Item, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            // An order checkout that cannot be read must not pass for another kind of payment.
            throw new InvalidDataException("Order checkout mapping is missing its order, buyer or amount.");
        }

        return new OrderCheckoutMapping(merchOrderId, orderId, buyerId, amount, GetS(response.Item, "paymentSubmittedAt"));
    }

    private async Task TryUpdateSlotAsync(
        string orderId, string setExpression, string condition,
        Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = KeyOf(SlotKey(orderId)),
                UpdateExpression = setExpression,
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // The slot moved on to another attempt (or never existed): nothing to record.
        }
    }

    private static Dictionary<string, AttributeValue> KeyOf(string key)
        => new() { { "idempotencyKey", new AttributeValue { S = key } } };

    private static string TtlEpoch()
        => DateTimeOffset.UtcNow.AddDays(TtlDays).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string? GetS(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var value) ? value.S : null;

    private static string? GetN(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var value) ? value.N ?? value.S : null;

    private static DateTime ParseUtc(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;
}

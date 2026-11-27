using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-19 (delivery-verification-api.md 2.9.0): the one transaction of an admin's delivery cancel after pickup. It
/// writes the delivery (<c>cancelled</c>, <c>cancelReason</c>, <c>cancelledAt</c>, the next version) and its
/// transaction-audit row, and checks that the order and every escrow are still as they were read. All of it lands or
/// none of it, so the audit row (with the admin and the reason) never goes missing and a write that lands in between
/// refuses the cancel.
/// </summary>
internal sealed class AdminDeliveryCancelRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _deliveriesTable;
    private readonly string _transactionAuditTable;

    public AdminDeliveryCancelRepository(IAmazonDynamoDB dynamoDb, string deliveriesTable, string transactionAuditTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _deliveriesTable = deliveriesTable;
        _transactionAuditTable = transactionAuditTable;
    }

    /// <summary>
    /// Writes the plan in one transaction: the delivery first, then its audit row, then the order and escrow checks.
    /// A failed condition on the delivery is <see cref="AdminDeliveryCancelWrite.DeliveryChanged"/>; anything else
    /// that cancels the transaction is <see cref="AdminDeliveryCancelWrite.LinkedChanged"/>. Nothing is written then.
    /// </summary>
    public async Task<AdminDeliveryCancelWrite> CancelAsync(AdminDeliveryCancelPlan plan)
    {
        var items = new List<TransactWriteItem>
        {
            new() { Update = DeliveryUpdate(plan) },
            new()
            {
                Put = new Put
                {
                    TableName = _transactionAuditTable,
                    Item = plan.AuditEntry
                        .Where(kv => kv.Value != null)
                        .ToDictionary(kv => kv.Key, kv => DynamoDbHelper.ToAttributeValue(kv.Value)),
                    // Append-only: a row at this key is never overwritten.
                    ConditionExpression = "attribute_not_exists(transactionId)"
                }
            }
        };
        items.AddRange(plan.Checks.Select(check => new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = check.TableName,
                Key = check.Key,
                ConditionExpression = "#status = :status",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":status"] = new() { S = check.Status } }
            }
        }));

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
            return AdminDeliveryCancelWrite.Written;
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons;
            return reasons is { Count: > 0 } && reasons[0]?.Code == "ConditionalCheckFailed"
                ? AdminDeliveryCancelWrite.DeliveryChanged
                : AdminDeliveryCancelWrite.LinkedChanged;
        }
    }

    /// <summary>
    /// The delivery: still in the status and at the version read, never soft-deleted. No <c>deletedAt</c> is written
    /// (the cancelled row stays in every party's list, as for every cancel since 1.9.0), and the version goes up by one.
    /// </summary>
    private Update DeliveryUpdate(AdminDeliveryCancelPlan plan) => new()
    {
        TableName = _deliveriesTable,
        Key = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = new() { S = plan.DeliveryId },
            ["tripId"] = new() { S = plan.TripId }
        },
        UpdateExpression = "SET #status = :cancelled, #cancelReason = :reason, #cancelledAt = :now, #updated = :now ADD #v :one",
        ConditionExpression = "#status = :from AND #v = :version AND attribute_not_exists(#deletedAt)",
        ExpressionAttributeNames = new Dictionary<string, string>
        {
            ["#status"] = "status",
            ["#cancelReason"] = "cancelReason",
            ["#cancelledAt"] = "cancelledAt",
            ["#updated"] = "updatedAt",
            ["#v"] = "version",
            ["#deletedAt"] = "deletedAt"
        },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":cancelled"] = new() { S = "cancelled" },
            [":reason"] = new() { S = plan.CancelReason },
            [":now"] = new() { S = plan.Now },
            [":from"] = new() { S = plan.FromStatus },
            [":version"] = new() { N = plan.ExpectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            [":one"] = new() { N = "1" }
        }
    };
}

/// <summary>The outcome of the cancel's transaction. Only <see cref="Written"/> wrote anything.</summary>
internal enum AdminDeliveryCancelWrite
{
    /// <summary>The delivery changed since it was read (status, version, or soft-deleted): nothing was written.</summary>
    DeliveryChanged = 0,

    Written,

    /// <summary>The order's status, an escrow's status or the audit key changed, or the items were busy: nothing was written.</summary>
    LinkedChanged
}

/// <param name="TableName">The table of the row.</param>
/// <param name="Key">The row's key.</param>
/// <param name="Status">The status read; the transaction requires it unchanged.</param>
internal sealed record AdminDeliveryCancelCheck(string TableName, Dictionary<string, AttributeValue> Key, string Status);

/// <summary>A planned cancel: what the caller read and what the transaction writes.</summary>
internal sealed record AdminDeliveryCancelPlan
{
    public required string DeliveryId { get; init; }

    /// <summary>The delivery's sort key.</summary>
    public required string TripId { get; init; }

    /// <summary><c>pickedUp</c> or <c>inTransit</c>, as read.</summary>
    public required string FromStatus { get; init; }

    /// <summary>The delivery version read (the page's <c>expectedVersion</c>).</summary>
    public required int ExpectedVersion { get; init; }

    public required string CancelReason { get; init; }

    /// <summary>ISO time of the cancel: <c>cancelledAt</c>, <c>updatedAt</c> and the audit row's key.</summary>
    public required string Now { get; init; }

    /// <summary>The transaction-audit row (<see cref="Helpers.TransactionAuditWriter.BuildOnceEntry"/>).</summary>
    public required Dictionary<string, object> AuditEntry { get; init; }

    /// <summary>The order and every escrow read, each required unchanged.</summary>
    public required IReadOnlyList<AdminDeliveryCancelCheck> Checks { get; init; }
}

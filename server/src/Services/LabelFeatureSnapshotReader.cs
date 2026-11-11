namespace WeredaConnect.Server.Services;

using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.RateLimiting;

/// <summary>
/// FRD-20 P1 (fraud-detection-architecture.md section 20.1, <c>specs/decisions/frd-20-ml-risk-scoring.md</c>):
/// reads the feature snapshot of a Fraud Review decision's subject user. Three bounded reads, no Scan, no
/// write: the user's signal partition over the whole score window (at most the score's page cap), the users
/// row (three attributes) and one Query of the role's escrow index for its completed escrows (the read
/// FRD-13 makes). The caller runs it before the decision's side effects and its review stamp, so the decided
/// signal still counts and a whitelist being granted silences nothing yet. Never throws: a failed read logs
/// <see cref="FailedToken"/> and returns null, and the label is written without a snapshot.
/// </summary>
internal sealed class LabelFeatureSnapshotReader
{
    /// <summary>Warning line of a snapshot that could not be read: <c>LABEL_SNAPSHOT_FAILED userId=&lt;id&gt; error=&lt;type&gt;</c>.</summary>
    public const string FailedToken = "LABEL_SNAPSHOT_FAILED";

    /// <summary>The escrows the completed count looks at: the newest of the user in its role (as FRD-13).</summary>
    internal const int CompletedReadLimit = PayoutDelaySettings.CompletedReadLimit;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _fraudSignalsTable;
    private readonly string _usersTable;
    private readonly string _escrowsTable;

    public LabelFeatureSnapshotReader(
        IAmazonDynamoDB dynamoDb, string fraudSignalsTable, string usersTable, string escrowsTable)
    {
        _dynamoDb = dynamoDb;
        _fraudSignalsTable = fraudSignalsTable;
        _usersTable = usersTable;
        _escrowsTable = escrowsTable;
    }

    public async Task<LabelFeatureSnapshot?> ReadAsync(string userId, DateTime nowUtc, Action<string> logWarning)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(_fraudSignalsTable))
        {
            return null;
        }

        try
        {
            // The score's construction: this read never touches the crops or rate-limit tables.
            var units = await new FraudDetectionService(_dynamoDb, "", _fraudSignalsTable, new RateLimitRepository(_dynamoDb, ""))
                .ReadFeatureUnitsAsync(userId, nowUtc, RiskScoreSettings.FromEnvironment());
            var user = await ReadUserAsync(userId);
            var role = user == null ? null : RoleCode(Str(user, "role"));
            var completed = role == null ? null : await CountCompletedAsync(userId, role);
            return new LabelFeatureSnapshot
            {
                SignalUnits = units.UnitsByType,
                CreditUnits = units.CreditUnits,
                CreditPoints = units.CreditPoints,
                Truncated = units.Truncated,
                WindowDays = units.WindowDays,
                AccountAgeDays = user == null ? null : AgeDays(Str(user, "createdAt"), nowUtc),
                FaydaVerified = user == null ? null : user.TryGetValue("faydaVerified", out var fayda) && fayda.BOOL,
                Role = role,
                CompletedEscrows = completed?.Count,
                CompletedEscrowsPartial = completed?.Partial
            };
        }
        catch (Exception ex)
        {
            logWarning($"{FailedToken} userId={MoneyAlarmLog.LogId(userId)} error={MoneyAlarmLog.LogId(ex.GetType().Name)}");
            return null;
        }
    }

    private async Task<Dictionary<string, AttributeValue>?> ReadUserAsync(string userId)
    {
        if (string.IsNullOrEmpty(_usersTable))
        {
            return null;
        }

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { { "userId", new AttributeValue { S = userId } } },
            ProjectionExpression = "#c, #f, #r",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#c", "createdAt" },
                { "#f", "faydaVerified" },
                { "#r", "role" }
            }
        });
        return response?.Item is { Count: > 0 } item ? item : null;
    }

    /// <summary>
    /// One Query of the role's party index, newest first, at most <see cref="CompletedReadLimit"/> rows read,
    /// <c>completed</c> as the filter, counted by DynamoDB (<c>Select COUNT</c>, no row returned). Null for a
    /// role without a party index, or without an escrows table.
    /// </summary>
    private async Task<(int Count, bool Partial)?> CountCompletedAsync(string userId, string role)
    {
        var (index, field) = role switch
        {
            "farmer" => ("FarmerStatusIndex", "farmerId"),
            "trucker" => ("TruckerStatusIndex", "truckerId"),
            "buyer" => ("BuyerStatusIndex", "buyerId"),
            _ => (null, null)
        };
        if (index == null || string.IsNullOrEmpty(_escrowsTable))
        {
            return null;
        }

        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _escrowsTable,
            IndexName = index,
            KeyConditionExpression = "#party = :userId",
            FilterExpression = "#status = :completed",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#party", field! },
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":userId", new AttributeValue { S = userId } },
                { ":completed", new AttributeValue { S = "completed" } }
            },
            Select = Select.COUNT,
            Limit = CompletedReadLimit,
            ScanIndexForward = false
        });
        return (response.Count, response.LastEvaluatedKey is { Count: > 0 });
    }

    private static string RoleCode(string? role) => role is "farmer" or "trucker" or "buyer" ? role : "other";

    private static int? AgeDays(string? createdAt, DateTime nowUtc) =>
        DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)
            ? Math.Max(0, (int)(nowUtc.ToUniversalTime() - created).TotalDays)
            : null;

    private static string? Str(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value) ? value.S : null;
}

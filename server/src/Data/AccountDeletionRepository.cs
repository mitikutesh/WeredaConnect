using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>The tables an account deletion reads and writes.</summary>
public sealed record AccountDeletionTables
{
    public required string Users { get; init; }
    public required string Crops { get; init; }
    public required string Trips { get; init; }
    public required string Orders { get; init; }
    public required string Deliveries { get; init; }
    public required string Escrows { get; init; }
    public required string EscrowAudit { get; init; }
    public required string EscrowIdempotency { get; init; }
    public required string Ratings { get; init; }
    public required string Messages { get; init; }
    public required string Intents { get; init; }
    public required string MatchEvents { get; init; }
    public required string DeviceTokens { get; init; }
}

/// <summary>
/// ACC-02: DynamoDB implementation of <see cref="IAccountDeletionRepository"/> with the low-level
/// API (AOT-safe, manual marshalling). The checkout slot, payment trail and device tokens go
/// through their existing repositories.
/// </summary>
public sealed class AccountDeletionRepository : IAccountDeletionRepository
{
    /// <summary>Attempts of a conditional write that re-reads its row after a lost condition.</summary>
    private const int MaxConditionAttempts = 3;

    // photoIds (crop-listing-api 1.6.0): without them no photo URL is ever signed for the listing again.
    private static readonly string[] CropScrubFields =
        { "farmerPhone", "latitude", "longitude", "locationName", "description", "audioNoteRef", "photoIds" };

    private static readonly string[] TripScrubFields = { "truckerPhone", "vehiclePlate", "vehicleDescription" };

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly AccountDeletionTables _tables;
    private readonly IOrderCheckoutRepository _checkouts;
    private readonly IEscrowAuditRepository _escrowAudit;
    private readonly IDeviceTokenRepository _deviceTokens;

    public AccountDeletionRepository(IAmazonDynamoDB dynamoDb, AccountDeletionTables tables)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _checkouts = new OrderCheckoutRepository(dynamoDb, tables.EscrowIdempotency);
        _escrowAudit = new EscrowAuditRepository(dynamoDb, tables.EscrowAudit);
        _deviceTokens = new DeviceTokenRepository(dynamoDb, tables.DeviceTokens);
    }

    // ── users row ──

    public async Task<UserDto?> GetAccountAsync(string userId)
    {
        var item = await GetUserItemAsync(userId);
        return item == null ? null : UserRepository.FromItem(item);
    }

    public async Task<string?> TryMarkInProgressAsync(string userId, string requestedAt)
    {
        try
        {
            var response = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tables.Users,
                Key = UserKey(userId),
                UpdateExpression = "SET #ds = :inProgress, #dra = if_not_exists(#dra, :now)",
                ConditionExpression = "attribute_exists(userId) AND (attribute_not_exists(#ds) OR #ds = :inProgress) "
                                      + "AND (attribute_not_exists(#susp) OR #susp = :false)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#ds", "deletionState" },
                    { "#dra", "deletionRequestedAt" },
                    { "#susp", "suspended" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":inProgress", S(AccountDeletionPolicy.InProgress) },
                    { ":now", S(requestedAt) },
                    { ":false", new AttributeValue { BOOL = false } }
                },
                ReturnValues = ReturnValue.ALL_NEW
            });
            return response.Attributes != null && response.Attributes.TryGetValue("deletionRequestedAt", out var at)
                ? at.S
                : requestedAt;
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }
    }

    public async Task<bool> TryClearInProgressAsync(string userId)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tables.Users,
                Key = UserKey(userId),
                UpdateExpression = "REMOVE #ds, #dra",
                ConditionExpression = "#ds = :inProgress",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#ds", "deletionState" },
                    { "#dra", "deletionRequestedAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":inProgress", S(AccountDeletionPolicy.InProgress) }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<string?> TombstoneAsync(string userId, string deletedAt, string nextState, bool requireInProgress)
    {
        for (var attempt = 0; attempt < MaxConditionAttempts; attempt++)
        {
            var item = await GetUserItemAsync(userId);
            if (item == null)
            {
                return null;
            }

            var state = item.TryGetValue("deletionState", out var ds) ? ds.S : null;
            if (requireInProgress && state != AccountDeletionPolicy.InProgress)
            {
                // Another run of the same deletion got here first.
                return state is AccountDeletionPolicy.CognitoPending or AccountDeletionPolicy.Completed
                    ? item.TryGetValue("deletedAt", out var stored) ? stored.S : null
                    : null;
            }

            var names = new Dictionary<string, string>
            {
                { "#ds", "deletionState" },
                { "#da", "deletedAt" },
                { "#ua", "updatedAt" },
                { "#v", "version" }
            };
            var values = new Dictionary<string, AttributeValue>
            {
                { ":next", S(nextState) },
                { ":deletedAt", S(deletedAt) },
                { ":now", S(Now()) }
            };

            // Everything off the allowlist goes, including fields added after this was written.
            var removes = new List<string>();
            foreach (var attribute in item.Keys.Where(k => !AccountDeletionPolicy.TombstoneAllowlist.Contains(k)))
            {
                var placeholder = $"#r{removes.Count}";
                names[placeholder] = attribute;
                removes.Add(placeholder);
            }

            var readVersion = item.TryGetValue("version", out var v) && v.N != null
                ? int.Parse(v.N, CultureInfo.InvariantCulture)
                : (int?)null;
            values[":newVersion"] = N((readVersion ?? 0) + 1);
            var condition = readVersion == null ? "attribute_not_exists(#v)" : "#v = :readVersion";
            if (readVersion != null)
            {
                values[":readVersion"] = N(readVersion.Value);
            }

            if (requireInProgress)
            {
                condition += " AND #ds = :inProgress";
                values[":inProgress"] = S(AccountDeletionPolicy.InProgress);
            }

            var update = "SET #ds = :next, #da = if_not_exists(#da, :deletedAt), #ua = :now, #v = :newVersion";
            if (removes.Count > 0)
            {
                update += " REMOVE " + string.Join(", ", removes);
            }

            try
            {
                var response = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tables.Users,
                    Key = UserKey(userId),
                    UpdateExpression = update,
                    ConditionExpression = condition,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                    ReturnValues = ReturnValue.ALL_NEW
                });
                return response.Attributes != null && response.Attributes.TryGetValue("deletedAt", out var written)
                    ? written.S
                    : deletedAt;
            }
            catch (ConditionalCheckFailedException)
            {
                // A concurrent write moved the version: read again and rebuild the removal list.
            }
        }

        throw new InvalidOperationException("The users row kept changing while it was being tombstoned.");
    }

    public async Task<bool> MarkCompletedAsync(string userId)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tables.Users,
                Key = UserKey(userId),
                UpdateExpression = "SET #ds = :completed, #ua = :now",
                ConditionExpression = "#ds IN (:pending, :completed)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#ds", "deletionState" },
                    { "#ua", "updatedAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":completed", S(AccountDeletionPolicy.Completed) },
                    { ":pending", S(AccountDeletionPolicy.CognitoPending) },
                    { ":now", S(Now()) }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<(List<string> UserIds, Dictionary<string, AttributeValue>? Next)> ScanIncompleteAsync(
        string requestedBefore, Dictionary<string, AttributeValue>? startKey)
    {
        // The users table has no index on the deletion state; a filtered Scan twice a day is cheap.
        var response = await _dynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _tables.Users,
            FilterExpression = "#ds IN (:inProgress, :pending) AND #dra < :cutoff",
            ProjectionExpression = "userId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#ds", "deletionState" },
                { "#dra", "deletionRequestedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":inProgress", S(AccountDeletionPolicy.InProgress) },
                { ":pending", S(AccountDeletionPolicy.CognitoPending) },
                { ":cutoff", S(requestedBefore) }
            },
            ExclusiveStartKey = startKey
        });

        var ids = (response?.Items ?? new List<Dictionary<string, AttributeValue>>())
            .Select(i => i.TryGetValue("userId", out var id) ? id.S : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        return (ids, response?.LastEvaluatedKey is { Count: > 0 } next ? next : null);
    }

    // ── rows the user is a party to ──

    public async Task<List<PartyRow>> QueryOrdersAsync(string userId)
        => await QueryPartyAsync(_tables.Orders, userId,
            ("BuyerIdCreatedAtIndex", "buyerId", "buyer"),
            ("FarmerIdCreatedAtIndex", "farmerId", "farmer"),
            ("TruckerIdCreatedAtIndex", "truckerId", "trucker"));

    public async Task<Dictionary<string, object>?> GetOrderAsync(string orderId, string createdAt)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tables.Orders,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", S(orderId) },
                { "createdAt", S(createdAt) }
            },
            ConsistentRead = true
        });
        return response?.Item is { Count: > 0 } item ? DynamoDbHelper.FromItem(item) : null;
    }

    public async Task<List<PartyRow>> QueryEscrowsAsync(string userId)
        => await QueryPartyAsync(_tables.Escrows, userId,
            ("BuyerStatusIndex", "buyerId", "buyer"),
            ("FarmerStatusIndex", "farmerId", "farmer"),
            ("TruckerStatusIndex", "truckerId", "trucker"));

    public async Task<List<PartyRow>> QueryDeliveriesAsync(string userId, IReadOnlyCollection<BuyerOrderRef> buyerOrders)
    {
        var rows = await QueryPartyAsync(_tables.Deliveries, userId,
            ("FarmerIdCreatedAtIndex", "farmerId", "farmer"),
            ("TruckerIdCreatedAtIndex", "truckerId", "trucker"),
            ("ParticipantIndex", "participantId", "buyer"));

        var seen = new HashSet<string>(
            rows.Where(r => r.Role == "buyer").Select(r => AccountDeletionPolicy.Text(r.Item, "deliveryId")),
            StringComparer.Ordinal);

        // Deliveries created before participantId was written are only reachable through the
        // listing: one query per listing, keeping the rows of this buyer or of one of their orders.
        foreach (var listing in buyerOrders.Where(o => !string.IsNullOrEmpty(o.ListingId)).GroupBy(o => o.ListingId))
        {
            var orderIds = listing.Select(o => o.OrderId).ToHashSet(StringComparer.Ordinal);
            foreach (var item in await QueryIndexAsync(_tables.Deliveries, "ListingIndex", "listingId", listing.Key))
            {
                var row = DynamoDbHelper.FromItem(item);
                var ofBuyer = AccountDeletionPolicy.Text(row, "buyerId") == userId
                              || orderIds.Contains(AccountDeletionPolicy.Text(row, "orderId"));
                if (ofBuyer && seen.Add(AccountDeletionPolicy.Text(row, "deliveryId")))
                {
                    rows.Add(new PartyRow("buyer", row));
                }
            }
        }

        return rows;
    }

    public async Task<bool> HasCheckoutSlotAsync(string orderId) => await _checkouts.GetSlotAsync(orderId) != null;

    public Task<List<Dictionary<string, object>>> GetOrderPaymentTrailAsync(string orderId)
        => _escrowAudit.GetAuditTrailAsync(UnappliedPayments.EscrowKey(orderId), consistentRead: true);

    // ── step 7: listings ──

    public async Task<List<ListingKey>> QueryCropKeysAsync(string farmerId)
        => (await QueryIndexAsync(_tables.Crops, "FarmerIdCreatedAtIndex", "farmerId", farmerId))
            .Select(i => ToListingKey(i, "cropId"))
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();

    public async Task<List<ListingKey>> QueryTripKeysAsync(string truckerId)
        => (await QueryIndexAsync(_tables.Trips, "TruckerIdCreatedAtIndex", "truckerId", truckerId))
            .Select(i => ToListingKey(i, "tripId"))
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();

    public Task<bool> WithdrawCropAsync(ListingKey key, string now)
        => WithdrawListingAsync(_tables.Crops, "cropId", key, now,
            withdrawFrom: new[] { "available", "reserved" }, withdrawnStatus: "deleted",
            nameField: "farmerName", scrubFields: CropScrubFields);

    public Task<bool> WithdrawTripAsync(ListingKey key, string now)
        => WithdrawListingAsync(_tables.Trips, "tripId", key, now,
            withdrawFrom: new[] { "open", "matched" }, withdrawnStatus: "cancelled",
            nameField: "truckerName", scrubFields: TripScrubFields);

    /// <summary>
    /// The step 7 rule. A listing still on offer is withdrawn (status, <c>deletedAt</c>); any other
    /// keeps its status and gets <c>deletedAt</c> only if missing. Both are scrubbed and take
    /// <c>version + 1</c>, conditional on the status and version read; a lost condition re-reads.
    /// </summary>
    private async Task<bool> WithdrawListingAsync(
        string table, string idField, ListingKey key, string now,
        string[] withdrawFrom, string withdrawnStatus, string nameField, string[] scrubFields)
    {
        var itemKey = new Dictionary<string, AttributeValue>
        {
            { idField, S(key.Id) },
            { "createdAt", S(key.CreatedAt) }
        };

        for (var attempt = 0; attempt < MaxConditionAttempts; attempt++)
        {
            var response = await _dynamoDb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = itemKey,
                ConsistentRead = true
            });
            if (response?.Item is not { Count: > 0 } item)
            {
                return false;
            }

            var status = item.TryGetValue("status", out var s) ? s.S : null;
            var withdraw = status != null && withdrawFrom.Contains(status);
            var named = item.TryGetValue(nameField, out var n) && n.S == AccountDeletionPolicy.Sentinel;
            if (!withdraw && named && item.ContainsKey("deletedAt") && !scrubFields.Any(item.ContainsKey))
            {
                return false;
            }

            var names = new Dictionary<string, string>
            {
                { "#s", "status" },
                { "#v", "version" },
                { "#name", nameField },
                { "#da", "deletedAt" },
                { "#ua", "updatedAt" }
            };
            var values = new Dictionary<string, AttributeValue>
            {
                { ":sentinel", S(AccountDeletionPolicy.Sentinel) },
                { ":now", S(now) }
            };

            var sets = new List<string> { "#name = :sentinel", "#ua = :now" };
            if (withdraw)
            {
                sets.Add("#s = :withdrawn");
                sets.Add("#da = :now");
                values[":withdrawn"] = S(withdrawnStatus);
            }
            else
            {
                sets.Add("#da = if_not_exists(#da, :now)");
            }

            var readVersion = item.TryGetValue("version", out var v) && v.N != null
                ? int.Parse(v.N, CultureInfo.InvariantCulture)
                : (int?)null;
            sets.Add("#v = :newVersion");
            values[":newVersion"] = N((readVersion ?? 0) + 1);

            var conditions = new List<string>();
            if (status == null)
            {
                conditions.Add("attribute_not_exists(#s)");
            }
            else
            {
                conditions.Add("#s = :readStatus");
                values[":readStatus"] = S(status);
            }

            if (readVersion == null)
            {
                conditions.Add("attribute_not_exists(#v)");
            }
            else
            {
                conditions.Add("#v = :readVersion");
                values[":readVersion"] = N(readVersion.Value);
            }

            var removes = new List<string>();
            foreach (var field in scrubFields)
            {
                var placeholder = $"#r{removes.Count}";
                names[placeholder] = field;
                removes.Add(placeholder);
            }

            try
            {
                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = table,
                    Key = itemKey,
                    UpdateExpression = "SET " + string.Join(", ", sets) + " REMOVE " + string.Join(", ", removes),
                    ConditionExpression = string.Join(" AND ", conditions),
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                });
                return true;
            }
            catch (ConditionalCheckFailedException)
            {
                // Changed since the read: read again and reapply the rule for its new status.
            }
        }

        throw new InvalidOperationException($"The listing {key.Id} kept changing while it was being withdrawn.");
    }

    // ── step 8: unpaid orders ──

    public async Task<bool> TryCancelUnpaidOrderAsync(Dictionary<string, object> order, string now)
    {
        var version = AccountDeletionPolicy.Number(order, "version") ?? 0;
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tables.Orders,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "orderId", S(AccountDeletionPolicy.Text(order, "orderId")) },
                    { "createdAt", S(AccountDeletionPolicy.Text(order, "createdAt")) }
                },
                UpdateExpression = "SET #s = :cancelled, #ca = :now, #cr = :reason, #ua = :now, #v = :newVersion",
                ConditionExpression = "#s IN (:created, :pending, :failed) AND #v = :readVersion",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#s", "status" },
                    { "#ca", "cancelledAt" },
                    { "#cr", "cancelReason" },
                    { "#ua", "updatedAt" },
                    { "#v", "version" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":cancelled", S("cancelled") },
                    { ":now", S(now) },
                    { ":reason", S(AccountDeletionPolicy.CancelReason) },
                    { ":created", S("created") },
                    { ":pending", S("pending_payment") },
                    { ":failed", S("payment_failed") },
                    { ":readVersion", N(version) },
                    { ":newVersion", N(version + 1) }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    // ── step 10: per-table policy ──

    public Task<bool> ScrubOrderAsync(Dictionary<string, object> order, IReadOnlyCollection<string> roles, string now)
        => ScrubPartyRowAsync(_tables.Orders, order, roles, ("orderId", "createdAt"), removePhones: false, stampUpdatedAt: now);

    public Task<bool> ScrubDeliveryAsync(Dictionary<string, object> delivery, IReadOnlyCollection<string> roles, string now)
        => ScrubPartyRowAsync(_tables.Deliveries, delivery, roles, ("deliveryId", "tripId"), removePhones: true, stampUpdatedAt: now);

    public Task<bool> ScrubEscrowAsync(Dictionary<string, object> escrow, IReadOnlyCollection<string> roles)
        => ScrubPartyRowAsync(_tables.Escrows, escrow, roles, ("escrowId", "createdAt"), removePhones: false, stampUpdatedAt: null);

    /// <summary>
    /// The user's copied name fields that the row has become the sentinel (a missing field is not
    /// added) and, with <paramref name="removePhones"/>, their phone fields go. Only on an existing
    /// row; no write when nothing would change.
    /// </summary>
    private async Task<bool> ScrubPartyRowAsync(
        string table, Dictionary<string, object> row, IReadOnlyCollection<string> roles,
        (string Partition, string Sort) keyNames, bool removePhones, string? stampUpdatedAt)
    {
        var partition = AccountDeletionPolicy.Text(row, keyNames.Partition);
        var sort = AccountDeletionPolicy.Text(row, keyNames.Sort);
        if (partition.Length == 0 || sort.Length == 0)
        {
            return false;
        }

        var names = new Dictionary<string, string> { { "#pk", keyNames.Partition } };
        var sets = new List<string>();
        var removes = new List<string>();
        foreach (var role in roles.Distinct(StringComparer.Ordinal))
        {
            var nameField = AccountDeletionPolicy.NameField(role);
            if (row.ContainsKey(nameField) && AccountDeletionPolicy.Text(row, nameField) != AccountDeletionPolicy.Sentinel)
            {
                var placeholder = $"#n{sets.Count}";
                names[placeholder] = nameField;
                sets.Add($"{placeholder} = :sentinel");
            }

            var phoneField = AccountDeletionPolicy.PhoneField(role);
            if (removePhones && row.ContainsKey(phoneField))
            {
                var placeholder = $"#p{removes.Count}";
                names[placeholder] = phoneField;
                removes.Add(placeholder);
            }
        }

        if (sets.Count == 0 && removes.Count == 0)
        {
            return false;
        }

        var values = new Dictionary<string, AttributeValue>();
        if (sets.Count > 0)
        {
            values[":sentinel"] = S(AccountDeletionPolicy.Sentinel);
        }

        if (stampUpdatedAt != null)
        {
            names["#ua"] = "updatedAt";
            sets.Add("#ua = :now");
            values[":now"] = S(stampUpdatedAt);
        }

        var update = (sets.Count > 0 ? "SET " + string.Join(", ", sets) : "")
                     + (removes.Count > 0 ? " REMOVE " + string.Join(", ", removes) : "");
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue>
                {
                    { keyNames.Partition, S(partition) },
                    { keyNames.Sort, S(sort) }
                },
                UpdateExpression = update.Trim(),
                ConditionExpression = "attribute_exists(#pk)",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values.Count > 0 ? values : null
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<List<Dictionary<string, object>>> QueryRatingsReceivedAsync(string rateeId)
        => (await QueryIndexAsync(_tables.Ratings, "RateeIdCreatedAtIndex", "rateeId", rateeId))
            .Select(DynamoDbHelper.FromItem)
            .ToList();

    public async Task<Dictionary<string, object>?> GetRatingAsync(string ratingId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tables.Ratings,
            Key = new Dictionary<string, AttributeValue> { { "ratingId", S(ratingId) } },
            ProjectionExpression = "ratingId, #c",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#c", "comment" } }
        });
        return response?.Item is { Count: > 0 } item ? DynamoDbHelper.FromItem(item) : null;
    }

    public async Task<bool> RemoveRatingCommentAsync(string ratingId)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tables.Ratings,
                Key = new Dictionary<string, AttributeValue> { { "ratingId", S(ratingId) } },
                UpdateExpression = "REMOVE #c",
                ConditionExpression = "attribute_exists(ratingId) AND attribute_exists(#c)",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#c", "comment" } }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<List<string>> QueryMessageIdsAsync(string userId)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, keyField) in new[] { ("SenderIdCreatedAtIndex", "senderId"), ("RecipientIdCreatedAtIndex", "recipientId") })
        {
            foreach (var item in await QueryIndexAsync(_tables.Messages, index, keyField, userId, projection: "messageId"))
            {
                if (item.TryGetValue("messageId", out var id) && !string.IsNullOrEmpty(id.S) && seen.Add(id.S))
                {
                    ids.Add(id.S);
                }
            }
        }

        return ids;
    }

    public Task DeleteMessageAsync(string messageId)
        => _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tables.Messages,
            Key = new Dictionary<string, AttributeValue> { { "messageId", S(messageId) } }
        });

    public async Task<List<string>> QueryIntentIdsAsync(string buyerId)
        => (await QueryAllAsync(new QueryRequest
        {
            TableName = _tables.Intents,
            KeyConditionExpression = "buyerId = :id",
            ProjectionExpression = "intentId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { ":id", S(buyerId) } }
        }))
            .Select(i => i.TryGetValue("intentId", out var id) ? id.S : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();

    public async Task<List<string>> QueryMatchEventIdsAsync(string intentId)
        => (await QueryAllAsync(new QueryRequest
        {
            TableName = _tables.MatchEvents,
            KeyConditionExpression = "intentId = :id",
            ProjectionExpression = "eventId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { ":id", S(intentId) } }
        }))
            .Select(i => i.TryGetValue("eventId", out var id) ? id.S : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();

    public Task DeleteMatchEventAsync(string intentId, string eventId)
        => _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tables.MatchEvents,
            Key = new Dictionary<string, AttributeValue>
            {
                { "intentId", S(intentId) },
                { "eventId", S(eventId) }
            }
        });

    public Task DeleteIntentAsync(string buyerId, string intentId)
        => _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tables.Intents,
            Key = new Dictionary<string, AttributeValue>
            {
                { "buyerId", S(buyerId) },
                { "intentId", S(intentId) }
            }
        });

    public Task<List<string>> QueryDeviceEndpointsAsync(string userId) => _deviceTokens.GetEndpointArnsByUserAsync(userId);

    public Task DeleteDeviceTokenAsync(string userId, string endpointArn) => _deviceTokens.DeleteAsync(userId, endpointArn);

    // ── helpers ──

    private async Task<Dictionary<string, AttributeValue>?> GetUserItemAsync(string userId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tables.Users,
            Key = UserKey(userId),
            ConsistentRead = true
        });
        return response?.Item is { Count: > 0 } item ? item : null;
    }

    private async Task<List<PartyRow>> QueryPartyAsync(
        string table, string userId, params (string Index, string KeyField, string Role)[] indexes)
    {
        var rows = new List<PartyRow>();
        foreach (var (index, keyField, role) in indexes)
        {
            foreach (var item in await QueryIndexAsync(table, index, keyField, userId))
            {
                rows.Add(new PartyRow(role, DynamoDbHelper.FromItem(item)));
            }
        }

        return rows;
    }

    private Task<List<Dictionary<string, AttributeValue>>> QueryIndexAsync(
        string table, string index, string keyField, string value, string? projection = null)
        => QueryAllAsync(new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "#k = :v",
            ProjectionExpression = projection,
            ExpressionAttributeNames = new Dictionary<string, string> { { "#k", keyField } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { ":v", S(value) } }
        });

    private async Task<List<Dictionary<string, AttributeValue>>> QueryAllAsync(QueryRequest request)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var response = await _dynamoDb.QueryAsync(request);
            if (response?.Items != null)
            {
                items.AddRange(response.Items);
            }

            request.ExclusiveStartKey = response?.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (request.ExclusiveStartKey != null);

        return items;
    }

    private static ListingKey? ToListingKey(Dictionary<string, AttributeValue> item, string idField)
        => item.TryGetValue(idField, out var id) && !string.IsNullOrEmpty(id.S)
           && item.TryGetValue("createdAt", out var createdAt) && !string.IsNullOrEmpty(createdAt.S)
            ? new ListingKey(id.S, createdAt.S)
            : null;

    private static Dictionary<string, AttributeValue> UserKey(string userId)
        => new() { { "userId", S(userId) } };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static string Now() => DateTime.UtcNow.ToString("o");
}

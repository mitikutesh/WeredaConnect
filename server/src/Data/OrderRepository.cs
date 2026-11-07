using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB order repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
/// Version-based optimistic concurrency for sync conflict detection.
///
/// Table schema (Orders):
///   PK: orderId (STRING)
///   SK: createdAt (STRING)
///   GSI BuyerIdCreatedAtIndex: buyerId + createdAt
///   GSI FarmerIdCreatedAtIndex: farmerId + createdAt
///   GSI TruckerIdCreatedAtIndex: truckerId + createdAt
///   GSI StatusCreatedAtIndex: status + createdAt
///   GSI ListingIdIndex: listingId + createdAt
///   GSI TripIdIndex: tripId + createdAt
///   GSI AdminQueueIndex (sparse, ESC-06): adminQueue + adminQueuedAt
///   GSI DeadlineIndex (sparse, ESC-16): deadlineShard + nextDeadlineAt
///
/// ESC-16: every write that can change what is due on an order (the versioned update, the fenced and
/// the linked update, the funding, the status advance and the stamp) also writes its next deadline
/// (<see cref="OrderDeadlines"/>) in the same request, derived from the row as it will be after the
/// write (the row read before it plus the fields set), or removes it when nothing is pending. Where
/// the write returns the row (or reads it back), the deadline is checked against it and corrected
/// with one more write conditioned on the version just written: only a stale read before the write
/// can make them differ. A failure there never fails the write; it logs
/// <c>ORDER_DEADLINE_STAMP_FAILED</c> and the cron's daily repair walk stamps the row. The create and
/// <c>payment_submitted -> payment_failed</c> leave it alone: no deadline of the index is pending
/// before funding.
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public OrderRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string orderId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "orderId", new AttributeValue { S = orderId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } }
        };

        // Copy data fields into the item
        foreach (var kvp in data)
        {
            if (kvp.Key is "orderId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(orderId)"
        };

        try
        {
            if (await DynamoDbHelper.PartitionExistsAsync(_dynamoDb, _tableName, "orderId", orderId))
            {
                throw new ConditionalCheckFailedException("Order already exists.");
            }

            await _dynamoDb.PutItemAsync(request);

            var result = DynamoDbHelper.FromItem(item);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Order already exists.", "orderId");
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion)
    {
        // First, get the current item to know its sort key
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                404, "SYNC_VALIDATION", "Order not found.", "orderId");
        }

        var request = BuildUpdateRequest(orderId, existing, data, expectedVersion);
        Dictionary<string, object> result;
        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            result = DynamoDbHelper.FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return VersionConflict(existing, expectedVersion);
        }

        // ESC-16: the read above is eventually consistent, so check the deadline against the row written.
        await EnsureDeadlineAsync(result);
        return AuthResult<Dictionary<string, object>>.Success(result);
    }

    /// <summary>FRD-03: the failure code of <see cref="UpdateUnlessEscrowHeldAsync"/> when the order's escrow is held.</summary>
    public const string EscrowHeldCode = "ESCROW_HELD";

    /// <summary>
    /// FRD-03: the transaction item that refuses a write while the order's escrow is <c>held</c>, for
    /// callers that build their own order transaction (ESC-15: the trucker's accept with the trip
    /// booking). <see cref="UpdateUnlessEscrowHeldAsync"/> uses the same condition.
    /// </summary>
    public static TransactWriteItem EscrowNotHeldCheck(EscrowRowKey escrow) => new()
    {
        ConditionCheck = new ConditionCheck
        {
            TableName = escrow.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrow.EscrowId } },
                { "createdAt", new AttributeValue { S = escrow.CreatedAt } }
            },
            ConditionExpression = "attribute_not_exists(#status) OR #status <> :held",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":held", new AttributeValue { S = EscrowHoldFields.Status } }
            }
        }
    };

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> UpdateUnlessEscrowHeldAsync(
        string orderId, Dictionary<string, object> data, int expectedVersion, EscrowRowKey escrow)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                404, "SYNC_VALIDATION", "Order not found.", "orderId");
        }

        var update = BuildUpdateRequest(orderId, existing, data, expectedVersion);
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = new List<TransactWriteItem>
                {
                    new()
                    {
                        Update = new Update
                        {
                            TableName = update.TableName,
                            Key = update.Key,
                            UpdateExpression = update.UpdateExpression,
                            ConditionExpression = update.ConditionExpression,
                            ExpressionAttributeNames = update.ExpressionAttributeNames,
                            ExpressionAttributeValues = update.ExpressionAttributeValues
                        }
                    },
                    new()
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = escrow.TableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { "escrowId", new AttributeValue { S = escrow.EscrowId } },
                                { "createdAt", new AttributeValue { S = escrow.CreatedAt } }
                            },
                            ConditionExpression = "attribute_not_exists(#status) OR #status <> :held",
                            ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                { ":held", new AttributeValue { S = EscrowHoldFields.Status } }
                            }
                        }
                    }
                }
            });
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order: 0 is the order, 1 the escrow. A held escrow
            // decides the answer even when the version moved too.
            if (IsConditionFailure(ex.CancellationReasons, 1))
            {
                return AuthResult<Dictionary<string, object>>.Failure(
                    409, EscrowHeldCode, "The order's escrow is held for review.");
            }

            if (IsConditionFailure(ex.CancellationReasons, 0))
            {
                return VersionConflict(existing, expectedVersion);
            }

            throw;
        }

        var written = await GetItemAsync(orderId, consistentRead: true);
        if (written == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(404, "SYNC_VALIDATION", "Order not found.", "orderId");
        }

        var row = DynamoDbHelper.FromItem(written);
        await EnsureDeadlineAsync(row);
        return AuthResult<Dictionary<string, object>>.Success(row);
    }

    private static AuthResult<Dictionary<string, object>> VersionConflict(
        Dictionary<string, AttributeValue> existing, int expectedVersion)
        => AuthResult<Dictionary<string, object>>.Failure(
            new AuthResultError
            {
                HttpStatus = 409,
                Code = "SYNC_CONFLICT",
                Message = $"Version conflict: server has version {existing["version"].N}, client expected {expectedVersion}."
            });

    /// <summary>
    /// The versioned order update of <see cref="UpdateAsync"/>, shared with the fenced write. ESC-16: it
    /// also sets (or removes) the next deadline of the row as <paramref name="existing"/> plus
    /// <paramref name="data"/>.
    /// </summary>
    private UpdateItemRequest BuildUpdateRequest(
        string orderId, Dictionary<string, AttributeValue> existing, Dictionary<string, object> data, int expectedVersion)
    {
        var createdAt = existing["createdAt"].S;
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = expectedVersion + 1;

        // Build update expression from data fields
        var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
        var removeParts = new List<string>();
        var attrNames = new Dictionary<string, string>
        {
            { "#updated", "updatedAt" },
            { "#v", "version" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":updated", new AttributeValue { S = now } },
            { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
            { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } }
        };

        var i = 0;
        foreach (var kvp in data)
        {
            if (kvp.Key is "orderId" or "version" or "createdAt" or "updatedAt" || OrderDeadlines.IsDeadlineField(kvp.Key))
            {
                continue;
            }

            var nameAlias = $"#f{i}";
            var valueAlias = $":f{i}";
            attrNames[nameAlias] = kvp.Key;
            attrValues[valueAlias] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            updateParts.Add($"{nameAlias} = {valueAlias}");
            i++;
        }

        AppendDeadline(AfterWrite(existing, data), updateParts, removeParts, attrNames, attrValues);

        return new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = UpdateExpression(updateParts, removeParts),
            ConditionExpression = "#v = :expectedVersion",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW
        };
    }

    /// <inheritdoc />
    public async Task<OrderLinkedWriteResult> TryUpdateWithAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion,
        string expectedStatus,
        IReadOnlyList<TransactWriteItem> linkedItems)
    {
        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderNotFound);
        }

        var now = DateTime.UtcNow.ToString("o");
        var written = new Dictionary<string, AttributeValue>(existing)
        {
            ["updatedAt"] = new AttributeValue { S = now },
            ["version"] = new AttributeValue { N = (expectedVersion + 1).ToString() }
        };

        var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
        var attrNames = new Dictionary<string, string>
        {
            { "#updated", "updatedAt" },
            { "#v", "version" },
            { "#expectedStatusName", "status" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":updated", written["updatedAt"] },
            { ":newVersion", written["version"] },
            { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } },
            { ":expectedStatus", new AttributeValue { S = expectedStatus } }
        };

        var i = 0;
        foreach (var kvp in data)
        {
            if (kvp.Key is "orderId" or "version" or "createdAt" or "updatedAt" || OrderDeadlines.IsDeadlineField(kvp.Key))
            {
                continue;
            }

            var value = DynamoDbHelper.ToAttributeValue(kvp.Value);
            attrNames[$"#f{i}"] = kvp.Key;
            attrValues[$":f{i}"] = value;
            updateParts.Add($"#f{i} = :f{i}");
            written[kvp.Key] = value;
            i++;
        }

        // ESC-16: the read above is consistent and the write checks the version, so the deadline is
        // derived from the row as written, except for a marker stamp (no version bump) that lands
        // between the read and the write. No check after the write: such a row is due early at worst,
        // and the sweep re-reads it and sets its correct deadline when it comes due.
        var removeParts = new List<string>();
        var deadline = AppendDeadline(DynamoDbHelper.FromItem(written), updateParts, removeParts, attrNames, attrValues);
        if (deadline.Added)
        {
            ApplyDeadline(written, deadline.Next);
        }

        var items = new List<TransactWriteItem>
        {
            new()
            {
                Update = new Update
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "orderId", new AttributeValue { S = orderId } },
                        { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
                    },
                    UpdateExpression = UpdateExpression(updateParts, removeParts),
                    ConditionExpression = "#v = :expectedVersion AND #expectedStatusName = :expectedStatus",
                    ExpressionAttributeNames = attrNames,
                    ExpressionAttributeValues = attrValues
                }
            }
        };
        items.AddRange(linkedItems);

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order. A linked item that refused decides: the
            // order's own condition is worth a retry only when everything it carries still holds.
            for (var index = 1; index < items.Count; index++)
            {
                if (IsConditionFailure(ex.CancellationReasons, index))
                {
                    return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused);
                }
            }

            if (IsConditionFailure(ex.CancellationReasons, 0))
            {
                return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.OrderChanged);
            }

            // A conflicting transaction or throttling: nothing was written, the caller retries.
            throw;
        }

        return new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, DynamoDbHelper.FromItem(written));
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string orderId)
    {
        var item = await GetItemAsync(orderId);
        return item != null ? DynamoDbHelper.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string orderId, bool consistentRead)
    {
        var item = await GetItemAsync(orderId, consistentRead);
        return item != null ? DynamoDbHelper.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> TryMarkFundedAsync(
        string orderId, Dictionary<string, object> fields)
    {
        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return null;
        }

        var setParts = new List<string> { "#status = :funded", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" },
            { "#v", "version" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":funded", new AttributeValue { S = "funded" } },
            { ":submitted", new AttributeValue { S = "payment_submitted" } },
            { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
            { ":one", new AttributeValue { N = "1" } }
        };

        var i = 0;
        foreach (var kvp in fields)
        {
            if (kvp.Key is "orderId" or "createdAt" or "updatedAt" or "version" or "status" || OrderDeadlines.IsDeadlineField(kvp.Key))
            {
                continue;
            }

            attrNames[$"#f{i}"] = kvp.Key;
            attrValues[$":f{i}"] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            setParts.Add($"#f{i} = :f{i}");
            i++;
        }

        // ESC-16: the farmer's request (T3) starts with the funding.
        var removeParts = new List<string>();
        AppendDeadline(AfterWrite(existing, fields, "funded"), setParts, removeParts, attrNames, attrValues);

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
            },
            UpdateExpression = UpdateExpression(setParts, removeParts, "#v :one"),
            ConditionExpression = "#status = :submitted",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW
        };

        Dictionary<string, object> funded;
        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            funded = DynamoDbHelper.FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }

        await EnsureDeadlineAsync(funded);
        return funded;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOrdersAsync(
        string? buyerId = null,
        string? farmerId = null,
        string? truckerId = null,
        string? status = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Use most specific GSI available. When a status filter is supplied
        // alongside a role id, it is applied as a FilterExpression on top of the
        // role GSI query — otherwise the status was silently dropped and every
        // status tab returned the user's full order list (web bug).
        if (!string.IsNullOrEmpty(buyerId))
        {
            return await QueryByGsiAsync("BuyerIdCreatedAtIndex", "buyerId", buyerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(farmerId))
        {
            return await QueryByGsiAsync("FarmerIdCreatedAtIndex", "farmerId", farmerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(truckerId))
        {
            return await QueryByGsiAsync("TruckerIdCreatedAtIndex", "truckerId", truckerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(status))
        {
            return await QueryByGsiAsync("StatusCreatedAtIndex", "status", status, limit, exclusiveStartKey);
        }

        if (!string.IsNullOrEmpty(listingId))
        {
            return await QueryByGsiAsync("ListingIdIndex", "listingId", listingId, limit, exclusiveStartKey);
        }

        // No filter — scan (limited use for admin)
        var scanRequest = new ScanRequest
        {
            TableName = _tableName,
            Limit = limit
        };

        if (exclusiveStartKey != null)
        {
            scanRequest.ExclusiveStartKey = exclusiveStartKey;
        }

        var scanResponse = await _dynamoDb.ScanAsync(scanRequest);
        var items = scanResponse.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, scanResponse.LastEvaluatedKey.Count > 0 ? scanResponse.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(string status, string? createdBefore = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusCreatedAtIndex",
            KeyConditionExpression = "#status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } }
            },
            ScanIndexForward = true // oldest first for cron
        };

        if (!string.IsNullOrEmpty(createdBefore))
        {
            request.KeyConditionExpression += " AND createdAt < :createdBefore";
            request.ExpressionAttributeValues[":createdBefore"] = new AttributeValue { S = createdBefore };
        }

        var result = new List<Dictionary<string, object>>();
        QueryResponse? response = null;

        do
        {
            if (response?.LastEvaluatedKey?.Count > 0)
            {
                request.ExclusiveStartKey = response.LastEvaluatedKey;
            }

            response = await _dynamoDb.QueryAsync(request);
            result.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
        }
        while (response.LastEvaluatedKey?.Count > 0);

        return result;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByStatusCreatedSinceAsync(
        string status, string createdSince, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 100)
        {
            limit = 100;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusCreatedAtIndex",
            KeyConditionExpression = "#status = :status AND #created >= :since",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#created", "createdAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } },
                { ":since", new AttributeValue { S = createdSince } }
            },
            Limit = limit,
            ScanIndexForward = false // newest first
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<DeliverySlotWriteResult> TryCreateDeliveryWithSlotAsync(
        string orderId,
        string deliveriesTable,
        Dictionary<string, AttributeValue> deliveryItem,
        DeliverySlotHolder? replacing = null)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return DeliverySlotWriteResult.OrderNotFound;
        }

        var values = new Dictionary<string, AttributeValue>
        {
            { ":did", new AttributeValue { S = deliveryItem["deliveryId"].S } },
            { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };

        string slotCondition;
        if (replacing == null)
        {
            slotCondition = "attribute_not_exists(#slot) OR #slot = :did";
        }
        else
        {
            values[":stale"] = new AttributeValue { S = replacing.DeliveryId };
            if (replacing.ClaimedAt == null)
            {
                slotCondition = "#slot = :stale AND attribute_not_exists(#claimedAt)";
            }
            else
            {
                values[":seen"] = new AttributeValue { S = replacing.ClaimedAt };
                slotCondition = "#slot = :stale AND #claimedAt = :seen";
            }
        }

        // ESC-06: the claim also requires the order to still take a delivery, so one never lands
        // on an order that was cancelled after the caller read it.
        values[":accepted"] = new AttributeValue { S = "accepted" };
        values[":pickupConfirmed"] = new AttributeValue { S = "pickup_confirmed" };
        values[":inTransit"] = new AttributeValue { S = "in_transit" };

        // Deliveries have no order index, so the order row carries the single-active-
        // delivery invariant as a conditional write, in the same transaction as the row it
        // names. The version is deliberately not bumped: the claim is not a client-visible
        // edit of the order.
        var request = new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new()
                {
                    Update = new Update
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            { "orderId", new AttributeValue { S = orderId } },
                            { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
                        },
                        UpdateExpression = "SET #slot = :did, #claimedAt = :now",
                        ConditionExpression = "attribute_exists(orderId) "
                            + $"AND #status IN (:accepted, :pickupConfirmed, :inTransit) AND ({slotCondition})",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            { "#slot", "activeDeliveryId" },
                            { "#claimedAt", "activeDeliveryClaimedAt" },
                            { "#status", "status" }
                        },
                        ExpressionAttributeValues = values
                    }
                },
                new()
                {
                    Put = new Put
                    {
                        TableName = deliveriesTable,
                        Item = deliveryItem,
                        ConditionExpression = "attribute_not_exists(deliveryId)"
                    }
                }
            }
        };

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request);
            return DeliverySlotWriteResult.Written;
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order. An existing row wins over a taken
            // slot: the caller answers it like any create of an existing delivery.
            if (IsConditionFailure(ex.CancellationReasons, 1))
            {
                return DeliverySlotWriteResult.DeliveryExists;
            }

            if (IsConditionFailure(ex.CancellationReasons, 0))
            {
                // The slot or the order status refused it: the fresh row says which.
                var current = await GetItemAsync(orderId, consistentRead: true);
                var status = current != null && current.TryGetValue("status", out var statusAttr) ? statusAttr.S : null;
                return current == null
                    ? DeliverySlotWriteResult.OrderNotFound
                    : status is "accepted" or "pickup_confirmed" or "in_transit"
                        ? DeliverySlotWriteResult.SlotTaken
                        : DeliverySlotWriteResult.OrderNotActive;
            }

            // A conflicting transaction or throttling: nothing was written, so the caller's
            // failure is safe to retry.
            throw;
        }
    }

    private static bool IsConditionFailure(List<CancellationReason>? reasons, int index) =>
        reasons != null && reasons.Count > index && reasons[index]?.Code == "ConditionalCheckFailed";

    /// <inheritdoc />
    public async Task<bool> TryEnterAdminQueueAsync(
        string orderId, string reason, string escrowId, string queuedAt, string expectedStatus)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return false;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = OrderKey(orderId, existing["createdAt"].S),
            UpdateExpression = "SET #marker = :open, #queuedAt = :queuedAt, #reason = :reason, #escrowId = :escrowId",
            ConditionExpression = "attribute_exists(orderId) AND attribute_not_exists(#marker) AND #status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#marker", AdminQueueFields.Marker },
                { "#queuedAt", AdminQueueFields.QueuedAt },
                { "#reason", AdminQueueFields.Reason },
                { "#escrowId", AdminQueueFields.EscrowId },
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":open", new AttributeValue { S = AdminQueueFields.MarkerOpen } },
                { ":queuedAt", new AttributeValue { S = queuedAt } },
                { ":reason", new AttributeValue { S = reason } },
                { ":escrowId", new AttributeValue { S = escrowId } },
                { ":status", new AttributeValue { S = expectedStatus } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryLeaveAdminQueueAsync(string orderId, string seenQueuedAt)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return false;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = OrderKey(orderId, existing["createdAt"].S),
            UpdateExpression = "REMOVE #marker, #queuedAt, #reason, #escrowId",
            ConditionExpression = "#queuedAt = :seen",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#marker", AdminQueueFields.Marker },
                { "#queuedAt", AdminQueueFields.QueuedAt },
                { "#reason", AdminQueueFields.Reason },
                { "#escrowId", AdminQueueFields.EscrowId }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":seen", new AttributeValue { S = seenQueuedAt } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryAdminQueueAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 100)
        {
            limit = 50;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = AdminQueueFields.IndexName,
            KeyConditionExpression = "#marker = :open",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#marker", AdminQueueFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":open", new AttributeValue { S = AdminQueueFields.MarkerOpen } }
            },
            Limit = limit,
            ScanIndexForward = true
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<bool> TryHoldForPaymentAsync(string orderId, string holdUntil, string? paymentSubmittedAt)
    {
        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        var condition = "#status = :submitted AND (attribute_not_exists(#hold) OR #hold < :hold)";
        var names = new Dictionary<string, string> { { "#status", "status" }, { "#hold", PaymentHoldFields.HoldUntil } };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":submitted", new AttributeValue { S = "payment_submitted" } },
            { ":hold", new AttributeValue { S = holdUntil } }
        };
        if (!string.IsNullOrEmpty(paymentSubmittedAt))
        {
            condition += " AND #psa = :psa";
            names["#psa"] = "paymentSubmittedAt";
            values[":psa"] = new AttributeValue { S = paymentSubmittedAt };
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = OrderKey(orderId, existing["createdAt"].S),
                UpdateExpression = "SET #hold = :hold",
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkPaymentFailedAsync(string orderId, int expectedVersion, string nowIso)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return false;
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = OrderKey(orderId, existing["createdAt"].S),
                UpdateExpression = "SET #status = :failed, #updated = :updated, #v = :newVersion",
                ConditionExpression =
                    "#v = :expectedVersion AND #status = :submitted AND (attribute_not_exists(#hold) OR #hold <= :now)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" },
                    { "#hold", PaymentHoldFields.HoldUntil }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":failed", new AttributeValue { S = "payment_failed" } },
                    { ":submitted", new AttributeValue { S = "payment_submitted" } },
                    { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
                    { ":newVersion", new AttributeValue { N = (expectedVersion + 1).ToString(CultureInfo.InvariantCulture) } },
                    { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } },
                    { ":now", new AttributeValue { S = nowIso } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<Dictionary<string, object>?> TryAdvanceStatusAsync(
        string orderId, string fromStatus, string toStatus, Dictionary<string, object>? extraFields = null)
        => AdvanceStatusCoreAsync(orderId, fromStatus, toStatus, extraFields, unless: null);

    /// <inheritdoc />
    public Task<Dictionary<string, object>?> TryAdvanceStatusUnlessAsync(
        string orderId, string fromStatus, string toStatus, Dictionary<string, object>? extraFields,
        string unlessField, string unlessValue)
        => AdvanceStatusCoreAsync(orderId, fromStatus, toStatus, extraFields, (unlessField, unlessValue));

    private async Task<Dictionary<string, object>?> AdvanceStatusCoreAsync(
        string orderId, string fromStatus, string toStatus, Dictionary<string, object>? extraFields,
        (string Field, string Value)? unless)
    {
        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return null;
        }

        var setParts = new List<string> { "#status = :to", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" },
            { "#v", "version" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":to", new AttributeValue { S = toStatus } },
            { ":from", new AttributeValue { S = fromStatus } },
            { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
            { ":one", new AttributeValue { N = "1" } }
        };

        if (extraFields != null)
        {
            var i = 0;
            foreach (var kvp in extraFields)
            {
                if (IsProtectedOrderField(kvp.Key))
                {
                    continue;
                }

                attrNames[$"#f{i}"] = kvp.Key;
                attrValues[$":f{i}"] = DynamoDbHelper.ToAttributeValue(kvp.Value);
                setParts.Add($"#f{i} = :f{i}");
                i++;
            }
        }

        var condition = "attribute_exists(orderId) AND #status = :from";
        if (unless != null)
        {
            attrNames["#unless"] = unless.Value.Field;
            attrValues[":unless"] = new AttributeValue { S = unless.Value.Value };
            condition += " AND (attribute_not_exists(#unless) OR #unless <> :unless)";
        }

        var removeParts = new List<string>();
        AppendDeadline(AfterWrite(existing, extraFields, toStatus), setParts, removeParts, attrNames, attrValues);

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = OrderKey(orderId, existing["createdAt"].S),
            UpdateExpression = UpdateExpression(setParts, removeParts, "#v :one"),
            ConditionExpression = condition,
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW
        };

        Dictionary<string, object> advanced;
        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            advanced = DynamoDbHelper.FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }

        await EnsureDeadlineAsync(advanced);
        return advanced;
    }

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> QueryByTripAsync(string tripId)
        => QueryAllByIndexAsync("TripIdIndex", "tripId", tripId);

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> QueryByListingAsync(string listingId)
        => QueryAllByIndexAsync("ListingIdIndex", "listingId", listingId);

    /// <summary>Every order under one key of a GSI (all pages, oldest first).</summary>
    private async Task<List<Dictionary<string, object>>> QueryAllByIndexAsync(string indexName, string keyName, string keyValue)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = indexName,
            KeyConditionExpression = $"#{keyName} = :{keyName}",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { $"#{keyName}", keyName }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { $":{keyName}", new AttributeValue { S = keyValue } }
            },
            ScanIndexForward = true // oldest first
        };

        var result = new List<Dictionary<string, object>>();
        QueryResponse? response = null;

        do
        {
            if (response?.LastEvaluatedKey?.Count > 0)
            {
                request.ExclusiveStartKey = response.LastEvaluatedKey;
            }

            response = await _dynamoDb.QueryAsync(request);
            result.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
        }
        while (response.LastEvaluatedKey?.Count > 0);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryStampAsync(
        string orderId, Dictionary<string, object> fields, IReadOnlyDictionary<string, string> expectedStrings)
    {
        var setParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>();

        var i = 0;
        foreach (var kvp in fields)
        {
            if (IsProtectedOrderField(kvp.Key))
            {
                continue;
            }

            attrNames[$"#f{i}"] = kvp.Key;
            attrValues[$":f{i}"] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            setParts.Add($"#f{i} = :f{i}");
            i++;
        }

        if (setParts.Count == 0)
        {
            throw new ArgumentException("A stamp needs at least one field that is not a key, version, updatedAt or status.", nameof(fields));
        }

        var conditionParts = new List<string> { "attribute_exists(orderId)" };
        var c = 0;
        foreach (var kvp in expectedStrings)
        {
            attrNames[$"#c{c}"] = kvp.Key;
            attrValues[$":c{c}"] = new AttributeValue { S = kvp.Value };
            conditionParts.Add($"#c{c} = :c{c}");
            c++;
        }

        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        // ESC-16: a marker can end what was due (a reminder stamped), so the deadline moves with it.
        // A stamp never sets the status, so a status in the fields is left out here too.
        var removeParts = new List<string>();
        AppendDeadline(AfterWrite(existing, fields.Where(f => f.Key != "status")), setParts, removeParts, attrNames, attrValues);

        Dictionary<string, object> stamped;
        try
        {
            var response = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = OrderKey(orderId, existing["createdAt"].S),
                UpdateExpression = UpdateExpression(setParts, removeParts),
                ConditionExpression = string.Join(" AND ", conditionParts),
                ExpressionAttributeNames = attrNames,
                ExpressionAttributeValues = attrValues,
                ReturnValues = ReturnValue.ALL_NEW
            });
            stamped = DynamoDbHelper.FromItem(response.Attributes ?? new Dictionary<string, AttributeValue>());
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        await EnsureDeadlineAsync(stamped);
        return true;
    }

    /// <summary>
    /// Order attributes a status or stamp write never takes from its caller's field map: the keys,
    /// the version and timestamp the write owns, the status (set by the write itself or not at all),
    /// and (ESC-16) the deadline attributes, which the write derives itself.
    /// </summary>
    private static bool IsProtectedOrderField(string key)
        => key is "orderId" or "createdAt" or "updatedAt" or "version" or "status" || OrderDeadlines.IsDeadlineField(key);

    // ──────────────────────────────────────────────
    // ESC-16: the next deadline (DeadlineIndex)
    // ──────────────────────────────────────────────

    /// <summary>The token of a deadline the write could not keep in step (the repair walk fixes the row).</summary>
    internal const string DeadlineStampFailedToken = "ORDER_DEADLINE_STAMP_FAILED";

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryDueDeadlinesAsync(
        string dueBefore, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 100)
        {
            limit = 100;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = OrderDeadlines.IndexName,
            KeyConditionExpression = "#shard = :shard AND #at < :due",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#shard", OrderDeadlines.ShardField },
                { "#at", OrderDeadlines.AtField }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":shard", new AttributeValue { S = OrderDeadlines.Shard } },
                { ":due", new AttributeValue { S = dueBefore } }
            },
            Limit = limit,
            ScanIndexForward = true // oldest deadline first
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = (response.Items ?? new List<Dictionary<string, AttributeValue>>()).Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public Task<bool> TrySetDeadlineAsync(string orderId, string createdAt, int expectedVersion, OrderDeadline? deadline)
        => SetDeadlineCoreAsync(orderId, createdAt, expectedVersion, deadline);

    private async Task<bool> SetDeadlineCoreAsync(string orderId, string createdAt, int expectedVersion, OrderDeadline? deadline)
    {
        var setParts = new List<string>();
        var removeParts = new List<string>();
        var attrNames = new Dictionary<string, string> { { "#v", "version" } };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":v", new AttributeValue { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } }
        };
        AddDeadlineParts(deadline, setParts, removeParts, attrNames, attrValues);

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = OrderKey(orderId, createdAt),
                UpdateExpression = setParts.Count > 0
                    ? "SET " + string.Join(", ", setParts)
                    : "REMOVE " + string.Join(", ", removeParts),
                ConditionExpression = "attribute_exists(orderId) AND #v = :v",
                ExpressionAttributeNames = attrNames,
                ExpressionAttributeValues = attrValues
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>The row as it will be after a write of <paramref name="changes"/> (and <paramref name="status"/>).</summary>
    private static Dictionary<string, object> AfterWrite(
        Dictionary<string, AttributeValue> existing, IEnumerable<KeyValuePair<string, object>>? changes, string? status = null)
    {
        var row = DynamoDbHelper.FromItem(existing);
        if (changes != null)
        {
            foreach (var (key, value) in changes)
            {
                // The status is set by the caller's fields (UpdateAsync) or by status below.
                if (!IsProtectedOrderField(key) || key == "status")
                {
                    row[key] = value;
                }
            }
        }

        if (status != null)
        {
            row["status"] = status;
        }

        return row;
    }

    /// <summary>
    /// Adds the next deadline of <paramref name="after"/> to an update: the three attributes set, or
    /// removed when nothing is pending. Nothing is added when the row already carries it (a write
    /// that changes nothing due, or a row that never had a deadline, keeps its old expression). When
    /// the deadline cannot be derived nothing is added either, the row keeps what it has and
    /// <see cref="DeadlineStampFailedToken"/> is logged. <c>Added</c> is false only then.
    /// </summary>
    private static (bool Added, OrderDeadline? Next) AppendDeadline(
        Dictionary<string, object> after, List<string> setParts, List<string> removeParts,
        Dictionary<string, string> attrNames, Dictionary<string, AttributeValue> attrValues)
    {
        OrderDeadline? next;
        try
        {
            next = OrderDeadlines.Next(after);
        }
        catch (Exception ex)
        {
            LogStampFailed(after, ex);
            return (false, null);
        }

        if (!OrderDeadlines.IsStored(after, next))
        {
            AddDeadlineParts(next, setParts, removeParts, attrNames, attrValues);
        }

        return (true, next);
    }

    private static void AddDeadlineParts(
        OrderDeadline? next, List<string> setParts, List<string> removeParts,
        Dictionary<string, string> attrNames, Dictionary<string, AttributeValue> attrValues)
    {
        attrNames["#ndAt"] = OrderDeadlines.AtField;
        attrNames["#ndKind"] = OrderDeadlines.KindField;
        attrNames["#ndShard"] = OrderDeadlines.ShardField;
        if (next is { } due)
        {
            attrValues[":ndAt"] = new AttributeValue { S = due.AtIso };
            attrValues[":ndKind"] = new AttributeValue { S = due.Kind };
            attrValues[":ndShard"] = new AttributeValue { S = OrderDeadlines.Shard };
            setParts.Add("#ndAt = :ndAt");
            setParts.Add("#ndKind = :ndKind");
            setParts.Add("#ndShard = :ndShard");
        }
        else
        {
            removeParts.Add("#ndAt");
            removeParts.Add("#ndKind");
            removeParts.Add("#ndShard");
        }
    }

    private static string UpdateExpression(List<string> setParts, List<string> removeParts, string? addClause = null)
        => "SET " + string.Join(", ", setParts)
           + (removeParts.Count > 0 ? " REMOVE " + string.Join(", ", removeParts) : string.Empty)
           + (addClause != null ? " ADD " + addClause : string.Empty);

    /// <summary>
    /// After a write whose row is known: when the deadline attributes are not the ones its state gives
    /// (only a stale read before the write can cause it), write them, conditioned on the version
    /// written, and update <paramref name="row"/> to match. Never throws: a failure is logged and the
    /// repair walk fixes the row.
    /// </summary>
    private async Task EnsureDeadlineAsync(Dictionary<string, object> row)
    {
        try
        {
            var next = OrderDeadlines.Next(row);
            if (OrderDeadlines.IsStored(row, next)
                || !int.TryParse(Str(row, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            {
                return;
            }

            if (await SetDeadlineCoreAsync(Str(row, "orderId"), Str(row, "createdAt"), version, next))
            {
                ApplyDeadline(row, next);
            }
        }
        catch (Exception ex)
        {
            LogStampFailed(row, ex);
        }
    }

    private static void LogStampFailed(IReadOnlyDictionary<string, object> row, Exception ex)
        => Console.WriteLine(
            $"{DeadlineStampFailedToken} orderId={MoneyAlarmLog.LogId(Str(row, "orderId"))} reason=[{ex.GetType().Name}]");

    private static void ApplyDeadline(Dictionary<string, object> row, OrderDeadline? next)
    {
        if (next is { } due)
        {
            row[OrderDeadlines.AtField] = due.AtIso;
            row[OrderDeadlines.KindField] = due.Kind;
            row[OrderDeadlines.ShardField] = OrderDeadlines.Shard;
            return;
        }

        foreach (var field in OrderDeadlines.Fields)
        {
            row.Remove(field);
        }
    }

    private static void ApplyDeadline(Dictionary<string, AttributeValue> row, OrderDeadline? next)
    {
        if (next is { } due)
        {
            row[OrderDeadlines.AtField] = new AttributeValue { S = due.AtIso };
            row[OrderDeadlines.KindField] = new AttributeValue { S = due.Kind };
            row[OrderDeadlines.ShardField] = new AttributeValue { S = OrderDeadlines.Shard };
            return;
        }

        foreach (var field in OrderDeadlines.Fields)
        {
            row.Remove(field);
        }
    }

    private static string Str(IReadOnlyDictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryMoneySweepPageAsync(
        string status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 100)
        {
            limit = 100;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusCreatedAtIndex",
            KeyConditionExpression = "#status = :status",
            // A filter may not name the index's key (status), so the marker is compared with the
            // partition's own value, which every row of the page carries.
            FilterExpression = "attribute_not_exists(#skip) OR #skip <> :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#skip", OrderMoneyFields.SweepSkip }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } }
            },
            Limit = limit,
            ScanIndexForward = true // oldest first
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkMoneySweepSkipAsync(string orderId, string status, string markedAt)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return false;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = OrderKey(orderId, existing["createdAt"].S),
            UpdateExpression = "SET #skip = :status, #skipAt = :at",
            ConditionExpression = "attribute_exists(orderId) AND #status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#skip", OrderMoneyFields.SweepSkip },
                { "#skipAt", OrderMoneyFields.SweepSkipAt },
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } },
                { ":at", new AttributeValue { S = markedAt } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static Dictionary<string, AttributeValue> OrderKey(string orderId, string createdAt) => new()
    {
        { "orderId", new AttributeValue { S = orderId } },
        { "createdAt", new AttributeValue { S = createdAt } }
    };

    /// <inheritdoc />
    public async Task ReleaseDeliverySlotAsync(string orderId, string deliveryId)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
            },
            UpdateExpression = "REMOVE #slot, #claimedAt",
            ConditionExpression = "#slot = :did",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#slot", "activeDeliveryId" },
                { "#claimedAt", "activeDeliveryClaimedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":did", new AttributeValue { S = deliveryId } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
        }
        catch (ConditionalCheckFailedException)
        {
            // Another delivery holds the slot (or it is already free): nothing to release.
        }
    }

    /// <summary>
    /// Query for an item by orderId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string orderId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "orderId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = orderId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Query a GSI by partition key with optional pagination and an optional
    /// status filter.
    /// </summary>
    /// <remarks>
    /// When <paramref name="statusFilter"/> is supplied it is applied as a
    /// DynamoDB FilterExpression on the non-key <c>status</c> attribute (a
    /// reserved word, hence the <c>#status</c> alias). Note: a FilterExpression
    /// is applied AFTER <c>Limit</c>, so a single page may contain fewer than
    /// <c>limit</c> matching items even when more exist; callers can follow the
    /// returned nextToken. This is acceptable here because a single user's order
    /// set is small.
    /// </remarks>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByGsiAsync(
        string indexName,
        string partitionKeyName,
        string partitionKeyValue,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey,
        string? statusFilter = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = indexName,
            KeyConditionExpression = $"#{partitionKeyName} = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { $"#{partitionKeyName}", partitionKeyName }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = partitionKeyValue } }
            },
            ScanIndexForward = false, // newest first
            Limit = limit
        };

        // Apply the status filter unless status IS the partition key (the
        // status-only GSI already constrains it via the key condition).
        if (!string.IsNullOrEmpty(statusFilter) && partitionKeyName != "status")
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = statusFilter };
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }
}

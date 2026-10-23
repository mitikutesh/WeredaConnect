using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB escrow repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Escrows):
///   PK: escrowId (STRING)
///   SK: createdAt (STRING)
///   GSI DeliveryIndex: deliveryId (PK) + createdAt (SK)
///   GSI BuyerStatusIndex: buyerId (PK) + createdAt (SK)
///   GSI FarmerStatusIndex: farmerId (PK) + createdAt (SK)
///   GSI StatusExpiresIndex: status (PK) + expiresAt (SK)
///   GSI TruckerStatusIndex: truckerId (PK) + createdAt (SK), sparse
///   GSI OrderIndex: orderId (PK) + createdAt (SK), sparse
///   GSI ManualPayoutIndex: manualPayout (PK) + manualPayoutQueuedAt (SK), sparse (ESC-02)
/// </summary>
public sealed class EscrowRepository : IEscrowRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public EscrowRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task CreateAsync(Dictionary<string, object> escrow)
    {
        var item = new Dictionary<string, AttributeValue>();

        foreach (var kvp in escrow)
        {
            if (kvp.Value == null)
            {
                continue; // skip nulls
            }

            item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(escrowId)"
        };

        await _dynamoDb.PutItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string escrowId)
    {
        var item = await GetItemAsync(escrowId);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string escrowId, bool consistentRead)
    {
        var item = await GetItemAsync(escrowId, consistentRead);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(string escrowId, string createdAt, string newStatus, string? expectedCurrentStatus = null, Dictionary<string, object>? additionalFields = null)
    {
        var now = DateTime.UtcNow.ToString("o");

        var updateParts = new List<string> { "#status = :newStatus", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":newStatus", new AttributeValue { S = newStatus } },
            { ":updated", new AttributeValue { S = now } }
        };

        if (additionalFields != null)
        {
            var i = 0;
            foreach (var kvp in additionalFields)
            {
                if (kvp.Key is "escrowId" or "createdAt" or "status" or "updatedAt")
                {
                    continue;
                }

                // ESC-07: a dispute adds to the version below; a caller's read + 1 is never written.
                if (kvp.Value == null || (newStatus == "disputed" && kvp.Key == "version"))
                {
                    continue;
                }

                var nameAlias = $"#f{i}";
                var valueAlias = $":f{i}";
                attrNames[nameAlias] = kvp.Key;
                attrValues[valueAlias] = CropRepository.ToAttributeValue(kvp.Value);
                updateParts.Add($"{nameAlias} = {valueAlias}");
                i++;
            }
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET " + string.Join(", ", updateParts),
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues
        };

        // ESC-07: the dispute freeze is conditioned on the status, the marker and the lock, not on
        // the version, so the version is added to (a row without one gets 1), never set from a read.
        if (newStatus == "disputed")
        {
            request.UpdateExpression += " ADD #version :one";
            request.ExpressionAttributeNames["#version"] = "version";
            request.ExpressionAttributeValues[":one"] = new AttributeValue { N = "1" };
        }

        // ESC-10: a settled escrow is no longer blocked; its block reason goes with the settlement.
        if (newStatus is "released" or "refunded")
        {
            request.UpdateExpression += " REMOVE #blockReason, #blockReasonAt";
            request.ExpressionAttributeNames["#blockReason"] = SettlementBlockFields.Reason;
            request.ExpressionAttributeNames["#blockReasonAt"] = SettlementBlockFields.ReasonAt;
        }

        // Optimistic concurrency: verify expected current status before update
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(expectedCurrentStatus))
        {
            conditions.Add("#status = :expectedStatus");
            request.ExpressionAttributeValues[":expectedStatus"] = new AttributeValue { S = expectedCurrentStatus };
        }

        // ESC-02: neither a gateway settlement nor a dispute lands on an escrow waiting in the
        // manual payout queue; only TryCloseManualPayoutAsync settles those (and removes the marker).
        // ESC-04: nor does the move to completed, which a queued trucker leg must close first.
        if (newStatus is "released" or "refunded" or "disputed" or "completed")
        {
            conditions.Add("attribute_not_exists(#manualPayout)");
            request.ExpressionAttributeNames["#manualPayout"] = ManualPayoutFields.Marker;
        }

        // ESC-04: an escrow is completed only once every leg is paid; a pending trucker leg keeps
        // it released until the trucker retry or the manual payout queue records the leg.
        if (newStatus == "completed")
        {
            conditions.Add("attribute_not_exists(#truckerPending)");
            request.ExpressionAttributeNames["#truckerPending"] = TruckerPayoutFields.Pending;
        }

        // ESC-03: a dispute never lands on an escrow whose settlement is in flight, which would
        // leave a disputed row behind a held lock that no verdict can resolve.
        if (newStatus == "disputed")
        {
            conditions.Add("attribute_not_exists(#lock)");
            request.ExpressionAttributeNames["#lock"] = "settlementLock";
        }

        if (conditions.Count > 0)
        {
            request.ConditionExpression = string.Join(" AND ", conditions);
        }

        await _dynamoDb.UpdateItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryExtendExpiryAsync(string escrowId, string createdAt, string expiresAt)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #expiresAt = :expiresAt, #updated = :updated",
            ConditionExpression = "#status = :escrowed AND attribute_not_exists(#refundReason) "
                + "AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#expiresAt", "expiresAt" },
                { "#updated", "updatedAt" },
                { "#status", "status" },
                { "#refundReason", "refundReason" },
                { "#lock", SettlementLockFields.Lock },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":expiresAt", new AttributeValue { S = expiresAt } },
                { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
                { ":escrowed", new AttributeValue { S = "escrowed" } }
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

    /// <summary>
    /// Statuses a settlement may lock from (ESC-03). <c>dispute_resolved</c> settles the admin
    /// verdict; <c>disputed</c> is not here because a dispute settles only through a verdict.
    /// </summary>
    private static readonly HashSet<string> SettleableStatuses = new(StringComparer.Ordinal)
    {
        "escrowed", "dispute_resolved"
    };

    /// <inheritdoc />
    public async Task<bool> TryAcquireSettlementLockAsync(
        string escrowId, string createdAt, string op, string expectedStatus, bool allowHeld = false)
    {
        // FRD-03: held is settleable only for the admin exits, which pass allowHeld.
        if (!SettleableStatuses.Contains(expectedStatus) && !(allowHeld && expectedStatus == EscrowHoldFields.Status))
        {
            return false;
        }

        var now = DateTime.UtcNow.ToString("o");
        var lockId = Guid.NewGuid().ToString("N");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            // ESC-05: the lock time starts the recovery clock (SettlementLockFields.StaleAfter);
            // the lock id tells this lock from any other.
            UpdateExpression = "SET settlementLock = :op, #lockAt = :now, #lockId = :lockId, #updated = :now",
            // Acquire only if no settlement is already in progress AND the escrow is
            // still in the status the caller validated. This is the atomic guard that runs
            // BEFORE any Telebirr call, so only the winner ever moves funds.
            // ESC-02: never on an escrow waiting in the manual payout queue.
            // ESC-03: the exact status, not a set, so a stale read can never lock a row that
            // moved on (for example to a verdict the operation contradicts).
            ConditionExpression =
                "attribute_not_exists(settlementLock) AND attribute_not_exists(#manualPayout) "
                + "AND #status = :expected",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#lockAt", SettlementLockFields.LockAt },
                { "#lockId", SettlementLockFields.LockId },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":op", new AttributeValue { S = op } },
                { ":now", new AttributeValue { S = now } },
                { ":lockId", new AttributeValue { S = lockId } },
                { ":expected", new AttributeValue { S = expectedStatus } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // ESC-05: the SDK retries a write whose answer was lost, and the retry fails its
            // condition on our own lock. Only the random lock id we wrote tells our lock from
            // another one taken at the same instant.
            var latest = await GetItemAsync(escrowId, consistentRead: true);
            return latest != null
                && latest.TryGetValue(SettlementLockFields.Lock, out var heldOp) && heldOp.S == op
                && latest.TryGetValue(SettlementLockFields.LockId, out var heldId) && heldId.S == lockId;
        }
    }

    /// <inheritdoc />
    public async Task<EscrowHoldWrite> TryHoldAsync(
        string escrowId, string createdAt, EscrowHoldRequest hold, OrderFence? fence = null)
    {
        var names = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#reason", EscrowHoldFields.HoldReason },
            { "#heldAt", EscrowHoldFields.HeldAt },
            { "#heldBy", EscrowHoldFields.HeldBy },
            { "#heldFrom", EscrowHoldFields.HeldFromStatus },
            { "#heldFor", EscrowHoldFields.HeldFor },
            { "#updated", "updatedAt" },
            { "#version", "version" },
            { "#lock", SettlementLockFields.Lock },
            { "#manualPayout", ManualPayoutFields.Marker }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":held", new AttributeValue { S = EscrowHoldFields.Status } },
            { ":reason", new AttributeValue { S = hold.HoldReason } },
            { ":heldAt", new AttributeValue { S = hold.HeldAt } },
            { ":system", new AttributeValue { S = "system" } },
            { ":from", new AttributeValue { S = hold.FromStatus } },
            { ":for", new AttributeValue { S = hold.HeldFor } },
            { ":one", new AttributeValue { N = "1" } }
        };
        const string updateExpression = "SET #status = :held, #reason = :reason, #heldAt = :heldAt, #heldBy = :system, "
                                        + "#heldFrom = :from, #heldFor = :for, #updated = :heldAt ADD #version :one";
        // A settlement in progress (its lock) or waiting for an admin (the queue) wins over the hold.
        const string condition = "#status = :from AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)";

        var orderChanged = false;
        try
        {
            if (fence == null)
            {
                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = EscrowKey(escrowId, createdAt),
                    UpdateExpression = updateExpression,
                    ConditionExpression = condition,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                });
            }
            else
            {
                if (string.IsNullOrEmpty(fence.TableName))
                {
                    throw new ArgumentException("An order fence needs the orders table name.", nameof(fence));
                }

                await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems = new List<TransactWriteItem>
                    {
                        new()
                        {
                            Update = new Update
                            {
                                TableName = _tableName,
                                Key = EscrowKey(escrowId, createdAt),
                                UpdateExpression = updateExpression,
                                ConditionExpression = condition,
                                ExpressionAttributeNames = names,
                                ExpressionAttributeValues = values
                            }
                        },
                        new()
                        {
                            // The cancel-then-refund fence of the auto-refund sweep: a farmer's accept in
                            // between moves the version, and nothing is held.
                            ConditionCheck = new ConditionCheck
                            {
                                TableName = fence.TableName,
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    { "orderId", new AttributeValue { S = fence.OrderId } },
                                    { "createdAt", new AttributeValue { S = fence.CreatedAt } }
                                },
                                ConditionExpression = "#status = :status AND #v = :version",
                                ExpressionAttributeNames = new Dictionary<string, string>
                                {
                                    { "#status", "status" },
                                    { "#v", "version" }
                                },
                                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                                {
                                    { ":status", new AttributeValue { S = fence.Status } },
                                    { ":version", new AttributeValue { N = fence.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
                                }
                            }
                        }
                    }
                });
            }

            return EscrowHoldWrite.Held;
        }
        catch (ConditionalCheckFailedException)
        {
            // The plain write lost its condition; the re-read below decides.
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order: 0 is the escrow, 1 the order fence.
            orderChanged = IsConditionFailure(ex.CancellationReasons, 1);
            if (!orderChanged && !IsConditionFailure(ex.CancellationReasons, 0))
            {
                throw; // a conflict or throttle, not a condition: the caller logs it, the next run retries
            }
        }

        // The SDK retries a write whose answer was lost, and the retry fails its condition on our own
        // hold: only the heldAt we wrote tells it from another hold.
        var latest = await GetItemAsync(escrowId, consistentRead: true);
        if (latest != null
            && latest.TryGetValue("status", out var status) && status.S == EscrowHoldFields.Status
            && latest.TryGetValue(EscrowHoldFields.HeldAt, out var heldAt) && heldAt.S == hold.HeldAt)
        {
            return EscrowHoldWrite.Held;
        }

        return orderChanged ? EscrowHoldWrite.OrderChanged : EscrowHoldWrite.EscrowChanged;
    }

    private static bool IsConditionFailure(List<CancellationReason>? reasons, int index) =>
        reasons != null && reasons.Count > index && reasons[index]?.Code == "ConditionalCheckFailed";

    /// <inheritdoc />
    public async Task ReleaseSettlementLockAsync(string escrowId, string createdAt)
    {
        var now = DateTime.UtcNow.ToString("o");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "REMOVE settlementLock, #lockAt, #lockId SET #updated = :now",
            // ESC-05: a queued payout keeps its lock, even when the caller missed that its
            // queue write committed.
            ConditionExpression = "attribute_exists(escrowId) AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#updated", "updatedAt" },
                { "#lockAt", SettlementLockFields.LockAt },
                { "#lockId", SettlementLockFields.LockId },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":now", new AttributeValue { S = now } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
        }
        catch (ConditionalCheckFailedException)
        {
            // Queued (the queue owns the lock now) or gone: nothing to release.
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt)
    {
        var request = BuildQueueRequest(escrowId, createdAt, op, reason, fromStatus, legs, queuedAt);
        // Only the settlement that holds the lock may queue, and only once. The lock
        // stays in place on purpose: a queued escrow must not be settled by anyone else.
        request.ExpressionAttributeNames["#lock"] = SettlementLockFields.Lock;
        request.ConditionExpression = "#lock = :op AND attribute_not_exists(#marker)";
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryQueueStaleSettlementAsync(
        string escrowId, string createdAt, string op, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt, string? lockId, string parkId)
    {
        var request = BuildQueueRequest(
            escrowId, createdAt, op, ManualPayoutFields.ReasonGatewayOutcomeUnknown, fromStatus, legs, queuedAt);
        request.UpdateExpression = request.UpdateExpression.Replace("SET ", "SET #parkId = :parkId, ");
        request.ExpressionAttributeNames["#parkId"] = ManualPayoutFields.ParkId;
        request.ExpressionAttributeValues[":parkId"] = new AttributeValue { S = parkId };
        // The same lock the recovery step read, and still the status it read: a lock released and
        // taken again in between (a new lock id) is someone else's settlement.
        request.ExpressionAttributeNames["#lock"] = SettlementLockFields.Lock;
        request.ExpressionAttributeNames["#lockId"] = SettlementLockFields.LockId;
        request.ExpressionAttributeNames["#status"] = "status";
        var lockCondition = "attribute_not_exists(#lockId)";
        if (lockId != null)
        {
            request.ExpressionAttributeValues[":lockId"] = new AttributeValue { S = lockId };
            lockCondition = "#lockId = :lockId";
        }

        request.ConditionExpression =
            $"#lock = :op AND attribute_not_exists(#marker) AND #status = :fromStatus AND {lockCondition}";
        return await TryConditionalUpdateAsync(request);
    }

    private async Task<bool> TryConditionalUpdateAsync(UpdateItemRequest request)
    {
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

    /// <summary>
    /// The queue write without its condition (ESC-02, shared with the ESC-05 recovery step and the
    /// ESC-04 trucker leg). It registers only the names its own expression uses: DynamoDB refuses an
    /// unused name, so each caller adds the names of its condition.
    /// </summary>
    private UpdateItemRequest BuildQueueRequest(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt)
    {
        var now = queuedAt;
        var sets = new List<string>
        {
            "#marker = :open", "#queuedAt = :now", "#op = :op", "#reason = :reason",
            "#fromStatus = :fromStatus", "#updated = :now"
        };
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#queuedAt", ManualPayoutFields.QueuedAt },
            { "#op", ManualPayoutFields.Op },
            { "#reason", ManualPayoutFields.Reason },
            { "#fromStatus", ManualPayoutFields.FromStatus },
            { "#updated", "updatedAt" },
            { "#version", "version" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":now", new AttributeValue { S = now } },
            { ":op", new AttributeValue { S = op } },
            { ":reason", new AttributeValue { S = reason } },
            { ":fromStatus", new AttributeValue { S = fromStatus } },
            { ":one", new AttributeValue { N = "1" } }
        };

        var i = 0;
        foreach (var leg in legs)
        {
            names[$"#ls{i}"] = ManualPayoutFields.StatusAttr(leg.Leg);
            values[$":ls{i}"] = new AttributeValue { S = leg.Status };
            sets.Add($"#ls{i} = :ls{i}");
            if (!string.IsNullOrEmpty(leg.Ref))
            {
                names[$"#lr{i}"] = ManualPayoutFields.RefAttr(leg.Leg);
                values[$":lr{i}"] = new AttributeValue { S = leg.Ref };
                sets.Add($"#lr{i} = :lr{i}");
            }

            if (!string.IsNullOrEmpty(leg.PaidBy))
            {
                names[$"#lb{i}"] = ManualPayoutFields.PaidByAttr(leg.Leg);
                values[$":lb{i}"] = new AttributeValue { S = leg.PaidBy };
                sets.Add($"#lb{i} = :lb{i}");
            }

            if (!string.IsNullOrEmpty(leg.PaidAt))
            {
                names[$"#la{i}"] = ManualPayoutFields.PaidAtAttr(leg.Leg);
                values[$":la{i}"] = new AttributeValue { S = leg.PaidAt };
                sets.Add($"#la{i} = :la{i}");
            }

            i++;
        }

        return new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET " + string.Join(", ", sets) + " ADD #version :one",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkManualLegPaidAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, string? otherLegMustBePending)
    {
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#op", ManualPayoutFields.Op },
            { "#legStatus", ManualPayoutFields.StatusAttr(leg) },
            { "#legRef", ManualPayoutFields.RefAttr(leg) },
            { "#legPaidBy", ManualPayoutFields.PaidByAttr(leg) },
            { "#legPaidAt", ManualPayoutFields.PaidAtAttr(leg) },
            { "#updated", "updatedAt" },
            { "#version", "version" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":op", new AttributeValue { S = op } },
            { ":pending", new AttributeValue { S = ManualPayoutFields.LegPending } },
            { ":paid", new AttributeValue { S = ManualPayoutFields.LegPaid } },
            { ":ref", new AttributeValue { S = externalRef } },
            { ":paidBy", new AttributeValue { S = paidBy } },
            { ":paidAt", new AttributeValue { S = paidAt } },
            { ":one", new AttributeValue { N = "1" } }
        };
        var condition = "#marker = :open AND #op = :op AND #legStatus = :pending";
        if (!string.IsNullOrEmpty(otherLegMustBePending))
        {
            names["#otherStatus"] = ManualPayoutFields.StatusAttr(otherLegMustBePending);
            condition += " AND #otherStatus = :pending";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression =
                "SET #legStatus = :paid, #legRef = :ref, #legPaidBy = :paidBy, #legPaidAt = :paidAt, #updated = :paidAt "
                + "ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
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
    public async Task<bool> TryCloseManualPayoutAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, IReadOnlyList<string> otherLegsMustBePaid,
        string newStatus, Dictionary<string, object> settlementFields)
    {
        var sets = new List<string>
        {
            "#legStatus = :paid", "#legRef = :ref", "#legPaidBy = :paidBy", "#legPaidAt = :paidAt",
            "#status = :newStatus", "#updated = :paidAt"
        };
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#op", ManualPayoutFields.Op },
            { "#legStatus", ManualPayoutFields.StatusAttr(leg) },
            { "#legRef", ManualPayoutFields.RefAttr(leg) },
            { "#legPaidBy", ManualPayoutFields.PaidByAttr(leg) },
            { "#legPaidAt", ManualPayoutFields.PaidAtAttr(leg) },
            { "#status", "status" },
            { "#updated", "updatedAt" },
            { "#version", "version" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":op", new AttributeValue { S = op } },
            { ":pending", new AttributeValue { S = ManualPayoutFields.LegPending } },
            { ":paid", new AttributeValue { S = ManualPayoutFields.LegPaid } },
            { ":ref", new AttributeValue { S = externalRef } },
            { ":paidBy", new AttributeValue { S = paidBy } },
            { ":paidAt", new AttributeValue { S = paidAt } },
            { ":newStatus", new AttributeValue { S = newStatus } },
            { ":escrowed", new AttributeValue { S = "escrowed" } },
            { ":disputed", new AttributeValue { S = "disputed" } },
            { ":disputeResolved", new AttributeValue { S = "dispute_resolved" } },
            { ":held", new AttributeValue { S = EscrowHoldFields.Status } },
            { ":one", new AttributeValue { N = "1" } }
        };

        // ESC-04: a trucker leg queued on a released escrow closes from released, and only when
        // it was queued from there (the farmer leg was paid by the release itself). FRD-03: an admin
        // release or refund of a held escrow that the gateway queued closes from held.
        names["#fromStatus"] = ManualPayoutFields.FromStatus;
        values[":released"] = new AttributeValue { S = "released" };
        var condition = "#marker = :open AND #op = :op AND #legStatus = :pending"
                        + " AND (#status = :escrowed OR #status = :disputed OR #status = :disputeResolved OR #status = :held"
                        + " OR (#status = :released AND #fromStatus = :released))";
        var i = 0;
        foreach (var other in otherLegsMustBePaid)
        {
            names[$"#other{i}"] = ManualPayoutFields.StatusAttr(other);
            condition += $" AND #other{i} = :paid";
            i++;
        }

        i = 0;
        foreach (var kvp in settlementFields)
        {
            if (kvp.Value == null || kvp.Key is "escrowId" or "createdAt" or "status" or "updatedAt" or "version")
            {
                continue;
            }

            names[$"#f{i}"] = kvp.Key;
            values[$":f{i}"] = CropRepository.ToAttributeValue(kvp.Value);
            sets.Add($"#f{i} = :f{i}");
            i++;
        }

        // ESC-04: once a release closes, no trucker leg is owed any more. ESC-10: nor is the
        // settlement blocked.
        names["#blockReason"] = SettlementBlockFields.Reason;
        names["#blockReasonAt"] = SettlementBlockFields.ReasonAt;
        var removes = "#marker, #blockReason, #blockReasonAt";
        if (op == ManualPayoutFields.OpRelease)
        {
            names["#tPending"] = TruckerPayoutFields.Pending;
            names["#tBlock"] = TruckerPayoutFields.BlockReason;
            names["#tBlockAt"] = TruckerPayoutFields.BlockReasonAt;
            names["#tLockId"] = TruckerPayoutFields.LockId;
            names["#tLockAt"] = TruckerPayoutFields.LockAt;
            removes += ", #tPending, #tBlock, #tBlockAt, #tLockId, #tLockAt";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET " + string.Join(", ", sets) + " REMOVE " + removes + " ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
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
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOpenManualPayoutsAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = ManualPayoutFields.IndexName,
            KeyConditionExpression = "#marker = :open",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#marker", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } }
            },
            Limit = limit,
            ScanIndexForward = true
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    private static Dictionary<string, AttributeValue> EscrowKey(string escrowId, string createdAt) => new()
    {
        { "escrowId", new AttributeValue { S = escrowId } },
        { "createdAt", new AttributeValue { S = createdAt } }
    };

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByDeliveryIdAsync(string deliveryId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "DeliveryIndex",
            KeyConditionExpression = "#deliveryId = :deliveryId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#deliveryId", "deliveryId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":deliveryId", new AttributeValue { S = deliveryId } }
            },
            Limit = 1,
            ScanIndexForward = false
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? CropRepository.FromItem(response.Items[0]) : null;
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByOrderIdAsync(string orderId, int limit = 5)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "OrderIndex",
            KeyConditionExpression = "#orderId = :orderId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#orderId", "orderId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":orderId", new AttributeValue { S = orderId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Select(CropRepository.FromItem).ToList();
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByBuyerAsync(
        string buyerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "BuyerStatusIndex",
            KeyConditionExpression = "#buyerId = :buyerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#buyerId", "buyerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":buyerId", new AttributeValue { S = buyerId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        if (!string.IsNullOrEmpty(status))
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "FarmerStatusIndex",
            KeyConditionExpression = "#farmerId = :farmerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#farmerId", "farmerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":farmerId", new AttributeValue { S = farmerId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        if (!string.IsNullOrEmpty(status))
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TruckerStatusIndex",
            KeyConditionExpression = "#truckerId = :truckerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#truckerId", "truckerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":truckerId", new AttributeValue { S = truckerId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        if (!string.IsNullOrEmpty(status))
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100)
    {
        if (limit < 1 || limit > 500)
        {
            limit = 100;
        }

        var (request, filters) = CronQuery(status, expiresAtBefore, limit);

        // Post-query filter for deliveryConfirmedBefore
        if (!string.IsNullOrEmpty(deliveryConfirmedBefore))
        {
            filters.Add("#confirmedAt <= :confirmedBefore");
            request.ExpressionAttributeNames["#confirmedAt"] = "deliveryConfirmedAt";
            request.ExpressionAttributeValues[":confirmedBefore"] = new AttributeValue { S = deliveryConfirmedBefore };
        }

        request.FilterExpression = string.Join(" AND ", filters);

        var allItems = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;

        // Paginate through all results up to the limit. A filtered page can come back empty
        // with a LastEvaluatedKey, so paging continues until the limit or the end of the index.
        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            allItems.AddRange(response.Items.Select(CropRepository.FromItem));
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null && allItems.Count < limit);

        return allItems.Take(limit).ToList();
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCronPageAsync(
        CronSweepQuery query, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var pageSize = query.PageSize is < 1 or > 500 ? 100 : query.PageSize;
        var (request, filters) = CronQuery(query.Status, query.ExpiresAtBefore, pageSize);

        // ESC-10: skips that need no read move into the filter, so they never reach the sweep.
        if (query.DeliveryConfirmed is { } confirmed)
        {
            filters.Add(confirmed ? "attribute_exists(#confirmed)" : "attribute_not_exists(#confirmed)");
            request.ExpressionAttributeNames["#confirmed"] = "deliveryConfirmedAt";
        }

        if (query.ExcludeLocked)
        {
            filters.Add("attribute_not_exists(#lock)");
            request.ExpressionAttributeNames["#lock"] = SettlementLockFields.Lock;
        }

        request.FilterExpression = string.Join(" AND ", filters);
        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <summary>
    /// The status query both cron reads share: <c>StatusExpiresIndex</c>, oldest <c>expiresAt</c> first,
    /// an optional <c>expiresAt</c> bound, and the filters that keep rows other owners hold out of every
    /// sweep. Returns the filter terms for the caller to extend and join.
    /// </summary>
    private (QueryRequest Request, List<string> Filters) CronQuery(string status, string? expiresAtBefore, int limit)
    {
        var keyCondition = "#status = :status";
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":status", new AttributeValue { S = status } }
        };

        // Use expiresAt sort key condition on the GSI when provided
        if (!string.IsNullOrEmpty(expiresAtBefore))
        {
            keyCondition += " AND #expiresAt <= :expiresAtBefore";
            attrNames["#expiresAt"] = "expiresAt";
            attrValues[":expiresAtBefore"] = new AttributeValue { S = expiresAtBefore };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusExpiresIndex",
            KeyConditionExpression = keyCondition,
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            Limit = limit,
            ScanIndexForward = true
        };

        // ESC-02: escrows waiting in the manual payout queue keep their status and expiresAt,
        // so they would sit at the head of this oldest-first window and starve every newer
        // escrow. The sweeps have nothing to do with them; the admin queue owns them.
        // ESC-04: a released escrow whose trucker leg is still owed belongs to the trucker retry;
        // the crash recovery must not complete it, and it must not fill the window either.
        var filters = new List<string> { "attribute_not_exists(#manualPayout)", "attribute_not_exists(#truckerPending)" };
        request.ExpressionAttributeNames["#manualPayout"] = ManualPayoutFields.Marker;
        request.ExpressionAttributeNames["#truckerPending"] = TruckerPayoutFields.Pending;

        // ESC-06: an order escrow held to its 21-day cap keeps that expiresAt and its order waits in
        // the admin queue, which owns it; it would sit at the head of both sweep windows for good.
        // It comes back once a refund is scheduled or its delivery is confirmed. Escrowed rows only:
        // a capped escrow that later moved on (disputed, released) must stay visible to its sweep.
        if (status == "escrowed")
        {
            filters.Add("(attribute_not_exists(#holdCap) OR attribute_exists(#capRefund) OR attribute_exists(#capConfirmed))");
            request.ExpressionAttributeNames["#holdCap"] = AdminQueueFields.HoldCapReachedAt;
            request.ExpressionAttributeNames["#capRefund"] = "refundReason";
            request.ExpressionAttributeNames["#capConfirmed"] = "deliveryConfirmedAt";
        }

        return (request, filters);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryStaleSettlementLocksAsync(
        string status, string lockedBefore, int limit = 50)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusExpiresIndex",
            KeyConditionExpression = "#status = :status",
            // A lock taken before the cutoff, or (a lock from before ESC-05, without a lock
            // time) a row last written before it. Queued rows keep their lock on purpose.
            FilterExpression =
                "attribute_exists(#lock) AND attribute_not_exists(#manualPayout) "
                + "AND (#lockAt <= :before OR (attribute_not_exists(#lockAt) AND #updated <= :before))",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#lock", SettlementLockFields.Lock },
                { "#lockAt", SettlementLockFields.LockAt },
                { "#updated", "updatedAt" },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } },
                { ":before", new AttributeValue { S = lockedBefore } }
            }
        };

        // The filter drops almost every row of the partition, so pages often come back empty
        // with a LastEvaluatedKey: keep paging to the limit or the end of the partition.
        var items = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            items.AddRange(response.Items.Select(CropRepository.FromItem));
            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null && items.Count < limit);

        return items.Take(limit).ToList();
    }

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> QueryPendingTruckerLegsAsync(int limit)
        => QueryTruckerLegsAsync("released", limit);

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> QueryLegacyTruckerLegsAsync(int limit)
        => QueryTruckerLegsAsync("completed", limit);

    /// <summary>
    /// ESC-04: rows of one status whose trucker leg is pending and not queued, oldest
    /// <c>expiresAt</c> first. The filter drops most rows of the partition, so paging goes on
    /// through empty pages to the limit or the end of the partition.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> QueryTruckerLegsAsync(string status, int limit)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusExpiresIndex",
            KeyConditionExpression = "#status = :status",
            FilterExpression = "#pending = :true AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#pending", TruckerPayoutFields.Pending },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } },
                { ":true", new AttributeValue { BOOL = true } }
            },
            ScanIndexForward = true
        };

        var items = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            items.AddRange(response.Items.Select(CropRepository.FromItem));
            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null && items.Count < limit);

        return items.Take(limit).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> TrySetSettlementBlockReasonAsync(
        string escrowId, string createdAt, string expectedStatus, string reason, string reasonAt)
    {
        // Bookkeeping only: neither the version nor updatedAt moves.
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #reason = :reason, #reasonAt = :reasonAt",
            ConditionExpression =
                "(attribute_not_exists(#reason) OR #reason <> :reason) AND #status = :expected "
                + "AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#reason", SettlementBlockFields.Reason },
                { "#reasonAt", SettlementBlockFields.ReasonAt },
                { "#status", "status" },
                { "#lock", SettlementLockFields.Lock },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":reason", new AttributeValue { S = reason } },
                { ":reasonAt", new AttributeValue { S = reasonAt } },
                { ":expected", new AttributeValue { S = expectedStatus } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryClearSettlementBlockReasonAsync(string escrowId, string createdAt, string storedReason)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "REMOVE #reason, #reasonAt",
            ConditionExpression = "#reason = :stored",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#reason", SettlementBlockFields.Reason },
                { "#reasonAt", SettlementBlockFields.ReasonAt }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":stored", new AttributeValue { S = storedReason } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TrySetTruckerBlockReasonAsync(string escrowId, string createdAt, string reason, string reasonAt)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #reason = :reason, #reasonAt = :reasonAt, #updated = :reasonAt",
            ConditionExpression =
                "(attribute_not_exists(#reason) OR #reason <> :reason) AND #status = :released AND #pending = :true "
                + "AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lockId)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#reason", TruckerPayoutFields.BlockReason },
                { "#reasonAt", TruckerPayoutFields.BlockReasonAt },
                { "#updated", "updatedAt" },
                { "#status", "status" },
                { "#pending", TruckerPayoutFields.Pending },
                { "#manualPayout", ManualPayoutFields.Marker },
                { "#lockId", TruckerPayoutFields.LockId }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":reason", new AttributeValue { S = reason } },
                { ":reasonAt", new AttributeValue { S = reasonAt } },
                { ":released", new AttributeValue { S = "released" } },
                { ":true", new AttributeValue { BOOL = true } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryBackfillTruckerIdAsync(string escrowId, string createdAt, string truckerId)
    {
        // truckerId keys the TruckerStatusIndex, so it is never stored empty: absent means none.
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #truckerId = :truckerId, #updated = :now",
            ConditionExpression = "attribute_exists(escrowId) AND attribute_not_exists(#truckerId)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#truckerId", "truckerId" },
                { "#updated", "updatedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":truckerId", new AttributeValue { S = truckerId } },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<string?> TryAcquireTruckerPayoutLockAsync(string escrowId, string createdAt)
    {
        var lockId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("o");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET #lockId = :lockId, #lockAt = :now, #updated = :now REMOVE #reason, #reasonAt",
            ConditionExpression =
                "#status = :released AND #pending = :true AND attribute_not_exists(#lockId) "
                + "AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#lockId", TruckerPayoutFields.LockId },
                { "#lockAt", TruckerPayoutFields.LockAt },
                { "#updated", "updatedAt" },
                { "#reason", TruckerPayoutFields.BlockReason },
                { "#reasonAt", TruckerPayoutFields.BlockReasonAt },
                { "#status", "status" },
                { "#pending", TruckerPayoutFields.Pending },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":lockId", new AttributeValue { S = lockId } },
                { ":now", new AttributeValue { S = now } },
                { ":released", new AttributeValue { S = "released" } },
                { ":true", new AttributeValue { BOOL = true } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return lockId;
        }
        catch (ConditionalCheckFailedException)
        {
            // As for the settlement lock (ESC-05): the SDK retries a write whose answer was lost,
            // and only the random id we wrote tells our lock from another one.
            var latest = await GetItemAsync(escrowId, consistentRead: true);
            return latest != null
                   && latest.TryGetValue(TruckerPayoutFields.LockId, out var held) && held.S == lockId
                ? lockId
                : null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseTruckerPayoutLockAsync(string escrowId, string createdAt, string lockId)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "REMOVE #lockId, #lockAt SET #updated = :now",
            // A queued leg keeps its lock, even when the caller missed that its queue write committed.
            ConditionExpression = "#lockId = :lockId AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#lockId", TruckerPayoutFields.LockId },
                { "#lockAt", TruckerPayoutFields.LockAt },
                { "#updated", "updatedAt" },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":lockId", new AttributeValue { S = lockId } },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordTruckerPayoutAsync(
        string escrowId, string createdAt, string lockId, string truckerRef, string paidAt, string completedAt)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression =
                "SET #status = :completed, #ref = :ref, #paidAt = :paidAt, #completedAt = :completedAt, "
                + "#updated = :completedAt REMOVE #pending, #reason, #reasonAt, #lockId, #lockAt ADD #version :one",
            ConditionExpression = "#status = :released AND #lockId = :lockId AND attribute_not_exists(#manualPayout)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#ref", TruckerPayoutFields.Ref },
                { "#paidAt", TruckerPayoutFields.PaidAt },
                { "#completedAt", "completedAt" },
                { "#updated", "updatedAt" },
                { "#pending", TruckerPayoutFields.Pending },
                { "#reason", TruckerPayoutFields.BlockReason },
                { "#reasonAt", TruckerPayoutFields.BlockReasonAt },
                { "#lockId", TruckerPayoutFields.LockId },
                { "#lockAt", TruckerPayoutFields.LockAt },
                { "#version", "version" },
                { "#manualPayout", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":completed", new AttributeValue { S = "completed" } },
                { ":released", new AttributeValue { S = "released" } },
                { ":ref", new AttributeValue { S = truckerRef } },
                { ":paidAt", new AttributeValue { S = paidAt } },
                { ":completedAt", new AttributeValue { S = completedAt } },
                { ":lockId", new AttributeValue { S = lockId } },
                { ":one", new AttributeValue { N = "1" } }
            }
        };
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryQueueTruckerLegAsync(
        string escrowId, string createdAt, string reason, IReadOnlyList<ManualPayoutLegSeed> legs,
        string queuedAt, string lockId, string? parkId)
    {
        var request = BuildQueueRequest(escrowId, createdAt, ManualPayoutFields.OpRelease, reason, "released", legs, queuedAt);
        if (parkId != null)
        {
            request.UpdateExpression = request.UpdateExpression.Replace("SET ", "SET #parkId = :parkId, ");
            request.ExpressionAttributeNames["#parkId"] = ManualPayoutFields.ParkId;
            request.ExpressionAttributeValues[":parkId"] = new AttributeValue { S = parkId };
        }

        // Only the holder of the trucker lock queues, and only once. Not the release's
        // settlementLock: a row from before the lock existed has none.
        request.ExpressionAttributeNames["#status"] = "status";
        request.ExpressionAttributeNames["#lockId"] = TruckerPayoutFields.LockId;
        request.ExpressionAttributeValues[":lockId"] = new AttributeValue { S = lockId };
        request.ConditionExpression = "#status = :fromStatus AND #lockId = :lockId AND attribute_not_exists(#marker)";
        return await TryConditionalUpdateAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryParkLegacyTruckerLegAsync(
        string escrowId, string createdAt, string currentStatus, IReadOnlyList<ManualPayoutLegSeed> legs,
        string queuedAt, string pendingSince)
    {
        var request = BuildQueueRequest(
            escrowId, createdAt, ManualPayoutFields.OpRelease, ManualPayoutFields.ReasonGatewayOutcomeUnknown,
            "released", legs, queuedAt);
        request.UpdateExpression = request.UpdateExpression.Replace("SET ", "SET #status = :fromStatus, #pendingSince = :pendingSince, ")
                                   + " REMOVE #completedAt";
        request.ExpressionAttributeNames["#status"] = "status";
        request.ExpressionAttributeNames["#pendingSince"] = TruckerPayoutFields.PendingSince;
        request.ExpressionAttributeNames["#completedAt"] = "completedAt";
        request.ExpressionAttributeNames["#pending"] = TruckerPayoutFields.Pending;
        request.ExpressionAttributeNames["#tLockId"] = TruckerPayoutFields.LockId;
        request.ExpressionAttributeValues[":pendingSince"] = new AttributeValue { S = pendingSince };
        request.ExpressionAttributeValues[":current"] = new AttributeValue { S = currentStatus };
        request.ExpressionAttributeValues[":true"] = new AttributeValue { BOOL = true };
        // Legacy only: ESC-04 writes a pending time with every pending flag and takes its own lock.
        request.ConditionExpression =
            "#status = :current AND #pending = :true AND attribute_not_exists(#pendingSince) "
            + "AND attribute_not_exists(#tLockId) AND attribute_not_exists(#marker)";
        return await TryConditionalUpdateAsync(request);
    }

    /// <summary>
    /// Query for an item by escrowId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string escrowId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "escrowId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = escrowId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}

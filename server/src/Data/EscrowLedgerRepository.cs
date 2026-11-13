using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-20: the escrow ledger on DynamoDB, low-level API for AOT (manual marshalling, no reflection).
///
/// Table (EscrowLedger):
///   PK: escrowId (STRING)
///   SK: ref (STRING)
///   GSI LedgerOpenIndex: openStatus (PK) + dueAt (SK), sparse (rows that are not done), projection ALL
///
/// The escrow update of a decision and of a row that becomes <c>done</c> runs in the same transaction as
/// the row write and its escrow-audit rows, conditioned on the escrow version read.
/// </summary>
internal sealed class EscrowLedgerRepository : IEscrowLedgerRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _ledgerTable;
    private readonly string _escrowsTable;
    private readonly string _auditTable;

    public EscrowLedgerRepository(IAmazonDynamoDB dynamoDb, string ledgerTable, string escrowsTable, string auditTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _ledgerTable = string.IsNullOrEmpty(ledgerTable) ? throw new ArgumentNullException(nameof(ledgerTable)) : ledgerTable;
        _escrowsTable = string.IsNullOrEmpty(escrowsTable) ? throw new ArgumentNullException(nameof(escrowsTable)) : escrowsTable;
        _auditTable = string.IsNullOrEmpty(auditTable) ? throw new ArgumentNullException(nameof(auditTable)) : auditTable;
    }

    /// <inheritdoc />
    public async Task<List<LedgerRow>> GetRowsAsync(string escrowId)
    {
        var rows = new List<LedgerRow>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _ledgerTable,
                KeyConditionExpression = "#id = :id",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = LedgerFields.EscrowId },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = S(escrowId) },
                ConsistentRead = true,
                ExclusiveStartKey = startKey
            });
            rows.AddRange(response.Items.Select(FromItem));
            startKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        } while (startKey != null);

        return rows;
    }

    /// <inheritdoc />
    public async Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> GetRowsPageAsync(
        string escrowId, int limit, Dictionary<string, AttributeValue>? startKey)
    {
        var request = new QueryRequest
        {
            TableName = _ledgerTable,
            KeyConditionExpression = "#id = :id",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = LedgerFields.EscrowId },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = S(escrowId) },
            ConsistentRead = true,
            Limit = Math.Clamp(limit, 1, 100)
        };
        if (startKey != null)
        {
            request.ExclusiveStartKey = startKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var rows = (response.Items ?? new List<Dictionary<string, AttributeValue>>()).Select(FromItem).ToList();
        return (rows, response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<LedgerRow?> GetRowAsync(string escrowId, string reference)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _ledgerTable,
            Key = RowKey(escrowId, reference),
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 } item ? FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<LedgerWrite> DecideAsync(LedgerDecision decision)
    {
        var items = new List<TransactWriteItem>();
        foreach (var row in decision.Rows)
        {
            items.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = _ledgerTable,
                    Item = ToItem(row),
                    ConditionExpression = "attribute_not_exists(#id)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = LedgerFields.EscrowId }
                }
            });
        }

        var escrowIndex = items.Count;
        items.Add(new TransactWriteItem { Update = BuildDecisionUpdate(decision) });
        items.AddRange(decision.Audits.Select(AuditPut));

        return await TransactAsync(items, rowIndexes: Enumerable.Range(0, decision.Rows.Count).ToHashSet(),
            escrowIndex, rowConflict: LedgerWrite.RowExists);
    }

    /// <inheritdoc />
    public async Task<LedgerWrite> PutRowAsync(LedgerRow row, LedgerAudit audit)
    {
        var items = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = _ledgerTable,
                    Item = ToItem(row),
                    ConditionExpression = "attribute_not_exists(#id)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = LedgerFields.EscrowId }
                }
            },
            AuditPut(audit)
        };
        return await TransactAsync(items, rowIndexes: new HashSet<int> { 0 }, escrowIndex: -1, rowConflict: LedgerWrite.RowExists);
    }

    /// <inheritdoc />
    public async Task<bool> TryStampCallingAsync(LedgerRow row, string now, string dueAt)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _ledgerTable,
                Key = RowKey(row.EscrowId, row.Ref),
                UpdateExpression = "SET #calling = :now, #due = :due, #updated = :now ADD #version :one",
                ConditionExpression = "#version = :v AND (#status = :pending OR #status = :failed) AND attribute_not_exists(#calling)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#calling"] = LedgerFields.CallingSince,
                    ["#due"] = LedgerFields.DueAt,
                    ["#updated"] = LedgerFields.UpdatedAt,
                    ["#version"] = LedgerFields.Version,
                    ["#status"] = LedgerFields.Status
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":now"] = S(now),
                    [":due"] = S(dueAt),
                    [":one"] = N(1),
                    [":v"] = N(row.Version),
                    [":pending"] = S(LedgerFields.StatusPending),
                    [":failed"] = S(LedgerFields.StatusFailed)
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
    public async Task<LedgerWrite> TransitionAsync(LedgerTransition transition)
    {
        var items = new List<TransactWriteItem>
        {
            new() { Update = BuildRowUpdate(transition.Before, transition.After, transition.ManualRecord) }
        };
        var escrowIndex = -1;
        if (transition.Escrow is { } escrow)
        {
            escrowIndex = items.Count;
            items.Add(new TransactWriteItem { Update = BuildEscrowStatusUpdate(escrow, transition.EscrowStatus, transition.EscrowFields) });
        }

        items.AddRange(transition.Audits.Select(AuditPut));
        var referenceIndex = -1;
        if (transition.ManualRecord?.Reference is { } reference)
        {
            referenceIndex = items.Count;
            items.Add(PayoutReferenceFields.BuildPut(reference));
        }

        return await TransactAsync(items, rowIndexes: new HashSet<int> { 0 }, escrowIndex, rowConflict: LedgerWrite.RowConflict,
            referenceIndex);
    }

    /// <inheritdoc />
    public async Task<(List<LedgerRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryOpenAsync(
        string openStatus, string dueBefore, int limit, Dictionary<string, AttributeValue>? startKey)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _ledgerTable,
            IndexName = LedgerFields.IndexName,
            KeyConditionExpression = "#open = :open AND #due <= :due",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#open"] = LedgerFields.OpenStatus,
                ["#due"] = LedgerFields.DueAt
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":open"] = S(openStatus),
                [":due"] = S(dueBefore)
            },
            ScanIndexForward = true,
            Limit = Math.Clamp(limit, 1, 500),
            ExclusiveStartKey = startKey
        });
        return (response.Items.Select(FromItem).ToList(),
            response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkManualOverdueLoggedAsync(string escrowId, string reference, string dueAt)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _ledgerTable,
                Key = RowKey(escrowId, reference),
                UpdateExpression = "SET #logged = :due",
                ConditionExpression = "#status = :manual AND #due = :due AND (attribute_not_exists(#logged) OR #logged <> :due)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = LedgerFields.Status,
                    ["#due"] = LedgerFields.DueAt,
                    ["#logged"] = LedgerFields.ManualOverdueLoggedFor
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":manual"] = S(LedgerFields.StatusManual),
                    [":due"] = S(dueAt)
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
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? Next)> QueryOwedTransportLegsAsync(
        string status, Dictionary<string, AttributeValue>? startKey)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _escrowsTable,
            IndexName = "StatusExpiresIndex",
            KeyConditionExpression = "#status = :status",
            FilterExpression = "#model = :ledger AND #legs.#transport = :held AND #legs.#crop <> :held",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "status",
                ["#model"] = LedgerEscrowFields.SettlementModel,
                ["#legs"] = LedgerEscrowFields.LegStatus,
                ["#transport"] = LedgerEscrowFields.LegTransport,
                ["#crop"] = LedgerEscrowFields.LegCrop
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = S(status),
                [":ledger"] = S(LedgerEscrowFields.ModelLedger),
                [":held"] = S(LedgerEscrowFields.Held)
            },
            ScanIndexForward = true,
            Limit = 100,
            ExclusiveStartKey = startKey
        });
        return (response.Items.Select(DynamoDbHelper.FromItem).ToList(),
            response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null);
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    private async Task<LedgerWrite> TransactAsync(
        List<TransactWriteItem> items, HashSet<int> rowIndexes, int escrowIndex, LedgerWrite rowConflict,
        int referenceIndex = -1)
    {
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
            return LedgerWrite.Written;
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            for (var i = 0; i < reasons.Count; i++)
            {
                if (reasons[i]?.Code != "ConditionalCheckFailed")
                {
                    continue;
                }

                if (rowIndexes.Contains(i))
                {
                    return rowConflict;
                }

                if (i == escrowIndex)
                {
                    return LedgerWrite.EscrowConflict;
                }
            }

            // FUP-05: the row and the escrow held, only the reference item failed: the reference is taken.
            if (referenceIndex >= 0 && referenceIndex < reasons.Count && reasons[referenceIndex]?.Code == "ConditionalCheckFailed")
            {
                return LedgerWrite.ReferenceTaken;
            }

            // A concurrent transaction on the same items, or an audit key taken at the same instant:
            // nothing was written, and the caller reads again and retries.
            return escrowIndex >= 0 ? LedgerWrite.EscrowConflict : rowConflict;
        }
    }

    private Update BuildDecisionUpdate(LedgerDecision decision)
    {
        var before = decision.Before;
        var after = decision.After;
        var names = new Dictionary<string, string>
        {
            ["#legs"] = LedgerEscrowFields.LegStatus,
            ["#refunded"] = LedgerEscrowFields.RefundedTotal,
            ["#released"] = LedgerEscrowFields.ReleasedTotal,
            ["#updated"] = "updatedAt",
            ["#version"] = "version",
            ["#status"] = "status",
            ["#lock"] = SettlementLockFields.Lock,
            ["#manual"] = ManualPayoutFields.Marker
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":legs"] = new() { M = after.Legs.ToDictionary(kv => kv.Key, kv => S(kv.Value)) },
            [":refunded"] = N(after.RefundedTotal),
            [":released"] = N(after.ReleasedTotal),
            [":updated"] = S(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
            [":one"] = N(1),
            [":v"] = N(before.Version),
            [":status"] = S(before.Status)
        };
        var sets = new List<string> { "#legs = :legs", "#refunded = :refunded", "#released = :released", "#updated = :updated" };
        AddAmount("farmerPayout", before.FarmerPayout, after.FarmerPayout, "fp");
        AddAmount("truckerPayout", before.TruckerPayout, after.TruckerPayout, "tp");
        AddAmount("commission", before.Commission, after.Commission, "cm");

        var extra = 0;
        foreach (var (field, value) in decision.ExtraEscrowFields ?? new Dictionary<string, object>())
        {
            names[$"#x{extra}"] = field;
            values[$":x{extra}"] = DynamoDbHelper.ToAttributeValue(value);
            sets.Add($"#x{extra} = :x{extra}");
            extra++;
        }

        // The version read is the compare-and-set; the status the caller validated and the legs that move
        // still being held say the same thing in words, so a reader of the condition sees the rule.
        var conditions = new List<string>
        {
            "#version = :v", "#status = :status", "attribute_not_exists(#lock)", "attribute_not_exists(#manual)"
        };
        var legIndex = 0;
        foreach (var leg in before.Legs.Keys.Where(leg => before.IsHeld(leg) && !after.IsHeld(leg)))
        {
            names[$"#leg{legIndex}"] = leg;
            conditions.Add($"#legs.#leg{legIndex} = :held");
            values[":held"] = S(LedgerEscrowFields.Held);
            legIndex++;
        }

        return new Update
        {
            TableName = _escrowsTable,
            Key = EscrowKey(before.EscrowId, before.CreatedAt),
            UpdateExpression = "SET " + string.Join(", ", sets) + " ADD #version :one",
            ConditionExpression = string.Join(" AND ", conditions),
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };

        void AddAmount(string field, decimal was, decimal now, string alias)
        {
            if (was == now)
            {
                return;
            }

            names[$"#{alias}"] = field;
            values[$":{alias}"] = N(now);
            sets.Add($"#{alias} = :{alias}");
        }
    }

    private Update BuildRowUpdate(LedgerRow before, LedgerRow after, ManualRecordGuard? manualRecord = null)
    {
        var names = new Dictionary<string, string>
        {
            ["#status"] = LedgerFields.Status,
            ["#version"] = LedgerFields.Version,
            ["#updated"] = LedgerFields.UpdatedAt,
            ["#attempts"] = LedgerFields.Attempts
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":status"] = S(after.Status),
            [":updated"] = S(after.UpdatedAt),
            [":attempts"] = N(after.Attempts),
            [":v"] = N(before.Version),
            [":was"] = S(before.Status),
            [":one"] = N(1)
        };
        var sets = new List<string> { "#status = :status", "#updated = :updated", "#attempts = :attempts" };
        var removes = new List<string>();

        Field(LedgerFields.TelebirrRef, before.TelebirrRef, after.TelebirrRef, "tref");
        Field(LedgerFields.LastCode, before.LastCode, after.LastCode, "code");
        Field(LedgerFields.CallingSince, before.CallingSince, after.CallingSince, "calling");
        Field(LedgerFields.ManualReason, before.ManualReason, after.ManualReason, "manualReason");
        Field(LedgerFields.PaidBy, before.PaidBy, after.PaidBy, "paidBy");
        Field(LedgerFields.SentAt, before.SentAt, after.SentAt, "sentAt");
        Field(LedgerFields.DoneAt, before.DoneAt, after.DoneAt, "doneAt");

        // The index keys: an open row carries its status and when to look again; a done row leaves the index.
        var open = after.Status == LedgerFields.StatusDone ? null : after.Status;
        Field(LedgerFields.OpenStatus, before.Status == LedgerFields.StatusDone ? null : before.Status, open, "open", forceSet: open != null);
        Field(LedgerFields.DueAt, before.DueAt, open == null ? null : after.DueAt ?? after.UpdatedAt, "due", forceSet: open != null);

        // FUP-05: an admin's record of a manual row needs no live claim of another admin and ends the claim.
        var condition = "#version = :v AND #status = :was";
        if (manualRecord != null)
        {
            names["#claimBy"] = ManualPayoutClaimFields.LedgerClaimedBy;
            names["#claimAt"] = ManualPayoutClaimFields.LedgerClaimedAt;
            names["#claimExp"] = ManualPayoutClaimFields.LedgerClaimExpiresAt;
            values[":claimActor"] = S(manualRecord.Actor);
            values[":claimNow"] = S(manualRecord.Now);
            condition += " AND (attribute_not_exists(#claimBy) OR #claimBy = :claimActor OR #claimExp < :claimNow)";
            removes.AddRange(new[] { "#claimBy", "#claimAt", "#claimExp" });
        }

        var expression = "SET " + string.Join(", ", sets);
        if (removes.Count > 0)
        {
            expression += " REMOVE " + string.Join(", ", removes);
        }

        return new Update
        {
            TableName = _ledgerTable,
            Key = RowKey(before.EscrowId, before.Ref),
            UpdateExpression = expression + " ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };

        void Field(string field, string? was, string? now, string alias, bool forceSet = false)
        {
            if (now != null && (forceSet || now != was))
            {
                names[$"#{alias}"] = field;
                values[$":{alias}"] = S(now);
                sets.Add($"#{alias} = :{alias}");
            }
            else if (now == null)
            {
                names[$"#{alias}"] = field;
                removes.Add($"#{alias}");
            }
        }
    }

    private Update BuildEscrowStatusUpdate(
        (string EscrowId, string CreatedAt, int Version) escrow, string? status, IReadOnlyDictionary<string, object>? fields)
    {
        var names = new Dictionary<string, string> { ["#updated"] = "updatedAt", ["#version"] = "version" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":updated"] = S(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
            [":v"] = N(escrow.Version),
            [":one"] = N(1)
        };
        var sets = new List<string> { "#updated = :updated" };
        if (status != null)
        {
            names["#status"] = "status";
            values[":status"] = S(status);
            sets.Add("#status = :status");
        }

        var i = 0;
        foreach (var (field, value) in fields ?? new Dictionary<string, object>())
        {
            names[$"#f{i}"] = field;
            values[$":f{i}"] = DynamoDbHelper.ToAttributeValue(value);
            sets.Add($"#f{i} = :f{i}");
            i++;
        }

        var expression = "SET " + string.Join(", ", sets);
        // ESC-10: a settled escrow is no longer blocked; its block reason goes with the settlement.
        if (status is "released" or "refunded" or "completed")
        {
            names["#blockReason"] = SettlementBlockFields.Reason;
            names["#blockReasonAt"] = SettlementBlockFields.ReasonAt;
            expression += " REMOVE #blockReason, #blockReasonAt";
        }

        return new Update
        {
            TableName = _escrowsTable,
            Key = EscrowKey(escrow.EscrowId, escrow.CreatedAt),
            // The escrow version read: two rows that become done at once serialize here, so the second
            // one sees the first and the status can never skip a step.
            UpdateExpression = expression + " ADD #version :one",
            ConditionExpression = "#version = :v",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    private TransactWriteItem AuditPut(LedgerAudit audit) => AuditPut(_auditTable, audit);

    /// <summary>
    /// An escrow-audit row as one item of a transaction, at its deterministic key, never overwritten. FUP-218: the
    /// reference correction writes its row the same way.
    /// </summary>
    internal static TransactWriteItem AuditPut(string auditTable, LedgerAudit audit)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["auditId"] = S(EscrowAuditRepository.DeterministicAuditId(audit.Action, audit.Timestamp)),
            ["escrowId"] = S(audit.EscrowId),
            ["timestamp"] = S(audit.Timestamp),
            ["action"] = S(audit.Action),
            ["fromState"] = S(audit.FromState),
            ["toState"] = S(audit.ToState),
            ["actor"] = S(audit.Actor),
            ["metadata"] = S(audit.Metadata)
        };
        if (!string.IsNullOrEmpty(audit.TelebirrRef))
        {
            item["telebirrRef"] = S(audit.TelebirrRef);
        }

        return new TransactWriteItem
        {
            Put = new Put
            {
                TableName = auditTable,
                Item = item,
                // Append-only: an audit row is never overwritten.
                ConditionExpression = "attribute_not_exists(#id)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = "escrowId" }
            }
        };
    }

    // ── Marshalling ─────────────────────────────────────────────────────────

    internal static Dictionary<string, AttributeValue> ToItem(LedgerRow row)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            [LedgerFields.EscrowId] = S(row.EscrowId),
            [LedgerFields.Ref] = S(row.Ref),
            [LedgerFields.Kind] = S(row.Kind),
            [LedgerFields.Amount] = N(row.Amount),
            [LedgerFields.Status] = S(row.Status),
            [LedgerFields.GatewayRef] = S(row.GatewayRef),
            [LedgerFields.Attempts] = N(row.Attempts),
            [LedgerFields.Reason] = S(row.Reason),
            [LedgerFields.Actor] = S(row.Actor),
            [LedgerFields.CreatedAt] = S(row.CreatedAt),
            [LedgerFields.UpdatedAt] = S(row.UpdatedAt),
            [LedgerFields.Version] = N(row.Version)
        };
        if (row.Legs.Count > 0)
        {
            item[LedgerFields.Legs] = new AttributeValue { SS = row.Legs.ToList() };
        }

        Optional(LedgerFields.PayeeRole, row.PayeeRole);
        Optional(LedgerFields.PayeeId, row.PayeeId);
        Optional(LedgerFields.HoldRef, row.HoldRef);
        Optional(LedgerFields.TelebirrRef, row.TelebirrRef);
        Optional(LedgerFields.LastCode, row.LastCode);
        Optional(LedgerFields.CallingSince, row.CallingSince);
        Optional(LedgerFields.ManualReason, row.ManualReason);
        Optional(LedgerFields.PaidBy, row.PaidBy);
        Optional(LedgerFields.OrderId, row.OrderId);
        Optional(LedgerFields.SentAt, row.SentAt);
        Optional(LedgerFields.DoneAt, row.DoneAt);
        if (row.Status != LedgerFields.StatusDone)
        {
            item[LedgerFields.OpenStatus] = S(row.Status);
            Optional(LedgerFields.DueAt, row.DueAt ?? row.CreatedAt);
        }

        return item;

        void Optional(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                item[name] = S(value);
            }
        }
    }

    internal static LedgerRow FromItem(Dictionary<string, AttributeValue> item)
    {
        string? Str(string name) => item.TryGetValue(name, out var v) && v.S != null ? v.S : null;
        decimal Num(string name) => item.TryGetValue(name, out var v) && v.N != null
            ? decimal.Parse(v.N, NumberStyles.Number, CultureInfo.InvariantCulture)
            : 0m;

        return new LedgerRow
        {
            EscrowId = Str(LedgerFields.EscrowId) ?? "",
            Ref = Str(LedgerFields.Ref) ?? "",
            Kind = Str(LedgerFields.Kind) ?? "",
            Legs = item.TryGetValue(LedgerFields.Legs, out var legs) && legs.SS is { Count: > 0 }
                ? legs.SS.OrderBy(l => l, StringComparer.Ordinal).ToList()
                : new List<string>(),
            PayeeRole = Str(LedgerFields.PayeeRole),
            PayeeId = Str(LedgerFields.PayeeId),
            Amount = Num(LedgerFields.Amount),
            Status = Str(LedgerFields.Status) ?? "",
            GatewayRef = Str(LedgerFields.GatewayRef) ?? "",
            HoldRef = Str(LedgerFields.HoldRef),
            TelebirrRef = Str(LedgerFields.TelebirrRef),
            Attempts = (int)Num(LedgerFields.Attempts),
            LastCode = Str(LedgerFields.LastCode),
            CallingSince = Str(LedgerFields.CallingSince),
            ManualReason = Str(LedgerFields.ManualReason),
            Reason = Str(LedgerFields.Reason) ?? "",
            Actor = Str(LedgerFields.Actor) ?? "",
            PaidBy = Str(LedgerFields.PaidBy),
            OrderId = Str(LedgerFields.OrderId),
            CreatedAt = Str(LedgerFields.CreatedAt) ?? "",
            UpdatedAt = Str(LedgerFields.UpdatedAt) ?? "",
            SentAt = Str(LedgerFields.SentAt),
            DoneAt = Str(LedgerFields.DoneAt),
            DueAt = Str(LedgerFields.DueAt),
            Version = (int)Num(LedgerFields.Version),
            ClaimedBy = Str(ManualPayoutClaimFields.LedgerClaimedBy),
            ClaimedAt = Str(ManualPayoutClaimFields.LedgerClaimedAt),
            ClaimExpiresAt = Str(ManualPayoutClaimFields.LedgerClaimExpiresAt),
            ManualOverdueLoggedFor = Str(LedgerFields.ManualOverdueLoggedFor)
        };
    }

    private static Dictionary<string, AttributeValue> RowKey(string escrowId, string reference) => new()
    {
        [LedgerFields.EscrowId] = S(escrowId),
        [LedgerFields.Ref] = S(reference)
    };

    private static Dictionary<string, AttributeValue> EscrowKey(string escrowId, string createdAt) => new()
    {
        ["escrowId"] = S(escrowId),
        ["createdAt"] = S(createdAt)
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(decimal value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}

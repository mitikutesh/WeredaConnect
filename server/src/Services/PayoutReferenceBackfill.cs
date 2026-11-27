using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-215 (escrow-protocol.md 2.14.0): the one-off escrow-cron action <c>backfill_payout_references</c>. Since
/// 2.12.0 (FUP-05) every admin record of a manual payout leg writes a <c>payout-ref#{REFERENCE}</c> item in
/// escrow-idempotency, so one Telebirr portal reference records one leg. References recorded before 2.12.0 have
/// no item, so a new record of one of them was not refused. This action writes the missing items:
/// <list type="bullet">
/// <item>each admin-recorded 1.x leg: <c>manual{Leg}Ref</c> with <c>manual{Leg}PaidBy</c> other than
/// <c>gateway</c>, recorded at <c>manual{Leg}PaidAt</c>;</item>
/// <item>each ledger row <c>done</c> with a <c>paidBy</c> other than <c>gateway</c> (an admin), with its
/// <c>telebirrRef</c> at its <c>doneAt</c>.</item>
/// </list>
/// The item is the record path's own (<see cref="PayoutReferenceFields.BuildPut"/>: the key trimmed and upper
/// case, 400 day TTL from the record), put with <c>attribute_not_exists</c>. An item already held by the same leg
/// is a no-op; one held by another leg or escrow is never overwritten: it logs
/// <see cref="ConflictToken"/> with both places for a person. Reads the escrows table with one filtered Scan
/// (no index lists recorded legs) and the ledger rows of each ledger escrow with a Query. Nothing about money,
/// a status or an amount changes. Manual only (no schedule, no deploy step), bounded by a marker and safe to
/// run again.
/// </summary>
internal sealed class PayoutReferenceBackfill
{
    public const string Action = "backfill_payout_references";

    /// <summary>
    /// The marker in escrow-idempotency (30 day TTL): <c>done</c> once a run read the whole table with no failure,
    /// after which a run reads nothing; <c>retry</c> after a run with a failure, so a chain resumed with
    /// <c>scanFrom</c> cannot write <c>done</c>; <c>pending</c> once a run from the start (no <c>scanFrom</c>)
    /// began a new chain after a <c>retry</c>, so that chain can write <c>done</c> when none of its runs fails.
    /// Deleting it makes the next run read again.
    /// </summary>
    public const string MarkerKey = "migration:fup-215:backfill_payout_references";
    public const string MarkerDone = "done";
    public const string MarkerRetry = "retry";
    public const string MarkerPending = "pending";

    /// <summary>The summary line of every run.</summary>
    public const string SummaryToken = "PAYOUT_REF_BACKFILL";

    /// <summary>A reference recorded on two places before 2.12.0: a person decides (not alarmed).</summary>
    public const string ConflictToken = "PAYOUT_REF_BACKFILL_CONFLICT";

    /// <summary>A read or write that failed; the next run tries again (not alarmed).</summary>
    public const string FailedToken = "PAYOUT_REF_BACKFILL_FAILED";

    /// <summary>A recorded leg whose record time cannot be read, so no item can be dated (bad data only).</summary>
    public const string SkippedToken = "PAYOUT_REF_BACKFILL_SKIPPED";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _escrowsTable;
    private readonly string _referenceTable;
    private readonly IEscrowLedgerRepository? _ledger;
    private readonly IEscrowIdempotencyRepository _markers;
    private readonly IManualPayoutClaimRepository _holders;

    public PayoutReferenceBackfill(
        IAmazonDynamoDB dynamoDb, string escrowsTable, string referenceTable, IEscrowLedgerRepository? ledger)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _escrowsTable = string.IsNullOrEmpty(escrowsTable) ? throw new ArgumentNullException(nameof(escrowsTable)) : escrowsTable;
        _referenceTable = string.IsNullOrEmpty(referenceTable) ? throw new ArgumentNullException(nameof(referenceTable)) : referenceTable;
        _ledger = ledger;
        _markers = new EscrowIdempotencyRepository(dynamoDb, referenceTable);
        _holders = new ManualPayoutClaimRepository(dynamoDb, escrowsTable, null, referenceTable);
    }

    /// <summary>Rows the Scan evaluates per page (before its filter).</summary>
    internal int PageSize { get; init; } = 100;

    /// <summary>One recorded leg: where it is and the reference item it should have (the reference trimmed, as the record stores it).</summary>
    private sealed record RecordedLeg(string EscrowId, string Ref, string ExternalRef, string RecordedBy, string? RecordedAt);

    private enum LegOutcome
    {
        Written,
        Present,
        Conflict,
        Failed,
        Skipped
    }

    /// <summary>What one run did; <see cref="Cursor"/> is where a Scan stopped on time resumes.</summary>
    internal sealed class Counts
    {
        public int Escrows { get; set; }
        public int Legs { get; set; }
        public int LedgerRows { get; set; }
        public int Written { get; set; }
        public int Present { get; set; }
        public int Conflicts { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public bool ScanDone { get; set; }
        public bool SkippedRun { get; set; }
        public string? Cursor { get; set; }
        public string? Marker { get; set; }

        /// <summary><c>ok</c> once the whole table is read with no failure and the marker says done; else <c>partial</c>.</summary>
        public string Outcome => SkippedRun || (ScanDone && Failed == 0 && Marker == MarkerDone) ? "ok" : "partial";
    }

    /// <summary>
    /// The cursor text of an escrows key, <c>{escrowId}|{createdAt}</c>, or null. Escrow ids are server-made and
    /// never hold a <c>|</c>.
    /// </summary>
    internal static string? CursorOf(Dictionary<string, AttributeValue>? key)
        => key != null && key.TryGetValue("escrowId", out var id) && !string.IsNullOrEmpty(id.S)
           && key.TryGetValue("createdAt", out var at) && !string.IsNullOrEmpty(at.S)
            ? $"{id.S}|{at.S}"
            : null;

    /// <summary>The escrows key of a cursor, or null when the text is not one.</summary>
    internal static Dictionary<string, AttributeValue>? KeyOf(string? cursor)
    {
        var bar = cursor?.IndexOf('|') ?? -1;
        if (cursor == null || bar <= 0 || bar == cursor.Length - 1 || cursor.IndexOf('|', bar + 1) >= 0)
        {
            return null;
        }

        return new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = new() { S = cursor[..bar] },
            ["createdAt"] = new() { S = cursor[(bar + 1)..] }
        };
    }

    /// <summary>
    /// One run: the marker, then the Scan from <paramref name="scanFrom"/> (a cursor of an earlier run that stopped
    /// on time) or from the start, until the end or the end of <paramref name="slice"/>. Never throws for a row or a
    /// page: a failure is counted, logged and left for the next run.
    /// </summary>
    public async Task<Counts> RunAsync(string? scanFrom, CronRunSlice slice, ILambdaContext context)
    {
        var counts = new Counts();
        var marker = await ReadMarkerAsync(context);
        counts.Marker = marker;
        if (marker == MarkerDone)
        {
            counts.SkippedRun = true;
            context.Logger.LogInformation($"{SummaryToken} marker=done, nothing read.");
            return counts;
        }

        if (scanFrom == null && marker == MarkerRetry)
        {
            // A run from the start after a failure begins a new chain: it and the runs resumed from its cursor
            // re-read every row the failed run left behind, so that chain may write done (a failure in it writes
            // retry again). If this write fails the marker stays retry and only this run can write done.
            marker = await WriteMarkerAsync(marker, MarkerPending, context) ?? marker;
            counts.Marker = marker;
        }

        await ScanAsync(KeyOf(scanFrom), slice, context, counts);

        if (counts.Failed > 0)
        {
            counts.Marker = await WriteMarkerAsync(marker, MarkerRetry, context) ?? marker;
        }
        else if (counts.ScanDone && (scanFrom == null || marker != MarkerRetry))
        {
            // A chain resumed with scanFrom after a run that failed somewhere cannot vouch for the rows that run
            // left behind: after a retry only a new chain (a run from the start, marker pending) writes done.
            counts.Marker = await WriteMarkerAsync(marker, MarkerDone, context) ?? marker;
        }

        context.Logger.LogInformation(
            $"{SummaryToken} escrows={counts.Escrows} legs={counts.Legs} ledgerRows={counts.LedgerRows} written={counts.Written} "
            + $"present={counts.Present} conflicts={counts.Conflicts} failed={counts.Failed} skipped={counts.Skipped} "
            + $"scanDone={(counts.ScanDone ? "true" : "false")} marker={counts.Marker ?? "none"} from={(scanFrom == null ? "start" : "scanFrom")}");
        return counts;
    }

    /// <summary>
    /// The filtered Scan of the escrows table: the rows that carry a recorded 1.x leg reference, and the ledger
    /// escrows. It reads the whole table once (read capacity for every row, matching or not: about one read unit
    /// per 8 KB, eventually consistent). Before each page and each escrow the time budget is checked; on a stop the
    /// cursor is the last escrow handled (or where this run started), so a resumed Scan neither skips nor repeats one.
    /// </summary>
    private async Task ScanAsync(Dictionary<string, AttributeValue>? startKey, CronRunSlice slice, ILambdaContext context, Counts counts)
    {
        var names = new Dictionary<string, string>
        {
            ["#id"] = "escrowId",
            ["#created"] = "createdAt",
            ["#model"] = LedgerEscrowFields.SettlementModel
        };
        var projection = new List<string> { "#id", "#created", "#model" };
        var filters = new List<string>();
        var i = 0;
        foreach (var leg in ManualPayoutFields.AllLegs)
        {
            names[$"#r{i}"] = ManualPayoutFields.RefAttr(leg);
            names[$"#b{i}"] = ManualPayoutFields.PaidByAttr(leg);
            names[$"#a{i}"] = ManualPayoutFields.PaidAtAttr(leg);
            projection.AddRange(new[] { $"#r{i}", $"#b{i}", $"#a{i}" });
            filters.Add($"attribute_exists(#r{i})");
            i++;
        }

        var values = new Dictionary<string, AttributeValue>();
        if (_ledger != null)
        {
            filters.Add("#model = :ledger");
            values[":ledger"] = new AttributeValue { S = LedgerEscrowFields.ModelLedger };
        }

        var request = new ScanRequest
        {
            TableName = _escrowsTable,
            ProjectionExpression = string.Join(", ", projection),
            FilterExpression = string.Join(" OR ", filters),
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values.Count > 0 ? values : null,
            Limit = Math.Clamp(PageSize, 1, 1000)
        };

        var position = startKey;
        while (true)
        {
            if (!slice.MayStartRow(context))
            {
                counts.Cursor = CursorOf(position);
                return;
            }

            ScanResponse page;
            try
            {
                request.ExclusiveStartKey = position;
                page = await _dynamoDb.ScanAsync(request);
            }
            catch (Exception ex)
            {
                // The next run starts again where this one stopped (the cursor) or from the start.
                counts.Failed++;
                counts.Cursor = CursorOf(position);
                context.Logger.LogWarning($"{FailedToken} step=scan reason=[{ex.GetType().Name}]");
                return;
            }

            foreach (var item in page.Items ?? new List<Dictionary<string, AttributeValue>>())
            {
                if (!slice.MayStartRow(context))
                {
                    counts.Cursor = CursorOf(position);
                    return;
                }

                await BackfillEscrowAsync(item, context, counts);
                position = new Dictionary<string, AttributeValue>
                {
                    ["escrowId"] = item["escrowId"],
                    ["createdAt"] = item["createdAt"]
                };
            }

            if (page.LastEvaluatedKey is not { Count: > 0 } next)
            {
                counts.ScanDone = true;
                return;
            }

            position = next;
        }
    }

    /// <summary>One escrow from the Scan: its admin-recorded 1.x legs, then the admin-done rows of its ledger.</summary>
    private async Task BackfillEscrowAsync(Dictionary<string, AttributeValue> item, ILambdaContext context, Counts counts)
    {
        counts.Escrows++;
        var escrowId = item["escrowId"].S;
        foreach (var leg in ManualPayoutFields.AllLegs)
        {
            var reference = Str(item, ManualPayoutFields.RefAttr(leg));
            var paidBy = Str(item, ManualPayoutFields.PaidByAttr(leg));
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrEmpty(paidBy)
                || paidBy == ManualPayoutFields.PaidByGateway)
            {
                continue;
            }

            counts.Legs++;
            Count(counts, await EnsureItemAsync(
                new RecordedLeg(escrowId, leg, reference.Trim(), paidBy, Str(item, ManualPayoutFields.PaidAtAttr(leg))), context));
        }

        if (_ledger == null || Str(item, LedgerEscrowFields.SettlementModel) != LedgerEscrowFields.ModelLedger)
        {
            return;
        }

        List<LedgerRow> rows;
        try
        {
            rows = await _ledger.GetRowsAsync(escrowId);
        }
        catch (Exception ex)
        {
            counts.Failed++;
            context.Logger.LogWarning(
                $"{FailedToken} step=ledger escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
            return;
        }

        foreach (var row in rows)
        {
            if (row.Status != LedgerFields.StatusDone || string.IsNullOrWhiteSpace(row.TelebirrRef)
                || string.IsNullOrEmpty(row.PaidBy) || row.PaidBy == LedgerFields.PaidByGateway)
            {
                continue;
            }

            counts.LedgerRows++;
            Count(counts, await EnsureItemAsync(
                new RecordedLeg(escrowId, row.Ref, row.TelebirrRef.Trim(), row.PaidBy, row.DoneAt ?? row.UpdatedAt), context));
        }
    }

    private static void Count(Counts counts, LegOutcome outcome)
    {
        switch (outcome)
        {
            case LegOutcome.Written:
                counts.Written++;
                break;
            case LegOutcome.Present:
                counts.Present++;
                break;
            case LegOutcome.Conflict:
                counts.Conflicts++;
                break;
            case LegOutcome.Skipped:
                counts.Skipped++;
                break;
            default:
                counts.Failed++;
                break;
        }
    }

    /// <summary>
    /// The reference item of one recorded leg, put only when its key is free. A key that is taken is read
    /// (strongly consistent): the same escrow and ref is a no-op, anything else a conflict for a person.
    /// </summary>
    private async Task<LegOutcome> EnsureItemAsync(RecordedLeg leg, ILambdaContext context)
    {
        var where = $"escrowId={MoneyAlarmLog.LogId(leg.EscrowId)} ref={LogRef(leg.Ref)}";
        if (!DateTime.TryParse(leg.RecordedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            context.Logger.LogWarning($"{SkippedToken} {where} reason=no_record_time");
            return LegOutcome.Skipped;
        }

        var put = PayoutReferenceFields.BuildPut(new PayoutReferenceRecord(
            _referenceTable, leg.ExternalRef, leg.EscrowId, leg.Ref, leg.RecordedBy, leg.RecordedAt!)).Put;
        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = put.TableName,
                Item = put.Item,
                ConditionExpression = put.ConditionExpression,
                ExpressionAttributeNames = put.ExpressionAttributeNames
            });
            return LegOutcome.Written;
        }
        catch (ConditionalCheckFailedException)
        {
            // Taken: by this leg (a rerun, or a record since 2.12.0) or by another one.
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{FailedToken} step=put {where} reason=[{ex.GetType().Name}]");
            return LegOutcome.Failed;
        }

        PayoutReferenceHolder? holder;
        try
        {
            holder = await _holders.GetReferenceHolderAsync(leg.ExternalRef);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{FailedToken} step=read {where} reason=[{ex.GetType().Name}]");
            return LegOutcome.Failed;
        }

        if (holder == null)
        {
            // Gone between the put and the read (its TTL passed): the next run writes it.
            context.Logger.LogWarning($"{FailedToken} step=read {where} reason=item_gone");
            return LegOutcome.Failed;
        }

        if (holder.EscrowId == leg.EscrowId && holder.Ref == leg.Ref)
        {
            return LegOutcome.Present;
        }

        context.Logger.LogWarning(
            $"{ConflictToken} reference={MoneyAlarmLog.LogId(PayoutReferenceFields.Normalize(leg.ExternalRef))} "
            + $"heldBy={MoneyAlarmLog.LogId(holder.EscrowId)}/{LogRef(holder.Ref)} recordedBy={MoneyAlarmLog.LogId(holder.RecordedBy)} "
            + $"alsoOn={MoneyAlarmLog.LogId(leg.EscrowId)}/{LogRef(leg.Ref)} alsoRecordedBy={MoneyAlarmLog.LogId(leg.RecordedBy)}: "
            + "one portal reference on two legs; a person checks the portal (escrow-protocol.md 2.14.0).");
        return LegOutcome.Conflict;
    }

    /// <summary>A 1.x leg name or a ledger ref for a log line (<c>#</c> becomes <c>:</c>, as <see cref="MoneyAlarmLog.LogId"/> drops it).</summary>
    private static string LogRef(string? reference) => MoneyAlarmLog.LogId(reference?.Replace('#', ':'));

    private async Task<string?> ReadMarkerAsync(ILambdaContext context)
    {
        try
        {
            var marker = await _markers.GetAsync(MarkerKey);
            return marker != null && marker.TryGetValue("status", out var status) ? status?.ToString() : null;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{SummaryToken} marker read failed [{ex.GetType().Name}]; running the read.");
            return null;
        }
    }

    /// <summary>Writes the marker; returns the status written, or null when the write failed (the next run reads again).</summary>
    private async Task<string?> WriteMarkerAsync(string? current, string status, ILambdaContext context)
    {
        try
        {
            if (current != null || !await _markers.StoreAsync(MarkerKey, "-", status))
            {
                await _markers.UpdateAsync(MarkerKey, status);
            }

            return status;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{SummaryToken} marker write failed [{ex.GetType().Name}].");
            return null;
        }
    }

    private static string? Str(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var value) ? value.S : null;
}

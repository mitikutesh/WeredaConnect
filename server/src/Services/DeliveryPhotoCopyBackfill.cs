using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-140 (delivery-verification-api.md 2.14.0): the one-off order-cron action <c>backfill_delivery_photo_copies</c>.
/// Since 2.14.0 the attach copies a handoff photo to its kept key under <c>evidence/photos/</c> and the reads sign
/// that copy, so <c>uploads/photos/</c> may expire. Photos attached before 2.14.0 have no copy; this action makes
/// them, before the expiry rule goes on in an environment:
/// <list type="bullet">
/// <item>one filtered Scan of the deliveries table for the rows with a photo (<c>pickupPhotoRef</c>,
/// <c>dropoffPhotoRef</c>, <c>arrivalPhotoRef</c>) and no kept copy of it;</item>
/// <item>each such photo whose key has this delivery's shape for its type (the only ones a read signs) is copied at
/// its recorded version (<see cref="DeliveryPhotoEvidence.KeepAsync"/>), then the copy is recorded with a
/// conditional update: the photo and its version unchanged, no copy recorded yet. Nothing else on the row moves
/// (no <c>version</c>, no <c>updatedAt</c>), so no phone sees a conflict and the notifier ignores the write.</item>
/// </list>
/// A key of another shape is skipped (never signed); an object that is gone is counted as missing (its read was
/// already dead). Manual only (no schedule, no deploy step), bounded by the time slice, resumable from the
/// <c>scanCursor</c> of a run that stopped, and safe to run again: a row with its copy recorded is filtered out.
/// </summary>
internal sealed class DeliveryPhotoCopyBackfill
{
    public const string Action = "backfill_delivery_photo_copies";

    /// <summary>The summary line of every run.</summary>
    public const string SummaryToken = "DELIVERY_PHOTO_BACKFILL";

    /// <summary>A copy, a write or a page that failed; the next run tries again (not alarmed).</summary>
    public const string FailedToken = "DELIVERY_PHOTO_BACKFILL_FAILED";

    /// <summary>A recorded photo whose object (or version) is not in the bucket: nothing to keep.</summary>
    public const string MissingToken = "DELIVERY_PHOTO_BACKFILL_MISSING";

    /// <summary>A recorded photo key without this delivery's shape for its type: no read signs it.</summary>
    public const string SkippedToken = "DELIVERY_PHOTO_BACKFILL_SKIPPED";

    /// <summary>The longest <c>scanFrom</c> accepted (two ids and a bar).</summary>
    public const int MaxCursorLength = 300;

    private static readonly string[] Types = { DeliveryPhotoKeys.Pickup, DeliveryPhotoKeys.Dropoff, DeliveryPhotoKeys.Arrival };

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _deliveriesTable;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public DeliveryPhotoCopyBackfill(IAmazonDynamoDB dynamoDb, string deliveriesTable, IAmazonS3 s3, string bucket)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _deliveriesTable = string.IsNullOrEmpty(deliveriesTable) ? throw new ArgumentNullException(nameof(deliveriesTable)) : deliveriesTable;
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _bucket = string.IsNullOrEmpty(bucket) ? throw new ArgumentNullException(nameof(bucket)) : bucket;
    }

    /// <summary>Rows the Scan evaluates per page (before its filter).</summary>
    internal int PageSize { get; init; } = 100;

    private enum PhotoOutcome
    {
        Copied,
        Present,
        Missing,
        Skipped,
        Failed
    }

    /// <summary>What one run did.</summary>
    private sealed class Counts
    {
        public int Deliveries { get; set; }
        public int Copied { get; set; }
        public int Present { get; set; }
        public int Missing { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public bool ScanDone { get; set; }
        public string? Cursor { get; set; }
    }

    /// <summary>The cursor text of a deliveries key, <c>{deliveryId}|{tripId}</c>, or null.</summary>
    internal static string? CursorOf(Dictionary<string, AttributeValue>? key)
        => key != null && key.TryGetValue("deliveryId", out var id) && !string.IsNullOrEmpty(id.S)
           && !id.S.Contains('|')
           && key.TryGetValue("tripId", out var trip) && !string.IsNullOrEmpty(trip.S)
            ? $"{id.S}|{trip.S}"
            : null;

    /// <summary>The deliveries key of a cursor (split at its first <c>|</c>), or null when the text is not one.</summary>
    internal static Dictionary<string, AttributeValue>? KeyOf(string? cursor)
    {
        var bar = cursor?.IndexOf('|') ?? -1;
        if (cursor == null || bar <= 0 || bar == cursor.Length - 1 || cursor.Length > MaxCursorLength
            || cursor.Any(char.IsControl))
        {
            return null;
        }

        return new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = new() { S = cursor[..bar] },
            ["tripId"] = new() { S = cursor[(bar + 1)..] }
        };
    }

    /// <summary>
    /// One run: the Scan from <paramref name="scanFrom"/> (the <c>scanCursor</c> of a run that stopped on time) or
    /// from the start, until the end or the end of <paramref name="slice"/>. Never throws for a row or a page: a
    /// failure is counted, logged and left for the next run. <c>outcome</c> is <c>ok</c> once this run read to the
    /// end of the table with no failure.
    /// </summary>
    public async Task<Dictionary<string, string>> RunAsync(string? scanFrom, CronRunSlice slice, ILambdaContext context)
    {
        var counts = new Counts();
        await ScanAsync(KeyOf(scanFrom), slice, context, counts);

        context.Logger.LogInformation(
            $"{SummaryToken} deliveries={counts.Deliveries} copied={counts.Copied} present={counts.Present} "
            + $"missing={counts.Missing} skipped={counts.Skipped} failed={counts.Failed} "
            + $"scanDone={(counts.ScanDone ? "true" : "false")} from={(scanFrom == null ? "start" : "scanFrom")}");
        var summary = new Dictionary<string, string>
        {
            ["action"] = Action,
            ["outcome"] = counts.ScanDone && counts.Failed == 0 ? "ok" : "partial",
            ["deliveries"] = counts.Deliveries.ToString(CultureInfo.InvariantCulture),
            ["copied"] = counts.Copied.ToString(CultureInfo.InvariantCulture),
            ["present"] = counts.Present.ToString(CultureInfo.InvariantCulture),
            ["missing"] = counts.Missing.ToString(CultureInfo.InvariantCulture),
            ["skipped"] = counts.Skipped.ToString(CultureInfo.InvariantCulture),
            ["failed"] = counts.Failed.ToString(CultureInfo.InvariantCulture),
        };
        if (!counts.ScanDone && !string.IsNullOrEmpty(counts.Cursor))
        {
            summary["scanCursor"] = counts.Cursor;
        }

        return summary;
    }

    /// <summary>
    /// The filtered Scan: the rows with a photo of some type and no kept copy of it. It reads the whole table once
    /// (read capacity for every row, matching or not: about one read unit per 8 KB, eventually consistent). Before
    /// each page and each row the time budget is checked; on a stop the cursor is the last row handled (or where
    /// this run started), so a resumed Scan neither skips nor repeats one.
    /// </summary>
    private async Task ScanAsync(Dictionary<string, AttributeValue>? startKey, CronRunSlice slice, ILambdaContext context, Counts counts)
    {
        var names = new Dictionary<string, string> { ["#id"] = "deliveryId", ["#trip"] = "tripId" };
        var projection = new List<string> { "#id", "#trip" };
        var filters = new List<string>();
        for (var i = 0; i < Types.Length; i++)
        {
            names[$"#r{i}"] = DeliveryPhotoKeys.FieldOf(Types[i]);
            names[$"#v{i}"] = DeliveryPhotoKeys.VersionFieldOf(Types[i]);
            names[$"#k{i}"] = DeliveryPhotoKeys.KeptFieldOf(Types[i]);
            projection.AddRange(new[] { $"#r{i}", $"#v{i}", $"#k{i}" });
            filters.Add($"(attribute_exists(#r{i}) AND attribute_not_exists(#k{i}))");
        }

        var request = new ScanRequest
        {
            TableName = _deliveriesTable,
            ProjectionExpression = string.Join(", ", projection),
            FilterExpression = string.Join(" OR ", filters),
            ExpressionAttributeNames = names,
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

                await BackfillDeliveryAsync(item, context, counts);
                position = new Dictionary<string, AttributeValue>
                {
                    ["deliveryId"] = item["deliveryId"],
                    ["tripId"] = item["tripId"]
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

    /// <summary>One delivery from the Scan: each photo it records without a kept copy.</summary>
    private async Task BackfillDeliveryAsync(Dictionary<string, AttributeValue> item, ILambdaContext context, Counts counts)
    {
        counts.Deliveries++;
        foreach (var type in Types)
        {
            var photoRef = Str(item, DeliveryPhotoKeys.FieldOf(type));
            if (string.IsNullOrEmpty(photoRef) || !string.IsNullOrEmpty(Str(item, DeliveryPhotoKeys.KeptFieldOf(type))))
            {
                continue;
            }

            switch (await KeepAsync(item, type, photoRef, context))
            {
                case PhotoOutcome.Copied:
                    counts.Copied++;
                    break;
                case PhotoOutcome.Present:
                    counts.Present++;
                    break;
                case PhotoOutcome.Missing:
                    counts.Missing++;
                    break;
                case PhotoOutcome.Skipped:
                    counts.Skipped++;
                    break;
                default:
                    counts.Failed++;
                    break;
            }
        }
    }

    /// <summary>
    /// The kept copy of one recorded photo: copied at its recorded version (the latest when the row has none, which
    /// is what a read signed), then recorded only while the row still names that photo and version and no copy.
    /// </summary>
    private async Task<PhotoOutcome> KeepAsync(
        Dictionary<string, AttributeValue> item, string type, string photoRef, ILambdaContext context)
    {
        var deliveryId = item["deliveryId"].S;
        var where = $"deliveryId={MoneyAlarmLog.LogId(deliveryId)} type={type}";
        if (!DeliveryPhotoKeys.IsKeyOf(photoRef, deliveryId, type))
        {
            context.Logger.LogWarning($"{SkippedToken} {where} reason=key_shape");
            return PhotoOutcome.Skipped;
        }

        var versionId = Str(item, DeliveryPhotoKeys.VersionFieldOf(type));
        DeliveryPhotoEvidence.KeptCopy kept;
        try
        {
            kept = await DeliveryPhotoEvidence.KeepAsync(_s3, _bucket, photoRef, versionId);
        }
        catch (AmazonS3Exception ex) when (DeliveryPhotoEvidence.IsMissing(ex))
        {
            context.Logger.LogWarning($"{MissingToken} {where}");
            return PhotoOutcome.Missing;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{FailedToken} step=copy {where} reason=[{ex.GetType().Name}]");
            return PhotoOutcome.Failed;
        }

        var names = new Dictionary<string, string>
        {
            ["#photo"] = DeliveryPhotoKeys.FieldOf(type),
            ["#version"] = DeliveryPhotoKeys.VersionFieldOf(type),
            ["#kept"] = DeliveryPhotoKeys.KeptFieldOf(type)
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":ref"] = new() { S = photoRef },
            [":kept"] = new() { S = kept.Key }
        };
        var update = "SET #kept = :kept";
        if (kept.VersionId != null)
        {
            names["#keptVersion"] = DeliveryPhotoKeys.KeptVersionFieldOf(type);
            values[":keptVersion"] = new() { S = kept.VersionId };
            update += ", #keptVersion = :keptVersion";
        }

        var condition = "#photo = :ref AND attribute_not_exists(#kept) AND ";
        if (string.IsNullOrEmpty(versionId))
        {
            condition += "attribute_not_exists(#version)";
        }
        else
        {
            values[":version"] = new() { S = versionId };
            condition += "#version = :version";
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _deliveriesTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["deliveryId"] = item["deliveryId"],
                    ["tripId"] = item["tripId"]
                },
                UpdateExpression = update,
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            });
            return PhotoOutcome.Copied;
        }
        catch (ConditionalCheckFailedException)
        {
            // The row changed since the Scan read it (another run recorded the copy, or the row went): a later run
            // reads it again if it still needs one.
            context.Logger.LogInformation($"{SummaryToken} {where} changed since read; left for a later run.");
            return PhotoOutcome.Present;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{FailedToken} step=write {where} reason=[{ex.GetType().Name}]");
            return PhotoOutcome.Failed;
        }
    }

    private static string? Str(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var value) ? value.S : null;
}

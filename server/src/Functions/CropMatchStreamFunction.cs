using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// DynamoDB Streams consumer for the crops table. On each newly inserted,
/// available crop it scores active buyer intents with <see cref="MatchScoring"/>
/// (the same scorer as GET /match and the mobile scorer) and publishes an FCM
/// push, via SNS, to every buyer whose intent matches.
///
/// AOT note: the handler takes the raw event <see cref="Stream"/> and parses it
/// with <see cref="JsonDocument"/> — the typed DynamoDBEvent (which nests the
/// SDK AttributeValue type) is not friendly to the source-gen serializer.
///
/// See specs/architecture/push-notifications-architecture.md.
/// </summary>
public class CropMatchStreamFunction
{
    private readonly IIntentRepository _intentRepo;
    private readonly IDeviceTokenRepository _deviceTokenRepo;
    private readonly IAmazonSimpleNotificationService _sns;

    /// <summary>Minimum relevance score for a crop to alert a buyer.</summary>
    private const double AlertThreshold = 0.5;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public CropMatchStreamFunction()
    {
        var intentsTable = Environment.GetEnvironmentVariable("INTENTS_TABLE")
                           ?? throw new InvalidOperationException("INTENTS_TABLE is not set.");
        var deviceTokensTable = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                                ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _intentRepo = new IntentRepository(dynamoDb, intentsTable);
        _deviceTokenRepo = new DeviceTokenRepository(dynamoDb, deviceTokensTable);
        _sns = new AmazonSimpleNotificationServiceClient();
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public CropMatchStreamFunction(
        IIntentRepository intentRepo,
        IDeviceTokenRepository deviceTokenRepo,
        IAmazonSimpleNotificationService sns)
    {
        _intentRepo = intentRepo ?? throw new ArgumentNullException(nameof(intentRepo));
        _deviceTokenRepo = deviceTokenRepo ?? throw new ArgumentNullException(nameof(deviceTokenRepo));
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
    }

    /// <summary>
    /// Lambda entry point. Delegates to <see cref="ProcessBatchAsync"/> and
    /// serializes the partial-batch-response so the runtime retries only failed
    /// records (raw Stream out — wired this way in Program.cs to keep the
    /// AOT-friendly JsonDocument parse on the input side).
    /// </summary>
    public async Task<Stream> ProcessStreamAsync(Stream input, ILambdaContext context)
    {
        var response = await ProcessBatchAsync(input, context);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            response, AppJsonContext.Default.StreamBatchResponse);
        return new MemoryStream(bytes);
    }

    /// <summary>
    /// Core batch processing. Each record is handled independently; a record
    /// that throws is reported in <see cref="StreamBatchResponse.BatchItemFailures"/>
    /// by its DynamoDB sequence number so Lambda retries from it (at-least-once
    /// alerting) instead of silently dropping it. Exposed for unit tests.
    /// </summary>
    public async Task<StreamBatchResponse> ProcessBatchAsync(Stream input, ILambdaContext context)
    {
        var failures = new List<StreamBatchItemFailure>();

        using var doc = await JsonDocument.ParseAsync(input);
        if (!doc.RootElement.TryGetProperty("Records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            return new StreamBatchResponse();
        }

        // Cache endpoint lookups so multiple matching intents for one buyer
        // don't re-query device tokens.
        var endpointsByBuyer = new Dictionary<string, List<string>>();

        foreach (var record in records.EnumerateArray())
        {
            var sequenceNumber = GetSequenceNumber(record);
            try
            {
                if (GetString(record, "eventName") != "INSERT")
                {
                    continue;
                }

                if (!record.TryGetProperty("dynamodb", out var dynamodb)
                    || !dynamodb.TryGetProperty("NewImage", out var newImage))
                {
                    continue;
                }

                await ProcessNewCropAsync(newImage, endpointsByBuyer, context);
            }
            catch (Exception ex)
            {
                // Report this record's sequence number so Lambda redelivers the
                // batch from the earliest reported failure to its end (DynamoDB
                // stream semantics) instead of checkpointing past it — so a
                // transient fault (e.g. a DynamoDB throttle on the intent query)
                // no longer permanently drops a buyer's alert. This is
                // at-least-once: records after the earliest failure can be
                // reprocessed on redelivery, so a duplicate push is possible
                // (acceptable for best-effort alerts; the per-invocation
                // seenIntents/endpoint cache only dedups within one delivery).
                context.Logger.LogError($"Stream record error: [{ex.GetType().Name}]");
                if (sequenceNumber.Length > 0)
                {
                    failures.Add(new StreamBatchItemFailure { ItemIdentifier = sequenceNumber });
                }
                // A record with no SequenceNumber can't be targeted for retry, so
                // it's intentionally dropped (logged above) rather than reporting an
                // empty identifier, which the runtime would reject.
            }
        }

        return new StreamBatchResponse { BatchItemFailures = failures };
    }

    private async Task ProcessNewCropAsync(
        JsonElement image,
        Dictionary<string, List<string>> endpointsByBuyer,
        ILambdaContext context)
    {
        if (!string.Equals(AttrS(image, "status"), "available", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cropType = AttrS(image, "cropType");
        if (string.IsNullOrEmpty(cropType))
        {
            return;
        }

        var crop = new CropCandidate
        {
            CropType = cropType,
            PricePerKg = AttrN(image, "pricePerKg") ?? 0,
            Quantity = AttrN(image, "quantity") ?? 0,
            LocationName = AttrS(image, "locationName") ?? AttrS(image, "woreda"),
            Latitude = AttrN(image, "latitude"),
            Longitude = AttrN(image, "longitude"),
            FarmerReliability = AttrN(image, "farmerReliability"),
            // A stream INSERT is a brand-new crop, so freshness is max by
            // definition; using "now" keeps the alert score from being penalised
            // for parse/clock skew (it differs from GET /match, which scores the
            // crop's real createdAt).
            CreatedAt = DateTime.UtcNow,
        };
        var cropId = AttrS(image, "cropId") ?? "";
        var woreda = AttrS(image, "woreda") ?? crop.LocationName ?? "";
        var now = DateTime.UtcNow;

        // A new crop of type X can satisfy intents wanting X or wanting a type for
        // which X is an accepted substitute (the substitute map is symmetric).
        var seenIntents = new HashSet<string>();

        // One push per buyer per crop: a buyer with several matching intents
        // for the same new crop gets a single alert, not one per intent —
        // less notification noise and fewer SNS publishes.
        var notifiedBuyers = new HashSet<string>();

        foreach (var wantedType in MatchScoring.CandidateCropTypes(cropType))
        {
            var intents = await _intentRepo.QueryActiveByCropTypeAsync(wantedType);
            foreach (var intent in intents)
            {
                var intentId = GetItemString(intent, "intentId");
                var buyerId = GetItemString(intent, "buyerId");
                if (intentId.Length == 0 || buyerId.Length == 0 || !seenIntents.Add(intentId))
                {
                    continue;
                }

                if (notifiedBuyers.Contains(buyerId))
                {
                    continue;
                }

                var criteria = new MatchCriteria
                {
                    CropType = GetItemString(intent, "cropType"),
                    MaxPricePerKg = ItemDouble(intent, "maxPricePerKg"),
                    QuantityNeeded = ItemDouble(intent, "quantityNeeded"),
                    DeliveryWoreda = GetItemStringOrNull(intent, "deliveryWoreda"),
                    DeliveryLatitude = ItemDouble(intent, "deliveryLatitude"),
                    DeliveryLongitude = ItemDouble(intent, "deliveryLongitude"),
                    NowUtc = now,
                };

                if (!MatchScoring.IsCandidate(criteria, crop)
                    || MatchScoring.ScoreListing(criteria, crop) < AlertThreshold)
                {
                    continue;
                }

                notifiedBuyers.Add(buyerId);
                await NotifyBuyerAsync(buyerId, intentId, cropId, cropType, woreda,
                    endpointsByBuyer, context);
            }
        }
    }

    private async Task NotifyBuyerAsync(
        string buyerId,
        string intentId,
        string cropId,
        string cropType,
        string woreda,
        Dictionary<string, List<string>> endpointsByBuyer,
        ILambdaContext context)
    {
        if (!endpointsByBuyer.TryGetValue(buyerId, out var endpoints))
        {
            endpoints = await _deviceTokenRepo.GetEndpointArnsByUserAsync(buyerId);
            endpointsByBuyer[buyerId] = endpoints;
        }

        if (endpoints.Count == 0)
        {
            return; // buyer has no registered device — in-app badge covers them
        }

        var title = $"New {cropType} listing";
        var body = woreda.Length > 0
            ? $"A new {cropType} listing in {woreda} matches your request."
            : $"A new {cropType} listing matches your request.";
        var message = BuildFcmMessage(title, body, intentId, cropId);

        foreach (var endpointArn in endpoints)
        {
            try
            {
                await _sns.PublishAsync(new PublishRequest
                {
                    TargetArn = endpointArn,
                    Message = message,
                    MessageStructure = "json",
                });
            }
            catch (EndpointDisabledException)
            {
                // The device unregistered/uninstalled — prune the dead endpoint so
                // future crops don't keep re-attempting (and re-logging) it.
                await PruneEndpointAsync(buyerId, endpointArn, context);
            }
            catch (NotFoundException)
            {
                // Endpoint no longer exists in SNS — prune the stale row.
                await PruneEndpointAsync(buyerId, endpointArn, context);
            }
            catch (Exception ex)
            {
                // A transient/other failure must not stop other buyers' alerts.
                context.Logger.LogError(
                    $"Publish failed for an endpoint of buyer {buyerId}: [{ex.GetType().Name}]");
            }
        }
    }

    /// <summary>
    /// Delete a dead SNS endpoint registration so it self-heals. Best-effort: a
    /// failure to prune is logged and swallowed (it must not abort the alert fan-out).
    /// </summary>
    private async Task PruneEndpointAsync(string buyerId, string endpointArn, ILambdaContext context)
    {
        try
        {
            await _deviceTokenRepo.DeleteAsync(buyerId, endpointArn);
            context.Logger.LogInformation($"Pruned disabled push endpoint for buyer {buyerId}.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to prune endpoint for buyer {buyerId}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// Build the SNS message-structure JSON for an FCM (GCM platform) push: the
    /// outer envelope's "GCM" value is the FCM payload encoded as a JSON string.
    /// JSON-escaping is delegated to System.Text.Json via <see cref="Quote"/>.
    /// </summary>
    private static string BuildFcmMessage(string title, string body, string intentId, string cropId)
    {
        var fcm =
            $"{{\"notification\":{{\"title\":{Quote(title)},\"body\":{Quote(body)}}},"
            + $"\"data\":{{\"intentId\":{Quote(intentId)},\"cropId\":{Quote(cropId)}}}}}";
        return $"{{\"GCM\":{Quote(fcm)}}}";
    }

    /// <summary>Serialize a string to a quoted, escaped JSON string literal.</summary>
    private static string Quote(string value)
        => JsonSerializer.Serialize(value, AppJsonContext.Default.String);

    // ── DynamoDB-JSON attribute readers (stream NewImage shape: {"key":{"S":..}}) ──

    private static string GetString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>The record's DynamoDB sequence number — the partial-batch failure id.</summary>
    private static string GetSequenceNumber(JsonElement record)
        => record.TryGetProperty("dynamodb", out var d)
           && d.TryGetProperty("SequenceNumber", out var s)
           && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : "";

    private static string? AttrS(JsonElement image, string key)
        => image.TryGetProperty(key, out var attr) && attr.TryGetProperty("S", out var s)
            ? s.GetString()
            : null;

    private static double? AttrN(JsonElement image, string key)
    {
        if (image.TryGetProperty(key, out var attr) && attr.TryGetProperty("N", out var n)
            && double.TryParse(n.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return null;
    }

    // ── DynamoDB item (FromItem) readers: numbers come back as strings ──

    private static string GetItemString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v.ToString() ?? "" : "";

    private static string? GetItemStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v.ToString() : null;

    private static double? ItemDouble(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v)
            && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}

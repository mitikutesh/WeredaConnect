using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

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

    public async Task ProcessStreamAsync(Stream input, ILambdaContext context)
    {
        using var doc = await JsonDocument.ParseAsync(input);
        if (!doc.RootElement.TryGetProperty("Records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Cache endpoint lookups so multiple matching intents for one buyer
        // don't re-query device tokens.
        var endpointsByBuyer = new Dictionary<string, List<string>>();

        foreach (var record in records.EnumerateArray())
        {
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
                // Never let one bad record block the shard; log and continue.
                context.Logger.LogError($"Stream record error: [{ex.GetType().Name}]");
            }
        }
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
            CreatedAt = DateTime.UtcNow,
        };
        var cropId = AttrS(image, "cropId") ?? "";
        var woreda = AttrS(image, "woreda") ?? crop.LocationName ?? "";
        var now = DateTime.UtcNow;

        // A new crop of type X can satisfy intents wanting X or wanting a type for
        // which X is an accepted substitute (the substitute map is symmetric).
        var seenIntents = new HashSet<string>();

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
            catch (Exception ex)
            {
                // A stale/disabled endpoint must not stop other buyers' alerts.
                context.Logger.LogError(
                    $"Publish failed for an endpoint of buyer {buyerId}: [{ex.GetType().Name}]");
            }
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

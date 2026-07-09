using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// DynamoDB Streams consumer for the messages table. On each newly inserted
/// message it looks up the recipient's registered device endpoints and publishes
/// an FCM push (via SNS) carrying deep-link data for the conversation thread.
///
/// Mirrors <see cref="CropMatchStreamFunction"/> but simpler: the recipient is
/// read straight off the NEW_IMAGE — there is no scoring or intent query.
///
/// AOT note: the handler takes the raw event <see cref="Stream"/> and parses it
/// with <see cref="JsonDocument"/> — the typed DynamoDBEvent (which nests the
/// SDK AttributeValue type) is not friendly to the source-gen serializer.
/// </summary>
public class MessageStreamFunction
{
    private readonly IDeviceTokenRepository _deviceTokenRepo;
    private readonly IAmazonSimpleNotificationService _sns;

    /// <summary>Max characters of the message body shown in the notification.</summary>
    private const int PreviewLength = 120;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public MessageStreamFunction()
    {
        var deviceTokensTable = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                                ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _deviceTokenRepo = new DeviceTokenRepository(dynamoDb, deviceTokensTable);
        _sns = new AmazonSimpleNotificationServiceClient();
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public MessageStreamFunction(
        IDeviceTokenRepository deviceTokenRepo,
        IAmazonSimpleNotificationService sns)
    {
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
    /// Core batch processing. Each record is handled independently; a record that
    /// throws is reported by its DynamoDB sequence number so Lambda retries from
    /// it (at-least-once delivery) instead of silently dropping it. Exposed for
    /// unit tests.
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

        // Cache endpoint lookups so a burst of messages to one recipient doesn't
        // re-query device tokens per record.
        var endpointsByUser = new Dictionary<string, List<string>>();

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

                await ProcessNewMessageAsync(newImage, endpointsByUser, context);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Stream record error: [{ex.GetType().Name}]");
                if (sequenceNumber.Length > 0)
                {
                    failures.Add(new StreamBatchItemFailure { ItemIdentifier = sequenceNumber });
                }
            }
        }

        return new StreamBatchResponse { BatchItemFailures = failures };
    }

    private async Task ProcessNewMessageAsync(
        JsonElement image,
        Dictionary<string, List<string>> endpointsByUser,
        ILambdaContext context)
    {
        var recipientId = AttrS(image, "recipientId") ?? "";
        if (recipientId.Length == 0)
        {
            return;
        }

        if (!endpointsByUser.TryGetValue(recipientId, out var endpoints))
        {
            endpoints = await _deviceTokenRepo.GetEndpointArnsByUserAsync(recipientId);
            endpointsByUser[recipientId] = endpoints;
        }

        if (endpoints.Count == 0)
        {
            return; // recipient has no registered device — in-app badge covers them
        }

        var senderName = AttrS(image, "senderName") ?? "New message";
        var body = AttrS(image, "body") ?? "";
        var preview = body.Length > PreviewLength ? body[..PreviewLength] + "…" : body;

        var message = BuildFcmMessage(
            title: senderName,
            body: preview,
            conversationId: AttrS(image, "conversationId") ?? "",
            messageId: AttrS(image, "messageId") ?? "",
            senderId: AttrS(image, "senderId") ?? "",
            contextType: AttrS(image, "contextType") ?? "",
            contextId: AttrS(image, "contextId") ?? "");

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
                await PruneEndpointAsync(recipientId, endpointArn, context);
            }
            catch (NotFoundException)
            {
                await PruneEndpointAsync(recipientId, endpointArn, context);
            }
            catch (Exception ex)
            {
                // A transient/other failure must not stop other endpoints' pushes.
                context.Logger.LogError(
                    $"Publish failed for an endpoint of user {recipientId}: [{ex.GetType().Name}]");
            }
        }
    }

    /// <summary>
    /// Delete a dead SNS endpoint registration so it self-heals. Best-effort: a
    /// failure to prune is logged and swallowed (it must not abort the fan-out).
    /// </summary>
    private async Task PruneEndpointAsync(string userId, string endpointArn, ILambdaContext context)
    {
        try
        {
            await _deviceTokenRepo.DeleteAsync(userId, endpointArn);
            context.Logger.LogInformation($"Pruned disabled push endpoint for user {userId}.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to prune endpoint for user {userId}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// Build the SNS message-structure JSON for an FCM (GCM platform) push: the
    /// outer envelope's "GCM" value is the FCM payload encoded as a JSON string.
    /// The data block deep-links the tapped notification to the conversation.
    /// </summary>
    private static string BuildFcmMessage(
        string title, string body, string conversationId, string messageId,
        string senderId, string contextType, string contextId)
    {
        var fcm =
            $"{{\"notification\":{{\"title\":{Quote(title)},\"body\":{Quote(body)}}},"
            + $"\"data\":{{\"type\":{Quote("message")},"
            + $"\"conversationId\":{Quote(conversationId)},"
            + $"\"messageId\":{Quote(messageId)},"
            + $"\"senderId\":{Quote(senderId)},"
            + $"\"contextType\":{Quote(contextType)},"
            + $"\"contextId\":{Quote(contextId)}}}}}";
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
}

using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-21: DynamoDB Streams consumer for the orders, deliveries and escrows tables. Each record's
/// old and new images go through <see cref="NotificationRules"/>, and every derived
/// <see cref="NotificationEvent"/> becomes an inbox row (<c>{prefix}-notifications</c>, always) and
/// an FCM push through SNS (when the event asks for one), in the recipient's preferred language.
///
/// Delivery semantics (at-least-once stream, idempotent inbox):
/// - the inbox put is conditional on the sort key <c>{createdAt}#{type}#{entityId}</c>, so a retried
///   record writes nothing twice; a failed derive or inbox write is reported by sequence number and
///   the batch stops there, since Lambda retries from that record on (the records after it are not
///   pushed twice);
/// - the push is sent whenever the event asks for one, duplicate row or not (a retry may repeat a
///   push, FCM collapses it on the <c>tag</c>); a failed push is logged and never fails the record.
///
/// Mirrors <see cref="MessageStreamFunction"/>: raw <see cref="Stream"/> in, parsed with
/// <see cref="JsonDocument"/> (AOT), partial-batch response out.
/// </summary>
public class OrderNotifyStreamFunction
{
    /// <summary>How long an inbox row lives (the table's TTL reads <c>expiresAt</c>).</summary>
    public static readonly TimeSpan InboxRetention = TimeSpan.FromDays(90);

    /// <summary>FCM time to live for an informational push.</summary>
    public const int DefaultPushTtlSeconds = 86400;

    /// <summary>Shortest time to live for a push about a request that is about to close.</summary>
    public const int MinPushTtlSeconds = 60;

    /// <summary>FCM's longest time to live (four weeks).</summary>
    public const int MaxPushTtlSeconds = 2419200;

    /// <summary>Most devices one push goes to; device registration has no cap of its own.</summary>
    public const int MaxEndpointsPerUser = 10;

    /// <summary>
    /// Time left below which no more pushes are sent in this invocation (inbox rows still are), so a
    /// slow fan-out never times out the batch. <c>TestLambdaContext</c> reports zero: no limit.
    /// </summary>
    public static readonly TimeSpan PushTimeReserve = TimeSpan.FromSeconds(5);

    private const string FcmTtlAttribute = "AWS.SNS.MOBILE.FCM.TTL";

    private readonly IDeviceTokenRepository _deviceTokenRepo;
    private readonly IUserRepository _userRepo;
    private readonly INotificationRepository _notificationRepo;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The rules the function derives events with: <see cref="NotificationRules.Derive"/>. A test
    /// seam for cases no recorded image produces (an inbox-only event, a derive failure).
    /// </summary>
    internal Func<string, string, StreamImage, StreamImage, DateTime, IReadOnlyList<NotificationEvent>> Derive { get; set; }
        = NotificationRules.Derive;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public OrderNotifyStreamFunction()
    {
        var deviceTokensTable = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                                ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var notificationsTable = Environment.GetEnvironmentVariable("NOTIFICATIONS_TABLE")
                                 ?? throw new InvalidOperationException("NOTIFICATIONS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _deviceTokenRepo = new DeviceTokenRepository(dynamoDb, deviceTokensTable);
        _userRepo = new UserRepository(dynamoDb, usersTable);
        _notificationRepo = new NotificationRepository(dynamoDb, notificationsTable);
        _sns = new AmazonSimpleNotificationServiceClient();
        _clock = TimeProvider.System;
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public OrderNotifyStreamFunction(
        IDeviceTokenRepository deviceTokenRepo,
        IUserRepository userRepo,
        INotificationRepository notificationRepo,
        IAmazonSimpleNotificationService sns,
        TimeProvider? clock = null)
    {
        _deviceTokenRepo = deviceTokenRepo ?? throw new ArgumentNullException(nameof(deviceTokenRepo));
        _userRepo = userRepo ?? throw new ArgumentNullException(nameof(userRepo));
        _notificationRepo = notificationRepo ?? throw new ArgumentNullException(nameof(notificationRepo));
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Lambda entry point: <see cref="ProcessBatchAsync"/> serialized as the partial-batch response
    /// (raw Stream out, wired this way in Program.cs like the other stream consumers).
    /// </summary>
    public async Task<Stream> ProcessStreamAsync(Stream input, ILambdaContext context)
    {
        var response = await ProcessBatchAsync(input, context);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.StreamBatchResponse);
        return new MemoryStream(bytes);
    }

    /// <summary>
    /// Core batch processing, exposed for unit tests. Two passes: derive every record's events first
    /// (so the recipients' languages are one batched read), then write and push them record by
    /// record. A record fails, and is reported for retry, when its rules throw or an inbox row
    /// cannot be written; nothing on the push path fails a record.
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

        var work = new List<RecordWork>();
        foreach (var record in records.EnumerateArray())
        {
            var sequenceNumber = GetSequenceNumber(record);
            try
            {
                var derived = DeriveRecord(record, sequenceNumber);
                if (derived != null)
                {
                    work.Add(derived);
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Notification derive error: [{ex.GetType().Name}]");
                Fail(failures, sequenceNumber);
                break;
            }
        }

        if (work.Count == 0)
        {
            return new StreamBatchResponse { BatchItemFailures = failures };
        }

        var recipients = work
            .SelectMany(w => w.Events.Select(e => e.RecipientUserId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var languages = await LookupLanguagesAsync(recipients, context);

        // Endpoint lookups are cached across the batch: one order change tells the same two or
        // three people several things.
        var endpointsByUser = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var item in work)
        {
            try
            {
                foreach (var notification in item.Events)
                {
                    var sortKey = await WriteInboxAsync(notification, item.RecordTimeUtc, context);
                    if (!notification.Push)
                    {
                        continue;
                    }

                    var language = languages.TryGetValue(notification.RecipientUserId, out var preferred)
                                   && !string.IsNullOrEmpty(preferred)
                        ? preferred
                        : PushText.FallbackLanguage;

                    try
                    {
                        await PushAsync(notification, sortKey, language, endpointsByUser, context);
                    }
                    catch (Exception ex)
                    {
                        // The inbox row is written; the client pulls it. Never a reason to retry the record.
                        context.Logger.LogError(
                            $"Push failed for user {MoneyAlarmLog.LogId(notification.RecipientUserId)}: [{ex.GetType().Name}]");
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Notification inbox error: [{ex.GetType().Name}]");
                Fail(failures, item.SequenceNumber);
                break;
            }
        }

        return new StreamBatchResponse { BatchItemFailures = failures };
    }

    /// <summary>One stream record's derived events, or null when the record is not for this pipeline.</summary>
    private RecordWork? DeriveRecord(JsonElement record, string sequenceNumber)
    {
        var table = TableOf(GetString(record, "eventSourceARN"));
        if (table == null)
        {
            return null;
        }

        if (!record.TryGetProperty("dynamodb", out var dynamodb) || dynamodb.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var eventName = GetString(record, "eventName");
        // The record time is part of the inbox key, so it must be the same on every retry: a record
        // without one fails (and after its retries reaches the DLQ) rather than get a clock key.
        var recordTimeUtc = RecordTimeOf(dynamodb)
                            ?? throw new InvalidOperationException("Stream record has no ApproximateCreationDateTime.");
        var oldImage = new StreamImage(dynamodb.TryGetProperty("OldImage", out var oldElement) ? oldElement : null);
        var newImage = new StreamImage(dynamodb.TryGetProperty("NewImage", out var newElement) ? newElement : null);

        var events = Derive(table, eventName, oldImage, newImage, recordTimeUtc);
        return events.Count == 0 ? null : new RecordWork(sequenceNumber, events, recordTimeUtc);
    }

    /// <summary>
    /// The inbox row for one event, stamped with its write time (the incremental pull's index).
    /// Returns its sort key (the push's <c>notificationId</c>). A duplicate (a retried record) is
    /// logged with <c>new=false</c>, not an error. The <c>NOTIFY_EMITTED</c> line is the one the
    /// setup runbook and the contract (section 5) look for.
    /// </summary>
    private async Task<string> WriteInboxAsync(NotificationEvent notification, DateTime recordTimeUtc, ILambdaContext context)
    {
        var utc = DateTime.SpecifyKind(recordTimeUtc, DateTimeKind.Utc);
        var createdAt = utc.ToString("o", CultureInfo.InvariantCulture);
        var sortKey = $"{createdAt}#{notification.Type}#{notification.EntityId}";
        var expiresAt = new DateTimeOffset(utc).Add(InboxRetention).ToUnixTimeSeconds();

        var isNew = await _notificationRepo.TryPutAsync(new NotificationRow(
            notification.RecipientUserId,
            sortKey,
            notification.Type,
            notification.Entity,
            notification.EntityId,
            notification.OrderId,
            notification.Params,
            createdAt,
            expiresAt,
            _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture)));

        // Type and entity are server constants; the ids go through LogId like every other log line.
        context.Logger.LogInformation(
            $"NOTIFY_EMITTED type={notification.Type} entity={notification.Entity} "
            + $"entityId={MoneyAlarmLog.LogId(notification.EntityId)} recipient={MoneyAlarmLog.LogId(notification.RecipientUserId)} "
            + $"push={(notification.Push ? "true" : "false")} new={(isNew ? "true" : "false")}");
        return sortKey;
    }

    /// <summary>
    /// The FCM push for one event to every registered device of its recipient. Dead endpoints are
    /// pruned; any other publish failure is logged and the remaining endpoints still get theirs.
    /// </summary>
    private async Task PushAsync(
        NotificationEvent notification,
        string notificationId,
        string language,
        Dictionary<string, List<string>> endpointsByUser,
        ILambdaContext context)
    {
        var recipientId = notification.RecipientUserId;
        if (!endpointsByUser.TryGetValue(recipientId, out var endpoints))
        {
            endpoints = await _deviceTokenRepo.GetEndpointArnsByUserAsync(recipientId);
            endpointsByUser[recipientId] = endpoints;
        }

        if (endpoints.Count == 0)
        {
            return; // no registered device: the inbox row covers them on their next pull
        }

        var (title, body) = PushText.Render(notification.Type, language, notification.Params);
        var message = BuildFcmMessage(title, body, notification, notificationId);
        var ttlSeconds = PushTtlSeconds(notification, _clock.GetUtcNow().UtcDateTime);

        foreach (var endpointArn in endpoints.Take(MaxEndpointsPerUser))
        {
            if (context.RemainingTime > TimeSpan.Zero && context.RemainingTime < PushTimeReserve)
            {
                context.Logger.LogWarning(
                    $"Push skipped for user {MoneyAlarmLog.LogId(recipientId)}: invocation out of time");
                return;
            }

            try
            {
                await _sns.PublishAsync(new PublishRequest
                {
                    TargetArn = endpointArn,
                    Message = message,
                    MessageStructure = "json",
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        [FcmTtlAttribute] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = ttlSeconds.ToString(CultureInfo.InvariantCulture)
                        }
                    }
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
                context.Logger.LogError(
                    $"Publish failed for an endpoint of user {MoneyAlarmLog.LogId(recipientId)}: [{ex.GetType().Name}]");
            }
        }
    }

    /// <summary>Delete a dead SNS endpoint registration so it self-heals. Best effort.</summary>
    private async Task PruneEndpointAsync(string userId, string endpointArn, ILambdaContext context)
    {
        try
        {
            await _deviceTokenRepo.DeleteAsync(userId, endpointArn);
            context.Logger.LogInformation($"Pruned disabled push endpoint for user {MoneyAlarmLog.LogId(userId)}.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Failed to prune endpoint for user {MoneyAlarmLog.LogId(userId)}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// The recipients' preferred languages in one batched read. A failed lookup means every push in
    /// the batch goes out in <see cref="PushText.FallbackLanguage"/>, never a lost notification.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LookupLanguagesAsync(
        IReadOnlyCollection<string> userIds, ILambdaContext context)
    {
        try
        {
            return await _userRepo.GetPreferredLanguagesAsync(userIds);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Language lookup failed, pushing in {PushText.FallbackLanguage}: [{ex.GetType().Name}]");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// FCM time to live: until the deadline for an event that asks the recipient to act (at least
    /// <see cref="MinPushTtlSeconds"/>, so a push about a request closing right now still lands),
    /// <see cref="DefaultPushTtlSeconds"/> otherwise.
    /// </summary>
    internal static int PushTtlSeconds(NotificationEvent notification, DateTime nowUtc)
    {
        if (notification.DeadlineUtc is { } deadline && NotificationTypes.ActionRequired.Contains(notification.Type))
        {
            var seconds = (long)Math.Ceiling((deadline - nowUtc).TotalSeconds);
            return (int)Math.Clamp(seconds, MinPushTtlSeconds, MaxPushTtlSeconds);
        }

        return DefaultPushTtlSeconds;
    }

    /// <summary>
    /// The SNS message-structure JSON for an FCM (GCM platform) push, built exactly like
    /// <see cref="MessageStreamFunction"/>: the outer envelope's <c>GCM</c> value is the FCM payload
    /// as a JSON string. The <c>tag</c> collapses repeated pushes about one order in the tray; the
    /// data block deep-links the tap to the order and names the inbox row.
    /// </summary>
    internal static string BuildFcmMessage(string title, string body, NotificationEvent notification, string notificationId)
    {
        // No order known (an escrow or delivery row without one): no tag to collapse on and no
        // orderId, so the client does not try to open /orders/.
        var orderId = notification.OrderId;
        var tag = string.IsNullOrEmpty(orderId) ? string.Empty : $",\"tag\":{Quote("order-" + orderId)}";
        var orderField = string.IsNullOrEmpty(orderId) ? string.Empty : $"\"orderId\":{Quote(orderId)},";

        var fcm =
            $"{{\"notification\":{{\"title\":{Quote(title)},\"body\":{Quote(body)}{tag}}},"
            + $"\"data\":{{\"type\":{Quote("order")},"
            + $"\"event\":{Quote(notification.Type)},"
            + orderField
            + $"\"entityId\":{Quote(notification.EntityId)},"
            + $"\"entity\":{Quote(notification.Entity)},"
            + $"\"notificationId\":{Quote(notificationId)}}}}}";
        return $"{{\"GCM\":{Quote(fcm)}}}";
    }

    /// <summary>
    /// Which rules table a record comes from, by the table name in its <c>eventSourceARN</c>
    /// (<c>arn:aws:dynamodb:...:table/{prefix}-orders/stream/...</c>): <c>orders</c>,
    /// <c>deliveries</c>, <c>escrows</c>, or null for any other source (skipped).
    /// </summary>
    internal static string? TableOf(string eventSourceArn)
    {
        var name = eventSourceArn;
        var tableStart = name.IndexOf("table/", StringComparison.Ordinal);
        if (tableStart >= 0)
        {
            name = name[(tableStart + "table/".Length)..];
            var tableEnd = name.IndexOf('/');
            if (tableEnd >= 0)
            {
                name = name[..tableEnd];
            }
        }

        if (name.EndsWith("-orders", StringComparison.Ordinal))
        {
            return "orders";
        }

        if (name.EndsWith("-deliveries", StringComparison.Ordinal))
        {
            return "deliveries";
        }

        if (name.EndsWith("-escrows", StringComparison.Ordinal))
        {
            return "escrows";
        }

        return null;
    }

    /// <summary>
    /// The record's <c>ApproximateCreationDateTime</c> (epoch seconds, possibly fractional, a number
    /// in the Lambda event) as UTC, or null when absent.
    /// </summary>
    internal static DateTime? RecordTimeOf(JsonElement dynamodb)
    {
        if (!dynamodb.TryGetProperty("ApproximateCreationDateTime", out var time))
        {
            return null;
        }

        if (time.ValueKind == JsonValueKind.Number && time.TryGetDouble(out var seconds))
        {
            return DateTime.UnixEpoch.AddSeconds(seconds);
        }

        if (time.ValueKind == JsonValueKind.String
            && double.TryParse(time.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            return DateTime.UnixEpoch.AddSeconds(seconds);
        }

        return null;
    }

    private static void Fail(List<StreamBatchItemFailure> failures, string sequenceNumber)
    {
        if (sequenceNumber.Length > 0)
        {
            failures.Add(new StreamBatchItemFailure { ItemIdentifier = sequenceNumber });
        }
    }

    /// <summary>Serialize a string to a quoted, escaped JSON string literal.</summary>
    private static string Quote(string value)
        => JsonSerializer.Serialize(value, AppJsonContext.Default.String);

    private static string GetString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>The record's DynamoDB sequence number, the partial-batch failure id.</summary>
    private static string GetSequenceNumber(JsonElement record)
        => record.TryGetProperty("dynamodb", out var d)
           && d.TryGetProperty("SequenceNumber", out var s)
           && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? ""
            : "";

    /// <summary>One record's derived events, kept between the derive pass and the write pass.</summary>
    private sealed record RecordWork(string SequenceNumber, IReadOnlyList<NotificationEvent> Events, DateTime RecordTimeUtc);
}

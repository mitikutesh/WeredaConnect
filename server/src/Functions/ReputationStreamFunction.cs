using Amazon.DynamoDBv2;
using Amazon.SQS;
using Amazon.SQS.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-28: the second consumer of the orders table stream (reputation-architecture.md section 7).
/// Each record's old and new images become at most one reputation event per party and slot,
/// recorded by <see cref="ReputationService"/> with the aggregate in one transaction.
///
/// Delivery semantics:
/// - <c>reputationPenalties</c> off: every record is acknowledged without a write (no retroactive
///   points, spec 4.11), logged once per container as <c>REPUTATION_STREAM_DISABLED</c>;
/// - a record whose handling throws is parked in SQS (<c>{prefix}-reputation-parked</c>, only the
///   attributes the derivation reads) and reported as processed, so its shard moves on; the
///   order-cron's hourly <c>reputation_unpark</c> replays it. Only when parking fails too is the
///   record reported as a batch item failure (stream retries, then the metadata DLQ);
/// - replays are safe: a slot that exists is never written twice.
///
/// Raw <see cref="Stream"/> in and out, parsed with <see cref="JsonDocument"/> (AOT), like the
/// other stream consumers.
/// </summary>
public class ReputationStreamFunction
{
    public const string DisabledToken = "REPUTATION_STREAM_DISABLED";
    public const string ParkedToken = "REPUTATION_PARKED";
    public const string ParkFailedToken = "REPUTATION_PARK_FAILED";

    private static bool _disabledLogged;

    private readonly ReputationRecordProcessor? _processor;
    private readonly IAmazonSQS? _sqs;
    private readonly string? _parkedQueueUrl;
    private readonly bool _penaltiesEnabled;

    /// <summary>Default constructor for the Lambda runtime. With the flag off nothing is built.</summary>
    public ReputationStreamFunction()
    {
        _penaltiesEnabled = ReputationFlags.PenaltiesEnabled;
        if (!_penaltiesEnabled)
        {
            return;
        }

        var eventsTable = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE")
                          ?? throw new InvalidOperationException("REPUTATION_EVENTS_TABLE is not set.");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var deviceTokensTable = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                                ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE");

        var dynamoDb = new AmazonDynamoDBClient();
        var settings = ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line));
        var fraud = string.IsNullOrEmpty(fraudSignalsTable)
            ? null
            : new FraudDetectionService(dynamoDb, "", fraudSignalsTable, new RateLimitRepository(dynamoDb, ""));
        var service = new ReputationService(
            new ReputationEventRepository(dynamoDb, eventsTable, usersTable), settings, fraud,
            copies: ReputationCopyRepository.FromEnvironment(dynamoDb));
        _processor = new ReputationRecordProcessor(
            service, new DeviceReachability(dynamoDb, deviceTokensTable).HasDeviceAsync, ReputationFlags.TimeoutPenaltiesEnabled);
        _parkedQueueUrl = Environment.GetEnvironmentVariable("REPUTATION_PARKED_QUEUE_URL");
        _sqs = string.IsNullOrEmpty(_parkedQueueUrl) ? null : new AmazonSQSClient();
    }

    /// <summary>Constructor for testing.</summary>
    public ReputationStreamFunction(
        ReputationRecordProcessor? processor, IAmazonSQS? sqs, string? parkedQueueUrl, bool penaltiesEnabled)
    {
        _processor = processor;
        _sqs = sqs;
        _parkedQueueUrl = parkedQueueUrl;
        _penaltiesEnabled = penaltiesEnabled;
    }

    /// <summary>Lambda entry point: <see cref="ProcessBatchAsync"/> as the partial-batch response.</summary>
    public async Task<Stream> ProcessStreamAsync(Stream input, ILambdaContext context)
    {
        var response = await ProcessBatchAsync(input, context);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.StreamBatchResponse);
        return new MemoryStream(bytes);
    }

    /// <summary>
    /// Core batch processing, exposed for unit tests. A record that throws is parked; when parking
    /// fails it is reported by sequence number and the batch stops there (Lambda retries from it).
    /// </summary>
    public async Task<StreamBatchResponse> ProcessBatchAsync(Stream input, ILambdaContext context)
    {
        var failures = new List<StreamBatchItemFailure>();
        if (!_penaltiesEnabled || _processor == null)
        {
            if (!_disabledLogged)
            {
                context.Logger.LogInformation($"{DisabledToken}: reputationPenalties is off; records are acknowledged without a write.");
                _disabledLogged = true;
            }

            return new StreamBatchResponse();
        }

        using var doc = await JsonDocument.ParseAsync(input);
        if (!doc.RootElement.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return new StreamBatchResponse();
        }

        foreach (var record in records.EnumerateArray())
        {
            var sequence = SequenceOf(record);
            try
            {
                await _processor.HandleRecordAsync(record, context.Logger);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Reputation record failed: [{ex.GetType().Name}]");
                if (await TryParkAsync(record, context))
                {
                    continue;
                }

                if (sequence.Length > 0)
                {
                    failures.Add(new StreamBatchItemFailure { ItemIdentifier = sequence });
                }

                break;
            }
        }

        return new StreamBatchResponse { BatchItemFailures = failures };
    }

    /// <summary>Park a failed record in SQS with projected images; false when that is not possible.</summary>
    private async Task<bool> TryParkAsync(JsonElement record, ILambdaContext context)
    {
        if (_sqs == null || string.IsNullOrEmpty(_parkedQueueUrl))
        {
            context.Logger.LogError($"{ParkFailedToken} reason=unconfigured");
            return false;
        }

        var message = ReputationParking.BuildMessage(record);
        if (message == null)
        {
            context.Logger.LogError($"{ParkFailedToken} reason=too_large");
            return false;
        }

        try
        {
            await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = _parkedQueueUrl, MessageBody = message });
            context.Logger.LogWarning($"{ParkedToken} bytes={message.Length}");
            return true;
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"{ParkFailedToken} reason=send [{ex.GetType().Name}]");
            return false;
        }
    }

    private static string SequenceOf(JsonElement record)
        => record.TryGetProperty("dynamodb", out var d)
           && d.TryGetProperty("SequenceNumber", out var s)
           && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Test seam: forget that the disabled line was logged.</summary>
    internal static void ResetForTests() => _disabledLogged = false;
}

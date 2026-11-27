using System.Text;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-28: turns one orders stream record into recorded reputation events (reputation-architecture.md
/// section 7): the derivation of 7.2, the reachability read of 7.3 for an expiry while
/// <c>timeoutPenalties</c> is on, then <see cref="ReputationService.RecordAsync"/> per candidate.
/// The stream function runs it on live records and the order-cron's <c>reputation_unpark</c> on
/// parked ones (same JSON shape), so a replay is the same code path. Replays are safe by the slot ids.
/// </summary>
public sealed class ReputationRecordProcessor
{
    private readonly ReputationService _service;
    private readonly Func<string, Task<bool>> _isReachable;
    private readonly bool _timeoutPenaltiesEnabled;

    public ReputationRecordProcessor(ReputationService service, Func<string, Task<bool>> isReachable, bool timeoutPenaltiesEnabled)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _isReachable = isReachable ?? throw new ArgumentNullException(nameof(isReachable));
        _timeoutPenaltiesEnabled = timeoutPenaltiesEnabled;
    }

    /// <summary>
    /// Record the events of one stream record (a live one or a parked one). Anything that throws
    /// (a throttle, a failed device read, a version conflict that never settles) propagates, so the
    /// caller parks or keeps the record. Returns how many candidates the record yielded.
    /// </summary>
    public async Task<int> HandleRecordAsync(JsonElement record, ILambdaLogger logger)
    {
        if (!record.TryGetProperty("eventName", out var eventName) || eventName.GetString() != "MODIFY"
            || !record.TryGetProperty("dynamodb", out var dynamodb) || dynamodb.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var oldImage = new StreamImage(dynamodb.TryGetProperty("OldImage", out var o) ? o : null);
        var newImage = new StreamImage(dynamodb.TryGetProperty("NewImage", out var n) ? n : null);
        var sequence = dynamodb.TryGetProperty("SequenceNumber", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        var candidates = ReputationDerivation.Derive(
            oldImage, newImage, OrderNotifyStreamFunction.RecordTimeOf(dynamodb), sequence);
        foreach (var derived in candidates)
        {
            var candidate = derived;
            if (candidate.Code == ReputationCodes.ResponseExpired && _timeoutPenaltiesEnabled)
            {
                candidate = candidate with { Reachable = await _isReachable(candidate.UserId) };
            }

            await _service.RecordAsync(candidate, _timeoutPenaltiesEnabled, logger);
        }

        return candidates.Count;
    }
}

/// <summary>
/// ESC-28: the SQS message a failed record is parked as (reputation-architecture.md 7.5). It keeps
/// the record's shape (so the replay reads it like a live record) but only the attributes the
/// derivation reads from each image (<see cref="ReputationDerivation.ProjectedAttributes"/>): two
/// whole order images could pass SQS's 256 KB limit, the projection is well under 2 KB.
/// </summary>
public static class ReputationParking
{
    /// <summary>SQS's message size limit.</summary>
    public const int MaxMessageBytes = 256 * 1024;

    /// <summary>The parked message's JSON, or null when even the projection does not fit (not expected).</summary>
    public static string? BuildMessage(JsonElement record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            CopyString(writer, record, "eventID");
            CopyString(writer, record, "eventName");
            CopyString(writer, record, "eventSourceARN");
            writer.WritePropertyName("dynamodb");
            writer.WriteStartObject();
            if (record.TryGetProperty("dynamodb", out var dynamodb) && dynamodb.ValueKind == JsonValueKind.Object)
            {
                CopyRaw(writer, dynamodb, "Keys");
                CopyRaw(writer, dynamodb, "SequenceNumber");
                CopyRaw(writer, dynamodb, "ApproximateCreationDateTime");
                CopyImage(writer, dynamodb, "OldImage");
                CopyImage(writer, dynamodb, "NewImage");
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        return bytes.Length <= MaxMessageBytes ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static void CopyImage(Utf8JsonWriter writer, JsonElement dynamodb, string name)
    {
        if (!dynamodb.TryGetProperty(name, out var image) || image.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var attribute in ReputationDerivation.ProjectedAttributes)
        {
            CopyRaw(writer, image, attribute);
        }

        writer.WriteEndObject();
    }

    private static void CopyRaw(Utf8JsonWriter writer, JsonElement source, string name)
    {
        if (source.TryGetProperty(name, out var value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static void CopyString(Utf8JsonWriter writer, JsonElement source, string name)
    {
        if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            writer.WriteString(name, value.GetString());
        }
    }
}

namespace WeredaConnect.Server.Models;

/// <summary>
/// AWS Lambda partial-batch-response for a DynamoDB stream source. Returning the
/// sequence numbers of failed records makes Lambda redeliver the batch from the
/// earliest reported failure to its end (rather than checkpointing past it and
/// permanently dropping a buyer's push alert on a transient fault). Note this is
/// at-least-once: records after the earliest failure may be reprocessed, so a
/// duplicate push is possible.
///
/// Serialized via the source-gen serializer, so the exact JSON shape the Lambda
/// runtime expects (<c>{"batchItemFailures":[{"itemIdentifier":"..."}]}</c>) is
/// pinned with explicit <see cref="JsonPropertyNameAttribute"/>s rather than the
/// context-wide camelCase policy.
/// </summary>
public sealed record StreamBatchResponse
{
    [JsonPropertyName("batchItemFailures")]
    public List<StreamBatchItemFailure> BatchItemFailures { get; init; } = new();
}

/// <summary>A single failed stream record, identified by its sequence number.</summary>
public sealed record StreamBatchItemFailure
{
    [JsonPropertyName("itemIdentifier")]
    public required string ItemIdentifier { get; init; }
}

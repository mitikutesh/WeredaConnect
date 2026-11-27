namespace WeredaConnect.Server.Models;

/// <summary>
/// SIM-56: one of the caller's own buying requests, as <c>GET /intents</c> returns it
/// (push-alerts-api.md 1.5.0). The fields the app synced with the <c>buyer_intent</c> entity,
/// plus the server's timestamps and version. The buyer's id is not repeated: every row is the
/// caller's.
/// </summary>
public sealed record IntentDto
{
    public required string IntentId { get; init; }
    public required string CropType { get; init; }

    /// <summary>Quintals, as the app sent them.</summary>
    public required double QuantityNeeded { get; init; }
    public required double MaxPricePerKg { get; init; }
    public required string DeliveryWoreda { get; init; }
    public double? DeliveryLatitude { get; init; }
    public double? DeliveryLongitude { get; init; }
    public string? NeededByDate { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public required int Version { get; init; }
}

/// <summary>Response body for <c>GET /intents</c>: the caller's active requests, newest first.</summary>
public sealed record ListIntentsResponse
{
    public required List<IntentDto> Intents { get; init; }
    public required int Count { get; init; }
}

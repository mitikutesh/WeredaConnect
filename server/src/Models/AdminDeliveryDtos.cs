namespace WeredaConnect.Server.Models;

/// <summary>
/// FUP-19: POST /admin/deliveries/{id}/cancel body. Cancels a delivery that was picked up and never delivered, once
/// the money behind it is decided (delivery-verification-api.md 2.9.0).
/// </summary>
public sealed record AdminDeliveryCancelRequest
{
    /// <summary>Why the delivery is cancelled: 10 to 500 characters after trimming. Kept in the audit row only.</summary>
    public string? Reason { get; init; }

    /// <summary>The delivery version the page read; the cancel is refused if the delivery moved since.</summary>
    public int? ExpectedVersion { get; init; }
}

/// <summary>FUP-19: the answer of an admin delivery cancel.</summary>
public sealed record AdminDeliveryCancelResponse
{
    public required string DeliveryId { get; init; }
    public string? OrderId { get; init; }

    /// <summary><c>pickedUp</c> or <c>inTransit</c>: the status the cancel moved from; null on a replay.</summary>
    public string? FromStatus { get; init; }

    /// <summary><c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary><c>admin_cancelled</c>.</summary>
    public required string CancelReason { get; init; }

    public string? CancelledAt { get; init; }

    /// <summary>The delivery version now.</summary>
    public int Version { get; init; }

    /// <summary>True when this cancel was applied before: nothing was written by this call.</summary>
    public bool AlreadyCancelled { get; init; }

    public string? Message { get; init; }
}

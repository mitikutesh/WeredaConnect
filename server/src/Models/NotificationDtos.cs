namespace WeredaConnect.Server.Models;

/// <summary>ESC-21: one inbox entry as <c>GET /notifications</c> returns it (notifications-api.md).</summary>
public sealed record NotificationDto
{
    /// <summary>Stable id; the client drops an entry whose id it already has.</summary>
    public required string NotificationId { get; init; }

    /// <summary>One of the notification types (notifications-api.md); the client picks the text by it.</summary>
    public required string Type { get; init; }

    /// <summary><c>order</c>, <c>delivery</c> or <c>escrow</c>.</summary>
    public required string Entity { get; init; }

    public required string EntityId { get; init; }

    /// <summary>The order to open on tap, when known.</summary>
    public string? OrderId { get; init; }

    /// <summary>Plain-text parameters for the text (names, kg, ETB, ISO deadline, reason code). Never a phone number.</summary>
    public required Dictionary<string, string> Params { get; init; }

    /// <summary>ISO 8601 UTC time of the change that produced the entry.</summary>
    public required string CreatedAt { get; init; }
}

/// <summary>Response of <c>GET /notifications</c>.</summary>
public sealed record GetNotificationsResponse
{
    public required List<NotificationDto> Notifications { get; init; }

    /// <summary>Opaque cursor for the next page; absent on the last page.</summary>
    public string? NextToken { get; init; }

    /// <summary>ISO server time this read started; the client sends it back as <c>since</c> on its next pull.</summary>
    public required string Cursor { get; init; }
}

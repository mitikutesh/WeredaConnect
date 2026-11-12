namespace WeredaConnect.Server.Models;

/// <summary>ESC-29: the body of <c>GET /reputation/me</c> and <c>GET /reputation/{userId}</c> (reputation-api.md sections 2 and 3).</summary>
public sealed record GetReputationResponse
{
    public required ReputationDto Reputation { get; init; }
}

/// <summary>
/// ESC-29: a reputation as returned. The party's own view (section 2) fills every field that has
/// a value; the public summary (section 3) only the ones buyers may see, with the public tier.
/// A field with no value is left out.
/// </summary>
public sealed record ReputationDto
{
    public required string UserId { get; init; }
    public required string Role { get; init; }

    /// <summary>Own view: the internal tier. Public summary: <c>good</c> or <c>low</c>, absent with public tags off.</summary>
    public string? Tier { get; init; }

    public int? ActivePoints { get; init; }
    public ReputationThresholdsDto? Thresholds { get; init; }
    public decimal? RatingAverage { get; init; }
    public long RatingCount { get; init; }
    public decimal? RatingDeduction { get; init; }
    public decimal? EffectiveRating { get; init; }
    public int? ReliabilityPct { get; init; }
    public int? CompletedOrders90d { get; init; }
    public int? FaultCancels90d { get; init; }
    public int? NoShows90d { get; init; }

    /// <summary>Public summary: completed plus fault cancels plus no-shows in 90 days.</summary>
    public int? TotalOrders90d { get; init; }

    public ReputationRestrictionDto? Restriction { get; init; }
    public ReputationOverrideDto? Override { get; init; }
    public string? NextPointExpiryAt { get; init; }

    /// <summary>Own view: the <c>reputationPenalties</c> flag.</summary>
    public bool? PenaltiesEnabled { get; init; }

    public required string AsOf { get; init; }
}

public sealed record ReputationThresholdsDto
{
    public required int Warning { get; init; }
    public required int Low { get; init; }
    public required int Restricted { get; init; }
}

public sealed record ReputationRestrictionDto
{
    public string? Since { get; init; }

    /// <summary>Absent while an admin review is open.</summary>
    public string? Until { get; init; }

    public required string Cause { get; init; }
}

/// <summary>An admin tag in force; the admin's id is never shown to the party.</summary>
public sealed record ReputationOverrideDto
{
    public required string Tag { get; init; }
    public string? Until { get; init; }
    public string? Reason { get; init; }
}

/// <summary>ESC-29: the body of <c>GET /reputation/me/events</c> (reputation-api.md section 4).</summary>
public sealed record GetReputationEventsResponse
{
    public required List<ReputationEventDto> Events { get; init; }
    public string? NextToken { get; init; }
}

/// <summary>One event as its party sees it: never the buyer's id, the base points or the appeal text of another reader.</summary>
public sealed record ReputationEventDto
{
    public required string EventId { get; init; }
    public required string Code { get; init; }
    public int Points { get; init; }
    public string? ZeroReason { get; init; }
    public int EarnedBack { get; init; }
    public int RemainingPoints { get; init; }
    public required string Status { get; init; }
    public string? OccurredAt { get; init; }
    public string? RecordedAt { get; init; }
    public string? PointsExpireAt { get; init; }
    public string? OrderId { get; init; }
    public string? TripId { get; init; }
    public string? EarnBackEventId { get; init; }
    public required ReputationEventAppealDto Appeal { get; init; }
}

public sealed record ReputationEventAppealDto
{
    /// <summary><c>none</c>, <c>pending</c>, <c>review</c>, <c>excused</c> or <c>upheld</c>.</summary>
    public required string Status { get; init; }

    public bool CanAppeal { get; init; }
    public string? DeadlineAt { get; init; }
    public string? DecidedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>ESC-29: the body of <c>POST /reputation/appeals/photo-url</c> (section 5).</summary>
public sealed record ReputationAppealPhotoRequest
{
    public long? SizeBytes { get; init; }
    public string? ContentType { get; init; }
}

public sealed record ReputationAppealPhotoResponse
{
    public required string UploadUrl { get; init; }
    public required string PhotoRef { get; init; }
    public required int ExpiresInSeconds { get; init; }
}

/// <summary>ESC-29: the body of <c>POST /reputation/events/{eventId}/appeal</c> (section 6).</summary>
public sealed record ReputationAppealRequest
{
    public string? Text { get; init; }
    public string? PhotoRef { get; init; }
}

public sealed record ReputationAppealResponse
{
    public required ReputationAppealStateDto Appeal { get; init; }
    public required ReputationEventDto Event { get; init; }
    public bool AlreadyAppealed { get; init; }
}

public sealed record ReputationAppealStateDto
{
    public required string Status { get; init; }
    public string? AppealedAt { get; init; }
    public bool HasPhoto { get; init; }
}

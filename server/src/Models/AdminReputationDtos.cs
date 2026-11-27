namespace WeredaConnect.Server.Models;

/// <summary>ESC-32: the body of <c>GET /admin/users/{userId}/reputation</c> (reputation-api.md 9.1).</summary>
public sealed record AdminReputationResponse
{
    /// <summary>Section 2's fields, plus <c>override.by</c>.</summary>
    public required ReputationDto Reputation { get; init; }

    /// <summary>Every restriction row the table still holds (current and past), newest first.</summary>
    public required List<AdminReputationRestrictionDto> Restrictions { get; init; }

    /// <summary>One page of the user's rows, newest first: every code, system rows included (not the <c>HEAL</c> markers).</summary>
    public required List<AdminReputationEventDto> Events { get; init; }

    public string? NextToken { get; init; }
}

public sealed record AdminReputationRestrictionDto
{
    public required string EventId { get; init; }

    /// <summary><c>points</c>, <c>delivery_overdue</c> or <c>admin</c>.</summary>
    public required string Cause { get; init; }

    /// <summary><c>active</c>, <c>ended</c> or <c>lifted</c>; an active row whose end has passed reads <c>ended</c>.</summary>
    public required string Status { get; init; }

    public string? Since { get; init; }

    /// <summary>Absent while an admin review is open.</summary>
    public string? Until { get; init; }

    public string? Reason { get; init; }
    public string? By { get; init; }
    public string? LiftedAt { get; init; }
    public string? LiftedBy { get; init; }
}

/// <summary>One row as an admin sees it: the party's fields plus the base points, the buyer and the whole appeal.</summary>
public sealed record AdminReputationEventDto
{
    public required string EventId { get; init; }
    public required string Code { get; init; }
    public string? Party { get; init; }
    public int Points { get; init; }
    public int BasePoints { get; init; }
    public string? ZeroReason { get; init; }
    public int EarnedBack { get; init; }
    public int RemainingPoints { get; init; }
    public required string Status { get; init; }
    public string? OccurredAt { get; init; }
    public string? RecordedAt { get; init; }
    public string? PointsExpireAt { get; init; }
    public string? OrderId { get; init; }
    public string? TripId { get; init; }
    public string? BuyerId { get; init; }
    public string? EarnBackEventId { get; init; }

    /// <summary>Present on penalty and completion rows; absent on system rows.</summary>
    public AdminReputationAppealDto? Appeal { get; init; }

    // ----- system rows -----
    public string? Cause { get; init; }
    public string? Since { get; init; }
    public string? Until { get; init; }
    public string? Tag { get; init; }
    public string? Reason { get; init; }
    public string? By { get; init; }
    public string? FromTier { get; init; }
    public string? ToTier { get; init; }
    public string? RestrictedUntil { get; init; }
    public string? LiftedAt { get; init; }
    public string? LiftedBy { get; init; }
}

public sealed record AdminReputationAppealDto
{
    /// <summary><c>none</c>, <c>pending</c>, <c>review</c>, <c>excused</c> or <c>upheld</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Whether the party may still send an appeal text (section 6.1).</summary>
    public bool CanAppeal { get; init; }

    public string? Text { get; init; }
    public string? AppealedAt { get; init; }

    /// <summary>When the party's text arrived; absent with no text. An uphold sends it back as <c>seenAppealTextAt</c>.</summary>
    public string? TextAt { get; init; }

    public bool HasPhoto { get; init; }

    /// <summary>A pre-signed GET of the appeal photo, valid 10 minutes.</summary>
    public string? AppealPhotoUrl { get; init; }

    public string? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? Note { get; init; }
}

/// <summary>ESC-32: the body of <c>GET /admin/reputation/appeals</c> (reputation-api.md 9.2).</summary>
public sealed record AdminReputationAppealsResponse
{
    public required List<AdminReputationAppealItemDto> Appeals { get; init; }
    public string? NextToken { get; init; }
}

public sealed record AdminReputationAppealItemDto
{
    public required string UserId { get; init; }
    public string? Role { get; init; }
    public string? DisplayName { get; init; }
    public required string EventId { get; init; }
    public required string Code { get; init; }
    public int Points { get; init; }
    public string? OccurredAt { get; init; }
    public string? OrderId { get; init; }
    public string? TripId { get; init; }
    public required string AppealStatus { get; init; }
    public string? AppealedAt { get; init; }
    public string? Text { get; init; }

    /// <summary>When the party's text arrived; absent with no text. An uphold sends it back as <c>seenAppealTextAt</c>.</summary>
    public string? AppealTextAt { get; init; }

    public bool HasPhoto { get; init; }

    /// <summary>A pre-signed GET of the appeal photo, valid 10 minutes.</summary>
    public string? AppealPhotoUrl { get; init; }
}

/// <summary>ESC-32: the body of <c>POST /admin/users/{userId}/reputation/events/{eventId}/decision</c> (9.3).</summary>
public sealed record AdminReputationDecisionRequest
{
    /// <summary><c>excuse</c> or <c>uphold</c>.</summary>
    public string? Decision { get; init; }

    /// <summary>Required, 3 to 500 characters; the party sees it.</summary>
    public string? Note { get; init; }

    /// <summary>Only for an uphold of a <c>DELIVERY_OVERDUE</c> review: the restriction's end.</summary>
    public string? Until { get; init; }

    /// <summary>
    /// For an uphold: the <c>textAt</c> (or the queue's <c>appealTextAt</c>) of the appeal as the
    /// admin's page showed it; absent when it showed no text. A different stored value is
    /// <c>409 REPUTATION_APPEAL_CHANGED</c>. Ignored on an excuse.
    /// </summary>
    public string? SeenAppealTextAt { get; init; }
}

public sealed record AdminReputationDecisionResponse
{
    public required AdminReputationEventDto Event { get; init; }
    public required ReputationDto Reputation { get; init; }
    public bool AlreadyDecided { get; init; }
}

/// <summary>ESC-32: the body of <c>POST /admin/users/{userId}/reputation</c> (9.4).</summary>
public sealed record AdminReputationActionRequest
{
    /// <summary><c>tag</c>, <c>untag</c>, <c>restrict</c> or <c>lift</c>.</summary>
    public string? Action { get; init; }

    /// <summary>Required, 3 to 500 characters.</summary>
    public string? Reason { get; init; }

    /// <summary>Required for tag, untag and restrict; not taken by lift.</summary>
    public string? Until { get; init; }
}

public sealed record AdminReputationActionResponse
{
    public required ReputationDto Reputation { get; init; }

    /// <summary>True when the same action was already in force and nothing was written.</summary>
    public bool AlreadyApplied { get; init; }
}

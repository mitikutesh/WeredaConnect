namespace WeredaConnect.Server.Models;

/// <summary>
/// FRD-19 (fraud-detection-architecture.md section 21): the redacted audit timeline the advisory cron sends to the
/// AI reviewer Lambda. Built by <c>FraudAdvisory.BuildTimeline</c> from server values only: no user id, name, phone
/// number, Fayda id, entity id, description, metadata, IP address, location or amount. Entities are pseudonymous
/// labels (<c>order-1</c>), stable within one request only.
/// </summary>
public sealed record AdvisoryReviewRequest
{
    public required AdvisorySubject Subject { get; init; }

    /// <summary>The user's signal rows in the scoring window, newest first, at most 50.</summary>
    public required List<AdvisorySignal> Signals { get; init; }

    /// <summary>Transaction audit events of the signals' orders and deliveries, oldest first, at most 100.</summary>
    public required List<AdvisoryEvent> Events { get; init; }

    /// <summary>The scoring window (<c>RISK_WINDOW_DAYS</c>).</summary>
    public required int WindowDays { get; init; }

    /// <summary>More signal rows exist than were sent.</summary>
    public bool SignalsTruncated { get; init; }

    /// <summary>More events exist than were sent.</summary>
    public bool EventsTruncated { get; init; }
}

/// <summary>FRD-19: who is reviewed, without saying who: role, account age, Fayda flag and the rules' score.</summary>
public sealed record AdvisorySubject
{
    /// <summary><c>farmer</c>, <c>trucker</c>, <c>buyer</c> or <c>unknown</c>.</summary>
    public required string Role { get; init; }

    public int? AccountAgeDays { get; init; }
    public bool? FaydaVerified { get; init; }

    /// <summary>The tier of the rules alone: <c>high</c> or <c>critical</c> (only those users are reviewed).</summary>
    public required string RiskTier { get; init; }

    public required int Score { get; init; }
    public required int EnforcementScore { get; init; }

    /// <summary>The score read was truncated (section 14.3), which alone makes the tier CRITICAL.</summary>
    public bool ScoreTruncated { get; init; }
}

/// <summary>FRD-19: one signal row as the reviewer sees it.</summary>
public sealed record AdvisorySignal
{
    /// <summary>The row's time (the start of its UTC hour for the hour-keyed writer).</summary>
    public required string At { get; init; }

    public required string Type { get; init; }

    /// <summary><c>hard</c> or <c>soft</c> (section 15).</summary>
    public required string Class { get; init; }

    public required int Weight { get; init; }

    /// <summary><c>pending</c> or <c>reviewed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The admin's decision on a reviewed row: <c>approve</c>, <c>reject</c> or <c>whitelist</c>.</summary>
    public string? Resolution { get; init; }

    /// <summary>A pseudonymous label of the row's entity (<c>order-1</c>), the same label in <see cref="AdvisoryEvent.Entity"/>.</summary>
    public string? Entity { get; init; }
}

/// <summary>FRD-19: one transaction audit event as the reviewer sees it.</summary>
public sealed record AdvisoryEvent
{
    public required string At { get; init; }

    /// <summary>The pseudonymous label of the signal entity the event was read for.</summary>
    public required string Entity { get; init; }

    /// <summary>The kind of the entity the event is about (<c>order</c>, <c>delivery</c>, <c>escrow</c>).</summary>
    public required string EntityType { get; init; }

    public required string Action { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }

    /// <summary><c>self</c> (the reviewed user), <c>other</c> (another user), <c>admin</c> or <c>system</c>.</summary>
    public required string Actor { get; init; }

    public string? ActorRole { get; init; }
}

/// <summary>
/// FRD-19: the reviewer Lambda's answer. <see cref="Ok"/> with a <see cref="Concern"/> of <c>none</c>, <c>low</c>,
/// <c>medium</c> or <c>high</c> and an admin-facing <see cref="Explanation"/>; otherwise <see cref="Error"/> says why
/// (<c>disabled</c>, <c>invalid_input</c>, <c>refused</c>, <c>truncated</c>, <c>unparsable</c>). The advisory cron
/// validates it again before anything is written.
/// </summary>
public sealed record AdvisoryReviewReply
{
    public bool Ok { get; init; }
    public string? Concern { get; init; }
    public string? Explanation { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// The admin side of reputation (reputation-api.md section 9, ESC-32):
///
/// - GET  /admin/users/{userId}/reputation                              the full view (9.1)
/// - GET  /admin/reputation/appeals?status=pending|review               the appeals queue (9.2)
/// - POST /admin/users/{userId}/reputation/events/{eventId}/decision    excuse or uphold (9.3)
/// - POST /admin/users/{userId}/reputation                              tag, untag, restrict, lift (9.4)
///
/// All four need the admin pool's <c>admin</c> group (no role-claim shortcut). The reads work
/// whatever the flags say; the writes need <c>reputationPenalties</c> (off: 503). Every write is one
/// transaction in <see cref="ReputationService"/>, then one transaction-audit row (entity
/// <c>user</c>: who, what, the reason or note, the tier and points before and after) and an
/// <c>AuditLogger</c> line. The appeal text is never logged and never audited.
///
/// ESC-29 (reputation-api.md 8.4): a suspended owner counts as restricted for buyers, so a suspend
/// or an unsuspend must reach the owner fields copied onto their crop and trip rows. The admin
/// Lambda leaves the day's <c>HEAL#</c> marker and then (ESC-32, TASK FUP-163) refreshes the copies
/// in the same request; when that fails the marker lets the daily job (<c>reputation_daily</c>) do
/// it. Until a copy lands, an order to the suspended owner is still refused by the live check of 8.2.
/// </summary>
public partial class AdminFunction
{
    public const int MinReputationReasonLength = 3;
    public const int MaxReputationReasonLength = 500;
    public const int AppealPhotoUrlExpirySeconds = 600;
    public const string ReputationEventDecidedCode = "REPUTATION_EVENT_DECIDED";
    public const string ReputationNotRestrictedCode = "REPUTATION_NOT_RESTRICTED";
    public const string ReputationAlreadyRestrictedCode = "REPUTATION_ALREADY_RESTRICTED";
    public const string ReputationAppealChangedCode = "REPUTATION_APPEAL_CHANGED";

    private static readonly string[] AppealQueueStatuses = { "pending", "review" };

    private IReputationEventRepository? _reputationRepo;
    private ReputationService? _reputationService;

    /// <summary><c>reputationPenalties</c>: off (every environment today) refuses the admin writes and writes no marker.</summary>
    internal bool ReputationPenaltiesEnabled { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary>The reputation-events table; null or empty writes no marker and the reputation routes answer 503.</summary>
    internal string? ReputationEventsTable { get; init; } = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE");

    /// <summary>The events and users rows of the reputation routes; null when the table is not configured. Tests set it.</summary>
    internal IReputationEventRepository? ReputationRepo
    {
        get => _reputationRepo ??= string.IsNullOrEmpty(ReputationEventsTable) || string.IsNullOrEmpty(_usersTable)
            ? null
            : new ReputationEventRepository(_dynamoDb, ReputationEventsTable, _usersTable);
        init => _reputationRepo = value;
    }

    /// <summary>
    /// The reputation service of the admin routes. The policy parameter is read only while the
    /// feature is on (reputation-api.md section 11); off, the reads show the stored state under the
    /// defaults. The copy writer is attached only while it is on, like every other writer. Tests set it.
    /// </summary>
    internal ReputationService? ReputationSvc
    {
        get
        {
            if (_reputationService != null || ReputationRepo == null)
            {
                return _reputationService;
            }

            var settings = ReputationPenaltiesEnabled
                ? ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line))
                : ReputationPolicySettings.Defaults;
            return _reputationService = new ReputationService(
                ReputationRepo, settings,
                copies: ReputationPenaltiesEnabled ? ReputationCopyRepository.FromEnvironment(_dynamoDb) : null);
        }
        init => _reputationService = value;
    }

    // ===== 9.1: GET /admin/users/{userId}/reputation =====

    private async Task<APIGatewayProxyResponse> HandleGetReputationAsync(
        string userId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (RefuseReputationRoute(request, write: false) is { } refused)
        {
            return refused;
        }

        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        if (!TryReadReputationLimit(query, out var limit))
        {
            return ReputationError(400, ReputationFunction.ValidationCode,
                $"limit must be a whole number from 1 to {ReputationFunction.MaxEventsPage}.", "limit");
        }

        (string OccurredAt, string EventId)? after = null;
        if (query.TryGetValue("nextToken", out var token) && !string.IsNullOrEmpty(token))
        {
            var key = PaginationTokenHelper.DecodeNextToken(token);
            if (key == null || !key.TryGetValue("occurredAt", out var at) || !key.TryGetValue("eventId", out var id)
                || !key.TryGetValue("adminUserId", out var owner) || owner.S != userId)
            {
                return ReputationError(400, ReputationFunction.ValidationCode, "Invalid or tampered nextToken parameter.", "nextToken");
            }

            after = (at.S, id.S);
        }

        try
        {
            var service = ReputationSvc!;
            var view = await service.ReadAdminAsync(userId);
            if (view == null || !string.IsNullOrEmpty(view.Read.User.DeletionState))
            {
                return ReputationError(404, ReputationFunction.NotFoundCode, "No such user.");
            }

            var now = view.Read.AsOf;
            // Newest occurredAt first; the event id breaks ties so paging is stable. The heal
            // markers are bookkeeping of the daily job, not history.
            var listed = view.Rows
                .Where(e => e.Code != ReputationCodes.Heal && !e.EventId.StartsWith(ReputationSlots.Heal, StringComparison.Ordinal))
                .Select(e => (Row: e, At: ReputationPolicy.Iso(e.OccurredAt)))
                .OrderByDescending(x => x.At, StringComparer.Ordinal)
                .ThenByDescending(x => x.Row.EventId, StringComparer.Ordinal)
                .Where(x => after is not { } a
                            || string.CompareOrdinal(x.At, a.OccurredAt) < 0
                            || (x.At == a.OccurredAt && string.CompareOrdinal(x.Row.EventId, a.EventId) < 0))
                .ToList();

            var page = listed.Take(limit).ToList();
            string? nextToken = null;
            if (listed.Count > limit)
            {
                var last = page[^1];
                nextToken = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
                {
                    ["adminUserId"] = new() { S = userId },
                    ["occurredAt"] = new() { S = last.At },
                    ["eventId"] = new() { S = last.Row.EventId }
                });
            }

            return LambdaResponse.Ok(new AdminReputationResponse
            {
                Reputation = AdminReputationDto(view.Read, service),
                Restrictions = view.Rows
                    .Where(e => e.Code == ReputationCodes.Restriction)
                    .OrderByDescending(e => e.Since ?? e.OccurredAt)
                    .Select(e => RestrictionDto(e, now))
                    .ToList(),
                Events = page.Select(x => AdminEventDto(x.Row, now, service.Settings, context)).ToList(),
                NextToken = nextToken
            }, AppJsonContext.Default.AdminReputationResponse);
        }
        catch (Exception ex)
        {
            return ReputationFailure(ex, "read", context);
        }
    }

    // ===== 9.2: GET /admin/reputation/appeals =====

    private async Task<APIGatewayProxyResponse> HandleListReputationAppealsAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (RefuseReputationRoute(request, write: false) is { } refused)
        {
            return refused;
        }

        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        var status = query.TryGetValue("status", out var s) && !string.IsNullOrEmpty(s) ? s : AppealQueueStatuses[0];
        if (!AppealQueueStatuses.Contains(status))
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "status must be pending or review.", "status");
        }

        if (!TryReadReputationLimit(query, out var limit))
        {
            return ReputationError(400, ReputationFunction.ValidationCode,
                $"limit must be a whole number from 1 to {ReputationFunction.MaxEventsPage}.", "limit");
        }

        ReputationAppealCursor? after = null;
        if (query.TryGetValue("nextToken", out var token) && !string.IsNullOrEmpty(token))
        {
            // A token of the other status would start the Query outside its partition.
            var key = PaginationTokenHelper.DecodeNextToken(token);
            if (key == null || !key.TryGetValue("appealStatus", out var tokenStatus) || tokenStatus.S != status
                || !key.TryGetValue("appealedAt", out var at) || !key.TryGetValue("userId", out var uid)
                || !key.TryGetValue("eventId", out var eid))
            {
                return ReputationError(400, ReputationFunction.ValidationCode, "Invalid or tampered nextToken parameter.", "nextToken");
            }

            after = new ReputationAppealCursor(at.S, uid.S, eid.S);
        }

        try
        {
            var (rows, next) = await ReputationRepo!.QueryAppealsAsync(status, limit, after);
            var users = await ReadAppealUsersAsync(rows.Select(r => r.UserId));

            return LambdaResponse.Ok(new AdminReputationAppealsResponse
            {
                Appeals = rows.Select(r =>
                {
                    users.TryGetValue(r.UserId, out var user);
                    var isTrip = r.Code == ReputationCodes.TripCancelledWithBookings;
                    return new AdminReputationAppealItemDto
                    {
                        UserId = r.UserId,
                        Role = user.Role ?? (string.IsNullOrEmpty(r.Party) ? null : r.Party),
                        DisplayName = user.Name,
                        EventId = r.EventId,
                        Code = r.Code,
                        Points = r.Points,
                        OccurredAt = ReputationPolicy.Iso(r.OccurredAt),
                        OrderId = isTrip ? null : r.OrderId,
                        TripId = isTrip ? r.TripId : null,
                        AppealStatus = r.AppealStatus ?? status,
                        AppealedAt = r.AppealedAt,
                        Text = r.AppealText,
                        AppealTextAt = r.AppealTextAt,
                        HasPhoto = !string.IsNullOrEmpty(r.AppealPhotoRef),
                        AppealPhotoUrl = SignAppealPhoto(r, context)
                    };
                }).ToList(),
                NextToken = next == null
                    ? null
                    : PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
                    {
                        ["appealStatus"] = new() { S = status },
                        ["appealedAt"] = new() { S = next.AppealedAt },
                        ["userId"] = new() { S = next.UserId },
                        ["eventId"] = new() { S = next.EventId }
                    })
            }, AppJsonContext.Default.AdminReputationAppealsResponse);
        }
        catch (Exception ex)
        {
            return ReputationFailure(ex, "appeals", context);
        }
    }

    // ===== 9.3: POST /admin/users/{userId}/reputation/events/{eventId}/decision =====

    private async Task<APIGatewayProxyResponse> HandleReputationDecisionAsync(
        string userId, string rawEventId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (RefuseReputationRoute(request, write: true) is { } refused)
        {
            return refused;
        }

        AdminReputationDecisionRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? string.Empty, AppJsonContext.Default.AdminReputationDecisionRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body == null)
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "A JSON body is required.");
        }

        if (body.Decision is not (ReputationService.DecisionExcuse or ReputationService.DecisionUphold))
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "decision must be excuse or uphold.", "decision");
        }

        var note = body.Note?.Trim() ?? string.Empty;
        if (note.Length < MinReputationReasonLength || note.Length > MaxReputationReasonLength)
        {
            return ReputationError(400, ReputationFunction.ValidationCode,
                $"note must be {MinReputationReasonLength} to {MaxReputationReasonLength} characters.", "note");
        }

        DateTime? until = null;
        if (!string.IsNullOrWhiteSpace(body.Until))
        {
            if (body.Decision != ReputationService.DecisionUphold || !ReputationPolicy.TryParseUtc(body.Until, out var parsed))
            {
                return ReputationError(400, ReputationFunction.ValidationCode,
                    "until is an ISO 8601 UTC time and is only taken by an uphold.", "until");
            }

            until = parsed;
        }

        // What the admin's page showed of the appeal; an ISO time the server wrote, so anything long is not one.
        var seenTextAt = string.IsNullOrWhiteSpace(body.SeenAppealTextAt) ? null : body.SeenAppealTextAt.Trim();
        if (seenTextAt is { Length: > 40 })
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "seenAppealTextAt is not a time this server sent.", "seenAppealTextAt");
        }

        var eventId = ReputationFunction.EventIdOf(request, rawEventId);
        var adminId = AuthHelper.GetUserId(request) ?? "admin";

        try
        {
            var service = ReputationSvc!;
            var result = await service.DecideAsync(userId, eventId, body.Decision, note, until, adminId, context.Logger, seenTextAt);
            if (!result.Succeeded)
            {
                return ReputationRefusal(result.Outcome, service.Settings);
            }

            var applied = result.Outcome == ReputationAdminOutcome.Applied;
            if (applied)
            {
                var action = body.Decision == ReputationService.DecisionExcuse ? "reputation_excused" : "reputation_upheld";
                // The body's until is not written here: only an end that was applied is, in the detail.
                await AuditReputationAsync(request, context, userId, adminId, action, result, $"event={eventId};note={note}");
            }

            return LambdaResponse.Ok(new AdminReputationDecisionResponse
            {
                Event = AdminEventDto(result.Event!, result.After!.AsOf, service.Settings, context),
                Reputation = AdminReputationDto(result.After!, service),
                AlreadyDecided = !applied
            }, AppJsonContext.Default.AdminReputationDecisionResponse);
        }
        catch (Exception ex)
        {
            return ReputationFailure(ex, "decision", context);
        }
    }

    // ===== 9.4: POST /admin/users/{userId}/reputation =====

    private async Task<APIGatewayProxyResponse> HandleReputationActionAsync(
        string userId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (RefuseReputationRoute(request, write: true) is { } refused)
        {
            return refused;
        }

        AdminReputationActionRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? string.Empty, AppJsonContext.Default.AdminReputationActionRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body == null)
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "A JSON body is required.");
        }

        if (body.Action is not (ReputationService.ActionTag or ReputationService.ActionUntag
            or ReputationService.ActionRestrict or ReputationService.ActionLift))
        {
            return ReputationError(400, ReputationFunction.ValidationCode, "action must be tag, untag, restrict or lift.", "action");
        }

        var reason = body.Reason?.Trim() ?? string.Empty;
        if (reason.Length < MinReputationReasonLength || reason.Length > MaxReputationReasonLength)
        {
            return ReputationError(400, ReputationFunction.ValidationCode,
                $"reason must be {MinReputationReasonLength} to {MaxReputationReasonLength} characters.", "reason");
        }

        DateTime? until = null;
        if (body.Action != ReputationService.ActionLift)
        {
            if (!ReputationPolicy.TryParseUtc(body.Until, out var parsed))
            {
                return ReputationError(400, ReputationFunction.ValidationCode, "until is required, as an ISO 8601 UTC time.", "until");
            }

            until = parsed;
        }

        var adminId = AuthHelper.GetUserId(request) ?? "admin";

        try
        {
            var service = ReputationSvc!;
            var result = await service.ApplyAdminActionAsync(userId, body.Action, reason, until, adminId, context.Logger);
            if (!result.Succeeded)
            {
                return ReputationRefusal(result.Outcome, service.Settings, body.Action);
            }

            var applied = result.Outcome == ReputationAdminOutcome.Applied;
            if (applied)
            {
                var action = body.Action switch
                {
                    ReputationService.ActionTag => "reputation_tagged",
                    ReputationService.ActionUntag => "reputation_untagged",
                    ReputationService.ActionRestrict => "reputation_restricted",
                    _ => "reputation_lifted"
                };
                await AuditReputationAsync(request, context, userId, adminId, action, result,
                    $"reason={reason}" + (until is { } end ? $";until={ReputationPolicy.Iso(end)}" : string.Empty));
            }

            return LambdaResponse.Ok(new AdminReputationActionResponse
            {
                Reputation = AdminReputationDto(result.After!, service),
                AlreadyApplied = !applied
            }, AppJsonContext.Default.AdminReputationActionResponse);
        }
        catch (Exception ex)
        {
            return ReputationFailure(ex, "action", context);
        }
    }

    // ===== shared =====

    /// <summary>
    /// The refusals common to the four routes, in order: the admin group (403 <c>ADMIN_001</c>; the
    /// Lambda's own check, on top of the admin pool's authorizer), the table (503), and for a write
    /// the flag (503 <c>REPUTATION_UNAVAILABLE</c>).
    /// </summary>
    private APIGatewayProxyResponse? RefuseReputationRoute(APIGatewayProxyRequest request, bool write)
    {
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (ReputationRepo == null || ReputationSvc == null)
        {
            return ReputationError(503, ReputationFunction.UnavailableCode, "The reputation table is not configured.");
        }

        return write && !ReputationPenaltiesEnabled
            ? ReputationError(503, ReputationFunction.UnavailableCode,
                "Reputation penalties are switched off in this environment, so reputation cannot be changed.")
            : null;
    }

    private static APIGatewayProxyResponse ReputationRefusal(
        ReputationAdminOutcome outcome, ReputationPolicySettings settings, string? action = null) => outcome switch
        {
            ReputationAdminOutcome.UserNotFound => ReputationError(404, ReputationFunction.NotFoundCode, "No such user."),
            ReputationAdminOutcome.NotParty => ReputationError(400, ReputationFunction.ValidationCode,
                "Only a farmer or trucker has a reputation."),
            ReputationAdminOutcome.EventNotFound => ReputationError(404, ReputationFunction.EventNotFoundCode, "No such event."),
            ReputationAdminOutcome.EventDecided => ReputationError(409, ReputationEventDecidedCode,
                "This event was already decided the other way."),
            ReputationAdminOutcome.AppealClosed => ReputationError(409, ReputationFunction.AppealClosedCode,
                "There is nothing to decide: the event has no open appeal, or it no longer counts."),
            ReputationAdminOutcome.NotRestricted => ReputationError(409, ReputationNotRestrictedCode,
                "This account has no restriction in force."),
            ReputationAdminOutcome.AlreadyRestricted => ReputationError(409, ReputationAlreadyRestrictedCode,
                "This account already has a restriction in force. Lift it first."),
            ReputationAdminOutcome.AppealChanged => ReputationError(409, ReputationAppealChangedCode,
                "The appeal changed since this page was loaded. Reload it, read the appeal and decide again."),
            _ => ReputationError(400, ReputationFunction.ValidationCode,
                action is ReputationService.ActionTag or ReputationService.ActionUntag
                    ? $"until must be in the future and at most {settings.TagMaxDays} days ahead."
                    : $"until must be in the future and at most {settings.AdminRestrictMaxDays} days ahead.",
                "until")
        };

    private static APIGatewayProxyResponse ReputationError(int status, string code, string message, string? field = null)
        => LambdaResponse.Error(status, code, message, field);

    private static APIGatewayProxyResponse ReputationFailure(Exception ex, string step, ILambdaContext context)
    {
        context.Logger.LogError($"Admin reputation {step} failed: [{ex.GetType().Name}]");
        return ReputationError(500, ReputationFunction.ServerErrorCode, "The reputation request failed. Please try again later.");
    }

    private static bool TryReadReputationLimit(IDictionary<string, string> query, out int limit)
    {
        limit = ReputationFunction.DefaultEventsPage;
        return !query.TryGetValue("limit", out var text)
               || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                   && limit >= 1 && limit <= ReputationFunction.MaxEventsPage);
    }

    /// <summary>
    /// One transaction-audit row per applied action (never for a replay): the acting admin, the
    /// action, the tier and points before and after, the reason or note, and what else changed
    /// (<see cref="ReputationAdminResult.Detail"/>: a tag, an untag and a lift can leave the tier and
    /// the points as they were). Best effort like every admin audit (a failed write is logged by the
    /// writer), plus the audit log line.
    /// </summary>
    private async Task AuditReputationAsync(
        APIGatewayProxyRequest request, ILambdaContext context, string userId, string adminId, string action,
        ReputationAdminResult result, string metadata)
    {
        static string State(ReputationRead? read)
            => read == null ? string.Empty : $"tier={read.Aggregate.Tier};points={read.Aggregate.ActivePoints}";

        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, userId, "user", userId, action, State(result.Before), State(result.After),
            AuditContext.From(request, adminId, "admin"),
            metadata: string.IsNullOrEmpty(result.Detail) ? metadata : $"{metadata};{result.Detail}");

        AuditLogger.LogAudit("ADMIN", "REPUTATION_ACTION",
            userId: adminId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "user", entityId: userId, detail: action, outcome: "success");
    }

    /// <summary>Section 2's shape with <c>override.by</c> (9.1).</summary>
    private ReputationDto AdminReputationDto(ReputationRead read, ReputationService service)
    {
        var dto = ReputationReadModels.Own(read, service.Settings, ReputationPenaltiesEnabled);
        return dto.Override != null && read.Aggregate.Override?.By is { } by
            ? dto with { Override = dto.Override with { By = by } }
            : dto;
    }

    private static AdminReputationRestrictionDto RestrictionDto(ReputationEvent e, DateTime now) => new()
    {
        EventId = e.EventId,
        Cause = e.Cause ?? ReputationCauses.Points,
        // An active row whose end has passed reads ended before the daily job writes it.
        Status = e.Status == ReputationStatuses.Active && e.Until is { } until && until <= now
            ? ReputationStatuses.Ended
            : e.Status,
        Since = e.Since is { } since ? ReputationPolicy.Iso(since) : null,
        Until = e.Until is { } end ? ReputationPolicy.Iso(end) : null,
        Reason = e.Reason,
        By = e.By,
        LiftedAt = e.LiftedAt,
        LiftedBy = e.LiftedBy
    };

    /// <summary>One row for an admin: nothing is hidden (the buyer, the base points, the appeal text and photo).</summary>
    private AdminReputationEventDto AdminEventDto(
        ReputationEvent e, DateTime now, ReputationPolicySettings settings, ILambdaContext context)
    {
        var isOrderEvent = ReputationCodes.OrderEvents.Contains(e.Code);
        var status = isOrderEvent
            ? ReputationReadModels.StatusAt(e, now)
            : e.Status == ReputationStatuses.Active && e.Until is { } until && until <= now ? ReputationStatuses.Ended : e.Status;
        return new AdminReputationEventDto
        {
            EventId = e.EventId,
            Code = e.Code,
            Party = string.IsNullOrEmpty(e.Party) ? null : e.Party,
            Points = e.Points,
            BasePoints = e.BasePoints,
            ZeroReason = e.ZeroReason,
            EarnedBack = e.EarnedBack,
            RemainingPoints = isOrderEvent && status == ReputationStatuses.Active ? Math.Max(0, e.Points - e.EarnedBack) : 0,
            Status = status,
            OccurredAt = ReputationPolicy.Iso(e.OccurredAt),
            RecordedAt = ReputationPolicy.Iso(e.RecordedAt),
            PointsExpireAt = e.PointsExpireAt is { } expires ? ReputationPolicy.Iso(expires) : null,
            OrderId = e.OrderId,
            TripId = e.TripId,
            BuyerId = e.BuyerId,
            EarnBackEventId = e.EarnBackEventId,
            Appeal = isOrderEvent
                ? new AdminReputationAppealDto
                {
                    Status = ReputationReadModels.AppealStatus(e),
                    CanAppeal = ReputationReadModels.CanAppeal(e, now, settings),
                    Text = e.AppealText,
                    AppealedAt = e.AppealTextAt ?? e.AppealedAt,
                    TextAt = e.AppealTextAt,
                    HasPhoto = !string.IsNullOrEmpty(e.AppealPhotoRef),
                    AppealPhotoUrl = SignAppealPhoto(e, context),
                    DecidedAt = e.DecidedAt,
                    DecidedBy = e.DecidedBy,
                    Note = e.DecisionNote
                }
                : null,
            Cause = e.Cause,
            Since = e.Since is { } since ? ReputationPolicy.Iso(since) : null,
            Until = e.Until is { } end ? ReputationPolicy.Iso(end) : null,
            Tag = e.Tag,
            Reason = e.Reason,
            By = e.By,
            FromTier = e.FromTier,
            ToTier = e.ToTier,
            RestrictedUntil = e.RestrictedUntil is { } restrictedUntil ? ReputationPolicy.Iso(restrictedUntil) : null,
            LiftedAt = e.LiftedAt,
            LiftedBy = e.LiftedBy
        };
    }

    /// <summary>
    /// A pre-signed GET of the row's appeal photo, valid 10 minutes, or null. Only a key under the
    /// row owner's own appeal prefix is signed (the shape the appeal accepted), so a stored value
    /// can never make the admin Lambda sign another object. Signing is local; a failure leaves the
    /// URL off.
    /// </summary>
    private string? SignAppealPhoto(ReputationEvent e, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(e.AppealPhotoRef) || string.IsNullOrEmpty(UploadsBucket) || DeliveryPhotoS3 is not { } s3
            || !ReputationFunction.IsOwnAppealPhoto(e.AppealPhotoRef, e.UserId))
        {
            return null;
        }

        try
        {
            return s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = UploadsBucket,
                Key = e.AppealPhotoRef,
                Verb = HttpVerb.GET,
                Expires = Clock().AddSeconds(AppealPhotoUrlExpirySeconds)
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Appeal photo URL not signed: [{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>The display name and role of the queue page's users: one BatchGetItem per 100, retried for unprocessed keys.</summary>
    private async Task<Dictionary<string, (string? Name, string? Role)>> ReadAppealUsersAsync(IEnumerable<string> userIds)
    {
        var result = new Dictionary<string, (string? Name, string? Role)>(StringComparer.Ordinal);
        foreach (var batch in userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().Chunk(100))
        {
            var requestItems = new Dictionary<string, KeysAndAttributes>
            {
                [_usersTable] = new()
                {
                    Keys = batch.Select(id => new Dictionary<string, AttributeValue> { ["userId"] = new() { S = id } }).ToList(),
                    ProjectionExpression = "userId, displayName, #role",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#role"] = "role" }
                }
            };

            var attempts = 0;
            while (requestItems.Count > 0 && attempts++ < 5)
            {
                var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = requestItems });
                if (response.Responses != null && response.Responses.TryGetValue(_usersTable, out var items))
                {
                    foreach (var item in items)
                    {
                        if (item.TryGetValue("userId", out var id) && !string.IsNullOrEmpty(id.S))
                        {
                            result[id.S] = (
                                item.TryGetValue("displayName", out var name) && !string.IsNullOrEmpty(name.S) ? name.S : null,
                                item.TryGetValue("role", out var role) && !string.IsNullOrEmpty(role.S) ? role.S : null);
                        }
                    }
                }

                requestItems = response.UnprocessedKeys is { Count: > 0 } left ? left : new Dictionary<string, KeysAndAttributes>();
            }
        }

        return result;
    }

    // ===== ESC-29 and FUP-163: the copies after a suspend or unsuspend =====

    /// <summary>
    /// Best effort, never fails the admin action: the day's heal marker, written right after the
    /// profile row so the daily job rewrites the copies whatever happens later in the request. A
    /// failure is logged with the copy token.
    /// </summary>
    private async Task MarkReputationCopyDueAsync(string userId, string action, ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled || string.IsNullOrEmpty(ReputationEventsTable)
            || action is not ("suspend" or "unsuspend"))
        {
            return;
        }

        try
        {
            var heal = ReputationEventRepository.HealUpsert(ReputationEventsTable, userId, DateTime.UtcNow);
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = heal.TableName,
                Key = heal.Key,
                UpdateExpression = heal.UpdateExpression,
                ExpressionAttributeNames = heal.ExpressionAttributeNames,
                ExpressionAttributeValues = heal.ExpressionAttributeValues
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{ReputationService.CopyFailedToken} step=admin_{action} user={MoneyAlarmLog.LogId(userId)} [{ex.GetType().Name}]");
        }

    }

    /// <summary>
    /// TASK FUP-163, best effort: the aggregate and the owner fields on the user's listings in this
    /// request. Called after the sign-in service was updated, so ending the user's sessions never
    /// waits for it. A failure is logged with the copy token and left to the daily job (the marker).
    /// </summary>
    private async Task RefreshReputationAfterSuspensionAsync(string userId, string action, ILambdaContext context)
    {
        if (!ReputationPenaltiesEnabled || string.IsNullOrEmpty(ReputationEventsTable)
            || action is not ("suspend" or "unsuspend"))
        {
            return;
        }

        try
        {
            if (ReputationSvc is { } service)
            {
                await service.RefreshAfterSuspensionAsync(userId, context.Logger);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{ReputationService.CopyFailedToken} step=admin_{action}_refresh user={MoneyAlarmLog.LogId(userId)} [{ex.GetType().Name}]");
        }
    }
}

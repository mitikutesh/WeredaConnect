using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-29: the reputation endpoints of reputation-api.md sections 2 to 6.
///
/// - GET  /reputation/me                        the caller's own reputation (farmer, trucker)
/// - GET  /reputation/me/events                 the caller's penalty history, newest first
/// - GET  /reputation/{userId}                  the public summary of a farmer or trucker
/// - POST /reputation/appeals/photo-url         a pre-signed PUT for the optional appeal photo
/// - POST /reputation/events/{eventId}/appeal   ask an admin to excuse one penalty
///
/// Role visibility (CLAUDE.md, spec 5.I8): a farmer or trucker reads only their own reputation; a
/// buyer reads only the public summary of a farmer or trucker; nobody reads a buyer's. The routes
/// sit behind the app user pool's authorizer, so an admin token never reaches them (admins use the
/// admin routes, ESC-32). The reads are not flagged; the two POSTs need <c>reputationPenalties</c>.
/// </summary>
public class ReputationFunction
{
    public const string ValidationCode = "REPUTATION_VALIDATION";
    public const string ForbiddenCode = "REPUTATION_FORBIDDEN";
    public const string NotFoundCode = "REPUTATION_NOT_FOUND";
    public const string EventNotFoundCode = "REPUTATION_EVENT_NOT_FOUND";
    public const string AppealClosedCode = "REPUTATION_APPEAL_CLOSED";
    public const string AppealExistsCode = "REPUTATION_APPEAL_EXISTS";
    public const string UnavailableCode = "REPUTATION_UNAVAILABLE";
    public const string ServerErrorCode = "REPUTATION_SERVER_ERROR";

    /// <summary>The appeal photo's key prefix in the private uploads bucket, then the user id.</summary>
    public const string AppealPhotoPrefix = "uploads/appeals/";

    public const int PhotoUrlExpirySeconds = 900;
    public const int MaxAppealTextLength = 500;
    public const int DefaultEventsPage = 50;
    public const int MaxEventsPage = 100;

    private readonly IReputationEventRepository _repo;
    private readonly ReputationService _service;
    private readonly ITransactionAuditRepository? _audit;
    private readonly IAmazonS3? _s3;
    private readonly string? _uploadsBucket;

    /// <summary><c>reputationPenalties</c>: the appeal endpoints work; the own view reports it. Off by default.</summary>
    internal bool PenaltiesEnabled { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary><c>reputationPublicTags</c>: the public summary carries the tier, the effective rating and the reliability. Off by default.</summary>
    internal bool PublicTags { get; init; } = ReputationFlags.PublicTagsEnabled;

    /// <summary>The server clock; tests set it.</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public ReputationFunction()
    {
        var eventsTable = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE")
                          ?? throw new InvalidOperationException("REPUTATION_EVENTS_TABLE is not set.");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var auditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _uploadsBucket = Environment.GetEnvironmentVariable("UPLOADS_BUCKET");

        var dynamoDb = new AmazonDynamoDBClient();
        _repo = new ReputationEventRepository(dynamoDb, eventsTable, usersTable);
        // The policy parameter is read only while the feature is on (reputation-api.md section 11);
        // off, the reads show the stored state under the defaults.
        var settings = ReputationFlags.PenaltiesEnabled
            ? ReputationPolicySettings.LoadForContainer(line => LambdaLogger.Log(line))
            : ReputationPolicySettings.Defaults;
        _service = new ReputationService(_repo, settings);
        _audit = string.IsNullOrEmpty(auditTable) ? null : new TransactionAuditRepository(dynamoDb, auditTable);
        _s3 = string.IsNullOrEmpty(_uploadsBucket) ? null : new AmazonS3Client();
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public ReputationFunction(
        IReputationEventRepository repo,
        ReputationService service,
        ITransactionAuditRepository? audit = null,
        IAmazonS3? s3 = null,
        string? uploadsBucket = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _audit = audit;
        _s3 = s3;
        _uploadsBucket = uploadsBucket;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var callerId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(callerId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        var role = AuthHelper.GetClaim(request, "custom:role")?.ToLowerInvariant();
        var segments = (request.Path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        // An API stage prefix may precede "reputation".
        var start = Array.IndexOf(segments, "reputation");
        var route = start < 0 ? Array.Empty<string>() : segments[(start + 1)..];

        try
        {
            return (request.HttpMethod, route) switch
            {
                ("GET", ["me"]) => await GetOwnAsync(callerId, role),
                ("GET", ["me", "events"]) => await GetOwnEventsAsync(request, callerId, role),
                ("GET", [var userId]) => await GetPublicAsync(callerId, role, Uri.UnescapeDataString(userId)),
                ("POST", ["appeals", "photo-url"]) => await PostPhotoUrlAsync(request, callerId, role, context),
                ("POST", ["events", var eventId, "appeal"]) => await PostAppealAsync(request, callerId, role, EventIdOf(request, eventId), context),
                _ => LambdaResponse.Error(404, NotFoundCode, "No such reputation route.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"ReputationFunction failed: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, ServerErrorCode, "The reputation could not be read. Please try again later.");
        }
    }

    // ===== section 2: GET /reputation/me =====

    private async Task<APIGatewayProxyResponse> GetOwnAsync(string callerId, string? role)
    {
        if (!IsParty(role))
        {
            return Forbidden("Only farmers and truckers have a reputation.");
        }

        var read = await _service.ReadAsync(callerId, withEvents: true);
        if (read == null || !string.IsNullOrEmpty(read.User.DeletionState))
        {
            return NotFound();
        }

        if (!IsParty(read.User.Role))
        {
            return Forbidden("Only farmers and truckers have a reputation.");
        }

        return LambdaResponse.Ok(
            new GetReputationResponse { Reputation = ReputationReadModels.Own(read, _service.Settings, PenaltiesEnabled) },
            AppJsonContext.Default.GetReputationResponse);
    }

    // ===== section 3: GET /reputation/{userId} =====

    private async Task<APIGatewayProxyResponse> GetPublicAsync(string callerId, string? role, string targetId)
    {
        // The table of section 3, decided before any read for a farmer or trucker, so the answer
        // never tells them whether another id exists.
        var isBuyer = role == "buyer";
        if (!isBuyer && !(IsParty(role) && targetId == callerId))
        {
            return Forbidden("You can only read your own reputation.");
        }

        var read = await _service.ReadAsync(targetId, withEvents: false);
        if (read == null || !string.IsNullOrEmpty(read.User.DeletionState) || !IsParty(read.User.Role))
        {
            // A buyer target, a missing row and a deleting account read the same.
            return NotFound();
        }

        return LambdaResponse.Ok(
            new GetReputationResponse { Reputation = ReputationReadModels.Public(read, PublicTags) },
            AppJsonContext.Default.GetReputationResponse);
    }

    // ===== section 4: GET /reputation/me/events =====

    private async Task<APIGatewayProxyResponse> GetOwnEventsAsync(APIGatewayProxyRequest request, string callerId, string? role)
    {
        if (!IsParty(role))
        {
            return Forbidden("Only farmers and truckers have a reputation.");
        }

        var query = request.QueryStringParameters ?? new Dictionary<string, string>();
        var limit = DefaultEventsPage;
        if (query.TryGetValue("limit", out var limitText)
            && (!int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)
                || limit < 1 || limit > MaxEventsPage))
        {
            return LambdaResponse.Error(400, ValidationCode, $"limit must be a whole number from 1 to {MaxEventsPage}.", "limit");
        }

        (string OccurredAt, string EventId)? after = null;
        if (query.TryGetValue("nextToken", out var token) && !string.IsNullOrEmpty(token))
        {
            var key = PaginationTokenHelper.DecodeNextToken(token);
            if (key == null || !key.TryGetValue("occurredAt", out var at) || !key.TryGetValue("eventId", out var id)
                || !key.TryGetValue("userId", out var owner) || owner.S != callerId)
            {
                return LambdaResponse.Error(400, ValidationCode, "Invalid or tampered nextToken parameter.", "nextToken");
            }

            after = (at.S, id.S);
        }

        var user = await _repo.GetUserStateAsync(callerId);
        if (user == null || !string.IsNullOrEmpty(user.DeletionState))
        {
            return NotFound();
        }

        var now = Clock();
        // Newest occurredAt first; the event id breaks ties so paging is stable.
        var listed = (await _service.ReadEventsAsync(callerId))
            .Where(ReputationReadModels.IsListed)
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
                ["userId"] = new() { S = callerId },
                ["occurredAt"] = new() { S = last.At },
                ["eventId"] = new() { S = last.Row.EventId }
            });
        }

        return LambdaResponse.Ok(new GetReputationEventsResponse
        {
            Events = page.Select(x => ReputationReadModels.Event(x.Row, now, _service.Settings)).ToList(),
            NextToken = nextToken
        }, AppJsonContext.Default.GetReputationEventsResponse);
    }

    // ===== section 5: POST /reputation/appeals/photo-url =====

    private async Task<APIGatewayProxyResponse> PostPhotoUrlAsync(
        APIGatewayProxyRequest request, string callerId, string? role, ILambdaContext context)
    {
        if (await RefuseWriteAsync(callerId, role) is { } refused)
        {
            return refused;
        }

        ReputationAppealPhotoRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? string.Empty, AppJsonContext.Default.ReputationAppealPhotoRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body?.SizeBytes is not long size || size < 1 || size > DeliveryPhotoKeys.MaxBytes)
        {
            return LambdaResponse.Error(400, ValidationCode, $"sizeBytes must be between 1 and {DeliveryPhotoKeys.MaxBytes}.", "sizeBytes");
        }

        var contentType = body.ContentType ?? "image/webp";
        if (!DeliveryPhotoKeys.ExtensionByContentType.TryGetValue(contentType, out var extension))
        {
            return LambdaResponse.Error(400, ValidationCode, "contentType must be image/webp or image/jpeg.", "contentType");
        }

        if (_s3 == null || string.IsNullOrEmpty(_uploadsBucket))
        {
            context.Logger.LogError("Appeal photo upload is not configured (UPLOADS_BUCKET).");
            return LambdaResponse.Error(500, ServerErrorCode, "Photo upload is not configured.");
        }

        var key = $"{AppealPhotoPrefix}{callerId}/{Guid.NewGuid():D}.{extension.TrimStart('.')}";
        var presign = new GetPreSignedUrlRequest
        {
            BucketName = _uploadsBucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = Clock().AddSeconds(PhotoUrlExpirySeconds),
            ContentType = contentType
        };
        // Signed with the type, so S3 refuses a PUT of any other length.
        presign.Headers.ContentLength = size;

        return LambdaResponse.Ok(new ReputationAppealPhotoResponse
        {
            UploadUrl = _s3.GetPreSignedURL(presign),
            PhotoRef = key,
            ExpiresInSeconds = PhotoUrlExpirySeconds
        }, AppJsonContext.Default.ReputationAppealPhotoResponse);
    }

    // ===== section 6: POST /reputation/events/{eventId}/appeal =====

    private async Task<APIGatewayProxyResponse> PostAppealAsync(
        APIGatewayProxyRequest request, string callerId, string? role, string eventId, ILambdaContext context)
    {
        if (await RefuseWriteAsync(callerId, role) is { } refused)
        {
            return refused;
        }

        ReputationAppealRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? string.Empty, AppJsonContext.Default.ReputationAppealRequest);
        }
        catch (JsonException)
        {
            body = null;
        }

        var text = body?.Text?.Trim() ?? string.Empty;
        if (text.Length < 1 || text.Length > MaxAppealTextLength)
        {
            return LambdaResponse.Error(400, ValidationCode, $"text must be 1 to {MaxAppealTextLength} characters.", "text");
        }

        var photoRef = string.IsNullOrWhiteSpace(body!.PhotoRef) ? null : body.PhotoRef!.Trim();
        if (photoRef != null && !IsOwnAppealPhoto(photoRef, callerId))
        {
            return LambdaResponse.Error(400, ValidationCode, "photoRef is not an appeal photo of this account.", "photoRef");
        }

        var now = Clock();
        // The event is looked up under the caller's own id: nobody can appeal another user's event.
        var row = await _repo.GetEventAsync(callerId, eventId);
        if (row == null || !ReputationCodes.OrderEvents.Contains(row.Code))
        {
            return LambdaResponse.Error(404, EventNotFoundCode, "No such event.");
        }

        if (AlreadyAppealed(row, text, photoRef) is { } answered)
        {
            return answered;
        }

        if (!ReputationReadModels.CanAppeal(row, now, _service.Settings))
        {
            return LambdaResponse.Error(409, AppealClosedCode, "This event can no longer be appealed.");
        }

        if (photoRef != null && !await PhotoExistsAsync(photoRef))
        {
            return LambdaResponse.Error(400, ValidationCode, "The appeal photo was not uploaded.", "photoRef");
        }

        var openReview = row.AppealStatus == "review";
        if (!await _repo.TryAppealAsync(callerId, eventId, text, photoRef, now, openReview))
        {
            // The row changed between the read and the write: decide again on what is stored now.
            row = await _repo.GetEventAsync(callerId, eventId);
            if (row == null)
            {
                return LambdaResponse.Error(404, EventNotFoundCode, "No such event.");
            }

            return AlreadyAppealed(row, text, photoRef)
                   ?? LambdaResponse.Error(409, AppealClosedCode, "This event can no longer be appealed.");
        }

        var iso = ReputationPolicy.Iso(now);
        var stored = row with
        {
            AppealText = text,
            AppealPhotoRef = photoRef,
            AppealTextAt = iso,
            AppealStatus = openReview ? row.AppealStatus : "pending",
            AppealedAt = openReview ? row.AppealedAt : iso
        };

        // The event id goes to the audit row; the appeal text never leaves the event row.
        await TransactionAuditWriter.EmitAsync(
            _audit, context, callerId, "user", callerId, "reputation_appealed", null, stored.AppealStatus,
            AuditContext.From(request, callerId, role), metadata: eventId);

        return AppealAnswer(stored, now, alreadyAppealed: false);
    }

    /// <summary>The same appeal sent again answers 200 and writes nothing; a different one is refused. Null when the row has no appeal.</summary>
    private APIGatewayProxyResponse? AlreadyAppealed(ReputationEvent row, string text, string? photoRef)
    {
        if (string.IsNullOrEmpty(row.AppealText))
        {
            return null;
        }

        return row.AppealText == text && row.AppealPhotoRef == photoRef
            ? AppealAnswer(row, Clock(), alreadyAppealed: true)
            : LambdaResponse.Error(409, AppealExistsCode, "This event already has an appeal.");
    }

    private APIGatewayProxyResponse AppealAnswer(ReputationEvent row, DateTime now, bool alreadyAppealed)
        => LambdaResponse.Ok(new ReputationAppealResponse
        {
            Appeal = new ReputationAppealStateDto
            {
                Status = ReputationReadModels.AppealStatus(row),
                AppealedAt = row.AppealTextAt ?? row.AppealedAt,
                HasPhoto = !string.IsNullOrEmpty(row.AppealPhotoRef)
            },
            Event = ReputationReadModels.Event(row, now, _service.Settings),
            AlreadyAppealed = alreadyAppealed
        }, AppJsonContext.Default.ReputationAppealResponse);

    // ===== shared =====

    /// <summary>
    /// The refusals of the two POSTs, in the contract's order: a buyer (403), the flag (503), then
    /// the caller's row read strongly consistent: deleting (410 ACCOUNT_004), suspended (403 AUTH_025).
    /// </summary>
    private async Task<APIGatewayProxyResponse?> RefuseWriteAsync(string callerId, string? role)
    {
        if (!IsParty(role))
        {
            return Forbidden("Only farmers and truckers can appeal.");
        }

        if (!PenaltiesEnabled)
        {
            return LambdaResponse.Error(503, UnavailableCode, "Appeals are not available yet.");
        }

        var user = await _repo.GetUserStateAsync(callerId);
        if (user == null)
        {
            return NotFound();
        }

        if (!string.IsNullOrEmpty(user.DeletionState))
        {
            return AccountDeletionGuard.Gone(user.DeletionState!);
        }

        return user.Suspended
            ? LambdaResponse.Error(403, AccountDeletionGuard.SuspendedCode, AccountDeletionGuard.SuspendedMessage)
            : null;
    }

    private async Task<bool> PhotoExistsAsync(string photoRef)
    {
        if (_s3 == null || string.IsNullOrEmpty(_uploadsBucket))
        {
            return false;
        }

        try
        {
            await _s3.GetObjectMetadataAsync(_uploadsBucket, photoRef);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }
    }

    /// <summary>A key this account was given by section 5: its own prefix, one file name, no path tricks.</summary>
    internal static bool IsOwnAppealPhoto(string photoRef, string callerId)
    {
        var prefix = $"{AppealPhotoPrefix}{callerId}/";
        if (!photoRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = photoRef[prefix.Length..];
        return name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
               && !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>The event id of the path, decoded once (<c>CANCEL%23ord-7f3a</c>); the gateway's path parameter when it gives one.</summary>
    internal static string EventIdOf(APIGatewayProxyRequest request, string rawSegment)
        => request.PathParameters != null && request.PathParameters.TryGetValue("eventId", out var fromGateway)
           && !string.IsNullOrEmpty(fromGateway)
            ? (fromGateway.Contains('#') ? fromGateway : Uri.UnescapeDataString(fromGateway))
            : Uri.UnescapeDataString(rawSegment);

    private static bool IsParty(string? role)
        => string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase);

    private static APIGatewayProxyResponse Forbidden(string message) => LambdaResponse.Error(403, ForbiddenCode, message);

    private static APIGatewayProxyResponse NotFound() => LambdaResponse.Error(404, NotFoundCode, "No such farmer or trucker.");
}

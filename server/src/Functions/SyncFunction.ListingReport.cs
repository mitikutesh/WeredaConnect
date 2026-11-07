using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-67: the <c>listing_report</c> entity (listing-report-api.md 1.0.0 section 1, sync-protocol.md 1.44.0).
/// Create only. A buyer reports a crop listing or a trip, which buyers see all of, with a reason code;
/// the report lands in the admin queue (<c>GET /admin/listing-reports</c>). One report per buyer and
/// listing (a repeat answers <c>duplicate</c>), at most <see cref="ListingReportRules.DailyLimit"/> a day.
/// Nothing is written to the listing and its owner is never told who reported it.
/// </summary>
public partial class SyncFunction
{
    private IListingReportRepository? _listingReportRepo;
    private IRateLimitRepository? _reportRateLimits;

    /// <summary>
    /// The listing-reports table. Absent env (<c>LISTING_REPORTS_TABLE</c>) means null, and a report answers
    /// an unprefixed error, which clients retry. Tests set it.
    /// </summary>
    internal IListingReportRepository? ListingReportRepo
    {
        get => _listingReportRepo ??= FromEnvironment("LISTING_REPORTS_TABLE", t => new ListingReportRepository(_dynamoDb, t));
        init => _listingReportRepo = value;
    }

    /// <summary>The per-reporter day counter (rate-limits table, <c>RATE_LIMITS_TABLE</c>). Tests set it.</summary>
    internal IRateLimitRepository? ReportRateLimits
    {
        get => _reportRateLimits ??= FromEnvironment("RATE_LIMITS_TABLE", t => new RateLimitRepository(_dynamoDb, t));
        init => _reportRateLimits = value;
    }

    /// <summary>The server clock of the report rules (the day counter, <c>createdAt</c>, the TTL); tests set it.</summary>
    internal Func<DateTime> ReportClock { get; init; } = () => DateTime.UtcNow;

    private static T? FromEnvironment<T>(string variable, Func<string, T> build) where T : class
    {
        var table = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(table) ? null : build(table);
    }

    private async Task<SyncOperationResult> ProcessListingReportAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        var reports = ListingReportRepo;
        var limits = ReportRateLimits;
        if (reports == null || limits == null)
        {
            return ReportResult(operation, "error", "Listing reports are not enabled on this server yet.");
        }

        if (!string.Equals(operation.Action, "create", StringComparison.Ordinal))
        {
            return ReportResult(operation, "error",
                $"SYNC_VALIDATION: Unsupported action '{operation.Action}'. Listing reports are create only.");
        }

        // Buyers see every crop listing and trip; a farmer or a trucker sees only their own (CLAUDE.md role
        // rules), so only a buyer has another party's listing to report.
        if (string.IsNullOrEmpty(authenticatedUserId)
            || !string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseWithSignalAsync(
                ReportResult(operation, "error", "SYNC_VALIDATION: Only buyers can report a listing."),
                RoleGateSignal(authenticatedRole), operation, ListingReportRules.EntityType, authenticatedUserId, context);
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return ReportResult(operation, "error", "SYNC_VALIDATION: Operation requires a data payload.");
        }

        var errors = ListingReportRules.Parse(operation.EntityId, operation.Data, out var input);
        if (errors.Count > 0 || input == null)
        {
            return ReportResult(operation, "error", $"SYNC_VALIDATION: {string.Join(" ", errors)}");
        }

        var ownerId = await ReadListingOwnerAsync(input);
        if (ownerId == null)
        {
            return ReportResult(operation, "error", "SYNC_VALIDATION: The reported listing does not exist.");
        }

        if (string.Equals(ownerId, authenticatedUserId, StringComparison.Ordinal))
        {
            return ReportResult(operation, "error", "SYNC_VALIDATION: You cannot report your own listing.");
        }

        // A repeat (another device, a resend with a new key) changes nothing and costs no quota.
        if (await reports.ExistsAsync(input.ListingId, authenticatedUserId))
        {
            return ReportResult(operation, "duplicate", "This listing is already reported.");
        }

        var now = ReportClock();
        var count = await limits.IncrementAsync(
            ListingReportRules.RateLimitKey(authenticatedUserId, now), ListingReportRules.RateLimitTtlSeconds);
        if (count > ListingReportRules.DailyLimit)
        {
            return ReportResult(operation, "error",
                $"SYNC_VALIDATION: report limit reached. A buyer can send at most {ListingReportRules.DailyLimit} reports a day.");
        }

        var created = await reports.TryCreateAsync(new ListingReportRow(
            input.ListingId,
            authenticatedUserId,
            input.ListingType,
            ownerId,
            input.Reason,
            ListingReportRules.StatusOpen,
            now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ListingReportRules.ExpiresAt(now)));
        if (!created)
        {
            return ReportResult(operation, "duplicate", "This listing is already reported.");
        }

        // No ids on the line: the reporter stays out of the logs as well.
        context.Logger.LogInformation(
            $"LISTING_REPORTED listingType={input.ListingType} reason={input.Reason}");
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = 1
        };
    }

    /// <summary>The farmer or trucker who owns the listing, or null when there is no such listing.</summary>
    private async Task<string?> ReadListingOwnerAsync(ListingReportInput input)
    {
        if (input.ListingType == ListingReportRules.ListingCrop)
        {
            var crop = await _cropRepo.GetByIdAsync(input.ListingId);
            var farmerId = crop == null ? "" : ItemString(crop, "farmerId");
            return string.IsNullOrEmpty(farmerId) ? null : farmerId;
        }

        var trip = await GetTripItemAsync(input.ListingId);
        var truckerId = trip == null ? "" : AttrString(trip, "truckerId");
        return string.IsNullOrEmpty(truckerId) ? null : truckerId;
    }

    private static SyncOperationResult ReportResult(SyncOperationRequest operation, string status, string message)
        => new()
        {
            OperationId = operation.OperationId,
            Status = status,
            Message = message
        };
}

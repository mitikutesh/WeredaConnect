using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-67: the admin queue of listing reports (listing-report-api.md 1.0.0 section 2).
///
/// - GET  /admin/listing-reports?status=open|dismissed|actioned&amp;limit=&amp;nextToken= : one page of reports
///   from the <c>StatusCreatedAtIndex</c> (open: oldest first), with what each reported listing holds now.
/// - POST /admin/listing-reports/{listingId}/review with <c>{decision, note?}</c>: decides every open report
///   of the listing (<c>dismissed</c> or <c>actioned</c>). Acting on the owner (suspend) stays on the user
///   page; this records the decision and the audit rows.
/// </summary>
public partial class AdminFunction
{
    private const int DefaultReportPage = 50;
    private const int MaxReportPage = 100;

    private IListingReportRepository? _listingReportRepo;

    /// <summary>
    /// The listing-reports table (<c>LISTING_REPORTS_TABLE</c>); null when the env is absent, and the routes
    /// answer 503. Tests set it.
    /// </summary>
    internal IListingReportRepository? ListingReportRepo
    {
        get
        {
            if (_listingReportRepo != null)
            {
                return _listingReportRepo;
            }

            var table = Environment.GetEnvironmentVariable("LISTING_REPORTS_TABLE");
            return _listingReportRepo = string.IsNullOrEmpty(table) ? null : new ListingReportRepository(_dynamoDb, table);
        }
        init => _listingReportRepo = value;
    }

    private async Task<APIGatewayProxyResponse> HandleListListingReportsAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var reports = ListingReportRepo;
        if (reports == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Listing reports table not configured.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        var status = queryParams.TryGetValue("status", out var s) && !string.IsNullOrEmpty(s)
            ? s
            : ListingReportRules.StatusOpen;
        if (!ListingReportRules.Statuses.Contains(status))
        {
            return LambdaResponse.Error(400, "ADMIN_005", "status must be open, dismissed or actioned.");
        }

        var limit = Math.Min(ParseLimit(queryParams, DefaultReportPage), MaxReportPage);

        Dictionary<string, AttributeValue>? startKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            startKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (startKey == null)
            {
                return LambdaResponse.Error(400, "ADMIN_005", "Invalid nextToken.");
            }
        }

        var (rows, lastKey) = await reports.ListByStatusAsync(status, limit, startKey);

        // What each reported listing holds now: one read per distinct listing on the page.
        var listings = new List<ListingReportListingDto>();
        foreach (var listing in rows
                     .Select(r => (r.ListingType, r.ListingId))
                     .Distinct())
        {
            listings.Add(await ReadReportedListingAsync(listing.ListingType, listing.ListingId, context));
        }

        var response = new ListingReportListResponse
        {
            Reports = rows.Select(ToReportDto).ToList(),
            Listings = listings,
            NextToken = lastKey == null ? null : PaginationTokenHelper.EncodeNextToken(lastKey)
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.ListingReportListResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleReviewListingReportsAsync(
        string listingId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        var reports = ListingReportRepo;
        if (reports == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Listing reports table not configured.");
        }

        if (string.IsNullOrWhiteSpace(listingId)
            || listingId.Length > ListingReportRules.MaxListingIdLength
            || listingId.Contains('#'))
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid listing id.");
        }

        ReviewListingReportsRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.ReviewListingReportsRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        if (body.Decision == null || !ListingReportRules.Decisions.Contains(body.Decision))
        {
            return LambdaResponse.Error(400, "ADMIN_009", "Unknown decision. Use dismissed or actioned.");
        }

        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > ListingReportRules.MaxReviewNoteLength })
        {
            return LambdaResponse.Error(400, "ADMIN_005",
                $"note must be at most {ListingReportRules.MaxReviewNoteLength} characters.");
        }

        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var now = Clock().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // Every open report of the listing; a report decided by another admin meanwhile is left as it is.
        var reviewed = 0;
        foreach (var row in (await reports.ListByListingAsync(listingId))
                     .Where(r => r.Status == ListingReportRules.StatusOpen))
        {
            if (await reports.TryReviewAsync(row.ListingId, row.ReporterId, body.Decision, adminUserId, now, note))
            {
                reviewed++;
            }
        }

        if (reviewed > 0)
        {
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, $"listing-report-{listingId}", "listing_report", listingId,
                $"listing_report_{body.Decision}", ListingReportRules.StatusOpen, body.Decision,
                AuditContext.From(request, adminUserId, "admin"),
                metadata: note == null ? $"reports={reviewed}" : $"reports={reviewed};note={note}");
        }

        AuditLogger.LogAudit("ADMIN", "LISTING_REPORT_REVIEW",
            userId: adminUserId, correlationId: AuditLogger.GetCorrelationId(request),
            entityType: "listingReport", entityId: listingId, detail: $"{body.Decision};reports={reviewed}",
            outcome: "success");

        return LambdaResponse.Ok(new ReviewListingReportsResponse
        {
            ListingId = listingId,
            Decision = body.Decision,
            Reviewed = reviewed,
            ReviewedAt = now
        }, AppJsonContext.Default.ReviewListingReportsResponse);
    }

    /// <summary>
    /// The listing as it is now (crops or trips table, by partition key). Not found when the row is gone or
    /// the table is not configured; a failed read is logged and also shown as not found, so one bad listing
    /// does not hide the queue.
    /// </summary>
    private async Task<ListingReportListingDto> ReadReportedListingAsync(
        string listingType, string listingId, ILambdaContext context)
    {
        var isCrop = listingType == ListingReportRules.ListingCrop;
        var table = isCrop ? _cropsTable : _tripsTable;
        var notFound = new ListingReportListingDto { ListingId = listingId, ListingType = listingType, Found = false };
        if (string.IsNullOrEmpty(table))
        {
            return notFound;
        }

        Dictionary<string, AttributeValue>? item;
        try
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "#id = :id",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#id"] = isCrop ? "cropId" : "tripId" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = listingId } },
                Limit = 1
            });
            item = response.Items is { Count: > 0 } items ? items[0] : null;
        }
        catch (AmazonDynamoDBException ex)
        {
            context.Logger.LogWarning($"Reported listing read failed: [{ex.GetType().Name}]");
            return notFound;
        }

        if (item == null)
        {
            return notFound;
        }

        string? Text(string key) => item.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v.S) ? v.S : null;

        return isCrop
            ? new ListingReportListingDto
            {
                ListingId = listingId,
                ListingType = listingType,
                Found = true,
                Status = Text("status"),
                OwnerName = Text("farmerName"),
                CropType = Text("cropType"),
                Description = Text("description"),
                AudioNoteRef = Text("audioNoteRef")
            }
            : new ListingReportListingDto
            {
                ListingId = listingId,
                ListingType = listingType,
                Found = true,
                Status = Text("status"),
                OwnerName = Text("truckerName"),
                FromWoreda = Text("fromWoreda"),
                ToWoreda = Text("toWoreda"),
                DepartureDate = Text("departureDate")
            };
    }

    private static ListingReportDto ToReportDto(ListingReportRow row) => new()
    {
        ListingId = row.ListingId,
        ListingType = row.ListingType,
        OwnerId = row.OwnerId,
        ReporterId = row.ReporterId,
        Reason = row.Reason,
        Status = row.Status,
        CreatedAt = row.CreatedAt,
        ReviewedAt = row.ReviewedAt,
        ReviewedBy = row.ReviewedBy,
        ReviewNote = row.ReviewNote
    };
}

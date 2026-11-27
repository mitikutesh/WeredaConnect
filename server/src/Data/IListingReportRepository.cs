using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-67: one buyer's report of one listing (listing-report-api.md 1.0.0, Storage). Table
/// <c>{prefix}-listing-reports</c>: PK <c>listingId</c>, SK <c>reporterId</c>, so a reporter holds at most
/// one report per listing. <c>ListingType</c> is <c>crop</c> or <c>trip</c>; <c>OwnerId</c> is the
/// listing's farmer or trucker. <c>Status</c> is <c>open</c> until an admin decides (<c>dismissed</c> or
/// <c>actioned</c>), and with <c>CreatedAt</c> keys the <c>StatusCreatedAtIndex</c> admin queue. TTL on
/// <c>expiresAt</c> (epoch seconds, 180 days after the report). Only admins read these rows: the
/// reporter is never shown to the listing's owner.
/// </summary>
public sealed record ListingReportRow(
    string ListingId,
    string ReporterId,
    string ListingType,
    string OwnerId,
    string Reason,
    string Status,
    string CreatedAt,
    long ExpiresAt,
    string? ReviewedAt = null,
    string? ReviewedBy = null,
    string? ReviewNote = null);

public interface IListingReportRepository
{
    /// <summary>Whether the reporter already reported the listing (strongly consistent read).</summary>
    Task<bool> ExistsAsync(string listingId, string reporterId);

    /// <summary>Puts the row unless the reporter already reported the listing. True when it was new.</summary>
    Task<bool> TryCreateAsync(ListingReportRow row);

    /// <summary>
    /// One page of the reports in <paramref name="status"/> from the <c>StatusCreatedAtIndex</c>: oldest
    /// first for <c>open</c> (a queue), newest first otherwise.
    /// </summary>
    Task<(List<ListingReportRow> Rows, Dictionary<string, AttributeValue>? LastEvaluatedKey)> ListByStatusAsync(
        string status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>Every report of the listing (strongly consistent, all pages up to a bound).</summary>
    Task<List<ListingReportRow>> ListByListingAsync(string listingId);

    /// <summary>
    /// Records an admin decision on one report while it is still <c>open</c>. False when the row is gone
    /// or was already decided.
    /// </summary>
    Task<bool> TryReviewAsync(
        string listingId, string reporterId, string decision, string reviewedBy, string reviewedAt, string? reviewNote);
}

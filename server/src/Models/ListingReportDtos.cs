namespace WeredaConnect.Server.Models;

// FUP-67: the admin queue of listing reports (listing-report-api.md 1.0.0 section 2).

/// <summary>One buyer's report of one listing, as the admin queue shows it.</summary>
public sealed record ListingReportDto
{
    public required string ListingId { get; init; }

    /// <summary><c>crop</c> or <c>trip</c>.</summary>
    public required string ListingType { get; init; }

    /// <summary>The listing's farmer or trucker.</summary>
    public required string OwnerId { get; init; }

    /// <summary>The buyer who reported it. Admin only: never shown to the owner.</summary>
    public required string ReporterId { get; init; }

    /// <summary><c>contact_info</c>, <c>misleading</c>, <c>scam</c>, <c>offensive</c> or <c>other</c>.</summary>
    public required string Reason { get; init; }

    /// <summary><c>open</c>, <c>dismissed</c> or <c>actioned</c>.</summary>
    public required string Status { get; init; }

    public required string CreatedAt { get; init; }
    public string? ReviewedAt { get; init; }
    public string? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
}

/// <summary>
/// What the reported listing holds now, read when the queue is listed. <see cref="Found"/> is false when
/// the listing row is gone; then only the ids are set.
/// </summary>
public sealed record ListingReportListingDto
{
    public required string ListingId { get; init; }
    public required string ListingType { get; init; }
    public required bool Found { get; init; }
    public string? Status { get; init; }
    public string? OwnerName { get; init; }

    // Crop listing
    public string? CropType { get; init; }
    public string? Description { get; init; }

    /// <summary>The crop's audio note reference, which no server check can screen (FUP-49).</summary>
    public string? AudioNoteRef { get; init; }

    // Trip
    public string? FromWoreda { get; init; }
    public string? ToWoreda { get; init; }
    public string? DepartureDate { get; init; }
}

/// <summary>GET /admin/listing-reports response.</summary>
public sealed record ListingReportListResponse
{
    public required List<ListingReportDto> Reports { get; init; }

    /// <summary>One entry per distinct listing of <see cref="Reports"/>.</summary>
    public required List<ListingReportListingDto> Listings { get; init; }

    public string? NextToken { get; init; }
}

/// <summary>
/// POST /admin/listing-reports/{listingId}/review request body. <see cref="Decision"/> is
/// <c>dismissed</c> (nothing wrong) or <c>actioned</c> (the admin acted, for example suspended the owner).
/// </summary>
public sealed record ReviewListingReportsRequest
{
    public string? Decision { get; init; }
    public string? Note { get; init; }
}

/// <summary>POST /admin/listing-reports/{listingId}/review response.</summary>
public sealed record ReviewListingReportsResponse
{
    public required string ListingId { get; init; }
    public required string Decision { get; init; }

    /// <summary>How many open reports this call decided (0 when another admin decided them first).</summary>
    public required int Reviewed { get; init; }

    public required string ReviewedAt { get; init; }
}

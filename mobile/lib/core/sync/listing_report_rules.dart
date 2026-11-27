/// FUP-67: the `listing_report` sync entity (listing-report-api.md 1.0.0,
/// sync-protocol.md 1.44.0). A buyer reports a crop listing or a trip, which
/// buyers see all of, to the admins with a reason code; there is no free
/// text. Create only, one report per buyer and listing: the server answers a
/// repeat `duplicate`. Every refusal starts with `SYNC_VALIDATION`, which the
/// sync queue already treats as permanent.
library;

/// The sync entity type.
const String listingReportEntityType = 'listing_report';

/// What kind of listing is reported.
enum ReportedListingType {
  crop('crop'),
  trip('trip');

  const ReportedListingType(this.wire);

  /// The `listingType` the server reads.
  final String wire;
}

/// Why a buyer reports a listing.
enum ListingReportReason {
  /// A phone number or other contact in the listing, its photos or its voice
  /// note, which the server cannot screen (FUP-49).
  contactInfo('contact_info'),
  misleading('misleading'),
  scam('scam'),
  offensive('offensive'),
  other('other');

  const ListingReportReason(this.wire);

  /// The `reason` code the server reads.
  final String wire;
}

/// Reports a buyer may send per UTC day (the server's limit).
const int listingReportDailyLimit = 10;

/// The entity id of a report: `{listingType}#{listingId}`.
String listingReportEntityId(ReportedListingType type, String listingId) =>
    '${type.wire}#$listingId';

/// The `data` of a `listing_report` create.
Map<String, dynamic> listingReportData({
  required ReportedListingType listingType,
  required String listingId,
  required ListingReportReason reason,
}) =>
    {
      'listingType': listingType.wire,
      'listingId': listingId,
      'reason': reason.wire,
    };

/// The start of the server's refusal for a buyer over the daily limit.
const String listingReportLimitPrefix = 'SYNC_VALIDATION: report limit reached';

/// Whether a /sync operation error refuses a report over the daily limit.
bool isListingReportLimitMessage(String? message) =>
    message != null && message.startsWith(listingReportLimitPrefix);

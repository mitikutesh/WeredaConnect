import 'package:wereda_connect/core/sync/listing_report_rules.dart';

/// What happened to a buyer's report.
enum ListingReportOutcome {
  /// Sent (web), or saved on the phone and sent by the sync queue when the
  /// phone is online (mobile).
  accepted,

  /// The server already holds this buyer's report of the listing.
  alreadyReported,
}

/// FUP-67: a buyer reports a crop listing or a trip to the admins
/// (listing-report-api.md 1.0.0). Mobile queues the `listing_report` create
/// for the sync queue (offline-first); web sends it straight to /sync.
///
/// Only buyers report: a farmer or a trucker sees only their own listings.
/// Only admins read reports; the owner never learns who reported.
abstract class BaseListingReportRepository {
  /// Reports [listingId] for [reason]. Throws when the report could not be
  /// saved or sent (the caller maps the error with `mapErrorToCode`).
  Future<ListingReportOutcome> report({
    required String reporterId,
    required ReportedListingType listingType,
    required String listingId,
    required ListingReportReason reason,
  });
}

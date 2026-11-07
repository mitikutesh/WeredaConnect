import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/sync/listing_report_rules.dart';

/// FUP-67: the wire shape of a `listing_report` (listing-report-api.md 1.0.0).
void main() {
  test('the entity id is the listing type and id', () {
    expect(
      listingReportEntityId(ReportedListingType.crop, 'c-1'),
      'crop#c-1',
    );
    expect(
      listingReportEntityId(ReportedListingType.trip, 't-1'),
      'trip#t-1',
    );
  });

  test('the reason codes are the server codes', () {
    expect(
      ListingReportReason.values.map((r) => r.wire),
      ['contact_info', 'misleading', 'scam', 'offensive', 'other'],
    );
  });

  test('the data carries no free text', () {
    expect(
      listingReportData(
        listingType: ReportedListingType.crop,
        listingId: 'c-1',
        reason: ListingReportReason.scam,
      ),
      {'listingType': 'crop', 'listingId': 'c-1', 'reason': 'scam'},
    );
  });

  test('only the limit refusal reads as the limit', () {
    expect(
      isListingReportLimitMessage(
        'SYNC_VALIDATION: report limit reached. A buyer can send at most 10 '
        'reports a day.',
      ),
      isTrue,
    );
    expect(
      isListingReportLimitMessage(
        'SYNC_VALIDATION: The reported listing does not exist.',
      ),
      isFalse,
    );
    expect(isListingReportLimitMessage(null), isFalse);
  });
}

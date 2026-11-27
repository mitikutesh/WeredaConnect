import 'dart:convert';

import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';

/// Mobile listing reports: the `listing_report` create goes into the sync
/// queue, so a report made offline is sent when the phone is online. No local
/// row: the report is only for the admins, and the server keeps one per buyer
/// and listing (a repeat answers `duplicate`, which the queue settles).
class ListingReportRepository extends BaseListingReportRepository {
  ListingReportRepository({required SyncQueue syncQueue})
      : _syncQueue = syncQueue;

  final SyncQueue _syncQueue;

  static const _uuid = Uuid();

  @override
  Future<ListingReportOutcome> report({
    required String reporterId,
    required ReportedListingType listingType,
    required String listingId,
    required ListingReportReason reason,
  }) async {
    // A full queue throws: nothing was saved, and the sheet says so.
    await _syncQueue.enqueue(
      userId: reporterId,
      entityType: listingReportEntityType,
      entityId: listingReportEntityId(listingType, listingId),
      action: 'create',
      data: jsonEncode(
        listingReportData(
          listingType: listingType,
          listingId: listingId,
          reason: reason,
        ),
      ),
      operationId: _uuid.v4(),
    );
    return ListingReportOutcome.accepted;
  }
}

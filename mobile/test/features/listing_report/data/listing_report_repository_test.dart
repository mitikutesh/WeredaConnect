import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/listing_report/data/listing_report_repository.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';

/// FUP-67: on the phone a report is a queued `listing_report` create, so a
/// report made offline is sent later by the sync queue.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;
  late ListingReportRepository repository;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('listing_report_repo_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'test_listing_report_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
    repository = ListingReportRepository(syncQueue: syncQueue);
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  test('a report is queued as a listing_report create for the buyer', () async {
    final outcome = await repository.report(
      reporterId: 'buyer-1',
      listingType: ReportedListingType.crop,
      listingId: 'crop-1',
      reason: ListingReportReason.contactInfo,
    );

    expect(outcome, ListingReportOutcome.accepted);
    final pending = await syncQueue.getPendingOperations();
    expect(pending, hasLength(1));
    final op = pending.single;
    expect(op.entityType, 'listing_report');
    expect(op.entityId, 'crop#crop-1');
    expect(op.action, 'create');
    // The queue's key starts with the reporter, so another user on a shared
    // phone never sends it.
    expect(op.idempotencyKey, startsWith('buyer-1:listing_report:crop#crop-1'));
    expect(jsonDecode(op.data), {
      'listingType': 'crop',
      'listingId': 'crop-1',
      'reason': 'contact_info',
    });
  });

  test('a trip report names the trip', () async {
    await repository.report(
      reporterId: 'buyer-1',
      listingType: ReportedListingType.trip,
      listingId: 'trip-9',
      reason: ListingReportReason.scam,
    );

    final op = (await syncQueue.getPendingOperations()).single;
    expect(op.entityId, 'trip#trip-9');
    expect(
      (jsonDecode(op.data) as Map<String, dynamic>)['listingType'],
      'trip',
    );
  });

  test('nothing is written but the queued operation', () async {
    await repository.report(
      reporterId: 'buyer-1',
      listingType: ReportedListingType.crop,
      listingId: 'crop-1',
      reason: ListingReportReason.other,
    );

    expect(await isar.syncOperations.count(), 1);
  });
}

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-18: builds before sync-protocol.md 1.20.0 queued a `cropListing`
/// update on delivery create and confirm. The server routes `crop_listing`
/// only, so the op failed, was reset by `retryFailed()` on every reconnect and
/// was sent again. The app start drops every such op once.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_queue_legacy_test_');
    isar = await Isar.open(
      [CropListingSchema, TripSchema, DeliverySchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'test_legacy_crop_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  /// Write an operation as an older build left it in the queue.
  Future<void> putOp(
    String operationId, {
    required String entityType,
    String entityId = 'listing-1',
    String userId = 'trucker-1',
    SyncStatus status = SyncStatus.pending,
    int retryCount = 0,
  }) =>
      isar.writeTxn(() async {
        await isar.syncOperations.put(
          SyncOperation()
            ..operationId = operationId
            ..idempotencyKey =
                '$userId:$entityType:$entityId:update:$operationId'
            ..entityType = entityType
            ..entityId = entityId
            ..action = 'update'
            ..data = '{"status":"reserved","version":2}'
            ..status = status
            ..retryCount = retryCount
            ..createdAt = DateTime.now().toUtc(),
        );
      });

  Future<Map<String, SyncStatus>> remainingOps() async => {
        for (final op in await isar.syncOperations.where().findAll())
          op.operationId: op.status,
      };

  group('dropLegacyCropListingOperations', () {
    test('drops every cropListing op, whatever its status or user', () async {
      // Trucker's create side effect, waiting to be sent.
      await putOp('legacy-pending', entityType: 'cropListing');
      // Buyer's confirm side effect, caught mid-sync by an app kill.
      await putOp(
        'legacy-syncing',
        entityType: 'cropListing',
        userId: 'buyer-1',
        status: SyncStatus.syncing,
      );
      // Out of retries: the next reconnect would reset and resend it.
      await putOp(
        'legacy-exhausted',
        entityType: 'cropListing',
        userId: 'buyer-1',
        status: SyncStatus.failed,
        retryCount: SyncQueue.maxRetries,
      );
      // Real operations stay, the farmer's own crop_listing included.
      await putOp('farmer-crop', entityType: 'crop_listing', userId: 'farmer');
      await putOp(
        'farmer-crop-failed',
        entityType: 'crop_listing',
        userId: 'farmer',
        status: SyncStatus.failed,
        retryCount: SyncQueue.maxRetries,
      );
      await putOp('delivery', entityType: 'delivery', entityId: 'del-1');

      expect(await syncQueue.dropLegacyCropListingOperations(), 3);

      expect(await remainingOps(), {
        'farmer-crop': SyncStatus.pending,
        'farmer-crop-failed': SyncStatus.failed,
        'delivery': SyncStatus.pending,
      });
    });

    test('ends the reconnect loop: nothing left to reset or send', () async {
      await putOp(
        'legacy-exhausted',
        entityType: 'cropListing',
        status: SyncStatus.failed,
        retryCount: SyncQueue.maxRetries,
      );
      await putOp('legacy-pending', entityType: 'cropListing');
      expect(await syncQueue.getFailedCount(), 1);

      await syncQueue.dropLegacyCropListingOperations();

      expect(await syncQueue.retryFailed(), 0);
      expect(await syncQueue.getPendingOperations(), isEmpty);
      expect(await syncQueue.getPendingCount(), 0);
      expect(await syncQueue.getFailedCount(), 0);
    });

    test('a second run removes nothing', () async {
      await putOp('legacy', entityType: 'cropListing');
      await putOp('delivery', entityType: 'delivery', entityId: 'del-1');

      expect(await syncQueue.dropLegacyCropListingOperations(), 1);
      expect(await syncQueue.dropLegacyCropListingOperations(), 0);
      expect(await remainingOps(), {'delivery': SyncStatus.pending});
    });

    test('an empty queue is a no-op', () async {
      expect(await syncQueue.dropLegacyCropListingOperations(), 0);
    });
  });

  group('the next crop pull after the drop', () {
    late _MockApiClient api;

    setUp(() {
      api = _MockApiClient();
    });

    /// A listing as an older build left it on the buyer's device: flipped
    /// locally and its version bumped, though the server never took it.
    Future<void> putStaleListing(String listingId, ListingStatus status) =>
        isar.writeTxn(() async {
          await isar.cropListings.put(
            CropListing()
              ..listingId = listingId
              ..farmerId = 'farmer-1'
              ..cropType = 'teff'
              ..quantity = 10
              ..pricePerKg = 25
              ..location = (LatLng()
                ..latitude = 9.0
                ..longitude = 38.7)
              ..locationName = 'Bishoftu'
              ..description = ''
              ..status = status
              ..createdAt = DateTime.now().toUtc()
              ..updatedAt = DateTime.now().toUtc()
              ..version = 4,
          );
        });

    test('shows the server listing the dropped op had held back', () async {
      // Partial order: the server still lists it with stock left.
      await putStaleListing('still-listed', ListingStatus.sold);
      // Fully ordered: the server took it off the market.
      await putStaleListing('sold-out', ListingStatus.reserved);
      await putOp(
        'legacy-1',
        entityType: 'cropListing',
        entityId: 'still-listed',
        userId: 'buyer-1',
      );
      await putOp(
        'legacy-2',
        entityType: 'cropListing',
        entityId: 'sold-out',
        userId: 'buyer-1',
        status: SyncStatus.failed,
        retryCount: SyncQueue.maxRetries,
      );

      await syncQueue.dropLegacyCropListingOperations();

      when(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((inv) async {
        final path = inv.positionalArguments.first as String;
        if (!path.contains('/crops')) return <String, dynamic>{};
        return {
          'crops': [
            {
              'cropId': 'still-listed',
              'farmerId': 'farmer-1',
              'farmerName': 'Abebe',
              'cropType': 'teff',
              'woreda': 'Bishoftu',
              'quantityKg': 6,
              'pricePerKg': 25,
              'status': 'available',
              'createdAt': '2026-10-20T08:00:00.000Z',
              'updatedAt': '2026-10-21T08:00:00.000Z',
              'version': 2,
              'description': '',
            },
          ],
        };
      });
      await IsarMarketplaceSync(
        api: api,
        isar: isar,
        getCurrentUserId: () async => 'buyer-1',
      ).pullCrops();

      final listings = {
        for (final c in await isar.cropListings.where().findAll())
          c.listingId: c,
      };
      expect(listings.keys, ['still-listed']);
      expect(listings['still-listed']!.status, ListingStatus.active);
      expect(listings['still-listed']!.quantity, 6);
      expect(listings['still-listed']!.version, 2);
    });
  });
}

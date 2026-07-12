import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';

void main() {
  group('DeliveryRepository – data isolation', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late DeliveryRepository repository;

    // Delivery IDs populated in setUp
    late String del1Id;
    const tripId = 'trip-isolation-test';

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir =
          Directory.systemTemp.createTempSync('delivery_repo_isolation_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name:
            'test_delivery_isolation_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      repository = DeliveryRepository(
        isar: isar,
        syncQueue: syncQueue,
      );

      // Delivery 1: farmer-A, trucker-1, buyer-1
      final d1 = await repository.createDelivery(
        tripId: tripId,
        orderId: 'order-test-001',
        listingId: 'listing-001',
        farmerId: 'farmer-A',
        farmerName: 'Farmer A',
        truckerId: 'trucker-1',
        truckerName: 'Trucker One',
        buyerId: 'buyer-1',
        buyerName: 'Buyer One',
        qrToken: 'qr-token-1',
      );
      del1Id = d1.deliveryId;

      // Delivery 2: farmer-B, trucker-2, buyer-2
      await repository.createDelivery(
        tripId: tripId,
        orderId: 'order-test-001',
        listingId: 'listing-002',
        farmerId: 'farmer-B',
        farmerName: 'Farmer B',
        truckerId: 'trucker-2',
        truckerName: 'Trucker Two',
        buyerId: 'buyer-2',
        buyerName: 'Buyer Two',
        qrToken: 'qr-token-2',
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('getDeliveryById returns delivery for participant (farmer-A)',
        () async {
      final result =
          await repository.getDeliveryById(del1Id, userId: 'farmer-A');

      expect(result, isNotNull);
      expect(result!.deliveryId, del1Id);
      expect(result.farmerId, 'farmer-A');
    });

    test('getDeliveryById returns null for non-participant (farmer-B on del1)',
        () async {
      final result =
          await repository.getDeliveryById(del1Id, userId: 'farmer-B');

      expect(result, isNull);
    });

    test('getDeliveriesByStatus returns only farmer-A delivery for farmer-A',
        () async {
      final deliveries = await repository.getDeliveriesByStatus(
        DeliveryStatus.pending,
        userId: 'farmer-A',
      );

      expect(deliveries.length, 1);
      expect(deliveries.first.deliveryId, del1Id);
      expect(deliveries.first.farmerId, 'farmer-A');
    });

    test('getDeliveriesByTrip returns only farmer-A delivery for farmer-A',
        () async {
      final deliveries = await repository.getDeliveriesByTrip(
        tripId,
        userId: 'farmer-A',
      );

      expect(deliveries.length, 1);
      expect(deliveries.first.deliveryId, del1Id);
      expect(deliveries.first.farmerId, 'farmer-A');
    });
  });
}

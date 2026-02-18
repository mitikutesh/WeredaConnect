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
  group('DeliveryRepository', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late DeliveryRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('delivery_repo_test_');
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
        name: 'test_delivery_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      repository = DeliveryRepository(
        isar: isar,
        syncQueue: syncQueue,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    // --- Create ---

    test('createDelivery saves to Isar with correct fields', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe Kebede',
        truckerId: 'trucker-001',
        truckerName: 'Dawit Assefa',
        buyerId: 'buyer-001',
        buyerName: 'Sara Hailu',
        qrToken: 'qr-token-abc',
      );

      expect(delivery.tripId, 'trip-001');
      expect(delivery.listingId, 'listing-001');
      expect(delivery.farmerId, 'farmer-001');
      expect(delivery.farmerName, 'Abebe Kebede');
      expect(delivery.truckerId, 'trucker-001');
      expect(delivery.truckerName, 'Dawit Assefa');
      expect(delivery.buyerId, 'buyer-001');
      expect(delivery.buyerName, 'Sara Hailu');
      expect(delivery.qrToken, 'qr-token-abc');
      expect(delivery.status, DeliveryStatus.pending);
      expect(delivery.version, 1);
      expect(delivery.deliveryId, isNotEmpty);
      expect(delivery.createdAt, delivery.updatedAt);

      // Verify persisted in Isar
      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb, isNotNull);
      expect(fromDb!.farmerId, 'farmer-001');
    });

    test('createDelivery enqueues sync operation', () async {
      await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe Kebede',
        truckerId: 'trucker-001',
        truckerName: 'Dawit Assefa',
        buyerId: 'buyer-001',
        buyerName: 'Sara Hailu',
        qrToken: 'qr-token-abc',
      );

      final pendingCount = await syncQueue.getPendingCount();
      expect(pendingCount, 1);

      final pending = await syncQueue.getPendingOperations();
      expect(pending.first.entityType, 'delivery');
      expect(pending.first.action, 'create');
    });

    test('createDelivery generates unique deliveryId', () async {
      final d1 = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-1',
      );
      final d2 = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-002',
        farmerId: 'farmer-002',
        farmerName: 'Kebede',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-002',
        buyerName: 'Meron',
        qrToken: 'qr-2',
      );

      expect(d1.deliveryId, isNot(d2.deliveryId));
    });

    // --- Confirm Pickup ---

    test('confirmPickup updates status to pickedUp with GPS and photo',
        () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      final gps = LatLng()
        ..latitude = 9.0192
        ..longitude = 38.7525;

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
        gpsPickup: gps,
        photoRef: 'photos/pickup-001.jpg',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.pickedUp);
      expect(fromDb.version, 2);
      expect(fromDb.pickedUpAt, isNotNull);
      expect(fromDb.gpsPickup, isNotNull);
      expect(fromDb.gpsPickup!.latitude, closeTo(9.0192, 0.0001));
      expect(fromDb.gpsPickup!.longitude, closeTo(38.7525, 0.0001));
      expect(fromDb.pickupPhotoRef, 'photos/pickup-001.jpg');
    });

    test('confirmPickup enqueues sync operation', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2); // create + update
      expect(pending.last.action, 'update');
      expect(pending.last.entityType, 'delivery');
    });

    // --- Confirm Delivery ---

    /// Helper: transition a delivery to inTransit via direct Isar write.
    /// In production this transition happens server-side; tests simulate it.
    Future<void> transitionToInTransit(String deliveryId) async {
      final d = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(deliveryId)
          .findFirst();
      await isar.writeTxn(() async {
        d!.status = DeliveryStatus.inTransit;
        d.updatedAt = DateTime.now().toUtc();
        d.version += 1;
        await isar.deliverys.put(d);
      });
    }

    test('confirmDelivery updates status to delivered', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await transitionToInTransit(delivery.deliveryId);

      final gps = LatLng()
        ..latitude = 8.9806
        ..longitude = 38.7578;

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
        gpsDropoff: gps,
        photoRef: 'photos/dropoff-001.jpg',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.delivered);
      expect(fromDb.version, 4); // create(1) + pickup(2) + inTransit(3) + deliver(4)
      expect(fromDb.confirmedAt, isNotNull);
      expect(fromDb.gpsDropoff, isNotNull);
      expect(fromDb.gpsDropoff!.latitude, closeTo(8.9806, 0.0001));
      expect(fromDb.dropoffPhotoRef, 'photos/dropoff-001.jpg');
    });

    test('confirmDelivery on pickedUp delivery is a no-op', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      // Status should remain pickedUp — no transition occurred
      expect(fromDb!.status, DeliveryStatus.pickedUp);
      expect(fromDb.version, 2); // create(1) + pickup(2), no delivery bump

      // No sync operation for the confirmDelivery call
      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2); // create + pickup only
    });

    test('confirmDelivery enqueues sync operation', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await transitionToInTransit(delivery.deliveryId);

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 3); // create + pickup + delivery
      expect(pending.last.action, 'update');
    });

    // --- Raise Dispute ---

    test('raiseDispute updates status to disputed with reason', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      // Must reach delivered status before disputing
      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await transitionToInTransit(delivery.deliveryId);
      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );

      await repository.raiseDispute(
        deliveryId: delivery.deliveryId,
        userId: 'buyer-001',
        reason: 'Wrong quantity delivered',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.disputed);
      expect(fromDb.disputeReason, 'Wrong quantity delivered');
      // create(1) + pickup(2) + inTransit(3) + deliver(4) + dispute(5)
      expect(fromDb.version, 5);
    });

    test('raiseDispute enqueues sync operation', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      // Must reach delivered status before disputing
      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await transitionToInTransit(delivery.deliveryId);
      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );

      await repository.raiseDispute(
        deliveryId: delivery.deliveryId,
        userId: 'buyer-001',
        reason: 'Quality issue',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 4); // create + pickup + delivery + dispute
      expect(pending.last.data, contains('"disputeReason":"Quality issue"'));
    });

    // --- Cancel ---

    test('cancelDelivery updates status to cancelled', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.cancelDelivery(
        deliveryId: delivery.deliveryId,
        userId: 'trucker-001',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.cancelled);
      expect(fromDb.version, 2);
    });

    test('cancelDelivery enqueues sync operation', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.cancelDelivery(
        deliveryId: delivery.deliveryId,
        userId: 'trucker-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2); // create + delete
      expect(pending.last.action, 'delete');
    });

    // --- Query Methods ---

    test('getDeliveriesByTrip returns only deliveries for that trip', () async {
      await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-1',
      );
      await repository.createDelivery(
        tripId: 'trip-002',
        listingId: 'listing-002',
        farmerId: 'farmer-002',
        farmerName: 'Kebede',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-002',
        buyerName: 'Meron',
        qrToken: 'qr-2',
      );
      await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-003',
        farmerId: 'farmer-003',
        farmerName: 'Taye',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-003',
        buyerName: 'Hana',
        qrToken: 'qr-3',
      );

      final deliveries = await repository.getDeliveriesByTrip('trip-001');
      expect(deliveries.length, 2);
      expect(
        deliveries.every((d) => d.tripId == 'trip-001'),
        isTrue,
      );
    });

    test('getDeliveriesByStatus returns deliveries with matching status',
        () async {
      final d1 = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-1',
      );
      await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-002',
        farmerId: 'farmer-002',
        farmerName: 'Kebede',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-002',
        buyerName: 'Meron',
        qrToken: 'qr-2',
      );

      // Confirm pickup on first delivery
      await repository.confirmPickup(
        deliveryId: d1.deliveryId,
        truckerId: 'trucker-001',
      );

      final pickedUp =
          await repository.getDeliveriesByStatus(DeliveryStatus.pickedUp);
      expect(pickedUp.length, 1);
      expect(pickedUp.first.deliveryId, d1.deliveryId);

      final pending =
          await repository.getDeliveriesByStatus(DeliveryStatus.pending);
      expect(pending.length, 1);
    });

    test('getDeliveryById returns correct delivery', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      final result = await repository.getDeliveryById(delivery.deliveryId);
      expect(result, isNotNull);
      expect(result!.farmerId, 'farmer-001');
    });

    test('getDeliveryById returns null for nonexistent ID', () async {
      final result = await repository.getDeliveryById('nonexistent-id');
      expect(result, isNull);
    });

    // --- Authorization guards ---

    test('confirmPickup with wrong truckerId is a no-op', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'wrong-trucker',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.pending);
      expect(fromDb.version, 1);

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 1); // only create, no pickup
    });

    test('confirmDelivery with wrong buyerId is a no-op', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await transitionToInTransit(delivery.deliveryId);

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'wrong-buyer',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.inTransit);
      expect(fromDb.version, 3); // create(1) + pickup(2) + inTransit(3)

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2); // create + pickup only
    });

    test('cancelDelivery with non-participant userId is a no-op', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.cancelDelivery(
        deliveryId: delivery.deliveryId,
        userId: 'random-user',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.pending);
      expect(fromDb.version, 1);

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 1); // only create, no cancel
    });

    test('cancelDelivery by farmer (participant) succeeds', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.cancelDelivery(
        deliveryId: delivery.deliveryId,
        userId: 'farmer-001',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.cancelled);
    });

    test('cancelDelivery by buyer (participant) succeeds', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      await repository.cancelDelivery(
        deliveryId: delivery.deliveryId,
        userId: 'buyer-001',
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.status, DeliveryStatus.cancelled);
    });

    // --- Null delivery (no-op) ---

    test('confirmPickup on nonexistent delivery does nothing', () async {
      await repository.confirmPickup(
        deliveryId: 'nonexistent-id',
        truckerId: 'trucker-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending, isEmpty);
    });

    test('confirmDelivery on nonexistent delivery does nothing', () async {
      await repository.confirmDelivery(
        deliveryId: 'nonexistent-id',
        buyerId: 'buyer-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending, isEmpty);
    });

    test('raiseDispute on nonexistent delivery does nothing', () async {
      await repository.raiseDispute(
        deliveryId: 'nonexistent-id',
        userId: 'buyer-001',
        reason: 'Issue',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending, isEmpty);
    });

    test('cancelDelivery on nonexistent delivery does nothing', () async {
      await repository.cancelDelivery(
        deliveryId: 'nonexistent-id',
        userId: 'trucker-001',
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending, isEmpty);
    });

    // --- Amharic names preserved ---

    test('createDelivery preserves Amharic names', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'ታደሰ ወርቁ',
        truckerId: 'trucker-001',
        truckerName: 'ዳዊት አሰፋ',
        buyerId: 'buyer-001',
        buyerName: 'ሳራ ሃይሉ',
        qrToken: 'qr-token',
      );

      expect(delivery.farmerName, 'ታደሰ ወርቁ');
      expect(delivery.truckerName, 'ዳዊት አሰፋ');
      expect(delivery.buyerName, 'ሳራ ሃይሉ');

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.farmerName, 'ታደሰ ወርቁ');
      expect(fromDb.truckerName, 'ዳዊት አሰፋ');
      expect(fromDb.buyerName, 'ሳራ ሃይሉ');
    });

    // --- GPS boundary values (Ethiopian coordinates) ---

    test('GPS boundary values for Ethiopian coordinates', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );

      // Southern Ethiopia boundary (~3°N)
      final pickupGps = LatLng()
        ..latitude = 3.4
        ..longitude = 33.0;

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
        gpsPickup: pickupGps,
      );

      // Transition to inTransit before confirming delivery
      await transitionToInTransit(delivery.deliveryId);

      // Northern Ethiopia boundary (~15°N)
      final dropoffGps = LatLng()
        ..latitude = 14.9
        ..longitude = 47.5;

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
        gpsDropoff: dropoffGps,
      );

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.gpsPickup!.latitude, closeTo(3.4, 0.001));
      expect(fromDb.gpsPickup!.longitude, closeTo(33.0, 0.001));
      expect(fromDb.gpsDropoff!.latitude, closeTo(14.9, 0.001));
      expect(fromDb.gpsDropoff!.longitude, closeTo(47.5, 0.001));
    });

    // --- Version control ---

    test('version increments correctly through lifecycle', () async {
      final delivery = await repository.createDelivery(
        tripId: 'trip-001',
        listingId: 'listing-001',
        farmerId: 'farmer-001',
        farmerName: 'Abebe',
        truckerId: 'trucker-001',
        truckerName: 'Dawit',
        buyerId: 'buyer-001',
        buyerName: 'Sara',
        qrToken: 'qr-token',
      );
      expect(delivery.version, 1);

      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      var fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.version, 2);

      await transitionToInTransit(delivery.deliveryId);
      fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.version, 3);

      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );
      fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo(delivery.deliveryId)
          .findFirst();
      expect(fromDb!.version, 4);
    });

    // --- Empty results ---

    test('getDeliveriesByTrip returns empty list for unknown trip', () async {
      final deliveries =
          await repository.getDeliveriesByTrip('nonexistent-trip');
      expect(deliveries, isEmpty);
    });

    test('getDeliveriesByStatus returns empty when no matches', () async {
      final deliveries =
          await repository.getDeliveriesByStatus(DeliveryStatus.disputed);
      expect(deliveries, isEmpty);
    });
  });
}

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';

void main() {
  group('Isar Schemas Tests', () {
    late Isar isar;
    late Directory tempDir;

    setUpAll(() async {
      // Initialize Isar native library for unit tests.
      // Downloads the binary from GitHub releases if not already present.
      await Isar.initializeIsarCore(download: true);

      // Use a temp directory instead of getApplicationDocumentsDirectory()
      // so the test runs in flutter test without a device/app context.
      tempDir = Directory.systemTemp.createTempSync('isar_schema_test_');

      // Open Isar database with all schemas
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
        name: 'test_schemas',
      );
    });

    tearDownAll(() async {
      // Clean up database
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('User schema: create, read, and query', () async {
      // Create a user record
      final user = User()
        ..userId = 'user-001'
        ..phoneNumber = '+251911234567'
        ..name = 'አበበ በቀለ' // Abebe Bekele in Amharic
        ..faydaId = null
        ..role = UserRole.farmer
        ..woreda = 'Bishoftu'
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.users.put(user);
      });

      // Query back by userId
      final retrieved =
          await isar.users.filter().userIdEqualTo('user-001').findFirst();

      // Verify
      expect(retrieved, isNotNull);
      expect(retrieved!.userId, 'user-001');
      expect(retrieved.phoneNumber, '+251911234567');
      expect(retrieved.name, 'አበበ በቀለ');
      expect(retrieved.role, UserRole.farmer);
      expect(retrieved.woreda, 'Bishoftu');
      expect(retrieved.version, 1);
    });

    test('CropListing schema: create with embedded LatLng', () async {
      // Create a crop listing
      final listing = CropListing()
        ..listingId = 'listing-001'
        ..farmerId = 'user-001'
        ..cropType = 'teff'
        ..quantity = 50.0 // 50 quintals
        ..pricePerKg = 85.0 // 85 ETB per kg
        ..location = (LatLng()
          ..latitude = 8.7519
          ..longitude = 38.9811)
        ..description = 'High quality white teff from Bishoftu region'
        ..audioNoteRef = null
        ..status = ListingStatus.active
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.cropListings.put(listing);
      });

      // Query back by status
      final activeListing = await isar.cropListings
          .filter()
          .statusEqualTo(ListingStatus.active)
          .findFirst();

      // Verify
      expect(activeListing, isNotNull);
      expect(activeListing!.listingId, 'listing-001');
      expect(activeListing.cropType, 'teff');
      expect(activeListing.quantity, 50.0);
      expect(activeListing.location.latitude, closeTo(8.7519, 0.0001));
      expect(activeListing.location.longitude, closeTo(38.9811, 0.0001));
    });

    test('Trip schema: create with matched listings', () async {
      // Create a trip
      final trip = Trip()
        ..tripId = 'trip-001'
        ..truckerId = 'user-002'
        ..truckerName = 'Tadesse Worku'
        ..truckerPhone = '+251912345678'
        ..fromWoreda = 'Bishoftu'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime.now().add(const Duration(days: 2)).toUtc()
        ..capacity = 100.0 // 100 quintals
        ..pricePerQuintal = 200.0 // 200 ETB per quintal
        ..status = TripStatus.matched
        ..matchedListings = ['listing-001']
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.trips.put(trip);
      });

      // Query back by trucker ID
      final truckerTrip =
          await isar.trips.filter().truckerIdEqualTo('user-002').findFirst();

      // Verify
      expect(truckerTrip, isNotNull);
      expect(truckerTrip!.tripId, 'trip-001');
      expect(truckerTrip.fromWoreda, 'Bishoftu');
      expect(truckerTrip.toWoreda, 'Addis Ababa');
      expect(truckerTrip.status, TripStatus.matched);
      expect(truckerTrip.matchedListings, contains('listing-001'));
    });

    test('Delivery schema: create with GPS tracking', () async {
      // Create a delivery
      final now = DateTime.now().toUtc();
      final delivery = Delivery()
        ..deliveryId = 'delivery-001'
        ..tripId = 'trip-001'
        ..listingId = 'listing-001'
        ..farmerId = 'farmer-001'
        ..farmerName = 'Abebe Kebede'
        ..truckerId = 'trucker-001'
        ..truckerName = 'Tadesse Worku'
        ..buyerId = 'buyer-001'
        ..buyerName = 'Mekonnen Tadesse'
        ..qrToken = 'QR-12345-ABCDE'
        ..gpsPickup = (LatLng()
          ..latitude = 8.7519
          ..longitude = 38.9811)
        ..gpsDropoff = null // Not yet delivered
        ..pickupPhotoRef = 'photos/delivery-001/pickup.jpg'
        ..status = DeliveryStatus.pickedUp
        ..confirmedAt = null
        ..createdAt = now
        ..updatedAt = now
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.deliverys.put(delivery);
      });

      // Query back by trip ID
      final tripDelivery =
          await isar.deliverys.filter().tripIdEqualTo('trip-001').findFirst();

      // Verify
      expect(tripDelivery, isNotNull);
      expect(tripDelivery!.deliveryId, 'delivery-001');
      expect(tripDelivery.qrToken, 'QR-12345-ABCDE');
      expect(tripDelivery.status, DeliveryStatus.pickedUp);
      expect(tripDelivery.gpsPickup, isNotNull);
      expect(tripDelivery.gpsDropoff, isNull);
    });

    test('Payment schema: create with Telebirr reference', () async {
      // Create a payment
      final payment = Payment()
        ..paymentId = 'payment-001'
        ..deliveryId = 'delivery-001'
        ..farmerId = 'user-001'
        ..buyerId = 'user-003'
        ..amount = 4450.0 // (50 quintals * 85 ETB) + transport cost
        ..telebirrRef = 'TB-20260214-12345'
        ..status = PaymentStatus.escrowed
        ..createdAt = DateTime.now().toUtc()
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.payments.put(payment);
      });

      // Query back by delivery ID
      final deliveryPayment = await isar.payments
          .filter()
          .deliveryIdEqualTo('delivery-001')
          .findFirst();

      // Verify
      expect(deliveryPayment, isNotNull);
      expect(deliveryPayment!.paymentId, 'payment-001');
      expect(deliveryPayment.amount, 4450.0);
      expect(deliveryPayment.telebirrRef, 'TB-20260214-12345');
      expect(deliveryPayment.status, PaymentStatus.escrowed);
    });

    test('SyncOperation schema: create and query by status', () async {
      // Create a sync operation
      final syncOp = SyncOperation()
        ..operationId = 'op-001'
        ..idempotencyKey = 'crop_listing:listing-001:update:1234567890'
        ..entityType = 'crop_listing'
        ..entityId = 'listing-001'
        ..action = 'update'
        ..data = '{"listingId":"listing-001","status":"sold"}'
        ..status = SyncStatus.pending
        ..retryCount = 0
        ..createdAt = DateTime.now().toUtc();

      // Save to database
      await isar.writeTxn(() async {
        await isar.syncOperations.put(syncOp);
      });

      // Query all pending operations
      final pendingOps = await isar.syncOperations
          .filter()
          .statusEqualTo(SyncStatus.pending)
          .findAll();

      // Verify
      expect(pendingOps, isNotEmpty);
      expect(pendingOps.first.operationId, 'op-001');
      expect(pendingOps.first.entityType, 'crop_listing');
      expect(pendingOps.first.action, 'update');
      expect(pendingOps.first.retryCount, 0);
    });

    test('Verify all schemas are registered', () {
      // All schemas should be accessible via Isar instance
      expect(isar.users, isNotNull);
      expect(isar.cropListings, isNotNull);
      expect(isar.trips, isNotNull);
      expect(isar.deliverys, isNotNull);
      expect(isar.payments, isNotNull);
      expect(isar.syncOperations, isNotNull);
    });

    // --- R4 Delivery Schema Edge Cases ---

    test('Delivery schema: create with null GPS (offline scenario)', () async {
      final now = DateTime.now().toUtc();
      final delivery = Delivery()
        ..deliveryId = 'delivery-gps-null'
        ..tripId = 'trip-001'
        ..listingId = 'listing-001'
        ..farmerId = 'farmer-001'
        ..farmerName = 'Abebe'
        ..truckerId = 'trucker-001'
        ..truckerName = 'Tadesse'
        ..buyerId = 'buyer-001'
        ..buyerName = 'Mekonnen'
        ..qrToken = 'QR-TOKEN-OFFLINE'
        ..gpsPickup = null
        ..gpsDropoff = null
        ..pickupPhotoRef = null
        ..dropoffPhotoRef = null
        ..status = DeliveryStatus.pending
        ..confirmedAt = null
        ..createdAt = now
        ..updatedAt = now
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.deliverys.put(delivery);
      });

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo('delivery-gps-null')
          .findFirst();

      expect(fromDb, isNotNull);
      expect(fromDb!.gpsPickup, isNull);
      expect(fromDb.gpsDropoff, isNull);
      expect(fromDb.pickupPhotoRef, isNull);
      expect(fromDb.dropoffPhotoRef, isNull);
      expect(fromDb.confirmedAt, isNull);
      expect(fromDb.status, DeliveryStatus.pending);
    });

    test('Delivery schema: all status values stored correctly', () async {
      const statuses = DeliveryStatus.values;

      for (var i = 0; i < statuses.length; i++) {
        final now = DateTime.now().toUtc();
        final delivery = Delivery()
          ..deliveryId = 'delivery-status-$i'
          ..tripId = 'trip-001'
          ..listingId = 'listing-001'
          ..farmerId = 'farmer-001'
          ..farmerName = 'Abebe'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Tadesse'
          ..buyerId = 'buyer-001'
          ..buyerName = 'Mekonnen'
          ..qrToken = 'QR-TOKEN-$i'
          ..status = statuses[i]
          ..createdAt = now
          ..updatedAt = now
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.deliverys.put(delivery);
        });
      }

      for (var i = 0; i < statuses.length; i++) {
        final fromDb = await isar.deliverys
            .filter()
            .deliveryIdEqualTo('delivery-status-$i')
            .findFirst();
        expect(fromDb, isNotNull);
        expect(fromDb!.status, statuses[i]);
      }
    });

    test('Delivery schema: GPS coordinates at Ethiopian boundaries', () async {
      // Ethiopian lat: 3-15°N, long: 33-48°E
      final now = DateTime.now().toUtc();
      final delivery = Delivery()
        ..deliveryId = 'delivery-eth-bounds'
        ..tripId = 'trip-001'
        ..listingId = 'listing-001'
        ..farmerId = 'farmer-001'
        ..farmerName = 'Abebe'
        ..truckerId = 'trucker-001'
        ..truckerName = 'Tadesse'
        ..buyerId = 'buyer-001'
        ..buyerName = 'Mekonnen'
        ..qrToken = 'QR-ETH-BOUNDS'
        ..gpsPickup = (LatLng()
          ..latitude = 3.0
          ..longitude = 33.0)
        ..gpsDropoff = (LatLng()
          ..latitude = 15.0
          ..longitude = 48.0)
        ..status = DeliveryStatus.delivered
        ..confirmedAt = now
        ..createdAt = now
        ..updatedAt = now
        ..version = 2;

      await isar.writeTxn(() async {
        await isar.deliverys.put(delivery);
      });

      final fromDb = await isar.deliverys
          .filter()
          .deliveryIdEqualTo('delivery-eth-bounds')
          .findFirst();

      expect(fromDb!.gpsPickup!.latitude, closeTo(3.0, 0.001));
      expect(fromDb.gpsPickup!.longitude, closeTo(33.0, 0.001));
      expect(fromDb.gpsDropoff!.latitude, closeTo(15.0, 0.001));
      expect(fromDb.gpsDropoff!.longitude, closeTo(48.0, 0.001));
    });

    test('Delivery schema: query by status index', () async {
      // Create deliveries with different statuses
      final statuses = [
        DeliveryStatus.pending,
        DeliveryStatus.pickedUp,
        DeliveryStatus.pending,
      ];

      for (var i = 0; i < statuses.length; i++) {
        final now = DateTime.now().toUtc();
        final delivery = Delivery()
          ..deliveryId = 'delivery-idx-$i'
          ..tripId = 'trip-idx'
          ..listingId = 'listing-idx'
          ..farmerId = 'farmer-001'
          ..farmerName = 'Abebe'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Tadesse'
          ..buyerId = 'buyer-001'
          ..buyerName = 'Mekonnen'
          ..qrToken = 'QR-IDX-$i'
          ..status = statuses[i]
          ..createdAt = now
          ..updatedAt = now
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.deliverys.put(delivery);
        });
      }

      final pendingDeliveries = await isar.deliverys
          .filter()
          .statusEqualTo(DeliveryStatus.pending)
          .findAll();

      // Should find 2 pending (plus the one from the earlier test)
      expect(pendingDeliveries.length, greaterThanOrEqualTo(2));
    });

    // --- R4 Payment Schema Edge Cases ---

    test('Payment schema: all status values stored correctly', () async {
      const statuses = PaymentStatus.values;

      for (var i = 0; i < statuses.length; i++) {
        final payment = Payment()
          ..paymentId = 'payment-status-$i'
          ..deliveryId = 'delivery-pay-$i'
          ..farmerId = 'farmer-001'
          ..buyerId = 'buyer-001'
          ..amount = 1000.0 + i
          ..telebirrRef = null
          ..status = statuses[i]
          ..createdAt = DateTime.now().toUtc()
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.payments.put(payment);
        });
      }

      for (var i = 0; i < statuses.length; i++) {
        final fromDb = await isar.payments
            .filter()
            .paymentIdEqualTo('payment-status-$i')
            .findFirst();
        expect(fromDb, isNotNull);
        expect(fromDb!.status, statuses[i]);
      }
    });

    test('Payment schema: ETB amount boundaries', () async {
      // Contract: min 100 ETB, max 500,000 ETB
      final paymentMin = Payment()
        ..paymentId = 'payment-min-amount'
        ..deliveryId = 'delivery-min'
        ..farmerId = 'farmer-001'
        ..buyerId = 'buyer-001'
        ..amount = 100.0
        ..status = PaymentStatus.escrowed
        ..createdAt = DateTime.now().toUtc()
        ..version = 1;

      final paymentMax = Payment()
        ..paymentId = 'payment-max-amount'
        ..deliveryId = 'delivery-max'
        ..farmerId = 'farmer-001'
        ..buyerId = 'buyer-001'
        ..amount = 500000.0
        ..status = PaymentStatus.escrowed
        ..createdAt = DateTime.now().toUtc()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.payments.put(paymentMin);
        await isar.payments.put(paymentMax);
      });

      final minFromDb = await isar.payments
          .filter()
          .paymentIdEqualTo('payment-min-amount')
          .findFirst();
      expect(minFromDb!.amount, 100.0);

      final maxFromDb = await isar.payments
          .filter()
          .paymentIdEqualTo('payment-max-amount')
          .findFirst();
      expect(maxFromDb!.amount, 500000.0);
    });

    test('Payment schema: ETB amount with santim precision', () async {
      // Contract: 2 decimal places (ETB sub-unit = santim)
      final payment = Payment()
        ..paymentId = 'payment-santim'
        ..deliveryId = 'delivery-santim'
        ..farmerId = 'farmer-001'
        ..buyerId = 'buyer-001'
        ..amount = 1234.56
        ..status = PaymentStatus.escrowed
        ..createdAt = DateTime.now().toUtc()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.payments.put(payment);
      });

      final fromDb = await isar.payments
          .filter()
          .paymentIdEqualTo('payment-santim')
          .findFirst();
      expect(fromDb!.amount, closeTo(1234.56, 0.001));
    });

    test('Payment schema: null telebirrRef before payment initiated', () async {
      final payment = Payment()
        ..paymentId = 'payment-no-telebirr'
        ..deliveryId = 'delivery-no-tb'
        ..farmerId = 'farmer-001'
        ..buyerId = 'buyer-001'
        ..amount = 5000.0
        ..telebirrRef = null
        ..status = PaymentStatus.escrowed
        ..createdAt = DateTime.now().toUtc()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.payments.put(payment);
      });

      final fromDb = await isar.payments
          .filter()
          .paymentIdEqualTo('payment-no-telebirr')
          .findFirst();
      expect(fromDb!.telebirrRef, isNull);
    });

    // --- R4 Trip Schema Edge Cases ---

    test('Trip schema: all TripStatus values stored correctly', () async {
      const statuses = TripStatus.values;

      for (var i = 0; i < statuses.length; i++) {
        final trip = Trip()
          ..tripId = 'trip-status-$i'
          ..truckerId = 'trucker-status'
          ..truckerName = 'Tadesse'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.now().add(const Duration(days: 2)).toUtc()
          ..capacity = 100.0
          ..pricePerQuintal = 120.0
          ..status = statuses[i]
          ..matchedListings = []
          ..createdAt = DateTime.now().toUtc()
          ..updatedAt = DateTime.now().toUtc()
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });
      }

      for (var i = 0; i < statuses.length; i++) {
        final fromDb = await isar.trips
            .filter()
            .tripIdEqualTo('trip-status-$i')
            .findFirst();
        expect(fromDb, isNotNull);
        expect(fromDb!.status, statuses[i]);
      }
    });

    test('Trip schema: capacity boundary values', () async {
      // Contract: 0.1 to 1000 quintals
      final tripMin = Trip()
        ..tripId = 'trip-cap-min'
        ..truckerId = 'trucker-cap'
        ..truckerName = 'Tadesse'
        ..truckerPhone = '+251912345678'
        ..fromWoreda = 'Bishoftu'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime.now().add(const Duration(days: 2)).toUtc()
        ..capacity = 0.1
        ..pricePerQuintal = 120.0
        ..status = TripStatus.open
        ..matchedListings = []
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      final tripMax = Trip()
        ..tripId = 'trip-cap-max'
        ..truckerId = 'trucker-cap'
        ..truckerName = 'Tadesse'
        ..truckerPhone = '+251912345678'
        ..fromWoreda = 'Bishoftu'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime.now().add(const Duration(days: 2)).toUtc()
        ..capacity = 1000.0
        ..pricePerQuintal = 120.0
        ..status = TripStatus.open
        ..matchedListings = []
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.trips.put(tripMin);
        await isar.trips.put(tripMax);
      });

      final minFromDb = await isar.trips
          .filter()
          .tripIdEqualTo('trip-cap-min')
          .findFirst();
      expect(minFromDb!.capacity, closeTo(0.1, 0.001));

      final maxFromDb = await isar.trips
          .filter()
          .tripIdEqualTo('trip-cap-max')
          .findFirst();
      expect(maxFromDb!.capacity, 1000.0);
    });

    test('Trip schema: matched listings with multiple crop IDs', () async {
      final trip = Trip()
        ..tripId = 'trip-multi-match'
        ..truckerId = 'trucker-match'
        ..truckerName = 'Match Trucker'
        ..truckerPhone = '+251900000004'
        ..fromWoreda = 'Bishoftu'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = DateTime.now().add(const Duration(days: 2)).toUtc()
        ..capacity = 200.0
        ..pricePerQuintal = 120.0
        ..status = TripStatus.matched
        ..matchedListings = ['crop-001', 'crop-002', 'crop-003']
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 2;

      await isar.writeTxn(() async {
        await isar.trips.put(trip);
      });

      final fromDb = await isar.trips
          .filter()
          .tripIdEqualTo('trip-multi-match')
          .findFirst();
      expect(fromDb!.matchedListings.length, 3);
      expect(fromDb.matchedListings, contains('crop-001'));
      expect(fromDb.matchedListings, contains('crop-002'));
      expect(fromDb.matchedListings, contains('crop-003'));
    });

    test('Index uniqueness: enforce unique userId constraint', () async {
      // Create first user
      final user1 = User()
        ..userId = 'duplicate-user'
        ..phoneNumber = '+251922334455'
        ..name = 'Test User'
        ..role = UserRole.farmer
        ..woreda = 'Adama'
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.users.put(user1);
      });

      // Verify first user was saved
      final saved =
          await isar.users.filter().userIdEqualTo('duplicate-user').findFirst();
      expect(saved, isNotNull);

      // Attempt to create second user with same userId should fail
      final user2 = User()
        ..userId = 'duplicate-user' // Same userId
        ..phoneNumber = '+251933445566' // Different phone
        ..name = 'Another User'
        ..role = UserRole.buyer
        ..woreda = 'Hawassa'
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

      // Expect unique constraint violation
      expect(
        () async => await isar.writeTxn(() async {
          await isar.users.put(user2);
        }),
        throwsA(isA<IsarError>()),
      );
    });
  });
}

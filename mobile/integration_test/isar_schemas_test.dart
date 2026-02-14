import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:isar/isar.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Isar Schemas Integration Tests', () {
    late Isar isar;

    setUpAll(() async {
      // Get application documents directory
      final dir = await getApplicationDocumentsDirectory();

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
        directory: dir.path,
        name: 'test_schemas',
      );
    });

    tearDownAll(() async {
      // Clean up database
      await isar.close(deleteFromDisk: true);
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
      final retrieved = await isar.users
          .filter()
          .userIdEqualTo('user-001')
          .findFirst();

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
      final truckerTrip = await isar.trips
          .filter()
          .truckerIdEqualTo('user-002')
          .findFirst();

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
      final delivery = Delivery()
        ..deliveryId = 'delivery-001'
        ..tripId = 'trip-001'
        ..listingId = 'listing-001'
        ..qrToken = 'QR-12345-ABCDE'
        ..gpsPickup = (LatLng()
          ..latitude = 8.7519
          ..longitude = 38.9811)
        ..gpsDropoff = null // Not yet delivered
        ..photoEvidenceRef = 'photos/delivery-001/pickup.jpg'
        ..status = DeliveryStatus.pickedUp
        ..confirmedAt = null
        ..version = 1;

      // Save to database
      await isar.writeTxn(() async {
        await isar.deliverys.put(delivery);
      });

      // Query back by trip ID
      final tripDelivery = await isar.deliverys
          .filter()
          .tripIdEqualTo('trip-001')
          .findFirst();

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
      final saved = await isar.users
          .filter()
          .userIdEqualTo('duplicate-user')
          .findFirst();
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

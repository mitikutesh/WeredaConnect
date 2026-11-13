import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';

void main() {
  group('ConflictResolver', () {
    late Isar isar;
    late Directory tempDir;
    late ConflictResolver resolver;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('conflict_resolver_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          OrderSchema,
          PaymentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'test_resolver_${DateTime.now().millisecondsSinceEpoch}',
      );
      resolver = ConflictResolver(isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    group('Order conflict resolution', () {
      test('takes the server pickup deadline (ESC-06)', () async {
        final operation = SyncOperation()
          ..operationId = 'op-order-1'
          ..idempotencyKey = 'key-order-1'
          ..entityType = 'order'
          ..entityId = 'order-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'orderId': 'order-001',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'totalPrice': 22750,
            'status': 'accepted',
            'createdAt': '2026-10-01T08:00:00Z',
            'pickupDeadlineAt': '2026-10-12T08:00:00.0000000Z',
          },
          serverVersion: 5,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-001').findFirst();
        expect(order!.status, OrderStatus.accepted);
        expect(order.pickupDeadlineAt!.toUtc(), DateTime.utc(2026, 10, 12, 8));
        expect(order.version, 5);
      });

      // FUP-285: the conflict echo carries the pickup judgement and the
      // check-ins as the parties read them (ESC-34).
      test('takes the pickup judgement and the check-ins', () async {
        final operation = SyncOperation()
          ..operationId = 'op-order-2'
          ..idempotencyKey = 'key-order-2'
          ..entityType = 'order'
          ..entityId = 'order-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'orderId': 'order-001',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'totalPrice': 22750,
            'status': 'accepted',
            'createdAt': '2026-10-01T08:00:00Z',
            'pickupJudgement': 'pending',
            'checkIns': {
              'crops_ready': {
                'receivedAt': '2026-10-11T07:00:00Z',
                'distanceChecked': false,
                'late': false,
              },
            },
          },
          serverVersion: 6,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-001').findFirst();
        expect(order!.pickupJudgement, 'pending');
        expect(order.checkIns.single.kind, 'crops_ready');
        expect(
          order.checkIns.single.receivedAt!.toUtc(),
          DateTime.utc(2026, 10, 11, 7),
        );
        expect(order.checkIns.single.distanceChecked, isFalse);
      });

      // FUP-336: the drop-off judgement, T8, the system's dispute reason and
      // the trucker's drop-off check-in come with the echo too (ESC-36).
      test('takes the drop-off judgement and the drop-off check-in', () async {
        final operation = SyncOperation()
          ..operationId = 'op-order-3'
          ..idempotencyKey = 'key-order-3'
          ..entityType = 'order'
          ..entityId = 'order-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'orderId': 'order-001',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'totalPrice': 22750,
            'status': 'disputed',
            'createdAt': '2026-10-01T08:00:00Z',
            'dropoffJudgement': 'silent_buyer',
            'dropoffJudgedAt': '2026-10-16T07:01:00Z',
            'dropoffDeadlineAt': '2026-10-15T08:00:00Z',
            'disputeReason': 'silent_buyer',
            'checkIns': {
              'arrived_dropoff': {
                'receivedAt': '2026-10-14T07:00:00Z',
                'distanceChecked': false,
                'late': false,
              },
            },
          },
          serverVersion: 9,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-001').findFirst();
        expect(order!.dropoffJudgement, 'silent_buyer');
        expect(
          order.dropoffJudgedAt!.toUtc(),
          DateTime.utc(2026, 10, 16, 7, 1),
        );
        expect(order.dropoffDeadlineAt!.toUtc(), DateTime.utc(2026, 10, 15, 8));
        expect(order.disputeReason, 'silent_buyer');
        expect(order.checkIns.single.kind, 'arrived_dropoff');
      });

      // FUP-370: the server's T9 of an order waiting for it is kept on the
      // device, so the card shows it offline.
      test('keeps the server\'s T9 of a pending drop-off', () async {
        final operation = SyncOperation()
          ..operationId = 'op-order-4'
          ..idempotencyKey = 'key-order-4'
          ..entityType = 'order'
          ..entityId = 'order-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'orderId': 'order-001',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'totalPrice': 22750,
            'status': 'in_transit',
            'createdAt': '2026-10-01T08:00:00Z',
            'dropoffJudgement': 'pending',
            'dropoffDeadlineAt': '2026-10-15T08:00:00Z',
            'dropoffConfirmAt': '2026-10-15T19:00:00.0000000Z',
            'checkIns': {
              'arrived_dropoff': {
                'receivedAt': '2026-10-14T07:00:00Z',
                'distanceChecked': true,
                'late': false,
              },
            },
          },
          serverVersion: 9,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-001').findFirst();
        expect(
          order!.dropoffConfirmAt!.toUtc(),
          DateTime.utc(2026, 10, 15, 19),
        );
      });

      // ESC-48: /sync rows carry no contact flag; a closed status, or an
      // admin override refund on an open status, drops the phones the last
      // pull left on the device.
      for (final (status, overrideFlag, keepsPhones) in [
        ('cancelled', null, false),
        ('in_transit', null, true),
        ('in_transit', 'refundOverrideAt', false),
        ('in_transit', 'disputeOverrideAt', false),
      ]) {
        test(
            'a $status server row${overrideFlag == null ? '' : ' with $overrideFlag'} '
            '${keepsPhones ? 'keeps' : 'drops'} the phones', () async {
          await isar.writeTxn(() async {
            await isar.orders.put(
              Order()
                ..orderId = 'order-002'
                ..listingId = 'listing-001'
                ..buyerId = 'buyer-001'
                ..farmerId = 'farmer-001'
                ..buyerName = 'Buyer'
                ..farmerName = 'Farmer'
                ..cropType = 'teff'
                ..quantity = 500
                ..pricePerUnit = 45.5
                ..totalPrice = 22750
                ..status = OrderStatus.accepted
                ..createdAt = DateTime.utc(2026, 10)
                ..version = 4
                ..contactOpen = true
                ..buyerPhone = '+251911000001'
                ..farmerPhone = '+251911000002',
            );
          });
          final operation = SyncOperation()
            ..operationId = 'op-order-2'
            ..idempotencyKey = 'key-order-2'
            ..entityType = 'order'
            ..entityId = 'order-002'
            ..action = 'update'
            ..data = '{}'
            ..status = SyncStatus.syncing
            ..retryCount = 0
            ..createdAt = DateTime.now().toUtc();

          await resolver.resolve(
            operation: operation,
            serverData: {
              'orderId': 'order-002',
              'listingId': 'listing-001',
              'buyerId': 'buyer-001',
              'farmerId': 'farmer-001',
              'buyerName': 'Buyer',
              'farmerName': 'Farmer',
              'cropType': 'teff',
              'quantity': 500,
              'pricePerUnit': 45.5,
              'totalPrice': 22750,
              'status': status,
              'createdAt': '2026-10-01T08:00:00Z',
              if (overrideFlag != null) overrideFlag: '2026-10-16T08:00:00Z',
            },
            serverVersion: 6,
          );

          final order = await isar.orders
              .filter()
              .orderIdEqualTo('order-002')
              .findFirst();
          // An override refund closes contact for good; otherwise the next
          // pull decides (null: the status set rules until then).
          expect(order!.contactOpen, overrideFlag == null ? isNull : isFalse);
          expect(order.farmerPhone, keepsPhones ? '+251911000002' : isNull);
          expect(order.buyerPhone, keepsPhones ? '+251911000001' : isNull);
        });
      }

      // SIM-25: the row as /sync sends it, typed like GET (numbers as
      // numbers) and without totalPrice, which the server never stores.
      Map<String, dynamic> serverShapedOrderRow() => {
            'orderId': 'order-003',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'cropCost': 22750,
            'commissionRate': 0.05,
            'commissionAmount': 1137.5,
            'orderTotal': 23887.5,
            'version': 7,
            'status': 'accepted',
            'createdAt': '2026-10-01T08:00:00Z',
          };

      SyncOperation orderOperation() => SyncOperation()
        ..operationId = 'op-order-3'
        ..idempotencyKey = 'key-order-3'
        ..entityType = 'order'
        ..entityId = 'order-003'
        ..action = 'update'
        ..data = '{}'
        ..status = SyncStatus.syncing
        ..retryCount = 0
        ..createdAt = DateTime.now().toUtc();

      test('takes a typed row without totalPrice, keeping the local total',
          () async {
        await isar.writeTxn(() async {
          await isar.orders.put(
            Order()
              ..orderId = 'order-003'
              ..listingId = 'listing-001'
              ..buyerId = 'buyer-001'
              ..farmerId = 'farmer-001'
              ..buyerName = 'Buyer'
              ..farmerName = 'Farmer'
              ..cropType = 'teff'
              ..quantity = 400
              ..pricePerUnit = 45.5
              ..totalPrice = 18200
              ..status = OrderStatus.funded
              ..createdAt = DateTime.utc(2026, 10)
              ..version = 5,
          );
        });

        await resolver.resolve(
          operation: orderOperation(),
          serverData: serverShapedOrderRow(),
          serverVersion: 7,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-003').findFirst();
        expect(order!.status, OrderStatus.accepted);
        expect(order.quantity, 500.0);
        expect(order.orderTotal, 23887.5);
        expect(order.commissionRate, 0.05);
        expect(order.totalPrice, 18200.0);
        expect(order.version, 7);
      });

      test('creates the order from a typed row without totalPrice', () async {
        await resolver.resolve(
          operation: orderOperation(),
          serverData: serverShapedOrderRow(),
          serverVersion: 7,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-003').findFirst();
        expect(order!.cropCost, 22750.0);
        expect(order.totalPrice, 0.0);
        expect(order.version, 7);
      });
    });

    // ESC-15: the trucker leaves the contact when the commitment ends. A
    // refused trucker change on the trucker's own device drops the parties'
    // numbers; on another party's device only the trucker's goes.
    for (final (opUser, keepsParties) in [
      ('trucker-001', false),
      ('buyer-001', true),
    ]) {
      test('an awaiting-transport row, refused op of $opUser', () async {
        await isar.writeTxn(() async {
          await isar.orders.put(
            Order()
              ..orderId = 'order-003'
              ..listingId = 'listing-001'
              ..buyerId = 'buyer-001'
              ..farmerId = 'farmer-001'
              ..truckerId = 'trucker-001'
              ..tripId = 'trip-001'
              ..buyerName = 'Buyer'
              ..farmerName = 'Farmer'
              ..cropType = 'teff'
              ..quantity = 500
              ..pricePerUnit = 45.5
              ..totalPrice = 22750
              ..status = OrderStatus.accepted
              ..createdAt = DateTime.utc(2026, 10)
              ..version = 4
              ..contactOpen = true
              ..buyerPhone = '+251911000001'
              ..farmerPhone = '+251911000002'
              ..truckerPhone = '+251911000003',
          );
        });
        final operation = SyncOperation()
          ..operationId = 'op-order-3'
          ..idempotencyKey = '$opUser:order:order-003:update:1'
          ..entityType = 'order'
          ..entityId = 'order-003'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'orderId': 'order-003',
            'listingId': 'listing-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'truckerId': 'trucker-001',
            'tripId': 'trip-001',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 500,
            'pricePerUnit': 45.5,
            'totalPrice': 22750,
            'status': 'awaiting_transport',
            'fulfillment': 'trucker',
            'farmerResponse': 'accepted',
            'truckerResponse': 'cancelled',
            'createdAt': '2026-10-01T08:00:00Z',
          },
          serverVersion: 6,
        );

        final order =
            await isar.orders.filter().orderIdEqualTo('order-003').findFirst();
        expect(order!.status, OrderStatus.awaitingTransport);
        expect(order.truckerPhone, isNull);
        expect(order.buyerPhone, keepsParties ? '+251911000001' : isNull);
        expect(order.farmerPhone, keepsParties ? '+251911000002' : isNull);
        expect(order.contactOpen, keepsParties ? isNull : isFalse);
      });
    }

    group('CropListing conflict resolution', () {
      test('resolves conflict when server version > client version', () async {
        // Arrange: Create local CropListing with version 1
        final listing = CropListing()
          ..listingId = 'listing-001'
          ..farmerId = 'farmer-001'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.7)
          ..description = 'Old description'
          ..locationName = 'Bishoftu'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        // Create sync operation for update
        final operation = SyncOperation()
          ..operationId = 'op-001'
          ..idempotencyKey = 'key-001'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server data with version 5 (conflict - server is ahead)
        final serverData = {
          'listingId': 'listing-001',
          'farmerId': 'farmer-001',
          'cropType': 'wheat',
          'quantity': 15.5,
          'pricePerKg': 30.0,
          'location': {'latitude': 9.1, 'longitude': 38.8},
          'description': 'Server version description',
          'audioNoteRef': 'audio/farmer-001/listing-001.m4a',
          'status': 'active',
          'createdAt': '2026-02-10T10:00:00Z',
          'updatedAt': '2026-02-14T15:30:00Z',
        };

        // Act: Resolve conflict
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 5,
        );

        // Assert: Local entity should be updated with server data
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-001')
            .findFirst();

        expect(updated, isNotNull);
        expect(
          updated!.version,
          equals(5),
        ); // Version updated to server version
        expect(updated.cropType, equals('wheat')); // Server data accepted
        expect(updated.quantity, equals(15.5));
        expect(updated.pricePerKg, equals(30.0));
        expect(updated.description, equals('Server version description'));
        expect(
          updated.audioNoteRef,
          equals('audio/farmer-001/listing-001.m4a'),
        );
        expect(updated.status, equals(ListingStatus.active));
        expect(updated.location.latitude, equals(9.1));
        expect(updated.location.longitude, equals(38.8));
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-14T15:30:00Z').toUtc()),
        );

        // Assert: Result contains audit info
        expect(result['entityType'], equals('crop_listing'));
        expect(result['entityId'], equals('listing-001'));
        expect(result['serverVersion'], equals(5));
        expect(result['resolvedAt'], isNotNull);
      });

      test('handles conflict when client was editing locally', () async {
        // Arrange: Local entity with pending changes (version 2)
        final listing = CropListing()
          ..listingId = 'listing-002'
          ..farmerId = 'farmer-002'
          ..cropType = 'barley'
          ..quantity = 20.0
          ..pricePerKg = 22.0
          ..location = (LatLng()
            ..latitude = 10.0
            ..longitude = 39.0)
          ..description = 'Local edits not synced'
          ..locationName = 'Adama'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-12T08:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T12:00:00Z')
          ..version = 2;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-002'
          ..idempotencyKey = 'key-002'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-002'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server already at version 8 (someone else edited)
        final serverData = {
          'listingId': 'listing-002',
          'farmerId': 'farmer-002',
          'cropType': 'maize',
          'quantity': 50.0,
          'pricePerKg': 18.0,
          'location': {'latitude': 10.2, 'longitude': 39.1},
          'description': 'Updated by another device',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-12T08:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act: Resolve conflict (server wins)
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 8,
        );

        // Assert: Local changes discarded, server version accepted
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-002')
            .findFirst();

        expect(updated, isNotNull);
        expect(updated!.version, equals(8));
        expect(updated.cropType, equals('maize')); // Server wins
        expect(updated.quantity, equals(50.0)); // Not 20.0 (local)
        expect(updated.description, equals('Updated by another device'));
      });

      test('updates all CropListing fields including optional ones', () async {
        // Arrange: Local listing without audio note
        final listing = CropListing()
          ..listingId = 'listing-003'
          ..farmerId = 'farmer-003'
          ..cropType = 'chickpea'
          ..quantity = 5.0
          ..pricePerKg = 35.0
          ..location = (LatLng()
            ..latitude = 8.5
            ..longitude = 38.0)
          ..description = 'No audio'
          ..locationName = 'Hawassa'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-13T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-13T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-003'
          ..idempotencyKey = 'key-003'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-003'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server has audio note added
        final serverData = {
          'listingId': 'listing-003',
          'farmerId': 'farmer-003',
          'cropType': 'chickpea',
          'quantity': 5.0,
          'pricePerKg': 35.0,
          'location': {'latitude': 8.5, 'longitude': 38.0},
          'description': 'With audio note now',
          'audioNoteRef': 'audio/farmer-003/listing-003.m4a',
          'status': 'active',
          'createdAt': '2026-02-13T10:00:00Z',
          'updatedAt': '2026-02-14T09:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: Audio note field updated
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-003')
            .findFirst();

        expect(
          updated!.audioNoteRef,
          equals('audio/farmer-003/listing-003.m4a'),
        );
        expect(updated.status, equals(ListingStatus.active));
      });

      test('handles status enum parsing correctly', () async {
        // Arrange
        final listing = CropListing()
          ..listingId = 'listing-004'
          ..farmerId = 'farmer-004'
          ..cropType = 'teff'
          ..quantity = 12.0
          ..pricePerKg = 28.0
          ..location = (LatLng()
            ..latitude = 9.5
            ..longitude = 39.5)
          ..description = 'Active listing'
          ..locationName = 'Debre Zeit'
          ..audioNoteRef = null
          ..status = ListingStatus.active
          ..createdAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..version = 3;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-004'
          ..idempotencyKey = 'key-004'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-004'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server changed status to sold
        final serverData = {
          'listingId': 'listing-004',
          'farmerId': 'farmer-004',
          'cropType': 'teff',
          'quantity': 12.0,
          'pricePerKg': 28.0,
          'location': {'latitude': 9.5, 'longitude': 39.5},
          'description': 'Sold successfully',
          'audioNoteRef': null,
          'status': 'sold',
          'createdAt': '2026-02-10T10:00:00Z',
          'updatedAt': '2026-02-14T18:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 4,
        );

        // Assert: Status correctly parsed
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-004')
            .findFirst();

        expect(updated!.status, equals(ListingStatus.sold));
        expect(updated.description, equals('Sold successfully'));
      });

      test('creates entity from server data when local entity not found',
          () async {
        // Arrange: Operation for non-existent local entity
        final operation = SyncOperation()
          ..operationId = 'op-999'
          ..idempotencyKey = 'key-999'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-nonexistent'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'listingId': 'listing-nonexistent',
          'farmerId': 'farmer-001',
          'cropType': 'wheat',
          'quantity': 10.0,
          'pricePerKg': 25.0,
          'location': {'latitude': 9.0, 'longitude': 38.0},
          'description': 'Test',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-14T10:00:00Z',
          'updatedAt': '2026-02-14T10:00:00Z',
        };

        // Act: Server-wins strategy creates the entity locally
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 1,
        );

        // Assert: Entity was created from server data
        final created = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-nonexistent')
            .findFirst();

        expect(created, isNotNull);
        expect(created!.cropType, equals('wheat'));
        expect(created.version, equals(1));
        expect(result['entityType'], equals('crop_listing'));
        expect(result['entityId'], equals('listing-nonexistent'));
        // ESC-31: an echo without the owner copy leaves it empty.
        expect(created.ownerTier, isNull);
        expect(created.ownerReputationAt, isNull);

        // The echo carries the owner copy: stored. The next echo without it
        // (the flag went off) clears it.
        await resolver.resolve(
          operation: operation,
          serverData: {
            ...serverData,
            'ownerTier': 'low',
            'ownerEffectiveRating': 4.2,
            'ownerRatingCount': 12,
            'ownerReliabilityPct': 83,
            'ownerReputationAt': '2026-11-07T09:00:00.000Z',
          },
          serverVersion: 2,
        );
        final withCopy = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-nonexistent')
            .findFirst();
        expect(withCopy!.ownerTier, 'low');
        expect(withCopy.ownerEffectiveRating, 4.2);
        expect(withCopy.ownerRatingCount, 12);
        expect(withCopy.ownerReliabilityPct, 83);
        expect(
          withCopy.ownerReputationAt?.toUtc(),
          DateTime.utc(2026, 11, 7, 9),
        );

        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 3,
        );
        final cleared = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-nonexistent')
            .findFirst();
        expect(cleared!.ownerTier, isNull);
        expect(cleared.ownerEffectiveRating, isNull);
        expect(cleared.ownerReliabilityPct, isNull);
      });

      test('throws exception when server data is malformed', () async {
        // Arrange: Create local listing
        final listing = CropListing()
          ..listingId = 'listing-005'
          ..farmerId = 'farmer-005'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Jimma'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-005'
          ..idempotencyKey = 'key-005'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-005'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Malformed server data (missing required fields)
        final serverData = {
          'listingId': 'listing-005',
          // Missing farmerId, cropType, etc.
        };

        // Act & Assert
        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 2,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('preserves entity createdAt timestamp', () async {
        // Arrange
        final originalCreatedAt = DateTime.parse('2026-02-01T10:00:00Z');
        final listing = CropListing()
          ..listingId = 'listing-006'
          ..farmerId = 'farmer-006'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Bahir Dar'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = originalCreatedAt
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-006'
          ..idempotencyKey = 'key-006'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-006'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'listingId': 'listing-006',
          'farmerId': 'farmer-006',
          'cropType': 'wheat',
          'quantity': 15.0,
          'pricePerKg': 30.0,
          'location': {'latitude': 9.0, 'longitude': 38.0},
          'description': 'Updated',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-01T10:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: createdAt preserved (not changed)
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-006')
            .findFirst();

        expect(updated!.createdAt.toUtc(), equals(originalCreatedAt.toUtc()));
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-14T16:00:00Z').toUtc()),
        );
      });
    });

    group('Trip conflict resolution', () {
      test('resolves conflict when server version > client version', () async {
        // Arrange: Create local Trip with version 1
        final trip = Trip()
          ..tripId = 'trip-001'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit Assefa'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-001'
          ..idempotencyKey = 'key-trip-001'
          ..entityType = 'trip'
          ..entityId = 'trip-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server data with version 3 (conflict - server is ahead)
        final serverData = {
          'tripId': 'trip-001',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit Assefa',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Debre Zeit',
          'toWoreda': 'Adama',
          'departureDate': '2026-02-21T07:00:00Z',
          'capacity': 150.0,
          'pricePerQuintal': 300.0,
          'status': 'matched',
          'matchedListings': ['listing-001', 'listing-002'],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-16T14:00:00Z',
        };

        // Act
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 3,
        );

        // Assert
        final updated =
            await isar.trips.filter().tripIdEqualTo('trip-001').findFirst();

        expect(updated, isNotNull);
        expect(updated!.version, equals(3));
        expect(updated.fromWoreda, equals('Debre Zeit'));
        expect(updated.toWoreda, equals('Adama'));
        expect(updated.capacity, equals(150.0));
        expect(updated.pricePerQuintal, equals(300.0));
        expect(updated.status, equals(TripStatus.matched));
        expect(updated.matchedListings, equals(['listing-001', 'listing-002']));
        expect(
          updated.departureDate.toUtc(),
          equals(DateTime.parse('2026-02-21T07:00:00Z').toUtc()),
        );
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-16T14:00:00Z').toUtc()),
        );

        // Audit info
        expect(result['entityType'], equals('trip'));
        expect(result['entityId'], equals('trip-001'));
        expect(result['serverVersion'], equals(3));
        expect(result['resolvedAt'], isNotNull);
      });

      test('creates trip from server data when local trip not found', () async {
        final operation = SyncOperation()
          ..operationId = 'op-trip-missing'
          ..idempotencyKey = 'key-trip-missing'
          ..entityType = 'trip'
          ..entityId = 'trip-nonexistent'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'tripId': 'trip-nonexistent',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Bishoftu',
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-02-20T06:00:00Z',
          'capacity': 100.0,
          'pricePerQuintal': 250.0,
          'status': 'open',
          'matchedListings': <String>[],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-15T10:00:00Z',
        };

        // Act: Server-wins strategy creates the trip locally
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 1,
        );

        // Assert: Trip was created from server data
        final created = await isar.trips
            .filter()
            .tripIdEqualTo('trip-nonexistent')
            .findFirst();

        expect(created, isNotNull);
        expect(created!.fromWoreda, equals('Bishoftu'));
        expect(created.toWoreda, equals('Addis Ababa'));
        expect(created.version, equals(1));
        expect(result['entityType'], equals('trip'));
        expect(result['entityId'], equals('trip-nonexistent'));
        expect(created.ownerTier, isNull);

        // ESC-31: the echo's owner copy is stored with the trip.
        await resolver.resolve(
          operation: operation,
          serverData: {
            ...serverData,
            'ownerTier': 'good',
            'ownerEffectiveRating': 4.7,
            'ownerRatingCount': 3,
            'ownerReliabilityPct': 91,
            'ownerReputationAt': '2026-11-07T09:00:00.000Z',
          },
          serverVersion: 2,
        );
        final withCopy = await isar.trips
            .filter()
            .tripIdEqualTo('trip-nonexistent')
            .findFirst();
        expect(withCopy!.ownerTier, 'good');
        expect(withCopy.ownerEffectiveRating, 4.7);
        expect(withCopy.ownerRatingCount, 3);
        expect(withCopy.ownerReliabilityPct, 91);
        expect(
          withCopy.ownerReputationAt?.toUtc(),
          DateTime.utc(2026, 11, 7, 9),
        );
      });

      test('throws exception when server data is malformed', () async {
        final trip = Trip()
          ..tripId = 'trip-002'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-bad'
          ..idempotencyKey = 'key-trip-bad'
          ..entityType = 'trip'
          ..entityId = 'trip-002'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Malformed: missing required fields
        final serverData = {'tripId': 'trip-002'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 2,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('handles numeric types correctly (int vs double)', () async {
        final trip = Trip()
          ..tripId = 'trip-003'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-num'
          ..idempotencyKey = 'key-trip-num'
          ..entityType = 'trip'
          ..entityId = 'trip-003'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server sends integers instead of doubles
        final serverData = {
          'tripId': 'trip-003',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Bishoftu',
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-02-20T06:00:00Z',
          'capacity': 200, // int
          'pricePerQuintal': 350, // int
          'status': 'open',
          'matchedListings': <String>[],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-16T10:00:00Z',
        };

        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        final updated =
            await isar.trips.filter().tripIdEqualTo('trip-003').findFirst();

        expect(updated!.capacity, equals(200.0));
        expect(updated.pricePerQuintal, equals(350.0));
      });
    });

    group('Delivery conflict resolution', () {
      test('keeps a self-pickup delivery\'s type from the server', () async {
        final operation = SyncOperation()
          ..operationId = 'op-del'
          ..idempotencyKey = 'key-del'
          ..entityType = 'delivery'
          ..entityId = 'del-self'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'deliveryId': 'del-self',
            'tripId': 'del-self',
            'orderId': 'order-1',
            'listingId': 'crop-1',
            'farmerId': 'farmer-1',
            'farmerName': 'Kedir',
            'buyerId': 'buyer-1',
            'buyerName': 'Dawit',
            'deliveryType': 'self_pickup',
            'status': 'pending',
            'createdAt': '2026-09-29T10:00:00Z',
            'updatedAt': '2026-09-29T10:00:00Z',
          },
          serverVersion: 2,
        );

        final stored = await isar.deliverys
            .filter()
            .deliveryIdEqualTo('del-self')
            .findFirst();
        expect(stored!.deliveryType, 'self_pickup');
        expect(stored.status, DeliveryStatus.pending);
      });
    });

    // FRD-12: the handoff photo this phone took waits in the queue; a
    // conflict on the delivery keeps its path until the server holds a key.
    group('Delivery conflict keeps the phone\'s photo', () {
      const local =
          '/data/user/0/app/files/photos/del-photo/pickup-1700000000000.webp';
      const key = 'uploads/photos/del-photo/dropoff-1700000000000.jpg';

      Future<Delivery?> resolveWith(Map<String, dynamic> extra) async {
        await isar.writeTxn(() async {
          await isar.deliverys.put(
            Delivery()
              ..deliveryId = 'del-photo'
              ..tripId = 'trip-1'
              ..listingId = 'crop-1'
              ..farmerId = 'farmer-1'
              ..farmerName = 'Kedir'
              ..truckerId = 'trucker-1'
              ..truckerName = 'Geleta'
              ..buyerId = 'buyer-1'
              ..buyerName = 'Dawit'
              ..qrToken = ''
              ..status = DeliveryStatus.pickedUp
              ..pickupPhotoRef = local
              ..dropoffPhotoRef =
                  '/data/user/0/app/files/photos/del-photo/dropoff-1.webp'
              ..createdAt = DateTime.utc(2026, 11, 7)
              ..updatedAt = DateTime.utc(2026, 11, 7)
              ..version = 2,
          );
        });
        final operation = SyncOperation()
          ..operationId = 'op-del-photo'
          ..idempotencyKey = 'key-del-photo'
          ..entityType = 'delivery'
          ..entityId = 'del-photo'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();
        await resolver.resolve(
          operation: operation,
          serverData: {
            'deliveryId': 'del-photo',
            'tripId': 'trip-1',
            'listingId': 'crop-1',
            'farmerId': 'farmer-1',
            'farmerName': 'Kedir',
            'truckerId': 'trucker-1',
            'truckerName': 'Geleta',
            'buyerId': 'buyer-1',
            'buyerName': 'Dawit',
            'status': 'pickedUp',
            'createdAt': '2026-11-07T08:00:00Z',
            'updatedAt': '2026-11-07T09:00:00Z',
            ...extra,
          },
          serverVersion: 3,
        );
        return isar.deliverys
            .filter()
            .deliveryIdEqualTo('del-photo')
            .findFirst();
      }

      test('a server row without photos keeps the local paths', () async {
        final stored = await resolveWith({});

        expect(stored!.pickupPhotoRef, local);
        expect(stored.dropoffPhotoRef, isNotNull);
      });

      test('a server key wins over the local path', () async {
        final stored = await resolveWith({'dropoffPhotoRef': key});

        expect(stored!.pickupPhotoRef, local);
        expect(stored.dropoffPhotoRef, key);
      });

      // FUP-336 (ESC-36): the trucker's arrival photo the same way, and the
      // system's confirm for a silent buyer.
      test("the trucker's arrival photo and the system confirm", () async {
        const arrivalLocal =
            '/data/user/0/app/files/photos/del-arrival/arrival-1.webp';
        const arrivalKey =
            'uploads/photos/del-arrival/arrival-1700000000000.jpg';
        await isar.writeTxn(() async {
          await isar.deliverys.put(
            Delivery()
              ..deliveryId = 'del-arrival'
              ..tripId = 'trip-1'
              ..listingId = 'crop-1'
              ..farmerId = 'farmer-1'
              ..farmerName = 'Kedir'
              ..truckerId = 'trucker-1'
              ..truckerName = 'Geleta'
              ..buyerId = 'buyer-1'
              ..buyerName = 'Dawit'
              ..qrToken = ''
              ..status = DeliveryStatus.inTransit
              ..arrivalPhotoRef = arrivalLocal
              ..createdAt = DateTime.utc(2026, 11, 7)
              ..updatedAt = DateTime.utc(2026, 11, 7)
              ..version = 2,
          );
        });
        Future<Delivery?> echo(Map<String, dynamic> extra) async {
          await resolver.resolve(
            operation: SyncOperation()
              ..operationId = 'op-del-arrival'
              ..idempotencyKey = 'key-del-arrival'
              ..entityType = 'delivery'
              ..entityId = 'del-arrival'
              ..action = 'update'
              ..data = '{}'
              ..status = SyncStatus.syncing
              ..retryCount = 0
              ..createdAt = DateTime.now().toUtc(),
            serverData: {
              'deliveryId': 'del-arrival',
              'tripId': 'trip-1',
              'listingId': 'crop-1',
              'farmerId': 'farmer-1',
              'farmerName': 'Kedir',
              'truckerId': 'trucker-1',
              'truckerName': 'Geleta',
              'buyerId': 'buyer-1',
              'buyerName': 'Dawit',
              'status': 'inTransit',
              'createdAt': '2026-11-07T08:00:00Z',
              'updatedAt': '2026-11-07T09:00:00Z',
              ...extra,
            },
            serverVersion: 3,
          );
          return isar.deliverys
              .filter()
              .deliveryIdEqualTo('del-arrival')
              .findFirst();
        }

        var stored = await echo({});
        expect(stored!.arrivalPhotoRef, arrivalLocal);
        expect(stored.autoConfirmedAt, isNull);

        stored = await echo({
          'arrivalPhotoRef': arrivalKey,
          'autoConfirmedAt': '2026-11-10T08:01:00Z',
          'status': 'delivered',
        });
        expect(stored!.arrivalPhotoRef, arrivalKey);
        expect(
          stored.autoConfirmedAt!.toUtc(),
          DateTime.utc(2026, 11, 10, 8, 1),
        );
      });
    });

    // FUP-128: the rows `/sync` answers a conflict with are the stored rows,
    // and those carry only what was written: no description on a crop, no
    // trucker name or phone on an older trip, no trucker at all on a
    // self-pickup. A hard cast on one of them failed the whole conflict.
    group('server rows without the optional fields', () {
      SyncOperation op(String entityType, String entityId) => SyncOperation()
        ..operationId = 'op-$entityId'
        ..idempotencyKey = 'user-1:$entityType:$entityId:update:1'
        ..entityType = entityType
        ..entityId = entityId
        ..action = 'update'
        ..data = '{}'
        ..status = SyncStatus.syncing
        ..retryCount = 0
        ..createdAt = DateTime.now().toUtc();

      Map<String, dynamic> cropRow() => {
            'listingId': 'crop-min',
            'farmerId': 'farmer-1',
            'cropType': 'teff',
            'quantity': 12,
            'pricePerKg': 30,
            'latitude': 8.75,
            'longitude': 38.98,
            'woreda': 'Bishoftu',
            'status': 'available',
            'createdAt': '2026-11-01T08:00:00Z',
            'updatedAt': '2026-11-02T08:00:00Z',
            'version': 4,
          };

      Map<String, dynamic> tripRow(String status) => {
            'tripId': 'trip-min',
            'truckerId': 'trucker-1',
            'fromWoreda': 'Bishoftu',
            'toWoreda': 'Adama',
            'departureDate': '2026-11-12T06:00:00Z',
            'capacity': 80,
            'pricePerQuintal': 200,
            'status': status,
            'createdAt': '2026-11-01T08:00:00Z',
            'updatedAt': '2026-11-10T08:00:00Z',
            'version': 5,
          };

      test('a new crop listing takes the row, its flat point and status',
          () async {
        await resolver.resolve(
          operation: op('crop_listing', 'crop-min'),
          serverData: cropRow(),
          serverVersion: 4,
        );

        final stored = await isar.cropListings
            .filter()
            .listingIdEqualTo('crop-min')
            .findFirst();
        expect(stored!.description, '');
        expect(stored.locationName, '');
        expect(stored.farmerName, '');
        expect(stored.audioNoteRef, isNull);
        expect(stored.location.latitude, 8.75);
        expect(stored.location.longitude, 38.98);
        // The server's live status is `available`, never a draft.
        expect(stored.status, ListingStatus.active);
        expect(stored.version, 4);
      });

      test('a known crop listing without a point keeps the local one',
          () async {
        await isar.writeTxn(
          () => isar.cropListings.put(
            CropListing()
              ..listingId = 'crop-min'
              ..farmerId = 'farmer-1'
              ..cropType = 'teff'
              ..quantity = 10
              ..pricePerKg = 25
              ..location = (LatLng()
                ..latitude = 9.1
                ..longitude = 38.2)
              ..description = 'Local words'
              ..locationName = 'Local place'
              ..status = ListingStatus.active
              ..createdAt = DateTime.parse('2026-11-01T08:00:00Z')
              ..updatedAt = DateTime.parse('2026-11-01T08:00:00Z')
              ..version = 2,
          ),
        );

        await resolver.resolve(
          operation: op('crop_listing', 'crop-min'),
          serverData: cropRow()
            ..remove('latitude')
            ..remove('longitude')
            ..['status'] = 'sold',
          serverVersion: 4,
        );

        final stored = await isar.cropListings
            .filter()
            .listingIdEqualTo('crop-min')
            .findFirst();
        expect(stored!.location.latitude, 9.1);
        expect(stored.location.longitude, 38.2);
        // Server wins: the row has no description.
        expect(stored.description, '');
        expect(stored.status, ListingStatus.sold);
        expect(stored.quantity, 12);
        expect(stored.version, 4);
      });

      test('a new crop listing without a point starts at 0, 0', () async {
        await resolver.resolve(
          operation: op('crop_listing', 'crop-min'),
          serverData: cropRow()
            ..remove('latitude')
            ..remove('longitude'),
          serverVersion: 4,
        );

        final stored = await isar.cropListings
            .filter()
            .listingIdEqualTo('crop-min')
            .findFirst();
        expect(stored!.location.latitude, 0);
        expect(stored.location.longitude, 0);
      });

      // The trucker's own trip, closed or finished by the server while the
      // phone was offline (trip-listing-api.md 2.4.0).
      for (final status in ['cancelled', 'delivered']) {
        test('a known trip takes a $status row and keeps its name and phone',
            () async {
          await isar.writeTxn(
            () => isar.trips.put(
              Trip()
                ..tripId = 'trip-min'
                ..truckerId = 'trucker-1'
                ..truckerName = 'Dawit'
                ..truckerPhone = '+251912345678'
                ..fromWoreda = 'Bishoftu'
                ..toWoreda = 'Adama'
                ..departureDate = DateTime.parse('2026-11-12T06:00:00Z')
                ..capacity = 80
                ..pricePerQuintal = 200
                ..status = TripStatus.inTransit
                ..matchedListings = <String>[]
                ..createdAt = DateTime.parse('2026-11-01T08:00:00Z')
                ..updatedAt = DateTime.parse('2026-11-01T08:00:00Z')
                ..version = 3,
            ),
          );

          await resolver.resolve(
            operation: op('trip', 'trip-min'),
            serverData: tripRow(status),
            serverVersion: 5,
          );

          final stored =
              await isar.trips.filter().tripIdEqualTo('trip-min').findFirst();
          expect(stored!.status.name, status);
          expect(stored.truckerName, 'Dawit');
          expect(stored.truckerPhone, '+251912345678');
          expect(stored.vehiclePlate, isNull);
          expect(stored.matchedListings, isEmpty);
          expect(stored.bookedQuintals, isNull);
          expect(stored.version, 5);
        });
      }

      test('a new trip without name and phone starts with empty ones',
          () async {
        await resolver.resolve(
          operation: op('trip', 'trip-min'),
          serverData: tripRow('cancelled'),
          serverVersion: 5,
        );

        final stored =
            await isar.trips.filter().tripIdEqualTo('trip-min').findFirst();
        expect(stored!.truckerName, '');
        expect(stored.truckerPhone, '');
        expect(stored.status, TripStatus.cancelled);
      });

      // The row a pickup, a confirmation or a dispute gets when the delivery
      // was cancelled under it: as the server scrubs it, with no phones, no
      // QR token and no GPS. `del-self` has no trucker either.
      for (final (id, selfPickup) in [('del-min', false), ('del-self', true)]) {
        test('a cancelled delivery row ($id) puts the local delivery back',
            () async {
          await isar.writeTxn(
            () => isar.deliverys.put(
              Delivery()
                ..deliveryId = id
                ..tripId = selfPickup ? id : 'trip-1'
                ..orderId = 'order-1'
                ..listingId = 'crop-1'
                ..farmerId = 'farmer-1'
                ..farmerName = 'Kedir'
                ..truckerId = selfPickup ? '' : 'trucker-1'
                ..truckerName = selfPickup ? '' : 'Dawit'
                ..buyerId = 'buyer-1'
                ..buyerName = 'Sara'
                ..qrToken = 'qr-1'
                ..deliveryType = selfPickup ? 'self_pickup' : null
                // The local optimistic handoff the server refused.
                ..status = selfPickup
                    ? DeliveryStatus.delivered
                    : DeliveryStatus.pickedUp
                ..pickedUpAt = DateTime.parse('2026-11-10T09:00:00Z')
                ..createdAt = DateTime.parse('2026-11-01T08:00:00Z')
                ..updatedAt = DateTime.parse('2026-11-10T09:00:00Z')
                ..version = 2,
            ),
          );

          await resolver.resolve(
            operation: op('delivery', id),
            serverData: {
              'deliveryId': id,
              'tripId': selfPickup ? id : 'trip-1',
              'orderId': 'order-1',
              'listingId': 'crop-1',
              'farmerId': 'farmer-1',
              'buyerId': 'buyer-1',
              if (!selfPickup) 'truckerId': 'trucker-1',
              if (selfPickup) 'deliveryType': 'self_pickup',
              'status': 'cancelled',
              'createdAt': '2026-11-01T08:00:00Z',
              'updatedAt': '2026-11-10T08:30:00Z',
              'version': 3,
            },
            serverVersion: 3,
          );

          final stored =
              await isar.deliverys.filter().deliveryIdEqualTo(id).findFirst();
          expect(stored!.status, DeliveryStatus.cancelled);
          expect(stored.pickedUpAt, isNull);
          expect(stored.confirmedAt, isNull);
          expect(stored.truckerId, selfPickup ? '' : 'trucker-1');
          expect(stored.orderId, 'order-1');
          expect(stored.deliveryType, selfPickup ? 'self_pickup' : isNull);
          expect(stored.version, 3);
        });
      }
    });

    group('Unsupported entity types', () {
      test('throws exception for payment entity', () async {
        final operation = SyncOperation()
          ..operationId = 'op-payment'
          ..idempotencyKey = 'key-payment'
          ..entityType = 'payment'
          ..entityId = 'payment-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'paymentId': 'payment-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('throws exception for user entity', () async {
        final operation = SyncOperation()
          ..operationId = 'op-user'
          ..idempotencyKey = 'key-user'
          ..entityType = 'user'
          ..entityId = 'user-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'userId': 'user-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('throws exception for unknown entity type', () async {
        final operation = SyncOperation()
          ..operationId = 'op-unknown'
          ..idempotencyKey = 'key-unknown'
          ..entityType = 'unknown_entity'
          ..entityId = 'entity-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'id': 'entity-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });
    });

    group('Edge cases', () {
      test('handles numeric types correctly (int vs double)', () async {
        // Arrange: Test that quantity and price handle both int and double from server
        final listing = CropListing()
          ..listingId = 'listing-007'
          ..farmerId = 'farmer-007'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Gondar'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-007'
          ..idempotencyKey = 'key-007'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-007'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server sends integers instead of doubles
        final serverData = {
          'listingId': 'listing-007',
          'farmerId': 'farmer-007',
          'cropType': 'wheat',
          'quantity': 20, // int
          'pricePerKg': 30, // int
          'location': {'latitude': 9.5, 'longitude': 38.5},
          'description': 'Updated',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-14T10:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: Numeric conversion works
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-007')
            .findFirst();

        expect(updated!.quantity, equals(20.0));
        expect(updated.pricePerKg, equals(30.0));
      });
    });
  });
}

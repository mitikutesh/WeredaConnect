import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/messaging/data/isar_conversation_context_resolver.dart';

void main() {
  group('IsarConversationContextResolver', () {
    late Isar isar;
    late Directory tempDir;
    late IsarConversationContextResolver resolver;

    final now = DateTime.utc(2026, 7, 11);

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('context_resolver_test_');
      isar = await Isar.open(
        [CropListingSchema, TripSchema, OrderSchema, DeliverySchema],
        directory: tempDir.path,
        name: 'test_context_resolver_${DateTime.now().millisecondsSinceEpoch}',
      );
      resolver = IsarConversationContextResolver(isar: isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    CropListing crop(String id) => CropListing()
      ..listingId = id
      ..farmerId = 'farmer-1'
      ..cropType = 'teff'
      ..quantity = 100
      ..pricePerKg = 55
      ..location = (LatLng()
        ..latitude = 9
        ..longitude = 38)
      ..locationName = 'Adama'
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    test('crop context resolves to crop type + quantity', () async {
      await isar.writeTxn(() async => isar.cropListings.put(crop('crop-1')));

      final info = await resolver.resolve('crop', 'crop-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'teff');
      expect(info.quantityKg, 100);
      // Cached for synchronous reads after the first resolve.
      expect(resolver.peek('crop', 'crop-1'), isNotNull);
    });

    test('trip context resolves to its route', () async {
      final trip = Trip()
        ..tripId = 'trip-1'
        ..truckerId = 'trucker-1'
        ..truckerName = 'T'
        ..truckerPhone = '+251900000002'
        ..fromWoreda = 'Adama'
        ..toWoreda = 'Bishoftu'
        ..departureDate = now
        ..capacity = 50
        ..pricePerQuintal = 200
        ..status = TripStatus.open
        ..matchedListings = []
        ..createdAt = now
        ..updatedAt = now
        ..version = 1;
      await isar.writeTxn(() async => isar.trips.put(trip));

      final info = await resolver.resolve('trip', 'trip-1');
      expect(info, isNotNull);
      expect(info!.fromWoreda, 'Adama');
      expect(info.toWoreda, 'Bishoftu');
    });

    test('order context resolves from the order\'s denormalized crop',
        () async {
      final order = Order()
        ..orderId = 'order-1'
        ..listingId = 'crop-1'
        ..buyerId = 'buyer-1'
        ..farmerId = 'farmer-1'
        ..buyerName = 'B'
        ..farmerName = 'F'
        ..cropType = 'wheat'
        ..quantity = 40
        ..pricePerUnit = 60
        ..totalPrice = 2400
        ..status = OrderStatus.created
        ..createdAt = now
        ..version = 1;
      await isar.writeTxn(() async => isar.orders.put(order));

      final info = await resolver.resolve('order', 'order-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'wheat');
      expect(info.quantityKg, 40);
    });

    Delivery delivery(String id, String listingId) => Delivery()
      ..deliveryId = id
      ..tripId = 'trip-1'
      ..listingId = listingId
      ..farmerId = 'farmer-1'
      ..farmerName = 'F'
      ..truckerId = 'trucker-1'
      ..truckerName = 'T'
      ..buyerId = 'buyer-1'
      ..buyerName = 'B'
      ..qrToken = 'qr'
      ..status = DeliveryStatus.pending
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    test('delivery context chains through its listing to the crop', () async {
      await isar.writeTxn(() async {
        await isar.cropListings.put(crop('crop-9'));
        await isar.deliverys.put(delivery('delivery-1', 'crop-9'));
      });

      final info = await resolver.resolve('delivery', 'delivery-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'teff');
      expect(info.quantityKg, 100);
    });

    test('delivery whose crop is not held locally falls back to null',
        () async {
      // A trucker never browses crops, so the listing may be absent locally.
      await isar.writeTxn(
        () async => isar.deliverys.put(delivery('delivery-2', 'crop-absent')),
      );

      expect(await resolver.resolve('delivery', 'delivery-2'), isNull);
    });

    test('unknown context type and missing entities resolve to null', () async {
      expect(await resolver.resolve('crop', 'nope'), isNull);
      expect(await resolver.resolve('something-else', 'x'), isNull);
    });

    // ESC-48: a thread's contact follows its order.
    group('resolveContact', () {
      Order order({
        OrderStatus status = OrderStatus.accepted,
        bool? contactOpen = true,
      }) =>
          Order()
            ..orderId = 'order-1'
            ..listingId = 'crop-1'
            ..buyerId = 'buyer-1'
            ..farmerId = 'farmer-1'
            ..truckerId = 'trucker-1'
            ..buyerName = 'B'
            ..farmerName = 'F'
            ..cropType = 'wheat'
            ..quantity = 40
            ..pricePerUnit = 60
            ..totalPrice = 2400
            ..status = status
            ..createdAt = now
            ..version = 1
            ..contactOpen = contactOpen
            ..buyerPhone = '+251911000001'
            ..farmerPhone = '+251911000002'
            ..truckerPhone = '+251911000003';

      test('crop and trip threads are closed', () async {
        expect(
          (await resolver.resolveContact('crop', 'crop-1', 'farmer-1')).open,
          isFalse,
        );
        expect(
          (await resolver.resolveContact('trip', 'trip-1', 'trucker-1')).open,
          isFalse,
        );
      });

      test('an open order gives the counterpart\'s number', () async {
        await isar.writeTxn(() async => isar.orders.put(order()));

        final contact =
            await resolver.resolveContact('order', 'order-1', 'farmer-1');
        expect(contact.open, isTrue);
        expect(contact.phone, '+251911000002');
      });

      test('a closed order gives no number', () async {
        await isar.writeTxn(
          () async => isar.orders.put(order(status: OrderStatus.cancelled)),
        );

        final contact =
            await resolver.resolveContact('order', 'order-1', 'farmer-1');
        expect(contact.open, isFalse);
        expect(contact.phone, isNull);
      });

      test('a delivery thread follows the delivery\'s order', () async {
        await isar.writeTxn(() async {
          await isar.orders.put(order(status: OrderStatus.inTransit));
          await isar.deliverys
              .put(delivery('delivery-1', 'crop-9')..orderId = 'order-1');
        });

        final contact = await resolver.resolveContact(
          'delivery',
          'delivery-1',
          'trucker-1',
        );
        expect(contact.open, isTrue);
        expect(contact.phone, '+251911000003');
      });

      test('an order not on the device is unknown, not closed', () async {
        await isar.writeTxn(
          () async => isar.deliverys.put(delivery('delivery-2', 'crop-9')),
        );

        expect(
          (await resolver.resolveContact('order', 'order-x', 'farmer-1')).open,
          isNull,
        );
        expect(
          (await resolver.resolveContact('delivery', 'delivery-2', 'farmer-1'))
              .open,
          isNull,
        );
      });
    });
  });
}

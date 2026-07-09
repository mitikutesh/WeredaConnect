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
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';

void main() {
  group('DeliveryBloc', () {
    late Isar isar;
    late Directory tempDir;
    late DeliveryRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('delivery_bloc_test_');
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
        name: 'test_delivery_bloc_${DateTime.now().millisecondsSinceEpoch}',
      );
      repository = DeliveryRepository(
        isar: isar,
        syncQueue: SyncQueue(isar),
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('initial state is DeliveryInitial', () {
      final bloc = DeliveryBloc(repository: repository);
      expect(bloc.state, const DeliveryInitial());
      bloc.close();
    });

    test('emits [Loading, DeliveriesLoaded] on LoadDeliveries', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(const LoadDeliveries(tripId: 'trip-001', userId: 'trucker-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveriesLoaded>());
      expect((states[1] as DeliveriesLoaded).deliveries, isEmpty);

      await subscription.cancel();
      await bloc.close();
    });

    test('emits [Loading, DeliveryCreated] on CreateDelivery', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const CreateDelivery(
          tripId: 'trip-001',
          listingId: 'listing-001',
          farmerId: 'farmer-001',
          farmerName: 'Abebe Kebede',
          truckerId: 'trucker-001',
          truckerName: 'Dawit Assefa',
          buyerId: 'buyer-001',
          buyerName: 'Sara Hailu',
          qrToken: 'qr-token-abc',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveryCreated>());
      expect((states[1] as DeliveryCreated).delivery.farmerId, 'farmer-001');

      await subscription.cancel();
      await bloc.close();
    });

    test(
        'emits [Loading, DeliveryCreated] on CreateSelfPickupDelivery '
        '(no trucker, deliveryType=self_pickup, tripId=deliveryId)', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const CreateSelfPickupDelivery(
          orderId: 'order-001',
          listingId: 'listing-001',
          farmerId: 'farmer-001',
          farmerName: 'Abebe Kebede',
          buyerId: 'buyer-001',
          buyerName: 'Sara Hailu',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveryCreated>());
      final created = (states[1] as DeliveryCreated).delivery;
      expect(created.deliveryType, 'self_pickup');
      expect(created.truckerId, isEmpty);
      expect(created.buyerId, 'buyer-001');
      // No trip: sort key is synthesized from the deliveryId.
      expect(created.tripId, created.deliveryId);

      await subscription.cancel();
      await bloc.close();
    });

    test('emits [Loading, PickupConfirmed] on ConfirmPickup', () async {
      // Create a delivery first
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

      final bloc = DeliveryBloc(repository: repository);
      final expectation = expectLater(
        bloc.stream,
        emitsInOrder([isA<DeliveryLoading>(), isA<PickupConfirmed>()]),
      );

      bloc.add(
        ConfirmPickup(
          deliveryId: delivery.deliveryId,
          truckerId: 'trucker-001',
          scannedQrNonce: 'test-nonce-abc',
        ),
      );

      await expectation;
      await bloc.close();
    });

    test('emits [Loading, DeliveryConfirmed] on ConfirmDelivery', () async {
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

      // Delivery must be inTransit for confirmDelivery to succeed
      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await isar.writeTxn(() async {
        final d = await isar.deliverys
            .filter()
            .deliveryIdEqualTo(delivery.deliveryId)
            .findFirst();
        d!.status = DeliveryStatus.inTransit;
        d.updatedAt = DateTime.now().toUtc();
        d.version += 1;
        await isar.deliverys.put(d);
      });

      final bloc = DeliveryBloc(repository: repository);
      final expectation = expectLater(
        bloc.stream,
        emitsInOrder([isA<DeliveryLoading>(), isA<DeliveryConfirmed>()]),
      );

      bloc.add(
        ConfirmDelivery(
          deliveryId: delivery.deliveryId,
          buyerId: 'buyer-001',
          scannedQrNonce: 'test-nonce-xyz',
        ),
      );

      await expectation;
      await bloc.close();
    });

    test('emits [Loading, DisputeRaised, DeliveriesLoaded] on RaiseDispute',
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

      // Reach delivered status so raiseDispute succeeds
      await repository.confirmPickup(
        deliveryId: delivery.deliveryId,
        truckerId: 'trucker-001',
      );
      await isar.writeTxn(() async {
        final d = await isar.deliverys
            .filter()
            .deliveryIdEqualTo(delivery.deliveryId)
            .findFirst();
        d!.status = DeliveryStatus.inTransit;
        d.updatedAt = DateTime.now().toUtc();
        d.version += 1;
        await isar.deliverys.put(d);
      });
      await repository.confirmDelivery(
        deliveryId: delivery.deliveryId,
        buyerId: 'buyer-001',
      );

      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        RaiseDispute(
          deliveryId: delivery.deliveryId,
          userId: 'buyer-001',
          reason: 'Wrong quantity',
          tripId: 'trip-001',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 3);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DisputeRaised>());
      expect(states[2], isA<DeliveriesLoaded>());
      final loaded = states[2] as DeliveriesLoaded;
      expect(loaded.deliveries.length, 1);
      expect(loaded.deliveries.first.status, DeliveryStatus.disputed);

      await subscription.cancel();
      await bloc.close();
    });

    test(
        'emits [Loading, DeliveryCancelled, DeliveriesLoaded] on CancelDelivery',
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

      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        CancelDelivery(
          deliveryId: delivery.deliveryId,
          userId: 'trucker-001',
          tripId: 'trip-001',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 3);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveryCancelled>());
      expect(states[2], isA<DeliveriesLoaded>());
      final loaded = states[2] as DeliveriesLoaded;
      expect(loaded.deliveries.length, 1);
      expect(loaded.deliveries.first.status, DeliveryStatus.cancelled);

      await subscription.cancel();
      await bloc.close();
    });

    test('FilterDeliveries by status returns filtered list', () async {
      // Create deliveries
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

      // Pick up first delivery
      await repository.confirmPickup(
        deliveryId: d1.deliveryId,
        truckerId: 'trucker-001',
      );

      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const FilterDeliveries(
          tripId: 'trip-001',
          userId: 'trucker-001',
          statusFilter: 'pickedUp',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveriesLoaded>());
      final loaded = states[1] as DeliveriesLoaded;
      expect(loaded.deliveries.length, 1);
      expect(loaded.statusFilter, 'pickedUp');
      expect(loaded.deliveries.first.deliveryId, d1.deliveryId);

      await subscription.cancel();
      await bloc.close();
    });

    test('FilterDeliveries with no status filter loads all for trip', () async {
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

      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const FilterDeliveries(tripId: 'trip-001', userId: 'trucker-001'),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[1], isA<DeliveriesLoaded>());
      final loaded = states[1] as DeliveriesLoaded;
      expect(loaded.deliveries.length, 2);
      expect(loaded.statusFilter, isNull);

      await subscription.cancel();
      await bloc.close();
    });

    test('LoadDeliveries for nonexistent trip emits empty list', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const LoadDeliveries(
          tripId: 'nonexistent-trip',
          userId: 'trucker-001',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states.length, 2);
      expect(states[0], isA<DeliveryLoading>());
      expect(states[1], isA<DeliveriesLoaded>());
      expect((states[1] as DeliveriesLoaded).deliveries, isEmpty);

      await subscription.cancel();
      await bloc.close();
    });

    test('Create then Load shows the created delivery', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      // Create a delivery
      bloc.add(
        const CreateDelivery(
          tripId: 'trip-001',
          listingId: 'listing-001',
          farmerId: 'farmer-001',
          farmerName: 'Abebe',
          truckerId: 'trucker-001',
          truckerName: 'Dawit',
          buyerId: 'buyer-001',
          buyerName: 'Sara',
          qrToken: 'qr-token',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // Now load deliveries
      bloc.add(const LoadDeliveries(tripId: 'trip-001', userId: 'trucker-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));

      // Should have: Loading, Created, Loading, Loaded(1 delivery)
      expect(states.length, 4);
      expect(states[1], isA<DeliveryCreated>());
      expect(states[3], isA<DeliveriesLoaded>());
      expect((states[3] as DeliveriesLoaded).deliveries.length, 1);

      await subscription.cancel();
      await bloc.close();
    });

    test('CreateDelivery with Amharic names preserves characters', () async {
      final bloc = DeliveryBloc(repository: repository);
      final states = <DeliveryState>[];
      final subscription = bloc.stream.listen(states.add);

      bloc.add(
        const CreateDelivery(
          tripId: 'trip-001',
          listingId: 'listing-001',
          farmerId: 'farmer-001',
          farmerName: 'ታደሰ ወርቁ',
          truckerId: 'trucker-001',
          truckerName: 'ዳዊት አሰፋ',
          buyerId: 'buyer-001',
          buyerName: 'ሳራ ሃይሉ',
          qrToken: 'qr-token',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(states[1], isA<DeliveryCreated>());
      final created = states[1] as DeliveryCreated;
      expect(created.delivery.farmerName, 'ታደሰ ወርቁ');
      expect(created.delivery.truckerName, 'ዳዊት አሰፋ');
      expect(created.delivery.buyerName, 'ሳራ ሃይሉ');

      await subscription.cancel();
      await bloc.close();
    });
  });
}

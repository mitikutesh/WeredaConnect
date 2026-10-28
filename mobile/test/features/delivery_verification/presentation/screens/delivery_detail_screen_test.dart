import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDeliveryState extends Fake implements DeliveryState {}

Delivery _makeDelivery({
  String deliveryId = 'delivery-001-abcd-efgh',
  DeliveryStatus status = DeliveryStatus.pending,
  LatLng? gpsPickup,
  LatLng? gpsDropoff,
  DateTime? pickedUpAt,
  DateTime? confirmedAt,
  String? disputeReason,
  String farmerId = 'farmer-001',
  String truckerId = 'trucker-001',
  String buyerId = 'buyer-001',
  String? deliveryType,
}) {
  return Delivery()
    ..deliveryId = deliveryId
    ..tripId = 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = farmerId
    ..farmerName = 'Abebe Kebede'
    ..truckerId = truckerId
    ..truckerName = 'Tadesse Worku'
    ..buyerId = buyerId
    ..buyerName = 'Mekonnen Tadesse'
    ..qrToken = 'test-token'
    ..deliveryType = deliveryType
    ..status = status
    ..gpsPickup = gpsPickup
    ..gpsDropoff = gpsDropoff
    ..pickedUpAt = pickedUpAt
    ..confirmedAt = confirmedAt
    ..disputeReason = disputeReason
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

void main() {
  late MockDeliveryBloc deliveryBloc;

  setUpAll(() {
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
  });

  setUp(() {
    deliveryBloc = MockDeliveryBloc();
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget({
    required Delivery delivery,
    String currentUserId = 'farmer-001',
  }) {
    return BlocProvider<DeliveryBloc>.value(
      value: deliveryBloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: DeliveryDetailScreen(
          delivery: delivery,
          currentUserId: currentUserId,
        ),
      ),
    );
  }

  group('DeliveryDetailScreen', () {
    testWidgets('renders delivery ID in app bar', (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      // deliveryId.substring(0, 8) = 'delivery'
      expect(find.textContaining('delivery'), findsAtLeastNWidgets(1));
    });

    testWidgets(
        'buyer sees the self-pickup scan action on a self_pickup pending delivery',
        (tester) async {
      final delivery = _makeDelivery(
        truckerId: '',
        deliveryType: 'self_pickup',
      );

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery, currentUserId: 'buyer-001'),
      );
      await tester.pumpAndSettle();

      // Without a local Isar escrow record (widget test), the gate falls back to
      // enabled and shows the self-pickup collect action.
      expect(find.text("Scan farmer's QR to collect"), findsOneWidget);
    });

    testWidgets(
        'buyer does NOT see the self-pickup scan action on a normal delivery',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery, currentUserId: 'buyer-001'),
      );
      await tester.pumpAndSettle();

      expect(find.text("Scan farmer's QR to collect"), findsNothing);
    });

    testWidgets('renders participant info', (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Farmer'), findsOneWidget);
      expect(find.text('Abebe Kebede'), findsOneWidget);
      expect(find.text('Trucker'), findsOneWidget);
      expect(find.text('Tadesse Worku'), findsOneWidget);
      expect(find.text('Buyer'), findsOneWidget);
      expect(find.text('Mekonnen Tadesse'), findsOneWidget);
    });

    testWidgets('renders status timeline steps', (tester) async {
      final delivery = _makeDelivery(status: DeliveryStatus.inTransit);

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Pending'), findsOneWidget);
      expect(find.text('Picked Up'), findsOneWidget);
      expect(find.text('In Transit'), findsOneWidget);
      expect(find.text('Delivered'), findsOneWidget);
    });

    testWidgets('shows GPS coordinates when gpsPickup is set', (tester) async {
      final gps = LatLng()
        ..latitude = 9.0192
        ..longitude = 38.7525;
      final delivery = _makeDelivery(
        status: DeliveryStatus.pickedUp,
        gpsPickup: gps,
        pickedUpAt: DateTime(2026, 2, 21),
      );

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.textContaining('9.0192'), findsOneWidget);
      expect(find.textContaining('38.7525'), findsOneWidget);
    });

    testWidgets('shows "Not captured" when GPS is null', (tester) async {
      final delivery = _makeDelivery(
        status: DeliveryStatus.pickedUp,
        pickedUpAt: DateTime(2026, 2, 21),
      );

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Not captured'), findsOneWidget);
    });

    testWidgets('shows Show QR Code button for farmer with pending delivery',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery),
      );
      await tester.pumpAndSettle();

      expect(find.text('Show QR Code'), findsOneWidget);
      expect(find.byIcon(Icons.qr_code), findsOneWidget);
    });

    testWidgets(
        'farmer of a pending self-pickup gets the QR, worded for the buyer',
        (tester) async {
      final delivery =
          _makeDelivery(truckerId: '', deliveryType: 'self_pickup');
      final repository = MockDeliveryRepository();
      when(() => deliveryBloc.repository).thenReturn(repository);
      when(
        () => repository.persistQrNonce(
          deliveryId: any(named: 'deliveryId'),
          qrNonce: any(named: 'qrNonce'),
          userId: any(named: 'userId'),
        ),
      ).thenAnswer((_) async {});

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text('Show QR Code'));
      await tester.tap(find.text('Show QR Code'));
      // Not pumpAndSettle: the QR spinner keeps animating while the device
      // key (secure storage) never answers in a widget test.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 500));

      expect(
        find.text('Show this QR code to the buyer when they collect'),
        findsOneWidget,
      );
      expect(find.text('Show this QR code to the trucker'), findsNothing);
    });

    testWidgets('shows Confirm Pickup button for trucker with pending delivery',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery, currentUserId: 'trucker-001'),
      );
      await tester.pumpAndSettle();

      expect(find.text('Confirm Pickup'), findsOneWidget);
      expect(find.byIcon(Icons.qr_code_scanner), findsOneWidget);
    });

    testWidgets(
        'shows Mark In Transit button for trucker with pickedUp delivery',
        (tester) async {
      final delivery = _makeDelivery(
        status: DeliveryStatus.pickedUp,
        pickedUpAt: DateTime(2026, 2, 21),
      );

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery, currentUserId: 'trucker-001'),
      );
      await tester.pumpAndSettle();

      expect(find.text('Mark In Transit'), findsOneWidget);
    });

    testWidgets(
        'shows Confirm Delivery button for buyer with inTransit delivery',
        (tester) async {
      final delivery = _makeDelivery(status: DeliveryStatus.inTransit);

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery, currentUserId: 'buyer-001'),
      );
      await tester.pumpAndSettle();

      expect(find.text('Confirm Delivery'), findsOneWidget);
    });

    // ESC-08: every party may cancel before pickup (the same screen on mobile
    // and web), nobody after.
    for (final userId in ['farmer-001', 'trucker-001', 'buyer-001']) {
      testWidgets('shows Cancel Delivery to $userId while pending',
          (tester) async {
        await tester.pumpWidget(
          buildTestWidget(delivery: _makeDelivery(), currentUserId: userId),
        );
        await tester.pumpAndSettle();

        expect(find.text('Cancel Delivery'), findsOneWidget);
        expect(find.byIcon(Icons.cancel_outlined), findsOneWidget);
      });

      testWidgets('hides Cancel Delivery from $userId once picked up',
          (tester) async {
        await tester.pumpWidget(
          buildTestWidget(
            delivery: _makeDelivery(status: DeliveryStatus.pickedUp),
            currentUserId: userId,
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text('Cancel Delivery'), findsNothing);
      });
    }

    testWidgets('shows Cancel Delivery to the buyer of a pending self-pickup',
        (tester) async {
      await tester.pumpWidget(
        buildTestWidget(
          delivery: _makeDelivery(truckerId: '', deliveryType: 'self_pickup'),
          currentUserId: 'buyer-001',
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Cancel Delivery'), findsOneWidget);
    });

    testWidgets('shows dispute section when status is disputed',
        (tester) async {
      final delivery = _makeDelivery(
        status: DeliveryStatus.disputed,
        disputeReason: 'Wrong quantity delivered',
        confirmedAt: DateTime(2026, 2, 21),
      );

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery),
      );
      await tester.pumpAndSettle();

      expect(find.text('Disputed'), findsAtLeastNWidgets(1));
      expect(find.text('Wrong quantity delivered'), findsOneWidget);
    });

    testWidgets('renders Delivery Details section title', (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Delivery Details'), findsOneWidget);
    });

    testWidgets('shows Pickup and Delivery section titles', (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Pickup'), findsAtLeastNWidgets(1));
      expect(find.text('Delivery'), findsAtLeastNWidgets(1));
    });

    testWidgets('shows "Not yet picked up" when pickedUpAt is null',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Not yet picked up'), findsOneWidget);
    });

    testWidgets('shows "Not yet delivered" when confirmedAt is null',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(buildTestWidget(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Not yet delivered'), findsOneWidget);
    });
  });

  // LT-11: the buyer and the farmer open the live trip from the delivery;
  // the trucker never gets the buyer's map here.
  group('DeliveryDetailScreen live trip (LT-11)', () {
    for (final status in [
      DeliveryStatus.pickedUp,
      DeliveryStatus.inTransit,
      DeliveryStatus.delivered,
    ]) {
      for (final userId in ['buyer-001', 'farmer-001']) {
        testWidgets('$userId sees Live trip on a ${status.name} delivery',
            (tester) async {
          await tester.pumpWidget(
            buildTestWidget(
              delivery: _makeDelivery(status: status),
              currentUserId: userId,
            ),
          );
          await tester.pumpAndSettle();
          expect(find.byKey(const Key('delivery_live_trip')), findsOneWidget);
        });
      }

      testWidgets('the trucker sees no Live trip on a ${status.name} delivery',
          (tester) async {
        await tester.pumpWidget(
          buildTestWidget(
            delivery: _makeDelivery(status: status),
            currentUserId: 'trucker-001',
          ),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('delivery_live_trip')), findsNothing);
      });
    }

    testWidgets('nobody gets it before pickup or on a self-pickup',
        (tester) async {
      for (final delivery in [
        _makeDelivery(),
        _makeDelivery(
          status: DeliveryStatus.delivered,
          truckerId: '',
          deliveryType: 'self_pickup',
        ),
      ]) {
        await tester.pumpWidget(
          buildTestWidget(delivery: delivery, currentUserId: 'buyer-001'),
        );
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('delivery_live_trip')), findsNothing);
      }
    });
  });
}

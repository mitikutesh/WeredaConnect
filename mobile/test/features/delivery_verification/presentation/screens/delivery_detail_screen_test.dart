import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

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

    testWidgets('shows Cancel Delivery button for participant with pending',
        (tester) async {
      final delivery = _makeDelivery();

      await tester.pumpWidget(
        buildTestWidget(delivery: delivery),
      );
      await tester.pumpAndSettle();

      expect(find.text('Cancel Delivery'), findsOneWidget);
      expect(find.byIcon(Icons.cancel_outlined), findsOneWidget);
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
}

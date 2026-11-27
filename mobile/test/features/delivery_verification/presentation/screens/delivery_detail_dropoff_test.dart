import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

const _deliveryId = '6f1c2a4e-1111-4b2c-9d3e-0a1b2c3d4e5f';
const _arrivalKey = 'uploads/photos/$_deliveryId/arrival-1700000.webp';

enum _Platform { phone, web }

Delivery _delivery({
  DeliveryStatus status = DeliveryStatus.inTransit,
  String? arrivalPhotoRef = _arrivalKey,
  DateTime? confirmedAt,
  DateTime? autoConfirmedAt,
  String? disputeReason,
}) {
  final now = DateTime.now().toUtc();
  return Delivery()
    ..deliveryId = _deliveryId
    ..tripId = 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Abebe Kebede'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Tadesse Worku'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..qrToken = ''
    ..orderId = 'order-001'
    ..status = status
    ..pickedUpAt = now.subtract(const Duration(days: 1))
    ..confirmedAt = confirmedAt
    ..autoConfirmedAt = autoConfirmedAt
    ..disputeReason = disputeReason
    ..arrivalPhotoRef = arrivalPhotoRef
    ..createdAt = now.subtract(const Duration(days: 2))
    ..updatedAt = now
    ..version = 4;
}

Order _order({
  String judgement = 'pending',
  OrderStatus status = OrderStatus.inTransit,
  bool checkedIn = true,
}) {
  final now = DateTime.now().toUtc();
  return Order()
    ..orderId = 'order-001'
    ..listingId = 'listing-001'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..farmerName = 'Abebe Kebede'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 85
    ..totalPrice = 8500
    ..status = status
    ..createdAt = now.subtract(const Duration(days: 3))
    ..version = 7
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..fulfillment = 'trucker'
    ..dropoffJudgement = judgement
    ..dropoffDeadlineAt = now.add(const Duration(days: 1))
    ..checkIns = [
      if (checkedIn)
        OrderCheckIn()
          ..kind = CheckInKind.arrivedDropoff
          ..receivedAt = now.subtract(const Duration(hours: 3))
          ..distanceChecked = true,
    ];
}

/// ESC-36, the client part (FUP-336): the delivery detail shows the
/// trucker's arrival photo, the drop-off check-in with what happens next
/// (the buyer next to Confirm Delivery; the trucker's button since FUP-371),
/// a system confirm, and the label of a system dispute, for the trucker,
/// the buyer and the farmer.
void main() {
  late MockDeliveryBloc deliveryBloc;
  late MockOrderBloc orderBloc;
  late MockOrderRepository orderRepo;
  late Order? order;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() => registerFallbackValue(FakeDeliveryEvent()));

  setUp(() {
    deliveryBloc = MockDeliveryBloc();
    orderBloc = MockOrderBloc();
    orderRepo = MockOrderRepository();
    order = _order();
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.repository).thenReturn(orderRepo);
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderRepo.getOrderById('order-001'))
        .thenAnswer((_) async => order);
  });

  Future<void> show(
    WidgetTester tester,
    Delivery delivery,
    String userId,
    _Platform platform,
  ) async {
    tester.view.physicalSize = const Size(1000, 5000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(const SizedBox.shrink());
    final screen = MultiBlocProvider(
      providers: [
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<OrderBloc>.value(value: orderBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: DeliveryDetailScreen(delivery: delivery, currentUserId: userId),
      ),
    );
    await tester.pumpWidget(
      switch (platform) {
        _Platform.phone => Provider<MarketplaceSync?>.value(
            value: MockMarketplaceSync(),
            child: screen,
          ),
        _Platform.web =>
          Provider<MarketplaceSync?>.value(value: null, child: screen),
      },
    );
    await tester.pumpAndSettle();
  }

  Finder textWith(String part) => find.textContaining(part, findRichText: true);

  for (final platform in _Platform.values) {
    group(platform.name, () {
      testWidgets(
          'buyer: the check-in time and when it may be confirmed for them, '
          'next to Confirm Delivery', (tester) async {
        await show(tester, _delivery(), 'buyer-001', platform);

        expect(find.byKey(const Key('dropoff_check_in_card')), findsOneWidget);
        expect(textWith('Received'), findsOneWidget);
        expect(textWith('may be confirmed for you'), findsOneWidget);
        expect(find.text(en.deliveryConfirmDelivery), findsOneWidget);
        expect(find.byKey(const Key('delivery_arrival_photo')), findsOneWidget);
        expect(find.text(en.deliveryPhotoArrival), findsOneWidget);
      });

      // FUP-371: the trucker's button is here too now
      // (delivery_detail_check_in_test.dart taps it).
      testWidgets('trucker: the state and "Arrived at drop-off"',
          (tester) async {
        order = _order(checkedIn: false);
        await show(
          tester,
          _delivery(arrivalPhotoRef: null),
          'trucker-001',
          platform,
        );

        expect(find.byKey(const Key('dropoff_check_in_card')), findsOneWidget);
        expect(
          textWith('Check in when you reach the drop-off'),
          findsOneWidget,
        );
        expect(find.text(en.dropoffCheckInButton), findsOneWidget);
        expect(find.byKey(const Key('delivery_arrival_photo')), findsNothing);
      });

      testWidgets('trucker, checked in: the state, no button', (tester) async {
        await show(tester, _delivery(), 'trucker-001', platform);

        expect(textWith('Received'), findsOneWidget);
        expect(find.text(en.dropoffCheckInButton), findsNothing);
      });

      testWidgets('farmer: the buyer\'s window after the check-in',
          (tester) async {
        await show(tester, _delivery(), 'farmer-001', platform);

        expect(textWith('may be confirmed for them'), findsOneWidget);
        expect(find.byKey(const Key('delivery_arrival_photo')), findsOneWidget);
      });

      testWidgets('a system confirm says so, for every party', (tester) async {
        final now = DateTime.now().toUtc();
        order = _order(
          judgement: DropoffJudgementOutcome.autoConfirmed,
          status: OrderStatus.delivered,
        );
        for (final userId in ['buyer-001', 'farmer-001', 'trucker-001']) {
          await show(
            tester,
            _delivery(
              status: DeliveryStatus.delivered,
              confirmedAt: now.subtract(const Duration(hours: 1)),
              autoConfirmedAt: now.subtract(const Duration(hours: 1)),
            ),
            userId,
            platform,
          );

          expect(
            find.byKey(const Key('delivery_auto_confirmed')),
            findsOneWidget,
          );
          expect(textWith('Confirmed by the system'), findsWidgets);
          expect(find.text(en.dropoffJudgementAutoConfirmed), findsOneWidget);
        }
        // The buyer still has the normal dispute window.
        expect(find.text(en.deliveryRaiseDispute), findsNothing);
        await show(
          tester,
          _delivery(
            status: DeliveryStatus.delivered,
            confirmedAt: now.subtract(const Duration(hours: 1)),
            autoConfirmedAt: now.subtract(const Duration(hours: 1)),
          ),
          'buyer-001',
          platform,
        );
        expect(find.text(en.deliveryRaiseDispute), findsOneWidget);
      });

      testWidgets('a system dispute shows its label, never the raw code',
          (tester) async {
        for (final (reason, label) in [
          ('delivery_overdue', en.dropoffJudgementDeliveryOverdue),
          ('silent_buyer', en.dropoffJudgementSilentBuyer),
          ('dropoff_out_of_range', en.dropoffJudgementOutOfRange),
        ]) {
          order = _order(judgement: reason, status: OrderStatus.disputed);
          await show(
            tester,
            _delivery(status: DeliveryStatus.disputed, disputeReason: reason),
            'buyer-001',
            platform,
          );

          expect(find.text(reason), findsNothing);
          // The dispute card and the drop-off card.
          expect(find.text(label), findsNWidgets(2));
        }
      });

      testWidgets('a party\'s own dispute reason stays as written',
          (tester) async {
        order = _order(
          judgement: DropoffJudgementOutcome.closed,
          status: OrderStatus.disputed,
        );
        await show(
          tester,
          _delivery(
            status: DeliveryStatus.disputed,
            disputeReason: 'Two sacks were wet.',
          ),
          'farmer-001',
          platform,
        );

        expect(find.text('Two sacks were wet.'), findsOneWidget);
        expect(find.text(en.dropoffJudgementClosed), findsOneWidget);
      });

      testWidgets('an order without the stamp shows no drop-off card',
          (tester) async {
        order = _order()..dropoffJudgement = null;
        await show(
          tester,
          _delivery(arrivalPhotoRef: null),
          'buyer-001',
          platform,
        );

        expect(find.byKey(const Key('dropoff_check_in_card')), findsNothing);
      });
    });
  }
}

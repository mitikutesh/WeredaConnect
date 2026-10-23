import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class FakeOrderState extends Fake implements OrderState {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDeliveryState extends Fake implements DeliveryState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

const _farmerId = 'farmer-001';
const _buyerId = 'buyer-001';
const _truckerId = 'trucker-001';
const _tripId = 'trip-001';

/// Build an accepted order. Callers toggle whether it carries a trip/trucker
/// to exercise the two delivery-creation paths.
Order _makeOrder({
  OrderStatus status = OrderStatus.accepted,
  String? tripId,
  String? truckerId,
  String? truckerName,
  DateTime? pickupDeadlineAt,
}) {
  return Order()
    ..orderId = 'order-001'
    ..listingId = 'listing-001'
    ..buyerId = _buyerId
    ..farmerId = _farmerId
    ..buyerName = 'Buyer Bee'
    ..farmerName = 'Farmer Fikru'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 85
    ..totalPrice = 8500
    ..status = status
    ..createdAt = DateTime(2026, 3, 10)
    ..version = 1
    ..tripId = tripId
    ..truckerId = truckerId
    ..truckerName = truckerName
    ..pickupDeadlineAt = pickupDeadlineAt;
}

/// A delivery of order-001 (self-pickup unless a trucker is given).
Delivery _makeDelivery({
  DeliveryStatus status = DeliveryStatus.pending,
  String orderId = 'order-001',
  String truckerId = '',
}) {
  return Delivery()
    ..deliveryId = 'delivery-001-abcd'
    ..tripId = 'delivery-001-abcd'
    ..orderId = orderId
    ..listingId = 'listing-001'
    ..farmerId = _farmerId
    ..farmerName = 'Farmer Fikru'
    ..truckerId = truckerId
    ..truckerName = ''
    ..buyerId = _buyerId
    ..buyerName = 'Buyer Bee'
    ..qrToken = ''
    ..deliveryType = truckerId.isEmpty ? 'self_pickup' : null
    ..status = status
    ..createdAt = DateTime(2026, 9, 29)
    ..updatedAt = DateTime(2026, 9, 29)
    ..version = 1;
}

void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockDeliveryRepository deliveryRepo;
  late StreamController<DeliveryState> deliveryStream;
  late Delivery? activeDelivery;
  late MockRatingBloc ratingBloc;

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeOrderState());
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
  });

  setUp(() {
    // The buyer's rate section reads what was already rated (nothing yet).
    ratingBloc = MockRatingBloc();
    final ratingRepo = MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);

    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    deliveryStream = StreamController<DeliveryState>.broadcast();

    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);

    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());

    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => deliveryStream.stream);
    when(() => deliveryBloc.add(any())).thenReturn(null);

    // The order's delivery is looked up among the user's own deliveries.
    deliveryRepo = MockDeliveryRepository();
    activeDelivery = null;
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => activeDelivery);
    when(
      () => deliveryRepo.persistQrNonce(
        deliveryId: any(named: 'deliveryId'),
        qrNonce: any(named: 'qrNonce'),
        userId: any(named: 'userId'),
      ),
    ).thenAnswer((_) async {});
  });

  tearDown(() {
    deliveryStream.close();
  });

  void stubAuth(UserRole role, String userId) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: userId,
        tokens: _testTokens,
        role: role,
        userName: 'Signed-in User',
      ),
    );
  }

  Widget buildScreen(Order order, {Locale locale = const Locale('en')}) {
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    return MultiBlocProvider(
      providers: [
        BlocProvider<OrderBloc>.value(value: orderBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: const OrderDetailScreen(orderId: 'order-001'),
      ),
    );
  }

  group('OrderDetailScreen delivery-creation gating', () {
    testWidgets(
        'trucker sees "Create Delivery" and tapping dispatches CreateDelivery',
        (tester) async {
      stubAuth(UserRole.trucker, _truckerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(
            tripId: _tripId,
            truckerId: _truckerId,
            truckerName: 'Trucker Tesfaye',
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Match on the label text, not the button type: `FilledButton.icon`'s
      // concrete runtime type differs across Flutter versions, so
      // `find.byType(FilledButton)` (exact-type) is not portable.
      final button = find.text('Create Delivery');
      expect(button, findsOneWidget);

      await tester.ensureVisible(button);
      await tester.tap(button);
      await tester.pump();

      verify(() => deliveryBloc.add(any(that: isA<CreateDelivery>())))
          .called(1);
    });

    testWidgets(
        'buyer with no trucker sees "Collect it myself" once accepted '
        'and tapping dispatches CreateSelfPickupDelivery', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      final button = find.text('Collect it myself');
      expect(button, findsOneWidget);

      await tester.ensureVisible(button);
      await tester.tap(button);
      await tester.pump();

      verify(
        () => deliveryBloc.add(
          const CreateSelfPickupDelivery(
            orderId: 'order-001',
            listingId: 'listing-001',
            farmerId: _farmerId,
            farmerName: 'Farmer Fikru',
            buyerId: _buyerId,
            buyerName: 'Buyer Bee',
          ),
        ),
      ).called(1);
    });

    testWidgets('buyer does NOT see self-pickup when a trucker is assigned',
        (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(
            tripId: _tripId,
            truckerId: _truckerId,
            truckerName: 'Trucker Tesfaye',
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Collect it myself'), findsNothing);
      expect(find.text('Create Delivery'), findsNothing);
    });

    testWidgets('trucker does NOT see "Create Delivery" without a trip',
        (tester) async {
      stubAuth(UserRole.trucker, _truckerId);
      // No trip on the order, so this trucker is not the assigned carrier and
      // cannot be authorized anyway — but assert the button is absent.
      await tester.pumpWidget(
        buildScreen(_makeOrder(truckerId: _truckerId)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Create Delivery'), findsNothing);
    });

    testWidgets('buyer sees no self-pickup before the farmer accepts',
        (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(_makeOrder(status: OrderStatus.funded)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Collect it myself'), findsNothing);
    });
  });

  group('OrderDetailScreen with the order\'s delivery', () {
    testWidgets(
        'farmer of an accepted self-pickup order can show the pickup QR '
        'and open the delivery', (tester) async {
      stubAuth(UserRole.farmer, _farmerId);
      activeDelivery = _makeDelivery();
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      expect(find.text('Show QR Code'), findsOneWidget);
      expect(find.text('Open delivery'), findsOneWidget);
      verify(
        () => deliveryRepo.findActiveDeliveryForOrder(
          'order-001',
          userId: _farmerId,
          role: 'farmer',
          // Lets web stop paging at deliveries older than the order.
          orderCreatedAt: DateTime(2026, 3, 10),
        ),
      ).called(1);
    });

    testWidgets('farmer sees no QR once the delivery is past pending',
        (tester) async {
      stubAuth(UserRole.farmer, _farmerId);
      activeDelivery =
          _makeDelivery(status: DeliveryStatus.pickedUp, truckerId: _truckerId);
      await tester.pumpWidget(
        buildScreen(_makeOrder(status: OrderStatus.pickupConfirmed)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Show QR Code'), findsNothing);
      expect(find.text('Open delivery'), findsOneWidget);
    });

    testWidgets(
        'buyer with a delivery already created sees "Open delivery", '
        'not "Collect it myself"', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      activeDelivery = _makeDelivery();
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      expect(find.text('Collect it myself'), findsNothing);
      expect(find.text('Show QR Code'), findsNothing);
      expect(find.text('Open delivery'), findsOneWidget);

      await tester.ensureVisible(find.text('Open delivery'));
      await tester.tap(find.text('Open delivery'));
      await tester.pumpAndSettle();
      expect(find.byType(DeliveryDetailScreen), findsOneWidget);
    });

    testWidgets('trucker with a delivery already created cannot create another',
        (tester) async {
      stubAuth(UserRole.trucker, _truckerId);
      activeDelivery = _makeDelivery(truckerId: _truckerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(
            tripId: _tripId,
            truckerId: _truckerId,
            truckerName: 'Trucker Tesfaye',
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Create Delivery'), findsNothing);
      expect(find.text('Open delivery'), findsOneWidget);
    });

    testWidgets('no live delivery for the order: the create is offered',
        (tester) async {
      // Cancelled and other orders' deliveries are filtered by the
      // repository (delivery_repository_test, web_delivery_repository_test).
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      expect(find.text('Collect it myself'), findsOneWidget);
      expect(find.text('Open delivery'), findsNothing);
    });

    testWidgets(
        'a created delivery opens, and back on the order it is '
        '"Open delivery"', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Collect it myself'));
      await tester.tap(find.text('Collect it myself'));
      await tester.pump();
      activeDelivery = _makeDelivery();
      deliveryStream.add(DeliveryCreated(delivery: _makeDelivery()));
      await tester.pumpAndSettle();
      expect(find.byType(DeliveryDetailScreen), findsOneWidget);

      await tester.pageBack();
      await tester.pumpAndSettle();

      expect(find.text('Collect it myself'), findsNothing);
      expect(find.text('Open delivery'), findsOneWidget);
    });

    testWidgets(
        'the farmer\'s "Show QR Code" opens the QR, worded for the buyer',
        (tester) async {
      stubAuth(UserRole.farmer, _farmerId);
      activeDelivery = _makeDelivery();
      await tester.pumpWidget(buildScreen(_makeOrder()));
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
    });

    testWidgets(
        'a create refused because the order already has a delivery '
        'opens that delivery instead of doing nothing', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Collect it myself'));
      await tester.tap(find.text('Collect it myself'));
      await tester.pump();
      // Created meanwhile (first tap, another device): the server refuses.
      activeDelivery = _makeDelivery();
      deliveryStream.add(
        const DeliveryError(
          message: 'SyncRejectedException: DELIVERY_VALIDATION: '
              'The order already has an active delivery.',
          code: 'generic',
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(DeliveryDetailScreen), findsOneWidget);
    });

    testWidgets('any other create failure shows an error', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Collect it myself'));
      await tester.tap(find.text('Collect it myself'));
      await tester.pump();
      deliveryStream.add(
        const DeliveryError(message: 'boom', code: 'generic'),
      );
      await tester.pumpAndSettle();

      expect(find.text('Delivery operation failed'), findsOneWidget);
      expect(find.byType(DeliveryDetailScreen), findsNothing);
      // Still offered, so the buyer can try again.
      expect(find.text('Collect it myself'), findsOneWidget);
    });

    testWidgets(
        'delivery states from other screens are ignored until this '
        'screen creates one', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(_makeOrder()));
      await tester.pumpAndSettle();

      deliveryStream.add(const DeliveryError(message: 'x', code: 'generic'));
      await tester.pumpAndSettle();

      expect(find.text('Delivery operation failed'), findsNothing);
    });
  });

  group('OrderDetailScreen buyer payment actions (ESC-44)', () {
    const payLabels = ['Pay Now', 'Pay again', 'Continue payment'];

    for (final (status, label) in [
      (OrderStatus.created, 'Pay Now'),
      (OrderStatus.pendingPayment, 'Pay Now'),
      (OrderStatus.paymentFailed, 'Pay again'),
      (OrderStatus.paymentSubmitted, 'Continue payment'),
    ]) {
      testWidgets('buyer sees "$label" on a ${status.name} order',
          (tester) async {
        stubAuth(UserRole.buyer, _buyerId);
        await tester.pumpWidget(buildScreen(_makeOrder(status: status)));
        await tester.pumpAndSettle();

        expect(find.text(label), findsOneWidget);
        for (final other in payLabels.where((l) => l != label)) {
          expect(find.text(other), findsNothing);
        }
      });

      for (final (role, userId) in [
        (UserRole.farmer, _farmerId),
        (UserRole.trucker, _truckerId),
      ]) {
        testWidgets(
            '${role.name} sees no payment action on a ${status.name} '
            'order', (tester) async {
          stubAuth(role, userId);
          await tester.pumpWidget(
            buildScreen(
              _makeOrder(
                status: status,
                tripId: _tripId,
                truckerId: _truckerId,
              ),
            ),
          );
          await tester.pumpAndSettle();

          for (final label in payLabels) {
            expect(find.text(label), findsNothing);
          }
        });
      }
    }

    testWidgets('a funded order offers the buyer no payment action',
        (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(_makeOrder(status: OrderStatus.funded)),
      );
      await tester.pumpAndSettle();

      for (final label in payLabels) {
        expect(find.text(label), findsNothing);
      }
    });
  });

  group('OrderDetailScreen cancel after a transport failure (ESC-06)', () {
    final passed = DateTime.now().toUtc().subtract(const Duration(hours: 1));
    final ahead = DateTime.now().toUtc().add(const Duration(days: 2));
    const buyerHint = 'The crops were not collected in time. You can cancel '
        'this order and get all your money back.';
    const partyHint = 'The crops were not collected in time. The buyer can '
        'now cancel this order.';

    Order truckOrder(DateTime? deadline) => _makeOrder(
          tripId: _tripId,
          truckerId: _truckerId,
          truckerName: 'Trucker Tesfaye',
          pickupDeadlineAt: deadline,
        );

    for (final (name, order) in [
      ('trucker order', () => truckOrder(passed)),
      ('self-pickup order', () => _makeOrder(pickupDeadlineAt: passed)),
    ]) {
      testWidgets('buyer of an accepted $name past its deadline can cancel',
          (tester) async {
        stubAuth(UserRole.buyer, _buyerId);
        await tester.pumpWidget(buildScreen(order()));
        await tester.pumpAndSettle();

        expect(find.text('Collect by'), findsOneWidget);
        expect(find.text(buyerHint), findsOneWidget);
        final cancel = find.text('Cancel Order');
        expect(cancel, findsOneWidget);

        await tester.ensureVisible(cancel);
        await tester.tap(cancel);
        await tester.pumpAndSettle();
        expect(
          find.text('You get back everything you paid, including transport '
              'and the platform commission.'),
          findsOneWidget,
        );
        await tester.tap(find.text('Cancel Order').last);
        await tester.pump();

        verify(
          () => orderBloc.add(
            any(
              that: isA<CancelOrder>()
                  .having((e) => e.orderId, 'orderId', 'order-001'),
            ),
          ),
        ).called(1);
      });
    }

    testWidgets('buyer sees no Cancel before the pickup deadline',
        (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(truckOrder(ahead)));
      await tester.pumpAndSettle();

      expect(find.text('Collect by'), findsOneWidget);
      expect(find.text('Cancel Order'), findsNothing);
      expect(find.text(buyerHint), findsNothing);
    });

    testWidgets('an accepted order without a deadline offers no Cancel',
        (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(truckOrder(null)));
      await tester.pumpAndSettle();

      expect(find.text('Collect by'), findsNothing);
      expect(find.text('Cancel Order'), findsNothing);
    });

    for (final (role, userId) in [
      (UserRole.farmer, _farmerId),
      (UserRole.trucker, _truckerId),
    ]) {
      testWidgets(
          '${role.name} is told the buyer can cancel, but gets no Cancel',
          (tester) async {
        stubAuth(role, userId);
        await tester.pumpWidget(buildScreen(truckOrder(passed)));
        await tester.pumpAndSettle();

        expect(find.text('Collect by'), findsOneWidget);
        expect(find.text(partyHint), findsOneWidget);
        expect(find.text('Cancel Order'), findsNothing);
      });
    }

    testWidgets('a funded order still offers the buyer Cancel', (tester) async {
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(_makeOrder(status: OrderStatus.funded)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Cancel Order'), findsOneWidget);
      expect(find.text('Collect by'), findsNothing);
    });
  });

  group('OrderDetailScreen rating (ESC-09)', () {
    // The order follows its money after delivery; disbursing can last days
    // while a manual payout waits, so the buyer may rate during it.
    // Tall enough that the whole detail list is built (it is lazy).
    void tallView(WidgetTester tester) {
      tester.view.physicalSize = const Size(1000, 6000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    for (final status in [
      OrderStatus.delivered,
      OrderStatus.disbursing,
      OrderStatus.settled,
    ]) {
      testWidgets('buyer can rate the farmer on a ${status.name} order',
          (tester) async {
        tallView(tester);
        stubAuth(UserRole.buyer, _buyerId);
        await tester.pumpWidget(buildScreen(_makeOrder(status: status)));
        await tester.pumpAndSettle();

        expect(find.text('Rate Farmer'), findsOneWidget);
      });
    }

    for (final status in [
      OrderStatus.inTransit,
      OrderStatus.disputed,
      OrderStatus.disputeResolved,
    ]) {
      testWidgets('buyer cannot rate a ${status.name} order', (tester) async {
        tallView(tester);
        stubAuth(UserRole.buyer, _buyerId);
        await tester.pumpWidget(buildScreen(_makeOrder(status: status)));
        await tester.pumpAndSettle();

        expect(find.text('Rate Farmer'), findsNothing);
      });
    }

    // ACC-03 + FUP-105: a deleted farmer reads as the localized "Deleted user"
    // on the order, and the buyer gets no Rate button for them (the server
    // refuses a rating of a deleted party).
    testWidgets('am: a deleted farmer reads in Amharic and cannot be rated',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(status: OrderStatus.settled)
            ..farmerName = deletedUserSentinel,
          locale: const Locale('am'),
        ),
      );
      await tester.pumpAndSettle();
      final am = lookupAppLocalizations(const Locale('am'));

      expect(find.text(am.deletedUser), findsOneWidget);
      expect(find.text(am.rateFarmer), findsNothing);
      expect(find.text(deletedUserSentinel), findsNothing);
    });

    // FUP-105: hide the Rate button for a deleted party on the order.
    testWidgets('buyer gets no Rate Farmer button for a deleted farmer',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(status: OrderStatus.settled)
            ..farmerName = deletedUserSentinel,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Rate Farmer'), findsNothing);
    });

    // FUP-105: only the deleted party's button goes; a live party is still rated.
    testWidgets(
        'buyer gets no Rate Trucker button for a deleted trucker but still '
        'rates the farmer', (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(
          _makeOrder(
            status: OrderStatus.settled,
            truckerId: _truckerId,
            truckerName: deletedUserSentinel,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Rate Trucker'), findsNothing);
      expect(find.text('Rate Farmer'), findsOneWidget);
    });

    testWidgets('the farmer never sees the rate section on a disbursing order',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.farmer, _farmerId);
      await tester.pumpWidget(
        buildScreen(_makeOrder(status: OrderStatus.disbursing)),
      );
      await tester.pumpAndSettle();

      expect(find.text('Rate Farmer'), findsNothing);
    });
  });

  // ESC-48: the parties reach each other here once the farmer accepted the
  // paid order, never before and never after the money went back.
  group('OrderDetailScreen contact (ESC-48)', () {
    void tallView(WidgetTester tester) {
      tester.view.physicalSize = const Size(1000, 6000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    Order openOrder({
      OrderStatus status = OrderStatus.accepted,
      bool? contactOpen = true,
      bool withTrucker = true,
      bool withPhones = true,
    }) {
      final order = _makeOrder(
        status: status,
        tripId: withTrucker ? _tripId : null,
        truckerId: withTrucker ? _truckerId : null,
        truckerName: withTrucker ? 'Trucker Tesfaye' : null,
      )..contactOpen = contactOpen;
      if (withPhones) {
        order
          ..buyerPhone = '+251911000001'
          ..farmerPhone = '+251911000002'
          ..truckerPhone = withTrucker ? '+251911000003' : null;
      }
      return order;
    }

    const allButtons = ['Contact Buyer', 'Contact Farmer', 'Contact Trucker'];
    const escrowNote = 'Contact opens after payment is held in escrow.';
    const acceptNote = 'Contact opens once the farmer accepts the order.';

    for (final (role, userId, expected) in [
      (UserRole.buyer, _buyerId, ['Contact Farmer', 'Contact Trucker']),
      (UserRole.farmer, _farmerId, ['Contact Buyer', 'Contact Trucker']),
      (UserRole.trucker, _truckerId, ['Contact Buyer', 'Contact Farmer']),
    ]) {
      testWidgets('${role.name} sees the other two parties on an open order',
          (tester) async {
        tallView(tester);
        stubAuth(role, userId);
        await tester.pumpWidget(buildScreen(openOrder()));
        await tester.pumpAndSettle();

        for (final label in allButtons) {
          expect(
            find.text(label),
            expected.contains(label) ? findsOneWidget : findsNothing,
            reason: label,
          );
        }
        expect(find.text(escrowNote), findsNothing);
      });
    }

    testWidgets('the sheet offers Call with the number and Message',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(openOrder(status: OrderStatus.inTransit)),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Contact Farmer'));
      await tester.pumpAndSettle();

      expect(find.text('+251911000002'), findsOneWidget);
      expect(find.text('Call'), findsOneWidget);
      expect(find.text('Send a message'), findsOneWidget);
    });

    testWidgets('open but no number on the device yet: Message only',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.farmer, _farmerId);
      await tester.pumpWidget(
        buildScreen(openOrder(contactOpen: null, withPhones: false)),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Contact Buyer'));
      await tester.pumpAndSettle();

      expect(find.text('Phone number not available'), findsOneWidget);
      expect(find.text('Call'), findsNothing);
      expect(find.text('Send a message'), findsOneWidget);
    });

    testWidgets('a self-pickup order has no trucker to contact',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(buildScreen(openOrder(withTrucker: false)));
      await tester.pumpAndSettle();

      expect(find.text('Contact Farmer'), findsOneWidget);
      expect(find.text('Contact Trucker'), findsNothing);
    });

    for (final (status, note) in [
      (OrderStatus.created, escrowNote),
      (OrderStatus.pendingPayment, escrowNote),
      (OrderStatus.paymentSubmitted, escrowNote),
      (OrderStatus.paymentFailed, escrowNote),
      (OrderStatus.funded, acceptNote),
    ]) {
      testWidgets('a ${status.name} order shows the note, no contact',
          (tester) async {
        tallView(tester);
        stubAuth(UserRole.farmer, _farmerId);
        // Even numbers left on the device stay hidden before the accept.
        await tester.pumpWidget(
          buildScreen(openOrder(status: status, contactOpen: null)),
        );
        await tester.pumpAndSettle();

        expect(find.text(note), findsOneWidget);
        for (final label in allButtons) {
          expect(find.text(label), findsNothing, reason: label);
        }
      });
    }

    for (final status in [
      OrderStatus.cancelled,
      OrderStatus.rejected,
      OrderStatus.disputeResolved,
    ]) {
      testWidgets('a ${status.name} order shows no contact and no note',
          (tester) async {
        tallView(tester);
        stubAuth(UserRole.buyer, _buyerId);
        await tester.pumpWidget(
          buildScreen(openOrder(status: status, contactOpen: null)),
        );
        await tester.pumpAndSettle();

        for (final label in allButtons) {
          expect(find.text(label), findsNothing, reason: label);
        }
        expect(find.text(escrowNote), findsNothing);
        expect(find.text(acceptNote), findsNothing);
      });
    }

    testWidgets(
        'an open order without numbers on the device is read once from the '
        'server, then reloaded', (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      final order = openOrder(contactOpen: null, withPhones: false);
      final sync = MockMarketplaceSync();
      when(() => sync.pullOrder('order-001'))
          .thenAnswer((_) async => openOrder());
      final states = StreamController<OrderState>.broadcast();
      addTearDown(states.close);
      when(() => orderBloc.stream).thenAnswer((_) => states.stream);
      when(() => orderBloc.isClosed).thenReturn(false);

      await tester.pumpWidget(
        Provider<MarketplaceSync?>.value(
          value: sync,
          child: buildScreen(order),
        ),
      );
      states.add(SingleOrderLoaded(order: order));
      await tester.pumpAndSettle();
      states.add(SingleOrderLoaded(order: order));
      await tester.pumpAndSettle();

      verify(() => sync.pullOrder('order-001')).called(1);
      // initState's load, then the reload after the pull.
      verify(() => orderBloc.add(any(that: isA<LoadOrderById>()))).called(2);
    });

    testWidgets('an open order with its numbers is not read again',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      final sync = MockMarketplaceSync();
      final states = StreamController<OrderState>.broadcast();
      addTearDown(states.close);
      when(() => orderBloc.stream).thenAnswer((_) => states.stream);

      await tester.pumpWidget(
        Provider<MarketplaceSync?>.value(
          value: sync,
          child: buildScreen(openOrder()),
        ),
      );
      states.add(SingleOrderLoaded(order: openOrder()));
      await tester.pumpAndSettle();

      verifyNever(() => sync.pullOrder(any()));
    });

    testWidgets('an admin override refund closes an open status',
        (tester) async {
      tallView(tester);
      stubAuth(UserRole.buyer, _buyerId);
      await tester.pumpWidget(
        buildScreen(
          openOrder(status: OrderStatus.inTransit, contactOpen: false),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Contact Farmer'), findsNothing);
      expect(find.textContaining('911000002'), findsNothing);
    });
  });
}

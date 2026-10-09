import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
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
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

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
    ..truckerName = truckerName;
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

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeOrderState());
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
  });

  setUp(() {
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

  Widget buildScreen(Order order) {
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    return MultiBlocProvider(
      providers: [
        BlocProvider<OrderBloc>.value(value: orderBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: Locale('en'),
        home: OrderDetailScreen(orderId: 'order-001'),
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
}

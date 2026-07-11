import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

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

void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late StreamController<DeliveryState> deliveryStream;

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

      final button = find.widgetWithText(FilledButton, 'Create Delivery');
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

      final button = find.widgetWithText(FilledButton, 'Collect it myself');
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
}

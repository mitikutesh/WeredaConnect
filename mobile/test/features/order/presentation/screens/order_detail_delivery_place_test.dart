import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

final String _placesJson = jsonEncode([
  {
    'id': 'gn-344979',
    'en': 'Addis Ababa',
    'am': 'አዲስ አበባ',
    'region': 'Addis Ababa',
    'lat': 9.02497,
    'lng': 38.74689,
    'population': 2757729,
  },
  {
    'id': 'gn-adama',
    'en': 'Adama',
    'region': 'Oromia',
    'lat': 8.54,
    'lng': 39.27,
    'population': 220212,
  },
  {
    'id': 'gn-331180',
    'en': 'Mojo',
    'region': 'Oromia',
    'lat': 8.58712,
    'lng': 39.12131,
    'population': 39316,
  },
]);

/// A trip order the server runs commitments for, both parties agreed, with
/// its own drop-off point at Adama unless [point] is false.
Order _order(OrderStatus status, {bool point = true}) => Order()
  ..orderId = 'order-001'
  ..listingId = 'listing-001'
  ..buyerId = 'buyer-001'
  ..farmerId = 'farmer-001'
  ..buyerName = 'Buyer Bee'
  ..farmerName = 'Farmer Fikru'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 85
  ..totalPrice = 8500
  ..status = status
  ..createdAt = DateTime.utc(2026, 10, 26)
  ..version = 3
  ..tripId = 'trip-001'
  ..truckerId = 'trucker-001'
  ..truckerName = 'Trucker Tesfaye'
  ..fulfillment = 'trucker'
  ..farmerResponse = 'accepted'
  ..truckerResponse = 'accepted'
  ..contactOpen = true
  ..deliveryLatitude = point ? 8.54 : null
  ..deliveryLongitude = point ? 39.27 : null;

/// LT-14: the order detail shows the order's own drop-off place to every
/// party that received it, and lets the buyer pick another until pickup
/// (the same screen on mobile and web).
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockRatingBloc ratingBloc;
  late List<OrderEvent> added;
  late StreamController<OrderState> states;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeDeliveryEvent());
    // The bundled list, a few towns of it: the card names the point.
    PlaceRepository.instance.loadFromStringForTest(_placesJson);
  });

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    ratingBloc = MockRatingBloc();
    added = [];
    states = StreamController<OrderState>.broadcast();
    final ratingRepo = MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => orderBloc.stream).thenAnswer((_) => states.stream);
    when(() => orderBloc.add(any())).thenAnswer((invocation) {
      added.add(invocation.positionalArguments.first as OrderEvent);
    });
    when(() => orderBloc.isClosed).thenReturn(false);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    final deliveryRepo = MockDeliveryRepository();
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => null);
  });

  tearDown(() => states.close());

  Future<void> show(
    WidgetTester tester,
    Order order,
    UserRole role,
    String userId,
  ) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
    );
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
          BlocProvider<RatingBloc>.value(value: ratingBloc),
        ],
        child: const MaterialApp(
          locale: Locale('en'),
          localizationsDelegates: [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: OrderDetailScreen(orderId: 'order-001'),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final card = find.byKey(const Key('order_delivery_place'));
  final name = find.byKey(const Key('order_delivery_place_name'));
  final change = find.byKey(const Key('order_delivery_place_edit'));
  final sheetField = find.descendant(
    of: find.byKey(const Key('delivery_place_field')),
    matching: find.byType(TextFormField),
  );

  String nameText(WidgetTester tester) => tester.widget<Text>(name).data!;

  group('the buyer', () {
    testWidgets('picks another place before pickup', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );

      expect(card, findsOneWidget);
      expect(nameText(tester), 'Adama');
      expect(
        find.descendant(
          of: change,
          matching: find.text(en.deliveryPlaceChange),
        ),
        findsOneWidget,
      );

      await tester.ensureVisible(change);
      await tester.tap(change);
      await tester.pumpAndSettle();

      expect(
        tester.widget<TextFormField>(sheetField).controller!.text,
        'Adama',
      );
      // The same place again: nothing to save.
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('delivery_place_save')))
            .onPressed,
        isNull,
      );
      await tester.enterText(sheetField, 'Moj');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Mojo').last);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('delivery_place_save')));
      await tester.pumpAndSettle();

      final update = added.whereType<UpdateDeliveryLocation>().single;
      expect(update.orderId, 'order-001');
      expect(update.buyerId, 'buyer-001');
      expect(update.point, (latitude: 8.58712, longitude: 39.12131));
    });

    testWidgets('a typed name is not saved until picked', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );

      await tester.ensureVisible(change);
      await tester.tap(change);
      await tester.pumpAndSettle();
      await tester.enterText(sheetField, 'Kality');
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pumpAndSettle();

      expect(find.text(en.deliveryPlacePickFromList), findsOneWidget);
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('delivery_place_save')))
            .onPressed,
        isNull,
      );
      await tester.tap(find.text(en.cancel));
      await tester.pumpAndSettle();
      expect(added.whereType<UpdateDeliveryLocation>(), isEmpty);
    });

    testWidgets("an order without a place shows the truck's destination",
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, point: false),
        UserRole.buyer,
        'buyer-001',
      );

      expect(nameText(tester), en.deliveryPlaceNone);
      expect(change, findsOneWidget);
    });

    testWidgets('after pickup sees it, without Change', (tester) async {
      await show(
        tester,
        _order(OrderStatus.inTransit),
        UserRole.buyer,
        'buyer-001',
      );

      expect(nameText(tester), 'Adama');
      expect(change, findsNothing);
    });

    testWidgets('a saved change is said and the order read again',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );
      added.clear();

      states.add(const DeliveryLocationSaved());
      await tester.pumpAndSettle();

      expect(find.text(en.deliveryPlaceSaved), findsOneWidget);
      expect(
        added.whereType<LoadOrderById>().single.orderId,
        'order-001',
      );
    });
  });

  testWidgets('the farmer sees it, without Change', (tester) async {
    await show(
      tester,
      _order(OrderStatus.accepted),
      UserRole.farmer,
      'farmer-001',
    );

    expect(nameText(tester), 'Adama');
    expect(change, findsNothing);
  });

  testWidgets('the committed trucker sees it, without Change', (tester) async {
    await show(
      tester,
      _order(OrderStatus.accepted),
      UserRole.trucker,
      'trucker-001',
    );

    expect(nameText(tester), 'Adama');
    expect(change, findsNothing);
  });

  testWidgets('an asked trucker sees it before answering, without Change',
      (tester) async {
    final order = _order(OrderStatus.funded)..truckerResponse = 'pending';
    await show(tester, order, UserRole.trucker, 'trucker-001');

    expect(nameText(tester), 'Adama');
    expect(change, findsNothing);
  });

  testWidgets('a trucker the server sent no point to sees no card',
      (tester) async {
    await show(
      tester,
      _order(OrderStatus.funded, point: false),
      UserRole.trucker,
      'trucker-001',
    );

    expect(card, findsNothing);
  });
}

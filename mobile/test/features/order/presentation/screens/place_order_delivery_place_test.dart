import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

final String _placesJson = jsonEncode([
  {
    'id': 'gn-344979',
    'en': 'Addis Ababa',
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

/// LT-14: Place order with a truck picks the order's own drop-off place, from
/// the intent's place or the registered one, and places the order with its
/// point. The same screen on mobile and web (the place list is bundled).
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late List<CreateOrder> created;
  late PlaceRepository places;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    created = [];
    places = PlaceRepository()..loadFromStringForTest(_placesJson);
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is CreateOrder) created.add(event);
    });
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  void signedIn({String? woreda = 'Addis Ababa'}) =>
      when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: 'buyer-001',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: UserRole.buyer,
          userName: 'Meron Alemu',
          woreda: woreda,
        ),
      );

  final listing = CropListing()
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..cropType = 'teff'
    ..quantity = 10
    ..pricePerKg = 60
    ..locationName = 'Meki';

  Trip trip() => Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Abebe'
    ..fromWoreda = 'Meki'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = DateTime.now().toUtc().add(const Duration(days: 2))
    ..capacity = 200
    ..remainingQuintals = 120
    ..pricePerQuintal = 100
    ..status = TripStatus.open;

  Future<void> open(
    WidgetTester tester, {
    Trip? selectedTrip,
    BuyerIntent? intent,
  }) async {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: PlaceOrderScreen(
            listing: listing,
            farmerName: 'Kedir',
            selectedTrip: selectedTrip,
            intent: intent,
            placeRepository: places,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> placeOrder(WidgetTester tester) async {
    await tester
        .ensureVisible(find.widgetWithText(FilledButton, 'Place Order'));
    await tester.tap(find.widgetWithText(FilledButton, 'Place Order'));
    await tester.pumpAndSettle();
  }

  final field = find.descendant(
    of: find.byKey(const Key('delivery_place_field')),
    matching: find.byType(TextFormField),
  );

  String fieldText(WidgetTester tester) =>
      tester.widget<TextFormField>(field).controller!.text;

  testWidgets('starts at the registered place and places its point',
      (tester) async {
    signedIn();
    await open(tester, selectedTrip: trip());

    expect(find.text(en.deliveryPlaceLabel), findsOneWidget);
    expect(find.text(en.deliveryPlaceHelper), findsOneWidget);
    expect(fieldText(tester), 'Addis Ababa');
    await placeOrder(tester);

    expect(
      created.single.deliveryPoint,
      (latitude: 9.02497, longitude: 38.74689),
    );
  });

  testWidgets("an order for an intent starts at the intent's place",
      (tester) async {
    signedIn();
    final intent = BuyerIntent()
      ..intentId = 'intent-1'
      ..buyerId = 'buyer-001'
      ..cropType = 'teff'
      ..quantityNeeded = 5
      ..maxPricePerKg = 70
      ..deliveryWoreda = 'Mojo'
      ..placeId = 'gn-331180';
    await open(tester, selectedTrip: trip(), intent: intent);

    expect(fieldText(tester), 'Mojo');
    await placeOrder(tester);

    expect(
      created.single.deliveryPoint,
      (latitude: 8.58712, longitude: 39.12131),
    );
  });

  testWidgets('another place picked from the list is placed instead',
      (tester) async {
    signedIn();
    await open(tester, selectedTrip: trip());

    await tester.enterText(field, 'Ada');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Adama').last);
    await tester.pumpAndSettle();
    await placeOrder(tester);

    expect(created.single.deliveryPoint, (latitude: 8.54, longitude: 39.27));
  });

  testWidgets(
      'a typed name that is not picked asks for a pick and sends no '
      'point', (tester) async {
    signedIn();
    await open(tester, selectedTrip: trip());

    await tester.enterText(field, 'Kality');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();

    expect(find.text(en.deliveryPlacePickFromList), findsOneWidget);
    await placeOrder(tester);
    // The order still goes: the trip's destination is the drop-off.
    expect(created.single.deliveryPoint, isNull);
  });

  testWidgets('a registered place off the list starts empty', (tester) async {
    signedIn(woreda: 'Somewhere Else');
    await open(tester, selectedTrip: trip());

    expect(fieldText(tester), isEmpty);
    await placeOrder(tester);
    expect(created.single.deliveryPoint, isNull);
  });

  testWidgets('is not offered without a truck (self-pickup)', (tester) async {
    signedIn();
    await open(tester);

    expect(find.byKey(const Key('delivery_place_field')), findsNothing);
    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();
    expect(created.single.deliveryPoint, isNull);
  });
}

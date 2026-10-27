import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

/// The order quantity is kg; the listing stock is quintals. A buyer must be
/// able to order a whole listing (up to 10,000 quintals = 1,000,000 kg), and
/// asking for more than the stock is caught on the screen, offline, instead of
/// failing on the server with a generic "An error occurred".
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late List<CreateOrder> created;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    created = [];
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is CreateOrder) created.add(event);
    });
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: AuthTokens(
          accessToken: 'fake-access-token',
          refreshToken: 'fake-refresh-token',
          idToken: 'fake-id-token',
        ),
        role: UserRole.buyer,
        userName: 'Meron Alemu',
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  CropListing listing({required double quintals, double pricePerKg = 1000}) =>
      CropListing()
        ..listingId = 'listing-001'
        ..farmerId = 'farmer-001'
        ..cropType = 'lentils'
        ..quantity = quintals
        ..pricePerKg = pricePerKg
        ..locationName = 'Bahir Dar';

  Trip trip({required double pricePerQuintal}) => Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Abebe'
    ..fromWoreda = 'Adama'
    ..toWoreda = 'Bishoftu'
    ..departureDate = DateTime.utc(2026, 11, 2)
    ..capacity = 200
    ..remainingQuintals = 120
    ..pricePerQuintal = pricePerQuintal;

  Future<void> open(
    WidgetTester tester, {
    required double quintals,
    double pricePerKg = 1000,
    Trip? selectedTrip,
  }) async {
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
            listing: listing(quintals: quintals, pricePerKg: pricePerKg),
            farmerName: 'Kedir',
            selectedTrip: selectedTrip,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  FilledButton placeOrderButton(WidgetTester tester) =>
      tester.widget<FilledButton>(find.byType(FilledButton));

  testWidgets('a whole 10,000-quintal listing can be ordered', (tester) async {
    await open(tester, quintals: 10000);

    expect(find.text('1000000'), findsOneWidget);
    expect(find.textContaining('kg available'), findsNothing);

    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();

    expect(created, hasLength(1));
    expect(created.single.quantity, 1000000);
  });

  testWidgets(
      'more kg than the listing has shows an error and blocks the order',
      (tester) async {
    await open(tester, quintals: 10000);

    await tester.enterText(find.byType(TextField), '1000001');
    await tester.pump();

    expect(
      find.text('Only 1,000,000 kg available. Enter a smaller quantity.'),
      findsOneWidget,
    );
    expect(placeOrderButton(tester).onPressed, isNull);
    expect(created, isEmpty);
  });

  testWidgets('float noise from quintals to kg does not block a full order',
      (tester) async {
    // 12.34 * 100 is 1233.9999999999998 in floating point.
    await open(tester, quintals: 12.34);

    expect(find.text('1234'), findsOneWidget);
    expect(find.textContaining('kg available'), findsNothing);
    expect(placeOrderButton(tester).onPressed, isNotNull);
  });

  testWidgets('price breakdown matches the server total with a trip (SIM-53)',
      (tester) async {
    // 5 quintals (500 kg) at 34 ETB/kg on a 100 ETB/quintal trip. The server
    // charges 17000 + 500 + 5% of the crop (850) = 18350. Commission on crop
    // plus transport would show 18375, more than the buyer is asked to pay.
    await open(
      tester,
      quintals: 5,
      pricePerKg: 34,
      selectedTrip: trip(pricePerQuintal: 100),
    );

    expect(find.text('500'), findsOneWidget);
    expect(find.text('17000.00 ETB'), findsOneWidget);
    expect(find.text('500.00 ETB'), findsOneWidget);
    expect(find.text('850.00 ETB'), findsOneWidget);
    expect(find.text('18350.00 ETB'), findsOneWidget);
    expect(find.text('18375.00 ETB'), findsNothing);
    // ESC-15: the buyer sees which trucker the order will ask, and the room.
    expect(find.text('Trucker: Abebe'), findsOneWidget);
    expect(find.textContaining('120 quintals free'), findsOneWidget);

    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();

    expect(created, hasLength(1));
    expect(created.single.quantity, 500);
    expect(created.single.tripId, 'trip-001');
    expect(created.single.transportCost, 500);
    expect(created.single.commissionRate, 0.05);
  });
}

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
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

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

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

  CropListing listing({
    required double quintals,
    double pricePerKg = 1000,
    String locationName = 'Bahir Dar',
  }) =>
      CropListing()
        ..listingId = 'listing-001'
        ..farmerId = 'farmer-001'
        ..cropType = 'lentils'
        ..quantity = quintals
        ..pricePerKg = pricePerKg
        ..locationName = locationName;

  // An open truck leaving in two days: one that can take the order.
  Trip trip({
    required double pricePerQuintal,
    double remaining = 120,
    DateTime? departure,
    TripStatus status = TripStatus.open,
  }) =>
      Trip()
        ..tripId = 'trip-001'
        ..truckerId = 'trucker-001'
        ..truckerName = 'Abebe'
        ..fromWoreda = 'Adama'
        ..toWoreda = 'Bishoftu'
        ..departureDate =
            departure ?? DateTime.now().toUtc().add(const Duration(days: 2))
        ..capacity = 200
        ..remainingQuintals = remaining
        ..pricePerQuintal = pricePerQuintal
        ..status = status;

  Future<void> open(
    WidgetTester tester, {
    required double quintals,
    double pricePerKg = 1000,
    Trip? selectedTrip,
    String farmerName = 'Kedir',
    String locationName = 'Bahir Dar',
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
            listing: listing(
              quintals: quintals,
              pricePerKg: pricePerKg,
              locationName: locationName,
            ),
            farmerName: farmerName,
            selectedTrip: selectedTrip,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  FilledButton placeOrderButton(WidgetTester tester) =>
      tester.widget<FilledButton>(find.byType(FilledButton));

  // SIM-33: the listing line never shows an id for the farmer or a junk
  // place.
  testWidgets('the listing line leaves out an id name and a junk place',
      (tester) async {
    await open(tester, quintals: 5);
    expect(find.text('Kedir · Bahir Dar'), findsOneWidget);

    const id = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b';
    await open(tester, quintals: 5, farmerName: id, locationName: '105');
    expect(find.textContaining(id), findsNothing);
    expect(find.textContaining('105'), findsNothing);

    await open(tester, quintals: 5, farmerName: id);
    expect(find.text('Bahir Dar'), findsOneWidget);
  });

  testWidgets('a whole 10,000-quintal listing can be ordered', (tester) async {
    await open(tester, quintals: 10000);

    expect(find.text('1,000,000'), findsOneWidget);
    expect(find.textContaining('kg available'), findsNothing);

    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();

    expect(created, hasLength(1));
    expect(created.single.quantity, 1000000);
  });

  // Place order board: "= 15 quintals", no trailing ".0".
  testWidgets('the quintal line drops a whole number\'s decimal',
      (tester) async {
    await open(tester, quintals: 15);
    expect(find.textContaining('15 quintals'), findsOneWidget);
    expect(find.textContaining('15.0 quintals'), findsNothing);
  });

  // Place order board: the typed quantity reads "1,500" kg.
  testWidgets('the typed quantity is grouped and ordered as a number',
      (tester) async {
    await open(tester, quintals: 10000);

    await tester.enterText(find.byType(TextField), '1500');
    await tester.pump();
    expect(find.text('1,500'), findsOneWidget);

    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();
    expect(created.single.quantity, 1500);
  });

  // A listing with fractional stock (2.555 q = 255.5 kg): the field shows
  // the fraction and the stepper stops at the stock, never above it.
  testWidgets('fractional stock is never rounded above itself', (tester) async {
    await open(tester, quintals: 2.555);

    expect(find.text('255.5'), findsOneWidget);
    await tester.ensureVisible(find.byIcon(Icons.add));
    await tester.tap(find.byIcon(Icons.add));
    await tester.pump();
    expect(find.text('255.5'), findsOneWidget);
    expect(find.textContaining('kg available'), findsNothing);
    expect(placeOrderButton(tester).onPressed, isNotNull);
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

    expect(find.text('1,234'), findsOneWidget);
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
    expect(find.text('17,000 ETB'), findsOneWidget);
    expect(find.text('500 ETB'), findsOneWidget);
    expect(find.text('850 ETB'), findsOneWidget);
    expect(find.text('18,350 ETB'), findsOneWidget);
    expect(find.text('18,375 ETB'), findsNothing);
    // ESC-15: the buyer sees which trucker the order will ask, and the room.
    expect(find.textContaining('Abebe · '), findsOneWidget);
    expect(find.textContaining('120 quintals left'), findsOneWidget);

    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();

    expect(created, hasLength(1));
    expect(created.single.quantity, 500);
    expect(created.single.tripId, 'trip-001');
    expect(created.single.transportCost, 500);
    expect(created.single.commissionRate, 0.05);
  });

  // B-2: the chosen truck is checked again when the order is placed.
  group('the chosen truck at Place Order', () {
    /// Opens the screen from a page that records what it closed with.
    Future<List<Object?>> openFromDetail(
      WidgetTester tester,
      Trip truck,
    ) async {
      final closedWith = <Object?>[];
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
            // A Scaffold, as the listing detail is: the reason shows there.
            home: Scaffold(
              body: Builder(
                builder: (context) => TextButton(
                  onPressed: () async => closedWith.add(
                    await Navigator.of(context).push<bool>(
                      MaterialPageRoute<bool>(
                        builder: (_) => PlaceOrderScreen(
                          listing: listing(quintals: 30),
                          farmerName: 'Kedir',
                          selectedTrip: truck,
                        ),
                      ),
                    ),
                  ),
                  child: const Text('open'),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      return closedWith;
    }

    Future<void> placeOrder(WidgetTester tester) async {
      await tester
          .ensureVisible(find.widgetWithText(FilledButton, 'Place Order'));
      await tester.tap(find.widgetWithText(FilledButton, 'Place Order'));
      await tester.pumpAndSettle();
    }

    testWidgets('starts at no more than the truck has room for',
        (tester) async {
      await open(
        tester,
        quintals: 30,
        selectedTrip: trip(pricePerQuintal: 100, remaining: 12),
      );

      // 30 quintals listed, 12 free on the truck: 1,200 kg.
      expect(find.text('1,200'), findsOneWidget);
      expect(
        find.textContaining('fits this trip (12 quintals left)'),
        findsOneWidget,
      );
    });

    testWidgets('a truck whose day is over: told why, back to choosing',
        (tester) async {
      final closedWith = await openFromDetail(
        tester,
        trip(
          pricePerQuintal: 100,
          departure: DateTime.now().toUtc().subtract(const Duration(days: 2)),
        ),
      );

      await placeOrder(tester);

      expect(created, isEmpty);
      expect(
        find.text('This truck already left. Choose another truck.'),
        findsOneWidget,
      );
      expect(find.byType(PlaceOrderScreen), findsNothing);
      expect(closedWith, [true]);
    });

    testWidgets('a truck no longer open: told why, back to choosing',
        (tester) async {
      final closedWith = await openFromDetail(
        tester,
        trip(pricePerQuintal: 100, status: TripStatus.cancelled),
      );

      await placeOrder(tester);

      expect(created, isEmpty);
      expect(
        find.text('That truck is no longer available. Choose again.'),
        findsOneWidget,
      );
      expect(closedWith, [true]);
    });

    testWidgets('more than the truck has room for: told why, order less',
        (tester) async {
      final closedWith = await openFromDetail(
        tester,
        trip(pricePerQuintal: 100, remaining: 12),
      );
      // The quantity field (LT-12 adds the landmark field beside a trip).
      await tester.enterText(
        find.byKey(const Key('place_order_quantity')),
        '1300',
      );
      await tester.pump();

      await placeOrder(tester);

      expect(created, isEmpty);
      expect(
        find.text(
          'This truck has no room left for this order. Choose another truck.',
        ),
        findsOneWidget,
      );
      expect(find.byType(PlaceOrderScreen), findsOneWidget);
      expect(closedWith, isEmpty);

      // 12 quintals fit.
      await tester.enterText(
        find.byKey(const Key('place_order_quantity')),
        '1200',
      );
      await tester.pump();
      await placeOrder(tester);
      expect(created.single.quantity, 1200);
      expect(created.single.tripId, 'trip-001');
    });

    // LT-12: the drop-off landmark for the driver, on a trip order only.
    group('the drop-off landmark', () {
      final note = find.byKey(const Key('dropoff_note_field'));

      testWidgets('is offered with a truck, and placed with the order',
          (tester) async {
        await open(
          tester,
          quintals: 10,
          selectedTrip: trip(pricePerQuintal: 100),
        );

        expect(find.text('Drop-off landmark (optional)'), findsOneWidget);
        expect(find.text('e.g. Merkato, near Anwar Mosque'), findsOneWidget);
        await tester.enterText(note, 'Merkato, near Anwar Mosque');
        await tester.pump();
        await placeOrder(tester);

        expect(created.single.dropoffNote, 'Merkato, near Anwar Mosque');
      });

      testWidgets('left empty, the order goes without it', (tester) async {
        await open(
          tester,
          quintals: 10,
          selectedTrip: trip(pricePerQuintal: 100),
        );

        await placeOrder(tester);

        expect(created.single.dropoffNote, '');
      });

      testWidgets('a phone number in it is refused before anything is queued',
          (tester) async {
        await open(
          tester,
          quintals: 10,
          selectedTrip: trip(pricePerQuintal: 100),
        );

        await tester.enterText(note, 'Merkato, call 0911 234 567');
        await tester.pump();

        expect(
          find.text(
            'Phone numbers are not allowed here. Remove the number: contact '
            'opens after payment is held in escrow.',
          ),
          findsOneWidget,
        );
        expect(placeOrderButton(tester).onPressed, isNull);
        await placeOrder(tester);
        expect(created, isEmpty);
      });

      testWidgets('stops at 80 characters, Amharic counted per syllable',
          (tester) async {
        await open(
          tester,
          quintals: 10,
          selectedTrip: trip(pricePerQuintal: 100),
        );

        await tester.enterText(note, 'ሰ' * 90);
        await tester.pump();

        final field = tester.widget<TextField>(note);
        expect(field.controller!.text, 'ሰ' * 80);
        expect(find.text('80/80'), findsOneWidget);
      });

      testWidgets('is not offered without a truck (self-pickup)',
          (tester) async {
        await open(tester, quintals: 10);

        expect(note, findsNothing);
        await tester.ensureVisible(find.byType(FilledButton));
        await tester.tap(find.byType(FilledButton));
        await tester.pump();
        expect(created.single.dropoffNote, isNull);
      });
    });
  });

  // E-5: placing an order says whether it went out or waits on the phone,
  // from the connectivity when it was saved (never waiting on the network).
  for (final (online, message) in [
    (true, 'Order placed successfully!'),
    (false, 'Saved on your phone. It will be sent when you have network.'),
  ]) {
    testWidgets('placed ${online ? 'online' : 'offline'}: "$message"',
        (tester) async {
      final monitor = MockConnectivityMonitor();
      when(() => monitor.isConnected()).thenAnswer((_) async => online);
      final states = StreamController<OrderState>.broadcast();
      addTearDown(states.close);
      when(() => orderBloc.stream).thenAnswer((_) => states.stream);
      await tester.pumpWidget(
        Provider<ConnectivityMonitor>.value(
          value: monitor,
          child: MultiBlocProvider(
            providers: [
              BlocProvider<OrderBloc>.value(value: orderBloc),
              BlocProvider<AuthBloc>.value(value: authBloc),
            ],
            child: MaterialApp(
              localizationsDelegates: AppLocalizations.localizationsDelegates,
              supportedLocales: AppLocalizations.supportedLocales,
              locale: const Locale('en'),
              home: PlaceOrderScreen(
                listing: listing(quintals: 10),
                farmerName: 'Kedir',
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final order = Order()
        ..orderId = 'order-1'
        ..listingId = 'listing-001'
        ..buyerId = 'buyer-001'
        ..farmerId = 'farmer-001'
        ..buyerName = 'Meron Alemu'
        ..farmerName = 'Kedir'
        ..cropType = 'lentils'
        ..quantity = 1000
        ..pricePerUnit = 1000
        ..totalPrice = 1000000
        ..status = OrderStatus.created
        ..createdAt = DateTime.utc(2026, 11, 4)
        ..version = 1;
      states.add(OrderCreated(order: order));
      await tester.pumpAndSettle();

      expect(find.text(message), findsOneWidget);
    });
  }

  // A second tap while the snackbar reads the connectivity never creates a
  // second order.
  testWidgets('Place Order stays disabled once the order is created',
      (tester) async {
    final monitor = MockConnectivityMonitor();
    final connectivity = Completer<bool>();
    when(() => monitor.isConnected()).thenAnswer((_) => connectivity.future);
    final states = StreamController<OrderState>.broadcast();
    addTearDown(states.close);
    when(() => orderBloc.stream).thenAnswer((_) => states.stream);
    await tester.pumpWidget(
      Provider<ConnectivityMonitor>.value(
        value: monitor,
        child: MultiBlocProvider(
          providers: [
            BlocProvider<OrderBloc>.value(value: orderBloc),
            BlocProvider<AuthBloc>.value(value: authBloc),
          ],
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: PlaceOrderScreen(
              listing: listing(quintals: 10),
              farmerName: 'Kedir',
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton));
    await tester.pump();
    expect(created, hasLength(1));

    final order = Order()
      ..orderId = 'order-1'
      ..listingId = 'listing-001'
      ..buyerId = 'buyer-001'
      ..farmerId = 'farmer-001'
      ..buyerName = 'Meron Alemu'
      ..farmerName = 'Kedir'
      ..cropType = 'lentils'
      ..quantity = 1000
      ..pricePerUnit = 1000
      ..totalPrice = 1000000
      ..status = OrderStatus.created
      ..createdAt = DateTime.utc(2026, 11, 4)
      ..version = 1;
    // OrderCreated while the connectivity read has not answered yet.
    states.add(OrderCreated(order: order));
    await tester.pump();
    await tester.pump();

    expect(placeOrderButton(tester).onPressed, isNull);
    await tester.tap(find.byType(FilledButton), warnIfMissed: false);
    await tester.pump();
    expect(created, hasLength(1));

    connectivity.complete(true);
    await tester.pumpAndSettle();
  });
}

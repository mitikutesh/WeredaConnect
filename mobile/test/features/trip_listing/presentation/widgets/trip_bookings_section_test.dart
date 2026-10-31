import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_bookings_section.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_summary_tile.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

Trip _trip() => Trip()
  ..tripId = 'trip-1'
  ..truckerId = 'trucker-1'
  ..truckerName = 'Dawit'
  ..truckerPhone = ''
  ..fromWoreda = 'Adama'
  ..toWoreda = 'Bishoftu'
  ..departureDate = DateTime.utc(2026, 11, 2)
  ..capacity = 200
  ..remainingQuintals = 120
  ..pricePerQuintal = 100
  ..status = TripStatus.open
  ..matchedListings = []
  ..createdAt = DateTime.utc(2026, 10)
  ..updatedAt = DateTime.utc(2026, 10)
  ..version = 1;

Order _order(
  String id,
  OrderStatus status, {
  String tripId = 'trip-1',
  String farmer = 'accepted',
  String trucker = 'pending',
  String farmerName = 'Chaltu',
}) =>
    Order()
      ..orderId = id
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..buyerName = 'Buyer'
      ..farmerName = farmerName
      ..cropType = 'teff'
      ..quantity = 500
      ..pricePerUnit = 45
      ..totalPrice = 22500
      ..status = status
      ..createdAt = DateTime.utc(2026, 10, 20)
      ..version = 1
      ..tripId = tripId
      ..truckerId = 'trucker-1'
      ..fulfillment = 'trucker'
      ..farmerResponse = farmer
      ..truckerResponse = trucker;

Widget _app(Widget child, {OrderBloc? bloc}) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: bloc == null
            ? child
            : BlocProvider<OrderBloc>.value(value: bloc, child: child),
      ),
    );

/// ESC-15: the trucker sees the paid orders on the trip and which one waits
/// for an answer; a buyer picks among several trucks.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  testWidgets('lists the trip\'s paid orders and flags the one to answer',
      (tester) async {
    final bloc = MockOrderBloc();
    final repo = MockOrderRepository();
    when(() => bloc.repository).thenReturn(repo);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => repo.getOrdersByUser('trucker-1', role: 'trucker')).thenAnswer(
      (_) async => [
        _order('asked', OrderStatus.funded, farmerName: 'Asked Farmer'),
        _order(
          'agreed',
          OrderStatus.disputed,
          trucker: 'accepted',
          farmerName: 'Disputed Farmer',
        ),
        _order(
          'other-trip',
          OrderStatus.funded,
          tripId: 'trip-2',
          farmerName: 'Other Trip',
        ),
        _order('ended', OrderStatus.cancelled, farmerName: 'Ended Farmer'),
      ],
    );

    await tester.pumpWidget(
      _app(TripBookingsSection(trip: _trip()), bloc: bloc),
    );
    await tester.pumpAndSettle();

    expect(find.text(en.tripBookings), findsOneWidget);
    expect(find.textContaining('Asked Farmer'), findsOneWidget);
    expect(find.textContaining('Disputed Farmer'), findsOneWidget);
    expect(find.textContaining('Other Trip'), findsNothing);
    expect(find.textContaining('Ended Farmer'), findsNothing);
    expect(find.text(en.actionAnswerTransport), findsOneWidget);
  });

  // Trip detail board: "Almaz Bekele, Meki → Selam Traders, Merkato", the
  // order's own places (order-lifecycle 2.2.0), else the trip's route.
  testWidgets('a booking names its own places, else the trip\'s route',
      (tester) async {
    final bloc = MockOrderBloc();
    final repo = MockOrderRepository();
    when(() => bloc.repository).thenReturn(repo);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => repo.getOrdersByUser('trucker-1', role: 'trucker')).thenAnswer(
      (_) async => [
        _order('placed', OrderStatus.funded, farmerName: 'Almaz')
          ..pickupPlace = 'Meki'
          ..dropoffPlace = 'Merkato',
        _order(
          'older',
          OrderStatus.accepted,
          trucker: 'accepted',
        ),
      ],
    );

    await tester.pumpWidget(
      _app(TripBookingsSection(trip: _trip()), bloc: bloc),
    );
    await tester.pumpAndSettle();

    expect(find.text('Almaz, Meki → Buyer, Merkato'), findsOneWidget);
    expect(find.text('Chaltu, Adama → Buyer, Bishoftu'), findsOneWidget);
  });

  // A failed read is not "no bookings": a request may wait among them.
  testWidgets('a failed read says so and retries', (tester) async {
    final bloc = MockOrderBloc();
    final repo = MockOrderRepository();
    when(() => bloc.repository).thenReturn(repo);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    var calls = 0;
    when(() => repo.getOrdersByUser('trucker-1', role: 'trucker'))
        .thenAnswer((_) async {
      calls++;
      if (calls == 1) throw Exception('offline');
      return [_order('asked', OrderStatus.funded, farmerName: 'Asked')];
    });

    await tester.pumpWidget(
      _app(TripBookingsSection(trip: _trip()), bloc: bloc),
    );
    await tester.pumpAndSettle();
    expect(find.text(en.tripBookingsLoadFailed), findsOneWidget);
    expect(find.text(en.tripNoBookings), findsNothing);

    await tester.tap(find.byKey(const Key('trip_bookings_retry')));
    await tester.pumpAndSettle();
    expect(find.textContaining('Asked'), findsOneWidget);
  });

  testWidgets('one answer at a time: the other request waits', (tester) async {
    final bloc = MockOrderBloc();
    final repo = MockOrderRepository();
    when(() => bloc.repository).thenReturn(repo);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => repo.getOrdersByUser('trucker-1', role: 'trucker')).thenAnswer(
      (_) async => [
        _order('a', OrderStatus.funded, farmerName: 'First'),
        _order('b', OrderStatus.funded, farmerName: 'Second'),
      ],
    );
    final pending = Completer<void>();
    when(
      () => repo.respondAsTrucker(
        any(),
        any(),
        accept: any(named: 'accept'),
        declineReason: any(named: 'declineReason'),
      ),
    ).thenAnswer((_) => pending.future);

    await tester.pumpWidget(
      _app(TripBookingsSection(trip: _trip()), bloc: bloc),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('booking_accept_a')));
    await tester.pump();

    final other = tester.widget<FilledButton>(
      find.byKey(const Key('booking_accept_b')),
    );
    expect(other.onPressed, isNull);
    pending.complete();
    await tester.pumpAndSettle();
  });

  testWidgets('says when nothing is booked', (tester) async {
    final bloc = MockOrderBloc();
    final repo = MockOrderRepository();
    when(() => bloc.repository).thenReturn(repo);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => repo.getOrdersByUser(any(), role: any(named: 'role')))
        .thenAnswer((_) async => []);

    await tester.pumpWidget(
      _app(TripBookingsSection(trip: _trip()), bloc: bloc),
    );
    await tester.pumpAndSettle();

    expect(find.text(en.tripNoBookings), findsOneWidget);
  });

  group('showChooseTripSheet', () {
    Future<Trip?> Function() open(WidgetTester tester, List<Trip> trips) {
      Trip? chosen;
      var done = false;
      return () async {
        await tester.pumpWidget(
          _app(
            Builder(
              builder: (context) => TextButton(
                onPressed: () async {
                  chosen = await showChooseTripSheet(context, trips);
                  done = true;
                },
                child: const Text('open'),
              ),
            ),
          ),
        );
        await tester.tap(find.text('open'));
        await tester.pumpAndSettle();
        expect(find.text(en.chooseTrip), findsOneWidget);
        expect(find.text('Trucker: Dawit'), findsOneWidget);
        expect(find.text('Trucker: Eyob'), findsOneWidget);
        expect(find.textContaining('120 quintals free'), findsOneWidget);
        expect(done, isFalse);
        return chosen;
      };
    }

    final second = _trip()
      ..tripId = 'trip-2'
      ..truckerName = 'Eyob'
      ..remainingQuintals = null;

    testWidgets('returns the tapped truck', (tester) async {
      Trip? chosen;
      await tester.pumpWidget(
        _app(
          Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                chosen = await showChooseTripSheet(context, [_trip(), second]);
              },
              child: const Text('open'),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      expect(find.text(en.chooseTrip), findsOneWidget);
      expect(find.textContaining('120 quintals free'), findsOneWidget);
      // A trip nobody booked has its whole capacity free.
      expect(find.textContaining('200 quintals free'), findsOneWidget);

      await tester.tap(find.text('Trucker: Eyob'));
      await tester.pumpAndSettle();

      expect(chosen?.tripId, 'trip-2');
    });

    testWidgets('dismissing it chooses nothing', (tester) async {
      final run = open(tester, [_trip(), second]);
      await run();
      await tester.tapAt(const Offset(10, 10));
      await tester.pumpAndSettle();
      expect(find.text(en.chooseTrip), findsNothing);
    });
  });
}

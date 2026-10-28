import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/live_trip/data/trip_plan_loader.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

import '../check_in_fakes.dart';

/// Shared fixtures for the LT-09 trucker screens: a real check-in engine on
/// the LT-08 fakes, the trip's deliveries and orders, and the app's
/// localizations.

const trucker = 'trucker-1';
const tripId = 'trip-1';

/// 06:00 UTC is 09:00 in Ethiopia: 3:00 in the morning on the Ethiopian
/// clock.
final startTime = DateTime.utc(2026, 10, 28, 6);

class MockTripRepository extends Mock implements BaseTripListingRepository {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

/// The engine and its fakes, signed in as [trucker].
class EngineRig {
  EngineRig._(
    this.clock,
    this.timers,
    this.location,
    this.trips,
    this.pings,
    this.states,
    this.engine,
  );

  static Future<EngineRig> signedIn({
    String? userId = trucker,
    bool background = true,
  }) async {
    final clock = FakeClock(startTime);
    final timers = FakeTimers();
    final location = FakeLocation(clock, supportsBackground: background);
    final trips = FakeTrips()
      ..set(tripId, [
        leg('d-1', DeliveryStatus.pickedUp),
        leg('d-2', DeliveryStatus.pending),
      ]);
    final pings = FakePings();
    final states = MemorySharingStateStore();
    final engine = CheckInEngine(
      pings: pings,
      location: location,
      trips: trips,
      states: states,
      notice: () => (title: 'T', text: 'X', channel: 'C'),
      now: () => clock.now,
      timer: timers.call,
    );
    if (userId != null) await engine.signIn(userId);
    return EngineRig._(clock, timers, location, trips, pings, states, engine);
  }

  final FakeClock clock;
  final FakeTimers timers;
  final FakeLocation location;
  final FakeTrips trips;
  final FakePings pings;
  final MemorySharingStateStore states;
  final CheckInEngine engine;
}

class FakeConnectivity implements ConnectivitySource {
  FakeConnectivity({bool online = true})
      : _current = [
          online ? ConnectivityResult.mobile : ConnectivityResult.none,
        ];

  List<ConnectivityResult> _current;
  final _changes = StreamController<List<ConnectivityResult>>.broadcast();

  void set({required bool online}) {
    _current = [online ? ConnectivityResult.mobile : ConnectivityResult.none];
    _changes.add(_current);
  }

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async => _current;

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged => _changes.stream;
}

Trip tripFixture() => Trip()
  ..tripId = tripId
  ..truckerId = trucker
  ..truckerName = 'Dawit Tesfaye'
  ..truckerPhone = '+251900100002'
  ..fromWoreda = 'Meki'
  ..toWoreda = 'Addis Ababa'
  ..departureDate = DateTime.utc(2026, 10, 27, 21)
  ..capacity = 50
  ..pricePerQuintal = 150
  ..status = TripStatus.matched
  ..matchedListings = []
  ..createdAt = DateTime.utc(2026, 10, 20)
  ..updatedAt = DateTime.utc(2026, 10, 20)
  ..version = 1;

Delivery deliveryFixture(
  String id, {
  required DeliveryStatus status,
  required String farmer,
  required String buyer,
  DateTime? pickedUpAt,
  DateTime? confirmedAt,
  DateTime? createdAt,
  String truckerId = trucker,
}) =>
    Delivery()
      ..deliveryId = id
      ..tripId = tripId
      ..orderId = 'o-$id'
      ..listingId = 'l-$id'
      ..farmerId = 'farmer-$id'
      ..farmerName = farmer
      ..truckerId = truckerId
      ..truckerName = 'Dawit Tesfaye'
      ..buyerId = 'buyer-$id'
      ..buyerName = buyer
      ..qrToken = ''
      ..status = status
      ..pickedUpAt = pickedUpAt
      ..confirmedAt = confirmedAt
      ..createdAt = createdAt ?? DateTime.utc(2026, 10, 25)
      ..updatedAt = DateTime.utc(2026, 10, 25)
      ..version = 1;

Order orderFixture(
  String deliveryId, {
  required String crop,
  required double kg,
  double? transportCost,
}) =>
    Order()
      ..orderId = 'o-$deliveryId'
      ..listingId = 'l-$deliveryId'
      ..buyerId = 'buyer-$deliveryId'
      ..farmerId = 'farmer-$deliveryId'
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = crop
      ..quantity = kg
      ..pricePerUnit = 40
      ..totalPrice = kg * 40
      ..status = OrderStatus.inTransit
      ..createdAt = DateTime.utc(2026, 10, 24)
      ..version = 1
      ..tripId = tripId
      ..truckerId = trucker
      ..transportCost = transportCost;

/// A loader over mock repositories that answer [deliveries] and [orders]
/// for [tripId].
TripPlanLoader loaderFor({
  required List<Delivery> deliveries,
  List<Order> orders = const [],
  Trip? trip,
  bool failDeliveries = false,
}) {
  final trips = MockTripRepository();
  final deliveryRepo = MockDeliveryRepository();
  final orderRepo = MockOrderRepository();
  when(() => trips.getTripById(tripId))
      .thenAnswer((_) async => trip ?? tripFixture());
  if (failDeliveries) {
    when(() => deliveryRepo.getDeliveriesByTrip(tripId, userId: trucker))
        .thenThrow(Exception('offline'));
  } else {
    when(() => deliveryRepo.getDeliveriesByTrip(tripId, userId: trucker))
        .thenAnswer((_) async => deliveries);
  }
  when(() => orderRepo.getOrderById(any())).thenAnswer((inv) async {
    final id = inv.positionalArguments.first as String;
    return orders.where((o) => o.orderId == id).firstOrNull;
  });
  return TripPlanLoader(
    trips: trips,
    deliveries: deliveryRepo,
    orders: orderRepo,
  );
}

/// The app's strings in [code].
AppLocalizations lookup(String code) => lookupAppLocalizations(Locale(code));

/// [home] in a MaterialApp with the app's localizations, phone sized.
/// [above] wraps the whole app (the providers the app puts above it, so a
/// pushed route finds them too).
Future<AppLocalizations> pumpApp(
  WidgetTester tester,
  Widget home, {
  String locale = 'en',
  Widget Function(Widget app)? above,
}) async {
  tester.view.physicalSize = const Size(420, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final app = MaterialApp(
    locale: Locale(locale),
    localizationsDelegates: const [
      AppLocalizations.delegate,
      FallbackMaterialLocalizationsDelegate(),
      GlobalWidgetsLocalizations.delegate,
      FallbackCupertinoLocalizationsDelegate(),
    ],
    supportedLocales: AppLocalizations.supportedLocales,
    home: home,
  );
  await tester.pumpWidget(above == null ? app : above(app));
  await tester.pump();
  return lookupAppLocalizations(Locale(locale));
}

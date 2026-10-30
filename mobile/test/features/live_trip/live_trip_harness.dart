import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:provider/single_child_widget.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/core/database/schemas/app_notification.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

import 'live_trip_fixtures.dart';

// Shared widget-test harness of the LT-11 live trip screens: a scripted
// track repository, the order's three parties and a delivery and order on
// the Addis Ababa to Adama corridor of live_trip_fixtures.dart.

const buyerId = 'buyer-001';
const farmerId = 'farmer-001';
const truckerId = 'trucker-001';

/// A track repository that answers [result] (or the stored snapshot with
/// [TrackFailure.offline] when [offline]) and counts the reads.
class FakeTrackRepository extends BaseTrackRepository {
  FakeTrackRepository({this.track, this.stored, this.offline = false});

  DeliveryTrack? track;
  TrackSnapshot? stored;
  bool offline;
  int fetches = 0;
  final readers = <String>[];

  @override
  Future<TrackResult> fetchTrack(
    String deliveryId, {
    required String userId,
  }) async {
    fetches++;
    readers.add(userId);
    final t = track;
    if (offline || t == null) {
      return TrackResult(snapshot: stored, failure: TrackFailure.offline);
    }
    return TrackResult(
      snapshot: TrackSnapshot(track: t, fetchedAt: DateTime.now().toUtc()),
      fromNetwork: true,
    );
  }

  @override
  Future<TrackSnapshot?> lastSnapshot(
    String deliveryId, {
    required String userId,
  }) async =>
      stored;
}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockNotificationRepository extends Mock
    implements BaseNotificationRepository {}

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockEscrowRepository extends Mock implements BaseEscrowRepository {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripListingRepository extends Mock
    implements BaseTripListingRepository {}

/// The order's escrow, held since the buyer paid.
Payment heldEscrow() => Payment()
  ..paymentId = 'escrow-order-order-001'
  ..deliveryId = 'delivery-101'
  ..orderId = 'order-001'
  ..farmerId = farmerId
  ..buyerId = buyerId
  ..amount = 91200
  ..commission = 4200
  ..commissionRate = 0.05
  ..farmerPayout = 84000
  ..status = PaymentStatus.escrowed
  ..createdAt = DateTime.utc(2026, 10, 26, 8, 20)
  ..updatedAt = DateTime.utc(2026, 10, 26, 8, 21)
  ..version = 1;

/// A trucked delivery of order-001 on the corridor.
Delivery liveDelivery({
  DeliveryStatus status = DeliveryStatus.inTransit,
  String id = 'delivery-101',
  String farmer = farmerId,
  DateTime? confirmedAt,
}) =>
    Delivery()
      ..deliveryId = id
      ..tripId = 'trip-001'
      ..orderId = 'order-001'
      ..listingId = 'listing-001'
      ..farmerId = farmer
      ..farmerName = 'Almaz Bekele'
      ..truckerId = truckerId
      ..truckerName = 'Dawit Tesfaye'
      ..buyerId = buyerId
      ..buyerName = 'Selam Traders'
      ..qrToken = 'qr'
      ..status = status
      ..pickedUpAt = status == DeliveryStatus.pending ? null : at(5, 5)
      ..confirmedAt = confirmedAt
      ..createdAt = DateTime.utc(2026, 10, 26)
      ..updatedAt = DateTime.utc(2026, 10, 27)
      ..version = 1;

/// The order of [liveDelivery]: 2,000 kg of wheat, paid, in transit.
Order liveOrder({OrderStatus status = OrderStatus.inTransit}) => Order()
  ..orderId = 'order-001'
  ..listingId = 'listing-001'
  ..buyerId = buyerId
  ..farmerId = farmerId
  ..buyerName = 'Selam Traders'
  ..farmerName = 'Almaz Bekele'
  ..cropType = 'wheat'
  ..quantity = 2000
  ..pricePerUnit = 42
  ..totalPrice = 84000
  ..cropCost = 84000
  ..transportCost = 3000
  ..commissionAmount = 4200
  ..orderTotal = 91200
  ..status = status
  ..createdAt = DateTime.utc(2026, 10, 26)
  ..paymentVerifiedAt = DateTime.utc(2026, 10, 26, 8, 20)
  ..version = 1
  ..tripId = 'trip-001'
  ..truckerId = truckerId
  ..truckerName = 'Dawit Tesfaye';

/// A `truck_nearby` inbox entry of [deliveryId].
AppNotification truckNearbyEntry(String deliveryId) => AppNotification()
  ..notificationId = 'n1'
  ..userId = buyerId
  ..type = 'truck_nearby'
  ..entity = 'delivery'
  ..entityId = deliveryId
  ..orderId = 'order-001'
  ..params = '{}'
  ..createdAt = at(8, 40);

/// The blocs and repositories the live trip screens read, mocked.
class LiveTripDeps {
  LiveTripDeps({this.order, this.escrow, List<AppNotification>? inbox})
      : inbox = inbox ?? [] {
    when(() => escrowBloc.repository).thenReturn(escrowRepository);
    when(() => escrowBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => escrowRepository.refreshEscrow(any()))
        .thenAnswer((_) async => escrow);
    when(() => escrowRepository.getEscrowByDelivery(any()))
        .thenAnswer((_) async => null);
    when(() => orderBloc.repository).thenReturn(orderRepository);
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderRepository.getOrderById(any()))
        .thenAnswer((_) async => order);
    when(() => deliveryBloc.repository).thenReturn(deliveryRepository);
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => deliveryRepository.getDeliveryById(
        any(),
        userId: any(named: 'userId'),
      ),
    ).thenAnswer((_) async => null);
    when(() => ratingBloc.repository).thenReturn(ratingRepository);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepository.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => notifications.recent(any(), limit: any(named: 'limit')))
        .thenAnswer((_) async => this.inbox);
    // The tab headers' bell reads the same inbox.
    when(() => notifications.changes).thenAnswer((_) => const Stream.empty());
    when(() => notifications.pull(any(), force: any(named: 'force')))
        .thenAnswer((_) async {});
    when(() => notifications.unreadCount(any())).thenAnswer((_) async => 0);
    when(() => tripBloc.repository).thenReturn(tripRepository);
    when(() => tripBloc.state).thenReturn(const TripListingInitial());
    when(() => tripBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripRepository.getTripById(any())).thenAnswer((_) async => trip);
  }

  /// The delivery's trip, for the buyer's driver card; null by default.
  Trip? trip;
  final tripBloc = MockTripListingBloc();
  final tripRepository = MockTripListingRepository();

  Order? order;
  Payment? escrow;
  List<AppNotification> inbox;
  final escrowBloc = MockEscrowBloc();
  final escrowRepository = MockEscrowRepository();
  final orderBloc = MockOrderBloc();
  final orderRepository = MockOrderRepository();
  final deliveryBloc = MockDeliveryBloc();
  final deliveryRepository = MockDeliveryRepository();
  final ratingBloc = MockRatingBloc();
  final ratingRepository = MockRatingRepository();
  final notifications = MockNotificationRepository();

  List<SingleChildWidget> providers(BaseTrackRepository tracks) => [
        Provider<BaseTrackRepository>.value(value: tracks),
        Provider<BaseNotificationRepository>.value(value: notifications),
        BlocProvider<OrderBloc>.value(value: orderBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
        BlocProvider<EscrowBloc>.value(value: escrowBloc),
        BlocProvider<TripListingBloc>.value(value: tripBloc),
      ];
}

/// [home] inside the app's localizations and [deps]' providers.
Widget liveTripApp({
  required Widget home,
  required LiveTripDeps deps,
  required BaseTrackRepository tracks,
  Locale locale = const Locale('en'),
  List<NavigatorObserver> observers = const [],
}) =>
    MultiProvider(
      providers: deps.providers(tracks),
      child: MaterialApp(
        // The app's delegates: Material and Cupertino fall back for om.
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        navigatorObservers: observers,
        home: home,
      ),
    );

/// Lets the poller's first reads, the order read and the inbox read land.
Future<void> settleReads(WidgetTester tester) async {
  for (var i = 0; i < 5; i++) {
    await tester.pump();
  }
}

/// The camera plugin is not present in widget tests.
void mockScannerChannels() {
  for (final name in [
    'dev.steenbakker.mobile_scanner/scanner/method',
    'dev.steenbakker.mobile_scanner/scanner/event',
  ]) {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(MethodChannel(name), (_) async => null);
  }
}

/// The corridor track with an arrival window of 10:20 to 11:00 UTC
/// (13:20 to 14:00 in Ethiopia) and 47 km left.
DeliveryTrack withEta(DeliveryTrack track) => DeliveryTrack(
      deliveryId: track.deliveryId,
      sharing: track.sharing,
      asOf: track.asOf,
      pausedUntil: track.pausedUntil,
      lastFix: track.lastFix,
      trail: track.trail,
      pauses: track.pauses,
      pickup: track.pickup,
      pickedUpAt: track.pickedUpAt,
      dropoff: track.dropoff,
      dropoffNote: track.dropoffNote,
      otherStops: track.otherStops,
      eta: TrackEta(from: at(10, 20), to: at(11, 0)),
      remainingKm: 47.2,
    );

/// A check-in 3 km from the drop-off at 09:07.
DeliveryTrack arrivingTrack() => DeliveryTrack(
      deliveryId: 'del-101',
      sharing: TrackSharing.live,
      asOf: asOf,
      lastFix: fix(8.57, 39.25, at(9, 7)),
      trail: [...corridorTrail, fix(8.57, 39.25, at(9, 7))],
      pickup: const GeoPoint(8.88, 38.79),
      pickedUpAt: at(5, 5),
      dropoff: const GeoPoint(8.55, 39.27),
      eta: TrackEta(from: at(9, 20), to: at(9, 35)),
      remainingKm: 3.9,
    );

/// The corridor track moved so its last check-in was [age] before the real
/// clock, with an arrival window an hour on: for screens that read the
/// device clock themselves (the deliveries tab).
DeliveryTrack recentTrack({Duration age = const Duration(minutes: 28)}) {
  final now = DateTime.now().toUtc();
  final base = DateTime.utc(now.year, now.month, now.day, now.hour, now.minute);
  final shift = base.subtract(age).difference(corridorTrail.last.capturedAt);
  final trail = [
    for (final f in corridorTrail)
      fix(
        f.point.latitude,
        f.point.longitude,
        f.capturedAt.add(shift),
        source: f.source ?? 'hourly',
      ),
  ];
  return DeliveryTrack(
    deliveryId: 'delivery-101',
    sharing: TrackSharing.live,
    asOf: base,
    lastFix: trail.last,
    trail: trail,
    pickup: const GeoPoint(8.88, 38.79),
    pickedUpAt: trail.first.capturedAt,
    dropoff: const GeoPoint(8.55, 39.27),
    eta: TrackEta(
      from: base.add(const Duration(minutes: 60)),
      to: base.add(const Duration(minutes: 100)),
    ),
    remainingKm: 47.2,
  );
}

/// The device clock of the tests: the corridor fixtures' 09:10 UTC.
DateTime fixedNow() => asOf;

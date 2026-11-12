import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/pickup_check_in_card.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

/// A GPS that answers what the test says.
class FakeGps extends GpsCaptureService {
  FakeGps(this.result);

  GpsCaptureResult result;
  int calls = 0;

  @override
  Future<GpsCaptureResult> captureCurrentPosition() async {
    calls++;
    return result;
  }

  int settingsOpened = 0;

  @override
  Future<bool> openAppSettings() async {
    settingsOpened++;
    return true;
  }
}

class MockSyncExecutor extends Mock implements SyncExecutor {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

const _atFarm = GpsCaptureResult.success(
  latitude: 8.7523,
  longitude: 38.9785,
  accuracy: 35,
);

const _noFix = GpsCaptureResult.unavailable('Location services disabled');

/// The platforms the order detail runs on: the phone has a MarketplaceSync
/// and a connectivity monitor (a check-in is queued), web has neither (a
/// check-in is sent at once).
enum _Platform { phone, web }

/// An accepted order the server stamped for the pickup judgement, its
/// check-in window open now (the pickup day began an hour ago).
Order _order({
  String? judgement = 'pending',
  OrderStatus status = OrderStatus.accepted,
  bool selfPickup = false,
  DateTime? pickupDate,
  DateTime? deadline,
}) {
  final now = DateTime.now().toUtc();
  return Order()
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
    ..createdAt = now.subtract(const Duration(days: 3))
    ..version = 5
    ..tripId = selfPickup ? null : 'trip-001'
    ..truckerId = selfPickup ? null : 'trucker-001'
    ..truckerName = selfPickup ? null : 'Trucker Tesfaye'
    ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
    ..farmerResponse = 'accepted'
    ..truckerResponse = selfPickup ? 'none' : 'accepted'
    ..pickupDate =
        selfPickup ? null : pickupDate ?? now.subtract(const Duration(hours: 1))
    ..pickupDeadlineAt = deadline ?? now.add(const Duration(hours: 6))
    ..pickupJudgement = judgement
    ..contactOpen = true;
}

OrderCheckIn _entry(String kind, {bool late = false}) => OrderCheckIn()
  ..kind = kind
  ..receivedAt = DateTime.utc(2026, 11, 20, 5, 40)
  ..distanceChecked = true
  ..isLate = late;

/// ESC-34, the client part (FUP-285): the order detail of an accepted order
/// stamped for the pickup judgement, for the farmer, the trucker and the
/// buyer (trip and self-pickup), on the phone and on web.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockRatingBloc ratingBloc;
  late MockMarketplaceSync sync;
  late MockConnectivityMonitor monitor;
  late StreamController<OrderState> orderStream;
  late FakeGps gps;
  late MockSyncExecutor executor;
  late StreamController<SyncBatchResult> syncResults;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeDeliveryEvent());
  });

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    ratingBloc = MockRatingBloc();
    sync = MockMarketplaceSync();
    monitor = MockConnectivityMonitor();
    orderStream = StreamController<OrderState>.broadcast();
    gps = FakeGps(_atFarm);
    executor = MockSyncExecutor();
    syncResults = StreamController<SyncBatchResult>.broadcast();
    when(() => executor.onSyncResult).thenAnswer((_) => syncResults.stream);
    final ratingRepo = MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => orderBloc.stream).thenAnswer((_) => orderStream.stream);
    when(() => orderBloc.add(any())).thenReturn(null);
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
    // The phone's server read on open is held back (a change waits): the
    // stored order stays on screen.
    when(() => sync.pullOrder(any())).thenAnswer((_) async => null);
    when(() => monitor.isConnected()).thenAnswer((_) async => true);
  });

  tearDown(() async {
    await orderStream.close();
    await syncResults.close();
  });

  Future<void> show(
    WidgetTester tester,
    Order order,
    UserRole role,
    String userId, {
    _Platform platform = _Platform.phone,
    Locale locale = const Locale('en'),
    OrderBloc? bloc,
  }) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
    );
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    final screen = MultiBlocProvider(
      providers: [
        BlocProvider<OrderBloc>.value(value: bloc ?? orderBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
      ],
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: OrderDetailScreen(orderId: 'order-001', gpsService: gps),
      ),
    );
    await tester.pumpWidget(
      switch (platform) {
        _Platform.phone => MultiProvider(
            providers: [
              Provider<MarketplaceSync?>.value(value: sync),
              Provider<ConnectivityMonitor>.value(value: monitor),
              Provider<SyncExecutor>.value(value: executor),
            ],
            child: screen,
          ),
        _Platform.web => Provider<MarketplaceSync?>.value(
            value: null,
            child: screen,
          ),
      },
    );
    await tester.pumpAndSettle();
  }

  List<CheckInAtPickup> checkIns() => verify(() => orderBloc.add(captureAny()))
      .captured
      .whereType<CheckInAtPickup>()
      .toList();

  List<CheckInAtPickup> checkInsSoFar() {
    try {
      return checkIns();
    } on TestFailure {
      return const [];
    }
  }

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.tap(finder);
    await tester.pumpAndSettle();
  }

  for (final platform in _Platform.values) {
    group(platform.name, () {
      testWidgets('trucker: "I have arrived" takes a fix and checks in',
          (tester) async {
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        expect(find.byKey(const Key('order_check_in_card')), findsOneWidget);
        expect(find.text(en.checkInPartyTrucker), findsOneWidget);
        expect(find.text(en.checkInPendingNote), findsOneWidget);
        expect(find.text(en.checkInCropsReadyButton), findsNothing);

        await tap(tester, find.text(en.checkInArrivedButton));

        expect(gps.calls, 1);
        final event = checkIns().single;
        expect(event.orderId, 'order-001');
        expect(event.userId, 'trucker-001');
        expect(event.kind, CheckInKind.arrivedPickup);
        expect(event.fix!.latitude, 8.7523);
        expect(event.fix!.accuracyM, 35);
      });

      testWidgets('trucker: no fix, no check-in, and it says why',
          (tester) async {
        gps.result = _noFix;
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        await tap(tester, find.text(en.checkInArrivedButton));

        expect(find.text(en.checkInNeedsLocation), findsOneWidget);
        expect(checkInsSoFar(), isEmpty);
      });

      testWidgets('farmer: "Crops are ready" goes without a fix',
          (tester) async {
        gps.result = _noFix;
        await show(
          tester,
          _order(),
          UserRole.farmer,
          'farmer-001',
          platform: platform,
        );

        expect(find.text(en.checkInArrivedButton), findsNothing);
        await tap(tester, find.text(en.checkInCropsReadyButton));

        final event = checkIns().single;
        expect(event.kind, CheckInKind.cropsReady);
        expect(event.fix, isNull);
      });

      testWidgets('farmer: a fix outside Ethiopia is not sent', (tester) async {
        gps.result = const GpsCaptureResult.success(
          latitude: 51.5,
          longitude: -0.12,
          accuracy: 10,
        );
        await show(
          tester,
          _order(),
          UserRole.farmer,
          'farmer-001',
          platform: platform,
        );

        await tap(tester, find.text(en.checkInCropsReadyButton));

        expect(checkIns().single.fix, isNull);
      });

      testWidgets(
          'buyer (trip): sees both check-ins, gives none, and has no '
          'deadline cancel on a stamped order', (tester) async {
        final now = DateTime.now().toUtc();
        await show(
          tester,
          _order(
            pickupDate: now.subtract(const Duration(days: 2)),
            deadline: now.subtract(const Duration(hours: 1)),
          )..checkIns = [_entry(CheckInKind.arrivedPickup)],
          UserRole.buyer,
          'buyer-001',
          platform: platform,
        );

        expect(find.byKey(const Key('order_check_in_card')), findsOneWidget);
        expect(
          find.textContaining('Received', findRichText: true),
          findsOneWidget,
        );
        expect(find.text(en.checkInDeadlinePassedNote), findsOneWidget);
        expect(find.text(en.checkInArrivedButton), findsNothing);
        expect(find.text(en.checkInCropsReadyButton), findsNothing);
        expect(find.text(en.cancelOrder), findsNothing);
        expect(find.text(en.orderTransportFailedBuyerHint), findsNothing);
      });

      testWidgets('buyer (self-pickup): "I have arrived" at the farm',
          (tester) async {
        await show(
          tester,
          _order(selfPickup: true),
          UserRole.buyer,
          'buyer-001',
          platform: platform,
        );

        expect(find.text(en.checkInPartyBuyer), findsOneWidget);
        expect(find.text(en.checkInPartyTrucker), findsNothing);
        await tap(tester, find.text(en.checkInArrivedButton));

        final event = checkIns().single;
        expect(event.userId, 'buyer-001');
        expect(event.kind, CheckInKind.arrivedPickup);
        expect(event.fix, isNotNull);
      });

      testWidgets('saved: says so and reads the order again', (tester) async {
        await show(
          tester,
          _order(),
          UserRole.farmer,
          'farmer-001',
          platform: platform,
        );

        orderStream.add(const CheckInSaved(kind: CheckInKind.cropsReady));
        await tester.pumpAndSettle();

        expect(find.text(en.checkInSent), findsOneWidget);
        expect(
          verify(() => orderBloc.add(captureAny()))
              .captured
              .whereType<LoadOrderById>(),
          hasLength(2),
          reason: 'the load on open, then the reload',
        );
      });

      testWidgets('a late check-in says it was kept for review',
          (tester) async {
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        orderStream.add(
          const OrderError(
            message: 'CHECKIN_TOO_LATE',
            code: AppErrorCode.checkInTooLate,
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text(en.errorCheckInTooLate), findsOneWidget);
        expect(en.errorCheckInTooLate, contains('kept for review'));
      });
    });
  }

  group('phone only', () {
    testWidgets('location denied for good: says so and offers the settings',
        (tester) async {
      gps.result = const GpsCaptureResult.unavailable(
        GpsCaptureResult.permanentlyDeniedReason,
      );
      await show(tester, _order(), UserRole.trucker, 'trucker-001');

      await tap(tester, find.text(en.checkInArrivedButton));

      expect(find.text(en.checkInLocationBlocked), findsOneWidget);
      expect(find.text(en.checkInNeedsLocation), findsNothing);
      expect(checkInsSoFar(), isEmpty);
      await tester.tap(find.text(en.checkInOpenSettings));
      await tester.pumpAndSettle();
      expect(gps.settingsOpened, 1);
    });

    testWidgets('the farmer with location denied for good still checks in',
        (tester) async {
      gps.result = const GpsCaptureResult.unavailable(
        GpsCaptureResult.permanentlyDeniedReason,
      );
      await show(tester, _order(), UserRole.farmer, 'farmer-001');

      await tap(tester, find.text(en.checkInCropsReadyButton));

      expect(checkIns().single.fix, isNull);
      expect(find.text(en.checkInLocationBlocked), findsNothing);
    });

    testWidgets('a repeat says it was already given, not sent', (tester) async {
      await show(tester, _order(), UserRole.farmer, 'farmer-001');

      orderStream.add(
        const CheckInSaved(kind: CheckInKind.cropsReady, alreadyGiven: true),
      );
      await tester.pumpAndSettle();

      expect(find.text(en.checkInAlreadyGiven), findsOneWidget);
      expect(find.text(en.checkInSent), findsNothing);
    });

    testWidgets('offline, the check-in is saved on the phone', (tester) async {
      when(() => monitor.isConnected()).thenAnswer((_) async => false);
      await show(tester, _order(), UserRole.trucker, 'trucker-001');

      orderStream.add(const CheckInSaved(kind: CheckInKind.arrivedPickup));
      await tester.pumpAndSettle();

      expect(find.text(en.savedOnPhoneWillSend), findsOneWidget);
    });

    testWidgets('a queued check-in shows its mark instead of the button',
        (tester) async {
      await show(
        tester,
        _order()
          ..queuedCheckIns = {
            'arrived_pickup': QueuedCheckIn(madeAt: DateTime.now().toUtc()),
          },
        UserRole.trucker,
        'trucker-001',
      );

      expect(
        find.textContaining('Saved on this phone', findRichText: true),
        findsOneWidget,
      );
      expect(find.text(en.checkInArrivedButton), findsNothing);
    });

    testWidgets('a sent one says sent until the server copy comes',
        (tester) async {
      await show(
        tester,
        _order()
          ..queuedCheckIns = {
            'crops_ready': QueuedCheckIn(
              madeAt: DateTime.now().toUtc(),
              sent: true,
            ),
          },
        UserRole.farmer,
        'farmer-001',
      );

      expect(find.textContaining('Sent', findRichText: true), findsOneWidget);
      expect(find.text(en.checkInCropsReadyButton), findsNothing);
    });
  });

  // Review item 1: the phone sends a queued check-in after a short wait
  // and may refuse it; the order detail follows the sync results while a
  // check-in made here waits for the server's copy.
  group('phone: the order follows the sync', () {
    late MockOrderRepository repo;
    late List<Order> reads;

    setUp(() {
      repo = MockOrderRepository();
      reads = [];
      when(() => repo.getOrderById('order-001')).thenAnswer(
        (_) async => reads.length > 1 ? reads.removeAt(0) : reads.first,
      );
    });

    /// A real order bloc over [repo], made inside the test's zone so its
    /// events run as the test pumps.
    OrderBloc makeBloc() {
      final bloc = OrderBloc(repository: repo);
      addTearDown(bloc.close);
      return bloc;
    }

    Order queued({bool sent = false}) => _order()
      ..queuedCheckIns = {
        'arrived_pickup': QueuedCheckIn(
          madeAt: DateTime.now().toUtc(),
          sent: sent,
        ),
      };

    testWidgets('a refusal gives the button back at once', (tester) async {
      reads.addAll([queued(), _order()]);
      await show(
        tester,
        queued(),
        UserRole.trucker,
        'trucker-001',
        bloc: makeBloc(),
      );
      expect(
        find.textContaining('not sent yet', findRichText: true),
        findsOneWidget,
      );
      expect(find.text(en.checkInArrivedButton), findsNothing);

      // The queue sent it and the server refused it (too far): rejected.
      syncResults.add(
        const SyncBatchResult(
          total: 1,
          failed: 1,
          status: SyncBatchStatus.partial,
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.textContaining('not sent yet', findRichText: true),
        findsNothing,
      );
      expect(find.text(en.checkInArrivedButton), findsOneWidget);
    });

    testWidgets(
        'a sent one stops saying "not sent yet" and the server copy '
        'is read', (tester) async {
      reads.addAll([queued(), queued(sent: true)]);
      await show(
        tester,
        queued(),
        UserRole.trucker,
        'trucker-001',
        bloc: makeBloc(),
      );
      // The reads on open (the order, the contact) are behind us.
      clearInteractions(sync);

      syncResults.add(
        const SyncBatchResult(
          total: 1,
          succeeded: 1,
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.textContaining('not sent yet', findRichText: true),
        findsNothing,
      );
      expect(find.textContaining('Sent', findRichText: true), findsOneWidget);
      verify(() => sync.pullOrder('order-001')).called(greaterThanOrEqualTo(1));
    });

    testWidgets('no check-in waiting: a sync reads nothing', (tester) async {
      reads.add(_order());
      await show(
        tester,
        _order(),
        UserRole.trucker,
        'trucker-001',
        bloc: makeBloc(),
      );
      verify(() => repo.getOrderById('order-001')).called(1);

      syncResults.add(
        const SyncBatchResult(
          total: 1,
          succeeded: 1,
        ),
      );
      await tester.pumpAndSettle();

      verifyNever(() => repo.getOrderById('order-001'));
    });
  });

  group('what the card shows', () {
    testWidgets('received and late check-ins, no button once given',
        (tester) async {
      await show(
        tester,
        _order()
          ..checkIns = [
            _entry(CheckInKind.arrivedPickup),
            _entry(CheckInKind.cropsReady, late: true),
          ],
        UserRole.trucker,
        'trucker-001',
      );

      expect(
        find.textContaining('Late, received', findRichText: true),
        findsOneWidget,
      );
      expect(
        find.textContaining(RegExp(r'^Received'), findRichText: true),
        findsOneWidget,
      );
      expect(find.text(en.checkInArrivedButton), findsNothing);
    });

    testWidgets('before the window: when it opens, no button', (tester) async {
      final now = DateTime.now().toUtc();
      await show(
        tester,
        _order(
          pickupDate: now.add(const Duration(days: 2)),
          deadline: now.add(const Duration(days: 3)),
        ),
        UserRole.farmer,
        'farmer-001',
      );

      expect(find.byKey(const Key('check_in_opens_at')), findsOneWidget);
      expect(
        find.textContaining('You can check in from', findRichText: true),
        findsOneWidget,
      );
      expect(find.text(en.checkInCropsReadyButton), findsNothing);
    });

    testWidgets('an order accepted without the stamp shows no card',
        (tester) async {
      await show(
        tester,
        _order(judgement: null),
        UserRole.trucker,
        'trucker-001',
      );

      expect(find.byKey(const Key('order_check_in_card')), findsNothing);
      expect(find.text(en.checkInArrivedButton), findsNothing);
    });

    testWidgets('picked up and scanned: no card', (tester) async {
      await show(
        tester,
        _order(judgement: 'scanned', status: OrderStatus.inTransit),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.byKey(const Key('order_check_in_card')), findsNothing);
    });

    for (final (judgement, status, text) in [
      (
        'trucker_no_show',
        OrderStatus.awaitingTransport,
        en.pickupJudgementTruckerNoShow,
      ),
      ('farmer_no_show', OrderStatus.cancelled, en.pickupJudgementFarmerNoShow),
      ('buyer_no_show', OrderStatus.cancelled, en.pickupJudgementBuyerNoShow),
      (
        'both_present_no_scan',
        OrderStatus.accepted,
        en.pickupJudgementBothPresent,
      ),
      (
        'unchecked_check_in',
        OrderStatus.accepted,
        en.pickupJudgementUnchecked,
      ),
      ('a_new_outcome', OrderStatus.accepted, en.pickupJudgementOther),
    ]) {
      testWidgets('judged $judgement: the outcome, no button', (tester) async {
        await show(
          tester,
          _order(
            judgement: judgement,
            status: status,
            selfPickup: judgement == 'buyer_no_show',
          ),
          UserRole.buyer,
          'buyer-001',
        );

        expect(find.text(text), findsOneWidget);
        expect(find.text(en.checkInArrivedButton), findsNothing);
        expect(
          find.text(en.cancelOrder),
          status == OrderStatus.awaitingTransport
              ? findsOneWidget
              : findsNothing,
        );
      });
    }

    testWidgets("a trucker no-show: the trucker's row and the failure",
        (tester) async {
      await show(
        tester,
        _order(
          judgement: 'trucker_no_show',
          status: OrderStatus.awaitingTransport,
        )
          ..truckerResponse = 'no_show'
          ..transportFailure = 'trucker_no_show'
          ..buyerChoiceDeadlineAt =
              DateTime.now().toUtc().add(const Duration(days: 2)),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.commitmentTruckerNoShow), findsOneWidget);
      expect(
        find.textContaining(
          en.transportFailureTruckerNoShow,
          findRichText: true,
        ),
        findsOneWidget,
      );
    });

    for (final locale in const [Locale('am'), Locale('om')]) {
      testWidgets('${locale.languageCode}: the card and the button render',
          (tester) async {
        final l10n = lookupAppLocalizations(locale);
        await show(
          tester,
          _order()..checkIns = [_entry(CheckInKind.cropsReady)],
          UserRole.trucker,
          'trucker-001',
          locale: locale,
        );

        expect(tester.takeException(), isNull);
        expect(find.text(l10n.checkInTitle), findsOneWidget);
        expect(find.text(l10n.checkInArrivedButton), findsOneWidget);
      });
    }
  });

  // Review item 2: the button comes when check-ins open and goes at the
  // deadline while the screen stays open.
  testWidgets('the card follows the window edges while it is open',
      (tester) async {
    var now = DateTime.utc(2026, 11, 19, 5);
    final order = _order(
      pickupDate: DateTime.utc(2026, 11, 20, 6),
      deadline: DateTime.utc(2026, 11, 21, 6),
    );
    await tester.pumpWidget(
      BlocProvider<OrderBloc>.value(
        value: orderBloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: SingleChildScrollView(
              child: PickupCheckInCard(
                order: order,
                userId: 'farmer-001',
                gpsService: gps,
                clock: () => now,
              ),
            ),
          ),
        ),
      ),
    );
    expect(find.byKey(const Key('check_in_opens_at')), findsOneWidget);
    expect(find.text(en.checkInCropsReadyButton), findsNothing);

    // Check-ins open (pickup day minus 24 hours).
    now = DateTime.utc(2026, 11, 19, 6, 0, 1);
    await tester.pump(const Duration(hours: 1));
    expect(find.text(en.checkInCropsReadyButton), findsOneWidget);
    expect(find.byKey(const Key('check_in_opens_at')), findsNothing);

    // The deadline passes: no button to be refused on a tap.
    now = DateTime.utc(2026, 11, 21, 6, 0, 5);
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(hours: 12));
    }
    expect(find.text(en.checkInCropsReadyButton), findsNothing);
    expect(find.text(en.checkInDeadlinePassedNote), findsOneWidget);
  });
}

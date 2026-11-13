import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

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

  @override
  Future<bool> openAppSettings() async => true;
}

const _deliveryId = '6f1c2a4e-1111-4b2c-9d3e-0a1b2c3d4e5f';
const _arrivalKey = 'uploads/photos/$_deliveryId/arrival-1700000.webp';
const _photoPath = '/data/photos/$_deliveryId/arrival-1.webp';

const _atFarm = GpsCaptureResult.success(
  latitude: 8.7523,
  longitude: 38.9785,
  accuracy: 35,
);

const _atDropoff = GpsCaptureResult.success(
  latitude: 9.031,
  longitude: 38.74,
  accuracy: 20,
);

/// The phone has a MarketplaceSync, a connectivity monitor and a sync
/// executor (a check-in is queued); web has none (it is sent at once).
enum _Platform { phone, web }

Delivery _delivery({
  DeliveryStatus status = DeliveryStatus.pending,
  bool selfPickup = false,
  String? arrivalPhotoRef,
}) {
  final now = DateTime.now().toUtc();
  return Delivery()
    ..deliveryId = _deliveryId
    ..tripId = selfPickup ? '' : 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Abebe Kebede'
    ..truckerId = selfPickup ? '' : 'trucker-001'
    ..truckerName = selfPickup ? '' : 'Tadesse Worku'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..deliveryType =
        selfPickup ? DeliveryConstants.selfPickupDeliveryType : null
    ..qrToken = ''
    ..orderId = 'order-001'
    ..status = status
    ..pickedUpAt = status == DeliveryStatus.pending
        ? null
        : now.subtract(const Duration(hours: 5))
    ..arrivalPhotoRef = arrivalPhotoRef
    ..createdAt = now.subtract(const Duration(days: 2))
    ..updatedAt = now
    ..version = 3;
}

/// An accepted order stamped for the pickup judgement, its check-in window
/// open now (the pickup day began an hour ago).
Order _atFarmOrder({bool selfPickup = false, String? judgement = 'pending'}) {
  final now = DateTime.now().toUtc();
  return Order()
    ..orderId = 'order-001'
    ..listingId = 'listing-001'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..farmerName = 'Abebe Kebede'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 85
    ..totalPrice = 8500
    ..status = OrderStatus.accepted
    ..createdAt = now.subtract(const Duration(days: 3))
    ..version = 5
    ..tripId = selfPickup ? null : 'trip-001'
    ..truckerId = selfPickup ? null : 'trucker-001'
    ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
    ..pickupDate = selfPickup ? null : now.subtract(const Duration(hours: 1))
    ..pickupDeadlineAt = now.add(const Duration(hours: 6))
    ..pickupJudgement = judgement;
}

/// An order on the road stamped for the drop-off judgement, T8 a day on.
Order _onTheRoadOrder({String? judgement = 'pending'}) {
  final now = DateTime.now().toUtc();
  return Order()
    ..orderId = 'order-001'
    ..listingId = 'listing-001'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..farmerName = 'Abebe Kebede'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 85
    ..totalPrice = 8500
    ..status = OrderStatus.inTransit
    ..createdAt = now.subtract(const Duration(days: 3))
    ..version = 7
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..fulfillment = 'trucker'
    ..pickupJudgement = 'scanned'
    ..dropoffJudgement = judgement
    ..dropoffDeadlineAt = now.add(const Duration(days: 1));
}

/// FUP-327 and FUP-371: the delivery detail offers the check-ins where the
/// parties already are, by the order detail's cards and rules: before
/// pickup "I have arrived" (the trucker, the self-pickup buyer) and "Crops
/// are ready" (the farmer); on the road the trucker's "Arrived at drop-off"
/// with the arrival photo. On the phone and on web.
void main() {
  late MockDeliveryBloc deliveryBloc;
  late MockDeliveryRepository deliveryRepo;
  late MockOrderBloc appOrderBloc;
  late MockOrderRepository orderRepo;
  late MockMarketplaceSync sync;
  late MockConnectivityMonitor monitor;
  late MockSyncExecutor executor;
  late StreamController<SyncBatchResult> syncResults;
  late FakeGps gps;
  late List<Order> reads;
  late List<String> photosTaken;
  late PhotoResult photoResult;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() {
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(
      const HandoffCheckInFix(latitude: 9, longitude: 38),
    );
  });

  setUp(() {
    deliveryBloc = MockDeliveryBloc();
    deliveryRepo = MockDeliveryRepository();
    appOrderBloc = MockOrderBloc();
    orderRepo = MockOrderRepository();
    sync = MockMarketplaceSync();
    monitor = MockConnectivityMonitor();
    executor = MockSyncExecutor();
    syncResults = StreamController<SyncBatchResult>.broadcast();
    gps = FakeGps(_atFarm);
    reads = [];
    photosTaken = [];
    photoResult = const PhotoResult.success(
      localPath: _photoPath,
      fileSizeBytes: 90000,
    );
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => null);
    // The app's order bloc only lends its repository: the delivery detail
    // drives a bloc of its own.
    when(() => appOrderBloc.repository).thenReturn(orderRepo);
    when(() => appOrderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderRepo.getOrderById('order-001')).thenAnswer(
      (_) async => reads.length > 1 ? reads.removeAt(0) : reads.first,
    );
    when(
      () => orderRepo.checkIn(
        any(),
        any(),
        kind: any(named: 'kind'),
        fix: any(named: 'fix'),
      ),
    ).thenAnswer((_) async => true);
    when(
      () => orderRepo.checkInAtDropoff(
        any(),
        any(),
        deliveryId: any(named: 'deliveryId'),
        fix: any(named: 'fix'),
        arrivalPhotoRef: any(named: 'arrivalPhotoRef'),
      ),
    ).thenAnswer((_) async => true);
    when(() => sync.pullOrder(any())).thenAnswer((_) async => null);
    when(() => monitor.isConnected()).thenAnswer((_) async => true);
    when(() => executor.onSyncResult).thenAnswer((_) => syncResults.stream);
  });

  tearDown(() async {
    await syncResults.close();
  });

  Future<void> show(
    WidgetTester tester,
    Delivery delivery,
    Order order,
    String userId, {
    _Platform platform = _Platform.phone,
    Locale locale = const Locale('en'),
  }) async {
    tester.view.physicalSize = const Size(1000, 5000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(const SizedBox.shrink());
    if (reads.isEmpty) reads.add(order);
    final screen = MultiBlocProvider(
      providers: [
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<OrderBloc>.value(value: appOrderBloc),
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
        home: DeliveryDetailScreen(
          delivery: delivery,
          currentUserId: userId,
          telebirrLive: true,
          checkInGps: gps,
          takeArrivalPhoto: (id) async {
            photosTaken.add(id);
            return photoResult;
          },
        ),
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

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.tap(finder);
    await tester.pumpAndSettle();
  }

  for (final platform in _Platform.values) {
    group(platform.name, () {
      testWidgets(
          'trucker, before pickup: "I have arrived" takes a fix and checks '
          'in through the repository', (tester) async {
        await show(
          tester,
          _delivery(),
          _atFarmOrder(),
          'trucker-001',
          platform: platform,
        );

        expect(find.byKey(const Key('order_check_in_card')), findsOneWidget);
        expect(find.text(en.checkInCropsReadyButton), findsNothing);
        await tap(tester, find.text(en.checkInArrivedButton));

        expect(gps.calls, 1);
        final fix = verify(
          () => orderRepo.checkIn(
            'order-001',
            'trucker-001',
            kind: CheckInKind.arrivedPickup,
            fix: captureAny(named: 'fix'),
          ),
        ).captured.single as HandoffCheckInFix;
        expect(fix.latitude, 8.7523);
        expect(find.text(en.checkInSent), findsOneWidget);
        // The order is read again after the check-in.
        verify(() => orderRepo.getOrderById('order-001')).called(2);
      });

      testWidgets('farmer, before pickup: "Crops are ready"', (tester) async {
        await show(
          tester,
          _delivery(),
          _atFarmOrder(),
          'farmer-001',
          platform: platform,
        );

        expect(find.text(en.checkInArrivedButton), findsNothing);
        await tap(tester, find.text(en.checkInCropsReadyButton));

        verify(
          () => orderRepo.checkIn(
            'order-001',
            'farmer-001',
            kind: CheckInKind.cropsReady,
            fix: any(named: 'fix'),
          ),
        ).called(1);
      });

      testWidgets('self-pickup buyer: "I have arrived" at the farm',
          (tester) async {
        await show(
          tester,
          _delivery(selfPickup: true),
          _atFarmOrder(selfPickup: true),
          'buyer-001',
          platform: platform,
        );

        expect(find.text(en.checkInPartyBuyer), findsOneWidget);
        await tap(tester, find.text(en.checkInArrivedButton));

        verify(
          () => orderRepo.checkIn(
            'order-001',
            'buyer-001',
            kind: CheckInKind.arrivedPickup,
            fix: any(named: 'fix'),
          ),
        ).called(1);
      });

      testWidgets('trip buyer, before pickup: the check-ins, no button',
          (tester) async {
        await show(
          tester,
          _delivery(),
          _atFarmOrder(),
          'buyer-001',
          platform: platform,
        );

        expect(find.byKey(const Key('order_check_in_card')), findsOneWidget);
        expect(find.byKey(const Key('check_in_button')), findsNothing);
      });

      testWidgets(
          'trucker, on the road: "Arrived at drop-off" takes the photo, then '
          'the fix, then checks in', (tester) async {
        gps.result = _atDropoff;
        await show(
          tester,
          _delivery(status: DeliveryStatus.inTransit),
          _onTheRoadOrder(),
          'trucker-001',
          platform: platform,
        );

        expect(find.byKey(const Key('dropoff_check_in_card')), findsOneWidget);
        expect(
          find.byKey(const Key('dropoff_check_in_photo_hint')),
          findsOneWidget,
        );
        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(photosTaken, [_deliveryId]);
        expect(gps.calls, 1);
        final captured = verify(
          () => orderRepo.checkInAtDropoff(
            'order-001',
            'trucker-001',
            deliveryId: _deliveryId,
            fix: captureAny(named: 'fix'),
            arrivalPhotoRef: captureAny(named: 'arrivalPhotoRef'),
          ),
        ).captured;
        expect((captured[0] as HandoffCheckInFix).latitude, 9.031);
        expect(captured[1], _photoPath);
        expect(find.text(en.checkInSent), findsOneWidget);
        // The screen's delivery was used: nothing was looked up.
        verifyNever(
          () => deliveryRepo.findActiveDeliveryForOrder(
            any(),
            userId: any(named: 'userId'),
            role: any(named: 'role'),
            orderCreatedAt: any(named: 'orderCreatedAt'),
          ),
        );
      });

      testWidgets('picked up with the arrival photo: no second photo',
          (tester) async {
        gps.result = _atDropoff;
        await show(
          tester,
          _delivery(
            status: DeliveryStatus.pickedUp,
            arrivalPhotoRef: _arrivalKey,
          ),
          _onTheRoadOrder(),
          'trucker-001',
          platform: platform,
        );

        expect(
          find.byKey(const Key('dropoff_check_in_photo_hint')),
          findsNothing,
        );
        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(photosTaken, isEmpty);
        final photo = verify(
          () => orderRepo.checkInAtDropoff(
            'order-001',
            'trucker-001',
            deliveryId: _deliveryId,
            fix: any(named: 'fix'),
            arrivalPhotoRef: captureAny(named: 'arrivalPhotoRef'),
          ),
        ).captured.single;
        expect(photo, isNull);
      });

      testWidgets('the buyer and the farmer on the road: no button',
          (tester) async {
        for (final userId in ['buyer-001', 'farmer-001']) {
          await show(
            tester,
            _delivery(status: DeliveryStatus.inTransit),
            _onTheRoadOrder(),
            userId,
            platform: platform,
          );

          expect(
            find.byKey(const Key('dropoff_check_in_card')),
            findsOneWidget,
          );
          expect(find.text(en.dropoffCheckInButton), findsNothing);
        }
      });

      testWidgets('a refusal says why, with the drop-off text', (tester) async {
        gps.result = _atDropoff;
        when(
          () => orderRepo.checkInAtDropoff(
            any(),
            any(),
            deliveryId: any(named: 'deliveryId'),
            fix: any(named: 'fix'),
            arrivalPhotoRef: any(named: 'arrivalPhotoRef'),
          ),
        ).thenThrow(
          const SyncRejectedException(
            'CHECKIN_TOO_FAR: You are 7.2 km from the drop-off (at most 5 km).',
          ),
        );
        await show(
          tester,
          _delivery(
            status: DeliveryStatus.inTransit,
            arrivalPhotoRef: _arrivalKey,
          ),
          _onTheRoadOrder(),
          'trucker-001',
          platform: platform,
        );

        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(find.text(en.errorDropoffCheckInTooFar), findsOneWidget);
        expect(find.text(en.dropoffCheckInButton), findsOneWidget);
      });

      testWidgets('orders without the stamps: no check-in card at all',
          (tester) async {
        reads.add(_atFarmOrder(judgement: null));
        await show(
          tester,
          _delivery(),
          _atFarmOrder(judgement: null),
          'trucker-001',
          platform: platform,
        );
        expect(find.byKey(const Key('order_check_in_card')), findsNothing);

        reads
          ..clear()
          ..add(_onTheRoadOrder(judgement: null));
        await show(
          tester,
          _delivery(status: DeliveryStatus.inTransit),
          _onTheRoadOrder(judgement: null),
          'trucker-001',
          platform: platform,
        );
        expect(find.byKey(const Key('dropoff_check_in_card')), findsNothing);
        expect(find.text(en.dropoffCheckInButton), findsNothing);
      });
    });
  }

  group('phone only', () {
    testWidgets('offline, the check-in is saved on the phone', (tester) async {
      when(() => monitor.isConnected()).thenAnswer((_) async => false);
      await show(tester, _delivery(), _atFarmOrder(), 'farmer-001');

      await tap(tester, find.text(en.checkInCropsReadyButton));

      expect(find.text(en.savedOnPhoneWillSend), findsOneWidget);
    });

    Order queued({bool sent = false}) => _atFarmOrder()
      ..queuedCheckIns = {
        'arrived_pickup': QueuedCheckIn(
          madeAt: DateTime.now().toUtc(),
          sent: sent,
        ),
      };

    testWidgets('a refused queued check-in gives the button back at once',
        (tester) async {
      reads.addAll([queued(), _atFarmOrder()]);
      await show(tester, _delivery(), queued(), 'trucker-001');
      expect(
        find.textContaining('not sent yet', findRichText: true),
        findsOneWidget,
      );
      expect(find.text(en.checkInArrivedButton), findsNothing);

      syncResults.add(
        const SyncBatchResult(
          total: 1,
          failed: 1,
          status: SyncBatchStatus.partial,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text(en.checkInArrivedButton), findsOneWidget);
    });

    testWidgets('a sent one reads the server copy', (tester) async {
      reads.addAll([queued(), queued(sent: true)]);
      await show(tester, _delivery(), queued(), 'trucker-001');

      syncResults.add(const SyncBatchResult(total: 1, succeeded: 1));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('not sent yet', findRichText: true),
        findsNothing,
      );
      verify(() => sync.pullOrder('order-001')).called(1);
    });

    testWidgets('no check-in waiting: a sync reads nothing', (tester) async {
      await show(tester, _delivery(), _atFarmOrder(), 'trucker-001');
      verify(() => orderRepo.getOrderById('order-001')).called(1);

      syncResults.add(const SyncBatchResult(total: 1, succeeded: 1));
      await tester.pumpAndSettle();

      verifyNever(() => orderRepo.getOrderById('order-001'));
      verifyNever(() => sync.pullOrder(any()));
    });
  });

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('${locale.languageCode}: both buttons render', (tester) async {
      final l10n = lookupAppLocalizations(locale);
      await show(
        tester,
        _delivery(),
        _atFarmOrder(),
        'trucker-001',
        locale: locale,
      );
      expect(tester.takeException(), isNull);
      expect(find.text(l10n.checkInArrivedButton), findsOneWidget);

      reads
        ..clear()
        ..add(_onTheRoadOrder());
      await show(
        tester,
        _delivery(status: DeliveryStatus.inTransit),
        _onTheRoadOrder(),
        'trucker-001',
        locale: locale,
      );
      expect(tester.takeException(), isNull);
      expect(find.text(l10n.dropoffCheckInButton), findsOneWidget);
    });
  }
}

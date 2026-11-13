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
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
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

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

/// A GPS that answers what the test says.
class FakeGps extends GpsCaptureService {
  FakeGps(this.result);

  GpsCaptureResult result;
  int calls = 0;
  int settingsOpened = 0;

  @override
  Future<GpsCaptureResult> captureCurrentPosition() async {
    calls++;
    return result;
  }

  @override
  Future<bool> openAppSettings() async {
    settingsOpened++;
    return true;
  }
}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

const _deliveryId = '6f1c2a4e-1111-4b2c-9d3e-0a1b2c3d4e5f';

const _atDropoff = GpsCaptureResult.success(
  latitude: 9.031,
  longitude: 38.74,
  accuracy: 20,
);

/// The platforms the order detail runs on: the phone has a MarketplaceSync
/// and a sync executor (the check-in is queued behind the photo), web has
/// neither (the photo was uploaded when taken and the check-in goes at once).
enum _Platform { phone, web }

/// An order on the road, stamped for the drop-off judgement, T8 a day on.
Order _order({
  String? judgement = 'pending',
  OrderStatus status = OrderStatus.inTransit,
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
    ..createdAt = now.subtract(const Duration(days: 4))
    ..version = 7
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Trucker Tesfaye'
    ..fulfillment = 'trucker'
    ..farmerResponse = 'accepted'
    ..truckerResponse = 'accepted'
    ..dropoffJudgement = judgement
    ..dropoffDeadlineAt = deadline ?? now.add(const Duration(days: 1))
    ..contactOpen = true;
}

OrderCheckIn _dropoff({DateTime? receivedAt, bool late = false}) =>
    OrderCheckIn()
      ..kind = CheckInKind.arrivedDropoff
      ..receivedAt = receivedAt ??
          DateTime.now().toUtc().subtract(const Duration(hours: 1))
      ..distanceChecked = true
      ..isLate = late;

Delivery _delivery({String? arrivalPhotoRef}) {
  final now = DateTime.now().toUtc();
  return Delivery()
    ..deliveryId = _deliveryId
    ..tripId = 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Farmer Fikru'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Trucker Tesfaye'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Buyer Bee'
    ..qrToken = ''
    ..orderId = 'order-001'
    ..status = DeliveryStatus.inTransit
    ..arrivalPhotoRef = arrivalPhotoRef
    ..createdAt = now
    ..updatedAt = now
    ..version = 3;
}

/// ESC-36, the client part (FUP-336): the order detail of an order on the
/// road stamped for the drop-off judgement, for the trucker, the buyer and
/// the farmer, on the phone and on web.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockDeliveryRepository deliveryRepo;
  late MockRatingBloc ratingBloc;
  late MockMarketplaceSync sync;
  late MockConnectivityMonitor monitor;
  late MockSyncExecutor executor;
  late StreamController<OrderState> orderStream;
  late StreamController<SyncBatchResult> syncResults;
  late FakeGps gps;
  late List<String> photosTaken;
  late PhotoResult photoResult;
  late Delivery? delivery;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeDeliveryEvent());
  });

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    deliveryRepo = MockDeliveryRepository();
    ratingBloc = MockRatingBloc();
    sync = MockMarketplaceSync();
    monitor = MockConnectivityMonitor();
    executor = MockSyncExecutor();
    orderStream = StreamController<OrderState>.broadcast();
    syncResults = StreamController<SyncBatchResult>.broadcast();
    gps = FakeGps(_atDropoff);
    photosTaken = [];
    delivery = _delivery();
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
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => delivery);
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
  }) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    // A fresh screen for every order shown (a test may show several).
    await tester.pumpWidget(const SizedBox.shrink());
    // The photo the platform keeps: a file on the phone, the uploaded key
    // on web.
    photoResult = platform == _Platform.phone
        ? const PhotoResult.success(
            localPath: '/data/photos/$_deliveryId/arrival-1.webp',
            fileSizeBytes: 90000,
          )
        : const PhotoResult.success(
            localPath: null,
            fileSizeBytes: 90000,
            serverPhotoRef: 'uploads/photos/$_deliveryId/arrival-1.jpg',
          );
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
    );
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    final screen = MultiBlocProvider(
      providers: [
        BlocProvider<OrderBloc>.value(value: orderBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: OrderDetailScreen(
          orderId: 'order-001',
          gpsService: gps,
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

  List<CheckInAtDropoff> checkInsSoFar() {
    try {
      return verify(() => orderBloc.add(captureAny()))
          .captured
          .whereType<CheckInAtDropoff>()
          .toList();
    } on TestFailure {
      return const [];
    }
  }

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.tap(finder);
    await tester.pumpAndSettle();
  }

  Finder textWith(String part) => find.textContaining(part, findRichText: true);

  for (final platform in _Platform.values) {
    group(platform.name, () {
      testWidgets(
          'trucker: "Arrived at drop-off" takes the photo, then the fix, '
          'then checks in', (tester) async {
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        expect(find.byKey(const Key('dropoff_check_in_card')), findsOneWidget);
        expect(find.text(en.dropoffCheckInPartyTrucker), findsOneWidget);
        expect(
          textWith('Check in when you reach the drop-off'),
          findsOneWidget,
        );
        expect(find.text(en.dropoffCheckInPhotoHint), findsOneWidget);

        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(photosTaken, [_deliveryId]);
        expect(gps.calls, 1);
        final event = checkInsSoFar().single;
        expect(event.orderId, 'order-001');
        expect(event.userId, 'trucker-001');
        expect(event.deliveryId, _deliveryId);
        expect(event.fix.latitude, 9.031);
        expect(event.fix.accuracyM, 20);
        expect(event.arrivalPhotoRef, photoResult.photoRef);
      });

      testWidgets('trucker: a delivery with its photo takes no other',
          (tester) async {
        delivery = _delivery(
          arrivalPhotoRef: 'uploads/photos/$_deliveryId/arrival-9.webp',
        );
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        expect(find.text(en.dropoffCheckInPhotoHint), findsNothing);
        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(photosTaken, isEmpty);
        expect(checkInsSoFar().single.arrivalPhotoRef, isNull);
      });

      testWidgets(
          'trucker: no fix, no check-in; the photo is kept for the next tap',
          (tester) async {
        gps.result = const GpsCaptureResult.unavailable('Location off');
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(find.text(en.dropoffCheckInNeedsLocation), findsOneWidget);
        expect(checkInsSoFar(), isEmpty);
        expect(photosTaken, hasLength(1));

        gps.result = _atDropoff;
        await tap(tester, find.text(en.dropoffCheckInButton));

        expect(photosTaken, hasLength(1), reason: 'the kept photo is used');
        expect(checkInsSoFar().single.arrivalPhotoRef, photoResult.photoRef);
      });

      testWidgets('trucker: a cancelled or failed photo checks nothing in',
          (tester) async {
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );
        photoResult = const PhotoResult.cancelled();
        await tap(tester, find.text(en.dropoffCheckInButton));
        expect(gps.calls, 0);

        photoResult = const PhotoResult.error('Photo upload failed');
        await tap(tester, find.text(en.dropoffCheckInButton));
        expect(find.text(en.deliveryPhotoFailed), findsOneWidget);
        expect(gps.calls, 0);
        expect(checkInsSoFar(), isEmpty);
      });

      testWidgets(
          'buyer: sees the check-in time and when it may be confirmed for '
          'them, and has no button', (tester) async {
        await show(
          tester,
          _order()..checkIns = [_dropoff()],
          UserRole.buyer,
          'buyer-001',
          platform: platform,
        );

        expect(find.byKey(const Key('dropoff_check_in_card')), findsOneWidget);
        expect(textWith('Received'), findsOneWidget);
        expect(
          textWith('the delivery may be confirmed for you'),
          findsOneWidget,
        );
        expect(find.text(en.dropoffCheckInButton), findsNothing);
      });

      testWidgets('farmer: waits for the trucker, then reads the buyer window',
          (tester) async {
        await show(
          tester,
          _order(),
          UserRole.farmer,
          'farmer-001',
          platform: platform,
        );

        expect(
          textWith('The trucker checks in at the drop-off by'),
          findsOneWidget,
        );
        expect(textWith('Not yet'), findsOneWidget);
        expect(find.text(en.dropoffCheckInButton), findsNothing);
      });

      testWidgets('farmer: after the check-in, the buyer\'s window',
          (tester) async {
        await show(
          tester,
          _order()..checkIns = [_dropoff()],
          UserRole.farmer,
          'farmer-001',
          platform: platform,
        );

        expect(
          textWith('may be confirmed for them'),
          findsOneWidget,
        );
      });

      testWidgets('a late check-in reads late, and no window is promised',
          (tester) async {
        await show(
          tester,
          _order(
            deadline: DateTime.now().toUtc().subtract(
                  const Duration(hours: 2),
                ),
          )..checkIns = [_dropoff(late: true)],
          UserRole.buyer,
          'buyer-001',
          platform: platform,
        );

        expect(textWith('Late, received'), findsOneWidget);
        expect(find.text(en.dropoffCheckInOutcomeSoon), findsOneWidget);
        expect(textWith('may be confirmed for you'), findsNothing);
      });

      testWidgets(
          'buyer: a delivery confirmed before the cron followed it promises '
          'nothing', (tester) async {
        await show(
          tester,
          _order(status: OrderStatus.delivered)..checkIns = [_dropoff()],
          UserRole.buyer,
          'buyer-001',
          platform: platform,
        );

        expect(textWith('Received'), findsOneWidget);
        expect(textWith('may be confirmed for you'), findsNothing);
        expect(find.byKey(const Key('dropoff_check_in_note')), findsNothing);
      });

      testWidgets('trucker: T8 passed, the button is gone', (tester) async {
        await show(
          tester,
          _order(
            deadline: DateTime.now().toUtc().subtract(
                  const Duration(minutes: 1),
                ),
          ),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        expect(find.text(en.dropoffCheckInOutcomeSoon), findsOneWidget);
        expect(find.text(en.dropoffCheckInButton), findsNothing);
      });

      testWidgets('every outcome has its label, for all three parties',
          (tester) async {
        final labels = {
          DropoffJudgementOutcome.closed: en.dropoffJudgementClosed,
          DropoffJudgementOutcome.autoConfirmed:
              en.dropoffJudgementAutoConfirmed,
          DropoffJudgementOutcome.silentBuyer: en.dropoffJudgementSilentBuyer,
          DropoffJudgementOutcome.deliveryOverdue:
              en.dropoffJudgementDeliveryOverdue,
          DropoffJudgementOutcome.dropoffOutOfRange:
              en.dropoffJudgementOutOfRange,
          DropoffJudgementOutcome.queued: en.dropoffJudgementQueued,
          'something_new': en.dropoffJudgementOther,
        };
        for (final (role, userId) in [
          (UserRole.trucker, 'trucker-001'),
          (UserRole.buyer, 'buyer-001'),
          (UserRole.farmer, 'farmer-001'),
        ]) {
          for (final MapEntry(key: judgement, value: label) in labels.entries) {
            await show(
              tester,
              _order(judgement: judgement, status: OrderStatus.disputed),
              role,
              userId,
              platform: platform,
            );
            expect(find.text(label), findsOneWidget, reason: judgement);
            expect(find.text(en.dropoffCheckInButton), findsNothing);
          }
        }
      });

      testWidgets('a refusal says why with the drop-off text', (tester) async {
        await show(
          tester,
          _order(),
          UserRole.trucker,
          'trucker-001',
          platform: platform,
        );

        orderStream.add(
          const OrderError(
            message: 'CHECKIN_PHOTO_REQUIRED',
            code: AppErrorCode.checkInPhotoRequired,
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text(en.errorCheckInPhotoRequired), findsOneWidget);
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

      await tap(tester, find.text(en.dropoffCheckInButton));

      expect(find.text(en.dropoffCheckInLocationBlocked), findsOneWidget);
      await tester.tap(find.text(en.checkInOpenSettings));
      await tester.pumpAndSettle();
      expect(gps.settingsOpened, 1);
      expect(checkInsSoFar(), isEmpty);
    });

    testWidgets('a queued check-in shows its mark instead of the button',
        (tester) async {
      await show(
        tester,
        _order()
          ..queuedCheckIns = {
            CheckInKind.arrivedDropoff:
                QueuedCheckIn(madeAt: DateTime.now().toUtc()),
          },
        UserRole.trucker,
        'trucker-001',
      );

      expect(textWith('Saved on this phone'), findsOneWidget);
      expect(find.text(en.dropoffCheckInButton), findsNothing);
    });

    testWidgets('the order is read again after a sync while one waits',
        (tester) async {
      await show(
        tester,
        _order()
          ..queuedCheckIns = {
            CheckInKind.arrivedDropoff:
                QueuedCheckIn(madeAt: DateTime.now().toUtc(), sent: true),
          },
        UserRole.trucker,
        'trucker-001',
      );
      clearInteractions(orderBloc);

      syncResults.add(
        const SyncBatchResult(
          total: 1,
          succeeded: 1,
        ),
      );
      await tester.pumpAndSettle();

      expect(
        verify(() => orderBloc.add(captureAny()))
            .captured
            .whereType<LoadOrderById>(),
        isNotEmpty,
      );
    });
  });
}

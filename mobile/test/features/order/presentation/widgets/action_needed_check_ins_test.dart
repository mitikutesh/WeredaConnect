import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockMarketplaceSync extends Mock implements MarketplaceSync {}

final _now = DateTime.utc(2026, 11, 20, 8);

/// An accepted order the server stamped for the pickup judgement, its
/// check-in window open at [_now] (the pickup day began two hours ago).
Order _atFarm(
  String id, {
  bool selfPickup = false,
  String? judgement = 'pending',
  DateTime? pickupDate,
  DateTime? deadline,
  DateTime? now,
}) {
  final at = now ?? _now;
  return Order()
    ..orderId = id
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..buyerName = 'Buyer'
    ..farmerName = 'Farmer'
    ..cropType = 'teff'
    ..quantity = 500
    ..pricePerUnit = 45
    ..totalPrice = 22500
    ..status = OrderStatus.accepted
    ..createdAt = DateTime.utc(2026, 11, 10)
    ..version = 4
    ..tripId = selfPickup ? null : 'trip-1'
    ..truckerId = selfPickup ? null : 'trucker-1'
    ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
    ..farmerResponse = 'accepted'
    ..truckerResponse = selfPickup ? 'none' : 'accepted'
    ..pickupDate =
        selfPickup ? null : pickupDate ?? at.subtract(const Duration(hours: 2))
    ..pickupDeadlineAt = deadline ?? at.add(const Duration(hours: 6))
    ..pickupJudgement = judgement;
}

/// A trucked order on the road the server stamped for the drop-off
/// judgement, T8 a day after [_now]; [received] is the trucker's drop-off
/// check-in.
Order _onTheRoad(
  String id, {
  OrderStatus status = OrderStatus.inTransit,
  String? judgement = 'pending',
  DateTime? received,
  bool late = false,
  DateTime? confirmAt,
  DateTime? now,
}) {
  final at = now ?? _now;
  return Order()
    ..orderId = id
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..buyerName = 'Buyer'
    ..farmerName = 'Farmer'
    ..cropType = 'teff'
    ..quantity = 500
    ..pricePerUnit = 45
    ..totalPrice = 22500
    ..status = status
    ..createdAt = DateTime.utc(2026, 11, 10)
    ..version = 6
    ..tripId = 'trip-1'
    ..truckerId = 'trucker-1'
    ..fulfillment = 'trucker'
    ..farmerResponse = 'accepted'
    ..truckerResponse = 'accepted'
    ..pickupJudgement = 'scanned'
    ..dropoffJudgement = judgement
    ..dropoffDeadlineAt = at.add(const Duration(days: 1))
    ..dropoffConfirmAt = confirmAt
    ..checkIns = [
      if (received != null)
        OrderCheckIn()
          ..kind = CheckInKind.arrivedDropoff
          ..receivedAt = received
          ..distanceChecked = true
          ..isLate = late,
    ];
}

OrderCheckIn _received(String kind) => OrderCheckIn()
  ..kind = kind
  ..receivedAt = _now.subtract(const Duration(minutes: 30))
  ..distanceChecked = true;

List<(String, ActionNeededKind, DateTime?)> _items(
  List<Order> orders,
  String userId,
  UserRole role, {
  Set<String> withDelivery = const {},
  DateTime? now,
}) =>
    [
      for (final item in actionNeededItems(
        orders: orders,
        userId: userId,
        role: role,
        ordersWithDelivery: withDelivery,
        now: now ?? _now,
      ))
        (item.order.orderId, item.kind, item.dueAt),
    ];

enum _Platform { phone, web }

/// FUP-327 and FUP-371: Home's "Action needed" cards for the check-ins, by
/// the order detail's rules, for the farmer, the trucker and the buyer (trip
/// and self-pickup), on the phone and on web.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() => registerFallbackValue(<Order>[]));

  group('the arrival check-ins at the farm (FUP-327)', () {
    test('the farmer: "Crops are ready" while the window is open', () {
      final order = _atFarm('o1');
      expect(_items([order], 'farmer-1', UserRole.farmer), [
        ('o1', ActionNeededKind.cropsReady, order.pickupDeadlineAt),
      ]);
    });

    test('the trucker: "I have arrived" once the delivery is set up', () {
      final order = _atFarm('o1');
      expect(
        _items(
          [order],
          'trucker-1',
          UserRole.trucker,
          withDelivery: {'o1'},
        ),
        [('o1', ActionNeededKind.checkInAtFarm, order.pickupDeadlineAt)],
      );
      // Without a delivery the step before it comes first.
      expect(
        _items([order], 'trucker-1', UserRole.trucker).single.$2,
        ActionNeededKind.setUpDelivery,
      );
    });

    test('the self-pickup buyer arrives; the trip buyer gives none', () {
      final self = _atFarm('self', selfPickup: true);
      expect(_items([self, _atFarm('trip')], 'buyer-1', UserRole.buyer), [
        ('self', ActionNeededKind.checkInAtFarm, self.pickupDeadlineAt),
      ]);
      // On a self-pickup order the farmer still says the crops are ready.
      expect(
        _items([self], 'farmer-1', UserRole.farmer).single.$2,
        ActionNeededKind.cropsReady,
      );
    });

    test(
        'none once given (the server has it, or it waits on the phone), '
        'before the window, after the deadline, or without the stamp', () {
      final cases = <String, Order>{
        'received': _atFarm('o')..checkIns = [_received('crops_ready')],
        'queued': _atFarm('o')
          ..queuedCheckIns = {'crops_ready': QueuedCheckIn(madeAt: _now)},
        'not open yet':
            _atFarm('o', pickupDate: _now.add(const Duration(days: 2))),
        'deadline passed':
            _atFarm('o', deadline: _now.subtract(const Duration(minutes: 1))),
        'flag off': _atFarm('o', judgement: null),
        'judged': _atFarm('o', judgement: 'farmer_no_show'),
      };
      for (final MapEntry(key: reason, value: order) in cases.entries) {
        expect(
          _items([order], 'farmer-1', UserRole.farmer),
          isEmpty,
          reason: reason,
        );
      }
      // The trucker's own given check-in; the farmer's does not count.
      expect(
        _items(
          [
            _atFarm('o')
              ..queuedCheckIns = {
                'arrived_pickup': QueuedCheckIn(madeAt: _now),
              },
          ],
          'trucker-1',
          UserRole.trucker,
          withDelivery: {'o'},
        ),
        isEmpty,
      );
      expect(
        _items(
          [
            _atFarm('o')..checkIns = [_received('crops_ready')],
          ],
          'trucker-1',
          UserRole.trucker,
          withDelivery: {'o'},
        ).single.$2,
        ActionNeededKind.checkInAtFarm,
      );
    });
  });

  group('the drop-off check-in (FUP-371)', () {
    test('the trucker: "Arrived at drop-off" on the road until T8', () {
      for (final status in [
        OrderStatus.pickupConfirmed,
        OrderStatus.inTransit,
      ]) {
        final order = _onTheRoad('o1', status: status);
        expect(_items([order], 'trucker-1', UserRole.trucker), [
          ('o1', ActionNeededKind.checkInAtDropoff, order.dropoffDeadlineAt),
        ]);
      }
      final cases = <String, Order>{
        'received': _onTheRoad('o', received: _now),
        'queued': _onTheRoad('o')
          ..queuedCheckIns = {'arrived_dropoff': QueuedCheckIn(madeAt: _now)},
        'T8 passed':
            _onTheRoad('o', now: _now.subtract(const Duration(days: 2))),
        'flag off': _onTheRoad('o', judgement: null),
        'delivered': _onTheRoad('o', status: OrderStatus.delivered),
      };
      for (final MapEntry(key: reason, value: order) in cases.entries) {
        expect(
          _items([order], 'trucker-1', UserRole.trucker),
          isEmpty,
          reason: reason,
        );
      }
    });

    test('the buyer: confirm by T9 after the counted check-in', () {
      final received = _now.subtract(const Duration(hours: 2));
      final order = _onTheRoad('o1', received: received);
      expect(_items([order], 'buyer-1', UserRole.buyer), [
        (
          'o1',
          ActionNeededKind.confirmDelivery,
          received.add(const Duration(hours: 48)),
        ),
      ]);
      // The server's own T9 (FUP-370) is the time.
      final serverT9 = received.add(const Duration(hours: 30));
      expect(
        _items(
          [_onTheRoad('o1', received: received, confirmAt: serverT9)],
          'buyer-1',
          UserRole.buyer,
        ).single.$3,
        serverT9,
      );
    });

    test(
        'no confirm card before the check-in, for a late one, after T9 or '
        'once judged; never for the farmer or the trucker', () {
      final received = _now.subtract(const Duration(hours: 2));
      final cases = <String, Order>{
        'no check-in': _onTheRoad('o'),
        'late': _onTheRoad('o', received: received, late: true),
        'T9 passed': _onTheRoad(
          'o',
          received: received,
          confirmAt: _now.subtract(const Duration(minutes: 1)),
        ),
        'judged': _onTheRoad(
          'o',
          received: received,
          judgement: 'auto_confirmed',
        ),
        'flag off': _onTheRoad('o', received: received, judgement: null),
      };
      for (final MapEntry(key: reason, value: order) in cases.entries) {
        expect(
          _items([order], 'buyer-1', UserRole.buyer),
          isEmpty,
          reason: reason,
        );
      }
      final checkedIn = _onTheRoad('o', received: received);
      expect(_items([checkedIn], 'farmer-1', UserRole.farmer), isEmpty);
      expect(_items([checkedIn], 'trucker-1', UserRole.trucker), isEmpty);
    });
  });

  test("another user's stamped orders never show a check-in card", () {
    final orders = [
      _atFarm('a'),
      _atFarm('b', selfPickup: true),
      _onTheRoad('c'),
      _onTheRoad('d', received: _now.subtract(const Duration(hours: 1))),
    ];
    for (final role in UserRole.values) {
      expect(
        _items(orders, 'someone-else', role, withDelivery: {'a'}),
        isEmpty,
        reason: role.name,
      );
    }
  });

  group('on Home', () {
    late MockOrderRepository repository;
    late MockDeliveryBloc deliveryBloc;
    late MockDeliveryRepository deliveryRepository;
    late MockMarketplaceSync sync;
    late List<Order> orders;

    setUp(() {
      repository = MockOrderRepository();
      deliveryBloc = MockDeliveryBloc();
      deliveryRepository = MockDeliveryRepository();
      sync = MockMarketplaceSync();
      orders = [];
      when(
        () => repository.getOrdersByUser(
          any(),
          role: any(named: 'role'),
          statusFilter: any(named: 'statusFilter'),
        ),
      ).thenAnswer((_) async => orders);
      // As the web repository: the orders as they are.
      when(() => repository.ordersWithQueuedCheckIns(any(), any())).thenAnswer(
        (invocation) async => invocation.positionalArguments[0] as List<Order>,
      );
      when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
      when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
      when(() => deliveryBloc.repository).thenReturn(deliveryRepository);
      // The trucker's farm order has its delivery set up.
      when(
        () => deliveryRepository.getDeliveriesByUser(
          any(),
          role: any(named: 'role'),
          statusFilter: any(named: 'statusFilter'),
        ),
      ).thenAnswer(
        (_) async => [
          Delivery()
            ..deliveryId = 'd1'
            ..orderId = 'farm'
            ..tripId = 'trip-1'
            ..listingId = 'listing-1'
            ..farmerId = 'farmer-1'
            ..truckerId = 'trucker-1'
            ..buyerId = 'buyer-1'
            ..qrToken = ''
            ..status = DeliveryStatus.pending
            ..createdAt = DateTime.utc(2026, 11, 10)
            ..updatedAt = DateTime.utc(2026, 11, 10)
            ..version = 1,
        ],
      );
      when(
        () => sync.pullOrders(
          userId: any(named: 'userId'),
          role: any(named: 'role'),
        ),
      ).thenAnswer((_) async {});
    });

    Future<void> pump(
      WidgetTester tester,
      String userId,
      UserRole role, {
      _Platform platform = _Platform.phone,
      Locale locale = const Locale('en'),
      DateTime Function()? clock,
    }) async {
      final bloc = MockOrderBloc();
      when(() => bloc.state).thenReturn(const OrderInitial());
      when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
      when(() => bloc.repository).thenReturn(repository);
      // A fresh list each time: Home makes a new one on every refresh.
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pumpWidget(
        Provider<MarketplaceSync?>.value(
          value: platform == _Platform.phone ? sync : null,
          child: MultiBlocProvider(
            providers: [
              BlocProvider<OrderBloc>.value(value: bloc),
              BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
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
              home: Scaffold(
                body: ActionNeededList(
                  userId: userId,
                  role: role,
                  clock: clock,
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    Finder due() => find.textContaining('Due', findRichText: true);

    for (final platform in _Platform.values) {
      group(platform.name, () {
        testWidgets('farmer: check in when the crops are ready, with the due',
            (tester) async {
          orders = [_atFarm('farm', now: DateTime.now().toUtc())];
          await pump(tester, 'farmer-1', UserRole.farmer, platform: platform);

          expect(find.text(en.actionCropsReady), findsOneWidget);
          expect(due(), findsOneWidget);
        });

        testWidgets('trucker: the farm, then the drop-off', (tester) async {
          final now = DateTime.now().toUtc();
          orders = [_atFarm('farm', now: now)];
          await pump(
            tester,
            'trucker-1',
            UserRole.trucker,
            platform: platform,
          );
          expect(find.text(en.actionCheckInAtFarm), findsOneWidget);
          expect(find.text(en.actionCheckInAtDropoff), findsNothing);

          orders = [_onTheRoad('road', now: now)];
          await pump(
            tester,
            'trucker-1',
            UserRole.trucker,
            platform: platform,
          );
          expect(find.text(en.actionCheckInAtDropoff), findsOneWidget);
          expect(due(), findsOneWidget);
        });

        testWidgets('self-pickup buyer: check in when you reach the farm',
            (tester) async {
          orders = [
            _atFarm('self', selfPickup: true, now: DateTime.now().toUtc()),
          ];
          await pump(tester, 'buyer-1', UserRole.buyer, platform: platform);

          expect(find.text(en.actionCheckInAtFarm), findsOneWidget);
        });

        testWidgets('trip buyer: confirm the delivery by T9', (tester) async {
          final now = DateTime.now().toUtc();
          orders = [
            _onTheRoad(
              'road',
              received: now.subtract(const Duration(hours: 1)),
              confirmAt: now.add(const Duration(days: 2)),
              now: now,
            ),
          ];
          await pump(tester, 'buyer-1', UserRole.buyer, platform: platform);

          expect(find.text(en.actionConfirmDelivery), findsOneWidget);
          expect(due(), findsOneWidget);
        });

        testWidgets('orders without the stamps: no card, and no queue read',
            (tester) async {
          final now = DateTime.now().toUtc();
          orders = [
            _atFarm('farm', judgement: null, now: now),
            _onTheRoad('road', judgement: null, now: now),
          ];
          for (final (userId, role) in [
            ('farmer-1', UserRole.farmer),
            ('trucker-1', UserRole.trucker),
            ('buyer-1', UserRole.buyer),
          ]) {
            await pump(tester, userId, role, platform: platform);
            expect(find.text(en.actionNeededTitle.toUpperCase()), findsNothing);
          }
          verifyNever(() => repository.ordersWithQueuedCheckIns(any(), any()));
        });
      });
    }

    group('phone', () {
      testWidgets('a check-in waiting on the phone takes the card away',
          (tester) async {
        orders = [_atFarm('farm', now: DateTime.now().toUtc())];
        when(() => repository.ordersWithQueuedCheckIns(any(), 'farmer-1'))
            .thenAnswer((invocation) async {
          final read = invocation.positionalArguments[0] as List<Order>;
          for (final order in read) {
            order.queuedCheckIns = {
              'crops_ready': QueuedCheckIn(madeAt: DateTime.now().toUtc()),
            };
          }
          return read;
        });

        await pump(tester, 'farmer-1', UserRole.farmer);

        expect(find.text(en.actionCropsReady), findsNothing);
        verify(() => repository.ordersWithQueuedCheckIns(any(), 'farmer-1'))
            .called(1);
      });

      testWidgets('marks that cannot be read: the card still shows',
          (tester) async {
        orders = [_atFarm('farm', now: DateTime.now().toUtc())];
        when(() => repository.ordersWithQueuedCheckIns(any(), any()))
            .thenThrow(StateError('queue closed'));

        await pump(tester, 'farmer-1', UserRole.farmer);

        expect(find.text(en.actionCropsReady), findsOneWidget);
        expect(
          find.byKey(const Key('action_needed_load_failed')),
          findsNothing,
        );
      });
    });

    testWidgets('the card comes when the window opens and goes at the deadline',
        (tester) async {
      var now = DateTime.utc(2026, 11, 19, 5);
      orders = [
        _atFarm(
          'farm',
          pickupDate: DateTime.utc(2026, 11, 20, 6),
          deadline: DateTime.utc(2026, 11, 21, 6),
        ),
      ];
      await pump(tester, 'farmer-1', UserRole.farmer, clock: () => now);
      expect(find.text(en.actionCropsReady), findsNothing);

      // Check-ins open (the pickup day minus 24 hours).
      now = DateTime.utc(2026, 11, 19, 6, 0, 1);
      await tester.pump(const Duration(hours: 1));
      expect(find.text(en.actionCropsReady), findsOneWidget);

      // The deadline passes: no card to be refused on a tap.
      now = DateTime.utc(2026, 11, 21, 6, 0, 5);
      for (var i = 0; i < 4; i++) {
        await tester.pump(const Duration(hours: 12));
      }
      expect(find.text(en.actionCropsReady), findsNothing);
    });

    for (final locale in const [Locale('am'), Locale('om')]) {
      testWidgets('${locale.languageCode}: each check-in card renders',
          (tester) async {
        final l10n = lookupAppLocalizations(locale);
        final now = DateTime.now().toUtc();
        for (final (order, userId, role, label) in [
          (
            _atFarm('farm', now: now),
            'farmer-1',
            UserRole.farmer,
            l10n.actionCropsReady
          ),
          (
            _atFarm('farm', now: now),
            'trucker-1',
            UserRole.trucker,
            l10n.actionCheckInAtFarm
          ),
          (
            _onTheRoad('road', now: now),
            'trucker-1',
            UserRole.trucker,
            l10n.actionCheckInAtDropoff
          ),
          (
            _onTheRoad(
              'road',
              received: now.subtract(const Duration(hours: 1)),
              now: now,
            ),
            'buyer-1',
            UserRole.buyer,
            l10n.actionConfirmDelivery,
          ),
        ]) {
          orders = [order];
          await pump(tester, userId, role, locale: locale);
          expect(tester.takeException(), isNull);
          expect(find.text(label), findsOneWidget);
        }
      });
    }
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/trucker_hero_card.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/home_cards.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

import '../features/live_trip/live_trip_harness.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class _MockInboxBloc extends Mock implements InboxBloc {}

class _MockSyncExecutor extends Mock implements SyncExecutor {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// The Home boards of design canvas page M (FarmerHomeM, TruckerHomeM):
/// white ground for every role, the raised card the page leads with, the
/// black and sand tiles, and the ways to what left the bar (the farmer's
/// Deliveries, the trucker's Payments).
void main() {
  late LiveTripDeps deps;
  late List<int> tabRequests;

  String userIdOf(UserRole role) => switch (role) {
        UserRole.farmer => farmerId,
        UserRole.trucker => truckerId,
        UserRole.buyer => buyerId,
      };

  /// An order that waits for [role]'s answer, due in two hours, or at the
  /// last minute of today in Ethiopia (UTC+3) when two hours on is already
  /// tomorrow there: the card must say "Due today" at any hour the suite
  /// runs.
  Order waitingFor(UserRole role) {
    const ethiopia = Duration(hours: 3);
    final now = DateTime.now().toUtc();
    var due = now.add(const Duration(hours: 2));
    if (due.add(ethiopia).day != now.add(ethiopia).day) {
      due = DateTime.utc(now.year, now.month, now.day, 20, 59);
    }
    return liveOrder(status: OrderStatus.funded)
      ..orderId = 'order-9'
      ..quantity = 1200
      ..buyerName = 'Abebe Trading'
      ..farmerResponse = role == UserRole.farmer ? 'pending' : 'accepted'
      ..truckerResponse = role == UserRole.trucker ? 'pending' : 'not_asked'
      ..farmerDeadlineAt = due
      ..truckerDeadlineAt = due;
  }

  Trip bookedTrip() => Trip()
    ..tripId = 'trip-001'
    ..truckerId = truckerId
    ..truckerName = 'Dawit Tesfaye'
    ..truckerPhone = '+251900100002'
    ..fromWoreda = 'Meki'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = DateTime.utc(2027, 3, 5, 9)
    ..capacity = 50.0
    ..remainingQuintals = 20
    ..pricePerQuintal = 150.0
    ..status = TripStatus.matched
    ..matchedListings = []
    ..createdAt = DateTime(2026, 10, 28)
    ..updatedAt = DateTime(2026, 10, 28)
    ..version = 1;

  setUp(() {
    deps = LiveTripDeps(order: liveOrder());
    when(
      () =>
          deps.orderRepository.getOrdersByUser(any(), role: any(named: 'role')),
    ).thenAnswer((_) async => []);
    when(() => deps.orderRepository.ordersWithQueuedCheckIns(any(), any()))
        .thenAnswer((i) async => i.positionalArguments[0] as List<Order>);
    when(
      () => deps.deliveryRepository.getDeliveriesByUser(
        any(),
        role: any(named: 'role'),
        statusFilter: any(named: 'statusFilter'),
      ),
    ).thenAnswer((_) async => []);
    // The payment history opened from the trucker's Payments row.
    when(() => deps.escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => deps.tripRepository.getMostRelevantTrip(any()))
        .thenAnswer((_) async => bookedTrip());
    when(() => deps.tripRepository.getRecentRoutes(any()))
        .thenAnswer((_) async => []);
  });

  Future<void> pumpHome(
    WidgetTester tester,
    UserRole role, {
    String userName = 'Almaz Bekele',
    double width = 390,
  }) async {
    tester.view.physicalSize = Size(width, 2000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    tabRequests = [];
    final auth = _MockAuthBloc();
    when(() => auth.stream).thenAnswer((_) => const Stream.empty());
    when(() => auth.state).thenReturn(
      AuthAuthenticated(
        userId: userIdOf(role),
        tokens: _tokens,
        role: role,
        userName: userName,
        woreda: 'Meki',
      ),
    );
    final intents = _MockBuyerIntentBloc();
    when(() => intents.state).thenReturn(const BuyerIntentInitial());
    when(() => intents.stream).thenAnswer((_) => const Stream.empty());
    final inbox = _MockInboxBloc();
    when(() => inbox.state).thenReturn(const InboxInitial());
    when(() => inbox.stream).thenAnswer((_) => const Stream.empty());
    final executor = _MockSyncExecutor();
    when(() => executor.onSyncResult).thenAnswer((_) => const Stream.empty());
    when(() => executor.isSyncing).thenReturn(false);

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ...deps.providers(FakeTrackRepository(track: withEta(recentTrack()))),
          Provider<SyncExecutor>.value(value: executor),
          BlocProvider<AuthBloc>.value(value: auth),
          BlocProvider<BuyerIntentBloc>.value(value: intents),
          BlocProvider<InboxBloc>.value(value: inbox),
        ],
        child: MaterialApp(
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          theme: AppTheme.light(role: role),
          home: Scaffold(
            body: HomeScreen(onNavigateToTab: tabRequests.add),
          ),
        ),
      ),
    );
    await settleReads(tester);
    await tester.pump(const Duration(milliseconds: 50));
  }

  Material tileMaterial(WidgetTester tester, String key) =>
      tester.widget<Material>(
        find
            .descendant(
              of: find.byKey(Key(key)),
              matching: find.byType(Material),
            )
            .first,
      );

  /// WCAG contrast ratio between two opaque colours.
  double contrast(Color a, Color b) {
    final la = a.computeLuminance();
    final lb = b.computeLuminance();
    final hi = la > lb ? la : lb;
    final lo = la > lb ? lb : la;
    return (hi + 0.05) / (lo + 0.05);
  }

  test('every role\'s dark step passes 4.5:1 on a sand tile', () {
    for (final role in UserRole.values) {
      final colors = RoleColors.forRole(role);
      expect(
        contrast(colors.dark, AppTheme.sand),
        greaterThanOrEqualTo(4.5),
        reason: role.name,
      );
    }
    // Why the amount is not in the role colour itself: plum on sand.
    expect(
      contrast(RoleColors.forRole(UserRole.farmer).primary, AppTheme.sand),
      lessThan(4.5),
    );
  });

  for (final role in [UserRole.farmer, UserRole.trucker]) {
    testWidgets(
        'the ${role.name}\'s escrow amount on its sand tile is the dark '
        'role colour', (tester) async {
      await pumpHome(tester, role);

      final tile = find.byKey(const Key('home_tile_payments'));
      expect(tileMaterial(tester, 'home_tile_payments').color, AppTheme.sand);
      final value = tester.widget<Text>(
        find.descendant(
          of: find.descendant(of: tile, matching: find.byType(FittedBox)),
          matching: find.byType(Text),
        ),
      );
      // The tile's value (a dash here: this harness has no stats to read).
      expect(value.style!.color, RoleColors.forRole(role).dark);
      expect(
        contrast(value.style!.color!, AppTheme.sand),
        greaterThanOrEqualTo(4.5),
      );
    });
  }

  test('homeFirstName is the first word of the name', () {
    expect(homeFirstName('Almaz Bekele'), 'Almaz');
    expect(homeFirstName('  Dawit   Tesfaye '), 'Dawit');
    expect(homeFirstName('Selam'), 'Selam');
    expect(homeFirstName(''), '');
    expect(homeFirstName('   '), '');
  });

  group('Farmer home', () {
    testWidgets(
        'header: the place and the Synced pill, the bell, "Welcome back," '
        'small and the first name at 42 px beside the farmer sketch',
        (tester) async {
      await pumpHome(tester, UserRole.farmer);

      expect(find.textContaining('Meki'), findsOneWidget);
      expect(find.byKey(const Key('home_sync_chip_label')), findsOneWidget);
      expect(find.byType(NotificationBell), findsOneWidget);
      expect(find.text('Welcome back,'), findsOneWidget);
      final name = tester.widget<Text>(find.text('Almaz'));
      expect(name.style!.fontSize, 42);
      expect(find.textContaining('Bekele'), findsNothing);
      expect(
        tester.widget<Text>(find.text('Welcome back,')).style!.fontSize,
        13,
      );
      final sketch = tester.widget<RoleSketch>(find.byType(RoleSketch));
      expect(sketch.role, UserRole.farmer);
      // The sketch sits beside the name, never under it.
      final nameRect = tester.getRect(find.text('Almaz'));
      final sketchRect = tester.getRect(find.byType(RoleSketch));
      expect(sketchRect.left, greaterThan(nameRect.right));
      expect(sketchRect.overlaps(nameRect), isFalse);
    });

    testWidgets('the action card is raised, with the orange pill and Open',
        (tester) async {
      when(
        () => deps.orderRepository
            .getOrdersByUser(any(), role: any(named: 'role')),
      ).thenAnswer((_) async => [waitingFor(UserRole.farmer)]);
      await pumpHome(tester, UserRole.farmer);

      final card = tester.widget<FwCard>(
        find.byKey(const Key('action_needed_card')),
      );
      expect(card.raised, isTrue);
      expect(card.color, AppTheme.paper);
      expect(find.byType(HomeActionPill), findsOneWidget);
      expect(find.text('ACTION NEEDED'), findsOneWidget);
      expect(find.text('Answer the new order'), findsOneWidget);
      expect(find.text('Abebe Trading · Teff · 1,200 kg'), findsNothing);
      expect(find.text('Abebe Trading · Wheat · 1,200 kg'), findsOneWidget);
      expect(find.textContaining('Due today'), findsOneWidget);
      final open = tester.widget<HomeOpenMark>(find.byType(HomeOpenMark));
      expect(open.pill, isTrue);
      expect(find.text('Open'), findsOneWidget);
    });

    testWidgets(
        'My Crops is the black tile, the others sand; Escrow says who '
        'holds it until when; Deliveries carries a link', (tester) async {
      await pumpHome(tester, UserRole.farmer);

      expect(tileMaterial(tester, 'home_tile_myCrops').color, AppTheme.ink);
      for (final key in [
        'home_tile_orders',
        'home_tile_payments',
        'home_tile_deliveries',
      ]) {
        expect(tileMaterial(tester, key).color, AppTheme.sand, reason: key);
      }
      expect(
        find.text('held for you until the buyer confirms delivery'),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('home_tile_deliveries')),
          matching: find.text('Track Delivery'),
        ),
        findsOneWidget,
      );
      // Two by two.
      final crops = tester.getRect(find.byKey(const Key('home_tile_myCrops')));
      final orders = tester.getRect(find.byKey(const Key('home_tile_orders')));
      final escrow =
          tester.getRect(find.byKey(const Key('home_tile_payments')));
      expect(crops.top, orders.top);
      expect(escrow.top, greaterThan(crops.bottom));
      expect(escrow.left, crops.left);
    });

    testWidgets(
        'the Deliveries tile opens the pushed Deliveries page: Deliveries '
        'is no bar tab for the farmer', (tester) async {
      await pumpHome(tester, UserRole.farmer);

      await tester.tap(find.byKey(const Key('home_tile_deliveries')));
      await settleReads(tester);
      await tester.pump(const Duration(milliseconds: 400));

      expect(find.byType(DeliveriesPage), findsOneWidget);
      expect(tabRequests, isEmpty);
    });

    testWidgets('My Crops and Pending Orders still switch to their tabs',
        (tester) async {
      await pumpHome(tester, UserRole.farmer);

      await tester.tap(find.byKey(const Key('home_tile_myCrops')));
      await tester.tap(find.byKey(const Key('home_tile_orders')));
      await tester.pump();

      // Farmer bar: Home, My Crops, Orders.
      expect(tabRequests, [1, 2]);
    });

    testWidgets('the goods on the road show on the sand card with its pill',
        (tester) async {
      when(
        () => deps.deliveryRepository.getDeliveriesByUser(
          any(),
          role: any(named: 'role'),
          statusFilter: any(named: 'statusFilter'),
        ),
      ).thenAnswer((_) async => [liveDelivery()]);
      await pumpHome(tester, UserRole.farmer);

      expect(find.byType(OnTheRoadCard), findsOneWidget);
      expect(find.text('Your Wheat is on the road'), findsOneWidget);
      expect(find.widgetWithText(FwPill, 'In Transit'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(OnTheRoadCard),
          matching: find.byType(RouteRibbon),
        ),
        findsOneWidget,
      );
      final card = tester.widget<FwCard>(
        find.descendant(
          of: find.byType(OnTheRoadCard),
          matching: find.byType(FwCard),
        ),
      );
      expect(card.color, AppTheme.sand);
      expect(card.raised, isFalse);
    });
  });

  group('Trucker home', () {
    testWidgets(
        'white ground: the greeting small, "Today" at 42 px in black, the '
        'truck sketch, the account button and no bell', (tester) async {
      await pumpHome(tester, UserRole.trucker, userName: 'Dawit Tesfaye');

      expect(find.text('Welcome back, Dawit'), findsOneWidget);
      final today = tester.widget<Text>(find.text('Today'));
      expect(today.style!.fontSize, 42);
      expect(today.style!.color, AppTheme.ink);
      expect(
        tester.widget<RoleSketch>(find.byType(RoleSketch)).role,
        UserRole.trucker,
      );
      expect(find.byType(AccountButton), findsOneWidget);
      expect(find.byType(NotificationBell), findsNothing);
      // No dark header behind the card any more.
      expect(
        find.byWidgetPredicate(
          (w) =>
              w is Container &&
              w.decoration is BoxDecoration &&
              (w.decoration! as BoxDecoration).color == AppTheme.ink,
        ),
        findsNothing,
      );
    });

    testWidgets(
        'the trip card leads, raised, in kilograms; the action card '
        'follows it flat on the orange tint', (tester) async {
      when(
        () => deps.orderRepository
            .getOrdersByUser(any(), role: any(named: 'role')),
      ).thenAnswer((_) async => [waitingFor(UserRole.trucker)]);
      await pumpHome(tester, UserRole.trucker, userName: 'Dawit Tesfaye');

      expect(find.text('Meki → Addis Ababa'), findsOneWidget);
      expect(find.text('3,000 of 5,000 kg loaded'), findsOneWidget);
      expect(find.text('(30 of 50 quintals)'), findsOneWidget);
      final hero = tester.widget<FwCard>(
        find.descendant(
          of: find.byType(TruckerHeroCard),
          matching: find.byType(FwCard),
        ),
      );
      expect(hero.raised, isTrue);

      final action = tester.widget<FwCard>(
        find.byKey(const Key('action_needed_card')),
      );
      expect(action.raised, isFalse);
      expect(action.color, AppTheme.orangeTint);
      expect(find.text('Answer the transport request'), findsOneWidget);
      expect(
        tester.getRect(find.byType(ActionNeededList)).top,
        greaterThan(tester.getRect(find.byType(TruckerHeroCard)).bottom),
      );
    });

    testWidgets('three sand tiles in one row', (tester) async {
      await pumpHome(tester, UserRole.trucker, userName: 'Dawit Tesfaye');

      final tiles = find.byKey(const Key('home_tile_myTrips'));
      // My Trips and Matched open the same tab.
      expect(tiles, findsNWidgets(2));
      expect(find.byKey(const Key('home_tile_payments')), findsOneWidget);
      expect(
        tester.getRect(tiles.first).top,
        tester.getRect(find.byKey(const Key('home_tile_payments'))).top,
      );
      expect(tileMaterial(tester, 'home_tile_payments').color, AppTheme.sand);
    });

    testWidgets(
        'the Payments row opens the payment history: Payments is no bar '
        'tab for the trucker', (tester) async {
      await pumpHome(tester, UserRole.trucker, userName: 'Dawit Tesfaye');

      final row = find.byKey(const Key('home_payments_row'));
      expect(row, findsOneWidget);
      expect(
        find.descendant(of: row, matching: find.text('Payments')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: row, matching: find.text('Open')),
        findsOneWidget,
      );
      expect(tester.getSize(row).height, greaterThanOrEqualTo(56));

      await tester.tap(row);
      await settleReads(tester);
      await tester.pump(const Duration(milliseconds: 400));

      expect(find.byType(PaymentHistoryScreen), findsOneWidget);
      expect(tabRequests, isEmpty);
    });

    testWidgets('no Payments row for the farmer', (tester) async {
      await pumpHome(tester, UserRole.farmer);

      expect(find.byKey(const Key('home_payments_row')), findsNothing);
    });
  });

  testWidgets('the buyer branch still renders, on the farmer pattern',
      (tester) async {
    await pumpHome(tester, UserRole.buyer);

    expect(find.text('Almaz'), findsOneWidget);
    expect(find.text('Find Crops'), findsOneWidget);
    expect(find.text('Find Trips'), findsOneWidget);
    expect(
      tester.widget<RoleSketch>(find.byType(RoleSketch)).role,
      UserRole.buyer,
    );
    expect(find.byKey(const Key('home_payments_row')), findsNothing);
  });

  for (final role in UserRole.values) {
    testWidgets('${role.name}: nothing overflows at 320 px and 2x text',
        (tester) async {
      when(
        () => deps.orderRepository
            .getOrdersByUser(any(), role: any(named: 'role')),
      ).thenAnswer((_) async => [waitingFor(role)]);
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await pumpHome(tester, role, width: 320);

      expect(tester.takeException(), isNull);
    });
  }
}

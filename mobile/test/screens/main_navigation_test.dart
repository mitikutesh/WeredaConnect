import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/main_navigation.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockConflictBloc extends Mock implements ConflictBloc {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripListingRepository extends Mock
    implements BaseTripListingRepository {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

/// Counts its builds: a tab body the create button's folding must not
/// rebuild.
class _CountedBody extends StatelessWidget {
  final ValueNotifier<int> builds;
  final Widget child;

  const _CountedBody({required this.builds, required this.child});

  @override
  Widget build(BuildContext context) {
    builds.value++;
    return child;
  }
}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// LT-01: the labeled create FAB floats above the bottom bar instead of
/// docking into a gap it was far wider than, so it never covers a tab.
void main() {
  late MockAuthBloc authBloc;
  late MockConflictBloc conflictBloc;
  late MockConnectivityMonitor monitor;

  setUp(() {
    authBloc = MockAuthBloc();
    conflictBloc = MockConflictBloc();
    monitor = MockConnectivityMonitor();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => conflictBloc.state).thenReturn(const ConflictInitial());
    when(() => conflictBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => monitor.isConnected()).thenAnswer((_) async => true);
    when(() => monitor.onConnectivityChanged)
        .thenAnswer((_) => const Stream.empty());
  });

  Widget buildApp(
    UserRole role, {
    Locale locale = const Locale('en'),
    // A tab's body; the default stand-in where it returns null.
    Widget? Function(RoleTab tab)? tabBody,
  }) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: 'u-1', tokens: _tokens, role: role),
    );
    return Provider<ConnectivityMonitor>.value(
      value: monitor,
      child: MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<ConflictBloc>.value(value: conflictBloc),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: locale,
          // The app's delegates: Material has no om, so it falls back.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: MainNavigation(
            tabBodyBuilder: (tab) =>
                tabBody?.call(tab) ?? Center(child: Text('body ${tab.name}')),
          ),
        ),
      ),
    );
  }

  void useSize(WidgetTester tester, Size size) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
  }

  Finder tabItem(String label) =>
      find.ancestor(of: find.text(label), matching: find.byType(InkWell)).first;

  const fabLabels = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  const tabLabels = {
    RoleTab.home: 'Home',
    RoleTab.myCrops: 'My Crops',
    RoleTab.myTrips: 'My Trips',
    RoleTab.market: 'Market',
    RoleTab.orders: 'Orders',
    RoleTab.deliveries: 'Deliveries',
    RoleTab.payments: 'Payments',
  };

  for (final size in const [Size(360, 640), Size(390, 844)]) {
    final width = size.width.toInt();
    for (final role in UserRole.values) {
      testWidgets('$width dp ${role.name}: the FAB covers no tab',
          (tester) async {
        useSize(tester, size);
        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();

        expect(find.text(fabLabels[role]!), findsOneWidget);
        final fab = tester.getRect(find.byType(FloatingActionButton));
        final bar = tester.getRect(find.byType(RoleNavBar));
        expect(fab.bottom, lessThanOrEqualTo(bar.top));
        expect(
          bar.top - fab.top,
          lessThanOrEqualTo(Breakpoints.fabClearance),
          reason: 'tab bodies pad by fabClearance; it must cover the FAB',
        );

        final tabs = tabsForRole(role);
        final rects = [
          for (final tab in tabs) tester.getRect(tabItem(tabLabels[tab]!)),
        ];
        for (var i = 0; i < tabs.length; i++) {
          expect(
            rects[i].overlaps(fab),
            isFalse,
            reason: '${tabs[i].name} hit area overlaps the FAB',
          );
          expect(rects[i].width, moreOrLessEquals(rects.first.width));
        }

        // Every tab still takes its own tap: none lands on the FAB.
        for (final tab in tabs.reversed) {
          await tester.tap(find.text(tabLabels[tab]!));
          await tester.pumpAndSettle();
          expect(find.text('body ${tab.name}'), findsOneWidget);
        }
      });
    }
  }

  for (final locale in const [Locale('am'), Locale('om')]) {
    for (final role in UserRole.values) {
      testWidgets(
          '360 dp ${role.name} in ${locale.languageCode}: '
          'labels fit their tab', (tester) async {
        useSize(tester, const Size(360, 640));
        await tester.pumpWidget(buildApp(role, locale: locale));
        await tester.pumpAndSettle();

        final context = tester.element(find.byType(MainNavigation));
        final l10n = AppLocalizations.of(context)!;
        for (final tab in tabsForRole(role)) {
          final label = tab.label(l10n);
          final slot = tester.getRect(tabItem(label));
          final text = tester.getRect(find.text(label));
          expect(slot.left, lessThanOrEqualTo(text.left));
          expect(text.right, lessThanOrEqualTo(slot.right));
        }
        expect(tester.takeException(), isNull);
      });
    }
  }

  // FUP-75: the shell shows the update banner over every role's tabs. The
  // flag is app-wide, so each test resets it after itself.
  group('update banner', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));
    tearDown(SecurityEvents.instance.resetAppUpdateRequiredForTesting);

    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      for (final role in UserRole.values) {
        testWidgets(
            '360 dp ${role.name} in ${locale.languageCode}: above every tab, '
            'tabs still work', (tester) async {
          useSize(tester, const Size(360, 640));
          SecurityEvents.instance.reportAppUpdateRequired();
          await tester.pumpWidget(buildApp(role, locale: locale));
          await tester.pumpAndSettle();

          final context = tester.element(find.byType(MainNavigation));
          final l10n = AppLocalizations.of(context)!;
          for (final tab in tabsForRole(role).reversed) {
            await tester.tap(tabItem(tab.label(l10n)));
            await tester.pumpAndSettle();
            final body = find.text('body ${tab.name}');
            expect(body, findsOneWidget);
            expect(find.text(l10n.appUpdateBannerMessage), findsOneWidget);
            expect(
              // What shows of it: long text scrolls inside.
              tester
                  .getRect(
                    find.ancestor(
                      of: find.byType(MaterialBanner),
                      matching: find.byType(SingleChildScrollView),
                    ),
                  )
                  .bottom,
              lessThanOrEqualTo(tester.getRect(body).top),
            );
          }
          expect(tester.takeException(), isNull);
        });
      }
    }
  });

  testWidgets('after those, no banner: the reset left nothing behind',
      (tester) async {
    SharedPreferences.setMockInitialValues({});
    useSize(tester, const Size(360, 640));
    await tester.pumpWidget(buildApp(UserRole.buyer));
    await tester.pumpAndSettle();

    expect(SecurityEvents.instance.appUpdateRequired, isFalse);
    expect(find.byType(MaterialBanner), findsNothing);
  });

  // BUG-27: at rest the labelled create button covered the status chip of
  // the last card in view. It folds to its plus while the tab scrolls
  // toward its end, and unfolds when the tab scrolls back or opens.
  group('the create button folds while the tab scrolls', () {
    final fab = find.byType(FloatingActionButton);

    // Folded, the plain 56 dp square; unfolded, the labelled pill.
    void expectFolded(WidgetTester tester, {required bool folded}) {
      final width = tester.getSize(fab).width;
      expect(width, folded ? moreOrLessEquals(56) : greaterThan(100));
    }

    // Folded or not, a screen reader and a long press get the full name.
    void expectNamed(String name) {
      expect(find.bySemanticsLabel(name), findsOneWidget);
      expect(find.byTooltip(name), findsOneWidget);
    }

    Future<void> scroll(WidgetTester tester, Finder target, double dy) async {
      await tester.drag(target, Offset(0, dy));
      await tester.pumpAndSettle();
    }

    for (final role in UserRole.values) {
      testWidgets(
          '${role.name}: folds going down, unfolds going up and on another '
          'tab, and the list is never rebuilt', (tester) async {
        useSize(tester, const Size(390, 844));
        final semantics = tester.ensureSemantics();
        final builds = ValueNotifier<int>(0);
        await tester.pumpWidget(
          buildApp(
            role,
            tabBody: (tab) => tab != RoleTab.home
                ? null
                : _CountedBody(
                    builds: builds,
                    child: ListView.builder(
                      key: const ValueKey('home list'),
                      padding: const EdgeInsets.only(
                        bottom: Breakpoints.fabClearance,
                      ),
                      itemCount: 60,
                      itemBuilder: (_, i) =>
                          SizedBox(height: 64, child: Text('row $i')),
                    ),
                  ),
          ),
        );
        await tester.pumpAndSettle();
        final name = fabLabels[role]!;
        final list = find.byKey(const ValueKey('home list'));
        final settledBuilds = builds.value;

        expectFolded(tester, folded: false);
        expectNamed(name);

        await scroll(tester, list, -300);
        expectFolded(tester, folded: true);
        expectNamed(name);

        await scroll(tester, list, 100);
        expectFolded(tester, folded: false);

        await scroll(tester, list, -200);
        expectFolded(tester, folded: true);
        // A finger's tremor back does not unfold it.
        await scroll(tester, list, 6);
        expectFolded(tester, folded: true);
        // Only the button followed the scrolling, never the list.
        expect(builds.value, settledBuilds);

        // Back at the very top, it unfolds.
        await tester.fling(list, const Offset(0, 2000), 3000);
        await tester.pumpAndSettle();
        expectFolded(tester, folded: false);
        await scroll(tester, list, -300);
        expectFolded(tester, folded: true);

        // Another tab opens at its top, with the label.
        await tester.tap(tabItem(tabLabels[tabsForRole(role)[1]]!));
        await tester.pumpAndSettle();
        expectFolded(tester, folded: false);
        expect(tester.takeException(), isNull);
        semantics.dispose();
      });
    }

    Order order(int i) => Order()
      ..orderId = 'order-$i'
      ..listingId = 'listing-$i'
      ..buyerId = 'buyer-00$i'
      ..farmerId = 'u-1'
      ..buyerName = 'Buyer $i'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 100
      ..pricePerUnit = 28
      ..totalPrice = 2800
      ..cropCost = 2800
      ..status = OrderStatus.created
      ..createdAt = DateTime.utc(2026, 11, 19)
      ..version = 1;

    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      testWidgets(
          'farmer Orders in ${locale.languageCode}: the pill at rest, the '
          'named plus scrolling down, the pill again scrolling up',
          (tester) async {
        useSize(tester, const Size(390, 844));
        final semantics = tester.ensureSemantics();
        final orderBloc = MockOrderBloc();
        when(() => orderBloc.state).thenReturn(
          OrdersLoaded(orders: [for (var i = 0; i < 12; i++) order(i)]),
        );
        when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
        await tester.pumpWidget(
          BlocProvider<OrderBloc>.value(
            value: orderBloc,
            child: buildApp(
              UserRole.farmer,
              locale: locale,
              tabBody: (tab) =>
                  tab == RoleTab.orders ? const OrdersTabScreen() : null,
            ),
          ),
        );
        await tester.pumpAndSettle();
        final l10n = lookupAppLocalizations(locale);
        await tester.tap(tabItem(RoleTab.orders.label(l10n)));
        await tester.pumpAndSettle();
        final name = l10n.sellCrops;
        // The list's middle, under the header and the pills.
        final list = find.byType(RefreshIndicator);
        expect(find.byType(OrderCard), findsWidgets);

        expectFolded(tester, folded: false);
        expectNamed(name);

        // The status pills scroll sideways: not the list.
        await tester.drag(
          find.byWidgetPredicate((w) => w is FwFilterPillRow),
          const Offset(-200, 0),
        );
        await tester.pumpAndSettle();
        expectFolded(tester, folded: false);

        await scroll(tester, list, -300);
        expectFolded(tester, folded: true);
        expectNamed(name);

        await scroll(tester, list, 120);
        expectFolded(tester, folded: false);
        expectNamed(name);
        expect(tester.takeException(), isNull);
        semantics.dispose();
      });
    }

    testWidgets(
        'buyer Home: the pill at rest, the named plus scrolling down, the '
        'pill again scrolling up', (tester) async {
      useSize(tester, const Size(360, 640));
      final semantics = tester.ensureSemantics();
      final buyerIntentBloc = MockBuyerIntentBloc();
      final cropListingBloc = MockCropListingBloc();
      final inboxBloc = MockInboxBloc();
      final tripListingBloc = MockTripListingBloc();
      final tripRepository = MockTripListingRepository();
      final syncExecutor = MockSyncExecutor();
      when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentInitial());
      when(() => buyerIntentBloc.stream)
          .thenAnswer((_) => const Stream.empty());
      when(() => cropListingBloc.state)
          .thenReturn(const CropListingsLoaded(listings: []));
      when(() => cropListingBloc.stream)
          .thenAnswer((_) => const Stream.empty());
      when(() => inboxBloc.state).thenReturn(const InboxInitial());
      when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
      when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
      when(() => tripListingBloc.stream)
          .thenAnswer((_) => const Stream.empty());
      when(() => tripListingBloc.repository).thenReturn(tripRepository);
      when(() => syncExecutor.onSyncResult)
          .thenAnswer((_) => const Stream.empty());
      when(() => syncExecutor.isSyncing).thenReturn(false);

      await tester.pumpWidget(
        Provider<SyncExecutor>.value(
          value: syncExecutor,
          child: MultiBlocProvider(
            providers: [
              BlocProvider<BuyerIntentBloc>.value(value: buyerIntentBloc),
              BlocProvider<CropListingBloc>.value(value: cropListingBloc),
              BlocProvider<InboxBloc>.value(value: inboxBloc),
              BlocProvider<TripListingBloc>.value(value: tripListingBloc),
            ],
            child: buildApp(
              UserRole.buyer,
              tabBody: (tab) => tab == RoleTab.home
                  ? HomeScreen(onNavigateToTab: (_) {})
                  : null,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final name = fabLabels[UserRole.buyer]!;
      final home = find.byType(HomeScreen);

      expectFolded(tester, folded: false);
      expectNamed(name);

      await scroll(tester, home, -300);
      expectFolded(tester, folded: true);
      expectNamed(name);

      await scroll(tester, home, 120);
      expectFolded(tester, folded: false);
      expectNamed(name);
      expect(tester.takeException(), isNull);
      semantics.dispose();
    });
  });
}

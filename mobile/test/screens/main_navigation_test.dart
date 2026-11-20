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

/// The phone shell (canvas page M): three tabs and the role's create
/// button in one black bar. Nothing floats over the tab's page any more
/// (LT-01, BUG-27: the floating button covered a tab, then the last
/// card's status chip), so the button covers no tab and no content.
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
          theme: AppTheme.light(role: role),
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

  // The create verb's full name: its semantics label and tooltip.
  const createNames = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  // The one word the bar shows.
  const createWords = {
    UserRole.farmer: 'Sell',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'Buy',
  };

  const tabLabels = {
    RoleTab.home: 'Home',
    RoleTab.myCrops: 'My Crops',
    RoleTab.myTrips: 'My Trips',
    RoleTab.market: 'Market',
    RoleTab.orders: 'Orders',
    RoleTab.deliveries: 'Deliveries',
  };

  final createButton = find.byType(RoleCreateButton);

  /// The create button's word, folded shut (width 0) or showing.
  double createWordWidth(WidgetTester tester) => tester
      .getSize(
        find.descendant(of: createButton, matching: find.byType(ClipRect)),
      )
      .width;

  for (final role in UserRole.values) {
    testWidgets('${role.name}: lands on the first tab of three',
        (tester) async {
      useSize(tester, const Size(390, 844));
      await tester.pumpWidget(buildApp(role));
      await tester.pumpAndSettle();

      final tabs = tabsForRole(role);
      expect(tabs, hasLength(3));
      expect(find.byType(RoleNavItem), findsNWidgets(3));
      expect(
        tester
            .widgetList<RoleNavItem>(find.byType(RoleNavItem))
            .map((item) => item.label),
        [for (final tab in tabs) tabLabels[tab]],
      );
      // A buyer has no Home: Deliveries is where they land.
      expect(find.text('body ${tabs.first.name}'), findsOneWidget);
      expect(
        tester.widget<RoleNavItem>(find.byType(RoleNavItem).first).selected,
        isTrue,
      );
      if (role == UserRole.buyer) {
        expect(find.text('Home'), findsNothing);
        expect(find.text('body deliveries'), findsOneWidget);
      }
    });
  }

  for (final size in const [Size(360, 640), Size(390, 844)]) {
    final width = size.width.toInt();
    for (final role in UserRole.values) {
      testWidgets(
          '$width dp ${role.name}: the create button sits in the bar, over '
          'no tab and no content', (tester) async {
        useSize(tester, size);
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);

        // Nothing floats: the button is the bar's last slot.
        expect(find.byType(FloatingActionButton), findsNothing);
        expect(createButton, findsOneWidget);
        expect(
          find.descendant(of: find.byType(RoleNavBar), matching: createButton),
          findsOneWidget,
        );
        expect(
          find.descendant(
            of: createButton,
            matching: find.text(createWords[role]!),
          ),
          findsOneWidget,
        );
        expect(find.bySemanticsLabel(createNames[role]!), findsOneWidget);
        expect(find.byTooltip(createNames[role]!), findsOneWidget);

        final create = tester.getRect(createButton);
        final bar = tester.getRect(find.byType(RoleNavBar));
        expect(bar.bottom, size.height);
        expect(create.top, greaterThanOrEqualTo(bar.top));
        expect(create.bottom, lessThanOrEqualTo(bar.bottom));
        expect(create.right, lessThanOrEqualTo(bar.right));
        expect(create.height, greaterThanOrEqualTo(48));
        expect(create.width, greaterThanOrEqualTo(48));

        // The tab's page ends where the bar begins: the button covers
        // none of it.
        final tabs = tabsForRole(role);
        final page = tester.getRect(
          find.ancestor(
            of: find.text('body ${tabs.first.name}'),
            matching: find.byType(CreateButtonScrollBody),
          ),
        );
        expect(page.bottom, lessThanOrEqualTo(bar.top));
        expect(page.overlaps(create), isFalse);

        final rects = [
          for (final tab in tabs) tester.getRect(tabItem(tabLabels[tab]!)),
        ];
        for (var i = 0; i < tabs.length; i++) {
          expect(
            rects[i].overlaps(create),
            isFalse,
            reason: '${tabs[i].name} hit area overlaps the create button',
          );
          expect(rects[i].right, lessThanOrEqualTo(create.left + 0.01));
          expect(rects[i].width, moreOrLessEquals(rects.first.width));
          expect(rects[i].width, greaterThanOrEqualTo(48));
          expect(rects[i].height, greaterThanOrEqualTo(48));
        }

        // Every tab still takes its own tap: none lands on the button.
        for (final tab in tabs.reversed) {
          await tester.tap(find.text(tabLabels[tab]!));
          await tester.pumpAndSettle();
          expect(find.text('body ${tab.name}'), findsOneWidget);
        }
        semantics.dispose();
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

  // BUG-27: the floating create button covered the status chip of the
  // last card in view, and folded to its plus while the tab scrolled to
  // get out of the way. In the bar it covers nothing, so it keeps its
  // word and its place however the tab scrolls.
  group('the create button stays put while the tab scrolls', () {
    // The test font is twice as wide as the app's: on a large phone the
    // farmer's "Sell" has room beside the tabs, as it has on every phone
    // in the app's own font (word_fit_labels_test).
    const wide = Size(430, 932);

    // A screen reader and a long press get the full name.
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
          '${role.name}: same button going down, going up and on another '
          'tab, and the list is never rebuilt', (tester) async {
        useSize(tester, wide);
        final semantics = tester.ensureSemantics();
        final builds = ValueNotifier<int>(0);
        final landing = tabsForRole(role).first;
        await tester.pumpWidget(
          buildApp(
            role,
            tabBody: (tab) => tab != landing
                ? null
                : _CountedBody(
                    builds: builds,
                    child: ListView.builder(
                      key: const ValueKey('landing list'),
                      itemCount: 60,
                      itemBuilder: (_, i) =>
                          SizedBox(height: 64, child: Text('row $i')),
                    ),
                  ),
          ),
        );
        await tester.pumpAndSettle();
        final name = createNames[role]!;
        final list = find.byKey(const ValueKey('landing list'));
        final settledBuilds = builds.value;
        final atRest = tester.getRect(createButton);
        final wordAtRest = createWordWidth(tester);
        final bar = tester.getRect(find.byType(RoleNavBar));
        if (role == UserRole.farmer) expect(wordAtRest, greaterThan(0));

        void expectUnchanged() {
          expect(tester.getRect(createButton), atRest);
          expect(createWordWidth(tester), wordAtRest);
          expect(tester.getRect(find.byType(RoleNavBar)), bar);
          expect(find.byType(FloatingActionButton), findsNothing);
          expectNamed(name);
        }

        expectUnchanged();
        await scroll(tester, list, -300);
        expectUnchanged();
        await scroll(tester, list, 100);
        expectUnchanged();
        await scroll(tester, list, -200);
        expectUnchanged();
        // The list scrolled under the bar, never under the button: its
        // last row in view ends at the bar.
        expect(tester.getRect(list).bottom, lessThanOrEqualTo(bar.top));
        expect(builds.value, settledBuilds);

        await tester.fling(list, const Offset(0, 2000), 3000);
        await tester.pumpAndSettle();
        expectUnchanged();

        // Another tab: the same button in the same place.
        await tester.tap(tabItem(tabLabels[tabsForRole(role)[1]]!));
        await tester.pumpAndSettle();
        expectUnchanged();
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
          'farmer Orders in ${locale.languageCode}: the named button stays '
          'while the cards scroll, and covers none of them', (tester) async {
        useSize(tester, wide);
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
        final atRest = tester.getRect(createButton);
        final wordAtRest = createWordWidth(tester);
        final bar = tester.getRect(find.byType(RoleNavBar));

        void expectUnchanged() {
          expect(tester.getRect(createButton), atRest);
          expect(createWordWidth(tester), wordAtRest);
          expectNamed(name);
          // BUG-27: the page, and so every card and its status chip,
          // ends at the bar; the button is inside the bar.
          expect(
            tester.getRect(find.byType(OrdersTabScreen)).bottom,
            lessThanOrEqualTo(bar.top),
          );
          expect(atRest.top, greaterThanOrEqualTo(bar.top));
        }

        expectUnchanged();

        // The status pills scroll sideways: not the list.
        await tester.drag(
          find.byWidgetPredicate((w) => w is FwFilterPillRow),
          const Offset(-200, 0),
        );
        await tester.pumpAndSettle();
        expectUnchanged();

        await scroll(tester, list, -300);
        expectUnchanged();

        await scroll(tester, list, 120);
        expectUnchanged();
        expect(tester.takeException(), isNull);
        semantics.dispose();
      });
    }

    testWidgets(
        'farmer Home: the named button stays while the real Home scrolls',
        (tester) async {
      useSize(tester, wide);
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
              UserRole.farmer,
              tabBody: (tab) => tab == RoleTab.home
                  ? HomeScreen(onNavigateToTab: (_) {})
                  : null,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final name = createNames[UserRole.farmer]!;
      final home = find.byType(HomeScreen);
      final atRest = tester.getRect(createButton);
      final wordAtRest = createWordWidth(tester);
      final bar = tester.getRect(find.byType(RoleNavBar));
      expect(wordAtRest, greaterThan(0));

      void expectUnchanged() {
        expect(tester.getRect(createButton), atRest);
        expect(createWordWidth(tester), wordAtRest);
        expect(tester.getRect(home).bottom, lessThanOrEqualTo(bar.top));
        expectNamed(name);
      }

      expectUnchanged();
      await scroll(tester, home, -300);
      expectUnchanged();
      await scroll(tester, home, 120);
      expectUnchanged();
      expect(tester.takeException(), isNull);
      semantics.dispose();
    });
  });
}

import 'dart:ui' show Tristate;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/adaptive_shell.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/app_update_banner.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// The web shell (canvas page M). On a phone width the role's create
/// button is the last slot of the black bar; on a tablet it floats beside
/// the rail; on a desktop it sits in the sidebar. SIM-44: it shows on tab
/// roots only, never over a pushed screen whose own Send / Save / Place
/// Order buttons it would cover.
void main() {
  late MockAuthBloc authBloc;
  late MockInboxBloc inboxBloc;

  setUp(() {
    authBloc = MockAuthBloc();
    inboxBloc = MockInboxBloc();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildApp(
    UserRole role, {
    String userName = '',
    String? woreda,
    Locale? locale,
    SecurityEvents? updateEvents,
    // The Orders tab's page; a line of text by default.
    Widget? ordersPage,
  }) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: 'u-1',
        tokens: _tokens,
        role: role,
        userName: userName,
        woreda: woreda,
      ),
    );
    final pushedRoutes = ShellPushedRoutesObserver();
    // The role's landing tab: Home, or Deliveries for a buyer, who has no
    // Home. Its page opens a detail screen.
    Widget landingPage(BuildContext context, GoRouterState state) => Builder(
          builder: (context) => Center(
            child: TextButton(
              onPressed: () => Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => const Scaffold(
                    body: Center(child: Text('pushed detail')),
                  ),
                ),
              ),
              child: const Text('open detail'),
            ),
          ),
        );
    final router = GoRouter(
      // As the app's redirect leaves a signed-in user.
      initialLocation: landingRouteForRole(role),
      routes: [
        ShellRoute(
          observers: [pushedRoutes],
          // With [updateEvents], as the web router mounts it (FUP-75).
          builder: (context, state, child) => AdaptiveShell(
            pushedRoutes: pushedRoutes,
            child: updateEvents == null
                ? child
                : AppUpdateBanner(
                    events: updateEvents,
                    isWeb: true,
                    reloadWeb: () async => true,
                    child: child,
                  ),
          ),
          routes: [
            GoRoute(path: '/', builder: landingPage),
            GoRoute(path: '/deliveries', builder: landingPage),
            GoRoute(
              path: '/trips',
              builder: (context, state) => const Text('trips'),
            ),
            GoRoute(
              path: '/profile',
              builder: (context, state) => const Text('profile'),
            ),
            GoRoute(
              path: '/orders',
              builder: (context, state) => ordersPage ?? const Text('orders'),
              routes: [
                GoRoute(
                  path: ':orderId',
                  builder: (context, state) => const Text('order detail'),
                ),
              ],
            ),
          ],
        ),
      ],
    );
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<InboxBloc>.value(value: inboxBloc),
      ],
      child: MaterialApp.router(
        theme: AppTheme.light(role: role),
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        routerConfig: router,
      ),
    );
  }

  // The create verb's full name: the sidebar button's text, and on the
  // bar's short pill its semantics label and tooltip.
  const createNames = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  // The one word the bar's pill shows.
  const createWords = {
    UserRole.farmer: 'Sell',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'Buy',
  };

  final createButton = find.byType(RoleCreateButton);

  /// The create pill's word, folded shut (width 0) or showing.
  double createWordWidth(WidgetTester tester) => tester
      .getSize(
        find.descendant(of: createButton, matching: find.byType(ClipRect)),
      )
      .width;

  for (final size in const [Size(400, 800), Size(1300, 900)]) {
    final layout = size.width < 600 ? 'mobile' : 'desktop';
    for (final role in UserRole.values) {
      testWidgets(
          '$layout ${role.name}: the create button hides over a pushed '
          'screen', (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        // The bar's last slot on the phone, the sidebar's button on
        // desktop: never a floating button.
        expect(createButton, findsOneWidget);
        expect(find.byType(FloatingActionButton), findsNothing);
        if (layout == 'mobile') {
          expect(
            find.descendant(
              of: find.byType(RoleNavBar),
              matching: createButton,
            ),
            findsOneWidget,
          );
          expect(
            find.descendant(
              of: createButton,
              matching: find.text(createWords[role]!),
            ),
            findsOneWidget,
          );
          expect(find.byTooltip(createNames[role]!), findsOneWidget);
        } else {
          expect(find.byType(RoleNavBar), findsNothing);
          expect(find.text(createNames[role]!), findsOneWidget);
        }

        await tester.tap(find.text('open detail'));
        await tester.pumpAndSettle();
        expect(find.text('pushed detail'), findsOneWidget);
        expect(createButton, findsNothing);

        Navigator.of(tester.element(find.text('pushed detail'))).pop();
        await tester.pumpAndSettle();
        expect(createButton, findsOneWidget);
      });
    }
  }

  // LT-01 and canvas page M: on a phone the create button is inside the
  // bar, beside the three tabs: it covers no tab and none of the page.
  for (final width in const [360.0, 390.0]) {
    for (final role in UserRole.values) {
      testWidgets(
          '${width.toInt()} dp ${role.name}: three tabs and the create '
          'button in one bar, over no tab and no content', (tester) async {
        tester.view.physicalSize = Size(width, 800);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);

        expect(find.byType(FloatingActionButton), findsNothing);
        final l10n = await AppLocalizations.delegate.load(const Locale('en'));
        final items =
            tester.widgetList<RoleNavItem>(find.byType(RoleNavItem)).toList();
        expect(
          items.map((item) => item.label),
          [for (final tab in tabsForRole(role)) tab.label(l10n)],
        );
        expect(items, hasLength(3));
        // The landing tab is the selected one.
        expect(items.map((item) => item.selected), [true, false, false]);

        final create = tester.getRect(createButton);
        final bar = tester.getRect(find.byType(RoleNavBar));
        expect(bar.bottom, 800);
        expect(bar.contains(create.topLeft), isTrue);
        expect(bar.contains(create.bottomRight - const Offset(1, 1)), isTrue);
        expect(create.width, greaterThanOrEqualTo(48));
        expect(create.height, greaterThanOrEqualTo(48));
        for (var i = 0; i < 3; i++) {
          final tab = tester.getRect(find.byType(RoleNavItem).at(i));
          expect(tab.overlaps(create), isFalse, reason: 'tab $i');
          expect(tab.right, lessThanOrEqualTo(create.left + 0.01));
          expect(tab.width, greaterThanOrEqualTo(48), reason: 'tab $i');
          expect(tab.height, greaterThanOrEqualTo(48), reason: 'tab $i');
        }
        // The page ends where the bar begins.
        expect(
          tester.getRect(find.text('open detail')).bottom,
          lessThanOrEqualTo(bar.top),
        );
      });
    }
  }

  // Profile and Settings sit outside the bar: the bar stays, with nothing
  // selected and without the create verb, which belongs to a tab's page.
  for (final role in UserRole.values) {
    testWidgets(
        'phone ${role.name}: on a non-tab shell route the bar has no create '
        'button and no selected tab', (tester) async {
      tester.view.physicalSize = const Size(400, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(buildApp(role));
      await tester.pumpAndSettle();
      expect(createButton, findsOneWidget);

      GoRouter.of(tester.element(find.text('open detail'))).go('/profile');
      await tester.pumpAndSettle();
      expect(find.text('profile'), findsOneWidget);
      expect(createButton, findsNothing);
      expect(find.byType(FloatingActionButton), findsNothing);
      expect(find.byType(RoleNavBar), findsOneWidget);
      final items = tester.widgetList<RoleNavItem>(find.byType(RoleNavItem));
      expect(items, hasLength(3));
      expect(items.any((item) => item.selected), isFalse);
    });
  }

  testWidgets('phone: a tab of the bar opens its route', (tester) async {
    tester.view.physicalSize = const Size(400, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(buildApp(UserRole.buyer));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Orders'));
    await tester.pumpAndSettle();
    expect(find.text('orders'), findsOneWidget);
    expect(
      tester
          .widgetList<RoleNavItem>(find.byType(RoleNavItem))
          .map((item) => item.selected),
      [false, false, true],
    );
    // Orders is a tab root: the create button is still there.
    expect(createButton, findsOneWidget);
  });

  // A buyer has no Home: at `/` (where the app's redirect never leaves
  // them) no tab is selected and there is no create button to cover a
  // page that is not theirs.
  testWidgets('phone buyer: `/` is no tab of theirs', (tester) async {
    tester.view.physicalSize = const Size(400, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(buildApp(UserRole.buyer));
    await tester.pumpAndSettle();
    expect(find.text('Home'), findsNothing);
    GoRouter.of(tester.element(find.text('open detail'))).go('/');
    await tester.pumpAndSettle();
    final items = tester.widgetList<RoleNavItem>(find.byType(RoleNavItem));
    expect(items.any((item) => item.selected), isFalse);
    expect(createButton, findsNothing);
  });

  // The boards' detail and create screens are full pages: on a phone the
  // bar leaves with the create button, and comes back with the tab.
  testWidgets('phone: no bar under a pushed screen', (tester) async {
    tester.view.physicalSize = const Size(400, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(buildApp(UserRole.trucker));
    await tester.pumpAndSettle();
    expect(find.byType(RoleNavBar), findsOneWidget);

    await tester.tap(find.text('open detail'));
    await tester.pumpAndSettle();
    expect(find.byType(RoleNavBar), findsNothing);

    Navigator.of(tester.element(find.text('pushed detail'))).pop();
    await tester.pumpAndSettle();
    expect(find.byType(RoleNavBar), findsOneWidget);
  });

  for (final size in const [Size(400, 800), Size(1300, 900)]) {
    final layout = size.width < 600 ? 'phone' : 'desktop';
    testWidgets('$layout: an order detail under the Orders tab is a full page',
        (tester) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(buildApp(UserRole.buyer));
      await tester.pumpAndSettle();
      final router = GoRouter.of(tester.element(find.text('open detail')));

      router.go('/orders');
      await tester.pumpAndSettle();
      expect(find.byType(RoleCreateButton), findsOneWidget);

      router.go('/orders/o-1');
      await tester.pumpAndSettle();
      expect(find.text('order detail'), findsOneWidget);
      expect(find.byType(RoleCreateButton), findsNothing);
      if (layout == 'phone') expect(find.byType(RoleNavBar), findsNothing);
    });
  }

  // FUP-75: on web the update banner tops the page, beside the sidebar,
  // over tab pages and pushed screens alike; the shell works as before.
  for (final size in const [Size(400, 800), Size(800, 900), Size(1300, 900)]) {
    final layout = size.width < 600
        ? 'phone'
        : size.width < 1024
            ? 'tablet'
            : 'desktop';
    for (final role in UserRole.values) {
      testWidgets('$layout ${role.name}: the update banner tops the page',
          (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        const message = 'Please update the app to place or change orders. '
            'Everything else still works.';

        final events = SecurityEvents.forTesting();
        await tester.pumpWidget(buildApp(role, updateEvents: events));
        await tester.pumpAndSettle();
        expect(find.text(message), findsNothing);

        events.reportAppUpdateRequired();
        await tester.pumpAndSettle();
        // What shows of it: long text scrolls inside.
        final banner = tester.getRect(
          find.ancestor(
            of: find.byType(MaterialBanner),
            matching: find.byType(SingleChildScrollView),
          ),
        );
        expect(find.text('Reload page'), findsOneWidget);
        expect(
          banner.bottom,
          lessThanOrEqualTo(tester.getRect(find.text('open detail')).top),
        );
        // In the page column, not over the sidebar or rail.
        expect(banner.left, layout == 'phone' ? 0 : greaterThan(0));
        expect(find.byType(RoleCreateButton), findsOneWidget);

        await tester.tap(find.text('open detail'));
        await tester.pumpAndSettle();
        expect(find.text('pushed detail'), findsOneWidget);
        expect(find.text(message), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  }

  // BUG-27: the tablet rail's floating create button folds to its plus
  // while the tab page scrolls toward its end. On a phone the button is
  // inside the bar, covers nothing, and stays as it is. The desktop
  // sidebar's button keeps its label.
  group('the create button while the tab scrolls', () {
    Widget longList(String key) => ListView.builder(
          key: ValueKey(key),
          itemCount: 60,
          itemBuilder: (_, i) => SizedBox(height: 64, child: Text('$key $i')),
        );

    Future<void> scroll(WidgetTester tester, Finder target, double dy) async {
      await tester.drag(target, Offset(0, dy));
      await tester.pumpAndSettle();
    }

    final pill = find.descendant(
      of: createButton,
      matching: find.byType(Material),
    );

    // Folded, the plain 54 dp round plus; unfolded, the labelled pill.
    void expectFolded(WidgetTester tester, {required bool folded}) {
      expect(createWordWidth(tester), folded ? 0 : greaterThan(0));
      expect(
        tester.getSize(pill).width,
        folded ? moreOrLessEquals(54) : greaterThan(80),
      );
    }

    for (final role in const [UserRole.farmer, UserRole.buyer]) {
      testWidgets(
          'tablet ${role.name}: follows the tab page, not a screen pushed '
          'over it', (tester) async {
        tester.view.physicalSize = const Size(800, 900);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final semantics = tester.ensureSemantics();

        await tester.pumpWidget(
          buildApp(role, ordersPage: longList('orders')),
        );
        await tester.pumpAndSettle();
        final router = GoRouter.of(tester.element(find.text('open detail')));
        router.go('/orders');
        await tester.pumpAndSettle();
        final list = find.byKey(const ValueKey('orders'));
        final name = createNames[role]!;

        // Floating beside the rail: no bar on a tablet.
        expect(find.byType(RoleNavBar), findsNothing);
        expect(tester.widget<RoleCreateButton>(createButton).floating, isTrue);
        expectFolded(tester, folded: false);
        await scroll(tester, list, -300);
        expectFolded(tester, folded: true);
        // Folded, a screen reader and a long press still get the name.
        expect(find.bySemanticsLabel(name), findsOneWidget);
        expect(find.byTooltip(name), findsOneWidget);

        await scroll(tester, list, 100);
        expectFolded(tester, folded: false);
        await scroll(tester, list, -200);
        expectFolded(tester, folded: true);

        // A screen pushed over the tab scrolls without the button: back
        // on the tab, the button is as the tab's list left it.
        Navigator.of(tester.element(list)).push(
          MaterialPageRoute<void>(
            builder: (_) => Scaffold(body: longList('pushed')),
          ),
        );
        await tester.pumpAndSettle();
        final pushed = find.byKey(const ValueKey('pushed'));
        await scroll(tester, pushed, -300);
        await scroll(tester, pushed, 100);
        Navigator.of(tester.element(pushed)).pop();
        await tester.pumpAndSettle();
        expectFolded(tester, folded: true);

        // Another tab opens at its top, with the label.
        router.go(landingRouteForRole(role));
        await tester.pumpAndSettle();
        expectFolded(tester, folded: false);
        expect(tester.takeException(), isNull);
        semantics.dispose();
      });
    }

    for (final role in const [UserRole.farmer, UserRole.buyer]) {
      testWidgets(
          'phone ${role.name}: in the bar, the same button however the tab '
          'scrolls', (tester) async {
        tester.view.physicalSize = const Size(400, 800);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        final semantics = tester.ensureSemantics();

        await tester.pumpWidget(
          buildApp(role, ordersPage: longList('orders')),
        );
        await tester.pumpAndSettle();
        final router = GoRouter.of(tester.element(find.text('open detail')));
        router.go('/orders');
        await tester.pumpAndSettle();
        final list = find.byKey(const ValueKey('orders'));
        final name = createNames[role]!;
        final atRest = tester.getRect(createButton);
        final wordAtRest = createWordWidth(tester);
        final bar = tester.getRect(find.byType(RoleNavBar));

        void expectUnchanged() {
          expect(tester.getRect(createButton), atRest);
          expect(createWordWidth(tester), wordAtRest);
          expect(tester.getRect(find.byType(RoleNavBar)), bar);
          expect(
            tester.widget<RoleCreateButton>(createButton).floating,
            isFalse,
          );
          expect(find.bySemanticsLabel(name), findsOneWidget);
          expect(find.byTooltip(name), findsOneWidget);
          // The list ends at the bar: the button covers no row of it.
          expect(tester.getRect(list).bottom, lessThanOrEqualTo(bar.top));
        }

        expectUnchanged();
        await scroll(tester, list, -300);
        expectUnchanged();
        await scroll(tester, list, 100);
        expectUnchanged();
        await scroll(tester, list, -200);
        expectUnchanged();

        router.go(landingRouteForRole(role));
        await tester.pumpAndSettle();
        expect(tester.getRect(createButton), atRest);
        expect(createWordWidth(tester), wordAtRest);
        expect(tester.takeException(), isNull);
        semantics.dispose();
      });
    }

    testWidgets('desktop: the sidebar button keeps its label', (tester) async {
      tester.view.physicalSize = const Size(1300, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        buildApp(UserRole.buyer, ordersPage: longList('orders')),
      );
      await tester.pumpAndSettle();
      GoRouter.of(tester.element(find.text('open detail'))).go('/orders');
      await tester.pumpAndSettle();
      // `FilledButton.icon` builds a private subclass on older Flutter (CI runs
      // 3.38), which an exact-type finder misses: match on `is` instead.
      final button = find.ancestor(
        of: find.text('New Buying Intent'),
        matching: find.byWidgetPredicate((w) => w is FilledButton),
      );
      final width = tester.getSize(button).width;

      await scroll(tester, find.byKey(const ValueKey('orders')), -300);
      expect(find.byType(FloatingActionButton), findsNothing);
      expect(tester.getSize(button).width, width);
    });
  });

  // Desktop sidebar footer: "Farmer · Meki" under the name, in the user's
  // language (it showed a lone " · " when the user had a wereda).
  group('sidebar footer', () {
    void desktop(WidgetTester tester) {
      tester.view.physicalSize = const Size(1300, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    testWidgets('shows the wereda and the role', (tester) async {
      desktop(tester);
      await tester.pumpWidget(
        buildApp(UserRole.farmer, userName: 'Almaz', woreda: 'Meki'),
      );
      await tester.pumpAndSettle();

      expect(find.text('Almaz'), findsOneWidget);
      expect(find.text('Farmer · Meki'), findsOneWidget);
      expect(find.text(' · '), findsNothing);
    });

    testWidgets('a nameless user is shown by role, in Amharic too',
        (tester) async {
      desktop(tester);
      await tester.pumpWidget(
        buildApp(UserRole.trucker, locale: const Locale('am')),
      );
      await tester.pumpAndSettle();

      final am = await AppLocalizations.delegate.load(const Locale('am'));
      expect(find.text(am.trucker), findsWidgets);
      expect(find.text('User'), findsNothing);
    });
  });

  // What left the bar stays one click away in the sidebar (canvas page
  // M). Hard product rule: a buyer can always browse ALL trips; with no
  // Home to link to them, Find Trips is in the buyer's sidebar.
  group('sidebar extras', () {
    void desktop(WidgetTester tester) {
      tester.view.physicalSize = const Size(1300, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    testWidgets('buyer: Find Trips opens `/trips`', (tester) async {
      desktop(tester);
      await tester.pumpWidget(buildApp(UserRole.buyer));
      await tester.pumpAndSettle();

      expect(find.text('Home'), findsNothing);
      await tester.tap(find.text('Find Trips'));
      await tester.pumpAndSettle();
      expect(find.text('trips'), findsOneWidget);
    });

    testWidgets('farmer: Deliveries opens `/deliveries`', (tester) async {
      desktop(tester);
      await tester.pumpWidget(buildApp(UserRole.farmer));
      await tester.pumpAndSettle();

      expect(find.text('open detail'), findsOneWidget);
      await tester.tap(find.text('Deliveries'));
      await tester.pumpAndSettle();
      final router = GoRouter.of(tester.element(find.text('open detail')));
      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/deliveries',
      );
    });

    testWidgets('farmer and trucker: no Find Trips, no Market', (tester) async {
      for (final role in [UserRole.farmer, UserRole.trucker]) {
        desktop(tester);
        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();
        expect(find.text('Find Trips'), findsNothing, reason: role.name);
        expect(find.text('Market'), findsNothing, reason: role.name);
      }
    });
  });

  // A-7: on a wide window the sidebar is the only way to the create button,
  // Messages, Payments, Settings and Profile. It was missing from the
  // semantics tree (the tab navigator's modal barrier hid it) and Tab
  // skipped it (no item could take focus).
  group('sidebar for screen readers and keyboards', () {
    void window(WidgetTester tester, Size size) {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    List<String> destinationLabels(AppLocalizations l10n, UserRole role) => [
          for (final tab in [
            ...tabsForRole(role),
            ...sidebarExtrasForRole(role),
          ])
            tab.label(l10n),
        ];

    Tristate focusOf(WidgetTester tester, String label) => tester
        .getSemantics(find.bySemanticsLabel(label))
        .getSemanticsData()
        .flagsCollection
        .isFocused;

    void expectFocused(WidgetTester tester, String label) =>
        expect(focusOf(tester, label), Tristate.isTrue, reason: label);

    void expectNotFocused(WidgetTester tester, String label) =>
        expect(focusOf(tester, label), isNot(Tristate.isTrue), reason: label);

    for (final role in UserRole.values) {
      testWidgets('desktop ${role.name}: every item is a focusable button',
          (tester) async {
        window(tester, const Size(1300, 900));
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(buildApp(role, userName: 'Almaz'));
        await tester.pumpAndSettle();
        final l10n = await AppLocalizations.delegate.load(const Locale('en'));

        for (final label in destinationLabels(l10n, role)) {
          expect(
            tester.getSemantics(find.bySemanticsLabel(label)),
            matchesSemantics(
              label: label,
              isButton: true,
              isFocusable: true,
              hasSelectedState: true,
              // The landing tab: Home, or Deliveries for a buyer.
              isSelected: label == tabsForRole(role).first.label(l10n),
              hasTapAction: true,
              hasFocusAction: true,
            ),
            reason: label,
          );
        }
        for (final label in [l10n.settings, 'Almaz']) {
          expect(
            tester.getSemantics(find.bySemanticsLabel(label)),
            matchesSemantics(
              label: label,
              isButton: true,
              isFocusable: true,
              hasSelectedState: true,
              hasTapAction: true,
              hasFocusAction: true,
            ),
            reason: label,
          );
        }
        // The role's create button lives only in the desktop sidebar.
        expect(
          tester.getSemantics(find.bySemanticsLabel(createNames[role]!)),
          matchesSemantics(
            label: createNames[role],
            isButton: true,
            isFocusable: true,
            hasEnabledState: true,
            isEnabled: true,
            hasTapAction: true,
            hasFocusAction: true,
          ),
        );
        semantics.dispose();
      });
    }

    testWidgets('tablet rail: icon items keep their labels', (tester) async {
      window(tester, const Size(800, 900));
      final semantics = tester.ensureSemantics();
      await tester.pumpWidget(buildApp(UserRole.trucker));
      await tester.pumpAndSettle();
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));

      for (final label in [
        ...destinationLabels(l10n, UserRole.trucker),
        l10n.settings,
      ]) {
        expect(
          tester.getSemantics(find.bySemanticsLabel(label)),
          matchesSemantics(
            label: label,
            isButton: true,
            isFocusable: true,
            hasSelectedState: true,
            isSelected: label == l10n.home,
            hasTapAction: true,
            hasFocusAction: true,
          ),
          reason: label,
        );
      }
      semantics.dispose();
    });

    for (final role in UserRole.values) {
      testWidgets('desktop ${role.name}: Tab walks the sidebar, then the page',
          (tester) async {
        window(tester, const Size(1300, 900));
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(buildApp(role, userName: 'Almaz'));
        await tester.pumpAndSettle();
        final l10n = await AppLocalizations.delegate.load(const Locale('en'));

        final order = [
          ...destinationLabels(l10n, role),
          createNames[role]!,
          l10n.settings,
          'Almaz',
        ];
        for (final label in order) {
          await tester.sendKeyEvent(LogicalKeyboardKey.tab);
          await tester.pump();
          expectFocused(tester, label);
        }
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
        expectFocused(tester, 'open detail');
        semantics.dispose();
      });
    }

    // A sheet on the tab's navigator is not a page: the first Tab stays in
    // it instead of jumping to the sidebar behind it.
    testWidgets('a sheet on the tab navigator keeps Tab inside it',
        (tester) async {
      window(tester, const Size(1300, 900));
      final semantics = tester.ensureSemantics();
      await tester.pumpWidget(buildApp(UserRole.buyer, userName: 'Almaz'));
      await tester.pumpAndSettle();

      // On the tab's own navigator (the default: not the root one).
      showModalBottomSheet<void>(
        context: tester.element(find.text('open detail')),
        builder: (_) => SizedBox(
          height: 200,
          child: Center(
            child: TextButton(
              onPressed: () {},
              child: const Text('sheet action'),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      expectFocused(tester, 'sheet action');
      expectNotFocused(tester, 'Deliveries');
      semantics.dispose();
    });

    testWidgets('a focused item shows a ring; Enter and Space open it',
        (tester) async {
      window(tester, const Size(1300, 900));
      final semantics = tester.ensureSemantics();
      await tester.pumpWidget(buildApp(UserRole.buyer, userName: 'Almaz'));
      await tester.pumpAndSettle();

      Iterable<Border?> rings(String label) => tester
          .widgetList<DecoratedBox>(
            find.descendant(
              of: find.bySemanticsLabel(label),
              matching: find.byType(DecoratedBox),
            ),
          )
          .map((box) => (box.decoration as BoxDecoration?)?.border as Border?)
          .where((border) => border != null);

      expect(rings('Orders'), isEmpty);
      // Deliveries, Market, then Orders.
      for (var i = 0; i < 3; i++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
      }
      expectFocused(tester, 'Orders');
      expectNotFocused(tester, 'Deliveries');
      expect(rings('Orders'), isNotEmpty);
      expect(rings('Deliveries'), isEmpty);

      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.text('orders'), findsOneWidget);

      // Back to the profile tile, the sidebar's last item: Space opens it.
      FocusManager.instance.primaryFocus?.unfocus();
      await tester.pump();
      final count = [
        ...tabsForRole(UserRole.buyer),
        ...sidebarExtrasForRole(UserRole.buyer),
      ].length;
      for (var i = 0; i < count + 3; i++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
      }
      expectFocused(tester, 'Almaz');
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(find.text('profile'), findsOneWidget);
      semantics.dispose();
    });
  });
}

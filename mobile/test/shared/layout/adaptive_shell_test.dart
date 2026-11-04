import 'dart:ui' show Tristate;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/adaptive_shell.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// SIM-44: the role FAB must show on tab roots only, never over a pushed
/// screen whose own Send / Save / Place Order buttons it would cover.
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
    final router = GoRouter(
      routes: [
        ShellRoute(
          observers: [pushedRoutes],
          builder: (context, state, child) =>
              AdaptiveShell(pushedRoutes: pushedRoutes, child: child),
          routes: [
            GoRoute(
              path: '/',
              builder: (context, state) => Builder(
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
              ),
            ),
            GoRoute(
              path: '/profile',
              builder: (context, state) => const Text('profile'),
            ),
            GoRoute(
              path: '/orders',
              builder: (context, state) => const Text('orders'),
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
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        routerConfig: router,
      ),
    );
  }

  const fabLabels = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  for (final size in const [Size(400, 800), Size(1300, 900)]) {
    final layout = size.width < 600 ? 'mobile' : 'desktop';
    for (final role in UserRole.values) {
      testWidgets('$layout ${role.name}: FAB hides over a pushed screen',
          (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();
        // A floating button on the phone, the sidebar's button on desktop.
        expect(find.byType(RoleCreateButton), findsOneWidget);
        expect(find.text(fabLabels[role]!), findsOneWidget);

        await tester.tap(find.text('open detail'));
        await tester.pumpAndSettle();
        expect(find.text('pushed detail'), findsOneWidget);
        expect(find.byType(RoleCreateButton), findsNothing);

        Navigator.of(tester.element(find.text('pushed detail'))).pop();
        await tester.pumpAndSettle();
        expect(find.byType(RoleCreateButton), findsOneWidget);
      });
    }
  }

  // LT-01: on a phone the FAB floats above the bottom bar, clear of every
  // tab, and tab bodies pad by fabClearance so it hides no last item.
  for (final width in const [360.0, 390.0]) {
    for (final role in UserRole.values) {
      testWidgets('${width.toInt()} dp ${role.name}: the FAB covers no tab',
          (tester) async {
        tester.view.physicalSize = Size(width, 800);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);

        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();

        final fab = tester.getRect(find.byType(FloatingActionButton));
        final bar = tester.getRect(find.byType(RoleNavBar));
        expect(fab.bottom, lessThanOrEqualTo(bar.top));
        expect(bar.top - fab.top, lessThanOrEqualTo(Breakpoints.fabClearance));
      });
    }
  }

  testWidgets('FAB hides on a non-tab shell route', (tester) async {
    tester.view.physicalSize = const Size(400, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(buildApp(UserRole.buyer));
    await tester.pumpAndSettle();
    expect(find.byType(FloatingActionButton), findsOneWidget);

    GoRouter.of(tester.element(find.text('open detail'))).go('/profile');
    await tester.pumpAndSettle();
    expect(find.text('profile'), findsOneWidget);
    expect(find.byType(FloatingActionButton), findsNothing);
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

  // Desktop sidebar footer: "Meki · Farmer" under the name, in the user's
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
      expect(find.text('Meki · Farmer'), findsOneWidget);
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
              isSelected: label == l10n.home,
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
          tester.getSemantics(find.bySemanticsLabel(fabLabels[role]!)),
          matchesSemantics(
            label: fabLabels[role],
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
          fabLabels[role]!,
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
      expectNotFocused(tester, 'Home');
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
      // Home, Market, then Orders.
      for (var i = 0; i < 3; i++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.tab);
        await tester.pump();
      }
      expectFocused(tester, 'Orders');
      expectNotFocused(tester, 'Home');
      expect(rings('Orders'), isNotEmpty);
      expect(rings('Home'), isEmpty);

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

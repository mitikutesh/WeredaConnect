import 'package:flutter/material.dart';
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
}

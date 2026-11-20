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
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/layout/adaptive_shell.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

/// The web sidebar and tablet rail as the Web · Buyer deliveries board
/// (page M) draws them: the black create button, the warm ground under the
/// selected entry, the user card, and the rail's floating button centred
/// over the page.
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

  Future<void> pumpShell(
    WidgetTester tester,
    UserRole role,
    Size size, {
    Locale? locale,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: 'u-1',
        tokens: const AuthTokens(
          accessToken: 'a',
          refreshToken: 'r',
          idToken: 'i',
        ),
        role: role,
        userName: 'Selam Traders',
        woreda: 'Merkato',
      ),
    );
    final landing = tabsForRole(role).first.route;
    final routes = {
      for (final tab in [...tabsForRole(role), ...sidebarExtrasForRole(role)])
        tab.route,
      '/profile',
      '/settings',
    };
    final router = GoRouter(
      initialLocation: landing,
      routes: [
        ShellRoute(
          builder: (context, state, child) => AdaptiveShell(child: child),
          routes: [
            for (final route in routes)
              GoRoute(
                path: route,
                builder: (context, state) => Center(child: Text('page $route')),
              ),
          ],
        ),
      ],
    );
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<InboxBloc>.value(value: inboxBloc),
        ],
        child: MaterialApp.router(
          locale: locale,
          theme: AppTheme.light(role: role),
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          routerConfig: router,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  const desktop = Size(1440, 1000);
  const tablet = Size(800, 1000);

  Finder createButton(String label) => find.ancestor(
        of: find.text(label),
        matching: find.byWidgetPredicate((w) => w is FilledButton),
      );

  const createLabels = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  for (final role in UserRole.values) {
    testWidgets(
        'desktop ${role.name}: the create button is black, in the '
        'sidebar', (tester) async {
      await pumpShell(tester, role, desktop);

      final button = createButton(createLabels[role]!);
      expect(button, findsOneWidget);
      final style = tester.widget<FilledButton>(button).style!;
      expect(style.backgroundColor!.resolve({}), AppTheme.ink);
      expect(style.foregroundColor!.resolve({}), Colors.white);
      expect(
        style.side!.resolve({WidgetState.focused})!.color,
        RoleColors.forRole(role).primary,
      );
      final rect = tester.getRect(button);
      expect(rect.height, 44);
      expect(rect.right, lessThan(Breakpoints.sidebarExpanded));
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets(
      'desktop buyer: the selected entry sits on the warm ground in '
      'the role\'s dark shade, the others in soft ink', (tester) async {
    await pumpShell(tester, UserRole.buyer, desktop);

    TextStyle styleOf(String label) =>
        tester.widget<Text>(find.text(label)).style!;
    // The buyer lands on Deliveries.
    expect(styleOf('Deliveries').color, RoleColors.buyer.dark);
    expect(styleOf('Deliveries').fontWeight, FontWeight.w700);
    expect(styleOf('Orders').color, AppTheme.inkSoft);
    expect(styleOf('Orders').fontWeight, FontWeight.w600);

    Color? groundOf(String label) => (tester
            .widget<AnimatedContainer>(
              find.ancestor(
                of: find.text(label),
                matching: find.byType(AnimatedContainer),
              ),
            )
            .decoration as BoxDecoration?)
        ?.color;
    expect(groundOf('Deliveries'), const Color(0xFFEFE3DC));
    expect(groundOf('Orders'), Colors.transparent);
  });

  testWidgets('desktop: the user card says the role, then the place',
      (tester) async {
    await pumpShell(tester, UserRole.buyer, desktop);
    expect(find.text('Selam Traders'), findsOneWidget);
    expect(find.text('Buyer · Merkato'), findsOneWidget);
  });

  testWidgets('the brand mark is the tractor in the role\'s colour',
      (tester) async {
    await pumpShell(tester, UserRole.trucker, desktop);
    final mark = find.byIcon(Icons.agriculture);
    expect(mark, findsOneWidget);
    final box = tester.widget<Container>(
      find.ancestor(of: mark, matching: find.byType(Container)).first,
    );
    expect(
      (box.decoration! as BoxDecoration).color,
      RoleColors.trucker.primary,
    );
  });

  for (final role in UserRole.values) {
    testWidgets(
        'tablet ${role.name}: the floating create button is centred '
        'over the page, not over the page plus the rail', (tester) async {
      await pumpShell(tester, role, tablet);

      final button = find.byType(RoleCreateButton);
      expect(button, findsOneWidget);
      const rail = Breakpoints.sidebarCollapsed;
      expect(
        tester.getCenter(button).dx,
        moreOrLessEquals(rail + (tablet.width - rail) / 2, epsilon: 1),
      );
      expect(tester.takeException(), isNull);
    });
  }

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets(
        'desktop ${locale.languageCode}: nothing overflows the '
        'sidebar', (tester) async {
      for (final role in UserRole.values) {
        await pumpShell(tester, role, const Size(1024, 700), locale: locale);
        expect(tester.takeException(), isNull);
      }
    });
  }
}

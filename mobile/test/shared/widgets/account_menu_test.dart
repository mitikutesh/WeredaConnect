import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

/// The phone's account button: the bar has no slot for Messages, Payments
/// (Orders for the trucker), Profile or Settings, so the header's round
/// button lists them, as the web sidebar does.
void main() {
  late MockAuthBloc authBloc;
  late MockInboxBloc inboxBloc;
  final en = lookupAppLocalizations(const Locale('en'));

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    authBloc = MockAuthBloc();
    inboxBloc = MockInboxBloc();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
  });

  void signIn(UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: 'user-001',
        tokens: _tokens,
        role: role,
        userName: 'Abebe Kebede',
        woreda: 'Meki',
      ),
    );
  }

  Future<void> pump(WidgetTester tester, Widget child) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ChangeNotifierProvider(create: (_) => LanguageProvider()),
          ChangeNotifierProvider(create: (_) => SettingsProvider()),
        ],
        child: MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<InboxBloc>.value(value: inboxBloc),
          ],
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(body: Center(child: child)),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> openSheet(WidgetTester tester) async {
    await tester.tap(find.byKey(const Key('account_button')));
    await tester.pumpAndSettle();
  }

  List<String> rowIds(WidgetTester tester) => tester
      .widgetList<ListTile>(find.byType(ListTile))
      .map((t) => (t.key! as ValueKey<String>).value)
      .toList();

  for (final (role, extras) in [
    (UserRole.farmer, ['messages', 'payments']),
    (UserRole.trucker, ['messages', 'orders']),
    (UserRole.buyer, ['messages', 'payments']),
  ]) {
    testWidgets('${role.name}: the sidebar\'s extras, Profile and Settings',
        (tester) async {
      signIn(role);
      await pump(tester, const AccountButton());
      await openSheet(tester);

      expect(rowIds(tester), [
        for (final id in [...extras, 'profile', 'settings']) 'account_menu_$id',
      ]);
      final roleName = switch (role) {
        UserRole.farmer => en.farmer,
        UserRole.trucker => en.trucker,
        UserRole.buyer => en.buyer,
      };
      expect(find.text('Abebe Kebede'), findsOneWidget);
      expect(find.text('Meki · $roleName'), findsOneWidget);
    });
  }

  testWidgets('unread messages: a dot on the button, the count on the row',
      (tester) async {
    signIn(UserRole.farmer);
    when(() => inboxBloc.state).thenReturn(
      const InboxLoaded(conversations: [], totalUnread: 3),
    );
    await pump(tester, const AccountButton());

    expect(find.byKey(const Key('account_button_dot')), findsOneWidget);
    await openSheet(tester);
    expect(
      find.descendant(
        of: find.byKey(const Key('account_menu_messages')),
        matching: find.text('3'),
      ),
      findsOneWidget,
    );
  });

  testWidgets('no unread messages: no dot', (tester) async {
    signIn(UserRole.buyer);
    await pump(tester, const AccountButton());

    expect(find.byKey(const Key('account_button')), findsOneWidget);
    expect(find.byKey(const Key('account_button_dot')), findsNothing);
  });

  testWidgets('a row closes the sheet and opens its page', (tester) async {
    signIn(UserRole.trucker);
    await pump(tester, const AccountButton());
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('account_menu_settings')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('account_menu_settings')), findsNothing);
    expect(find.byType(SettingsScreen), findsOneWidget);
  });

  testWidgets('beside a web sidebar: the bell alone', (tester) async {
    signIn(UserRole.farmer);
    await pump(
      tester,
      const SidebarScope(
        child: HeaderActions(bell: Icon(Icons.notifications_outlined)),
      ),
    );

    expect(find.byIcon(Icons.notifications_outlined), findsOneWidget);
    expect(find.byKey(const Key('account_button')), findsNothing);
  });

  testWidgets('without a sidebar: the bell, then the account button',
      (tester) async {
    signIn(UserRole.farmer);
    await pump(
      tester,
      const HeaderActions(bell: Icon(Icons.notifications_outlined)),
    );

    final bell = tester.getCenter(find.byIcon(Icons.notifications_outlined));
    final account = tester.getCenter(find.byKey(const Key('account_button')));
    expect(account.dx, greaterThan(bell.dx));
  });

  testWidgets('signed out: no account button', (tester) async {
    when(() => authBloc.state).thenReturn(const AuthUnauthenticated());
    await pump(tester, const AccountButton());

    expect(find.byKey(const Key('account_button')), findsNothing);
  });
}

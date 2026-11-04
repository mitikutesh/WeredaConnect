import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeGate implements LocalAuthGate {
  @override
  Future<LocalAuthOutcome> authenticate(String reason) async =>
      LocalAuthOutcome.authenticated;

  @override
  Future<bool> isAvailable() async => false;
}

AuthAuthenticated _signedIn(UserRole role) => AuthAuthenticated(
      userId: 'user-1',
      tokens:
          const AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
      role: role,
      userName: 'Abebe Kebede',
      phoneNumber: '+251911234567',
    );

/// E-1: logout deletes this user's unsent changes from the phone, so the
/// dialog says so first and offers Sync now. With nothing unsent it only
/// asks, as before.
void main() {
  late MockAuthBloc bloc;
  late AppLockService lock;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() => registerFallbackValue(FakeAuthEvent()));

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    bloc = MockAuthBloc();
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    final storage = MockSecureStorage();
    when(() => storage.read(key: any(named: 'key')))
        .thenAnswer((_) async => null);
    lock = AppLockService(gate: FakeGate(), storage: storage);
  });

  /// Opens the logout dialog on the Profile of [role] with [work] unsent.
  /// [loads] records the user ids the dialog asked for.
  Future<List<String>> openDialog(
    WidgetTester tester, {
    UserRole role = UserRole.farmer,
    List<LogoutUnsentWork> work = const [(changes: 0, payments: 0)],
    bool online = true,
    Future<void> Function()? syncNow,
  }) async {
    tester.view.physicalSize = const Size(390, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => bloc.state).thenReturn(_signedIn(role));
    final loads = <String>[];
    var call = 0;
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ChangeNotifierProvider(create: (_) => LanguageProvider()),
          ChangeNotifierProvider(create: (_) => SettingsProvider()),
        ],
        child: BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            theme: AppTheme.light(),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: ProfileScreen(
              loadUser: (_) async => null,
              appLockService: lock,
              loadUnsentWork: (userId) async {
                loads.add(userId);
                return work[call < work.length ? call++ : work.length - 1];
              },
              syncUnsentWork: syncNow ?? () async {},
              isOnline: () async => online,
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.widgetWithText(OutlinedButton, 'Logout'));
    await tester.tap(find.widgetWithText(OutlinedButton, 'Logout'));
    await tester.pumpAndSettle();
    return loads;
  }

  Finder dialogText(String text) => find.descendant(
        of: find.byKey(const Key('logout_dialog_text')),
        matching: find.text(text),
      );

  for (final role in UserRole.values) {
    testWidgets('${role.name}: nothing unsent, the plain confirmation',
        (tester) async {
      final loads = await openDialog(tester, role: role);

      expect(loads, ['user-1']);
      expect(dialogText(en.logoutConfirmation), findsOneWidget);
      expect(find.byKey(const Key('logout_sync_now')), findsNothing);
      expect(find.text(en.logoutAnyway), findsNothing);

      await tester.tap(find.byKey(const Key('logout_confirm')));
      await tester.pumpAndSettle();
      verify(() => bloc.add(const AuthLogoutRequested())).called(1);
    });
  }

  testWidgets('unsent changes and payments: counts, Sync now, Log out anyway',
      (tester) async {
    await openDialog(tester, work: const [(changes: 3, payments: 1)]);

    expect(dialogText(en.logoutUnsentChanges(3)), findsOneWidget);
    expect(dialogText(en.logoutUnpaidPayments(1)), findsOneWidget);
    expect(dialogText(en.logoutConfirmation), findsNothing);
    expect(dialogText(en.logoutOfflineNeedsInternet), findsNothing);
    expect(find.byKey(const Key('logout_sync_now')), findsOneWidget);
    expect(find.text(en.logoutAnyway), findsOneWidget);

    await tester.tap(find.text(en.logoutAnyway));
    await tester.pumpAndSettle();
    verify(() => bloc.add(const AuthLogoutRequested())).called(1);
  });

  testWidgets('Cancel keeps the session and the changes', (tester) async {
    await openDialog(tester, work: const [(changes: 1, payments: 0)]);

    expect(dialogText(en.logoutUnsentChanges(1)), findsOneWidget);
    await tester.tap(find.text(en.cancel));
    await tester.pumpAndSettle();
    verifyNever(() => bloc.add(any()));
  });

  testWidgets('offline: says logging in again needs internet', (tester) async {
    await openDialog(
      tester,
      work: const [(changes: 2, payments: 0)],
      online: false,
    );

    expect(dialogText(en.logoutUnsentChanges(2)), findsOneWidget);
    expect(dialogText(en.logoutOfflineNeedsInternet), findsOneWidget);
  });

  testWidgets('Sync now: Logout waits, then the counts are read again',
      (tester) async {
    final sync = Completer<void>();
    var synced = 0;
    final loads = await openDialog(
      tester,
      work: const [(changes: 2, payments: 1), (changes: 0, payments: 0)],
      syncNow: () {
        synced++;
        return sync.future;
      },
    );

    await tester.tap(find.byKey(const Key('logout_sync_now')));
    await tester.pump();

    expect(synced, 1);
    expect(find.text(en.syncInProgress), findsOneWidget);
    final confirm = tester.widget<TextButton>(
      find.byKey(const Key('logout_confirm')),
    );
    expect(confirm.onPressed, isNull);

    sync.complete();
    await tester.pumpAndSettle();

    // Everything went: the dialog only asks now.
    expect(loads, ['user-1', 'user-1']);
    expect(dialogText(en.logoutConfirmation), findsOneWidget);
    expect(find.byKey(const Key('logout_sync_now')), findsNothing);
    expect(find.text(en.logout), findsWidgets);
    await tester.tap(find.byKey(const Key('logout_confirm')));
    await tester.pumpAndSettle();
    verify(() => bloc.add(const AuthLogoutRequested())).called(1);
  });

  testWidgets('Sync now that hangs gives Logout back after the timeout',
      (tester) async {
    await openDialog(
      tester,
      work: const [(changes: 2, payments: 0), (changes: 1, payments: 0)],
      syncNow: () => Completer<void>().future,
    );

    await tester.tap(find.byKey(const Key('logout_sync_now')));
    await tester.pump();
    expect(
      tester
          .widget<TextButton>(find.byKey(const Key('logout_confirm')))
          .onPressed,
      isNull,
    );

    await tester.pump(ProfileScreen.logoutSyncTimeout);
    await tester.pumpAndSettle();

    expect(dialogText(en.logoutUnsentChanges(1)), findsOneWidget);
    expect(
      tester
          .widget<TextButton>(find.byKey(const Key('logout_confirm')))
          .onPressed,
      isNotNull,
    );
  });
}

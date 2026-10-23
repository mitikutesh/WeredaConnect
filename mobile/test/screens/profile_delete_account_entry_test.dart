import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class _NoLockGate implements LocalAuthGate {
  @override
  Future<LocalAuthOutcome> authenticate(String reason) async =>
      LocalAuthOutcome.failed;

  @override
  Future<bool> isAvailable() async => false;
}

AuthAuthenticated _signedIn(UserRole role) => AuthAuthenticated(
      userId: '${role.name}-1',
      tokens: const AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        idToken: 'i',
      ),
      role: role,
      userName: 'Abebe',
      phoneNumber: '+251911234567',
    );

/// ACC-03: "Delete account" sits in the Profile of every role, in both the
/// phone and the wide (web) layout, and opens the deletion screen.
void main() {
  late MockAuthBloc bloc;
  late StreamController<AuthState> states;

  setUpAll(() {
    // The deletion screen builds its repository on the app's API client.
    ApiClient().initialize(environment: Environment.development);
  });

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.stream).thenAnswer((_) => states.stream);
  });

  tearDown(() {
    unawaited(states.close());
  });

  Widget profile() => ProfileScreen(
        loadUser: (_) async => null,
        appLockService: AppLockService(
          gate: _NoLockGate(),
          storage: MockSecureStorage(),
        ),
      );

  Future<void> pump(WidgetTester tester, Widget home, Size size) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      BlocProvider<AuthBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: home,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final entry = find.byKey(const Key('profile_delete_account'));

  const layouts = {
    'phone': Size(400, 3000),
    'wide': Size(1280, 3000),
  };

  for (final role in UserRole.values) {
    for (final MapEntry(key: layout, value: size) in layouts.entries) {
      testWidgets('${role.name}, $layout layout: shows Delete account',
          (tester) async {
        when(() => bloc.state).thenReturn(_signedIn(role));
        await pump(tester, profile(), size);

        expect(entry, findsOneWidget);
        expect(find.text('Delete account'), findsOneWidget);
        expect(
          find.text('Remove your account and personal data'),
          findsOneWidget,
        );
      });
    }

    testWidgets('${role.name}: the entry opens the deletion screen',
        (tester) async {
      when(() => bloc.state).thenReturn(_signedIn(role));
      await pump(tester, profile(), layouts['phone']!);

      await tester.ensureVisible(entry);
      await tester.tap(entry);
      await tester.pumpAndSettle();

      expect(find.byType(DeleteAccountScreen), findsOneWidget);
      expect(find.text('Delete my account'), findsOneWidget);
    });
  }

  Future<void> openDeletionFromHome(WidgetTester tester) async {
    when(() => bloc.state).thenReturn(_signedIn(UserRole.buyer));
    await pump(
      tester,
      Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(builder: (_) => profile()),
            ),
            child: const Text('home'),
          ),
        ),
      ),
      layouts['phone']!,
    );
    await tester.tap(find.text('home'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(entry);
    await tester.tap(entry);
    await tester.pumpAndSettle();
    expect(find.byType(DeleteAccountScreen), findsOneWidget);
  }

  void expectBackHome() {
    expect(find.byType(DeleteAccountScreen), findsNothing);
    expect(find.byType(ProfileScreen), findsNothing);
    expect(find.text('home'), findsOneWidget);
  }

  testWidgets('the deletion wipe returns to the first route', (tester) async {
    await openDeletionFromHome(tester);

    // What the AuthBloc emits while it wipes the deleted account.
    when(() => bloc.state).thenReturn(const AuthAccountWiped());
    states
      ..add(const AuthLoading())
      ..add(const AuthAccountWiped());
    await tester.pumpAndSettle();

    expectBackHome();
  });

  testWidgets('a single sign-out state also leaves no screen above Profile',
      (tester) async {
    await openDeletionFromHome(tester);

    // A rejected refresh token signs out in one step (AuthSessionExpired).
    when(() => bloc.state).thenReturn(const AuthUnauthenticated());
    states.add(const AuthUnauthenticated());
    await tester.pumpAndSettle();

    expectBackHome();
  });
}

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/account/presentation/screens/edit_profile_screen.dart';
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
      woreda: 'Adama',
    );

/// SIM-56: "Edit profile" sits in the Profile of every role, in both the
/// phone and the wide (web) layout, and opens the edit screen.
void main() {
  late MockAuthBloc bloc;
  late StreamController<AuthState> states;

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.stream).thenAnswer((_) => states.stream);
  });

  tearDown(() {
    unawaited(states.close());
  });

  Future<void> pump(WidgetTester tester, Size size) async {
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
          home: ProfileScreen(
            loadUser: (_) async => null,
            appLockService: AppLockService(
              gate: _NoLockGate(),
              storage: MockSecureStorage(),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final entry = find.byKey(const Key('profile_edit'));

  const layouts = {
    'phone': Size(400, 3000),
    'wide': Size(1280, 3000),
  };

  for (final role in UserRole.values) {
    for (final MapEntry(key: layout, value: size) in layouts.entries) {
      testWidgets('${role.name}, $layout layout: shows Edit profile',
          (tester) async {
        when(() => bloc.state).thenReturn(_signedIn(role));
        await pump(tester, size);

        expect(entry, findsOneWidget);
        expect(find.text('Name and woreda'), findsOneWidget);
      });
    }

    testWidgets('${role.name}: the entry opens the edit screen',
        (tester) async {
      when(() => bloc.state).thenReturn(_signedIn(role));
      await pump(tester, layouts['phone']!);

      await tester.ensureVisible(entry);
      await tester.tap(entry);
      await tester.pumpAndSettle();

      expect(find.byType(EditProfileScreen), findsOneWidget);
      // Without a cached copy the form starts from the session.
      expect(find.text('Abebe'), findsOneWidget);
    });
  }
}

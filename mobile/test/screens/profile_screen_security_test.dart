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
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class FakeGate implements LocalAuthGate {
  LocalAuthOutcome outcome = LocalAuthOutcome.authenticated;
  bool available = true;
  int prompts = 0;

  @override
  Future<LocalAuthOutcome> authenticate(String reason) async {
    prompts++;
    return outcome;
  }

  @override
  Future<bool> isAvailable() async => available;
}

const _signedIn = AuthAuthenticated(
  userId: 'buyer-1',
  tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
  role: UserRole.buyer,
  userName: 'Abebe',
  phoneNumber: '+251911234567',
);

User _user({required bool verified}) => User()
  ..userId = 'buyer-1'
  ..phoneNumber = '+251911234567'
  ..name = 'Abebe'
  ..role = UserRole.buyer
  ..woreda = 'Adama'
  ..isVerified = verified
  ..faydaVerified = verified
  ..createdAt = DateTime.utc(2026, 9)
  ..updatedAt = DateTime.utc(2026, 9)
  ..version = 1;

void main() {
  late MockAuthBloc bloc;
  late MockSecureStorage storage;
  late FakeGate gate;
  late AppLockService lock;
  late Map<String, String> stored;

  setUp(() {
    bloc = MockAuthBloc();
    when(() => bloc.state).thenReturn(_signedIn);
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    stored = {};
    storage = MockSecureStorage();
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => stored[AppLockService.enabledKey]);
    when(
      () => storage.write(
        key: AppLockService.enabledKey,
        value: any(named: 'value'),
      ),
    ).thenAnswer((inv) async {
      stored[AppLockService.enabledKey] = inv.namedArguments[#value] as String;
    });
    gate = FakeGate();
    lock = AppLockService(gate: gate, storage: storage);
  });

  Future<void> pumpProfile(
    WidgetTester tester, {
    bool verified = false,
  }) async {
    tester.view.physicalSize = const Size(1080, 4000);
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
            loadUser: (_) async => _user(verified: verified),
            appLockService: lock,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final lockSwitch = find.byKey(const Key('profile_app_lock_switch'));
  final faydaEntry = find.byKey(const Key('profile_verify_fayda'));

  Future<void> toggleLock(WidgetTester tester) async {
    await tester.ensureVisible(lockSwitch);
    await tester.tap(lockSwitch);
    await tester.pumpAndSettle();
  }

  bool switchValue(WidgetTester tester) =>
      tester.widget<SwitchListTile>(lockSwitch).value;

  group('app lock switch', () {
    testWidgets('turning it off needs a successful device unlock',
        (tester) async {
      await pumpProfile(tester);
      expect(switchValue(tester), isTrue, reason: 'defaults on');

      gate.outcome = LocalAuthOutcome.failed;
      await toggleLock(tester);

      expect(gate.prompts, 1);
      expect(switchValue(tester), isTrue);
      expect(stored[AppLockService.enabledKey], isNull);

      gate.outcome = LocalAuthOutcome.authenticated;
      await toggleLock(tester);

      expect(gate.prompts, 2);
      expect(switchValue(tester), isFalse);
      expect(stored[AppLockService.enabledKey], 'false');
    });

    testWidgets('turning it back on does not prompt', (tester) async {
      stored[AppLockService.enabledKey] = 'false';
      await pumpProfile(tester);
      expect(switchValue(tester), isFalse);

      await toggleLock(tester);

      expect(gate.prompts, 0);
      expect(switchValue(tester), isTrue);
      expect(stored[AppLockService.enabledKey], 'true');
    });

    testWidgets('is hidden when the device cannot authenticate',
        (tester) async {
      gate.available = false;
      await pumpProfile(tester);

      expect(lockSwitch, findsNothing);
    });
  });

  group('Fayda menu entry', () {
    testWidgets('shows while the account is not verified', (tester) async {
      await pumpProfile(tester);

      expect(faydaEntry, findsOneWidget);
      expect(find.text('Verify with Fayda'), findsOneWidget);
    });

    testWidgets('is gone once the account is verified', (tester) async {
      await pumpProfile(tester, verified: true);

      expect(faydaEntry, findsNothing);
    });
  });
}

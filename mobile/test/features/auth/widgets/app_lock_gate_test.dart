import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/auth/widgets/app_lock_gate.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

void main() {
  late MockSecureStorage storage;
  late FakeGate gate;
  late AppLockService service;
  late DateTime clock;

  setUp(() {
    storage = MockSecureStorage();
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => null);
    gate = FakeGate();
    service = AppLockService(gate: gate, storage: storage);
    clock = DateTime.utc(2026, 9, 25, 8);
  });

  Widget app({bool lockOnStart = false}) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: AppLockGate(
          lockOnStart: lockOnStart,
          service: service,
          now: () => clock,
          child: const Scaffold(body: Text('home')),
        ),
      );

  final lockScreen = find.byKey(const Key('app_lock_screen'));

  testWidgets('a restored session unlocks after the device prompt',
      (tester) async {
    await tester.pumpWidget(app(lockOnStart: true));
    await tester.pumpAndSettle();

    expect(gate.prompts, 1);
    expect(lockScreen, findsNothing);
    expect(find.text('home'), findsOneWidget);
  });

  testWidgets('a failed prompt keeps the lock; Unlock asks again',
      (tester) async {
    gate.outcome = LocalAuthOutcome.failed;
    await tester.pumpWidget(app(lockOnStart: true));
    await tester.pumpAndSettle();

    expect(lockScreen, findsOneWidget);
    expect(find.text('WeredaConnect is locked'), findsOneWidget);

    gate.outcome = LocalAuthOutcome.authenticated;
    await tester.tap(find.byKey(const Key('app_lock_unlock_button')));
    await tester.pumpAndSettle();

    expect(gate.prompts, 2);
    expect(lockScreen, findsNothing);
  });

  testWidgets('a password login (not restored) does not prompt',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    expect(gate.prompts, 0);
    expect(lockScreen, findsNothing);
  });

  testWidgets('the lock switched off never prompts', (tester) async {
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => 'false');
    await tester.pumpWidget(app(lockOnStart: true));
    await tester.pumpAndSettle();

    expect(gate.prompts, 0);
  });

  testWidgets('returning after more than 5 minutes locks', (tester) async {
    gate.outcome = LocalAuthOutcome.failed;
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
    clock = clock.add(const Duration(minutes: 6));
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pumpAndSettle();

    expect(gate.prompts, 1);
    expect(lockScreen, findsOneWidget);
  });

  testWidgets('a short trip (Fayda browser, camera) does not lock',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
    clock = clock.add(const Duration(minutes: 2));
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pumpAndSettle();

    expect(gate.prompts, 0);
    expect(lockScreen, findsNothing);
  });

  group('Fayda browser flow', () {
    tearDown(AppLockService.resetExternalFlows);

    void leaveAndReturn(WidgetTester tester, {void Function()? whileAway}) {
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      clock = clock.add(const Duration(minutes: 12));
      whileAway?.call();
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    }

    testWidgets('a long Fayda trip does not lock', (tester) async {
      await tester.pumpWidget(app());
      await tester.pumpAndSettle();

      AppLockService.beginExternalFlow();
      leaveAndReturn(tester);
      await tester.pumpAndSettle();

      expect(gate.prompts, 0);
      expect(lockScreen, findsNothing);
    });

    testWidgets('does not lock when the deep link ends the flow before resume',
        (tester) async {
      await tester.pumpWidget(app());
      await tester.pumpAndSettle();

      AppLockService.beginExternalFlow();
      leaveAndReturn(tester, whileAway: AppLockService.endExternalFlow);
      await tester.pumpAndSettle();

      expect(gate.prompts, 0);
      expect(lockScreen, findsNothing);
    });

    testWidgets('a later long absence after the flow ended locks again',
        (tester) async {
      await tester.pumpWidget(app());
      await tester.pumpAndSettle();

      AppLockService.beginExternalFlow();
      leaveAndReturn(tester, whileAway: AppLockService.endExternalFlow);
      await tester.pumpAndSettle();
      leaveAndReturn(tester);
      await tester.pumpAndSettle();

      expect(gate.prompts, 1);
      expect(lockScreen, findsNothing);
    });
  });

  group('mounted above the Navigator (MaterialApp.builder)', () {
    Widget routedApp({bool active = true, bool lockOnStart = false}) =>
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          builder: (context, child) => AppLockGate(
            active: active,
            lockOnStart: lockOnStart,
            service: service,
            now: () => clock,
            child: child!,
          ),
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (_) => const _PaymentScreen(),
                  ),
                ),
                child: const Text('open payment'),
              ),
            ),
          ),
        );

    void backgroundFor(WidgetTester tester, Duration away) {
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      clock = clock.add(away);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    }

    testWidgets('a pushed route is covered and unusable until unlock',
        (tester) async {
      _PaymentScreen.taps = 0;
      await tester.pumpWidget(routedApp());
      await tester.pumpAndSettle();
      await tester.tap(find.text('open payment'));
      await tester.pumpAndSettle();
      expect(find.text('pay now'), findsOneWidget);

      // The user cancels the device prompt after a long absence.
      gate.outcome = LocalAuthOutcome.failed;
      backgroundFor(tester, const Duration(minutes: 6));
      await tester.pumpAndSettle();

      expect(gate.prompts, 1);
      expect(lockScreen, findsOneWidget);
      // The lock screen is above the pushed route: taps never reach it.
      await tester.tap(find.text('pay now'), warnIfMissed: false);
      await tester.pumpAndSettle();
      expect(_PaymentScreen.taps, 0);
      expect(
        tester.getSemantics(find.text('pay now')),
        isNot(matchesSemantics(label: 'pay now')),
      );

      gate.outcome = LocalAuthOutcome.authenticated;
      await tester.tap(find.byKey(const Key('app_lock_unlock_button')));
      await tester.pumpAndSettle();

      expect(lockScreen, findsNothing);
      // The route survived the lock and works again.
      await tester.tap(find.text('pay now'));
      await tester.pumpAndSettle();
      expect(_PaymentScreen.taps, 1);
    });

    testWidgets('a restored session covers the app before the prompt answers',
        (tester) async {
      gate.outcome = LocalAuthOutcome.failed;
      await tester.pumpWidget(routedApp(lockOnStart: true));
      // First frame, before the async availability check resolves.
      expect(lockScreen, findsOneWidget);
      await tester.pumpAndSettle();
      expect(lockScreen, findsOneWidget);
    });

    testWidgets('a restored session with the lock off uncovers itself',
        (tester) async {
      when(() => storage.read(key: AppLockService.enabledKey))
          .thenAnswer((_) async => 'false');
      await tester.pumpWidget(routedApp(lockOnStart: true));
      await tester.pumpAndSettle();

      expect(gate.prompts, 0);
      expect(lockScreen, findsNothing);
    });

    testWidgets('signed out (inactive) never locks', (tester) async {
      await tester.pumpWidget(routedApp(active: false, lockOnStart: true));
      await tester.pumpAndSettle();
      backgroundFor(tester, const Duration(minutes: 30));
      await tester.pumpAndSettle();

      expect(gate.prompts, 0);
      expect(lockScreen, findsNothing);
    });

    testWidgets('signing out removes a showing lock', (tester) async {
      gate.outcome = LocalAuthOutcome.failed;
      await tester.pumpWidget(routedApp(lockOnStart: true));
      await tester.pumpAndSettle();
      expect(lockScreen, findsOneWidget);

      await tester.pumpWidget(routedApp(active: false));
      await tester.pumpAndSettle();
      expect(lockScreen, findsNothing);
    });
  });
}

class _PaymentScreen extends StatelessWidget {
  const _PaymentScreen();

  static int taps = 0;

  @override
  Widget build(BuildContext context) => Scaffold(
        body: TextButton(
          onPressed: () => taps++,
          child: const Text('pay now'),
        ),
      );
}

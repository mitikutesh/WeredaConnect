import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/security_prompt_listener.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class MockAuthService extends Mock implements AuthService {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

const _old = AuthTokens(accessToken: 'a1', refreshToken: 'r1', idToken: 'i1');
const _fresh = AuthTokens(accessToken: 'a2', refreshToken: 'r2', idToken: 'i2');

const _signedIn = AuthAuthenticated(
  userId: 'buyer-1',
  tokens: _old,
  role: UserRole.buyer,
  userName: 'Abebe',
  phoneNumber: '+251911234567',
);

/// Pump enough frames for dialogs and async handlers without waiting for the
/// focused text field's cursor blink to stop (it never does).
Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

void main() {
  late MockAuthBloc bloc;
  late StreamController<AuthState> states;
  late SecurityEvents events;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeOrderEvent());
  });

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    events = SecurityEvents.forTesting();
    when(() => bloc.state).thenReturn(_signedIn);
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    // The real bloc stores the tokens then emits the updated session.
    when(() => bloc.add(any())).thenAnswer((invocation) {
      final event = invocation.positionalArguments.first;
      if (event is AuthStepUpSucceeded) {
        scheduleMicrotask(() => states.add(_signedIn.withTokens(event.tokens)));
      }
    });
  });

  // Not awaited: a broadcast close() waits for listeners the fake-async test
  // zone has already torn down.
  tearDown(() {
    unawaited(states.close());
  });

  Widget host(Widget child) => BlocProvider<AuthBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: child),
        ),
      );

  group('showStepUpDialog', () {
    testWidgets('re-logs in with the session phone and swaps the tokens',
        (tester) async {
      final calls = <(String, String)>[];
      var completed = 0;
      final sub = events.onStepUpCompleted.listen((_) => completed++);
      bool? outcome;

      await tester.pumpWidget(
        host(
          Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                outcome = await showStepUpDialog(
                  context,
                  securityEvents: events,
                  authenticate: (phone, password) async {
                    calls.add((phone, password));
                    return AuthResult.success(_fresh, userId: 'buyer-1');
                  },
                );
              },
              child: const Text('pay'),
            ),
          ),
        ),
      );
      await tester.tap(find.text('pay'));
      await settle(tester);

      expect(find.text("Confirm it's you"), findsOneWidget);
      expect(find.text('Phone: +251911234567'), findsOneWidget);

      await tester.enterText(
        find.byKey(const Key('step_up_password_field')),
        'Secret123',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('step_up_confirm_button')));
      await settle(tester);

      expect(calls, [('+251911234567', 'Secret123')]);
      verify(() => bloc.add(const AuthStepUpSucceeded(tokens: _fresh)))
          .called(1);
      expect(outcome, isTrue);
      expect(completed, 1);
      expect(find.text("Confirm it's you"), findsNothing);
      unawaited(sub.cancel());
    });

    testWidgets('a wrong password stays open with the error', (tester) async {
      bool? outcome;
      await tester.pumpWidget(
        host(
          Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                outcome = await showStepUpDialog(
                  context,
                  securityEvents: events,
                  authenticate: (_, __) async => AuthResult.failure(
                    AuthErrorCode.invalidCredentials,
                    'nope',
                  ),
                );
              },
              child: const Text('pay'),
            ),
          ),
        ),
      );
      await tester.tap(find.text('pay'));
      await settle(tester);
      await tester.enterText(
        find.byKey(const Key('step_up_password_field')),
        'wrong',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('step_up_confirm_button')));
      await settle(tester);

      expect(find.text('Invalid phone number or password'), findsOneWidget);
      verifyNever(() => bloc.add(any()));
      expect(outcome, isNull);

      await tester.tap(find.text('Cancel'));
      await settle(tester);
      expect(outcome, isFalse);
    });
  });

  /// Stands in for the Fayda screen: pops [result] when tapped.
  Widget faydaScreen(bool result) => Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () => Navigator.of(context).pop(result),
            child: const Text('fayda screen'),
          ),
        ),
      );

  group('showFaydaStepUpDialog', () {
    Future<StepUpOutcome?> run(
      WidgetTester tester, {
      required bool verifies,
      bool cancel = false,
    }) async {
      StepUpOutcome? outcome;
      await tester.pumpWidget(
        host(
          Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                outcome = await showFaydaStepUpDialog(
                  context,
                  securityEvents: events,
                  verificationScreen: (_) => faydaScreen(verifies),
                );
              },
              child: const Text('pay'),
            ),
          ),
        ),
      );
      await tester.tap(find.text('pay'));
      await settle(tester);
      expect(
        find.textContaining('Payments above 10,000 ETB need a Fayda login'),
        findsOneWidget,
      );
      // No password here: above 10,000 ETB only Fayda counts.
      expect(find.byKey(const Key('step_up_password_field')), findsNothing);
      if (cancel) {
        await tester.tap(find.text('Cancel'));
      } else {
        await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
        await settle(tester);
        await tester.tap(find.text('fayda screen'));
      }
      await settle(tester);
      return outcome;
    }

    testWidgets('a Fayda login is verified and retries queued payments',
        (tester) async {
      var completed = 0;
      final sub = events.onStepUpCompleted.listen((_) => completed++);

      expect(await run(tester, verifies: true), StepUpOutcome.verified);
      expect(completed, 1);
      unawaited(sub.cancel());
    });

    testWidgets('a failed Fayda login is not verified and retries nothing',
        (tester) async {
      var completed = 0;
      final sub = events.onStepUpCompleted.listen((_) => completed++);

      expect(await run(tester, verifies: false), StepUpOutcome.notVerified);
      expect(completed, 0);
      unawaited(sub.cancel());
    });

    testWidgets('cancelling the prompt is a decline', (tester) async {
      expect(
        await run(tester, verifies: true, cancel: true),
        StepUpOutcome.declined,
      );
    });
  });

  group('SecurityPromptListener', () {
    testWidgets('a background Fayda step-up opens Fayda, not the password',
        (tester) async {
      var completed = 0;
      final sub = events.onStepUpCompleted.listen((_) => completed++);
      await tester.pumpWidget(
        host(
          SecurityPromptListener(
            events: events,
            verificationScreen: (_) => faydaScreen(true),
            child: const Text('app'),
          ),
        ),
      );

      events.emit(const StepUpRequired(withFayda: true));
      await settle(tester);
      expect(find.byKey(const Key('step_up_password_field')), findsNothing);
      await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
      await settle(tester);
      await tester.tap(find.text('fayda screen'));
      await settle(tester);

      expect(completed, 1);
      expect(find.text('Confirmed. Retrying your payment.'), findsOneWidget);
      unawaited(sub.cancel());
    });

    Future<void> tryFayda(WidgetTester tester, {required bool cancel}) async {
      events.emit(const StepUpRequired(withFayda: true));
      await settle(tester);
      if (cancel) {
        await tester.tap(find.text('Cancel'));
      } else {
        await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
        await settle(tester);
        await tester.tap(find.text('fayda screen'));
      }
      await settle(tester);
    }

    testWidgets('a Fayda attempt that did not verify keeps prompting',
        (tester) async {
      await tester.pumpWidget(
        host(
          SecurityPromptListener(
            events: events,
            verificationScreen: (_) => faydaScreen(false),
            child: const Text('app'),
          ),
        ),
      );

      await tryFayda(tester, cancel: false);

      // Not a "no": the next background sync asks again.
      expect(events.stepUpSuppressed, isFalse);
      events.emit(const StepUpRequired(withFayda: true));
      await settle(tester);
      expect(
        find.byKey(const Key('fayda_step_up_verify_button')),
        findsOneWidget,
      );
    });

    testWidgets('cancelling the Fayda prompt starts the quiet period',
        (tester) async {
      await tester.pumpWidget(
        host(
          SecurityPromptListener(
            events: events,
            verificationScreen: (_) => faydaScreen(true),
            child: const Text('app'),
          ),
        ),
      );

      await tryFayda(tester, cancel: true);

      expect(events.stepUpSuppressed, isTrue);
    });

    testWidgets('a background StepUpRequired opens the step-up dialog',
        (tester) async {
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events.emit(const StepUpRequired());
      await settle(tester);

      expect(find.text("Confirm it's you"), findsOneWidget);
    });

    testWidgets('a KYC rejection explains and offers Fayda', (tester) async {
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events.emit(
        const KycRequired(
          orderId: 'o-1',
          message: 'ORDER_KYC_REQUIRED: Fayda needed',
        ),
      );
      await settle(tester);

      expect(find.text('Verification needed'), findsOneWidget);
      expect(
        find.textContaining('Payments above 10,000 ETB need a verified Fayda'),
        findsOneWidget,
      );
      expect(find.byKey(const Key('kyc_verify_button')), findsOneWidget);
    });

    group('KYC verified in the background prompt', () {
      const rejected = KycRequired(
        orderId: 'o-1',
        message: 'ORDER_KYC_REQUIRED: buyer_fayda: Verify your Fayda ID',
        paymentMethod: 'telebirr',
        paymentReference: 'MOCK-CBE-1',
      );

      Widget fayda(bool result) => Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).pop(result),
                child: const Text('fayda screen'),
              ),
            ),
          );

      Future<List<KycRequired>> run(
        WidgetTester tester,
        KycRequired event, {
        required bool verifies,
      }) async {
        final resubmitted = <KycRequired>[];
        await tester.pumpWidget(
          host(
            SecurityPromptListener(
              events: events,
              verificationScreen: (_) => fayda(verifies),
              resubmitPayment: (_, e) async => resubmitted.add(e),
              child: const Text('app'),
            ),
          ),
        );
        events.emit(event);
        await settle(tester);
        if (event.reason.buyerCanFix) {
          await tester.tap(find.byKey(const Key('kyc_verify_button')));
          await settle(tester);
          await tester.tap(find.text('fayda screen'));
        } else {
          await tester.tap(find.byKey(const Key('kyc_ok_button')));
        }
        await settle(tester);
        return resubmitted;
      }

      testWidgets('verified as the owner: the same payment is sent again',
          (tester) async {
        final resubmitted = await run(tester, rejected, verifies: true);

        expect(resubmitted.single.orderId, 'o-1');
        expect(resubmitted.single.paymentMethod, 'telebirr');
        expect(resubmitted.single.paymentReference, 'MOCK-CBE-1');
        expect(
          find.text('Identity verified. Your payment will be sent again.'),
          findsOneWidget,
        );
      });

      Future<void> verifyWithDefaultResend(
        WidgetTester tester,
        MockOrderBloc orderBloc,
      ) async {
        await tester.pumpWidget(
          BlocProvider<OrderBloc>.value(
            value: orderBloc,
            child: host(
              SecurityPromptListener(
                events: events,
                verificationScreen: (_) => fayda(true),
                child: const Text('app'),
              ),
            ),
          ),
        );
        events.emit(rejected);
        await settle(tester);
        await tester.tap(find.byKey(const Key('kyc_verify_button')));
        await settle(tester);
        await tester.tap(find.text('fayda screen'));
        await settle(tester);
      }

      testWidgets(
          'default resend: queues the same payment on the app OrderBloc '
          'repository', (tester) async {
        final repo = MockOrderRepository();
        final orderBloc = MockOrderBloc();
        when(() => orderBloc.repository).thenReturn(repo);
        when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
        when(
          () => repo.submitPayment(
            any(),
            any(),
            paymentMethod: any(named: 'paymentMethod'),
          ),
        ).thenAnswer((_) async {});

        await verifyWithDefaultResend(tester, orderBloc);

        verify(
          () => repo.submitPayment(
            'o-1',
            'MOCK-CBE-1',
            paymentMethod: 'telebirr',
          ),
        ).called(1);
        // The shared bloc is not driven (no state change for open screens).
        verifyNever(() => orderBloc.add(any()));
        expect(
          find.text('Identity verified. Your payment will be sent again.'),
          findsOneWidget,
        );
      });

      testWidgets('default resend failure: asks to pay again by hand',
          (tester) async {
        final repo = MockOrderRepository();
        final orderBloc = MockOrderBloc();
        when(() => orderBloc.repository).thenReturn(repo);
        when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
        when(
          () => repo.submitPayment(
            any(),
            any(),
            paymentMethod: any(named: 'paymentMethod'),
          ),
        ).thenThrow(StateError('order gone'));

        await verifyWithDefaultResend(tester, orderBloc);

        expect(find.textContaining('submit the payment again'), findsOneWidget);
      });

      testWidgets('verification failed: nothing is sent', (tester) async {
        final resubmitted = await run(tester, rejected, verifies: false);

        expect(resubmitted, isEmpty);
      });

      testWidgets('farmer not verified: no Fayda offered, nothing is sent',
          (tester) async {
        final resubmitted = await run(
          tester,
          const KycRequired(
            orderId: 'o-1',
            message: 'ORDER_KYC_REQUIRED: farmer_fayda: The farmer must verify',
            paymentMethod: 'cbe_transfer',
            paymentReference: 'MOCK-CBE-1',
          ),
          verifies: true,
        );

        expect(resubmitted, isEmpty);
      });

      testWidgets('verified but no payload: asks to pay again by hand',
          (tester) async {
        final resubmitted = await run(
          tester,
          const KycRequired(
            orderId: 'o-1',
            message: 'ORDER_KYC_REQUIRED: buyer_fayda: Verify',
          ),
          verifies: true,
        );

        expect(resubmitted, isEmpty);
        expect(
          find.textContaining('submit the payment again'),
          findsOneWidget,
        );
      });
    });

    testWidgets('prompts nothing when signed out', (tester) async {
      when(() => bloc.state).thenReturn(const AuthUnauthenticated());
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events.emit(const StepUpRequired());
      await settle(tester);

      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('repeated events while a prompt is open show one dialog',
        (tester) async {
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events
        ..emit(const StepUpRequired())
        ..emit(const StepUpRequired())
        ..emit(
          const KycRequired(orderId: 'o-1', message: 'ORDER_KYC_REQUIRED'),
        );
      await settle(tester);

      expect(find.text("Confirm it's you"), findsOneWidget);
      expect(find.text('Verification needed'), findsNothing);
      expect(find.byType(AlertDialog), findsOneWidget);
    });

    testWidgets('a confirmed step-up shows the retrying snackbar',
        (tester) async {
      final service = MockAuthService();
      when(() => bloc.authService).thenReturn(service);
      when(
        () => service.loginOnline(
          phoneNumber: any(named: 'phoneNumber'),
          password: any(named: 'password'),
        ),
      ).thenAnswer((_) async => AuthResult.success(_fresh, userId: 'buyer-1'));
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events.emit(const StepUpRequired());
      await settle(tester);
      await tester.enterText(
        find.byKey(const Key('step_up_password_field')),
        'Secret123',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('step_up_confirm_button')));
      await settle(tester);

      expect(find.text("Confirm it's you"), findsNothing);
      expect(find.text('Confirmed. Retrying your payment.'), findsOneWidget);
    });

    testWidgets('a cancelled prompt stays quiet for the cooldown',
        (tester) async {
      var clock = DateTime.utc(2026, 9, 26, 9);
      events = SecurityEvents.forTesting(now: () => clock);
      await tester.pumpWidget(
        host(SecurityPromptListener(events: events, child: const Text('app'))),
      );

      events.emit(const StepUpRequired());
      await settle(tester);
      await tester.tap(find.text('Cancel'));
      await settle(tester);
      expect(find.byType(AlertDialog), findsNothing);

      // Later background syncs inside the cooldown do not reopen it.
      clock = clock.add(const Duration(minutes: 9));
      events.emit(const StepUpRequired());
      await settle(tester);
      expect(find.byType(AlertDialog), findsNothing);

      // After the cooldown the next sync prompts again.
      clock = clock.add(const Duration(minutes: 2));
      events.emit(const StepUpRequired());
      await settle(tester);
      expect(find.text("Confirm it's you"), findsOneWidget);
    });
  });

  group('SecurityEvents step-up cooldown', () {
    test('a completed step-up ends the quiet period', () {
      final clock = DateTime.utc(2026, 9, 26, 9);
      final bus = SecurityEvents.forTesting(now: () => clock);
      final seen = <SecurityEvent>[];
      final sub = bus.events.listen(seen.add);

      bus.stepUpDeclined();
      expect(bus.stepUpSuppressed, isTrue);
      bus.stepUpCompleted();
      expect(bus.stepUpSuppressed, isFalse);
      unawaited(sub.cancel());
    });

    test('KYC events are never suppressed', () async {
      final clock = DateTime.utc(2026, 9, 26, 9);
      final bus = SecurityEvents.forTesting(now: () => clock);
      final seen = <SecurityEvent>[];
      final sub = bus.events.listen(seen.add);

      bus
        ..stepUpDeclined()
        ..emit(const StepUpRequired())
        ..emit(const KycRequired(orderId: 'o-1', message: 'x'));
      await Future<void>.delayed(Duration.zero);

      expect(seen, hasLength(1));
      expect(seen.single, isA<KycRequired>());
      await sub.cancel();
    });
  });
}

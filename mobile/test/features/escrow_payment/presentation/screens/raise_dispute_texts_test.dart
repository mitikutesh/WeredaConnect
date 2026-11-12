import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart'
    show escrowDisputeRefundScheduledCode;
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/raise_dispute_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockEscrowBloc extends Mock implements EscrowBloc {}

class _MockAuthBloc extends Mock implements AuthBloc {}

class _FakeEscrowEvent extends Fake implements EscrowEvent {}

/// FUP-77: the texts of the dispute screen name the dispute. The step-up
/// prompts (AUTH_040 password, AUTH_041 Fayda) no longer say "this payment",
/// and a dispute refused because the refund is scheduled says so. The buyer
/// and the farmer both raise disputes here; the trucker has no access.
void main() {
  late _MockEscrowBloc escrowBloc;
  late _MockAuthBloc authBloc;
  late AppLocalizations en;

  setUpAll(() async {
    registerFallbackValue(_FakeEscrowEvent());
    en = await AppLocalizations.delegate.load(const Locale('en'));
  });

  setUp(() {
    escrowBloc = _MockEscrowBloc();
    authBloc = _MockAuthBloc();
    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  /// Submits a dispute as [role], then answers it with [error].
  Future<void> disputeRefused(
    WidgetTester tester,
    UserRole role,
    EscrowError error,
  ) async {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: '${role.name}-001',
        tokens: const AuthTokens(
          accessToken: 'a',
          refreshToken: 'r',
          idToken: 'i',
        ),
        role: role,
        phoneNumber: '+251911234567',
      ),
    );
    final states = StreamController<EscrowState>.broadcast();
    addTearDown(states.close);
    when(() => escrowBloc.stream).thenAnswer((_) => states.stream);
    when(() => escrowBloc.add(any())).thenReturn(null);
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<EscrowBloc>.value(value: escrowBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: RaiseDisputeScreen(
            escrowId: 'esc-001',
            amount: 25000,
            deliveryId: 'del-001-long-id',
            disputeWindowEndsAt:
                DateTime.now().toUtc().add(const Duration(hours: 24)),
            faydaVerificationScreen: (_) => const Text('fayda screen'),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byType(RadioListTile<String>).first);
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.byIcon(Icons.warning_amber));
    await tester.pumpAndSettle();
    await tester.tap(find.byIcon(Icons.warning_amber));
    await tester.pumpAndSettle();
    await tester.tap(
      find.descendant(
        of: find.byType(AlertDialog),
        matching: find.text('Submit Dispute'),
      ),
    );
    await tester.pumpAndSettle();

    states.add(error);
    // Fixed frames: the password field's cursor never stops blinking.
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  }

  for (final role in [UserRole.buyer, UserRole.farmer]) {
    testWidgets('${role.name}: the Fayda prompt names the dispute',
        (tester) async {
      await disputeRefused(
        tester,
        role,
        const EscrowError(code: 'AUTH_041', message: 'Fayda needed'),
      );

      expect(
        find.byKey(const Key('fayda_step_up_verify_button')),
        findsOneWidget,
      );
      expect(find.text(en.faydaStepUpDisputeMessage), findsOneWidget);
      expect(find.text(en.faydaStepUpMessage), findsNothing);
      expect(find.textContaining('payment'), findsNothing);
    });

    testWidgets('${role.name}: the password prompt names the dispute',
        (tester) async {
      await disputeRefused(
        tester,
        role,
        const EscrowError(code: 'AUTH_040', message: 'step-up'),
      );

      expect(find.byKey(const Key('step_up_password_field')), findsOneWidget);
      expect(find.text(en.stepUpDisputeMessage), findsOneWidget);
      expect(find.text(en.stepUpRequiredMessage), findsNothing);
    });

    testWidgets('${role.name}: a scheduled refund is named in the refusal',
        (tester) async {
      await disputeRefused(
        tester,
        role,
        const EscrowError(
          code: escrowDisputeRefundScheduledCode,
          message: 'A refund to the buyer is scheduled for this escrow; it '
              'can no longer be disputed.',
        ),
      );

      expect(find.text(en.escrowErrorDisputeRefundScheduled), findsOneWidget);
      expect(find.text(en.escrowErrorInvalidTransition), findsNothing);
    });
  }

  test('the dispute prompts have their own text in en, am and om', () async {
    for (final locale in AppLocalizations.supportedLocales) {
      final l10n = await AppLocalizations.delegate.load(locale);
      final reason = locale.languageCode;

      expect(l10n.stepUpDisputeMessage.trim(), isNotEmpty, reason: reason);
      expect(
        l10n.stepUpDisputeMessage,
        isNot(l10n.stepUpRequiredMessage),
        reason: reason,
      );
      expect(
        l10n.faydaStepUpDisputeMessage.trim(),
        isNotEmpty,
        reason: reason,
      );
      expect(
        l10n.faydaStepUpDisputeMessage,
        isNot(l10n.faydaStepUpMessage),
        reason: reason,
      );
    }
    // am and om are translated, not copies of the English.
    final am = await AppLocalizations.delegate.load(const Locale('am'));
    final om = await AppLocalizations.delegate.load(const Locale('om'));
    for (final l10n in [am, om]) {
      expect(l10n.stepUpDisputeMessage, isNot(en.stepUpDisputeMessage));
      expect(
        l10n.faydaStepUpDisputeMessage,
        isNot(en.faydaStepUpDisputeMessage),
      );
      expect(
        l10n.escrowErrorDisputeRefundScheduled,
        isNot(en.escrowErrorDisputeRefundScheduled),
      );
      expect(
        l10n.errorOrderCancelPaymentBusy,
        isNot(en.errorOrderCancelPaymentBusy),
      );
      expect(l10n.kycUnknownReasonMessage, isNot(en.kycUnknownReasonMessage));
    }
  });
}

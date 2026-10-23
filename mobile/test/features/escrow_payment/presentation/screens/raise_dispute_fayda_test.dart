import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/raise_dispute_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

/// FRD-06: AUTH_041 on a dispute opens the Fayda login (never the password
/// prompt, which cannot satisfy it) and sends the same dispute again.
void main() {
  late MockEscrowBloc escrowBloc;
  late MockAuthBloc authBloc;

  setUpAll(() => registerFallbackValue(FakeEscrowEvent()));

  setUp(() {
    escrowBloc = MockEscrowBloc();
    authBloc = MockAuthBloc();
    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
        role: UserRole.buyer,
      ),
    );
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  Future<List<EscrowEvent>> disputeThenFayda(
    WidgetTester tester, {
    required bool verifies,
  }) async {
    final states = StreamController<EscrowState>.broadcast();
    addTearDown(states.close);
    final sent = <EscrowEvent>[];
    when(() => escrowBloc.stream).thenAnswer((_) => states.stream);
    when(() => escrowBloc.add(any())).thenAnswer(
      (inv) => sent.add(inv.positionalArguments.first as EscrowEvent),
    );
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
            faydaVerificationScreen: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).pop(verifies),
                child: const Text('fayda screen'),
              ),
            ),
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
    expect(sent.whereType<RaisePaymentDispute>(), hasLength(1));

    states.add(const EscrowError(code: 'AUTH_041', message: 'Fayda needed'));
    await tester.pumpAndSettle();
    // The Fayda prompt, not the password one.
    expect(find.byKey(const Key('step_up_password_field')), findsNothing);
    await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();
    return sent;
  }

  testWidgets('a Fayda login sends the same dispute again', (tester) async {
    final sent = await disputeThenFayda(tester, verifies: true);

    final disputes = sent.whereType<RaisePaymentDispute>().toList();
    expect(disputes, hasLength(2));
    expect(disputes.last, same(disputes.first));
  });

  testWidgets('a failed Fayda login sends nothing more', (tester) async {
    final sent = await disputeThenFayda(tester, verifies: false);

    expect(sent.whereType<RaisePaymentDispute>(), hasLength(1));
  });
}

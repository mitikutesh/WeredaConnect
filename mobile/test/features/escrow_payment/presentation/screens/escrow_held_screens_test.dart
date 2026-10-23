import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

Payment _payment(
  PaymentStatus status, {
  String id = 'escrow-order-ord-1',
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = id
    ..deliveryId = 'del-001-long-delivery-id'
    ..orderId = 'ord-1'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..truckerId = 'trucker-001'
    ..amount = 1500
    ..commission = 37.5
    ..commissionRate = 0.025
    ..farmerPayout = 1262.5
    ..truckerPayout = 200
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    // Inside the window: an escrowed payment would offer a dispute here.
    ..disputeWindowEndsAt = now.add(const Duration(hours: 24))
    ..version = 1;
}

/// FRD-06: the escrow detail and the payment list show "Under Review" for a
/// held escrow, to every party, and offer no action the server refuses.
void main() {
  late MockEscrowBloc escrowBloc;
  late MockAuthBloc authBloc;

  setUpAll(() => registerFallbackValue(FakeEscrowEvent()));

  setUp(() {
    escrowBloc = MockEscrowBloc();
    authBloc = MockAuthBloc();
    when(() => escrowBloc.add(any())).thenReturn(null);
    when(() => escrowBloc.stream)
        .thenAnswer((_) => const Stream<EscrowState>.empty());
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  void signIn(UserRole role) => when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: '${role.name}-001',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
        ),
      );

  Widget host(Widget home) => MultiBlocProvider(
        providers: [
          BlocProvider<EscrowBloc>.value(value: escrowBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: home,
        ),
      );

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  group('EscrowStatusScreen', () {
    testWidgets('an escrowed payment in its window offers a dispute (control)',
        (tester) async {
      tallView(tester);
      when(() => escrowBloc.state).thenReturn(
        EscrowDetailLoaded(payment: _payment(PaymentStatus.escrowed)),
      );

      await tester.pumpWidget(
        host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
      );
      await tester.pumpAndSettle();

      expect(find.text('Raise Dispute'), findsOneWidget);
    });

    testWidgets('held: Under Review, no dispute', (tester) async {
      tallView(tester);
      when(() => escrowBloc.state).thenReturn(
        EscrowDetailLoaded(payment: _payment(PaymentStatus.held)),
      );

      await tester.pumpWidget(
        host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
      );
      await tester.pumpAndSettle();

      expect(find.text('UNDER REVIEW'), findsOneWidget);
      expect(
        find.text(
          'Payment paused while the platform reviews it. The money stays safe.',
        ),
        findsOneWidget,
      );
      expect(find.text('Raise Dispute'), findsNothing);
    });

    testWidgets('a status the app does not know: neutral, no action',
        (tester) async {
      tallView(tester);
      when(() => escrowBloc.state).thenReturn(
        EscrowDetailLoaded(payment: _payment(PaymentStatus.unknown)),
      );

      await tester.pumpWidget(
        host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
      );
      await tester.pumpAndSettle();

      expect(find.text('STATUS UNKNOWN'), findsOneWidget);
      expect(find.text('Raise Dispute'), findsNothing);
      expect(find.text('Payment Pending'), findsWidgets);
    });
  });

  group('PaymentHistoryScreen', () {
    for (final role in const [
      UserRole.buyer,
      UserRole.farmer,
      UserRole.trucker,
    ]) {
      testWidgets('${role.name} sees Under Review on a held payment',
          (tester) async {
        signIn(role);
        when(() => escrowBloc.state).thenReturn(
          PaymentHistoryLoaded(
            payments: [
              _payment(PaymentStatus.held),
              _payment(PaymentStatus.completed, id: 'escrow-order-ord-2'),
            ],
          ),
        );

        await tester.pumpWidget(host(const PaymentHistoryScreen()));
        await tester.pumpAndSettle();

        expect(find.text('Under Review'), findsOneWidget);
      });
    }
  });
}

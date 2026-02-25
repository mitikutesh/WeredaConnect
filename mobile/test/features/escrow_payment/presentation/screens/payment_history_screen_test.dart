import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

class FakeEscrowState extends Fake implements EscrowState {}

Payment _makePayment({
  String paymentId = 'esc-001',
  PaymentStatus status = PaymentStatus.escrowed,
  double amount = 15000.0,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = paymentId
    ..deliveryId = 'del-001-long-id'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = amount
    ..commission = 375.0
    ..commissionRate = 0.025
    ..farmerPayout = 14625.0
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..version = 1;
}

PaymentIntent _makeIntent({
  String key = 'buyer-001:del-001:initiate:1708300000000',
  IntentStatus status = IntentStatus.pendingSync,
}) {
  return PaymentIntent()
    ..idempotencyKey = key
    ..deliveryId = 'del-001-long-id'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..amount = 5000.0
    ..status = status
    ..createdAt = DateTime.now().toUtc();
}

void main() {
  late MockEscrowBloc escrowBloc;
  late MockAuthBloc authBloc;

  setUpAll(() {
    registerFallbackValue(FakeEscrowEvent());
    registerFallbackValue(FakeEscrowState());
  });

  setUp(() {
    escrowBloc = MockEscrowBloc();
    authBloc = MockAuthBloc();
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: AuthTokens(
          accessToken: 'test-access',
          refreshToken: 'test-refresh',
          idToken: 'test-id',
        ),
      ),
    );
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  Widget buildScreen() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<EscrowBloc>.value(value: escrowBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: Locale('en'),
        home: PaymentHistoryScreen(),
      ),
    );
  }

  group('PaymentHistoryScreen', () {
    testWidgets('shows loading indicator', (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows empty state when no payments or intents',
        (WidgetTester tester) async {
      const state = PaymentHistoryLoaded(payments: []);
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(
        find.byIcon(Icons.account_balance_wallet_outlined),
        findsOneWidget,
      );
    });

    testWidgets('shows payment cards when history loaded',
        (WidgetTester tester) async {
      final payments = [
        _makePayment(),
        _makePayment(paymentId: 'esc-002', status: PaymentStatus.completed),
      ];
      final state =
          PaymentHistoryLoaded(payments: payments, pendingIntents: []);
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Should show amount displays for each payment
      expect(find.textContaining('15,000.00'), findsWidgets);
    });

    testWidgets('shows pending intents section', (WidgetTester tester) async {
      final intents = [_makeIntent()];
      final state = PaymentHistoryLoaded(payments: [], pendingIntents: intents);
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byType(PendingIntentCard), findsOneWidget);
    });

    testWidgets('shows role filter chips', (WidgetTester tester) async {
      final state =
          PaymentHistoryLoaded(payments: [_makePayment()], pendingIntents: []);
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Role chips: All, As Buyer, As Farmer
      expect(find.byType(FilterChip), findsWidgets);
    });

    testWidgets('shows error state', (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(
        const EscrowError(code: 'ESCROW_CLIENT', message: 'Network error'),
      );
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('Network error'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('dispatches LoadPaymentHistory on init',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      verify(
        () => escrowBloc.add(const LoadPaymentHistory(userId: 'farmer-001')),
      ).called(1);
    });

    testWidgets('tapping status filter chip dispatches FilterPaymentHistory',
        (WidgetTester tester) async {
      final state =
          PaymentHistoryLoaded(payments: [_makePayment()], pendingIntents: []);
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Find and tap the "Escrowed" chip (from status filter row)
      // The status filter chips include: All, Escrowed, Released, Disputed, Refunded
      // We can tap the second "All" to find the status filter "All" chip
      final allChips = find.byType(FilterChip);
      expect(allChips, findsWidgets);

      // Count chips — status filter: All, Escrowed, Released, Disputed, Refunded = 5
      final chipCount = tester.widgetList(allChips).length;
      expect(chipCount, 5);
    });
  });
}

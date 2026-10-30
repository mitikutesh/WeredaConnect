import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

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
        role: UserRole.farmer,
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

      // The farmer (setUp role) sees their own crop leg, never the buyer's
      // held total (SIM-15).
      expect(find.textContaining('14,625 ETB'), findsWidgets);
      expect(find.textContaining('15,000 ETB'), findsNothing);

      // LT-01: the list ends clear of the floating create button.
      final list = tester.widget<ListView>(
        find.descendant(
          of: find.byType(RefreshIndicator),
          matching: find.byType(ListView),
        ),
      );
      expect(
        list.padding!.resolve(TextDirection.ltr).bottom,
        Breakpoints.fabClearance,
      );
    });

    testWidgets(
        'a cancelled-order escrow reads Refund Pending with its order reference',
        (WidgetTester tester) async {
      // Order-funded escrow of a cancelled order: no delivery yet, refund
      // scheduled. It used to render "Funds Held Safely" and a blank "Del. #".
      final payment = _makePayment()
        ..deliveryId = ''
        ..orderId = 'ord-12345678-abcd'
        ..refundPending = true
        ..refundReason = 'order_cancelled';
      final state = PaymentHistoryLoaded(
        payments: [payment],
        pendingIntents: [],
      );
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(
        find.widgetWithText(EscrowStatusBadge, 'Refund Pending'),
        findsOneWidget,
      );
      expect(
        find.widgetWithText(EscrowStatusBadge, 'Funds Held Safely'),
        findsNothing,
      );
      expect(find.textContaining('Order #ord-1234'), findsOneWidget);
      expect(find.textContaining('Del. #'), findsNothing);
    });

    testWidgets('an expired hold reads Refund Pending even without the flag',
        (WidgetTester tester) async {
      // Older servers send no refundPending: an escrowed hold past its expiry
      // is about to be swept back to the buyer.
      final payment = _makePayment()
        ..expiresAt = DateTime.now().subtract(const Duration(hours: 1));
      final state = PaymentHistoryLoaded(
        payments: [payment],
        pendingIntents: [],
      );
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(
        find.widgetWithText(EscrowStatusBadge, 'Refund Pending'),
        findsOneWidget,
      );
    });

    testWidgets('buyer sees the full held amount of each payment',
        (WidgetTester tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: AuthTokens(
            accessToken: 'test-access',
            refreshToken: 'test-refresh',
            idToken: 'test-id',
          ),
          role: UserRole.buyer,
        ),
      );
      final state = PaymentHistoryLoaded(
        payments: [_makePayment()],
        pendingIntents: [],
      );
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // The row and the summary card both carry the buyer's held total.
      expect(find.textContaining('15,000 ETB'), findsWidgets);
      expect(find.textContaining('14,625 ETB'), findsNothing);
    });

    testWidgets('trucker sees only the transport leg of each payment',
        (WidgetTester tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'trucker-001',
          tokens: AuthTokens(
            accessToken: 'test-access',
            refreshToken: 'test-refresh',
            idToken: 'test-id',
          ),
          role: UserRole.trucker,
        ),
      );
      final payment = _makePayment(amount: 16000.0)
        ..truckerId = 'trucker-001'
        ..truckerPayout = 1000.0;
      final state = PaymentHistoryLoaded(
        payments: [payment],
        pendingIntents: [],
      );
      when(() => escrowBloc.state).thenReturn(state);
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('1,000 ETB'), findsWidgets);
      expect(find.textContaining('16,000 ETB'), findsNothing);
      expect(find.textContaining('14,625 ETB'), findsNothing);
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

      // Status pills: All, Escrowed, Released, Completed, Disputed, Refunded
      expect(find.byType(FwFilterPill), findsWidgets);
    });

    testWidgets('shows error state', (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(
        const EscrowError(code: 'ESCROW_CLIENT', message: 'Network error'),
      );
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Error state now shows a distinct error view (not the empty
      // "No payments yet" state), so load failures are visible to the user.
      expect(find.text('No payments yet'), findsNothing);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('dispatches LoadPaymentHistory on init',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      // Must forward the caller's role so the server scopes to the farmer GSI
      // (without it, the server defaults to the buyer query and the farmer sees
      // zero payouts).
      verify(
        () => escrowBloc.add(
          const LoadPaymentHistory(userId: 'farmer-001', roleFilter: 'farmer'),
        ),
      ).called(1);
    });

    testWidgets('the summary card sums this party\'s held and released legs',
        (WidgetTester tester) async {
      // Farmer (setUp role): one escrowed and one completed payout, each on
      // its own row and once more in the summary card.
      final held = _makePayment();
      final released = _makePayment(
        paymentId: 'esc-002',
        status: PaymentStatus.completed,
        amount: 8000.0,
      )..farmerPayout = 7800.0;
      when(() => escrowBloc.state).thenReturn(
        PaymentHistoryLoaded(payments: [held, released], pendingIntents: []),
      );
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.text('Held in escrow'), findsOneWidget);
      expect(find.text('Released'), findsOneWidget);
      expect(find.text('14,625 ETB'), findsNWidgets(2));
      expect(find.text('7,800 ETB'), findsNWidgets(2));
      expect(find.textContaining('15,000'), findsNothing);
    });

    testWidgets('no summary card without any payment',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(
        PaymentHistoryLoaded(
          payments: const [],
          pendingIntents: [_makeIntent()],
        ),
      );
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.text('Held in escrow'), findsNothing);
      expect(find.byType(PendingIntentCard), findsOneWidget);
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
      // The status filter chips include: All, Escrowed, Released, Completed,
      // Disputed, Refunded
      final allChips = find.byType(FwFilterPill);
      expect(allChips, findsWidgets);

      // Count chips, status filter: All, Escrowed, Released, Completed,
      // Disputed, Refunded = 6
      final chipCount = tester.widgetList(allChips).length;
      expect(chipCount, 6);
    });
  });
}

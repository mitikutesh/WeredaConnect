import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

class FakeEscrowState extends Fake implements EscrowState {}

Payment _makePayment({
  PaymentStatus status = PaymentStatus.escrowed,
  double amount = 15000.0,
  double commission = 375.0,
  double farmerPayout = 14625.0,
  double? truckerPayout,
  DateTime? disputeWindowEndsAt,
  DateTime? updatedAt,
  String? disputeReason,
  String? disputeDescription,
  String? telebirrRef,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = 'esc-001'
    ..deliveryId = 'del-001-long-delivery-id'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = amount
    ..commission = commission
    ..commissionRate = 0.025
    ..farmerPayout = farmerPayout
    ..truckerPayout = truckerPayout
    ..status = status
    ..createdAt = now
    ..updatedAt = updatedAt ?? now
    ..expiresAt = now.add(const Duration(days: 7))
    ..disputeWindowEndsAt = disputeWindowEndsAt
    ..disputeReason = disputeReason
    ..disputeDescription = disputeDescription
    ..telebirrRef = telebirrRef
    ..version = 1;
}

void main() {
  late MockEscrowBloc escrowBloc;

  setUpAll(() {
    registerFallbackValue(FakeEscrowEvent());
    registerFallbackValue(FakeEscrowState());
  });

  setUp(() {
    escrowBloc = MockEscrowBloc();
  });

  Widget buildScreen({String escrowId = 'esc-001'}) {
    return BlocProvider<EscrowBloc>.value(
      value: escrowBloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: EscrowStatusScreen(escrowId: escrowId),
      ),
    );
  }

  group('EscrowStatusScreen', () {
    testWidgets('shows loading indicator when state is EscrowLoading',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows error state with retry button',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(
        const EscrowError(code: 'ESCROW_016', message: 'Payment not found.'),
      );
      when(() => escrowBloc.stream).thenAnswer(
        (_) => Stream.value(
          const EscrowError(
            code: 'ESCROW_016',
            message: 'Payment not found.',
          ),
        ),
      );

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('Payment not found'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('shows status badge and timeline when detail loaded',
        (WidgetTester tester) async {
      final payment = _makePayment();
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byType(EscrowStatusBadge), findsOneWidget);
      expect(find.byType(EscrowTimeline), findsOneWidget);
    });

    testWidgets('shows amount breakdown', (WidgetTester tester) async {
      final payment = _makePayment();
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('15,000.00'), findsWidgets);
      expect(find.textContaining('375.00'), findsOneWidget);
      expect(find.textContaining('14,625.00'), findsOneWidget);
    });

    testWidgets('shows the trucker leg when the order had a trucker split',
        (WidgetTester tester) async {
      // 16,000 = 375 commission + 14,625 farmer + 1,000 trucker (SIM-15).
      final payment = _makePayment(amount: 16000.0, truckerPayout: 1000.0);
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.text('Trucker Receives'), findsOneWidget);
      expect(find.textContaining('1,000.00'), findsOneWidget);
      expect(find.textContaining('14,625.00'), findsOneWidget);
    });

    testWidgets('hides the trucker leg without a trucker split',
        (WidgetTester tester) async {
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: _makePayment()));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.text('Trucker Receives'), findsNothing);
    });

    testWidgets('shows dispute button for escrowed payment within window',
        (WidgetTester tester) async {
      final payment = _makePayment(
        disputeWindowEndsAt:
            DateTime.now().toUtc().add(const Duration(hours: 24)),
      );
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byType(OutlinedButton), findsOneWidget);
      expect(find.byIcon(Icons.warning_amber), findsOneWidget);
    });

    testWidgets('hides dispute button when not escrowed',
        (WidgetTester tester) async {
      final payment = _makePayment(status: PaymentStatus.completed);
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byType(OutlinedButton), findsNothing);
    });

    testWidgets('hides dispute button when dispute window expired',
        (WidgetTester tester) async {
      final payment = _makePayment(
        disputeWindowEndsAt:
            DateTime.now().toUtc().subtract(const Duration(hours: 1)),
      );
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byType(OutlinedButton), findsNothing);
    });

    testWidgets('shows dispute info when status is disputed',
        (WidgetTester tester) async {
      final payment = _makePayment(
        status: PaymentStatus.disputed,
        disputeReason: 'damaged_goods',
        disputeDescription: 'The goods were damaged during transport.',
      );
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('Dispute Details'), findsOneWidget);
      expect(find.textContaining('damaged_goods'), findsOneWidget);
      expect(
        find.textContaining('The goods were damaged during transport'),
        findsOneWidget,
      );
    });

    testWidgets('shows stale data indicator when data is > 1 hour old',
        (WidgetTester tester) async {
      final payment = _makePayment(
        updatedAt: DateTime.now().toUtc().subtract(const Duration(minutes: 90)),
      );
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.info_outline), findsWidgets);
    });

    testWidgets('does not show stale indicator for fresh data',
        (WidgetTester tester) async {
      final payment = _makePayment(updatedAt: DateTime.now().toUtc());
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // info_outline appears only in the footer when no stale indicator;
      // with fresh data, the stale indicator row should not exist.
      // We check that the "may be outdated" text is absent.
      // The icon itself might appear for other widgets, so check text.
      // Search for the l10n key text via substring
      final staleTextFinder = find.textContaining('outdated');
      expect(staleTextFinder, findsNothing);
    });

    testWidgets('shows Telebirr reference when available',
        (WidgetTester tester) async {
      final payment = _makePayment(telebirrRef: 'TLB-REF-123456');
      when(() => escrowBloc.state)
          .thenReturn(EscrowDetailLoaded(payment: payment));
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('TLB-REF-123456'), findsOneWidget);
    });

    testWidgets('dispatches LoadEscrowDetail on init',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => const Stream<EscrowState>.empty());

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      verify(
        () => escrowBloc.add(const LoadEscrowDetail(escrowId: 'esc-001')),
      ).called(1);
    });
  });
}

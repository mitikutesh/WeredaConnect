import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Wrap a widget in MaterialApp with l10n delegates.
Widget _wrapWithL10n(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: SingleChildScrollView(child: child)),
  );
}

/// Create a test payment.
Payment _makePayment({
  PaymentStatus status = PaymentStatus.escrowed,
  DateTime? releasedAt,
  DateTime? completedAt,
  DateTime? refundedAt,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = 'esc-001'
    ..deliveryId = 'del-001'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = 15000.0
    ..commission = 375.0
    ..commissionRate = 0.025
    ..farmerPayout = 14625.0
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..releasedAt = releasedAt
    ..completedAt = completedAt
    ..refundedAt = refundedAt
    ..version = 1;
}

void main() {
  group('EscrowTimeline', () {
    testWidgets('renders initiated status with 4 steps',
        (WidgetTester tester) async {
      final payment = _makePayment(status: PaymentStatus.initiated);
      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      // Should have: Initiated, Escrowed, Released, Completed
      expect(find.textContaining('Payment Pending'), findsOneWidget);
      expect(find.textContaining('Funds Held Safely'), findsOneWidget);
      expect(find.textContaining('Funds Released'), findsOneWidget);
      expect(find.textContaining('Payment Complete'), findsOneWidget);
    });

    testWidgets('renders escrowed status correctly',
        (WidgetTester tester) async {
      final payment = _makePayment();
      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      expect(find.textContaining('Payment Pending'), findsOneWidget);
      expect(find.textContaining('Funds Held Safely'), findsOneWidget);
    });

    testWidgets('renders completed status with all steps reached',
        (WidgetTester tester) async {
      final now = DateTime.now().toUtc();
      final payment = _makePayment(
        status: PaymentStatus.completed,
        releasedAt: now.subtract(const Duration(hours: 2)),
        completedAt: now,
      );

      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      expect(find.textContaining('Payment Pending'), findsOneWidget);
      expect(find.textContaining('Payment Complete'), findsOneWidget);
    });

    testWidgets('renders disputed status with dispute step',
        (WidgetTester tester) async {
      final payment = _makePayment(status: PaymentStatus.disputed);

      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      // Should include the dispute step
      expect(find.textContaining('Under Dispute'), findsOneWidget);
      expect(find.textContaining('Payment Pending'), findsOneWidget);
      expect(find.textContaining('Funds Held Safely'), findsOneWidget);
    });

    testWidgets(
        'renders refunded status with refund step instead of released/completed',
        (WidgetTester tester) async {
      final now = DateTime.now().toUtc();
      final payment = _makePayment(
        status: PaymentStatus.refunded,
        refundedAt: now,
      );

      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      expect(find.textContaining('Refunded'), findsOneWidget);
      // Should NOT show Released/Completed for refunded flow
      expect(find.textContaining('Funds Released'), findsNothing);
      expect(find.textContaining('Payment Complete'), findsNothing);
    });

    testWidgets('renders released status correctly',
        (WidgetTester tester) async {
      final now = DateTime.now().toUtc();
      final payment = _makePayment(
        status: PaymentStatus.released,
        releasedAt: now,
      );

      await tester.pumpWidget(_wrapWithL10n(EscrowTimeline(payment: payment)));
      await tester.pumpAndSettle();

      expect(find.textContaining('Funds Released'), findsOneWidget);
    });
  });
}

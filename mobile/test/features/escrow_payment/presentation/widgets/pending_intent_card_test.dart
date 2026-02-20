import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Wrap a widget in MaterialApp with l10n delegates.
Widget _wrapWithL10n(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: child),
  );
}

/// Create a test payment intent.
PaymentIntent _makeIntent({
  IntentStatus status = IntentStatus.pendingSync,
  double amount = 5000.0,
  String? lastError,
}) {
  return PaymentIntent()
    ..idempotencyKey = 'buyer-001:del-001:initiate:1708300000000'
    ..deliveryId = 'del-001-long-delivery-id'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..amount = amount
    ..status = status
    ..createdAt = DateTime.now().toUtc()
    ..lastError = lastError;
}

void main() {
  group('PendingIntentCard', () {
    testWidgets('displays amount', (WidgetTester tester) async {
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('5,000.00'), findsOneWidget);
    });

    testWidgets('displays truncated delivery ID', (WidgetTester tester) async {
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      // Should show first 8 chars of delivery ID
      expect(find.textContaining('del-001-'), findsOneWidget);
      expect(find.textContaining('Queued'), findsOneWidget);
    });

    testWidgets('shows cancel button when pendingSync with onCancel callback',
        (WidgetTester tester) async {
      var cancelled = false;
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(
          PendingIntentCard(
            intent: intent,
            onCancel: () => cancelled = true,
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Find the Cancel button
      final cancelButton = find.byType(TextButton);
      expect(cancelButton, findsOneWidget);

      await tester.tap(cancelButton);
      expect(cancelled, isTrue);
    });

    testWidgets('hides cancel button when onCancel is null',
        (WidgetTester tester) async {
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      expect(find.byType(TextButton), findsNothing);
    });

    testWidgets('hides cancel button when not pendingSync',
        (WidgetTester tester) async {
      final intent = _makeIntent(status: IntentStatus.syncing);

      await tester.pumpWidget(
        _wrapWithL10n(
          PendingIntentCard(
            intent: intent,
            onCancel: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(TextButton), findsNothing);
    });

    testWidgets('shows sync icon', (WidgetTester tester) async {
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.sync), findsOneWidget);
    });

    testWidgets('shows error text when lastError is set',
        (WidgetTester tester) async {
      final intent = _makeIntent(lastError: 'Network timeout');

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Network timeout'), findsOneWidget);
    });

    testWidgets('does not show error text when lastError is null',
        (WidgetTester tester) async {
      final intent = _makeIntent();

      await tester.pumpWidget(
        _wrapWithL10n(PendingIntentCard(intent: intent)),
      );
      await tester.pumpAndSettle();

      // Only amount and delivery info text widgets
      expect(find.textContaining('Network'), findsNothing);
    });
  });
}

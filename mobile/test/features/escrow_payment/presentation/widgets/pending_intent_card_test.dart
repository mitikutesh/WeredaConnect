import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/pending_intent_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// Wrap a widget in MaterialApp with l10n delegates.
Widget _wrapWithL10n(Widget child, {String locale = 'en'}) {
  return MaterialApp(
    // Afan Oromo has no Flutter material strings; the app uses the same
    // fallback delegates.
    localizationsDelegates: const [
      AppLocalizations.delegate,
      FallbackMaterialLocalizationsDelegate(),
      GlobalWidgetsLocalizations.delegate,
      FallbackCupertinoLocalizationsDelegate(),
    ],
    supportedLocales: AppLocalizations.supportedLocales,
    locale: Locale(locale),
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

      expect(find.textContaining('5,000 ETB'), findsOneWidget);
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

    // FUP-70: lastError is a code; the card shows its localized text.
    for (final locale in ['en', 'am', 'om']) {
      testWidgets('$locale: shows the text of the stored error code',
          (WidgetTester tester) async {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final intent = _makeIntent(
          status: IntentStatus.failed,
          lastError: 'ESCROW_039',
        );

        await tester.pumpWidget(
          _wrapWithL10n(PendingIntentCard(intent: intent), locale: locale),
        );
        await tester.pumpAndSettle();

        expect(
          find.text(l10n.escrowErrorDeliveryPaymentUnavailable),
          findsOneWidget,
        );
        expect(find.textContaining('ESCROW_039'), findsNothing);
      });

      testWidgets(
          '$locale: an unknown code or old raw server text shows the '
          'generic text, never the raw value', (WidgetTester tester) async {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        for (final stored in [
          'ESCROW_999',
          'Online payment for a delivery is not available here.',
        ]) {
          await tester.pumpWidget(
            _wrapWithL10n(
              PendingIntentCard(intent: _makeIntent(lastError: stored)),
              locale: locale,
            ),
          );
          await tester.pumpAndSettle();

          expect(find.text(l10n.escrowErrorGeneric), findsOneWidget);
          expect(find.textContaining(stored), findsNothing);
        }
      });
    }

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

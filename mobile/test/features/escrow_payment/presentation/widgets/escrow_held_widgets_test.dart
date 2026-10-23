import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

Widget _wrap(Widget child, {Locale locale = const Locale('en')}) => MaterialApp(
      locale: locale,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        FallbackMaterialLocalizationsDelegate(),
        GlobalWidgetsLocalizations.delegate,
        FallbackCupertinoLocalizationsDelegate(),
      ],
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: SingleChildScrollView(child: child)),
    );

Payment _payment(PaymentStatus status) {
  final now = DateTime.utc(2026, 10, 20, 9);
  return Payment()
    ..paymentId = 'escrow-order-ord-1'
    ..deliveryId = ''
    ..orderId = 'ord-1'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = 1500
    ..commission = 37.5
    ..commissionRate = 0.025
    ..farmerPayout = 1462.5
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..version = 1;
}

/// FRD-06: `held` reads "Under Review" and a status the app does not know
/// reads as a neutral "Status Unknown", in all three languages.
void main() {
  group('EscrowStatusBadge', () {
    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      final l10n = lookupAppLocalizations(locale);

      testWidgets('held chip (${locale.languageCode})', (tester) async {
        await tester.pumpWidget(
          _wrap(
            const EscrowStatusBadge(status: PaymentStatus.held),
            locale: locale,
          ),
        );
        await tester.pumpAndSettle();

        expect(find.text(l10n.escrowStatusHeld), findsOneWidget);
        final chip = tester.widget<StatusChip>(find.byType(StatusChip));
        expect(chip.kind, StatusKind.warning);
        expect(chip.icon, Icons.pause_circle_outline);
      });

      testWidgets('held large badge says why (${locale.languageCode})',
          (tester) async {
        await tester.pumpWidget(
          _wrap(
            const EscrowStatusBadge(status: PaymentStatus.held, large: true),
            locale: locale,
          ),
        );
        await tester.pumpAndSettle();

        expect(
          find.text(l10n.escrowStatusHeld.toUpperCase()),
          findsOneWidget,
        );
        expect(find.text(l10n.escrowDescHeld), findsOneWidget);
      });

      testWidgets('unknown large badge (${locale.languageCode})',
          (tester) async {
        await tester.pumpWidget(
          _wrap(
            const EscrowStatusBadge(status: PaymentStatus.unknown, large: true),
            locale: locale,
          ),
        );
        await tester.pumpAndSettle();

        expect(
          find.text(l10n.escrowStatusUnknown.toUpperCase()),
          findsOneWidget,
        );
        expect(find.text(l10n.escrowDescUnknown), findsOneWidget);
        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.help_outline);
        expect(icon.color, AppTheme.onStatusNeutralContainer);
      });
    }

    testWidgets('refundPending never turns held into "Refund Pending"',
        (tester) async {
      await tester.pumpWidget(
        _wrap(
          const EscrowStatusBadge(
            status: PaymentStatus.held,
            refundPending: true,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Under Review'), findsOneWidget);
      expect(find.text('Refund Pending'), findsNothing);
    });
  });

  group('EscrowTimeline', () {
    testWidgets('held: funds were held, the review is the current step',
        (tester) async {
      await tester.pumpWidget(
        _wrap(EscrowTimeline(payment: _payment(PaymentStatus.held))),
      );
      await tester.pumpAndSettle();

      final labels = tester
          .widgetList<Text>(find.byType(Text))
          .map((t) => t.data)
          .toList();
      expect(
        labels,
        containsAllInOrder([
          'Payment Pending',
          'Funds Held Safely',
          'Under Review',
          'Funds Released',
        ]),
      );
      final current = tester.widget<Text>(find.text('Under Review'));
      expect(current.style?.fontWeight, FontWeight.bold);
    });

    testWidgets('unknown: renders without a current step', (tester) async {
      await tester.pumpWidget(
        _wrap(EscrowTimeline(payment: _payment(PaymentStatus.unknown))),
      );
      await tester.pumpAndSettle();

      expect(find.text('Under Review'), findsNothing);
      for (final text in tester.widgetList<Text>(find.byType(Text))) {
        expect(text.style?.fontWeight, isNot(FontWeight.bold));
      }
    });
  });
}

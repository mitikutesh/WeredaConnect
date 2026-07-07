import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Wrap a widget in MaterialApp with l10n delegates.
Widget _wrapWithL10n(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: child),
  );
}

void main() {
  group('EscrowStatusBadge', () {
    group('compact variant (default)', () {
      for (final status in PaymentStatus.values) {
        testWidgets('renders for status ${status.name}',
            (WidgetTester tester) async {
          await tester.pumpWidget(
            _wrapWithL10n(EscrowStatusBadge(status: status)),
          );
          await tester.pumpAndSettle();

          // Should find the badge widget
          expect(find.byType(EscrowStatusBadge), findsOneWidget);
          // Should find an icon
          expect(find.byType(Icon), findsOneWidget);
        });
      }

      testWidgets('initiated shows neutral hourglass_empty',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.initiated),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.hourglass_empty);
        expect(icon.color, AppTheme.onStatusNeutralContainer);
      });

      testWidgets('escrowed shows warning shield', (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.escrowed),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.shield);
        expect(icon.color, AppTheme.onStatusWarningContainer);
      });

      testWidgets('released shows info send', (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.released),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.send);
        expect(icon.color, AppTheme.onStatusInfoContainer);
      });

      testWidgets('completed shows success check_circle',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.completed),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.check_circle);
        expect(icon.color, AppTheme.onStatusSuccessContainer);
      });

      testWidgets('cancelled shows neutral cancel',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.cancelled),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.cancel);
        expect(icon.color, AppTheme.onStatusNeutralContainer);
      });

      testWidgets('disputed shows error warning', (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.disputed),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.warning);
        expect(icon.color, AppTheme.onStatusErrorContainer);
      });

      testWidgets('refunded shows info replay', (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.refunded),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.icon, Icons.replay);
        expect(icon.color, AppTheme.onStatusInfoContainer);
      });

      testWidgets('compact variant has the shared StatusChip icon size (14px)',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(status: PaymentStatus.escrowed),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.size, 14);
      });
    });

    group('large variant', () {
      testWidgets('large variant has 48px icon size',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(
              status: PaymentStatus.escrowed,
              large: true,
            ),
          ),
        );
        await tester.pumpAndSettle();

        final icon = tester.widget<Icon>(find.byType(Icon));
        expect(icon.size, 48);
      });

      testWidgets('large variant renders description text',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(
              status: PaymentStatus.escrowed,
              large: true,
            ),
          ),
        );
        await tester.pumpAndSettle();

        // Should show description "Funds held safely until delivery"
        expect(find.textContaining('Funds held safely'), findsOneWidget);
      });

      testWidgets('large variant renders label in uppercase',
          (WidgetTester tester) async {
        await tester.pumpWidget(
          _wrapWithL10n(
            const EscrowStatusBadge(
              status: PaymentStatus.completed,
              large: true,
            ),
          ),
        );
        await tester.pumpAndSettle();

        // Label should be uppercase
        expect(find.textContaining('PAYMENT COMPLETE'), findsOneWidget);
      });
    });
  });
}

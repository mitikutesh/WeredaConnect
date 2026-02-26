import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
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

void main() {
  group('AmountDisplay', () {
    testWidgets('formats 100 ETB correctly', (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 100)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('100.00'), findsOneWidget);
      expect(find.textContaining('ETB'), findsOneWidget);
    });

    testWidgets('formats 15,000 ETB with thousands separator',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 15000)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('15,000.00'), findsOneWidget);
    });

    testWidgets('formats 500,000 ETB correctly', (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 500000)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('500,000.00'), findsOneWidget);
    });

    testWidgets('formats decimal amounts correctly',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 1234.56)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('1,234.56'), findsOneWidget);
    });

    testWidgets('large variant uses different text style',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 15000, large: true)),
      );
      await tester.pumpAndSettle();

      // Should render the text (large variant has headlineMedium style)
      expect(find.textContaining('15,000.00'), findsOneWidget);
    });

    testWidgets('applies custom color', (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(const AmountDisplay(amount: 15000, color: Colors.green)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('15,000.00'), findsOneWidget);
    });

    testWidgets('applies custom style', (WidgetTester tester) async {
      await tester.pumpWidget(
        _wrapWithL10n(
          const AmountDisplay(
            amount: 15000,
            style: TextStyle(fontSize: 32, fontWeight: FontWeight.bold),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('15,000.00'), findsOneWidget);
    });
  });
}

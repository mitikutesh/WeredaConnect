import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_empty_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Widget _buildWidget(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: child),
  );
}

void main() {
  group('ConflictEmptyState', () {
    testWidgets('renders check_circle icon at size 80',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(const ConflictEmptyState()),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.check_circle), findsOneWidget);

      final icon = tester.widget<Icon>(find.byIcon(Icons.check_circle));
      expect(icon.size, equals(80));
      expect(icon.color, equals(Colors.green.shade400));
    });

    testWidgets('shows title text from l10n.conflictEmptyTitle',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(const ConflictEmptyState()),
      );
      await tester.pumpAndSettle();

      expect(find.text('Everything is up to date'), findsOneWidget);
    });

    testWidgets('shows subtitle text from l10n.conflictEmptySubtitle',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(const ConflictEmptyState()),
      );
      await tester.pumpAndSettle();

      expect(find.text('No changes from other devices.'), findsOneWidget);
    });

    testWidgets('is centered with column mainAxisAlignment.center',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(const ConflictEmptyState()),
      );
      await tester.pumpAndSettle();

      // ConflictEmptyState wraps content in Center > Column
      final column = tester.widget<Column>(
        find.descendant(
          of: find.byType(ConflictEmptyState),
          matching: find.byType(Column),
        ),
      );
      expect(column.mainAxisAlignment, equals(MainAxisAlignment.center));
    });
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_field_diff.dart';
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
  group('ConflictFieldDiff', () {
    testWidgets('renders field name and server value',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Crop Type',
            serverValue: 'Teff',
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Displays "Crop Type: Teff"
      expect(find.text('Crop Type: Teff'), findsOneWidget);
    });

    testWidgets('shows "CURRENT VERSION" label in uppercase',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Status',
            serverValue: 'active',
          ),
        ),
      );
      await tester.pumpAndSettle();

      // l10n.conflictCurrentVersion.toUpperCase() → "CURRENT VERSION"
      expect(find.text('CURRENT VERSION'), findsOneWidget);
    });

    testWidgets('has green styling (background and left border)',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Quantity',
            serverValue: '50.0',
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Find the Container with green decoration
      final container = tester.widget<Container>(
        find
            .ancestor(
              of: find.text('Quantity: 50.0'),
              matching: find.byType(Container),
            )
            .first,
      );

      final decoration = container.decoration as BoxDecoration;
      expect(decoration.color, equals(Colors.green.shade50));
      expect(
        decoration.border,
        equals(
          Border(
            left: BorderSide(color: Colors.green.shade700, width: 4),
          ),
        ),
      );
    });

    testWidgets('shows check icon', (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Price/kg',
            serverValue: '45.0',
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.check), findsOneWidget);
    });

    testWidgets('handles long field values correctly',
        (WidgetTester tester) async {
      const longValue =
          'This is a very long value that might overflow the widget boundary in some layouts';

      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Description',
            serverValue: longValue,
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Should render without error
      expect(find.textContaining('Description: $longValue'), findsOneWidget);
    });

    testWidgets('"CURRENT VERSION" label has correct text style',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Status',
            serverValue: 'active',
          ),
        ),
      );
      await tester.pumpAndSettle();

      final labelText = tester.widget<Text>(find.text('CURRENT VERSION'));
      expect(labelText.style?.fontSize, equals(12));
      expect(labelText.style?.fontWeight, equals(FontWeight.w600));
      expect(labelText.style?.color, equals(Colors.green.shade800));
    });

    testWidgets('field value text has fontSize 14',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        _buildWidget(
          const ConflictFieldDiff(
            fieldName: 'Crop Type',
            serverValue: 'Wheat',
          ),
        ),
      );
      await tester.pumpAndSettle();

      final valueText = tester.widget<Text>(find.text('Crop Type: Wheat'));
      expect(valueText.style?.fontSize, equals(14));
    });
  });
}

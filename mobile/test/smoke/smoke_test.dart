import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/main.dart';

/// Smoke tests verify that the app can start and basic functionality works.
/// These are quick sanity checks that run before more comprehensive tests.
void main() {
  group('Smoke Tests', () {
    testWidgets('app starts without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());

      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('home page loads', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();

      // Verify HomePage is displayed
      expect(find.byType(HomePage), findsOneWidget);
    });

    testWidgets('basic UI elements are present', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pumpAndSettle();

      // Verify essential UI elements
      expect(find.text('WeredaConnect'), findsOneWidget);
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });
}

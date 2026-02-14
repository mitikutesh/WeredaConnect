import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/main.dart';

/// Smoke tests verify that the app can start and basic functionality works.
/// These are quick sanity checks that run before more comprehensive tests.
void main() {
  setUpAll(() {
    // Initialize API client before tests (normally done in main())
    ApiClient().initialize(ApiConfig.baseUrl);
  });

  group('Smoke Tests', () {
    testWidgets('app starts without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());

      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('auth gate loads', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());

      // Verify AuthGate is rendered (root routing widget)
      expect(find.byType(AuthGate), findsOneWidget);
    });

    testWidgets('basic UI elements are present', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pump();

      // Verify essential UI elements (splash or auth screen)
      expect(find.text('WeredaConnect'), findsWidgets);
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });
}

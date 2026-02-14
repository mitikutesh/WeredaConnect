import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/main.dart';

void main() {
  setUpAll(() {
    // Initialize API client before tests (normally done in main())
    ApiClient().initialize(ApiConfig.baseUrl);
  });

  group('WeredaConnectApp', () {
    testWidgets('renders without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());

      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('shows AuthGate as root', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(const WeredaConnectApp());

      // AuthGate is the root routing widget
      expect(find.byType(AuthGate), findsOneWidget);
    });

    testWidgets('shows splash screen initially', (WidgetTester tester) async {
      // Build the app (don't settle - splash is transient)
      await tester.pumpWidget(const WeredaConnectApp());
      await tester.pump();

      // Splash screen shows agriculture icon and app name
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
      expect(find.text('WeredaConnect'), findsWidgets);
    });
  });
}

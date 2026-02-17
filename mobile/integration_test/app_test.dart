import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/main.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

/// Integration tests verify that different parts of the app work together.
/// These tests run on a real device or emulator.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(() {
    ApiClient().initialize();
    SharedPreferences.setMockInitialValues({});
  });

  group('App Integration Tests', () {
    testWidgets('app launches and displays home screen',
        (WidgetTester tester) async {
      // Start the app
      await tester.pumpWidget(
        ChangeNotifierProvider(
          create: (_) => LanguageProvider(),
          child: const WeredaConnectApp(),
        ),
      );
      await tester.pumpAndSettle();

      // Verify home screen is displayed
      expect(find.text('WeredaConnect'), findsOneWidget);
      expect(find.text('Welcome to WeredaConnect'), findsOneWidget);
      expect(find.text('Offline Ready'), findsOneWidget);
    });

    testWidgets('app bar is displayed correctly', (WidgetTester tester) async {
      await tester.pumpWidget(
        ChangeNotifierProvider(
          create: (_) => LanguageProvider(),
          child: const WeredaConnectApp(),
        ),
      );
      await tester.pumpAndSettle();

      // Verify app bar
      expect(find.text('WeredaConnect'), findsOneWidget);
    });

    testWidgets('agriculture icon is visible', (WidgetTester tester) async {
      await tester.pumpWidget(
        ChangeNotifierProvider(
          create: (_) => LanguageProvider(),
          child: const WeredaConnectApp(),
        ),
      );
      await tester.pumpAndSettle();

      // Verify icon
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });
}

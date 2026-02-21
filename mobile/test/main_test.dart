import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';
import 'package:wereda_connect/main.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

class _MockEscrowSyncService extends Mock implements EscrowSyncService {}

void main() {
  setUpAll(() {
    // Initialize API client before tests (normally done in main())
    ApiClient().initialize();
    // Mock shared preferences for OnboardingBloc
    SharedPreferences.setMockInitialValues({});
  });

  /// Helper to wrap WeredaConnectApp with required LanguageProvider
  Widget buildApp() {
    return ChangeNotifierProvider(
      create: (_) => LanguageProvider(),
      child: WeredaConnectApp(
        connectivityMonitor: ConnectivityMonitor(dio: Dio()),
        escrowSyncService: _MockEscrowSyncService(),
        conflictNotificationService: ConflictNotificationService(),
      ),
    );
  }

  group('WeredaConnectApp', () {
    testWidgets('renders without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(buildApp());

      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('shows AuthGate as root', (WidgetTester tester) async {
      // Build the app and pump to allow localization delegates to load
      await tester.pumpWidget(buildApp());
      await tester.pump();

      // AuthGate is the root routing widget
      expect(find.byType(AuthGate), findsOneWidget);
    });

    testWidgets('shows splash screen initially', (WidgetTester tester) async {
      // Build the app (don't settle - splash is transient)
      await tester.pumpWidget(buildApp());
      await tester.pump();

      // Splash screen shows agriculture icon and app name
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
      expect(find.text('WeredaConnect'), findsWidgets);
    });
  });
}

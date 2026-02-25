import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';
import 'package:wereda_connect/main.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

class _MockEscrowSyncService extends Mock implements EscrowSyncService {}

/// Integration tests verify that different parts of the app work together.
/// These tests run on a real device or emulator.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(() {
    ApiClient().initialize();
    SharedPreferences.setMockInitialValues({});
  });

  Widget buildApp() {
    return ChangeNotifierProvider(
      create: (_) => LanguageProvider(),
      child: WeredaConnectApp(
        authBloc: AuthBloc(),
        connectivityMonitor: ConnectivityMonitor(dio: Dio()),
        escrowSyncService: _MockEscrowSyncService(),
        conflictNotificationService: ConflictNotificationService(),
      ),
    );
  }

  group('App Integration Tests', () {
    testWidgets('app launches and displays home screen',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildApp());
      await tester.pumpAndSettle();

      // Verify splash/auth screen is displayed
      expect(find.text('WeredaConnect'), findsOneWidget);
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });

    testWidgets('app bar is displayed correctly', (WidgetTester tester) async {
      await tester.pumpWidget(buildApp());
      await tester.pumpAndSettle();

      // Verify app bar
      expect(find.text('WeredaConnect'), findsOneWidget);
    });

    testWidgets('agriculture icon is visible', (WidgetTester tester) async {
      await tester.pumpWidget(buildApp());
      await tester.pumpAndSettle();

      // Verify icon
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });
}

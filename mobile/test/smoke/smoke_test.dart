import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/core/sync/sync_worker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';
import 'package:wereda_connect/app/run_mobile.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

class _MockEscrowSyncService extends Mock implements EscrowSyncService {}

class _MockSyncQueue extends Mock implements SyncQueue {}

class _MockSyncExecutor extends Mock implements SyncExecutor {}

class _MockSyncWorker extends Mock implements SyncWorker {}

/// Smoke tests verify that the app can start and basic functionality works.
/// These are quick sanity checks that run before more comprehensive tests.
void main() {
  setUpAll(() {
    // Initialize API client before tests (normally done in main())
    ApiClient().initialize();
    // Mock shared preferences for OnboardingBloc
    SharedPreferences.setMockInitialValues({});
  });

  /// Helper to wrap WeredaConnectApp with required LanguageProvider
  Widget buildApp() {
    final mockExecutor = _MockSyncExecutor();
    when(() => mockExecutor.isSyncing).thenReturn(false);
    when(() => mockExecutor.onSyncResult)
        .thenAnswer((_) => const Stream.empty());
    return ChangeNotifierProvider(
      create: (_) => LanguageProvider(),
      child: WeredaConnectApp(
        authBloc: AuthBloc(),
        connectivityMonitor: ConnectivityMonitor(dio: Dio()),
        escrowSyncService: _MockEscrowSyncService(),
        conflictNotificationService: ConflictNotificationService(),
        syncQueue: _MockSyncQueue(),
        syncExecutor: mockExecutor,
        syncWorker: _MockSyncWorker(),
      ),
    );
  }

  group('Smoke Tests', () {
    testWidgets('app starts without crashing', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(buildApp());

      // Verify the app renders
      expect(find.byType(WeredaConnectApp), findsOneWidget);
    });

    testWidgets('auth gate loads', (WidgetTester tester) async {
      // Build the app and pump to allow localization delegates to load
      await tester.pumpWidget(buildApp());
      await tester.pump();

      // Verify AuthGate is rendered (root routing widget)
      expect(find.byType(AuthGate), findsOneWidget);
    });

    testWidgets('basic UI elements are present', (WidgetTester tester) async {
      // Build the app
      await tester.pumpWidget(buildApp());
      await tester.pump();

      // Verify essential UI elements (splash or auth screen)
      expect(find.text('WeredaConnect'), findsWidgets);
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
    });
  });
}

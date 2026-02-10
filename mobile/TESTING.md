# 🧪 Mobile Testing Guide (Flutter)

## Overview
Comprehensive testing guide for WeredaConnect Flutter mobile app. Covers unit tests, widget tests, integration tests, E2E tests, and smoke tests.

---

## Quick Start

```bash
# Run all unit tests
flutter test

# Run with coverage
flutter test --coverage

# Run integration tests
flutter test integration_test/

# Run specific test file
flutter test test/core/sync/sync_service_test.dart

# Run tests in watch mode
flutter test --watch
```

---

## 1. Unit Tests

### What to Test
- Business logic in domain layer
- Data transformations in repositories
- Sync engine logic
- Validation functions
- Utility functions

### Example: Testing Repository (Offline-First Pattern)

```dart
// test/features/reports/data/repositories/report_repository_test.dart
import 'package:flutter_test/flutter_test.dart';
import 'package:mockito/mockito.dart';
import 'package:mockito/annotations.dart';

@GenerateMocks([ReportLocalDataSource, ReportRemoteDataSource, ConnectivityService])
void main() {
  late ReportRepository repository;
  late MockReportLocalDataSource mockLocal;
  late MockReportRemoteDataSource mockRemote;
  late MockConnectivityService mockConnectivity;

  setUp(() {
    mockLocal = MockReportLocalDataSource();
    mockRemote = MockReportRemoteDataSource();
    mockConnectivity = MockConnectivityService();
    repository = ReportRepository(
      localDataSource: mockLocal,
      remoteDataSource: mockRemote,
      connectivity: mockConnectivity,
    );
  });

  group('getReport', () {
    const testId = 'report-123';
    final testReport = Report(id: testId, title: 'Test Report');

    test('should return local data when available', () async {
      // Arrange
      when(mockLocal.getReport(testId))
          .thenAnswer((_) async => testReport);

      // Act
      final result = await repository.getReport(testId);

      // Assert
      expect(result, testReport);
      verify(mockLocal.getReport(testId));
      verifyNever(mockRemote.getReport(any));
    });

    test('should fetch from remote when not in local DB and online', () async {
      // Arrange
      when(mockLocal.getReport(testId))
          .thenAnswer((_) async => null);
      when(mockConnectivity.isOnline)
          .thenAnswer((_) async => true);
      when(mockRemote.getReport(testId))
          .thenAnswer((_) async => testReport);
      when(mockLocal.saveReport(testReport))
          .thenAnswer((_) async => {});

      // Act
      final result = await repository.getReport(testId);

      // Assert
      expect(result, testReport);
      verify(mockLocal.getReport(testId));
      verify(mockRemote.getReport(testId));
      verify(mockLocal.saveReport(testReport)); // Cache for offline
    });

    test('should throw OfflineException when not in local DB and offline', () async {
      // Arrange
      when(mockLocal.getReport(testId))
          .thenAnswer((_) async => null);
      when(mockConnectivity.isOnline)
          .thenAnswer((_) async => false);

      // Act & Assert
      expect(
        () => repository.getReport(testId),
        throwsA(isA<OfflineException>()),
      );
    });
  });
}
```

### Example: Testing Sync Engine

```dart
// test/core/sync/sync_service_test.dart
import 'package:flutter_test/flutter_test.dart';

void main() {
  late SyncService syncService;
  late MockSyncQueue mockQueue;
  late MockApiClient mockApi;

  setUp(() {
    mockQueue = MockSyncQueue();
    mockApi = MockApiClient();
    syncService = SyncService(queue: mockQueue, api: mockApi);
  });

  group('syncPendingOperations', () {
    test('should sync all pending operations when online', () async {
      // Arrange
      final pendingOps = [
        SyncOperation(id: '1', type: SyncType.create, data: {}),
        SyncOperation(id: '2', type: SyncType.update, data: {}),
      ];
      when(mockQueue.getPending()).thenAnswer((_) async => pendingOps);
      when(mockApi.execute(any)).thenAnswer((_) async => true);

      // Act
      await syncService.syncPendingOperations();

      // Assert
      verify(mockApi.execute(pendingOps[0]));
      verify(mockApi.execute(pendingOps[1]));
      verify(mockQueue.remove(pendingOps[0].id));
      verify(mockQueue.remove(pendingOps[1].id));
    });

    test('should keep operation in queue when sync fails', () async {
      // Arrange
      final pendingOp = SyncOperation(id: '1', type: SyncType.create, data: {});
      when(mockQueue.getPending()).thenAnswer((_) async => [pendingOp]);
      when(mockApi.execute(any)).thenThrow(ApiException('Network error'));

      // Act
      await syncService.syncPendingOperations();

      // Assert
      verify(mockApi.execute(pendingOp));
      verifyNever(mockQueue.remove(any)); // Should NOT remove on failure
    });
  });
}
```

---

## 2. Widget Tests

### What to Test
- UI components render correctly
- User interactions trigger expected behavior
- State changes update UI
- Localization displays correct strings

### Example: Testing Widget with BLoC

```dart
// test/features/reports/presentation/widgets/report_card_test.dart
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

void main() {
  testWidgets('ReportCard displays report title and status', (tester) async {
    // Arrange
    final report = Report(
      id: '123',
      title: 'Test Report',
      synced: false,
    );

    // Act
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ReportCard(report: report),
        ),
      ),
    );

    // Assert
    expect(find.text('Test Report'), findsOneWidget);
    expect(find.byIcon(Icons.sync_problem), findsOneWidget); // Not synced icon
  });

  testWidgets('ReportCard shows synced icon when synced', (tester) async {
    // Arrange
    final report = Report(id: '123', title: 'Test', synced: true);

    // Act
    await tester.pumpWidget(
      MaterialApp(home: Scaffold(body: ReportCard(report: report))),
    );

    // Assert
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
  });
}
```

---

## 3. Integration Tests

### What to Test
- Complete user flows (offline → online)
- Database persistence (Isar)
- Sync conflict resolution
- Authentication flows

### Example: Offline Sync Integration Test

```dart
// integration_test/offline_sync_test.dart
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Offline Sync Flow', () {
    testWidgets('Create report offline, sync when online', (tester) async {
      // Start app
      await tester.pumpWidget(MyApp());
      await tester.pumpAndSettle();

      // 1. Go offline
      await setConnectivity(false);

      // 2. Create a report
      await tester.tap(find.byIcon(Icons.add));
      await tester.pumpAndSettle();
      
      await tester.enterText(find.byKey(Key('title_field')), 'Offline Report');
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // 3. Verify report is saved locally
      expect(find.text('Offline Report'), findsOneWidget);
      expect(find.byIcon(Icons.sync_problem), findsOneWidget); // Not synced

      // 4. Go online
      await setConnectivity(true);
      await tester.pump(Duration(seconds: 2)); // Wait for sync

      // 5. Verify report is synced
      expect(find.byIcon(Icons.check_circle), findsOneWidget); // Synced
    });
  });
}
```

---

## 4. E2E Tests (Maestro)

### Local E2E Testing with Backend

For complete end-to-end testing with mobile app → backend server → database, see:
**[E2E Local Testing Guide](../docs/developer/e2e-local-testing.md)**

**Quick Setup**:
```bash
# 1. Start LocalStack (backend services)
cd ../server
docker-compose -f docker-compose.localstack.yml up -d

# 2. Run mobile app with LocalStack URL
cd ../mobile
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566

# Note: 10.0.2.2 is Android emulator's way to access host machine's localhost
# For iOS simulator, use: http://localhost:4566
# For physical device, use your computer's IP: http://192.168.1.100:4566
```

### Setup Maestro
```bash
# Install Maestro
curl -Ls "https://get.maestro.mobile.dev" | bash

# Run E2E tests
maestro test .maestro/
```

### Example: E2E Test Flow
```yaml
# .maestro/create_report_flow.yaml
appId: com.weredaconnect.app
---
- launchApp
- assertVisible: "WeredaConnect"

# Navigate to create report
- tapOn: "Create Report"
- assertVisible: "New Report"

# Fill form
- tapOn: "Title"
- inputText: "E2E Test Report"
- tapOn: "Description"
- inputText: "Created by automated test"

# Save
- tapOn: "Save"
- assertVisible: "Report saved"
- assertVisible: "E2E Test Report"
```

---

## 5. Smoke Tests

### What to Test
- App launches successfully
- Critical screens load
- Offline mode works
- Database initializes

### Example: Smoke Test Suite

```dart
// integration_test/smoke_test.dart
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Smoke Tests', () {
    testWidgets('App launches and shows home screen', (tester) async {
      await tester.pumpWidget(MyApp());
      await tester.pumpAndSettle();
      
      expect(find.text('WeredaConnect'), findsOneWidget);
    });

    testWidgets('Can navigate to reports screen', (tester) async {
      await tester.pumpWidget(MyApp());
      await tester.pumpAndSettle();
      
      await tester.tap(find.byIcon(Icons.list));
      await tester.pumpAndSettle();
      
      expect(find.text('Reports'), findsOneWidget);
    });

    testWidgets('Offline mode indicator works', (tester) async {
      await tester.pumpWidget(MyApp());
      await setConnectivity(false);
      await tester.pump();
      
      expect(find.text('Offline Mode'), findsOneWidget);
    });
  });
}
```

---

## Test Configuration

### pubspec.yaml Dependencies
```yaml
dev_dependencies:
  flutter_test:
    sdk: flutter
  integration_test:
    sdk: flutter
  mockito: ^5.4.0
  build_runner: ^2.4.0
  test: ^1.24.0
```

### Generate Mocks
```bash
flutter pub run build_runner build --delete-conflicting-outputs
```

---

## CI/CD Integration

### GitHub Actions Example
```yaml
name: Flutter Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: subosito/flutter-action@v2
        with:
          flutter-version: '3.27.x'

      - name: Install dependencies
        run: flutter pub get

      - name: Run unit tests
        run: flutter test --coverage

      - name: Upload coverage
        uses: codecov/codecov-action@v3
        with:
          files: ./coverage/lcov.info
```

---

## Best Practices

### 1. Test Offline Scenarios First
```dart
// ✅ GOOD: Test offline first
test('should work offline', () { });
test('should sync when online', () { });

// ❌ BAD: Only testing online
test('should fetch from API', () { });
```

### 2. Use Test Data Factories
```dart
class TestDataFactory {
  static Report createReport({String? id, bool synced = false}) {
    return Report(
      id: id ?? 'test-${DateTime.now().millisecondsSinceEpoch}',
      title: 'Test Report',
      synced: synced,
    );
  }
}
```

### 3. Clean Up After Tests
```dart
tearDown(() async {
  await database.clear();
  await sharedPreferences.clear();
});
```

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0


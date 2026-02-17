# Mobile Testing Guide (Flutter)

> **Testing philosophy and priorities**: See `.ai/testing.md`

## Quick Start

```bash
flutter test                              # All unit tests
flutter test --coverage                   # With coverage
flutter test integration_test/            # Integration tests
flutter test test/core/sync/sync_service_test.dart  # Specific file
```

---

## Unit Tests

### Offline-First Repository Pattern

```dart
// test/features/reports/data/repositories/report_repository_test.dart
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
      when(mockLocal.getReport(testId)).thenAnswer((_) async => testReport);
      final result = await repository.getReport(testId);
      expect(result, testReport);
      verify(mockLocal.getReport(testId));
      verifyNever(mockRemote.getReport(any));
    });

    test('should fetch from remote when not in local DB and online', () async {
      when(mockLocal.getReport(testId)).thenAnswer((_) async => null);
      when(mockConnectivity.isOnline).thenAnswer((_) async => true);
      when(mockRemote.getReport(testId)).thenAnswer((_) async => testReport);
      when(mockLocal.saveReport(testReport)).thenAnswer((_) async => {});

      final result = await repository.getReport(testId);
      expect(result, testReport);
      verify(mockLocal.saveReport(testReport)); // Cache for offline
    });

    test('should throw OfflineException when not in local DB and offline', () async {
      when(mockLocal.getReport(testId)).thenAnswer((_) async => null);
      when(mockConnectivity.isOnline).thenAnswer((_) async => false);
      expect(() => repository.getReport(testId), throwsA(isA<OfflineException>()));
    });
  });
}
```

### Sync Engine

```dart
// test/core/sync/sync_service_test.dart
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
      final pendingOps = [
        SyncOperation(id: '1', type: SyncType.create, data: {}),
        SyncOperation(id: '2', type: SyncType.update, data: {}),
      ];
      when(mockQueue.getPending()).thenAnswer((_) async => pendingOps);
      when(mockApi.execute(any)).thenAnswer((_) async => true);

      await syncService.syncPendingOperations();

      verify(mockApi.execute(pendingOps[0]));
      verify(mockApi.execute(pendingOps[1]));
      verify(mockQueue.remove(pendingOps[0].id));
      verify(mockQueue.remove(pendingOps[1].id));
    });

    test('should keep operation in queue when sync fails', () async {
      final pendingOp = SyncOperation(id: '1', type: SyncType.create, data: {});
      when(mockQueue.getPending()).thenAnswer((_) async => [pendingOp]);
      when(mockApi.execute(any)).thenThrow(ApiException('Network error'));

      await syncService.syncPendingOperations();

      verifyNever(mockQueue.remove(any)); // Should NOT remove on failure
    });
  });
}
```

---

## Widget Tests

```dart
// test/features/reports/presentation/widgets/report_card_test.dart
void main() {
  testWidgets('ReportCard displays title and unsynced icon', (tester) async {
    final report = Report(id: '123', title: 'Test Report', synced: false);
    await tester.pumpWidget(
      MaterialApp(home: Scaffold(body: ReportCard(report: report))),
    );
    expect(find.text('Test Report'), findsOneWidget);
    expect(find.byIcon(Icons.sync_problem), findsOneWidget);
  });

  testWidgets('ReportCard shows synced icon when synced', (tester) async {
    final report = Report(id: '123', title: 'Test', synced: true);
    await tester.pumpWidget(
      MaterialApp(home: Scaffold(body: ReportCard(report: report))),
    );
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
  });
}
```

---

## Integration Tests

### Offline Sync Flow

```dart
// integration_test/offline_sync_test.dart
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Offline Sync Flow', () {
    testWidgets('Create report offline, sync when online', (tester) async {
      await tester.pumpWidget(MyApp());
      await tester.pumpAndSettle();

      // Go offline, create report
      await setConnectivity(false);
      await tester.tap(find.byIcon(Icons.add));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(Key('title_field')), 'Offline Report');
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      expect(find.text('Offline Report'), findsOneWidget);
      expect(find.byIcon(Icons.sync_problem), findsOneWidget);

      // Go online, verify sync
      await setConnectivity(true);
      await tester.pump(Duration(seconds: 2));
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
    });
  });
}
```

### E2E with Backend (LocalStack)

```bash
# Start LocalStack
cd ../server && docker-compose -f docker-compose.localstack.yml up -d

# Run mobile app against LocalStack
cd ../mobile
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566  # Android emulator
# iOS simulator: http://localhost:4566
# Physical device: http://YOUR_IP:4566
```

See `docs/developer/e2e-local-testing.md` for full guide.

---

## Smoke Tests

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

## Best Practices

### Test offline scenarios first
```dart
// GOOD: Test offline first
test('should work offline', () { });
test('should sync when online', () { });

// BAD: Only testing online
test('should fetch from API', () { });
```

### Use test data factories
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

### Clean up after tests
```dart
tearDown(() async {
  await database.clear();
  await sharedPreferences.clear();
});
```

### Generate mocks
```bash
flutter pub run build_runner build --delete-conflicting-outputs
```

---

**Last Updated**: 2026-02-09

# Integration Tests

This directory contains end-to-end integration tests that validate the WeredaConnect mobile app against real backend services and device conditions.

## Test Files

### `sync_crop_listing_test.dart`

Comprehensive integration tests for the offline-first sync engine.

**Coverage**:
- Basic sync flow (offline → online)
- Multiple operations batch sync
- Conflict resolution with server-wins strategy
- Performance metrics (sync latency)
- Edge cases (empty queue, concurrent sync prevention)

**Prerequisites**:
- Backend deployed and accessible
- Clean Isar database (handled automatically)
- Test user credentials (if authentication required)

## Running Tests

### Quick Test (No Device)

Run tests without a device using the Flutter test command:

```bash
cd mobile
flutter test integration_test/sync_crop_listing_test.dart
```

**Note**: This runs tests without platform integration, so some features (like real network calls) may be mocked.

### Device Testing (Recommended)

For accurate performance metrics, run on a real device or emulator:

#### Android

```bash
# Start an emulator or connect a device
flutter devices

# Run integration tests on device
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart
```

#### iOS

```bash
# List available simulators/devices
flutter devices

# Run integration tests on iOS
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart \
  -d <device-id>
```

### Performance Profiling

To measure performance with profiling enabled:

```bash
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart \
  --profile
```

## Test Configuration

### Backend Endpoint

Tests use the backend URL from `Environment.config.apiBaseUrl`:

- **Development**: `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Staging**: Override with `--dart-define=ENVIRONMENT=staging`
- **Production**: Override with `--dart-define=ENVIRONMENT=production`

Example with custom environment:

```bash
flutter test integration_test/sync_crop_listing_test.dart \
  --dart-define=ENVIRONMENT=staging
```

### Authentication

Tests currently use a test user ID (`test-farmer-001`). For full integration testing with authentication:

1. Configure test user credentials in the test setup
2. Add authentication token generation
3. Pass tokens to Dio client via interceptors

**TODO**: Implement proper test authentication flow.

## Simulating Ethiopian Network Conditions

For realistic performance testing on 3G networks:

### Android

1. Enable Developer Options on device
2. Go to Settings → Developer Options → Networking
3. Set Network Throttling:
   - Download: 0.75 Mbps
   - Upload: 0.25 Mbps
   - Latency: 100-200ms

### iOS

1. Use Network Link Conditioner (Xcode)
2. Select "3G" preset
3. Run tests with throttled network

### Chrome DevTools (for web tests)

1. Open DevTools → Network tab
2. Set throttling to "Slow 3G"
3. Run web integration tests

## Test Structure

Each test file follows this structure:

```dart
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  // Global fixtures
  late Isar isar;
  late SyncQueue syncQueue;
  late SyncExecutor syncExecutor;
  // ... other dependencies

  setUpAll(() async {
    // Initialize dependencies once
    // Set up test database
  });

  tearDownAll(() async {
    // Clean up resources
    // Delete test database
  });

  setUp(() async {
    // Clear database before each test
  });

  group('Test Group', () {
    test('should do something', () async {
      // ARRANGE: Set up test conditions
      // ACT: Perform action
      // ASSERT: Verify results
    });
  });
}
```

## Performance Metrics

Performance metrics are logged to the console during test execution and documented in `/docs/performance/sync-performance-metrics.md`.

### Metrics Tracked

1. **Sync Latency**: Time from sync trigger to completion
2. **Batch Processing Time**: Time to sync N operations
3. **Average Latency per Operation**: Total time / number of operations
4. **Success Rate**: Succeeded / Total operations
5. **Conflict Resolution Time**: Time to resolve a single conflict

### Performance Targets

| Metric | Target | Context |
|--------|--------|---------|
| Single operation | <5s | 3G network tolerance |
| Batch (50 ops) | <250s | 5s average per operation |
| Conflict resolution | <500ms | Minimal overhead |

## Troubleshooting

### "Isar instance already open"

**Cause**: Previous test run didn't clean up database.

**Solution**:
```bash
# Find and kill Flutter processes
pkill -9 flutter

# Clear temporary files
rm -rf /tmp/flutter_test_*
```

### "Connection refused" errors

**Cause**: Backend not accessible or wrong URL.

**Solution**:
1. Verify backend is running:
   ```bash
   curl https://s0om0hib4i.execute-api.me-south-1.amazonaws.com/health
   ```
2. Check environment configuration in `lib/core/config/environment.dart`
3. Ensure device/emulator has internet access

### Tests timing out

**Cause**: Network too slow or backend under load.

**Solution**:
1. Increase test timeout:
   ```dart
   test('should sync', () async {
     // ...
   }, timeout: Timeout(Duration(minutes: 5)));
   ```
2. Reduce batch size in tests
3. Use faster network connection

### Authentication failures (401)

**Cause**: No valid auth token in test setup.

**Solution**:
1. Implement test authentication flow
2. Generate valid JWT token for test user
3. Add token to Dio client headers

## Contributing

When adding new integration tests:

1. **Follow AAA pattern**: Arrange, Act, Assert
2. **Clean up resources**: Use `setUp`/`tearDown` hooks
3. **Mock external dependencies**: Use fake implementations when possible
4. **Document performance**: Log metrics for new test scenarios
5. **Test offline-first**: Simulate offline/online transitions
6. **Ethiopian context**: Consider 3G network conditions

## References

- **Sync Protocol**: `/specs/contracts/sync-protocol.md`
- **Performance Metrics**: `/docs/performance/sync-performance-metrics.md`
- **Sync Executor**: `/mobile/lib/core/sync/sync_executor.dart`
- **Flutter Integration Testing**: https://docs.flutter.dev/testing/integration-tests

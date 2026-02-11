# Performance Testing Implementation Plan

**Created**: 2026-02-10  
**Status**: Ready for Implementation  
**Target**: Add comprehensive performance testing to WeredaConnect

---

## Executive Summary

This plan implements performance testing infrastructure for WeredaConnect's offline-first architecture, accounting for Ethiopian infrastructure constraints (3G networks, 2GB RAM devices). The implementation is phased over 8 weeks with clear deliverables and effort estimates.

### Key Performance Targets
- **Mobile Cold Start**: <3 seconds
- **Server Cold Start**: <2 seconds (Lambda)
- **API Response Time (p95)**: <500ms
- **Sync Time**: <5s for 100 items on 3G (500-2000ms latency)
- **APK Size**: <50MB
- **Memory Usage**: <100MB peak on 2GB devices
- **Load Capacity**: 100 requests/second

---

## 1. Overview

### 1.1 Goals
1. **Establish Performance Baselines**: Measure current performance across all components
2. **Detect Regressions**: Automated performance regression detection in CI/CD
3. **Validate Targets**: Ensure all performance targets are met before production
4. **Optimize Bottlenecks**: Identify and fix performance issues early

### 1.2 Success Criteria
- [ ] All performance tests run in CI/CD
- [ ] Performance baselines established for all metrics
- [ ] Regression detection alerts on >10% degradation
- [ ] Performance reports generated on every PR
- [ ] All targets met in staging environment

### 1.3 Scope

**In Scope**:
- Mobile app cold start and warm start times
- Server Lambda cold start and warm start times
- API response times (p50, p95, p99)
- Sync engine performance (offline → online sync)
- Memory profiling (mobile and server)
- APK size tracking
- Load testing (100 req/s sustained)
- Network simulation (3G conditions)

**Out of Scope**:
- End-user monitoring (APM) - future phase
- Database query optimization - covered by unit tests
- Frontend rendering performance - covered by widget tests
- Security testing - separate initiative

---

## 2. Prerequisites

### 2.1 Tools & Dependencies

#### Mobile (Flutter)
- **Flutter DevTools**: Built-in profiling (memory, CPU, network)
- **flutter_driver**: Integration test driver for performance tests
- **integration_test**: Performance benchmarking
- **Android Profiler**: Memory and CPU profiling on real devices

#### Server (.NET)
- **BenchmarkDotNet**: Microbenchmarking for .NET code
- **k6**: Load testing tool (JavaScript-based, open source)
- **AWS X-Ray**: Distributed tracing (already in Lambda)
- **CloudWatch Metrics**: Cold start and response time tracking

#### Infrastructure
- **k6**: Load testing against deployed APIs
- **Artillery**: Alternative load testing tool
- **CloudWatch Alarms**: Performance regression alerts

### 2.2 Environment Requirements
- **CI/CD**: GitHub Actions with extended timeout (60 min for perf tests)
- **Test Devices**: Android emulator with 2GB RAM constraint
- **Network Simulation**: Linux `tc` (traffic control) for 3G simulation
- **Baseline Storage**: S3 bucket for performance baseline data

---

## 3. Implementation Steps

### Phase 1: Mobile Performance Testing (Weeks 1-2)

#### Step 1.1: Mobile Cold Start Measurement
**Effort**: 8 hours  
**Files to Create**:
- `mobile/integration_test/performance/cold_start_test.dart`
- `mobile/test_driver/perf_driver.dart`

**Implementation**:
```dart
// mobile/integration_test/performance/cold_start_test.dart
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wereda_connect/main.dart' as app;

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  
  testWidgets('Cold start performance', (tester) async {
    // Measure time from app launch to first frame
    final timeline = await binding.traceAction(() async {
      app.main();
      await tester.pumpAndSettle();
    });
    
    // Extract metrics
    final summary = TimelineSummary.summarize(timeline);
    
    // Assert cold start < 3 seconds
    final coldStartMs = summary.summaryJson['average_frame_build_time_millis'];
    expect(coldStartMs, lessThan(3000), 
      reason: 'Cold start must be <3s, got ${coldStartMs}ms');
    
    // Write results to file for CI/CD
    await summary.writeSummaryToFile('cold_start', pretty: true);
    await summary.writeTimelineToFile('cold_start', pretty: true);
  });
}
```

**Testing**:
```bash
# Run locally
flutter drive \
  --driver=test_driver/perf_driver.dart \
  --target=integration_test/performance/cold_start_test.dart \
  --profile

# Expected output: cold_start.timeline_summary.json
```

---

#### Step 1.2: Memory Profiling
**Effort**: 12 hours  
**Files to Create**:
- `mobile/integration_test/performance/memory_test.dart`
- `scripts/mobile_memory_profile.sh`

**Implementation**:
```dart
// mobile/integration_test/performance/memory_test.dart
import 'dart:developer' as developer;
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wereda_connect/main.dart' as app;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  
  testWidgets('Memory usage under load', (tester) async {
    app.main();
    await tester.pumpAndSettle();
    
    // Force GC and get baseline
    developer.Timeline.startSync('memory_baseline');
    await Future.delayed(Duration(seconds: 2));
    developer.Timeline.finishSync();
    
    // Simulate heavy usage: load 100 images
    for (int i = 0; i < 100; i++) {
      // Navigate to image-heavy screen
      // await tester.tap(find.byKey(Key('listing_$i')));
      // await tester.pumpAndSettle();
    }
    
    // Measure peak memory
    developer.Timeline.startSync('memory_peak');
    await Future.delayed(Duration(seconds: 2));
    developer.Timeline.finishSync();
    
    // Assert: Peak memory < 100MB
    // Note: Actual memory measurement requires DevTools or platform channels
  });
}
```

**Shell Script for Memory Profiling**:
```bash
#!/bin/bash
# scripts/mobile_memory_profile.sh

set -e

echo "Starting memory profiling..."

# Build profile APK
flutter build apk --profile --target-platform android-arm64

# Install on device
adb install -r build/app/outputs/flutter-apk/app-profile.apk

# Start app and get PID
adb shell am start -n et.wereda.connect/.MainActivity
sleep 5
PID=$(adb shell pidof et.wereda.connect)

# Monitor memory for 60 seconds
echo "Monitoring memory for PID: $PID"
for i in {1..60}; do
  MEMORY=$(adb shell dumpsys meminfo $PID | grep "TOTAL" | awk '{print $2}')
  echo "[$i] Memory: ${MEMORY} KB"
  sleep 1
done

# Check if peak memory < 100MB (102400 KB)
MAX_MEMORY=$(adb shell dumpsys meminfo $PID | grep "TOTAL" | awk '{print $2}')
if [ "$MAX_MEMORY" -gt 102400 ]; then
  echo "FAIL: Peak memory ${MAX_MEMORY}KB exceeds 100MB limit"
  exit 1
else
  echo "PASS: Peak memory ${MAX_MEMORY}KB is within 100MB limit"
fi
```

---

#### Step 1.3: APK Size Tracking
**Effort**: 4 hours
**Files to Create**:
- `scripts/track_apk_size.sh`
- `.github/workflows/apk-size-check.yml`

**Implementation**:
```bash
#!/bin/bash
# scripts/track_apk_size.sh

set -e

APK_PATH="mobile/build/app/outputs/flutter-apk/app-release.apk"
MAX_SIZE_MB=50
MAX_SIZE_BYTES=$((MAX_SIZE_MB * 1024 * 1024))

# Build release APK
cd mobile
flutter build apk --release --target-platform android-arm64
cd ..

# Get APK size
APK_SIZE=$(stat -f%z "$APK_PATH" 2>/dev/null || stat -c%s "$APK_PATH")
APK_SIZE_MB=$((APK_SIZE / 1024 / 1024))

echo "APK Size: ${APK_SIZE_MB}MB (${APK_SIZE} bytes)"

# Check against limit
if [ "$APK_SIZE" -gt "$MAX_SIZE_BYTES" ]; then
  echo "❌ FAIL: APK size ${APK_SIZE_MB}MB exceeds ${MAX_SIZE_MB}MB limit"
  exit 1
else
  echo "✅ PASS: APK size ${APK_SIZE_MB}MB is within ${MAX_SIZE_MB}MB limit"
fi

# Save to baseline file
mkdir -p .performance-baselines
echo "${APK_SIZE}" > .performance-baselines/apk-size.txt
echo "Baseline saved to .performance-baselines/apk-size.txt"
```

---

#### Step 1.4: Sync Performance Test
**Effort**: 16 hours
**Files to Create**:
- `mobile/integration_test/performance/sync_test.dart`
- `mobile/test/fixtures/sync_test_data.dart`

**Implementation**:
```dart
// mobile/integration_test/performance/sync_test.dart
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Sync Performance Tests', () {
    testWidgets('Sync 100 items on 3G network', (tester) async {
      // Setup: Create 100 pending sync operations
      final syncQueue = MockSyncQueue();
      for (int i = 0; i < 100; i++) {
        await syncQueue.enqueue(SyncOperation(
          type: SyncType.create,
          entityType: 'listing',
          entityId: 'listing_$i',
          data: {'title': 'Test Listing $i'},
        ));
      }

      // Simulate 3G network (500-2000ms latency)
      final mockApi = MockApiClient(latency: Duration(milliseconds: 1000));

      // Measure sync time
      final stopwatch = Stopwatch()..start();
      await syncQueue.syncAll(apiClient: mockApi);
      stopwatch.stop();

      final syncTimeSeconds = stopwatch.elapsedMilliseconds / 1000;

      // Assert: Sync time < 5 seconds for 100 items
      expect(syncTimeSeconds, lessThan(5.0),
        reason: 'Sync 100 items must complete in <5s on 3G, got ${syncTimeSeconds}s');

      // Write results
      final results = {
        'sync_time_seconds': syncTimeSeconds,
        'items_synced': 100,
        'network_latency_ms': 1000,
        'items_per_second': 100 / syncTimeSeconds,
      };

      await File('sync_performance.json').writeAsString(jsonEncode(results));
    });

    testWidgets('Sync with conflict resolution', (tester) async {
      // Test sync performance when conflicts occur
      // Target: <10s for 100 items with 10% conflict rate
    });
  });
}
```

---

### Phase 2: Server Performance Testing (Weeks 3-4)

#### Step 2.1: Lambda Cold Start Measurement
**Effort**: 8 hours
**Files to Create**:
- `server/tests/Performance/ColdStartBenchmark.cs`
- `scripts/measure_lambda_cold_start.sh`

**Implementation**:
```csharp
// server/tests/Performance/ColdStartBenchmark.cs
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Amazon.Lambda.APIGatewayEvents;
using WeredaConnect.Server.Functions;

namespace WeredaConnect.Server.Tests.Performance;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 0, iterationCount: 1)] // Simulate cold start
public class ColdStartBenchmark
{
    private HealthCheckFunction _function;
    private APIGatewayProxyRequest _request;

    [GlobalSetup]
    public void Setup()
    {
        // Reset cold start state
        HealthCheckFunction.ResetColdStart();

        _function = new HealthCheckFunction();
        _request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/health",
        };
    }

    [Benchmark]
    public void ColdStart()
    {
        var context = new TestLambdaContext();
        var response = _function.FunctionHandler(_request, context);

        // Assert cold start < 2 seconds
        if (context.RemainingTime.TotalSeconds > 2)
        {
            throw new Exception($"Cold start took {context.RemainingTime.TotalSeconds}s, exceeds 2s limit");
        }
    }
}

// Run with: dotnet run -c Release --project tests/Performance
```

**Shell Script for Real Lambda Cold Start**:
```bash
#!/bin/bash
# scripts/measure_lambda_cold_start.sh

set -e

FUNCTION_NAME="wereda-connect-health-check-dev"
ITERATIONS=10

echo "Measuring Lambda cold start for $FUNCTION_NAME..."

# Force cold start by updating environment variable
for i in $(seq 1 $ITERATIONS); do
  echo "Iteration $i/$ITERATIONS"

  # Update env var to force new container
  aws lambda update-function-configuration \
    --function-name $FUNCTION_NAME \
    --environment "Variables={FORCE_COLD_START=$i}" \
    --region us-east-1 \
    > /dev/null

  # Wait for update to complete
  sleep 5

  # Invoke and measure
  START=$(date +%s%3N)
  aws lambda invoke \
    --function-name $FUNCTION_NAME \
    --region us-east-1 \
    --log-type Tail \
    /tmp/response.json \
    > /tmp/invoke.json
  END=$(date +%s%3N)

  DURATION=$((END - START))
  INIT_DURATION=$(cat /tmp/invoke.json | jq -r '.LogResult' | base64 -d | grep "Init Duration" | awk '{print $3}')

  echo "  Total: ${DURATION}ms, Init: ${INIT_DURATION}ms"

  # Check if cold start < 2000ms
  if [ "$DURATION" -gt 2000 ]; then
    echo "  ❌ FAIL: Cold start ${DURATION}ms exceeds 2000ms limit"
  else
    echo "  ✅ PASS: Cold start ${DURATION}ms is within 2000ms limit"
  fi
done
```

---

#### Step 2.2: Load Testing with k6
**Effort**: 12 hours
**Files to Create**:
- `server/tests/load/health_check_load.js`
- `server/tests/load/api_load_test.js`
- `scripts/run_load_tests.sh`

**Implementation**:
```javascript
// server/tests/load/health_check_load.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');

// Load test configuration
export const options = {
  stages: [
    { duration: '1m', target: 10 },   // Ramp up to 10 RPS
    { duration: '3m', target: 50 },   // Ramp up to 50 RPS
    { duration: '5m', target: 100 },  // Sustain 100 RPS
    { duration: '2m', target: 0 },    // Ramp down
  ],
  thresholds: {
    http_req_duration: ['p(95)<500'], // 95% of requests < 500ms
    http_req_failed: ['rate<0.01'],   // Error rate < 1%
    errors: ['rate<0.01'],
  },
};

const BASE_URL = __ENV.API_BASE_URL || 'https://dev-api.weredaconnect.com';

export default function () {
  // Health check endpoint
  const response = http.get(`${BASE_URL}/health`);

  // Validate response
  const success = check(response, {
    'status is 200': (r) => r.status === 200,
    'response time < 500ms': (r) => r.timings.duration < 500,
    'body contains status': (r) => r.json('status') === 'healthy',
  });

  errorRate.add(!success);

  // Simulate 3G network delay (500-2000ms)
  sleep(Math.random() * 1.5 + 0.5);
}

// Run with: k6 run server/tests/load/health_check_load.js
```



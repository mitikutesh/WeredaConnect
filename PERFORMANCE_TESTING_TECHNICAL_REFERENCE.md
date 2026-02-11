# Performance Testing Technical Reference

---

## 1. Mobile Performance Testing

### Cold Start Measurement
```bash
# Clear app data and measure startup time
adb shell pm clear com.weredaconnect.app
adb shell am start -W com.weredaconnect.app/.MainActivity

# Output includes:
# TotalTime: 2500 (milliseconds)
# Target: <3000ms on 2GB device
```

### Memory Profiling
```dart
// In test file
import 'dart:developer' as developer;

test('Memory usage during sync', () async {
  final info = await developer.Service.getVM();
  final initialMemory = info.isolates.first.heapUsage;
  
  await syncService.syncAll();
  
  final finalMemory = info.isolates.first.heapUsage;
  final delta = finalMemory - initialMemory;
  
  expect(delta, lessThan(10 * 1024 * 1024)); // <10MB increase
});
```

### APK Size Tracking
```bash
# Build release APK
flutter build apk --release --split-per-abi

# Check sizes
ls -lh build/app/outputs/flutter-apk/
# app-armeabi-v7a-release.apk
# app-arm64-v8a-release.apk

# Target: <50MB total
```

### Frame Rate Testing
```dart
// Use Performance Overlay
flutter run --profile

// In app, press 'P' to toggle overlay
// Monitor frame rate (target: 60 FPS)
// Check for jank (frames >16ms)
```

### Sync Performance Benchmark
```dart
test('Sync 100 items performance', () async {
  // Create 100 pending operations
  final operations = List.generate(100, (i) => 
    SyncOperation(id: '$i', type: SyncType.create, data: {})
  );
  
  final stopwatch = Stopwatch()..start();
  await syncService.syncOperations(operations);
  stopwatch.stop();
  
  // Target: <5000ms on 3G
  expect(stopwatch.elapsedMilliseconds, lessThan(5000));
});
```

---

## 2. Server Performance Testing

### Cold Start Measurement
```csharp
// In HealthCheckFunction
private static readonly Stopwatch StartupTimer = Stopwatch.StartNew();
private static bool _coldStart = true;

public APIGatewayProxyResponse FunctionHandler(
    APIGatewayProxyRequest request, 
    ILambdaContext context)
{
    var coldStart = _coldStart;
    _coldStart = false;
    
    var response = new HealthCheckResponse
    {
        Status = "healthy",
        ColdStart = coldStart,
        StartupTimeMs = StartupTimer.ElapsedMilliseconds
    };
    
    return new APIGatewayProxyResponse
    {
        StatusCode = 200,
        Body = JsonSerializer.Serialize(response),
        Headers = new Dictionary<string, string>
        {
            ["X-Cold-Start"] = coldStart.ToString(),
            ["X-Startup-Ms"] = StartupTimer.ElapsedMilliseconds.ToString()
        }
    };
}
```

### Load Testing with k6
```javascript
// server/tests/Performance/load-test.js
import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 20 },   // Ramp up
    { duration: '1m30s', target: 100 }, // Stay at 100
    { duration: '30s', target: 0 },    // Ramp down
  ],
  thresholds: {
    http_req_duration: ['p(95)<500'],  // p95 < 500ms
    http_req_failed: ['<5%'],          // <5% errors
  },
};

export default function () {
  const url = 'http://localhost:4566/health';
  const res = http.get(url);
  
  check(res, {
    'status is 200': (r) => r.status === 200,
    'response time < 500ms': (r) => r.timings.duration < 500,
  });
  
  sleep(1);
}
```

### BenchmarkDotNet
```csharp
// server/tests/Performance/SyncServiceBenchmarks.cs
using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class SyncServiceBenchmarks
{
    private SyncService _service;
    
    [GlobalSetup]
    public void Setup()
    {
        _service = new SyncService(/* dependencies */);
    }
    
    [Benchmark]
    public async Task SyncSingleOperation()
    {
        var op = new SyncOperation { /* ... */ };
        await _service.SyncOperation(op);
    }
    
    [Benchmark]
    public async Task SyncBatch100Operations()
    {
        var ops = Enumerable.Range(0, 100)
            .Select(i => new SyncOperation { /* ... */ })
            .ToList();
        await _service.SyncOperations(ops);
    }
}
```

---

## 3. Integration Performance Testing

### E2E Sync Timing with Maestro
```yaml
# .maestro/performance_test.yaml
appId: com.weredaconnect.app
---
- launchApp

# Measure offline operation time
- tapOn: "Create Report"
- inputText: "Performance Test"
- tapOn: "Save"
- assertVisible: "Report saved"

# Measure sync time when online
- tapOn: "Settings"
- toggleSwitch: "Offline Mode"
- sleep: 2000  # Wait for sync

# Verify sync completed
- assertVisible: "All synced"
```

### Network Throttling (3G Simulation)
```bash
# Using Android emulator network throttling
adb emu gsm voice off
adb emu gsm data off
adb emu gsm data on
adb emu gsm voice on

# Or use tc (traffic control) on Linux
tc qdisc add dev eth0 root tbf rate 1mbit burst 32kbit latency 400ms
```

### Conflict Resolution Benchmark
```csharp
[Benchmark]
public async Task ResolveConflict_ServerWins()
{
    var clientVersion = new Report { Version = 1, Title = "Client" };
    var serverVersion = new Report { Version = 2, Title = "Server" };
    
    var resolved = await _conflictResolver.Resolve(
        clientVersion, 
        serverVersion, 
        ConflictStrategy.ServerWins
    );
    
    Assert.Equal("Server", resolved.Title);
}
```

---

## 4. CI/CD Integration

### Mobile Performance Job
```yaml
- name: Measure Performance
  working-directory: mobile
  run: |
    flutter build apk --release
    SIZE=$(stat -f%z build/app/outputs/flutter-apk/app-release.apk)
    echo "APK_SIZE_BYTES=$SIZE" >> $GITHUB_ENV
    
    # Run performance tests
    flutter test test/performance/ --reporter json > perf-results.json

- name: Store Metrics
  uses: actions/upload-artifact@v4
  with:
    name: mobile-perf-metrics
    path: mobile/perf-results.json
```

### Server Performance Job
```yaml
- name: Run Load Tests
  working-directory: server
  run: |
    # Install k6
    curl https://get.k6.io | bash
    
    # Run load test
    k6 run tests/Performance/load-test.js \
      --out json=results.json
    
    # Run benchmarks
    dotnet test tests/Performance/ \
      --configuration Release \
      --logger json > benchmark-results.json

- name: Upload Results
  uses: actions/upload-artifact@v4
  with:
    name: server-perf-metrics
    path: |
      server/results.json
      server/benchmark-results.json
```

### Performance Regression Detection
```python
# scripts/compare_metrics.py
import json
import sys

def compare_metrics(current, baseline, threshold=10):
    """Compare current metrics with baseline, fail if regression > threshold%"""
    
    for metric, current_value in current.items():
        baseline_value = baseline.get(metric)
        if not baseline_value:
            continue
            
        regression = ((current_value - baseline_value) / baseline_value) * 100
        
        if regression > threshold:
            print(f"❌ {metric}: {regression:.1f}% regression")
            return False
        elif regression > 0:
            print(f"⚠️  {metric}: {regression:.1f}% slower")
        else:
            print(f"✅ {metric}: {abs(regression):.1f}% faster")
    
    return True

if __name__ == "__main__":
    with open(sys.argv[1]) as f:
        current = json.load(f)
    with open(sys.argv[2]) as f:
        baseline = json.load(f)
    
    if not compare_metrics(current, baseline):
        sys.exit(1)
```

---

## 5. Metrics Collection

### Mobile Metrics
```dart
class PerformanceMetrics {
  final int coldStartMs;
  final int peakMemoryMb;
  final int apkSizeBytes;
  final double frameRate;
  final int syncTimeMs;
  
  Map<String, dynamic> toJson() => {
    'coldStartMs': coldStartMs,
    'peakMemoryMb': peakMemoryMb,
    'apkSizeBytes': apkSizeBytes,
    'frameRate': frameRate,
    'syncTimeMs': syncTimeMs,
  };
}
```

### Server Metrics
```csharp
public class PerformanceMetrics
{
    public long ColdStartMs { get; set; }
    public long ResponseTimeP95Ms { get; set; }
    public long MemoryMb { get; set; }
    public int ThroughputRps { get; set; }
    public long DynamoDbLatencyMs { get; set; }
}
```

---

## 6. Baseline Metrics Template

```json
{
  "mobile": {
    "coldStartMs": 2500,
    "peakMemoryMb": 95,
    "apkSizeBytes": 45000000,
    "frameRate": 60,
    "syncTimeMs": 4500
  },
  "server": {
    "coldStartMs": 1800,
    "responseTimeP95Ms": 450,
    "memoryMb": 512,
    "throughputRps": 100,
    "dynamoDbLatencyMs": 85
  },
  "integration": {
    "offlineToOnlineSyncMs": 8000,
    "conflictResolutionMs": 800,
    "networkLatencyMs": 1000
  }
}
```

---

## 7. Tools Installation

### k6
```bash
# macOS
brew install k6

# Linux
sudo apt-get install k6

# Run test
k6 run load-test.js
```

### BenchmarkDotNet
```bash
# Add to .csproj
<PackageReference Include="BenchmarkDotNet" Version="0.13.2" />

# Run benchmarks
dotnet run -c Release --project tests/Performance/
```

### Flutter DevTools
```bash
flutter pub global activate devtools
devtools
```

---

## References

- k6 Documentation: https://k6.io/docs/
- BenchmarkDotNet: https://benchmarkdotnet.org/
- Flutter Performance: https://flutter.dev/docs/perf
- AWS Lambda Performance: https://docs.aws.amazon.com/lambda/


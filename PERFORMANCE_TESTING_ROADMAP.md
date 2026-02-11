# Performance Testing Roadmap

**Status**: Planning phase  
**Priority**: High (critical for production readiness)

---

## Performance Targets

### Mobile (Flutter)
| Metric | Target | Current | Measurement |
|--------|--------|---------|-------------|
| Cold Start | <3s | TBD | `adb shell am start -W` |
| APK Size | <50MB | TBD | `ls -lh build/app/outputs/` |
| Memory (Peak) | <100MB | ~30-40MB (est.) | DevTools Memory tab |
| Memory (Idle) | <50MB | ~35MB (est.) | DevTools Memory tab |
| Frame Rate | 60 FPS | TBD | Performance Overlay |
| Sync Time (100 items) | <5s on 3G | TBD | Stopwatch in sync service |

### Server (.NET 10)
| Metric | Target | Current | Measurement |
|--------|--------|---------|-------------|
| Cold Start | <2s | TBD | Lambda duration metric |
| Response Time (p95) | <500ms | TBD | CloudWatch metrics |
| Memory | 512MB | TBD | Lambda memory config |
| Throughput | 100 req/s | TBD | Load test results |
| DynamoDB Latency | <100ms | TBD | CloudWatch metrics |

### Integration
| Metric | Target | Current | Measurement |
|--------|--------|---------|-------------|
| Offline→Online Sync | <10s | TBD | E2E test timing |
| Conflict Resolution | <1s | TBD | Unit test timing |
| Network Latency (3G) | 500-2000ms | Simulated | Network throttling |

---

## Implementation Plan

### Phase 1: Baseline Metrics (Weeks 1-2)

#### Mobile
```bash
# 1. Cold start measurement
adb shell am start -W com.weredaconnect.app/.MainActivity

# 2. APK size tracking
flutter build apk --release
ls -lh build/app/outputs/flutter-apk/app-release.apk

# 3. Memory profiling
flutter run --profile
# Use DevTools Memory tab
```

#### Server
```bash
# 1. Cold start in Lambda
# Add timing to HealthCheckFunction
var stopwatch = Stopwatch.StartNew();
// ... handler code ...
stopwatch.Stop();
response.Headers["X-Duration-Ms"] = stopwatch.ElapsedMilliseconds.ToString();

# 2. Load test baseline
# Use k6 or Apache JMeter
```

### Phase 2: CI/CD Integration (Weeks 3-4)

#### Mobile Performance Job
```yaml
- name: Measure APK Size
  run: |
    flutter build apk --release
    SIZE=$(ls -lh build/app/outputs/flutter-apk/app-release.apk | awk '{print $5}')
    echo "APK_SIZE=$SIZE" >> $GITHUB_ENV

- name: Store APK Size Metric
  uses: actions/upload-artifact@v4
  with:
    name: performance-metrics
    path: performance-metrics.json
```

#### Server Performance Job
```yaml
- name: Run Load Tests
  run: |
    dotnet test tests/Performance/ --configuration Release
    
- name: Collect Metrics
  run: |
    # Parse test results and store metrics
    dotnet test tests/Performance/ --logger json > results.json
```

### Phase 3: Performance Regression Detection (Weeks 5-6)

```yaml
- name: Compare with Baseline
  run: |
    # Compare current metrics with baseline
    # Fail if regression > 10%
    python scripts/compare_metrics.py \
      --current performance-metrics.json \
      --baseline baseline-metrics.json \
      --threshold 10
```

---

## Tools & Frameworks

### Mobile Performance
- **Flutter DevTools** - Memory, CPU, frame rate profiling
- **Android Profiler** - Native Android metrics
- **Dart Benchmark** - Sync performance benchmarks
- **Custom Stopwatch** - Timing critical operations

### Server Performance
- **k6** - Load testing (JavaScript-based, easy to write)
- **BenchmarkDotNet** - .NET micro-benchmarks
- **CloudWatch** - Lambda metrics collection
- **AWS X-Ray** - Distributed tracing

### Integration Performance
- **Maestro** - E2E test timing
- **Network Link Conditioner** - 3G simulation
- **Custom Timing Instrumentation** - Sync timing

---

## Test Scenarios

### Mobile
1. **Cold Start** - App launch from killed state
2. **Memory Leak** - Long-running app with repeated operations
3. **Sync Performance** - 100 items offline→online
4. **Conflict Resolution** - Concurrent updates
5. **Battery Drain** - 1 hour active use

### Server
1. **Concurrent Requests** - 100 concurrent users
2. **Cold Start** - First Lambda invocation
3. **DynamoDB Throughput** - Burst traffic
4. **Conflict Resolution** - Concurrent writes
5. **Error Handling** - Failure scenarios

### Integration
1. **Offline→Online** - Sync after connectivity restored
2. **Conflict Merge** - Server wins strategy
3. **Network Latency** - 3G simulation
4. **Concurrent Users** - Multiple devices syncing

---

## Metrics Collection

### Mobile
```dart
// Add to sync service
final stopwatch = Stopwatch()..start();
await syncService.syncAll();
stopwatch.stop();

// Log metrics
print('Sync completed in ${stopwatch.elapsedMilliseconds}ms');
// Store in local database for analysis
```

### Server
```csharp
// Add to Lambda handler
var stopwatch = Stopwatch.StartNew();
var result = await handler.Handle(request);
stopwatch.Stop();

// Include in response headers
response.Headers["X-Duration-Ms"] = stopwatch.ElapsedMilliseconds.ToString();
response.Headers["X-Cold-Start"] = isColdStart.ToString();
```

### CI/CD
```yaml
# Store metrics as artifacts
- name: Upload Performance Metrics
  uses: actions/upload-artifact@v4
  with:
    name: performance-metrics-${{ github.run_id }}
    path: |
      mobile/performance-metrics.json
      server/performance-metrics.json
```

---

## Success Criteria

### Phase 1 Complete
- [ ] Baseline metrics collected for all components
- [ ] Metrics stored in version control
- [ ] CI/CD jobs execute without errors

### Phase 2 Complete
- [ ] Performance jobs integrated into CI/CD
- [ ] Metrics collected on every PR/push
- [ ] Metrics dashboard created

### Phase 3 Complete
- [ ] Regression detection working
- [ ] Alerts configured for threshold violations
- [ ] Performance targets met or documented

---

## Constraints & Considerations

### AOT Compilation
- Can't use reflection-based profiling
- Use System.Diagnostics.Stopwatch for timing
- Avoid dynamic code generation

### Offline-First
- All tests must account for offline scenarios
- Sync performance critical for UX
- Conflict resolution must be fast

### Low-End Devices
- Test on 2GB RAM emulator
- Monitor memory usage continuously
- Optimize for ARM64 architecture

### 3G Networks
- Simulate 500-2000ms latency
- Test with packet loss (1-5%)
- Measure sync time on slow networks

---

## Next Steps

1. **Week 1**: Implement cold start measurement
2. **Week 2**: Add APK size tracking
3. **Week 3**: Implement load testing framework
4. **Week 4**: Integrate into CI/CD
5. **Week 5**: Create performance dashboard
6. **Week 6**: Set up regression detection

---

## References

- Mobile Optimization: `docs/mobile/optimization/`
- E2E Testing: `docs/developer/e2e-local-testing.md`
- Testing Guide: `mobile/TESTING.md`, `server/TESTING.md`
- Performance Targets: `server/README.md`, `mobile/README.md`


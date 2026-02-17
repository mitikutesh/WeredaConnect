# Sync Performance Metrics

**Last Updated**: 2026-02-15
**Test Environment**: Integration tests against deployed backend
**Backend**: https://s0om0hib4i.execute-api.me-south-1.amazonaws.com (me-south-1)

## Overview

This document tracks performance metrics for the offline-first sync engine, validating that sync operations meet our performance targets for Ethiopian 3G networks.

## Performance Targets

| Metric | Target | Rationale |
|--------|--------|-----------|
| Single operation sync latency | <5s | Responsive feedback on slow 3G networks |
| Batch processing (50 ops) | <250s | 5s avg per operation in batch |
| Conflict resolution overhead | <500ms | Minimal impact to sync time |
| Queue processing rate | >10 ops/min | Efficient for offline backlog |

## Test Scenarios

### 1. Basic Sync Flow

**Test**: Create listing offline, sync when online
**Operations**: 1 crop listing

**Metrics** (to be populated after test run):
- Sync latency: ___ ms
- Success rate: ___/___
- Status: ___

**Ethiopian Context**: This simulates a farmer creating a listing in a rural area with no connectivity, then syncing when they reach a market with 3G coverage.

### 2. Multiple Operations Sync

**Test**: Batch sync of 10 crop listings
**Operations**: 10 crop listings

**Metrics** (to be populated after test run):
- Total sync time: ___ s
- Average latency per operation: ___ ms
- Success rate: ___/___
- Throughput: ___ ops/min

**Ethiopian Context**: This simulates a farmer who has been offline for several days, accumulating multiple listings that need to be synced.

### 3. Large Batch Processing

**Test**: Batch sync of 50 crop listings
**Operations**: 50 crop listings

**Metrics** (to be populated after test run):
- Total sync time: ___ s
- Average latency per operation: ___ ms
- Success rate: ___/___
- Throughput: ___ ops/min
- Memory usage: ___ MB (if measured)

**Ethiopian Context**: This simulates extreme offline scenarios, such as a cooperative with multiple farmers syncing their collective listings.

### 4. Conflict Resolution Performance

**Test**: Resolve conflict with server-wins strategy
**Operations**: 1 conflict resolution

**Metrics** (to be populated after test run):
- Conflict detection time: ___ ms
- Conflict resolution time: ___ ms
- Total overhead: ___ ms
- Local data update time: ___ ms

**Ethiopian Context**: Conflicts are rare but must be resolved quickly to avoid user confusion.

## Running Performance Tests

### Prerequisites

1. **Backend deployed**: Ensure the backend is deployed and accessible
   ```bash
   curl https://s0om0hib4i.execute-api.me-south-1.amazonaws.com/health
   ```

2. **Test credentials**: Configure test user authentication (if required)

3. **Clean database**: Tests run with a fresh Isar database

### Execute Tests

Run the integration tests from the mobile directory:

```bash
cd mobile
flutter test integration_test/sync_crop_listing_test.dart
```

For device testing (more accurate performance metrics):

```bash
# Android
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart

# iOS (if needed)
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart \
  -d <device-id>
```

### Measuring on Real Devices

For accurate performance metrics representative of Ethiopian conditions:

1. **Test on low-end Android device**: 2GB RAM, Android 8+
2. **Simulate 3G network**: Use network throttling (Android Developer Options)
3. **Monitor memory**: Use Android Studio Profiler
4. **Measure battery**: Check power consumption during sync

## Historical Results

### 2026-02-15 - Initial Integration Tests

**Status**: Tests created, awaiting first run with proper authentication

**Test Coverage**:
- ✅ Basic sync flow (offline → online)
- ✅ Multiple operations sync (10 listings)
- ✅ Batch processing (50 listings)
- ✅ Conflict resolution (server-wins)
- ✅ Edge cases (empty queue, concurrent sync prevention)

**Next Steps**:
1. Configure test user authentication
2. Run tests against deployed backend
3. Populate metrics in this document
4. Identify performance bottlenecks
5. Optimize if needed

## Performance Optimization Notes

### Current Optimizations

1. **Batch Processing**:
   - Max 50 operations per batch to minimize HTTP requests
   - 1s delay between batches to avoid server overload

2. **Network Timeouts**:
   - 30s connect timeout (accounts for slow 3G)
   - 30s receive timeout (allows large batch responses)

3. **Conflict Resolution**:
   - Server-wins strategy (minimal local processing)
   - Single Isar transaction per conflict (fast)

4. **Queue Management**:
   - FIFO ordering (oldest first)
   - Max 3 retries with exponential backoff
   - Synced operations purged after 30 days

### Potential Bottlenecks

Areas to monitor during testing:

1. **Network latency**: Ethiopian 3G can be unpredictable
2. **Isar write performance**: Large batches may slow on 2GB RAM devices
3. **JSON serialization**: 50 crop listings = ~50KB payload
4. **Conflict resolution**: Multiple conflicts in a batch could add latency

### Optimization Ideas (if needed)

If performance targets are not met:

1. **Reduce batch size**: From 50 to 25 operations
2. **Compress payloads**: Use gzip for large batches
3. **Parallel conflict resolution**: Process multiple conflicts concurrently
4. **Cache health checks**: Avoid redundant connectivity pings
5. **Incremental sync UI**: Show progress per operation (user perception)

## Monitoring in Production

Once deployed, track these metrics via CloudWatch:

1. **Sync latency** (P50, P95, P99)
2. **Batch size distribution**
3. **Conflict rate**
4. **Failure rate by error type**
5. **Retry count distribution**

## References

- **Sync Protocol**: `/specs/contracts/sync-protocol.md`
- **Integration Tests**: `/mobile/integration_test/sync_crop_listing_test.dart`
- **Sync Executor**: `/mobile/lib/core/sync/sync_executor.dart`
- **Conflict Resolver**: `/mobile/lib/core/sync/conflict_resolver.dart`

# Integration Tests

End-to-end tests for the WeredaConnect mobile app against real backend services.

## Test Files

### `sync_crop_listing_test.dart`
- Basic sync flow (offline → online)
- Multiple operations batch sync
- Conflict resolution (server-wins strategy)
- Performance metrics (sync latency)
- Edge cases (empty queue, concurrent sync prevention)

## Running Tests

```bash
cd mobile

# Quick test (no device)
flutter test integration_test/sync_crop_listing_test.dart

# On device/emulator (recommended for performance metrics)
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart

# With profiling
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/sync_crop_listing_test.dart \
  --profile
```

### Environment Override

```bash
flutter test integration_test/sync_crop_listing_test.dart \
  --dart-define=ENVIRONMENT=staging
```

Default backend: `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`

## Simulating Ethiopian 3G

### Android
1. Developer Options → Networking → Network Throttling
2. Download: 0.75 Mbps, Upload: 0.25 Mbps, Latency: 100-200ms

## Performance Targets

| Metric | Target |
|--------|--------|
| Single operation | <5s on 3G |
| Batch (50 ops) | <250s |
| Conflict resolution | <500ms |

## Troubleshooting

| Issue | Fix |
|-------|-----|
| "Isar instance already open" | `pkill -9 flutter && rm -rf /tmp/flutter_test_*` |
| "Connection refused" | Verify backend: `curl <api-url>/health` |
| Tests timing out | Increase timeout or reduce batch size |
| 401 errors | Implement test auth flow (TODO) |

## References

- Sync Protocol: `specs/contracts/sync-protocol.md`
- Sync Executor: `mobile/lib/core/sync/sync_executor.dart`

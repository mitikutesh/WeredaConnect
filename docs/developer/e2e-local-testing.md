# E2E Local Testing Guide

Mobile emulator + LocalStack backend, no AWS deployment needed.

## Architecture

```
Flutter App (Emulator) → LocalStack (Docker :4566) → DynamoDB / S3
```

## Prerequisites

- Docker 20.10+, Flutter 3.27+, Android SDK
- 8GB+ RAM, 10GB free disk
- Optional: [Maestro](https://get.maestro.mobile.dev) for E2E flows

---

## Quick Start

### 1. Start LocalStack Backend

```bash
# Terminal 1: Start LocalStack
cd server
docker-compose -f docker-compose.localstack.yml up -d

# Verify it's running
curl http://localhost:4566/_localstack/health

# Expected output:
# {"services": {"dynamodb": "running", "s3": "running", ...}}
```

### 2. Launch Emulator & Run App

```bash
# Start existing emulator (see android-emulation.md for setup)
emulator -avd WeredaConnect_API26 &

# Or list and launch available emulators
flutter emulators
flutter emulators --launch <emulator-id>

# Run Flutter app pointing to LocalStack
cd mobile
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566
```

### 3. Test E2E Flow

1. Enable airplane mode in emulator
2. Create a crop listing or trip request
3. Verify data saved locally
4. Disable airplane mode
5. Verify sync completes

---

## Network Configuration

| Platform | LocalStack URL |
|----------|---------------|
| Android emulator | `http://10.0.2.2:4566` (`10.0.2.2` = host machine) |
| iOS simulator | `http://localhost:4566` |
| Physical device | `http://<your-IP>:4566` (same WiFi network) |

```bash
# Physical device: find your IP, then run
flutter run -d <device-id> --dart-define=API_BASE_URL=http://<your-IP>:4566
```

---

## Emulator Setup

See **[Android Emulation Setup Guide](./android-emulation.md)** for detailed instructions.

```bash
# Create low-end AVD matching target devices (2GB RAM, API 26)
avdmanager create avd \
  --name wereda_low_end \
  --package "system-images;android-26;google_apis;x86_64" \
  --device "pixel_2" \
  --ram 2048
```

---

## Testing Scenarios

### Offline-First Flow
1. Start LocalStack + run mobile app
2. Enable airplane mode, create a crop listing, verify "Saved locally"
3. Disable airplane mode, verify sync completes
4. Confirm data in LocalStack: `curl http://localhost:4566/_aws/dynamodb/tables`

### Conflict Resolution
1. Create listing online, note ID
2. Go offline, edit listing
3. Modify same listing directly in LocalStack
4. Go online, verify server-wins conflict resolution

### Network Throttling (3G)
- Android emulator: Settings > Network > Preferred network type > 3G
- Verify: sync works (slower), UI stays responsive, timeouts trigger retry

### Voice-to-Text
- Requires physical device (emulator voice input is limited)
- Test with Amharic and Afan Oromo input on crop listing fields

---

## Integration Tests

```bash
cd mobile
flutter test integration_test/                              # all
flutter test integration_test/offline_sync_test.dart        # specific
flutter test integration_test/ -d <device-id> --verbose     # on device
```

---

## Maestro E2E Tests

### Setup Maestro

```bash
# Install Maestro
curl -Ls "https://get.maestro.mobile.dev" | bash

# Verify installation
maestro --version
```

### Example Maestro Flow

```yaml
# mobile/.maestro/create_crop_listing.yaml
appId: et.wereda.connect
---
# Launch app
- launchApp

# Verify home screen
- assertVisible: "WeredaConnect"

# Enable offline mode for testing
- tapOn: "Settings"
- tapOn: "Offline Mode"
- assertVisible: "Offline Mode Active"

# Navigate to create listing
- tapOn: "Create Listing"
- assertVisible: "New Crop Listing"

# Fill form
- tapOn:
    id: "crop_title_field"
- inputText: "Teff - 100kg"

- tapOn:
    id: "crop_price_field"
- inputText: "5000"

- tapOn:
    id: "location_field"
- inputText: "Addis Ababa, Bole"

# Save
- tapOn: "Save"

# Verify saved
- assertVisible: "Saved locally"
- assertVisible: "Pending Sync: 1"

# Go online
- tapOn: "Settings"
- tapOn: "Offline Mode"  # Toggle off
- assertVisible: "Syncing..."

# Wait for sync
- waitForAnimationToEnd:
    timeout: 10000

# Verify synced
- assertVisible: "Synced successfully"
- assertVisible: "Pending Sync: 0"
```

### Run Maestro Tests

```bash
# Run all flows
maestro test mobile/.maestro/

# Run specific flow
maestro test mobile/.maestro/create_crop_listing.yaml

# Run with recording
maestro test mobile/.maestro/ --record

# Run on specific device
maestro test mobile/.maestro/ --device <device-id>
```

---

## Debugging

### Logs

```bash
# LocalStack logs
docker logs -f weredaconnect-localstack
docker logs weredaconnect-localstack 2>&1 | grep ERROR

# Flutter logs
flutter logs
flutter logs | grep "SYNC"

# Android logcat
adb logcat | grep "WeredaConnect"
```

### Inspect Isar Database

```bash
flutter pub global activate devtools
flutter pub global run devtools
```

### Network Traffic Inspection

Use Charles Proxy, Proxyman (macOS), or mitmproxy to inspect HTTP requests between app and LocalStack.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "Connection refused" from app | Verify LocalStack running (`docker ps`), use `10.0.2.2` on Android (not `localhost`) |
| No emulators available | Run `flutter doctor -v`, accept licenses: `flutter doctor --android-licenses` |
| Sync not working | Check LocalStack endpoints: `curl http://localhost:4566/_aws/dynamodb/tables` |
| App crashes on low-end AVD | Increase AVD RAM to 3072 MB, profile with `flutter run --profile` |

---

## Performance Testing

```bash
# Cold start time (target: <3000ms on 2GB device)
adb shell am start -W et.wereda.connect/.MainActivity

# Memory/frame profiling
flutter run --profile   # then open DevTools → Memory tab
# Targets: <150MB memory, 60fps, sync <5s for 100 items on 3G
```

---

## CI/CD Integration

### GitHub Actions Example

```yaml
# .github/workflows/e2e-tests.yml
name: E2E Tests

on: [push, pull_request]

jobs:
  e2e-android:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v3

      - uses: subosito/flutter-action@v2
        with:
          flutter-version: '3.27.x'

      - name: Start LocalStack
        run: |
          cd server
          docker-compose -f docker-compose.localstack.yml up -d
          sleep 10

      - name: Run integration tests
        uses: reactivecircus/android-emulator-runner@v2
        with:
          api-level: 26
          ram-size: 2048M
          script: |
            cd mobile
            flutter test integration_test/

      - name: Stop LocalStack
        if: always()
        run: |
          cd server
          docker-compose -f docker-compose.localstack.yml down
```

---

## Best Practices

- Always test offline scenarios first
- Use realistic Ethiopian test data (phone: +251..., Amharic text, ETB currency)
- Test on low-end AVD (2GB RAM, API 26, 3G)
- Clean Isar DB and sync queue in `setUp()`

---

## Related Documentation

- **[Testing Guidelines](./testing-guidelines.md)** - Overall testing strategy
- **[Android Emulation Setup](./android-emulation.md)** - Emulator configuration
- **[Dev Workflow](./dev-workflow.md)** - Development process

---

**Last Updated**: 2026-02-16

# E2E Local Testing Guide

## Overview

Complete guide for running end-to-end tests locally with mobile emulator → backend server → database. This simulates real-world usage without deploying to AWS.

## Audience
- Developers testing full stack integration
- QA engineers validating user flows
- Contributors testing their changes

---

## Architecture

```
Flutter Mobile App (Emulator/Simulator)
    ↓ HTTP Requests
LocalStack (Docker)
    ├── API Gateway (Port 4566)
    ├── Lambda Functions
    ├── DynamoDB Tables
    └── S3 Buckets
```

---

## Prerequisites

### Required Tools
```bash
# Docker (for LocalStack)
docker --version  # Should be 20.10+

# Flutter
flutter --version  # Should be 3.27+

# Android SDK (for emulator)
flutter doctor  # Check Android toolchain

# Optional: Maestro (for E2E tests)
curl -Ls "https://get.maestro.mobile.dev" | bash
```

### System Requirements
- **macOS/Linux**: Recommended for best compatibility
- **RAM**: 8GB minimum (16GB recommended)
- **Disk**: 10GB free space
- **Network**: Internet connection for initial setup

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

### 2. Launch Mobile Emulator

For detailed emulator setup instructions, see:
**[Android Emulation Setup Guide](./android-emulation.md)**

Quick start:
```bash
# List and launch available emulators
flutter emulators
flutter emulators --launch <emulator-id>

# Or create a new low-end emulator (matches target devices)
flutter emulators --create \
  --name wereda_test \
  --device "pixel_2" \
  --api-level 26 \
  --ram 2048

# Verify device is connected
flutter devices
```

### 3. Run Mobile App

```bash
# Terminal 2: Run Flutter app
cd mobile

# Install dependencies
flutter pub get

# Generate code (Isar, localization)
flutter pub run build_runner build --delete-conflicting-outputs

# Run on emulator with LocalStack URL
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566
```

### 4. Test E2E Flow

```
1. In emulator: Enable airplane mode
2. Create a crop listing or trip request
3. Verify data saved locally (check app UI)
4. Disable airplane mode
5. Watch sync indicator (should show "Syncing...")
6. Verify data synced to LocalStack
```

---

## Network Configuration

### Android Emulator

**Important**: Android emulator uses special IP addresses:

- `10.0.2.2` → Your computer's `localhost`
- `localhost` → The emulator itself (won't work!)

```dart
// mobile/lib/core/config/environment.dart
class Environment {
  static const String apiBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'http://10.0.2.2:4566', // Android emulator
  );
}
```

### iOS Simulator

iOS simulator can use `localhost` directly:

```dart
import 'dart:io';

class ApiConfig {
  static String get baseUrl {
    if (Platform.isAndroid) {
      return 'http://10.0.2.2:4566';  // Android emulator
    } else if (Platform.isIOS) {
      return 'http://localhost:4566';  // iOS simulator
    } else {
      return 'http://192.168.1.100:4566';  // Physical device
    }
  }
}
```

### Physical Device Testing

For testing on real Android/iOS devices:

```bash
# 1. Find your computer's IP address
ifconfig | grep "inet " | grep -v 127.0.0.1
# Example output: inet 192.168.1.100

# 2. Update API base URL
# Use your computer's IP instead of localhost

# 3. Ensure phone and computer are on same WiFi network

# 4. Connect device via USB
adb devices  # Android
# or
flutter devices  # iOS

# 5. Run app
flutter run -d <device-id> --dart-define=API_BASE_URL=http://192.168.1.100:4566
```

---

## Emulator Setup

### Android Emulator

For detailed Android emulator setup, see:
**[Android Emulation Setup Guide](./android-emulation.md)**

Recommended settings for target devices (2GB RAM, low-end):

```bash
# Using Android Studio:
# Tools → Device Manager → Create Device

# Settings:
# - Device: Pixel 2 or similar
# - System Image: Android 8.0 (API 26) - Oreo
# - RAM: 2048 MB (2GB)
# - Internal Storage: 2048 MB
# - SD Card: 512 MB
# - Graphics: Automatic

# Or via command line:
avdmanager create avd \
  --name wereda_low_end \
  --package "system-images;android-26;google_apis;x86_64" \
  --device "pixel_2" \
  --ram 2048
```

### iOS Simulator

```bash
# List available simulators
xcrun simctl list devices

# Boot a simulator
xcrun simctl boot "iPhone SE (3rd generation)"

# Or use Xcode:
# Xcode → Open Developer Tool → Simulator
```

---

## Testing Scenarios

### Scenario 1: Offline-First Flow

**Goal**: Verify data is saved locally when offline and syncs when online

```bash
# 1. Start LocalStack
cd server && docker-compose -f docker-compose.localstack.yml up -d

# 2. Run mobile app
cd mobile && flutter run

# 3. In emulator:
#    a. Enable airplane mode (swipe down → tap airplane icon)
#    b. Create a crop listing:
#       - Title: "Teff - 100kg"
#       - Price: "5000 Birr"
#       - Location: "Addis Ababa, Bole"
#    c. Tap "Save"
#    d. Verify: "Saved locally - will sync when online" message
#    e. Check sync queue: Should show 1 pending item

# 4. Disable airplane mode
#    a. Swipe down → tap airplane icon again
#    b. Watch sync indicator: Should show "Syncing..."
#    c. Wait 2-3 seconds
#    d. Verify: "Synced successfully" message
#    e. Check sync queue: Should be empty

# 5. Verify in LocalStack
curl http://localhost:4566/_aws/dynamodb/tables
# Should show your data in DynamoDB
```

### Scenario 2: Conflict Resolution

**Goal**: Test what happens when same data is modified offline and on server

```bash
# 1. Create a crop listing while online
# 2. Note the listing ID
# 3. Go offline (airplane mode)
# 4. Edit the listing (change price)
# 5. Manually update the same listing on server (via LocalStack)
# 6. Go online
# 7. Verify conflict resolution (server wins by default)
```

### Scenario 3: Network Throttling (3G Simulation)

**Goal**: Test app performance on slow networks

```bash
# Android Emulator:
# Settings → Network & Internet → Mobile network → Preferred network type → 3G

# Or use Chrome DevTools:
# 1. Open Chrome DevTools
# 2. Network tab → Throttling → Slow 3G
# 3. Proxy emulator traffic through Chrome

# Expected behavior:
# - Sync should still work (may take longer)
# - UI should remain responsive
# - Show progress indicators
# - Timeout after 30 seconds with retry option
```

### Scenario 4: Voice-to-Text (Amharic)

**Goal**: Test voice input in local languages

```bash
# 1. Run app on physical device (emulator voice input is limited)
# 2. Navigate to "Create Crop Listing"
# 3. Tap microphone icon on title field
# 4. Speak in Amharic: "ጤፍ አምሳ ኪሎ" (Teff 50 kg)
# 5. Verify transcription accuracy
# 6. Repeat for Afan Oromo
```

---

## Integration Tests

### Running Flutter Integration Tests

```bash
# Run all integration tests
cd mobile
flutter test integration_test/

# Run specific test
flutter test integration_test/offline_sync_test.dart

# Run with verbose output
flutter test integration_test/ --verbose

# Run on specific device
flutter test integration_test/ -d <device-id>
```

### Example Integration Test

```dart
// mobile/integration_test/crop_listing_e2e_test.dart
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:weredaconnect/main.dart' as app;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Crop Listing E2E', () {
    testWidgets('Create listing offline and sync online', (tester) async {
      // Start app
      app.main();
      await tester.pumpAndSettle();

      // Verify home screen
      expect(find.text('WeredaConnect'), findsOneWidget);

      // Go offline
      await setConnectivity(false);
      await tester.pumpAndSettle();
      expect(find.text('Offline Mode'), findsOneWidget);

      // Navigate to create listing
      await tester.tap(find.byIcon(Icons.add));
      await tester.pumpAndSettle();

      // Fill form
      await tester.enterText(
        find.byKey(Key('crop_title')),
        'Teff - 100kg',
      );
      await tester.enterText(
        find.byKey(Key('crop_price')),
        '5000',
      );

      // Save
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // Verify saved locally
      expect(find.text('Saved locally'), findsOneWidget);
      expect(find.text('Pending Sync: 1'), findsOneWidget);

      // Go online
      await setConnectivity(true);
      await tester.pumpAndSettle(Duration(seconds: 5));

      // Verify synced
      expect(find.text('Synced'), findsOneWidget);
      expect(find.text('Pending Sync: 0'), findsOneWidget);
    });
  });
}
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
appId: com.weredaconnect.app
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

### Check Mobile → LocalStack Connectivity

Add this to your Flutter app for debugging:

```dart
// Test connectivity to LocalStack
Future<void> testLocalStackConnection() async {
  try {
    final response = await http.get(
      Uri.parse('http://10.0.2.2:4566/_localstack/health'),
    );
    print('LocalStack health: ${response.statusCode}');
    print('Response: ${response.body}');
  } catch (e) {
    print('Error connecting to LocalStack: $e');
  }
}
```

### View LocalStack Logs

```bash
# Follow logs in real-time
docker logs -f weredaconnect-localstack

# View last 100 lines
docker logs --tail 100 weredaconnect-localstack

# Search for errors
docker logs weredaconnect-localstack 2>&1 | grep ERROR
```

### View Mobile App Logs

```bash
# Flutter logs
flutter logs

# Filter by tag
flutter logs | grep "SYNC"

# Android logcat
adb logcat | grep "WeredaConnect"

# iOS logs
xcrun simctl spawn booted log stream --predicate 'processImagePath contains "WeredaConnect"'
```

### Inspect Isar Database

```bash
# Install Isar Inspector
flutter pub global activate isar_inspector

# Run inspector
flutter pub global run isar_inspector

# Or use Flutter DevTools
flutter pub global activate devtools
flutter pub global run devtools
```

### Monitor Network Traffic

Use a proxy tool to inspect HTTP requests:

```bash
# Option 1: Charles Proxy
# Download from https://www.charlesproxy.com/
# Configure emulator to use proxy: Settings → WiFi → Modify Network → Proxy

# Option 2: Proxyman (macOS)
# Download from https://proxyman.io/
# Automatic emulator detection

# Option 3: mitmproxy (CLI)
brew install mitmproxy
mitmproxy -p 8888
# Configure emulator proxy to localhost:8888
```

---

## Troubleshooting

### Issue: "Connection refused" from mobile app

**Cause**: Mobile app can't reach LocalStack

**Solutions**:
```bash
# 1. Verify LocalStack is running
docker ps | grep localstack

# 2. Check LocalStack health
curl http://localhost:4566/_localstack/health

# 3. Verify correct IP in mobile app
# Android: Use 10.0.2.2 (not localhost)
# iOS: Use localhost

# 4. Check firewall settings
# macOS: System Preferences → Security & Privacy → Firewall
# Allow incoming connections for Docker
```

### Issue: "No emulators available"

**Cause**: Android SDK not properly configured

**Solutions**:
```bash
# 1. Run Flutter doctor
flutter doctor -v

# 2. Install Android SDK
# Android Studio → Preferences → Appearance & Behavior → System Settings → Android SDK

# 3. Accept licenses
flutter doctor --android-licenses

# 4. Create emulator via Android Studio
# Tools → Device Manager → Create Device
```

### Issue: Sync not working

**Cause**: Sync service not triggering or failing

**Solutions**:
```bash
# 1. Check connectivity service
# Verify app detects online/offline state correctly

# 2. Check sync queue
# View pending items in Isar database

# 3. Enable debug logging
# Add print statements in sync service

# 4. Check LocalStack endpoints
curl http://localhost:4566/_aws/dynamodb/tables
```

### Issue: App crashes on low-end emulator

**Cause**: Memory constraints

**Solutions**:
```bash
# 1. Increase emulator RAM
# Edit AVD → Advanced Settings → RAM: 3072 MB

# 2. Reduce image cache in app
# Already configured in main.dart (50MB limit)

# 3. Profile memory usage
flutter run --profile
# DevTools → Memory tab

# 4. Optimize images
# Use cached_network_image with memory cache limits
```

---

## Performance Testing

### Measure Cold Start Time

```bash
# Android
adb shell am start -W com.weredaconnect.app/.MainActivity

# Expected output:
# TotalTime: 2500 (should be < 3000ms on 2GB device)
```

### Measure Sync Performance

```dart
// Add timing to sync service
final stopwatch = Stopwatch()..start();
await syncService.syncAll();
stopwatch.stop();
print('Sync completed in ${stopwatch.elapsedMilliseconds}ms');

// Target: < 5000ms for 100 items on 3G
```

### Memory Profiling

```bash
# Run in profile mode
flutter run --profile

# Open DevTools
flutter pub global run devtools

# Monitor:
# - Memory usage (should stay < 150MB on 2GB device)
# - Frame rendering (should be 60fps)
# - Network requests (should batch when possible)
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

### 1. Always Test Offline First
- Create data offline before testing online sync
- Verify local storage works independently
- Test graceful degradation

### 2. Use Realistic Test Data
- Ethiopian phone numbers: +251912345678
- Amharic/Afan Oromo text
- Local currency (Birr)
- Realistic locations (Addis Ababa, Bahir Dar, etc.)

### 3. Test on Low-End Devices
- 2GB RAM emulator
- Android 8.0 (API 26)
- Slow network (3G)
- Limited storage

### 4. Clean Up Between Tests
```dart
setUp(() async {
  // Clear Isar database
  await isar.writeTxn(() => isar.clear());

  // Reset sync queue
  await syncQueue.clear();

  // Reset connectivity state
  await setConnectivity(true);
});
```

### 5. Use Descriptive Test Names
```dart
// Good
testWidgets('should save crop listing locally when offline and sync when online')

// Bad
testWidgets('test sync')
```

---

## Related Documentation

- **[Testing Guidelines](./testing-guidelines.md)** - Overall testing strategy
- **[Mobile Testing](../../mobile/TESTING.md)** - Flutter-specific testing
- **[Server Testing](../../server/TESTING.md)** - Backend testing with LocalStack
- **[Getting Started](./getting-started.md)** - Initial setup
- **[Dev Workflow](./dev-workflow.md)** - Development process

---

## Next Steps

After setting up E2E testing:

1. ✅ Write integration tests for critical user flows
2. ✅ Add Maestro E2E tests for smoke testing
3. ✅ Set up CI/CD pipeline with E2E tests
4. ✅ Document test scenarios in `mobile/.maestro/`
5. ✅ Create test data fixtures for realistic testing

---

**Last Updated**: 2026-02-10
**Version**: 1.0.0
**Maintainer**: Development Team


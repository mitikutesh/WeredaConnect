# 📱 Android Emulation Setup Guide

## Overview

This guide covers setting up and running the WeredaConnect Flutter app on Android emulator for development and testing. For complete E2E testing with backend, see [E2E Local Testing Guide](./e2e-local-testing.md).

---

## Prerequisites

```bash
# Verify Flutter installation
flutter doctor

# Required: Android SDK, Android Studio, or command-line tools
# Minimum: Android 8.0 (API 26)
```

---

## Quick Setup

### 1. Create Android Emulator

**Option A: Using Android Studio (GUI)**
```
Tools → Device Manager → Create Virtual Device
- Device: Pixel 2 or Nexus 5
- System Image: Android 8.0 (API 26)
- RAM: 2048 MB (matches target devices)
- Storage: 2048 MB internal, 512 MB SD card
```

**Option B: Command Line**
```bash
# Install system image
sdkmanager "system-images;android-26;google_apis;arm64-v8a"

# Create emulator
avdmanager create avd \
  -n "WeredaConnect_API26" \
  -k "system-images;android-26;google_apis;arm64-v8a" \
  -d "Nexus 5"
```

### 2. Start Emulator

```bash
# List available emulators
emulator -list-avds

# Start emulator (runs in background)
emulator -avd WeredaConnect_API26 -memory 2048 &

# Verify device is connected
flutter devices
```

### 3. Run App

```bash
cd mobile
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
flutter run
```

---

## Testing with Backend

For app-to-server testing, see [E2E Local Testing Guide](./e2e-local-testing.md):

```bash
# Terminal 1: Start LocalStack
cd server && docker-compose -f docker-compose.localstack.yml up -d

# Terminal 2: Run app with LocalStack URL
cd mobile
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566
```

**Note**: `10.0.2.2` is Android emulator's special address for host's localhost.

---

## Debugging & Monitoring

```bash
# View real-time logs
flutter logs

# Hot reload (press 'r' while app running)
# Hot restart (press 'R')

# Inspect network traffic
adb shell setprop log.tag.OkHttp VERBOSE
flutter logs | grep OkHttp

# Clear app data
adb shell pm clear et.wereda.connect
```

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Emulator won't start | `emulator -avd <name> -wipe-data` |
| App crashes | `flutter clean && flutter pub get && flutter run` |
| Can't reach server | Use `10.0.2.2` not `localhost` |
| Slow emulator | Enable hardware acceleration: `emulator -avd <name> -accel on` |
| Database errors | `adb shell pm clear et.wereda.connect` |

---

## Performance Testing

Test on low-end device specs (2GB RAM, Android 8+):

```bash
# Create low-spec emulator
avdmanager create avd \
  -n "WeredaConnect_LowEnd" \
  -k "system-images;android-26;google_apis;arm64-v8a" \
  -d "Nexus 5" \
  -c 1024M  # 1GB RAM

# Run with memory constraints
emulator -avd WeredaConnect_LowEnd -memory 1024
```

---

## Common Commands

```bash
# List emulators
emulator -list-avds

# Start emulator
emulator -avd <name> &

# Check devices
flutter devices

# Run app
flutter run

# Run tests
flutter test
flutter test integration_test/

# Build APK
flutter build apk --debug

# Stop emulator
adb emu kill
```

---

**Last Updated**: 2026-02-10  
**Version**: 1.0.0


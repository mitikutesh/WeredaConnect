# Testing & Validation Guide

## Pre-Implementation Checklist

- [ ] Read MAIN_DART_READY_TO_IMPLEMENT.md
- [ ] Backup current main.dart
- [ ] Have Flutter SDK installed
- [ ] Have 2GB RAM device or emulator available

---

## Step 1: Code Compilation

```bash
# Navigate to mobile directory
cd mobile

# Get dependencies
flutter pub get

# Analyze code
flutter analyze

# Expected: No errors or warnings
```

---

## Step 2: Basic Functionality Test

```bash
# Run on device/emulator
flutter run

# Expected:
# - App launches successfully
# - No console errors
# - UI renders correctly
# - All 3 languages work (en, am, om)
```

---

## Step 3: Lifecycle Testing

### Test Pause/Resume Cycle

1. **Launch app**
   ```bash
   flutter run
   ```

2. **Press home button** (app paused)
   - Observe: Image cache should clear
   - Check DevTools Memory tab

3. **Return to app** (app resumed)
   - Observe: App resumes normally
   - Check: No memory leaks

4. **Repeat 5 times**
   - Verify: Consistent behavior

---

## Step 4: Error Handling Test

### Test Error Boundary

1. **Add intentional error** (temporary)
   ```dart
   // In HomePage.build()
   throw Exception('Test error');
   ```

2. **Run app**
   ```bash
   flutter run
   ```

3. **Observe**:
   - App should NOT crash
   - Error should appear in console
   - Error message should be readable

4. **Remove test error**
   ```bash
   git checkout mobile/lib/main.dart
   ```

---

## Step 5: Memory Profiling

### Using Flutter DevTools

```bash
# Open DevTools
flutter pub global activate devtools
devtools

# In another terminal
flutter run

# In DevTools:
# 1. Connect to running app
# 2. Go to Memory tab
# 3. Click "Record memory profile"
# 4. Perform actions:
#    - Launch app
#    - Pause app (home button)
#    - Resume app
#    - Pause again
# 5. Stop recording
# 6. Analyze memory graph
```

### Expected Results

```
Initial Load:     ~35-40MB
Peak Usage:       ~90-100MB
After Pause:      ~5-10MB (cache cleared)
After Resume:     ~35-40MB (cache rebuilt)
```

---

## Step 6: Image Cache Testing

### Verify Cache Limits

```dart
// Add to HomePage.build() temporarily
print('Image cache size: ${imageCache.currentSize}');
print('Image cache bytes: ${imageCache.currentSizeBytes}');

// Expected:
// Image cache size: <= 50
// Image cache bytes: <= 52,428,800 (50MB)
```

---

## Step 7: APK Size Verification

```bash
# Build release APK
flutter build apk --release

# Check size
ls -lh build/app/outputs/flutter-apk/app-release.apk

# Expected: < 50MB
```

---

## Step 8: Performance Overlay Test

```bash
# Run with performance overlay
flutter run --profile

# In app:
# - Press 'P' to toggle performance overlay
# - Observe frame rate
# - Expected: 60 FPS (or 120 FPS on high-end)
# - No jank (frames >16ms)
```

---

## Step 9: 2GB RAM Device Testing

### Emulator Setup

```bash
# Create 2GB RAM emulator
flutter emulators create --name low_end_2gb

# Edit config
# ~/.android/avd/low_end_2gb.avd/config.ini
# Set: hw.ramSize=2048

# Run on emulator
flutter run -d low_end_2gb
```

### Physical Device Testing

```bash
# Connect 2GB RAM device
adb devices

# Run app
flutter run -d <device_id>

# Monitor memory
adb shell dumpsys meminfo et.wereda.connect
```

---

## Step 10: Offline Mode Testing

```bash
# Enable airplane mode on device
# OR use emulator network settings

# Test app functionality:
# - App should work normally
# - No network errors
# - Sync queue should work when back online

# Disable airplane mode
# Verify sync completes
```

---

## Validation Checklist

### Compilation
- [ ] `flutter analyze` passes
- [ ] No warnings or errors
- [ ] Code compiles successfully

### Functionality
- [ ] App launches
- [ ] UI renders correctly
- [ ] All 3 languages work
- [ ] Offline mode works

### Memory
- [ ] Initial RAM < 50MB
- [ ] Peak RAM < 100MB
- [ ] Background RAM < 10MB
- [ ] No memory leaks

### Lifecycle
- [ ] Pause/resume works
- [ ] Cache clears on pause
- [ ] Cache rebuilds on resume
- [ ] No crashes on lifecycle changes

### Error Handling
- [ ] Errors don't crash app
- [ ] Errors logged to console
- [ ] App recovers gracefully

### Performance
- [ ] 60 FPS on 2GB device
- [ ] Load time < 3s
- [ ] No jank (>16ms frames)
- [ ] APK size < 50MB

---

## Troubleshooting

### Issue: App crashes on launch
**Solution**: Check imports, verify `foundation.dart` is imported

### Issue: Memory not decreasing on pause
**Solution**: Verify lifecycle observer is registered

### Issue: High memory usage
**Solution**: Check image cache limits, profile with DevTools

### Issue: APK size increased
**Solution**: Run `flutter clean`, rebuild

---

## Performance Targets

| Metric | Target | Pass/Fail |
|--------|--------|-----------|
| Initial RAM | <50MB | [ ] |
| Peak RAM | <100MB | [ ] |
| Background RAM | <10MB | [ ] |
| APK Size | <50MB | [ ] |
| Load Time | <3s | [ ] |
| Frame Rate | 60 FPS | [ ] |
| Crash Rate | <0.5% | [ ] |

---

## Sign-Off

- [ ] All tests passed
- [ ] All metrics met
- [ ] Ready for production
- [ ] Documented any issues

---

**Testing Date**: ___________
**Tester**: ___________
**Status**: ___________


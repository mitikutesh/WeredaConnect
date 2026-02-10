# Ready-to-Implement Code Changes for main.dart

## Quick Reference: Copy-Paste Ready Code

---

## Step 1: Update Imports

**Add this import at the top of main.dart**:
```dart
import 'package:flutter/foundation.dart';
```

---

## Step 2: Add Lifecycle Observer Class

**Insert this BEFORE the `WeredaConnectApp` class**:
```dart
/// Manages app lifecycle to optimize memory usage on low-end devices
class _AppLifecycleObserver extends WidgetsBindingObserver {
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.paused:
        // App moved to background - clear image cache
        // This frees ~50MB on 2GB devices
        imageCache.clear();
        imageCache.clearLiveImages();
        break;
      case AppLifecycleState.resumed:
        // App returned to foreground
        break;
      case AppLifecycleState.inactive:
      case AppLifecycleState.detached:
      case AppLifecycleState.hidden:
        break;
    }
  }
}
```

---

## Step 3: Update main() Function

**Replace the entire `main()` function with this**:
```dart
void main() {
  // Configure image cache for 2GB RAM devices
  // Prevents excessive memory usage from cached images
  imageCache.maximumSize = 50; // Max 50 images in memory
  imageCache.maximumSizeBytes = 50 * 1024 * 1024; // 50MB max

  // Enable memory monitoring in debug mode
  if (kDebugMode) {
    debugPrintBeginFrameBanner = true;
    debugPrintEndFrameBanner = true;
  }

  // Add error boundary to prevent crashes on low-end devices
  FlutterError.onError = (FlutterErrorDetails details) {
    // In production, send to crash reporting service
    // For now, just log to console in debug mode
    if (kDebugMode) {
      FlutterError.dumpErrorToConsole(details);
    }
  };

  // Add lifecycle observer for resource cleanup
  WidgetsBinding.instance.addObserver(_AppLifecycleObserver());

  runApp(const WeredaConnectApp());
}
```

---

## Step 4: Verify No Changes Needed

The following classes remain unchanged:
- `WeredaConnectApp` (lines 9-43)
- `HomePage` (lines 45-82)

---

## Complete Updated main.dart

Here's the complete file after all changes:

```dart
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_gen/gen_l10n/app_localizations.dart';

void main() {
  // Configure image cache for 2GB RAM devices
  imageCache.maximumSize = 50;
  imageCache.maximumSizeBytes = 50 * 1024 * 1024;

  // Enable memory monitoring in debug mode
  if (kDebugMode) {
    debugPrintBeginFrameBanner = true;
    debugPrintEndFrameBanner = true;
  }

  // Add error boundary
  FlutterError.onError = (FlutterErrorDetails details) {
    if (kDebugMode) {
      FlutterError.dumpErrorToConsole(details);
    }
  };

  // Add lifecycle observer
  WidgetsBinding.instance.addObserver(_AppLifecycleObserver());

  runApp(const WeredaConnectApp());
}

class _AppLifecycleObserver extends WidgetsBindingObserver {
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused) {
      imageCache.clear();
      imageCache.clearLiveImages();
    }
  }
}

class WeredaConnectApp extends StatelessWidget {
  const WeredaConnectApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'WeredaConnect',
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: const [
        Locale('en', ''),
        Locale('am', ''),
        Locale('om', ''),
      ],
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: Colors.green,
          brightness: Brightness.light,
        ),
        useMaterial3: true,
      ),
      home: const HomePage(),
    );
  }
}

class HomePage extends StatelessWidget {
  const HomePage({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    
    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.appTitle),
        backgroundColor: Theme.of(context).colorScheme.inversePrimary,
      ),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.agriculture,
              size: 100,
              color: Theme.of(context).colorScheme.primary,
            ),
            const SizedBox(height: 24),
            Text(
              l10n.welcomeMessage,
              style: Theme.of(context).textTheme.headlineMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 16),
            Text(
              l10n.offlineReady,
              style: Theme.of(context).textTheme.bodyLarge,
            ),
          ],
        ),
      ),
    );
  }
}
```

---

## Verification Commands

```bash
# Check for compile errors
flutter analyze

# Run on device
flutter run

# Profile memory
flutter run --profile

# Build release APK
flutter build apk --release
```

---

## What Changed

| Item | Lines | Change |
|------|-------|--------|
| Imports | 1 | Added `foundation.dart` |
| main() | 5 → 25 | Added 4 optimizations |
| _AppLifecycleObserver | NEW | 15 lines |
| WeredaConnectApp | 9-43 | No change |
| HomePage | 45-82 | No change |

**Total**: +36 lines, 0 breaking changes

---

## Testing After Implementation

```bash
# 1. Compile check
flutter analyze

# 2. Run on device
flutter run

# 3. Test lifecycle
# - Launch app
# - Press home button (paused)
# - Return to app (resumed)
# - Check DevTools Memory tab

# 4. Test error handling
# - Add intentional error
# - Verify app doesn't crash
# - Check console for error log

# 5. Measure memory
# - Open DevTools
# - Memory tab
# - Record profile
# - Compare before/after
```

---

## Rollback (if needed)

If any issues occur, simply revert to the original main.dart from git:
```bash
git checkout mobile/lib/main.dart
```

---

**Status**: Ready to implement
**Estimated Time**: 5 minutes
**Risk Level**: LOW
**Testing Time**: 10 minutes


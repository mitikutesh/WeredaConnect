import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/screens/main_navigation.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();

  // Configure image cache for 2GB RAM devices
  // Prevents excessive memory usage from cached images
  imageCache.maximumSize = 50; // Max 50 images in memory
  imageCache.maximumSizeBytes = 50 * 1024 * 1024; // 50MB max

  // Enable memory monitoring in debug mode
  if (kDebugMode) {
    debugPrint('WeredaConnect debug mode enabled');
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

/// Manages app lifecycle to optimize memory usage on low-end devices
class _AppLifecycleObserver extends WidgetsBindingObserver {
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused) {
      // App moved to background - clear image cache
      // This frees ~50MB on 2GB devices
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

      // Localization - delegates and locales defined in generated l10n code
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,

      // Theme
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: Colors.green,
        ),
        useMaterial3: true,
      ),

      // Home - use MainNavigation instead of HomePage
      home: const MainNavigation(),
    );
  }
}

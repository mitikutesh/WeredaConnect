import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_gen/gen_l10n/app_localizations.dart';

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
    // For now, just log to console
    if (kDebugMode) {
      FlutterError.dumpErrorToConsole(details);
    }
  };

  // Add lifecycle observer for resource cleanup
  WidgetsBinding.instance.addObserver(_AppLifecycleObserver());

  runApp(const WeredaConnectApp());
}

/// Observes app lifecycle to manage resources efficiently
class _AppLifecycleObserver extends WidgetsBindingObserver {
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.paused:
        // App moved to background - clear non-essential caches
        // This helps free up RAM for other apps
        imageCache.clear();
        imageCache.clearLiveImages();
        break;
      case AppLifecycleState.resumed:
        // App returned to foreground - resume normal operations
        break;
      case AppLifecycleState.inactive:
      case AppLifecycleState.detached:
      case AppLifecycleState.hidden:
        break;
    }
  }
}

class WeredaConnectApp extends StatelessWidget {
  const WeredaConnectApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'WeredaConnect',

      // Localization - only 3 locales for low-end devices
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: const [
        Locale('en', ''), // English
        Locale('am', ''), // Amharic
        Locale('om', ''), // Afan Oromo
      ],

      // Theme - lightweight, single ColorScheme
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: Colors.green,
          brightness: Brightness.light,
        ),
        useMaterial3: true,
      ),

      // Home - simple, stateless widget
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

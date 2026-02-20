import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/core/network/data_usage_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Initialize data usage tracker before API client so the interceptor
  // can be attached immediately.
  final dataUsageTracker = DataUsageTracker();
  await dataUsageTracker.initialize();

  // Initialize API client before AuthBloc creation
  ApiClient().initialize(
    interceptors: [DataUsageInterceptor(dataUsageTracker)],
  );

  // Initialize Isar before runApp - required for AuthCheckRequested
  // which reads cached user from Isar
  await IsarService.instance.initialize();

  // Initialize language provider and load saved language preference
  final languageProvider = LanguageProvider();
  await languageProvider.initialize();

  // Initialize settings provider and load persisted settings
  final settingsProvider = SettingsProvider();
  await settingsProvider.initialize();

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

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: languageProvider),
        ChangeNotifierProvider.value(value: settingsProvider),
        ChangeNotifierProvider.value(value: dataUsageTracker),
      ],
      child: const WeredaConnectApp(),
    ),
  );
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
    return Provider<ConnectivityMonitor>(
      create: (_) => ConnectivityMonitor(dio: ApiClient().dio),
      dispose: (_, monitor) => monitor.dispose(),
      child: MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>(
          create: (_) => AuthBloc()..add(const AuthCheckRequested()),
        ),
        BlocProvider<OnboardingBloc>(
          create: (_) => OnboardingBloc(),
        ),
        BlocProvider<CropListingBloc>(
          create: (_) => CropListingBloc(
            repository: CropListingRepository(
              isar: IsarService.instance.db,
              syncQueue: SyncQueue(IsarService.instance.db),
            ),
          ),
        ),
        BlocProvider<TripListingBloc>(
          create: (_) => TripListingBloc(
            repository: TripListingRepository(
              isar: IsarService.instance.db,
              syncQueue: SyncQueue(IsarService.instance.db),
            ),
          ),
        ),
        BlocProvider<DeliveryBloc>(
          create: (_) => DeliveryBloc(
            repository: DeliveryRepository(
              isar: IsarService.instance.db,
              syncQueue: SyncQueue(IsarService.instance.db),
            ),
          ),
        ),
        BlocProvider<EscrowBloc>(
          create: (context) => EscrowBloc(
            repository: EscrowRepository(
              isar: IsarService.instance.db,
              apiClient: ApiClient(),
              connectivityMonitor: context.read<ConnectivityMonitor>(),
            ),
          ),
        ),
      ],
      child: Consumer<LanguageProvider>(
        builder: (context, languageProvider, child) {
          return MaterialApp(
            title: 'WeredaConnect',

            // Localization - use dynamic locale from provider
            locale: languageProvider.locale,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: const [
              Locale('en'), // English
              Locale('am'), // Amharic (app text + English Material widgets)
              Locale('om'), // Afan Oromo (app text + English Material widgets)
            ],

            // Theme
            theme: ThemeData(
              colorScheme: ColorScheme.fromSeed(
                seedColor: Colors.green,
              ),
              useMaterial3: true,
            ),

            // Auth gate handles routing based on authentication state
            home: const AuthGate(),
          );
        },
      ),
    ),
    );
  }
}

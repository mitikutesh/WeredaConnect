import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/core/sync/sync_worker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/core/network/auth_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Warn if API credentials are missing — the app still starts in offline/demo
  // mode but online login and sync will be unavailable.
  if (!Environment.isApiConfigured) {
    debugPrint(
      '[WeredaConnect] WARNING: API_BASE_URL and Cognito credentials not '
      'provided via --dart-define. Online features will be unavailable.',
    );
  }

  // Initialize data usage tracker before API client so the interceptor
  // can be attached immediately.
  final dataUsageTracker = DataUsageTracker();
  await dataUsageTracker.initialize();

  // Initialize API client before AuthBloc creation
  ApiClient().initialize(
    interceptors: [DataUsageInterceptor(dataUsageTracker)],
  );

  // Wire AuthInterceptor for automatic token injection and 401 refresh.
  // Must be created after ApiClient.initialize() since it needs the Dio instance.
  const secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  // Separate Dio for token refresh — must NOT share the AuthInterceptor.
  // AuthInterceptor uses QueuedInterceptor which serializes all calls.
  // If the refresh callback used ApiClient().dio, the refresh POST would
  // queue behind the still-running onError handler → deadlock → timeout
  // → "Authentication failed" on every sync attempt.
  final refreshDio = Dio(
    BaseOptions(
      baseUrl: ApiClient().dio.options.baseUrl,
      connectTimeout: ApiClient().dio.options.connectTimeout,
      receiveTimeout: ApiClient().dio.options.receiveTimeout,
      headers: Map<String, dynamic>.from(ApiClient().dio.options.headers),
    ),
  );

  final authInterceptor = AuthInterceptor(
    storage: secureStorage,
    dio: ApiClient().dio,
    refreshTokenCallback: (refreshToken) async {
      try {
        final response = await refreshDio.post<Map<String, dynamic>>(
          '/auth/refresh',
          data: {'refreshToken': refreshToken},
        );
        final data = response.data as Map<String, dynamic>;
        final tokenData = data['tokens'] as Map<String, dynamic>;
        final newAccessToken = tokenData['accessToken'] as String;
        final newRefreshToken =
            tokenData['refreshToken'] as String? ?? refreshToken;

        // Update refresh token in storage so next refresh uses the new one
        await secureStorage.write(
          key: 'refresh_token',
          value: newRefreshToken,
        );

        // Also update per-phone token cache for offline auth
        final phone = await secureStorage.read(key: 'current_user_phone');
        if (phone != null) {
          final expiresIn = tokenData['expiresIn'] as int? ?? 3600;
          final tokensJson = '{'
              '"accessToken":"$newAccessToken",'
              '"refreshToken":"$newRefreshToken",'
              '"idToken":"${tokenData['idToken']}",'
              '"expiresAt":"${DateTime.now().add(Duration(seconds: expiresIn)).toIso8601String()}"'
              '}';
          await secureStorage.write(key: 'tokens_$phone', value: tokensJson);
        }

        return newAccessToken;
      } on DioException catch (e) {
        // Server explicitly rejected the refresh token (400/401) — return null
        // to signal that tokens should be cleared and user must re-login.
        // Transient errors (timeout, network) — rethrow so AuthInterceptor
        // keeps the refresh token intact for the next attempt.
        final statusCode = e.response?.statusCode;
        if (statusCode != null && (statusCode == 400 || statusCode == 401)) {
          return null;
        }
        rethrow;
      }
    },
  );
  // Insert at position 0 so auth headers are injected before other interceptors
  ApiClient().dio.interceptors.insert(0, authInterceptor);

  // Initialize Isar before runApp - required for AuthCheckRequested
  // which reads cached user from Isar
  await IsarService.instance.initialize();

  // Create ConnectivityMonitor early so it can be shared between
  // the widget tree and EscrowSyncService.
  final connectivityMonitor = ConnectivityMonitor(dio: ApiClient().dio);

  // Start escrow sync service to process offline payment intents
  // when connectivity returns (MH2 / MH4 requirements).
  final escrowSyncService = EscrowSyncService(
    isar: IsarService.instance.db,
    apiClient: ApiClient(),
    connectivityMonitor: connectivityMonitor,
  );
  escrowSyncService.startListening();

  // Create conflict notification service for sync conflict UI
  final conflictNotificationService = ConflictNotificationService();

  // Create global SyncExecutor and SyncWorker for offline-first sync.
  // The executor auto-syncs on connectivity changes; the worker adds
  // periodic 15-minute sync and daily purge of old operations.
  final syncQueue = SyncQueue(IsarService.instance.db);

  // One-time migration: fix trip sync ops with server-rejected fields
  try {
    await syncQueue.migrateBadTripSyncData();
  } on Object catch (e) {
    debugPrint('[SYNC] migration failed (non-fatal): $e');
  }

  final syncExecutor = SyncExecutor(
    syncQueue: syncQueue,
    connectivityMonitor: connectivityMonitor,
    dio: ApiClient().dio,
    clientId: 'mobile-${DateTime.now().millisecondsSinceEpoch}',
    conflictResolver: ConflictResolver(IsarService.instance.db),
    conflictNotificationService: conflictNotificationService,
    getCurrentUserId: () => secureStorage.read(key: AuthBloc.currentUserIdKey),
  );
  final syncWorker = SyncWorker(
    executor: syncExecutor,
    syncQueue: syncQueue,
  );
  syncWorker.start();

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

  // Create AuthBloc early so the lifecycle observer can clear sensitive data
  final authBloc = AuthBloc(
    isar: IsarService.instance.isInitialized ? IsarService.instance.db : null,
  )..add(const AuthCheckRequested());

  // Add lifecycle observer for resource cleanup and sensitive data clearing
  WidgetsBinding.instance.addObserver(
    _AppLifecycleObserver(authBloc: authBloc, syncWorker: syncWorker),
  );

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: languageProvider),
        ChangeNotifierProvider.value(value: settingsProvider),
        ChangeNotifierProvider.value(value: dataUsageTracker),
      ],
      child: WeredaConnectApp(
        authBloc: authBloc,
        connectivityMonitor: connectivityMonitor,
        escrowSyncService: escrowSyncService,
        conflictNotificationService: conflictNotificationService,
        syncQueue: syncQueue,
        syncExecutor: syncExecutor,
        syncWorker: syncWorker,
      ),
    ),
  );
}

/// Manages app lifecycle to optimize memory usage on low-end devices
/// and clear sensitive in-memory data when the app is backgrounded.
class _AppLifecycleObserver extends WidgetsBindingObserver {
  _AppLifecycleObserver({required this.authBloc, required this.syncWorker});

  final AuthBloc authBloc;
  final SyncWorker syncWorker;

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused) {
      // App moved to background - clear image cache
      // This frees ~50MB on 2GB devices
      imageCache.clear();
      imageCache.clearLiveImages();

      // NOTE: No sensitive in-memory data to clear after Fayda OIDC migration.
      // Registration no longer stores pending passwords in AuthBloc.
    } else if (state == AppLifecycleState.resumed) {
      // App returned to foreground — trigger immediate sync attempt
      syncWorker.onAppResumed();
    }
  }
}

class WeredaConnectApp extends StatelessWidget {
  final AuthBloc authBloc;
  final ConnectivityMonitor connectivityMonitor;
  final EscrowSyncService escrowSyncService;
  final ConflictNotificationService conflictNotificationService;
  final SyncQueue syncQueue;
  final SyncExecutor syncExecutor;
  final SyncWorker syncWorker;

  const WeredaConnectApp({
    required this.authBloc,
    required this.connectivityMonitor,
    required this.escrowSyncService,
    required this.conflictNotificationService,
    required this.syncQueue,
    required this.syncExecutor,
    required this.syncWorker,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return MultiProvider(
      providers: [
        Provider<ConnectivityMonitor>(
          create: (_) => connectivityMonitor,
          dispose: (_, monitor) => monitor.dispose(),
        ),
        Provider<EscrowSyncService>(
          create: (_) => escrowSyncService,
          dispose: (_, service) => service.dispose(),
        ),
        Provider<ConflictNotificationService>(
          create: (_) => conflictNotificationService,
          dispose: (_, service) => service.dispose(),
        ),
        Provider<SyncQueue>(
          create: (_) => syncQueue,
          dispose: (_, queue) => queue.dispose(),
        ),
        Provider<SyncExecutor>(
          create: (_) => syncExecutor,
          dispose: (_, executor) => executor.dispose(),
        ),
        Provider<SyncWorker>(
          create: (_) => syncWorker,
          dispose: (_, worker) => worker.dispose(),
        ),
      ],
      child: MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(
            value: authBloc,
          ),
          BlocProvider<OnboardingBloc>(
            create: (_) => OnboardingBloc(),
          ),
          BlocProvider<CropListingBloc>(
            create: (_) => CropListingBloc(
              repository: CropListingRepository(
                isar: IsarService.instance.db,
                syncQueue: syncQueue,
              ),
            ),
          ),
          BlocProvider<TripListingBloc>(
            create: (_) => TripListingBloc(
              repository: TripListingRepository(
                isar: IsarService.instance.db,
                syncQueue: syncQueue,
              ),
            ),
          ),
          BlocProvider<DeliveryBloc>(
            create: (_) => DeliveryBloc(
              repository: DeliveryRepository(
                isar: IsarService.instance.db,
                syncQueue: syncQueue,
              ),
            ),
          ),
          BlocProvider<OrderBloc>(
            create: (_) => OrderBloc(
              repository: OrderRepository(
                isar: IsarService.instance.db,
                syncQueue: syncQueue,
              ),
            ),
          ),
          BlocProvider<BuyerIntentBloc>(
            create: (_) => BuyerIntentBloc(
              repository: BuyerIntentRepository(
                isar: IsarService.instance.db,
              ),
              matchingService: MatchingService(
                isar: IsarService.instance.db,
              ),
            ),
          ),
          BlocProvider<ConflictBloc>(
            create: (context) => ConflictBloc(
              notificationService: context.read<ConflictNotificationService>(),
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
                Locale(
                  'om',
                ), // Afan Oromo (app text + English Material widgets)
              ],

              // Theme
              theme: AppTheme.light(),

              // Auth gate handles routing based on authentication state
              home: const AuthGate(),
            );
          },
        ),
      ),
    );
  }
}

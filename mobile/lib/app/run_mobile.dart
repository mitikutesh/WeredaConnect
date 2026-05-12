import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/core/sync/sync_worker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/screens/auth_gate.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

Future<void> runMobile({
  required FlutterSecureStorage secureStorage,
  required DataUsageTracker dataUsageTracker,
  required LanguageProvider languageProvider,
  required SettingsProvider settingsProvider,
}) async {
  // Initialize Isar before runApp
  await IsarService.instance.initialize();

  // Connectivity monitor
  final connectivityMonitor = ConnectivityMonitor(dio: ApiClient().dio);

  // Escrow sync service
  final escrowSyncService = EscrowSyncService(
    isar: IsarService.instance.db,
    apiClient: ApiClient(),
    connectivityMonitor: connectivityMonitor,
  );
  escrowSyncService.startListening();

  // Conflict notification service
  final conflictNotificationService = ConflictNotificationService();

  // SyncQueue + SyncExecutor + SyncWorker
  final syncQueue = SyncQueue(IsarService.instance.db);

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

  // Configure image cache for 2GB RAM devices
  imageCache.maximumSize = 50;
  imageCache.maximumSizeBytes = 50 * 1024 * 1024;

  if (kDebugMode) {
    debugPrint('WeredaConnect debug mode enabled');
  }

  // AuthBloc with Isar for offline data cleanup on logout
  final authBloc = AuthBloc(
    isar: IsarService.instance.isInitialized ? IsarService.instance.db : null,
  )..add(const AuthCheckRequested());

  // Lifecycle observer for resource cleanup
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

class _AppLifecycleObserver extends WidgetsBindingObserver {
  _AppLifecycleObserver({required this.authBloc, required this.syncWorker});

  final AuthBloc authBloc;
  final SyncWorker syncWorker;

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused) {
      imageCache.clear();
      imageCache.clearLiveImages();
    } else if (state == AppLifecycleState.resumed) {
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
              locale: languageProvider.locale,
              localizationsDelegates: const [
                AppLocalizations.delegate,
                FallbackMaterialLocalizationsDelegate(),
                GlobalWidgetsLocalizations.delegate,
                FallbackCupertinoLocalizationsDelegate(),
              ],
              supportedLocales: const [
                Locale('en'),
                Locale('am'),
                Locale('om'),
              ],
              theme: AppTheme.light(),
              home: const AuthGate(),
            );
          },
        ),
      ),
    );
  }
}

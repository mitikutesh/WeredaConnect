// Web-only entry point for WeredaConnect.
// Build with: flutter build web --target lib/main_web.dart
// This avoids importing Isar/FFI code that doesn't compile to JavaScript.
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_web_plugins/url_strategy.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/auth_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_auth_binding.dart';
import 'package:wereda_connect/core/network/data_usage_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/core/routing/app_router.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/auth/token_refresh.dart';
import 'package:wereda_connect/core/auth/web_test_auth_seam.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/onboarding/bloc/onboarding_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

// Web-only repositories (no Isar)
import 'package:wereda_connect/features/buyer/data/repositories/web_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/web_matching_service.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/web_delivery_repository.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/messaging/data/repositories/web_message_repository.dart';
import 'package:wereda_connect/features/messaging/data/web_conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/rating/data/repositories/web_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';

Future<void> main() async {
  usePathUrlStrategy();
  WidgetsFlutterBinding.ensureInitialized();

  if (!Environment.isApiConfigured) {
    debugPrint(
      '[WeredaConnect] WARNING: API_BASE_URL and Cognito credentials not '
      'provided via --dart-define. Online features will be unavailable.',
    );
  }

  // Initialize data usage tracker
  final dataUsageTracker = DataUsageTracker();
  await dataUsageTracker.initialize();

  // Initialize API client (no cert pinning on web)
  ApiClient().initialize(
    // Requests are metered after AuthInterceptor (sees the bearer token),
    // responses before it (see DataUsageInterceptor.responses).
    interceptors: [DataUsageInterceptor.requests(dataUsageTracker)],
  );

  // Wire AuthInterceptor for automatic token injection and 401 refresh.
  const secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  final refreshDio = Dio(
    BaseOptions(
      baseUrl: ApiClient().dio.options.baseUrl,
      connectTimeout: ApiClient().dio.options.connectTimeout,
      receiveTimeout: ApiClient().dio.options.receiveTimeout,
      headers: Map<String, dynamic>.from(ApiClient().dio.options.headers),
    ),
  );
  refreshDio.interceptors.add(DataUsageInterceptor(dataUsageTracker));

  // Shared refresh callback: persists the new tokens, restarts the 48h
  // offline session clock, and signs the user out (SessionSignals) when the
  // server rejects the refresh token.
  final authInterceptor = AuthInterceptor(
    storage: secureStorage,
    dio: ApiClient().dio,
    retryInterceptors: [DataUsageInterceptor(dataUsageTracker)],
    refreshTokenCallback: buildRefreshTokenCallback(
      refreshDio: refreshDio,
      storage: secureStorage,
    ),
  );
  ApiClient().dio.interceptors.insert(0, authInterceptor);
  ApiClient()
      .dio
      .interceptors
      .insert(0, DataUsageInterceptor.responses(dataUsageTracker));

  // Initialize language and settings providers
  final languageProvider = LanguageProvider();
  await languageProvider.initialize();

  final settingsProvider = SettingsProvider();
  await settingsProvider.initialize();

  // Error boundary
  FlutterError.onError = (FlutterErrorDetails details) {
    if (kDebugMode) {
      FlutterError.dumpErrorToConsole(details);
    }
  };

  final authBloc = AuthBloc()..add(const AuthCheckRequested());
  // F-37: production-gated test auth seam for e2e (no-op in prod builds).
  registerWebTestAuthSeam(authBloc);
  bindDataUsageToAuth(dataUsageTracker, authBloc);
  final api = ApiClient();

  // SIM-37: web has no offline sync queue, so push a language change straight to
  // the server profile (best effort) through the shared /sync endpoint, so the
  // choice follows the user to other devices. No-op until authenticated.
  languageProvider.onLanguageChanged = (languageCode) async {
    final state = authBloc.state;
    if (state is! AuthAuthenticated) return;
    final userId = state.userId;
    final now = DateTime.now().toUtc();
    final epochMs = now.millisecondsSinceEpoch;
    try {
      await api.dio.post<dynamic>(
        '/sync',
        data: {
          'syncBatchId': 'lang-$epochMs',
          'clientId': 'web',
          'operations': [
            {
              'operationId': 'lang-$epochMs',
              'idempotencyKey': '$userId:user:$userId:update:$epochMs',
              'entityType': 'user',
              'entityId': userId,
              'action': 'update',
              'data': {'preferredLanguage': languageCode},
              'timestamp': now.toIso8601String(),
            },
          ],
        },
      );
    } on DioException catch (e) {
      debugPrint('[main_web] language sync failed: ${e.message}');
    }
  };

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: languageProvider),
        ChangeNotifierProvider.value(value: settingsProvider),
        ChangeNotifierProvider.value(value: dataUsageTracker),
        // No Isar on web: nothing to pull into a local cache. Read as null.
        Provider<MarketplaceSync?>.value(value: null),
      ],
      child: MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<OnboardingBloc>(create: (_) => OnboardingBloc()),
          BlocProvider<CropListingBloc>(
            create: (_) => CropListingBloc(
              repository: WebCropListingRepository(api: api),
            ),
          ),
          BlocProvider<TripListingBloc>(
            create: (_) => TripListingBloc(
              repository: WebTripListingRepository(api: api),
            ),
          ),
          BlocProvider<DeliveryBloc>(
            create: (_) => DeliveryBloc(
              repository: WebDeliveryRepository(api: api),
            ),
          ),
          BlocProvider<OrderBloc>(
            create: (_) => OrderBloc(
              repository: WebOrderRepository(api: api),
            ),
          ),
          BlocProvider<BuyerIntentBloc>(
            create: (_) => BuyerIntentBloc(
              repository: WebBuyerIntentRepository(api: api),
              matchingService: WebMatchingService(api: api),
            ),
          ),
          BlocProvider<RatingBloc>(
            create: (_) => RatingBloc(
              repository: WebRatingRepository(api: api),
            ),
          ),
          BlocProvider<InboxBloc>(
            create: (_) => InboxBloc(
              repository: WebMessageRepository(api: api),
              contextResolver: WebConversationContextResolver(
                api: api,
                roleOf: () {
                  final state = authBloc.state;
                  return state is AuthAuthenticated ? state.role : null;
                },
              ),
            ),
          ),
          BlocProvider<ConflictBloc>(
            create: (_) => ConflictBloc(
              notificationService: ConflictNotificationService(),
            ),
          ),
          BlocProvider<EscrowBloc>(
            create: (_) => EscrowBloc(
              repository: WebEscrowRepository(api: api),
            ),
          ),
        ],
        child: Consumer<LanguageProvider>(
          builder: (context, langProvider, child) {
            final router = createWebRouter(authBloc);
            return MaterialApp.router(
              title: 'WeredaConnect',
              locale: langProvider.locale,
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
              routerConfig: router,
            );
          },
        ),
      ),
    ),
  );
}

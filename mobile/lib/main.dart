import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/auth_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/core/routing/app_router.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
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

// Conditional import: native runner on mobile, stub on web
import 'package:wereda_connect/app/run_mobile.dart'
    if (dart.library.html) 'package:wereda_connect/app/run_mobile_web.dart';

// Web-only repositories
import 'package:wereda_connect/features/buyer/data/repositories/web_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/web_matching_service.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/web_delivery_repository.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';

Future<void> main() async {
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
    interceptors: [DataUsageInterceptor(dataUsageTracker)],
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

        await secureStorage.write(
          key: 'refresh_token',
          value: newRefreshToken,
        );

        // Update cached token structure so AuthBloc sees fresh tokens.
        final phone = await secureStorage.read(key: 'current_user_phone');
        if (phone != null) {
          final expiresIn = tokenData['expiresIn'] as int? ?? 3600;
          final tokensJson = '{'
              '"accessToken":"$newAccessToken",'
              '"refreshToken":"$newRefreshToken",'
              '"idToken":"${tokenData['idToken']}",'
              '"expiresAt":"${DateTime.now().add(Duration(seconds: expiresIn)).toIso8601String()}"'
              '}';
          await secureStorage.write(
            key: 'tokens_$phone',
            value: tokensJson,
          );
        }

        return newAccessToken;
      } on DioException catch (e) {
        final statusCode = e.response?.statusCode;
        if (statusCode != null && (statusCode == 400 || statusCode == 401)) {
          return null;
        }
        rethrow;
      }
    },
  );
  ApiClient().dio.interceptors.insert(0, authInterceptor);

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

  if (kIsWeb) {
    await _runWeb(
      secureStorage: secureStorage,
      dataUsageTracker: dataUsageTracker,
      languageProvider: languageProvider,
      settingsProvider: settingsProvider,
    );
  } else {
    await runMobile(
      secureStorage: secureStorage,
      dataUsageTracker: dataUsageTracker,
      languageProvider: languageProvider,
      settingsProvider: settingsProvider,
    );
  }
}

// ─── Web initialization ────────────────────────────────────────────

Future<void> _runWeb({
  required FlutterSecureStorage secureStorage,
  required DataUsageTracker dataUsageTracker,
  required LanguageProvider languageProvider,
  required SettingsProvider settingsProvider,
}) async {
  // No Isar, no SyncWorker, no cert pinning, no lifecycle observer on web
  final authBloc = AuthBloc()..add(const AuthCheckRequested());
  final api = ApiClient();

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: languageProvider),
        ChangeNotifierProvider.value(value: settingsProvider),
        ChangeNotifierProvider.value(value: dataUsageTracker),
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
              repository: WebBuyerIntentRepository(),
              matchingService: WebMatchingService(api: api),
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

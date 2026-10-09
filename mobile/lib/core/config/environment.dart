/// Environment configuration for API endpoints and feature flags
library;

/// Environment types
enum AppEnvironment {
  development,
  staging,
  production,
}

/// Global environment configuration
class Environment {
  const Environment._({
    required this.apiBaseUrl,
    required this.environment,
    required this.enableLogging,
    required this.connectTimeout,
    required this.receiveTimeout,
    required this.cognitoUserPoolId,
    required this.cognitoClientId,
    required this.cognitoRegion,
  });

  /// Current environment (set via --dart-define at build time)
  static AppEnvironment get current {
    const env =
        String.fromEnvironment('ENVIRONMENT', defaultValue: 'development');
    return AppEnvironment.values.firstWhere(
      (e) => e.name == env,
      orElse: () => AppEnvironment.development,
    );
  }

  /// Whether this build pays orders through the live Telebirr checkout
  /// (ESC-44). Only decides what the payment screen shows; what happens after
  /// a payment is decided by the server.
  static bool get telebirrLive => telebirrLiveFor(
        const String.fromEnvironment('ENVIRONMENT'),
        define: const bool.fromEnvironment('TELEBIRR_LIVE'),
      );

  /// [telebirrLive] for a raw `ENVIRONMENT` define. A production build is
  /// always live: the server refuses mock payments there. CI passes `prod`,
  /// which [current] does not recognise, so the raw value is read here.
  static bool telebirrLiveFor(String rawEnvironment, {required bool define}) {
    final env = rawEnvironment.trim().toLowerCase();
    return define || env == 'prod' || env == 'production';
  }

  /// Configuration for current environment
  static Environment get config {
    switch (current) {
      case AppEnvironment.development:
        return development;
      case AppEnvironment.staging:
        return staging;
      case AppEnvironment.production:
        return production;
    }
  }

  /// Development environment configuration (AWS Deployment - eu-north-1 Stockholm).
  /// SECURITY: All credentials MUST be passed via --dart-define at build time.
  /// No defaults are provided for Cognito/API values to prevent accidental leaks.
  static const development = Environment._(
    apiBaseUrl: String.fromEnvironment('API_BASE_URL'),
    environment: AppEnvironment.development,
    enableLogging: true,
    connectTimeout: Duration(seconds: 30),
    receiveTimeout: Duration(seconds: 30),
    cognitoUserPoolId: String.fromEnvironment('COGNITO_USER_POOL_ID'),
    cognitoClientId: String.fromEnvironment('COGNITO_CLIENT_ID'),
    cognitoRegion: String.fromEnvironment('COGNITO_REGION'),
  );

  /// Staging environment configuration
  static const staging = Environment._(
    apiBaseUrl: String.fromEnvironment('API_BASE_URL'),
    environment: AppEnvironment.staging,
    enableLogging: true,
    connectTimeout: Duration(seconds: 20),
    receiveTimeout: Duration(seconds: 20),
    cognitoUserPoolId: String.fromEnvironment('COGNITO_USER_POOL_ID'),
    cognitoClientId: String.fromEnvironment('COGNITO_CLIENT_ID'),
    cognitoRegion: String.fromEnvironment('COGNITO_REGION'),
  );

  /// Production environment configuration
  static const production = Environment._(
    apiBaseUrl: String.fromEnvironment('API_BASE_URL'),
    environment: AppEnvironment.production,
    enableLogging: false,
    connectTimeout: Duration(seconds: 15),
    receiveTimeout: Duration(seconds: 15),
    cognitoUserPoolId: String.fromEnvironment('COGNITO_USER_POOL_ID'),
    cognitoClientId: String.fromEnvironment('COGNITO_CLIENT_ID'),
    cognitoRegion: String.fromEnvironment('COGNITO_REGION'),
  );

  /// Base URL for API requests
  final String apiBaseUrl;

  /// Current environment
  final AppEnvironment environment;

  /// Enable debug logging
  final bool enableLogging;

  /// Connection timeout
  final Duration connectTimeout;

  /// Receive timeout
  final Duration receiveTimeout;

  /// AWS Cognito User Pool ID
  final String cognitoUserPoolId;

  /// AWS Cognito Client ID
  final String cognitoClientId;

  /// AWS Cognito Region
  final String cognitoRegion;

  /// Is development environment
  bool get isDevelopment => environment == AppEnvironment.development;

  /// Is staging environment
  bool get isStaging => environment == AppEnvironment.staging;

  /// Is production environment
  bool get isProduction => environment == AppEnvironment.production;

  /// Whether the required API credentials have been provided via --dart-define.
  static bool get isApiConfigured =>
      config.apiBaseUrl.isNotEmpty &&
      config.cognitoUserPoolId.isNotEmpty &&
      config.cognitoClientId.isNotEmpty &&
      config.cognitoRegion.isNotEmpty;
}

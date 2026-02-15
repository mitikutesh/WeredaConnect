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
  });

  /// Current environment (set via --dart-define at build time)
  static AppEnvironment get current {
    const env = String.fromEnvironment('ENVIRONMENT', defaultValue: 'development');
    return AppEnvironment.values.firstWhere(
      (e) => e.name == env,
      orElse: () => AppEnvironment.development,
    );
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

  /// Development environment configuration
  static const development = Environment._(
    apiBaseUrl: String.fromEnvironment(
      'API_BASE_URL',
      defaultValue: 'https://s0om0hib4i.execute-api.me-south-1.amazonaws.com',
    ),
    environment: AppEnvironment.development,
    enableLogging: true,
    connectTimeout: Duration(seconds: 30),
    receiveTimeout: Duration(seconds: 30),
  );

  /// Staging environment configuration
  static const staging = Environment._(
    apiBaseUrl: String.fromEnvironment(
      'API_BASE_URL',
      defaultValue: 'https://api-staging.weredaconnect.com',
    ),
    environment: AppEnvironment.staging,
    enableLogging: true,
    connectTimeout: Duration(seconds: 20),
    receiveTimeout: Duration(seconds: 20),
  );

  /// Production environment configuration
  static const production = Environment._(
    apiBaseUrl: String.fromEnvironment(
      'API_BASE_URL',
      defaultValue: 'https://api.weredaconnect.com',
    ),
    environment: AppEnvironment.production,
    enableLogging: false,
    connectTimeout: Duration(seconds: 15),
    receiveTimeout: Duration(seconds: 15),
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

  /// Is development environment
  bool get isDevelopment => environment == AppEnvironment.development;

  /// Is staging environment
  bool get isStaging => environment == AppEnvironment.staging;

  /// Is production environment
  bool get isProduction => environment == AppEnvironment.production;
}

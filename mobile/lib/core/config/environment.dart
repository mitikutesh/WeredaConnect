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

  /// Development environment configuration (AWS Deployment - me-south-1)
  static const development = Environment._(
    apiBaseUrl: String.fromEnvironment(
      'API_BASE_URL',
      defaultValue: 'https://s0om0hib4i.execute-api.me-south-1.amazonaws.com',
    ),
    environment: AppEnvironment.development,
    enableLogging: true,
    connectTimeout: Duration(seconds: 30),
    receiveTimeout: Duration(seconds: 30),
    cognitoUserPoolId: String.fromEnvironment(
      'COGNITO_USER_POOL_ID',
      defaultValue: 'me-south-1_lKHy1XJrd',
    ),
    cognitoClientId: String.fromEnvironment(
      'COGNITO_CLIENT_ID',
      defaultValue: '7g3kbk30cmbc297ghmid8pu1nm',
    ),
    cognitoRegion: String.fromEnvironment(
      'COGNITO_REGION',
      defaultValue: 'me-south-1',
    ),
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
    cognitoUserPoolId: String.fromEnvironment(
      'COGNITO_USER_POOL_ID',
      defaultValue: 'me-south-1_lKHy1XJrd',
    ),
    cognitoClientId: String.fromEnvironment(
      'COGNITO_CLIENT_ID',
      defaultValue: '7g3kbk30cmbc297ghmid8pu1nm',
    ),
    cognitoRegion: String.fromEnvironment(
      'COGNITO_REGION',
      defaultValue: 'me-south-1',
    ),
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
    cognitoUserPoolId: String.fromEnvironment(
      'COGNITO_USER_POOL_ID',
      defaultValue: 'me-south-1_lKHy1XJrd',
    ),
    cognitoClientId: String.fromEnvironment(
      'COGNITO_CLIENT_ID',
      defaultValue: '7g3kbk30cmbc297ghmid8pu1nm',
    ),
    cognitoRegion: String.fromEnvironment(
      'COGNITO_REGION',
      defaultValue: 'me-south-1',
    ),
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

  /// Enable mock auth for local development (bypasses real API)
  /// Set to true to test UI without server dependency
  static const bool useMockAuth = bool.fromEnvironment(
    'USE_MOCK_AUTH',
    defaultValue: true, // Default to true for easier development
  );
}

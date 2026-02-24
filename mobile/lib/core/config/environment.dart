/// Environment configuration for API endpoints and feature flags
library;

import 'package:flutter/foundation.dart';

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

  /// Development environment configuration (AWS Deployment - me-south-1).
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

  /// Enable mock auth for local development (bypasses real API).
  /// Defaults to true in debug builds, false in release/profile builds.
  /// Override with --dart-define=USE_MOCK_AUTH=true/false.
  static const bool useMockAuth = bool.hasEnvironment('USE_MOCK_AUTH')
      ? bool.fromEnvironment('USE_MOCK_AUTH')
      : kDebugMode;
}

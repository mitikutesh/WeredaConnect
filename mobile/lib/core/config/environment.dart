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
  static AppEnvironment get current =>
      environmentFor(const String.fromEnvironment('ENVIRONMENT'));

  /// [current] for a raw `ENVIRONMENT` define (FUP-22). CI and
  /// `scripts/deploy.sh` pass `dev`, `staging` or `prod`; the enum names are
  /// accepted too, in any case and with surrounding spaces. No define (a local
  /// run) is development. Before FUP-22 only the enum names matched, so a
  /// `prod` build ran with the development settings.
  ///
  /// Any other value is a build mistake. When [strict] (debug builds) it
  /// throws, so the app fails at startup instead of guessing. Otherwise
  /// (profile and release builds) it gets the production settings, the most
  /// restrictive ones: a typo must not ship development settings.
  static AppEnvironment environmentFor(
    String rawEnvironment, {
    bool strict = kDebugMode,
  }) {
    final known = switch (rawEnvironment.trim().toLowerCase()) {
      '' || 'dev' || 'development' => AppEnvironment.development,
      'staging' => AppEnvironment.staging,
      'prod' || 'production' => AppEnvironment.production,
      _ => null,
    };
    if (known != null) return known;
    if (strict) {
      throw ArgumentError.value(
        rawEnvironment,
        'ENVIRONMENT',
        'Unknown build environment; use dev, staging or prod',
      );
    }
    return AppEnvironment.production;
  }

  /// Whether this build pays orders through the live Telebirr checkout
  /// (ESC-44). Only decides what the payment screen shows; what happens after
  /// a payment is decided by the server.
  static bool get telebirrLive => telebirrLiveFor(
        const String.fromEnvironment('ENVIRONMENT'),
        define: const bool.fromEnvironment('TELEBIRR_LIVE'),
      );

  /// [telebirrLive] for a raw `ENVIRONMENT` define. A production build is
  /// always live: the server refuses mock payments there. The environment is
  /// read through [environmentFor], so this always agrees with [current].
  static bool telebirrLiveFor(
    String rawEnvironment, {
    required bool define,
    bool strict = kDebugMode,
  }) =>
      define ||
      environmentFor(rawEnvironment, strict: strict) ==
          AppEnvironment.production;

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

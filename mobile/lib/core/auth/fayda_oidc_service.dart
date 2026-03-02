import 'dart:convert';
import 'dart:math';

import 'package:dio/dio.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';

/// Result of parsing a Fayda OIDC callback URI.
class FaydaCallbackResult {
  final String? code;
  final String? state;
  final String? error;
  final String? errorDescription;

  const FaydaCallbackResult({
    this.code,
    this.state,
    this.error,
    this.errorDescription,
  });

  bool get isSuccess => code != null && error == null;
}

/// Result of exchanging a Fayda auth code for a verification token.
class FaydaExchangeResult {
  final bool isSuccess;
  final String? verificationToken;
  final String? verifiedName;
  final String? errorCode;
  final String? errorMessage;

  const FaydaExchangeResult({
    required this.isSuccess,
    this.verificationToken,
    this.verifiedName,
    this.errorCode,
    this.errorMessage,
  });
}

/// Service for Fayda eSignet OIDC authorization flow.
///
/// Uses the system browser for the authorization step (url_launcher),
/// then receives the callback via deep link (app_links).
///
/// Config via --dart-define:
/// - FAYDA_AUTHORIZE_URL: Fayda eSignet authorization endpoint
/// - FAYDA_CLIENT_ID: OAuth2 client ID registered with Fayda
class FaydaOidcService {
  static const _authorizeUrl = String.fromEnvironment('FAYDA_AUTHORIZE_URL');
  static const _clientId = String.fromEnvironment('FAYDA_CLIENT_ID');
  static const redirectUri = 'weredaconnect://fayda-callback';

  final Dio _dio;

  FaydaOidcService({Dio? dio}) : _dio = dio ?? ApiClient().dio;

  /// Whether Fayda OIDC is configured via dart-define.
  static bool get isConfigured =>
      _authorizeUrl.isNotEmpty && _clientId.isNotEmpty;

  /// Generate a cryptographically-random state parameter for CSRF protection.
  String generateState() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64Url.encode(bytes).replaceAll('=', '');
  }

  /// Build the Fayda eSignet authorization URL.
  Uri buildAuthorizationUrl(String state) {
    return Uri.parse(_authorizeUrl).replace(
      queryParameters: {
        'response_type': 'code',
        'client_id': _clientId,
        'redirect_uri': redirectUri,
        'scope': 'openid profile',
        'state': state,
        'acr_values': 'mosip:idp:acr:biometrics',
      },
    );
  }

  /// Parse the deep link callback URI from Fayda.
  FaydaCallbackResult parseCallback(Uri uri) {
    return FaydaCallbackResult(
      code: uri.queryParameters['code'],
      state: uri.queryParameters['state'],
      error: uri.queryParameters['error'],
      errorDescription: uri.queryParameters['error_description'],
    );
  }

  /// Exchange the authorization code for a verification token via our server.
  ///
  /// Our server handles the actual token exchange with Fayda eSignet
  /// and returns a short-lived verification token for registration.
  Future<FaydaExchangeResult> exchangeCode({
    required String authCode,
    required String state,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        ApiConfig.faydaVerifyEndpoint,
        data: {
          'authorizationCode': authCode,
          'state': state,
          'redirectUri': redirectUri,
        },
      );

      final data = response.data as Map<String, dynamic>;

      return FaydaExchangeResult(
        isSuccess: true,
        verificationToken: data['verificationToken'] as String,
        verifiedName: data['verifiedName'] as String?,
      );
    } on DioException catch (e) {
      if (e.response?.data is Map<String, dynamic>) {
        final error = (e.response!.data as Map<String, dynamic>)['error']
            as Map<String, dynamic>?;
        return FaydaExchangeResult(
          isSuccess: false,
          errorCode: error?['code'] as String? ?? 'FAYDA_001',
          errorMessage:
              error?['message'] as String? ?? 'Fayda verification failed',
        );
      }
      return const FaydaExchangeResult(
        isSuccess: false,
        errorCode: 'FAYDA_001',
        errorMessage: 'Fayda verification failed',
      );
    } on Exception {
      return const FaydaExchangeResult(
        isSuccess: false,
        errorCode: 'FAYDA_001',
        errorMessage: 'Fayda verification failed',
      );
    }
  }
}

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/json_body.dart';

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

/// Result of POST /auth/fayda-start or /user/fayda-start.
///
/// The server owns the OIDC parameters (client id, redirect, nonce, PKCE);
/// the app only opens [authorizeUrl] and checks that the callback carries the
/// same [state].
class FaydaStartResult {
  final bool isSuccess;
  final String? state;
  final Uri? authorizeUrl;
  final DateTime? expiresAt;
  final String? errorCode;
  final String? errorMessage;
  final int? retryAfterSeconds;

  const FaydaStartResult({
    required this.isSuccess,
    this.state,
    this.authorizeUrl,
    this.expiresAt,
    this.errorCode,
    this.errorMessage,
    this.retryAfterSeconds,
  });

  const FaydaStartResult.failure(
    String this.errorCode,
    String this.errorMessage, {
    this.retryAfterSeconds,
  })  : isSuccess = false,
        state = null,
        authorizeUrl = null,
        expiresAt = null;
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

/// Result of POST /user/fayda-link (verify an existing account later).
class FaydaLinkResult {
  final bool isSuccess;
  final bool faydaVerified;
  final DateTime? faydaVerifiedAt;
  final String? verifiedName;
  final String? errorCode;
  final String? errorMessage;

  const FaydaLinkResult({
    required this.isSuccess,
    this.faydaVerified = false,
    this.faydaVerifiedAt,
    this.verifiedName,
    this.errorCode,
    this.errorMessage,
  });
}

/// Client for the server-driven Fayda eSignet OIDC flow.
///
/// 1. [startRegistration] (public) or [startLink] (signed in) asks the server
///    for an authorize URL and a `state`.
/// 2. The caller opens the URL in the system browser; Fayda redirects back to
///    [redirectUri] (deep link).
/// 3. [parseCallback] reads the code; the caller checks the state matches.
/// 4. [exchangeCode] (registration) or [link] (existing account) completes it.
///
/// The app no longer builds the authorize URL itself, so it needs no Fayda
/// client id or authorize endpoint at build time.
class FaydaOidcService {
  /// Deep link Fayda redirects back to.
  static const redirectUri = 'weredaconnect://fayda-callback';

  final Dio _dio;

  FaydaOidcService({Dio? dio}) : _dio = dio ?? ApiClient().dio;

  /// Whether [uri] is our Fayda callback deep link.
  static bool isCallbackUri(Uri uri) =>
      uri.scheme == 'weredaconnect' && uri.host == 'fayda-callback';

  /// Parse the deep link callback URI from Fayda.
  FaydaCallbackResult parseCallback(Uri uri) {
    return FaydaCallbackResult(
      code: uri.queryParameters['code'],
      state: uri.queryParameters['state'],
      error: uri.queryParameters['error'],
      errorDescription: uri.queryParameters['error_description'],
    );
  }

  /// Start Fayda verification before an account exists
  /// (POST /auth/fayda-start). [phoneNumber] is optional: the registration
  /// flow runs Fayda before the phone step.
  Future<FaydaStartResult> startRegistration({String? phoneNumber}) {
    return _start(
      ApiConfig.faydaStartEndpoint,
      {
        if (phoneNumber != null && phoneNumber.isNotEmpty)
          'phoneNumber': phoneNumber,
      },
    );
  }

  /// Start Fayda verification for the signed-in user (POST /user/fayda-start).
  Future<FaydaStartResult> startLink() {
    return _start(ApiConfig.userFaydaStartEndpoint, const {});
  }

  Future<FaydaStartResult> _start(
    String endpoint,
    Map<String, dynamic> body,
  ) async {
    try {
      final response = await _dio.post<dynamic>(endpoint, data: body);
      final data = decodeJsonMap(response.data);
      final state = data?['state'];
      final url = data?['authorizeUrl'];
      final authorizeUrl = url is String ? Uri.tryParse(url) : null;
      if (state is! String ||
          state.isEmpty ||
          authorizeUrl == null ||
          !authorizeUrl.hasScheme) {
        return const FaydaStartResult.failure(
          AuthErrorCode.faydaServiceUnavailable,
          'Fayda start returned an unexpected response',
        );
      }
      return FaydaStartResult(
        isSuccess: true,
        state: state,
        authorizeUrl: authorizeUrl,
        expiresAt: _parseTime(data?['expiresAt']),
      );
    } on DioException catch (e) {
      final error = _errorOf(e);
      return FaydaStartResult.failure(
        error.code,
        error.message,
        retryAfterSeconds: error.retryAfterSeconds,
      );
    } on Object catch (e) {
      debugPrint('[FaydaOidcService] start failed: $e');
      return const FaydaStartResult.failure(
        AuthErrorCode.faydaServiceUnavailable,
        'Fayda verification is unavailable',
      );
    }
  }

  /// Exchange the authorization code for a registration verification token
  /// (POST /auth/fayda-verify). The server does the token exchange with Fayda.
  Future<FaydaExchangeResult> exchangeCode({
    required String authCode,
    required String state,
  }) async {
    try {
      final response = await _dio.post<dynamic>(
        ApiConfig.faydaVerifyEndpoint,
        data: {
          'authorizationCode': authCode,
          'state': state,
          'redirectUri': redirectUri,
        },
      );

      final data = decodeJsonMap(response.data);
      final token = data?['verificationToken'];
      if (token is! String || token.isEmpty) {
        return const FaydaExchangeResult(
          isSuccess: false,
          errorCode: AuthErrorCode.faydaVerificationFailed,
          errorMessage: 'Fayda verification failed',
        );
      }

      return FaydaExchangeResult(
        isSuccess: true,
        verificationToken: token,
        verifiedName: data?['verifiedName'] as String?,
      );
    } on DioException catch (e) {
      final error = _errorOf(e);
      return FaydaExchangeResult(
        isSuccess: false,
        errorCode: error.code,
        errorMessage: error.message,
      );
    } on Object {
      return const FaydaExchangeResult(
        isSuccess: false,
        errorCode: AuthErrorCode.faydaVerificationFailed,
        errorMessage: 'Fayda verification failed',
      );
    }
  }

  /// Link a Fayda identity to the signed-in account (POST /user/fayda-link).
  Future<FaydaLinkResult> link({
    required String authCode,
    required String state,
  }) async {
    try {
      final response = await _dio.post<dynamic>(
        ApiConfig.userFaydaLinkEndpoint,
        data: {
          'authorizationCode': authCode,
          'state': state,
          'redirectUri': redirectUri,
        },
      );
      final data = decodeJsonMap(response.data);
      if (data?['faydaVerified'] != true) {
        return const FaydaLinkResult(
          isSuccess: false,
          errorCode: AuthErrorCode.faydaVerificationFailed,
          errorMessage: 'Fayda verification failed',
        );
      }
      return FaydaLinkResult(
        isSuccess: true,
        faydaVerified: true,
        faydaVerifiedAt: _parseTime(data?['faydaVerifiedAt']),
        verifiedName: data?['verifiedName'] as String?,
      );
    } on DioException catch (e) {
      final error = _errorOf(e);
      return FaydaLinkResult(
        isSuccess: false,
        errorCode: error.code,
        errorMessage: error.message,
      );
    } on Object {
      return const FaydaLinkResult(
        isSuccess: false,
        errorCode: AuthErrorCode.faydaVerificationFailed,
        errorMessage: 'Fayda verification failed',
      );
    }
  }

  static DateTime? _parseTime(Object? raw) =>
      raw is String ? DateTime.tryParse(raw)?.toUtc() : null;

  /// Map a Dio failure to a Fayda / auth error code. Network trouble and 5xx
  /// without a code count as "service unavailable" (FAYDA_003), which lets
  /// registration continue without Fayda.
  static ({String code, String message, int? retryAfterSeconds}) _errorOf(
    DioException e,
  ) {
    final error = decodeJsonMap(e.response?.data)?['error'];
    if (error is Map) {
      final code = error['code'];
      final retry = error['retryAfterSeconds'];
      if (code is String && code.isNotEmpty) {
        return (
          code: code,
          message: error['message'] as String? ?? 'Fayda verification failed',
          retryAfterSeconds: retry is num ? retry.toInt() : null,
        );
      }
    }
    final status = e.response?.statusCode;
    if (status == 429) {
      return (
        code: AuthErrorCode.rateLimitExceeded,
        message: 'Too many attempts',
        retryAfterSeconds: null,
      );
    }
    if (status == null || status >= 500) {
      return (
        code: AuthErrorCode.faydaServiceUnavailable,
        message: 'Fayda verification is unavailable',
        retryAfterSeconds: null,
      );
    }
    return (
      code: AuthErrorCode.faydaVerificationFailed,
      message: 'Fayda verification failed',
      retryAfterSeconds: null,
    );
  }
}

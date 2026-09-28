import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/auth/fayda_callback_links.dart';
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
///    [redirectUri] (deep link on mobile, `fayda-callback.html` on web, see
///    [faydaCallbackLinks]).
/// 3. [parseCallback] reads the code; the caller checks the state matches.
/// 4. [exchangeCode] (registration) or [link] (existing account) completes it.
///
/// The app no longer builds the authorize URL itself, so it needs no Fayda
/// client id or authorize endpoint at build time.
/// Called before each automatic retry of a Fayda call: [attempt] is the
/// attempt about to run (2 or more) out of [total].
typedef FaydaRetryCallback = void Function(int attempt, int total);

class FaydaOidcService {
  /// Pauses between automatic attempts. Three attempts in all: a flaky 3G link
  /// or a cold Lambda usually recovers within a few seconds.
  static const defaultRetryDelays = [
    Duration(seconds: 1),
    Duration(seconds: 3),
  ];

  /// Deep link Fayda redirects back to on mobile.
  static const deepLinkRedirectUri = 'weredaconnect://fayda-callback';

  /// Where Fayda redirects back to: the deep link, or on web the callback page
  /// (accepted by the server only against the mock provider).
  static String get redirectUri => webFaydaRedirectUri() ?? deepLinkRedirectUri;

  final Dio _dio;

  /// The web callback page, or null off the web. Injectable so tests on the
  /// VM can exercise the web path.
  final String? Function() _webRedirect;

  /// Pauses between attempts; its length + 1 is the number of attempts.
  final List<Duration> _retryDelays;

  FaydaOidcService({
    Dio? dio,
    String? Function()? webRedirect,
    List<Duration> retryDelays = defaultRetryDelays,
  })  : _dio = dio ?? ApiClient().dio,
        _webRedirect = webRedirect ?? webFaydaRedirectUri,
        _retryDelays = retryDelays;

  String get _redirectUri => _webRedirect() ?? deepLinkRedirectUri;

  /// Whether [uri] is our Fayda callback ([webRedirect] is the web callback
  /// page, null off the web).
  static bool isCallbackUri(Uri uri, {String? webRedirect}) {
    if (uri.scheme == 'weredaconnect' && uri.host == 'fayda-callback') {
      return true;
    }
    final web = Uri.tryParse(webRedirect ?? webFaydaRedirectUri() ?? '');
    if (web == null || !web.isScheme('https') && !web.isScheme('http')) {
      return false;
    }
    return uri.scheme == web.scheme &&
        uri.authority == web.authority &&
        uri.path == web.path;
  }

  /// Whether [uri] is a Fayda callback that answers an attempt: it carries a
  /// `state`. A stateless one (e.g. the web callback tab reloaded after it
  /// stripped its query) is not a response and must not end or fail a wait.
  static bool isCallbackResponse(Uri uri, {String? webRedirect}) =>
      isCallbackUri(uri, webRedirect: webRedirect) &&
      (uri.queryParameters['state']?.isNotEmpty ?? false);

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
  Future<FaydaStartResult> startRegistration({
    String? phoneNumber,
    FaydaRetryCallback? onRetry,
  }) {
    return _startWithRetry(
      ApiConfig.faydaStartEndpoint,
      {
        if (phoneNumber != null && phoneNumber.isNotEmpty)
          'phoneNumber': phoneNumber,
      },
      onRetry,
    );
  }

  /// Start Fayda verification for the signed-in user (POST /user/fayda-start).
  Future<FaydaStartResult> startLink({FaydaRetryCallback? onRetry}) {
    return _startWithRetry(ApiConfig.userFaydaStartEndpoint, const {}, onRetry);
  }

  /// A start mints fresh state on every call, so any transient failure
  /// (no connection, 5xx, FAYDA_003) is safe to repeat before giving up.
  Future<FaydaStartResult> _startWithRetry(
    String endpoint,
    Map<String, dynamic> body,
    FaydaRetryCallback? onRetry,
  ) async {
    final total = _retryDelays.length + 1;
    for (var attempt = 1;; attempt++) {
      final result = await _start(endpoint, body);
      if (result.isSuccess ||
          result.errorCode != AuthErrorCode.faydaServiceUnavailable ||
          attempt == total) {
        return result;
      }
      onRetry?.call(attempt + 1, total);
      await Future<void>.delayed(_retryDelays[attempt - 1]);
    }
  }

  /// The redirect is sent with every start so the server stores the one the
  /// verify / link call must repeat.

  Future<FaydaStartResult> _start(
    String endpoint,
    Map<String, dynamic> body,
  ) async {
    try {
      final response = await _dio.post<dynamic>(
        endpoint,
        data: {...body, 'redirectUri': _redirectUri},
      );
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
      // The web callback is refused (AUTH_007, field redirectUri) when the
      // real provider is in use, since it registers the deep link only.
      // Retrying cannot help: the user has to use the mobile app.
      if (_webRedirect() != null && _isRedirectRefused(e)) {
        return const FaydaStartResult.failure(
          AuthErrorCode.faydaUnavailableOnWeb,
          'Fayda verification is not available on the web',
        );
      }
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
  ///
  /// The authorization code and state are single use, so only a request that
  /// never reached the server (no connection) is retried.
  Future<FaydaExchangeResult> exchangeCode({
    required String authCode,
    required String state,
    FaydaRetryCallback? onRetry,
  }) async {
    try {
      final response = await _postUnsentWithRetry(
        ApiConfig.faydaVerifyEndpoint,
        {
          'authorizationCode': authCode,
          'state': state,
          'redirectUri': _redirectUri,
        },
        onRetry,
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
          'redirectUri': _redirectUri,
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

  /// POST, repeating only failures where the request was never sent.
  Future<Response<dynamic>> _postUnsentWithRetry(
    String endpoint,
    Map<String, dynamic> data,
    FaydaRetryCallback? onRetry,
  ) async {
    final total = _retryDelays.length + 1;
    for (var attempt = 1;; attempt++) {
      try {
        return await _dio.post<dynamic>(endpoint, data: data);
      } on DioException catch (e) {
        final neverSent = e.type == DioExceptionType.connectionError ||
            e.type == DioExceptionType.connectionTimeout;
        if (!neverSent || attempt == total) rethrow;
        onRetry?.call(attempt + 1, total);
        await Future<void>.delayed(_retryDelays[attempt - 1]);
      }
    }
  }

  static DateTime? _parseTime(Object? raw) =>
      raw is String ? DateTime.tryParse(raw)?.toUtc() : null;

  /// Whether the server refused our redirectUri (400 AUTH_007, field redirectUri).
  static bool _isRedirectRefused(DioException e) {
    final error = decodeJsonMap(e.response?.data)?['error'];
    return error is Map &&
        error['code'] == AuthErrorCode.internalServerError &&
        error['field'] == 'redirectUri';
  }

  /// Map a Dio failure to a Fayda / auth error code. Network trouble and 5xx
  /// without a code count as "service unavailable" (FAYDA_003), which the
  /// start call retries before giving up.
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

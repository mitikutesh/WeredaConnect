import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

DioException _dio(DioExceptionType type, {Object? error}) => DioException(
      requestOptions: RequestOptions(path: '/auth/login'),
      type: type,
      error: error,
    );

void main() {
  group('isSecureConnectionFailure (SIM-23)', () {
    test('a rejected certificate pin is a secure-connection failure', () {
      // The pinned HttpClient throws a dart:io HandshakeException, which dio
      // wraps as `unknown`; a validator rejection is `badCertificate`.
      expect(
        isSecureConnectionFailure(
          _dio(
            DioExceptionType.unknown,
            error: const HandshakeException('CERTIFICATE_VERIFY_FAILED'),
          ),
        ),
        isTrue,
      );
      expect(
        isSecureConnectionFailure(_dio(DioExceptionType.badCertificate)),
        isTrue,
      );
      expect(
        isSecureConnectionFailure(
          _dio(
            DioExceptionType.connectionError,
            error: const TlsException('handshake error'),
          ),
        ),
        isTrue,
      );
    });

    test('a plain connectivity failure is not', () {
      expect(
        isSecureConnectionFailure(
          _dio(
            DioExceptionType.connectionError,
            error: const SocketException('Network is unreachable'),
          ),
        ),
        isFalse,
      );
      expect(
        isSecureConnectionFailure(_dio(DioExceptionType.connectionTimeout)),
        isFalse,
      );
      expect(isSecureConnectionFailure(Exception('boom')), isFalse);
    });
  });

  group('mapErrorToCode', () {
    test('403 is forbidden, 401 is auth, TLS is secureConnection', () {
      expect(
        mapErrorToCode(const AuthException('Forbidden', 403)),
        AppErrorCode.forbidden,
      );
      expect(
        mapErrorToCode(const AuthException('Unauthorized')),
        AppErrorCode.auth,
      );
      expect(
        mapErrorToCode(const SecureConnectionException()),
        AppErrorCode.secureConnection,
      );
      expect(mapErrorToCode(const NetworkException()), AppErrorCode.network);
    });
  });

  group('localizeLoadError', () {
    test('only connectivity failures read as waiting for network', () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      expect(
        localizeLoadError(AppErrorCode.network, l10n),
        l10n.dataWillSyncWhenOnline,
      );
      expect(
        localizeLoadError(AppErrorCode.timeout, l10n),
        l10n.dataWillSyncWhenOnline,
      );
      expect(
        localizeLoadError(AppErrorCode.forbidden, l10n),
        l10n.errorNoPermission,
      );
      expect(
        localizeLoadError(AppErrorCode.secureConnection, l10n),
        l10n.errorSecureConnectionFailed,
      );
      expect(localizeLoadError(AppErrorCode.server, l10n), l10n.errorOccurred);
    });

    test('the new messages exist in all three languages', () async {
      for (final code in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(code));
        expect(l10n.errorNoPermission, isNotEmpty, reason: code);
        expect(l10n.errorSecureConnectionFailed, isNotEmpty, reason: code);
        expect(
          l10n.errorNoPermission,
          isNot(l10n.dataWillSyncWhenOnline),
          reason: code,
        );
      }
    });
  });
}

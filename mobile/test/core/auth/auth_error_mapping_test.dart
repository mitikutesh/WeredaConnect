import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

void main() {
  group('SECURE_CONNECTION_FAILED (SIM-23)', () {
    test('is its own code, recoverable, and never the offline-login copy', () {
      const code = AuthErrorCode.secureConnectionFailed;
      expect(code, isNot(AuthErrorCode.networkError));
      expect(code.isRecoverable, isTrue);
      expect(code.userMessage, contains('secure connection'));
      expect(code.userMessage, isNot(contains('offline')));
    });

    test('localizes to the secure-connection message in every language',
        () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final message =
            localizeAuthError(l10n, AuthErrorCode.secureConnectionFailed);
        expect(message, l10n.errorSecureConnectionFailed, reason: locale);
        expect(message, isNot(l10n.authOfflineNotAvailable), reason: locale);
        expect(message, isNot(l10n.authNetworkError), reason: locale);
      }
    });
  });

  group('AUTH_027 phone number in profile text (FUP-52)', () {
    test('is the server code and has a fallback text', () {
      expect(AuthErrorCode.profileContactInText, 'AUTH_027');
      expect(
        AuthErrorCode.profileContactInText.userMessage,
        contains('phone number'),
      );
    });

    test('localizes to the profile phone message in every language', () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final message =
            localizeAuthError(l10n, AuthErrorCode.profileContactInText);
        expect(message, l10n.authProfileContactInText, reason: locale);
        expect(message, isNot(l10n.genericError), reason: locale);
      }
    });
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

void main() {
  group('AUTH_026, refresh token of another user on logout (SIM-55)', () {
    test('is the code the server sends, with its own fallback text', () {
      const code = AuthErrorCode.refreshTokenNotOwned;
      expect(code, 'AUTH_026');
      expect(code.userMessage, contains('another account'));
      expect(code.userMessage, isNot('An error occurred. Please try again'));
    });

    test('localizes to the session text in every language, not the generic one',
        () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final message =
            localizeAuthError(l10n, AuthErrorCode.refreshTokenNotOwned);
        expect(message, l10n.authErrorTokenExpired, reason: locale);
        expect(message, isNot(l10n.genericError), reason: locale);
      }
    });
  });
}

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ESC-31 (FUP-165): the day a reputation restriction ends, read from the
/// `REPUTATION_RESTRICTED: until=...:` refusal (reputation-api.md 8.1) and
/// shown in the localized error.
void main() {
  const dated = 'REPUTATION_RESTRICTED: until=2026-11-21T09:00:00.000Z: '
      "Your account can't take new orders until 21 Nov 2026. Your current "
      'orders continue.';

  group('reputationRestrictedUntil', () {
    test('reads the date', () {
      expect(reputationRestrictedUntil(dated), DateTime.utc(2026, 11, 21, 9));
    });

    test('reads it from an exception text around the message', () {
      expect(
        reputationRestrictedUntil(
            const SyncRejectedException(dated).toString()),
        DateTime.utc(2026, 11, 21, 9),
      );
    });

    test('no date while an admin review is open', () {
      expect(
        reputationRestrictedUntil(
          "REPUTATION_RESTRICTED: until=review: Your account can't take new "
          'orders for now.',
        ),
        isNull,
      );
    });

    test('no date on the counterparty form, other codes and old wording', () {
      for (final message in <String?>[
        null,
        '',
        'REPUTATION_RESTRICTED: counterparty: This farmer or trucker '
            "can't take new orders right now.",
        'REPUTATION_RESTRICTED: restricted',
        "SYNC_VALIDATION: Your account can't take new orders until 21 Nov "
            '2026. Your current orders continue.',
        'ORDER_VALIDATION: until=2026-11-21T09:00:00.000Z: x',
        'REPUTATION_RESTRICTED: until=not-a-date: x',
      ]) {
        expect(reputationRestrictedUntil(message), isNull, reason: '$message');
      }
    });
  });

  group('localizeErrorCode', () {
    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      test('${locale.languageCode}: the restricted text names the day', () {
        final l10n = lookupAppLocalizations(locale);
        final text = localizeErrorCode(
          AppErrorCode.reputationRestricted,
          l10n,
          message: dated,
        );
        expect(text, isNot(l10n.errorReputationRestricted));
        expect(text, contains('2026'));
        expect(text, isNot(contains('{date}')));
        // The raw server text is never shown.
        expect(text, isNot(contains('REPUTATION_RESTRICTED')));
        expect(text, isNot(contains('until=')));
      });
    }

    test('the day is the Ethiopia calendar day', () {
      final l10n = lookupAppLocalizations(const Locale('en'));
      expect(
        localizeErrorCode(
          AppErrorCode.reputationRestricted,
          l10n,
          // 22:00 UTC on the 20th is the 21st in Ethiopia (UTC+3).
          message: 'REPUTATION_RESTRICTED: until=2026-11-20T22:00:00.000Z: x',
        ),
        l10n.errorReputationRestrictedUntil('Nov 21, 2026'),
      );
    });

    test('without a date the text is the one from before', () {
      final l10n = lookupAppLocalizations(const Locale('en'));
      expect(
        localizeErrorCode(AppErrorCode.reputationRestricted, l10n),
        l10n.errorReputationRestricted,
      );
      expect(
        localizeErrorCode(
          AppErrorCode.reputationRestricted,
          l10n,
          message: 'REPUTATION_RESTRICTED: until=review: x',
        ),
        l10n.errorReputationRestricted,
      );
    });

    test('a date never changes the text of another code', () {
      final l10n = lookupAppLocalizations(const Locale('en'));
      expect(
        localizeErrorCode(
          AppErrorCode.counterpartyRestricted,
          l10n,
          message: dated,
        ),
        l10n.errorCounterpartyRestricted,
      );
    });
  });
}

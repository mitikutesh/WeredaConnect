import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ACC-03: the server's "Deleted user" sentinel is shown in the user's
/// language; real names are shown as they are.
void main() {
  for (final code in ['en', 'am', 'om']) {
    final l10n = lookupAppLocalizations(Locale(code));

    test('$code: the exact sentinel becomes the localized deletedUser', () {
      expect(partyDisplayName(deletedUserSentinel, l10n), l10n.deletedUser);
      expect(l10n.deletedUser, isNotEmpty);
    });
  }

  final en = lookupAppLocalizations(const Locale('en'));

  test('only an exact match is the sentinel', () {
    expect(partyDisplayName('Abebe Kebede', en), 'Abebe Kebede');
    expect(partyDisplayName('deleted user', en), 'deleted user');
    expect(partyDisplayName(' Deleted user', en), ' Deleted user');
  });

  test('a missing name is empty', () {
    expect(partyDisplayName(null, en), '');
    expect(partyDisplayName('', en), '');
  });

  test('am and om do not show the English sentinel', () {
    for (final code in ['am', 'om']) {
      final l10n = lookupAppLocalizations(Locale(code));
      expect(l10n.deletedUser, isNot(deletedUserSentinel), reason: code);
    }
  });
}

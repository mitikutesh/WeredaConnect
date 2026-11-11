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

  test('a user id where the name belongs is empty, like a missing name', () {
    expect(partyDisplayName('3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b', en), '');
    expect(partyDisplayName(' 3F2B8C1E9A4D4E6F8B7A1C2D3E4F5A6B ', en), '');
    // Names that hold digits or hyphens are still names.
    expect(partyDisplayName('Abebe-Kebede 2', en), 'Abebe-Kebede 2');
    expect(partyDisplayName('farmer-1', en), 'farmer-1');
  });

  // FUP-130: a chat title or inbox row has no role to fall back on, so a
  // missing name (or an id stored as one) reads as a localized noun.
  for (final code in ['en', 'am', 'om']) {
    final l10n = lookupAppLocalizations(Locale(code));

    test('$code: no real name reads as the localized "User", never the id', () {
      const id = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b';
      expect(l10n.unnamedUser, isNotEmpty);
      expect(partyNameOrUnnamed(null, l10n), l10n.unnamedUser);
      expect(partyNameOrUnnamed('  ', l10n), l10n.unnamedUser);
      expect(partyNameOrUnnamed(id, l10n), l10n.unnamedUser);
      expect(partyNameOrUnnamed(' Abebe Kebede ', l10n), 'Abebe Kebede');
      expect(
        partyNameOrUnnamed(deletedUserSentinel, l10n),
        l10n.deletedUser,
      );
    });
  }

  test('the unnamed fallback is a noun of its own, not the Contact verb', () {
    for (final code in ['en', 'am', 'om']) {
      final l10n = lookupAppLocalizations(Locale(code));
      expect(l10n.unnamedUser, isNot(l10n.contact), reason: code);
    }
  });

  test('a name to store is trimmed, and empty for a missing name or an id', () {
    expect(partyNameToStore(' Abebe Kebede '), 'Abebe Kebede');
    expect(partyNameToStore(null), '');
    expect(partyNameToStore('   '), '');
    expect(partyNameToStore('3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b'), '');
    expect(partyNameToStore('3F2B8C1E9A4D4E6F8B7A1C2D3E4F5A6B'), '');
    expect(partyNameToStore(deletedUserSentinel), deletedUserSentinel);
  });

  test('am and om do not show the English sentinel', () {
    for (final code in ['am', 'om']) {
      final l10n = lookupAppLocalizations(Locale(code));
      expect(l10n.deletedUser, isNot(deletedUserSentinel), reason: code);
    }
  });
}

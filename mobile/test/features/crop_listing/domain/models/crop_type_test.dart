import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

void main() {
  group('CropType.tryFromKey', () {
    test('parses stored keys, tolerating case and whitespace', () {
      expect(CropType.tryFromKey('teff'), CropType.teff);
      expect(CropType.tryFromKey(' Coffee '), CropType.coffee);
    });

    test('returns null for an unknown key instead of defaulting to teff', () {
      expect(CropType.tryFromKey('quinoa'), isNull);
      expect(CropType.tryFromKey(''), isNull);
      // fromKey keeps its lenient default for callers that need an icon.
      expect(CropType.fromKey('quinoa'), CropType.teff);
    });
  });

  group('CropType.labelFor (SIM-22: no raw English on am/om cards)', () {
    test('English', () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      expect(CropType.labelFor('teff', l10n), 'Teff');
      expect(CropType.labelFor('maize', l10n), 'Maize');
    });

    test('Amharic and Afan Oromo use the translated names', () async {
      final am = await AppLocalizations.delegate.load(const Locale('am'));
      final om = await AppLocalizations.delegate.load(const Locale('om'));
      expect(CropType.labelFor('teff', am), am.cropTypeTeff);
      expect(CropType.labelFor('teff', om), om.cropTypeTeff);
      expect(CropType.labelFor('teff', am), isNot('teff'));
      expect(CropType.labelFor('teff', am), isNot('Teff'));
      expect(CropType.labelFor('teff', om), isNot('teff'));
      for (final type in CropType.values) {
        expect(
          CropType.labelFor(type.name, am),
          type.displayName(am),
          reason: type.name,
        );
      }
    });

    test('an unknown key is shown as-is, never mistranslated', () async {
      final am = await AppLocalizations.delegate.load(const Locale('am'));
      expect(CropType.labelFor('quinoa', am), 'quinoa');
    });
  });
}

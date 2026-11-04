import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// Material and Cupertino strings (Back, OK, the date picker) in the app
/// language: Flutter ships Amharic, so Amharic users get it; Afaan Oromoo
/// has no Flutter translation and stays on English.
void main() {
  const material = FallbackMaterialLocalizationsDelegate();
  const cupertino = FallbackCupertinoLocalizationsDelegate();

  tearDown(() => Intl.defaultLocale = null);

  test('every app language is supported', () {
    for (final code in ['en', 'am', 'om']) {
      expect(material.isSupported(Locale(code)), isTrue, reason: code);
      expect(cupertino.isSupported(Locale(code)), isTrue, reason: code);
    }
  });

  test('Amharic loads Flutter\'s Amharic strings, not English', () async {
    final en = await material.load(const Locale('en'));
    final am = await material.load(const Locale('am'));

    expect(am.backButtonTooltip, isNot(en.backButtonTooltip));
    expect(am.okButtonLabel, isNot(en.okButtonLabel));
    expect(am.cancelButtonLabel, isNot(en.cancelButtonLabel));
    expect(
      RegExp(r'[ሀ-፿]').hasMatch(am.backButtonTooltip),
      isTrue,
      reason: 'Ethiopic script',
    );

    final cupertinoEn = await cupertino.load(const Locale('en'));
    final cupertinoAm = await cupertino.load(const Locale('am'));
    expect(cupertinoAm.copyButtonLabel, isNot(cupertinoEn.copyButtonLabel));
  });

  test('Afaan Oromoo falls back to English', () async {
    final en = await material.load(const Locale('en'));
    final om = await material.load(const Locale('om'));
    expect(om.backButtonTooltip, en.backButtonTooltip);
    expect(om.okButtonLabel, en.okButtonLabel);

    final cupertinoEn = await cupertino.load(const Locale('en'));
    final cupertinoOm = await cupertino.load(const Locale('om'));
    expect(cupertinoOm.copyButtonLabel, cupertinoEn.copyButtonLabel);
  });

  test('loading a language sets the date locale', () async {
    await material.load(const Locale('am'));
    expect(Intl.defaultLocale, 'am');

    await material.load(const Locale('om'));
    expect(Intl.defaultLocale, 'en');

    await material.load(const Locale('en'));
    expect(Intl.defaultLocale, 'en');
  });
}

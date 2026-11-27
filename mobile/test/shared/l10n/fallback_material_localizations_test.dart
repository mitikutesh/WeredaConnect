import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// Material and Cupertino strings (Back, OK, the date picker) in the app
/// language: Flutter ships Amharic, so Amharic users get it; Afaan Oromoo
/// has no Flutter translation, the app brings its own.
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

  // BUG-L4, BUG-25: the date picker and the Back button were English.
  test('Afaan Oromoo has its own picker labels and date names', () async {
    final en = await material.load(const Locale('en'));
    final om = await material.load(const Locale('om'));
    expect(om.backButtonTooltip, "Deebi'i");
    expect(om.okButtonLabel, 'Tole');
    expect(om.cancelButtonLabel, 'Dhiisi');
    expect(om.datePickerHelpText, 'Guyyaa filadhu');

    // A Thursday in November.
    final day = DateTime.utc(2026, 11, 19);
    expect(om.formatMonthYear(day), 'Sadaasa 2026');
    expect(om.formatMediumDate(day), 'Kam, Sad 19');
    expect(om.formatFullDate(day), 'Kamisa, Sadaasa 19, 2026');
    expect(om.formatShortDate(day), 'Sad 19, 2026');
    expect(om.narrowWeekdays, ['D', 'W', 'K', 'R', 'K', 'J', 'S']);
    // The week starts on Sunday, as in the English and Amharic pickers.
    expect(om.firstDayOfWeekIndex, 0);
    // The typed date keeps the English shape and parses back.
    expect(om.formatCompactDate(day), '11/19/2026');
    expect(om.parseCompactDate('11/19/2026'), DateTime(2026, 11, 19));
    // Labels with no Afaan Oromoo text yet stay English.
    expect(om.licensesPageTitle, en.licensesPageTitle);

    final cupertinoEn = await cupertino.load(const Locale('en'));
    final cupertinoOm = await cupertino.load(const Locale('om'));
    expect(cupertinoOm.copyButtonLabel, cupertinoEn.copyButtonLabel);
  });

  test('loading a language sets the date locale', () async {
    await material.load(const Locale('am'));
    expect(Intl.defaultLocale, 'am');

    await material.load(const Locale('om'));
    expect(Intl.defaultLocale, 'om');

    await material.load(const Locale('en'));
    expect(Intl.defaultLocale, 'en');
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';

/// Dates follow the app language: Amharic month and day names in Amharic,
/// English in English and Afaan Oromoo (intl has no Oromo date names).
void main() {
  setUpAll(() async {
    // What MaterialApp does at start: Flutter's localizations bring intl's
    // date data for every language they ship, Amharic included.
    await GlobalMaterialLocalizations.delegate.load(const Locale('en'));
  });

  tearDown(() => Intl.defaultLocale = null);

  test('the date locale for each app language', () {
    expect(dateLocaleFor('en'), 'en');
    expect(dateLocaleFor('am'), 'am');
    expect(dateLocaleFor('om'), 'en');
  });

  test('a formatter built without a locale follows the app language', () {
    final day = DateTime.utc(2026, 10, 29);

    applyDateLocale(const Locale('am'));
    final amharic = DateFormat.yMMMd().format(day);
    expect(amharic, isNot('Oct 29, 2026'));
    expect(RegExp(r'[ሀ-፿]').hasMatch(amharic), isTrue);

    applyDateLocale(const Locale('om'));
    expect(DateFormat.yMMMd().format(day), 'Oct 29, 2026');

    applyDateLocale(const Locale('en'));
    expect(DateFormat.yMMMd().format(day), 'Oct 29, 2026');
  });

  test('numbers keep their digits and separators in Amharic', () {
    applyDateLocale(const Locale('am'));
    expect(NumberFormat('#,##0').format(1234567), '1,234,567');
    expect(NumberFormat('#,##0.##').format(1234.5), '1,234.5');
    expect(formatEtbAmount(4500), '4,500');
  });

  test('the server date parser does not follow the app language', () {
    applyDateLocale(const Locale('am'));
    // api_client.dart reads the server's Date header with an en_US
    // formatter: the global default must not reach it.
    expect(
      DateFormat('dd MMM yyyy HH:mm:ss', 'en_US')
          .parseUtc('04 Nov 2026 10:00:00'),
      DateTime.utc(2026, 11, 4, 10),
    );
  });
}

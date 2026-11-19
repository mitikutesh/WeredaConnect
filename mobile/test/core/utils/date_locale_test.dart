import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';

/// Dates follow the app language: Amharic month and day names in Amharic,
/// Afaan Oromoo ones in Afaan Oromoo (BUG-L2: intl ships none, the app adds
/// them), English in English.
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
    // Afaan Oromoo is English until its names are registered.
    expect(dateLocaleFor('om'), 'en');
    registerOromoDateData();
    expect(dateLocaleFor('om'), 'om');
    expect(dateLocaleFor('fr'), 'en');
  });

  test('a formatter built without a locale follows the app language', () {
    final day = DateTime.utc(2026, 10, 29);

    applyDateLocale(const Locale('am'));
    final amharic = DateFormat.yMMMd().format(day);
    expect(amharic, isNot('Oct 29, 2026'));
    expect(RegExp(r'[ሀ-፿]').hasMatch(amharic), isTrue);

    applyDateLocale(const Locale('om'));
    expect(DateFormat.yMMMd().format(day), 'Onk 29, 2026');

    applyDateLocale(const Locale('en'));
    expect(DateFormat.yMMMd().format(day), 'Oct 29, 2026');
  });

  // BUG-L2: the three shapes the screens use (payout date and order card:
  // yMMMd; trip departure: MMMd; the boards: "EEE d MMM").
  test('Afaan Oromoo month and weekday names, short and full', () {
    applyDateLocale(const Locale('om'));
    // A Thursday.
    final day = DateTime.utc(2026, 11, 19);
    expect(DateFormat.yMMMd().format(day), 'Sad 19, 2026');
    expect(DateFormat.MMMd().format(day), 'Sad 19');
    expect(DateFormat('EEE d MMM').format(day), 'Kam 19 Sad');
    expect(DateFormat.yMMMMEEEEd().format(day), 'Kamisa, Sadaasa 19, 2026');
    expect(DateFormat.yMMMM().format(DateTime.utc(2026)), 'Amajjii 2026');
    // The CLDR `om` spellings.
    expect(DateFormat.MMMM().format(DateTime.utc(2026, 4)), 'Eebila');
    expect(DateFormat.MMM().format(DateTime.utc(2026, 4)), 'Elb');
    expect(DateFormat.MMMM().format(DateTime.utc(2026, 5)), 'Caamsaa');
    expect(DateFormat.MMM().format(DateTime.utc(2026, 5)), 'Cam');
    expect(DateFormat.jm().format(DateTime.utc(2026, 1, 1, 15)), '3:00 WB');
    // Every month and weekday has a name of its own.
    final months = {
      for (var m = 1; m <= 12; m++)
        DateFormat.MMMM().format(DateTime.utc(2026, m)),
    };
    final weekdays = {
      for (var d = 1; d <= 7; d++)
        DateFormat.EEEE().format(DateTime.utc(2026, 11, d)),
    };
    expect(months, hasLength(12));
    expect(weekdays, hasLength(7));
    expect(DateFormat.EEEE().format(DateTime.utc(2026, 11, 22)), 'Dilbata');
  });

  test('numbers keep their digits and separators in Afaan Oromoo', () {
    applyDateLocale(const Locale('om'));
    expect(Intl.defaultLocale, 'om');
    // A formatter built without a locale must not throw on the new default.
    expect(NumberFormat('#,##0').format(1234567), '1,234,567');
    expect(NumberFormat('#,##0.##').format(1234.5), '1,234.5');
    expect(NumberFormat.compact().format(1200), '1.2K');
    expect(formatEtbAmount(4500), '4,500');
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

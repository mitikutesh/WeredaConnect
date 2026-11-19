import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';

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

  // FUP-545: Amharic and Afaan Oromoo show the Ethiopian date first, the
  // Gregorian month and day in brackets; English is unchanged.
  group('the Ethiopian date next to the Gregorian one', () {
    // A Thursday: Hidar 10, 2019.
    final today = DateTime.utc(2026, 11, 19);

    test('English stays Gregorian', () {
      applyDateLocale(const Locale('en'));
      expect(formatDate(today), 'Nov 19, 2026');
      expect(formatMonthDay(today), 'Nov 19');
      expect(formatDate(today), DateFormat.yMMMd().format(today));
      expect(formatMonthDay(today), DateFormat.MMMd().format(today));
    });

    test('before a language is applied it is English', () {
      // A bare unit test, or the first frame before the localizations load.
      expect(formatDate(today), 'Nov 19, 2026');
    });

    test('Amharic', () {
      applyDateLocale(const Locale('am'));
      expect(formatDate(today), 'ኅዳር 10፣ 2019 (ኖቬም 19)');
      expect(formatMonthDay(today), 'ኅዳር 10 (ኖቬም 19)');
    });

    test('Afaan Oromoo', () {
      applyDateLocale(const Locale('om'));
      expect(formatDate(today), 'Sadaasa 10, 2019 (Sad 19)');
      expect(formatMonthDay(today), 'Sadaasa 10 (Sad 19)');
    });

    test('Pagume, New Year and the Gregorian New Year', () {
      final pagume = DateTime.utc(2026, 9, 8);
      final pagume6 = DateTime.utc(2027, 9, 11);
      final newYear = DateTime.utc(2026, 9, 11);
      final january = DateTime.utc(2027);

      applyDateLocale(const Locale('am'));
      expect(formatDate(pagume), 'ጳጉሜን 3፣ 2018 (ሴፕቴ 8)');
      expect(formatDate(pagume6), 'ጳጉሜን 6፣ 2019 (ሴፕቴ 11)');
      expect(formatDate(newYear), 'መስከረም 1፣ 2019 (ሴፕቴ 11)');
      expect(formatDate(january), 'ታኅሣሥ 23፣ 2019 (ጃንዩ 1)');

      applyDateLocale(const Locale('om'));
      expect(formatDate(pagume), 'Qaammee 3, 2018 (Ful 8)');
      expect(formatDate(pagume6), 'Qaammee 6, 2019 (Ful 11)');
      expect(formatDate(newYear), 'Fulbaana 1, 2019 (Ful 11)');
      expect(formatDate(january), 'Mudde 23, 2019 (Ama 1)');

      applyDateLocale(const Locale('en'));
      expect(formatDate(pagume), 'Sep 8, 2026');
      expect(formatDate(newYear), 'Sep 11, 2026');
    });

    test('a late deadline shows the day it is in Ethiopia', () {
      // 23:00 UTC on November 19 is 02:00 on November 20 in Ethiopia.
      final deadline = DateTime.utc(2026, 11, 19, 23);
      applyDateLocale(const Locale('am'));
      expect(formatMonthDay(dayInEthiopia(deadline)), 'ኅዳር 11 (ኖቬም 20)');
      applyDateLocale(const Locale('om'));
      expect(formatMonthDay(dayInEthiopia(deadline)), 'Sadaasa 11 (Sad 20)');
    });

    test('with the weekday, in the boards\' layout', () {
      applyDateLocale(const Locale('am'));
      expect(
        formatDualDate(
          today,
          DateFormat('EEE d MMM'),
          year: false,
          weekday: true,
        ),
        'ሐሙስ ኅዳር 10 (ኖቬም 19)',
      );
      applyDateLocale(const Locale('en'));
      expect(
        formatDualDate(
          today,
          DateFormat('EEE d MMM'),
          year: false,
          weekday: true,
        ),
        'Thu 19 Nov',
      );
    });

    test('the formatter\'s own locale decides, not only the app language', () {
      applyDateLocale(const Locale('am'));
      // A formatter pinned to English stays Gregorian.
      expect(
        formatDualDate(today, DateFormat.yMMMd('en'), year: true),
        'Nov 19, 2026',
      );
    });

    test('13 month names in each language, each its own', () {
      for (final language in ['am', 'om']) {
        final names = ethiopianMonthNames[language]!;
        expect(names, hasLength(13), reason: language);
        expect(names.toSet(), hasLength(13), reason: language);
      }
      expect(ethiopianMonthNames['am']!.first, 'መስከረም');
      expect(ethiopianMonthNames['am']!.last, 'ጳጉሜን');
      expect(ethiopianMonthNames['om']!.first, 'Fulbaana');
      expect(ethiopianMonthNames['om']!.last, 'Qaammee');
      expect(ethiopianMonthNames.containsKey('en'), isFalse);
    });
  });
}

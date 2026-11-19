import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/ethiopian_calendar.dart';

/// FUP-545: the Ethiopian calendar converter behind the dual dates on the
/// Amharic and Afaan Oromoo screens.
void main() {
  EthiopianDate eth(int y, int m, int d) => EthiopianDate(y, m, d);
  EthiopianDate fromG(int y, int m, int d) =>
      EthiopianDate.fromGregorian(y, m, d);
  DateTime g(int y, int m, int d) => DateTime.utc(y, m, d);

  group('known dates', () {
    // Gregorian date, Ethiopian date, what it is.
    final anchors = <(DateTime, EthiopianDate, String)>[
      // Dates the app shows around the 2026-11-19 persona simulation.
      (g(2026, 11, 19), eth(2019, 3, 10), 'Hidar 10, 2019'),
      (g(2026, 10, 23), eth(2019, 2, 13), 'Tikimt 13, 2019'),
      (g(2026, 10, 29), eth(2019, 2, 19), 'Tikimt 19, 2019'),
      // Ethiopian New Year, September 11 most years.
      (g(2024, 9, 11), eth(2017, 1, 1), 'New Year 2017'),
      (g(2025, 9, 11), eth(2018, 1, 1), 'New Year 2018'),
      (g(2026, 9, 11), eth(2019, 1, 1), 'New Year 2019'),
      // ... September 12 in the Gregorian year before a Gregorian leap
      // year, after Pagume 6 of an Ethiopian leap year.
      (g(2023, 9, 11), eth(2015, 13, 6), 'Pagume 6, 2015'),
      (g(2023, 9, 12), eth(2016, 1, 1), 'New Year 2016'),
      (g(2027, 9, 11), eth(2019, 13, 6), 'Pagume 6, 2019'),
      (g(2027, 9, 12), eth(2020, 1, 1), 'New Year 2020'),
      (g(1999, 9, 12), eth(1992, 1, 1), 'New Year 1992'),
      // Pagume 5 of a common year is the day before New Year.
      (g(2026, 9, 10), eth(2018, 13, 5), 'Pagume 5, 2018'),
      (g(2026, 9, 6), eth(2018, 13, 1), 'Pagume 1, 2018'),
      (g(2026, 9, 5), eth(2018, 12, 30), 'Nehase 30, 2018'),
      // The Ethiopian millennium.
      (g(2007, 9, 12), eth(2000, 1, 1), 'Meskerem 1, 2000'),
      (g(2007, 9, 11), eth(1999, 13, 6), 'Pagume 6, 1999'),
      // Gregorian leap days.
      (g(2024, 2, 29), eth(2016, 6, 21), 'Yekatit 21, 2016'),
      (g(2024, 3, 1), eth(2016, 6, 22), 'Yekatit 22, 2016'),
      (g(2028, 2, 29), eth(2020, 6, 21), 'Yekatit 21, 2020'),
      (g(2000, 2, 29), eth(1992, 6, 21), 'Yekatit 21, 1992'),
      (g(2025, 2, 28), eth(2017, 6, 21), 'Yekatit 21, 2017'),
      (g(2025, 3, 1), eth(2017, 6, 22), 'Yekatit 22, 2017'),
      // Gregorian year boundaries, inside an Ethiopian year.
      (g(2026, 12, 31), eth(2019, 4, 22), 'Tahsas 22, 2019'),
      (g(2027, 1, 1), eth(2019, 4, 23), 'Tahsas 23, 2019'),
      (g(2000, 1, 1), eth(1992, 4, 22), 'Tahsas 22, 1992'),
      // Christmas (Genna) on January 7: Tahsas 29, Tahsas 28 in a year
      // that started on September 12.
      (g(2025, 1, 7), eth(2017, 4, 29), 'Genna 2017'),
      (g(2024, 1, 7), eth(2016, 4, 28), 'Genna 2016'),
      // Epiphany (Timket), Tir 11.
      (g(2025, 1, 19), eth(2017, 5, 11), 'Timket 2017'),
      (g(2024, 1, 20), eth(2016, 5, 11), 'Timket 2016'),
      // The battle of Adwa, before 1900 (a Gregorian common year the
      // Ethiopian calendar does not skip, so the offset was a day less).
      (g(1896, 3, 1), eth(1888, 6, 23), 'Adwa'),
      // The first day of the era (August 29, 8 AD on the Julian calendar).
      (g(8, 8, 27), eth(1, 1, 1), 'Meskerem 1, year 1'),
    ];

    for (final (gregorian, ethiopian, name) in anchors) {
      test('$name is ${gregorian.toIso8601String().substring(0, 10)}', () {
        expect(EthiopianDate.fromDateTime(gregorian), ethiopian);
        expect(ethiopian.toGregorian(), gregorian);
      });
    }

    test('Meskerem 1, year 1 is the epoch, Julian Day 1724221', () {
      expect(eth(1, 1, 1).julianDayNumber, EthiopianDate.epoch);
      expect(EthiopianDate.epoch, 1724221);
      // Hidar 10, 2019 and November 19, 2026 share a Julian Day.
      expect(eth(2019, 3, 10).julianDayNumber, 2461364);
    });

    test('New Year is September 11, or 12 before a year divisible by 4', () {
      // From 1900 to 2099. That is the year before a Gregorian leap year,
      // except in 2099: 2100 is no Gregorian leap year, but Pagume 6 of
      // 2091 still comes first.
      for (var g = 1900; g <= 2099; g++) {
        // The Ethiopian year starting in September of Gregorian year g.
        final newYear = eth(g - 7, 1, 1).toGregorian();
        expect(
          newYear,
          DateTime.utc(g, 9, (g + 1) % 4 == 0 ? 12 : 11),
          reason: 'Ethiopian year ${g - 7}',
        );
      }
      expect(eth(2092, 1, 1).toGregorian(), DateTime.utc(2099, 9, 12));
      // After February 2100 the offset grows by a day.
      expect(eth(2093, 1, 1).toGregorian(), DateTime.utc(2100, 9, 12));
      expect(eth(1893, 1, 1).toGregorian(), DateTime.utc(1900, 9, 11));
      expect(eth(1892, 1, 1).toGregorian(), DateTime.utc(1899, 9, 11));
    });

    test('the year is 7 behind from Meskerem, 8 behind from January', () {
      expect(fromG(2026, 9, 11).year, 2026 - 7);
      expect(fromG(2026, 12, 31).year, 2026 - 7);
      expect(fromG(2027, 1, 1).year, 2027 - 8);
      expect(fromG(2027, 9, 10).year, 2027 - 8);
    });
  });

  group('the time and timezone of a DateTime are ignored', () {
    test('its own year, month and day are the date', () {
      final late = EthiopianDate.fromDateTime(
        DateTime.utc(2026, 11, 19, 23, 59, 59),
      );
      final local = EthiopianDate.fromDateTime(DateTime(2026, 11, 19, 1));
      expect(late, eth(2019, 3, 10));
      expect(local, eth(2019, 3, 10));
    });
  });

  group('leap years and month lengths', () {
    test('Pagume has 6 days when the year leaves 3 after dividing by 4', () {
      for (final y in [3, 1991, 1999, 2011, 2015, 2019, 2023]) {
        expect(EthiopianDate.isLeapYear(y), isTrue, reason: '$y');
        expect(EthiopianDate.daysInMonth(y, 13), 6);
      }
      for (final y in [1, 2000, 2016, 2017, 2018, 2020]) {
        expect(EthiopianDate.isLeapYear(y), isFalse, reason: '$y');
        expect(EthiopianDate.daysInMonth(y, 13), 5);
      }
    });

    test('Meskerem to Nehase have 30 days', () {
      for (var m = 1; m <= 12; m++) {
        expect(EthiopianDate.daysInMonth(2019, m), 30);
      }
    });

    test('a year is 365 days, 366 in a leap year', () {
      for (var y = 1990; y <= 2030; y++) {
        final days =
            eth(y + 1, 1, 1).julianDayNumber - eth(y, 1, 1).julianDayNumber;
        expect(days, EthiopianDate.isLeapYear(y) ? 366 : 365, reason: '$y');
      }
    });
  });

  group('round trips', () {
    // The next Ethiopian day, from the calendar's rules alone (not the
    // converter's arithmetic).
    (int, int, int) next(int y, int m, int d) {
      final last = m < 13 ? 30 : (y % 4 == 3 ? 6 : 5);
      if (d < last) return (y, m, d + 1);
      if (m < 13) return (y, m + 1, 1);
      return (y + 1, 1, 1);
    }

    test('day by day from 1888 to 2112 against the calendar rules', () {
      // Meskerem 1, 1880 was September 11, 1887.
      var g = DateTime.utc(1887, 9, 11);
      var (y, m, d) = (1880, 1, 1);
      final end = DateTime.utc(2112, 12, 31);
      var days = 0;
      while (!g.isAfter(end)) {
        final e = EthiopianDate.fromDateTime(g);
        if (e.year != y || e.month != m || e.day != d) {
          fail('$g gave $e, expected $y-$m-$d');
        }
        if (e.toGregorian() != g) fail('$e back to ${e.toGregorian()}, not $g');
        (y, m, d) = next(y, m, d);
        g = DateTime.utc(g.year, g.month, g.day + 1);
        days++;
      }
      expect(days, greaterThan(82000));
    });

    test('every Ethiopian day of years 1 to 40 and 1990 to 2100', () {
      for (final years in [(1, 40), (1990, 2100)]) {
        for (var y = years.$1; y <= years.$2; y++) {
          for (var m = 1; m <= 13; m++) {
            for (var d = 1; d <= EthiopianDate.daysInMonth(y, m); d++) {
              final e = eth(y, m, d);
              final back = EthiopianDate.fromDateTime(e.toGregorian());
              if (back != e) fail('$e went to ${e.toGregorian()}, back $back');
            }
          }
        }
      }
    });

    test('every 7th day over 8000 years, counted with DateTime', () {
      // DateTime counts the Gregorian days from the era's first day
      // (August 27, 8 on the proleptic Gregorian calendar); the converter
      // must land on the same Julian Day and come back to the same date.
      final first = DateTime.utc(8, 8, 27);
      for (var offset = 0; offset < 8000 * 366; offset += 7) {
        final g = first.add(Duration(days: offset));
        final e = EthiopianDate.fromDateTime(g);
        if (e.julianDayNumber != EthiopianDate.epoch + offset) {
          fail('$g gave $e, Julian Day ${e.julianDayNumber}');
        }
        if (e.toGregorian() != g) fail('$e back to ${e.toGregorian()}, not $g');
      }
    });
  });

  group('invalid input', () {
    test('an Ethiopian day that does not exist', () {
      expect(() => eth(2019, 0, 1), throwsArgumentError);
      expect(() => eth(2019, 14, 1), throwsArgumentError);
      expect(() => eth(2019, 1, 0), throwsArgumentError);
      expect(() => eth(2019, 2, 31), throwsArgumentError);
      // Pagume 6 only in a leap year, Pagume 7 never.
      expect(() => eth(2018, 13, 6), throwsArgumentError);
      expect(() => eth(2019, 13, 7), throwsArgumentError);
      expect(eth(2019, 13, 6).day, 6);
      expect(() => eth(0, 1, 1), throwsArgumentError);
      expect(() => eth(-5, 1, 1), throwsArgumentError);
      expect(() => EthiopianDate.daysInMonth(2019, 0), throwsArgumentError);
      expect(() => EthiopianDate.daysInMonth(2019, 14), throwsArgumentError);
    });

    test('a Gregorian day that does not exist', () {
      expect(() => fromG(2026, 2, 29), throwsArgumentError);
      expect(() => fromG(2024, 2, 30), throwsArgumentError);
      expect(() => fromG(1900, 2, 29), throwsArgumentError);
      expect(() => fromG(2026, 4, 31), throwsArgumentError);
      expect(() => fromG(2026, 0, 1), throwsArgumentError);
      expect(() => fromG(2026, 13, 1), throwsArgumentError);
      expect(() => fromG(2026, 1, 0), throwsArgumentError);
      expect(() => fromG(2026, 1, 32), throwsArgumentError);
      expect(fromG(2000, 2, 29), eth(1992, 6, 21));
    });

    test('a Gregorian day before the Ethiopian era', () {
      expect(() => fromG(8, 8, 26), throwsArgumentError);
      expect(() => fromG(1, 1, 1), throwsArgumentError);
      expect(() => fromG(-100, 1, 1), throwsArgumentError);
      expect(fromG(8, 8, 27), eth(1, 1, 1));
    });
  });

  test('equality and text', () {
    expect(eth(2019, 3, 10), eth(2019, 3, 10));
    expect(eth(2019, 3, 10).hashCode, eth(2019, 3, 10).hashCode);
    expect(eth(2019, 3, 10), isNot(eth(2019, 3, 11)));
    expect(eth(2019, 3, 10).toString(), 'EthiopianDate(2019-3-10)');
  });
}

/// A day on the Ethiopian calendar (Amete Mihret era), converted to and from
/// the Gregorian calendar through the Julian Day Number.
///
/// The year has twelve months of 30 days and then Pagume, 5 days long, or 6
/// in a leap year (one where `year % 4 == 3`: 2015, 2019, 2023). The year
/// starts on Meskerem 1: from 1900 to 2099 Gregorian September 11, or
/// September 12 when the next year is divisible by 4 (the year before a
/// Gregorian leap year, 2023 and 2027 for example). The
/// Ethiopian year is 7 behind the Gregorian one from Meskerem 1 to
/// December 31 and 8 behind from January 1 to the end of Pagume.
///
/// Display only: dates for storage, the wire, ids and cache keys stay
/// Gregorian.
final class EthiopianDate {
  /// Meskerem 1 of year [year] when [month] and [day] are 1: [month] is 1
  /// (Meskerem) to 13 (Pagume). Throws an [ArgumentError] for a day that
  /// does not exist (year 0, month 14, Tikimt 31, Pagume 6 outside a leap
  /// year).
  factory EthiopianDate(int year, int month, int day) {
    if (year < 1) {
      throw ArgumentError.value(year, 'year', 'must be 1 or later');
    }
    final last = daysInMonth(year, month);
    if (day < 1 || day > last) {
      throw RangeError.range(day, 1, last, 'day');
    }
    return EthiopianDate._(year, month, day);
  }

  const EthiopianDate._(this.year, this.month, this.day);

  /// The Ethiopian date of the Gregorian [year]-[month]-[day]. Throws an
  /// [ArgumentError] for a Gregorian day that does not exist (February 29
  /// outside a leap year, month 13) or one before Meskerem 1 of year 1.
  factory EthiopianDate.fromGregorian(int year, int month, int day) {
    if (month < 1 || month > 12) {
      throw RangeError.range(month, 1, 12, 'month');
    }
    final last = _gregorianDaysInMonth(year, month);
    if (day < 1 || day > last) {
      throw RangeError.range(day, 1, last, 'day');
    }
    final jdn = _gregorianToJdn(year, month, day);
    if (jdn < epoch) {
      throw ArgumentError.value(
        '$year-$month-$day',
        'date',
        'is before Meskerem 1 of year 1',
      );
    }
    return _fromJdn(jdn);
  }

  /// The Ethiopian date of [date]'s own year, month and day; its time and
  /// timezone are ignored, as a `DateFormat` does. For a server instant pass
  /// `dayInEthiopia(instant)`, so a deadline at 23:00 UTC (02:00 the next
  /// morning in Ethiopia) gets the Ethiopian day it falls on in Ethiopia.
  factory EthiopianDate.fromDateTime(DateTime date) =>
      EthiopianDate.fromGregorian(date.year, date.month, date.day);

  /// The Julian Day Number of Meskerem 1, year 1 (Amete Mihret).
  static const int epoch = 1724221;

  final int year;

  /// 1 (Meskerem) to 13 (Pagume).
  final int month;

  /// 1 to 30; 1 to 5 or 6 in Pagume.
  final int day;

  /// Whether [year] has a sixth day of Pagume.
  static bool isLeapYear(int year) => year % 4 == 3;

  /// The number of days of [month] (1 to 13) in [year].
  static int daysInMonth(int year, int month) {
    if (month < 1 || month > 13) {
      throw RangeError.range(month, 1, 13, 'month');
    }
    if (month < 13) return 30;
    return isLeapYear(year) ? 6 : 5;
  }

  /// The Julian Day Number of this day.
  int get julianDayNumber =>
      epoch + 365 * (year - 1) + year ~/ 4 + 30 * (month - 1) + day - 1;

  /// The same day on the Gregorian calendar, as a UTC midnight.
  DateTime toGregorian() {
    final (y, m, d) = _jdnToGregorian(julianDayNumber);
    return DateTime.utc(y, m, d);
  }

  @override
  bool operator ==(Object other) =>
      other is EthiopianDate &&
      other.year == year &&
      other.month == month &&
      other.day == day;

  @override
  int get hashCode => Object.hash(year, month, day);

  @override
  String toString() => 'EthiopianDate($year-$month-$day)';

  /// Day 0 of a four-year cycle whose fourth year is the leap year: the
  /// cycle runs from year 0, so years 3, 7, 11 and on end with Pagume 6.
  static const int _cycleStart = epoch - 365;

  static EthiopianDate _fromJdn(int jdn) {
    final days = jdn - _cycleStart;
    final r = days % 1461;
    final n = r % 365 + 365 * (r ~/ 1460);
    final year = 4 * (days ~/ 1461) + r ~/ 365 - r ~/ 1460;
    return EthiopianDate._(year, n ~/ 30 + 1, n % 30 + 1);
  }

  static bool _isGregorianLeapYear(int year) =>
      year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

  static int _gregorianDaysInMonth(int year, int month) => switch (month) {
        2 => _isGregorianLeapYear(year) ? 29 : 28,
        4 || 6 || 9 || 11 => 30,
        _ => 31,
      };

  // Fliegel and Van Flandern (1968), for the proleptic Gregorian calendar.
  static int _gregorianToJdn(int year, int month, int day) {
    final a = (14 - month) ~/ 12;
    final y = year + 4800 - a;
    final m = month + 12 * a - 3;
    return day +
        (153 * m + 2) ~/ 5 +
        365 * y +
        y ~/ 4 -
        y ~/ 100 +
        y ~/ 400 -
        32045;
  }

  static (int, int, int) _jdnToGregorian(int jdn) {
    final a = jdn + 32044;
    final b = (4 * a + 3) ~/ 146097;
    final c = a - (146097 * b) ~/ 4;
    final d = (4 * c + 3) ~/ 1461;
    final e = c - (1461 * d) ~/ 4;
    final m = (5 * e + 2) ~/ 153;
    return (
      100 * b + d - 4800 + m ~/ 10,
      m + 3 - 12 * (m ~/ 10),
      e - (153 * m + 2) ~/ 5 + 1,
    );
  }
}

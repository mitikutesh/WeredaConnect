import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';

/// SIM-50: a trip's departure day is the day it falls on in Ethiopia, for
/// every viewer. The inputs are built with explicit offsets, so the cases do
/// not depend on the timezone of the machine running them.
void main() {
  DateTime day(String iso) => dayInEthiopia(DateTime.parse(iso));

  group('dayInEthiopia', () {
    test('25 Oct picked in Addis Ababa, as the server returns it', () {
      // The picker gives local midnight, the client sends it as UTC and the
      // server stores 2026-10-24T21:00:00.000Z. Formatting that UTC value
      // showed 24 Oct on web (the reported bug).
      expect(day('2026-10-24T21:00:00.000Z'), DateTime.utc(2026, 10, 25));
      expect(day('2026-10-25T00:00:00.000+03:00'), DateTime.utc(2026, 10, 25));
    });

    test('a value sent without a zone, read by the server as UTC', () {
      expect(day('2026-10-25T00:00:00.000Z'), DateTime.utc(2026, 10, 25));
    });

    test('a day picked on a browser west of Ethiopia', () {
      // 25 Oct picked at UTC-4 is 2026-10-25T04:00:00.000Z; toLocal() would
      // be right there, and this rule agrees.
      expect(day('2026-10-25T00:00:00.000-04:00'), DateTime.utc(2026, 10, 25));
    });

    test('a departure with a time of day keeps its day in Ethiopia', () {
      // The contract allows a time (its example is 06:00Z). 13:00Z is 16:00
      // in Ethiopia, still 18 Feb; rounding to the nearest UTC midnight would
      // say 19 Feb.
      expect(day('2026-02-18T06:00:00Z'), DateTime.utc(2026, 2, 18));
      expect(day('2026-02-18T13:00:00Z'), DateTime.utc(2026, 2, 18));
    });

    test('a day in Ethiopia ends at 21:00 UTC', () {
      expect(day('2026-10-25T20:59:59.999Z'), DateTime.utc(2026, 10, 25));
      expect(day('2026-10-25T21:00:00.000Z'), DateTime.utc(2026, 10, 26));
    });

    test('rolls over the month and the year', () {
      expect(day('2026-10-31T21:00:00.000Z'), DateTime.utc(2026, 11));
      expect(day('2026-12-31T21:00:00.000Z'), DateTime.utc(2027));
    });

    test('the same instant gives the same day in UTC or local form', () {
      // Web keeps the parsed value in UTC; Isar hands mobile a local one.
      final instant = DateTime.parse('2026-10-24T21:00:00.000Z');
      expect(dayInEthiopia(instant.toLocal()), dayInEthiopia(instant));
      expect(dayInEthiopia(instant.toLocal()), DateTime.utc(2026, 10, 25));
    });

    test('formats as the day in Ethiopia', () {
      expect(
        DateFormat.yMMMd().format(day('2026-10-24T21:00:00.000Z')),
        'Oct 25, 2026',
      );
      expect(
        DateFormat.MMMd().format(day('2026-10-24T21:00:00.000Z')),
        'Oct 25',
      );
    });
  });
}

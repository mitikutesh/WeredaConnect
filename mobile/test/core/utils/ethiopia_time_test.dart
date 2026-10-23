import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';

/// SIM-50: a trip's departure day is the day it falls on in Ethiopia, for
/// every viewer. The inputs are built with explicit offsets, so the cases do
/// not depend on the timezone of the machine running them.
// intl renders a narrow no-break space (U+202F) before AM/PM; normalize
// it so the literal expectations below stay readable.
String _norm(String s) => s.replaceAll('\u202f', ' ').replaceAll('\u00a0', ' ');

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

  group('instantInEthiopia', () {
    DateTime at(String iso) => instantInEthiopia(DateTime.parse(iso));

    test('an instant on the previous UTC day is today in Ethiopia', () {
      // 02:30 on 23 Oct in Ethiopia is 23:30 on 22 Oct UTC. Web kept the UTC
      // value and formatted it, so it showed 22 Oct (the reported bug).
      final eat = at('2026-10-22T23:30:00.000Z');
      expect((eat.year, eat.month, eat.day), (2026, 10, 23));
      expect((eat.hour, eat.minute), (2, 30));
      // The old, unconverted display showed the previous day.
      expect(
        DateFormat.yMMMd().format(DateTime.parse('2026-10-22T23:30:00.000Z')),
        'Oct 22, 2026',
      );
      expect(_norm(DateFormat.yMMMd().add_jm().format(eat)),
          'Oct 23, 2026 2:30 AM');
    });

    test('keeps the time of day, not only the date', () {
      expect(
        _norm(
            DateFormat.yMMMd().add_jm().format(at('2026-06-19T10:00:00.000Z'))),
        'Jun 19, 2026 1:00 PM',
      );
    });

    test('shows Ethiopia time whatever the device timezone', () {
      // The same instant written at UTC-4 formats to the same Ethiopia time.
      final fromUtc = at('2026-10-22T23:30:00.000Z');
      final fromWest = at('2026-10-22T19:30:00.000-04:00');
      expect(fromWest, fromUtc);
      expect(_norm(DateFormat.jm().format(fromWest)), '2:30 AM');
    });

    test('the same instant in UTC or local form gives the same wall clock', () {
      // Web keeps the parsed value in UTC; Isar hands mobile a local one.
      final instant = DateTime.parse('2026-10-22T23:30:00.000Z');
      expect(instantInEthiopia(instant.toLocal()), instantInEthiopia(instant));
    });

    test('its calendar day matches dayInEthiopia', () {
      final instant = DateTime.parse('2026-10-22T23:30:00.000Z');
      final eat = instantInEthiopia(instant);
      expect(
        DateTime.utc(eat.year, eat.month, eat.day),
        dayInEthiopia(instant),
      );
    });
  });
}

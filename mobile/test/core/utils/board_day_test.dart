import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';

/// The boards write days as "Thu 30 Oct" (Trip detail, Deliveries, Crop
/// detail) and a date field as "Thu 30 Oct 2026" (Create trip).
void main() {
  test('a day and a date in the boards\' shape', () {
    expect(formatBoardDay(DateTime.utc(2026, 10, 29)), 'Thu 29 Oct');
    expect(formatBoardDate(DateTime.utc(2026, 10, 29)), 'Thu 29 Oct 2026');
  });

  test('a trip departure is the day it falls on in Ethiopia', () {
    // Local midnight on 25 Oct in Addis Ababa, stored as UTC.
    expect(
      boardDayInEthiopia(DateTime.parse('2026-10-24T21:00:00.000Z')),
      'Sun 25 Oct',
    );
  });

  // The formatters used to be built once, at first use, and kept that
  // language for the rest of the run.
  group('in the app language', () {
    setUpAll(() async {
      await GlobalMaterialLocalizations.delegate.load(const Locale('en'));
    });

    tearDown(() => Intl.defaultLocale = null);

    test('Amharic day and month names, then English again', () {
      final day = DateTime.utc(2026, 10, 29);
      expect(formatBoardDay(day), 'Thu 29 Oct');

      // FUP-545: the Ethiopian date (Tikimt 19, 2019) first, the Gregorian
      // month and day in brackets.
      applyDateLocale(const Locale('am'));
      expect(formatBoardDay(day), 'ሐሙስ ጥቅምት 19 (ኦክቶ 29)');
      expect(formatBoardDate(day), 'ሐሙስ ጥቅምት 19፣ 2019 (ኦክቶ 29)');

      // BUG-L2: Afaan Oromoo names, not English ones.
      applyDateLocale(const Locale('om'));
      expect(formatBoardDay(day), 'Kam Onkoloolessa 19 (Onk 29)');
      expect(formatBoardDate(day), 'Kam Onkoloolessa 19, 2019 (Onk 29)');

      applyDateLocale(const Locale('en'));
      expect(formatBoardDate(day), 'Thu 29 Oct 2026');
    });

    test('a trip departure is the Ethiopian day it falls on in Ethiopia', () {
      // Local midnight on 25 Oct in Addis Ababa (Tikimt 15), stored as UTC.
      final departure = DateTime.parse('2026-10-24T21:00:00.000Z');
      applyDateLocale(const Locale('am'));
      expect(boardDayInEthiopia(departure), 'እሑድ ጥቅምት 15 (ኦክቶ 25)');
      applyDateLocale(const Locale('om'));
      expect(boardDayInEthiopia(departure), 'Dil Onkoloolessa 15 (Onk 25)');
    });
  });
}

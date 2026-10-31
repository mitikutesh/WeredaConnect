import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/board_day.dart';

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
}

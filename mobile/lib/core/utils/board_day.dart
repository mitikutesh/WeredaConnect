import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';

/// A day the way the Fieldway boards write it: "Thu 30 Oct" (weekday, day,
/// month), in the app's date locale. Amharic and Afaan Oromoo put the
/// Ethiopian date first (FUP-545, [formatDualDate]): "ዓርብ ጥቅምት 20 (ኦክቶ 30)",
/// "Jim Onkoloolessa 20 (Onk 30)".
///
/// Built on each call, not once: a formatter keeps the locale it was made
/// in, and the app language can change while the app runs.
String formatBoardDay(DateTime day) => formatDualDate(
      day,
      DateFormat('EEE d MMM'),
      year: false,
      weekday: true,
    );

/// The same with the year, for a date field ("Thu 30 Oct 2026",
/// "ዓርብ ጥቅምት 20፣ 2019 (ኦክቶ 30)").
String formatBoardDate(DateTime day) => formatDualDate(
      day,
      DateFormat('EEE d MMM y'),
      year: true,
      weekday: true,
    );

/// "Thu 30 Oct" for the Ethiopia calendar day [instant] falls on (a trip's
/// departure, a pickup), whatever the device timezone ([dayInEthiopia]).
String boardDayInEthiopia(DateTime instant) =>
    formatBoardDay(dayInEthiopia(instant));

import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';

/// A day the way the Fieldway boards write it: "Thu 30 Oct" (weekday, day,
/// month), in the app's date locale.
///
/// Built on each call, not once: a formatter keeps the locale it was made
/// in, and the app language can change while the app runs.
String formatBoardDay(DateTime day) => DateFormat('EEE d MMM').format(day);

/// The same with the year, for a date field ("Thu 30 Oct 2026").
String formatBoardDate(DateTime day) => DateFormat('EEE d MMM y').format(day);

/// "Thu 30 Oct" for the Ethiopia calendar day [instant] falls on (a trip's
/// departure, a pickup), whatever the device timezone ([dayInEthiopia]).
String boardDayInEthiopia(DateTime instant) =>
    formatBoardDay(dayInEthiopia(instant));

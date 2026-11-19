import 'dart:ui' show Locale;

import 'package:intl/date_symbol_data_custom.dart';
import 'package:intl/date_symbols.dart';
import 'package:intl/intl.dart';
import 'package:intl/number_symbols_data.dart';

/// The intl locale dates are written in for the app language
/// [languageCode]: Amharic month and day names in Amharic, Afaan Oromoo
/// ones in Afaan Oromoo (intl ships none, [registerOromoDateData] adds
/// them), everything else in English.
///
/// Both need intl's date data, which Flutter's Material localizations load
/// at start; until then, and in a bare unit test, it is English.
String dateLocaleFor(String languageCode) => switch (languageCode) {
      'am' when _dateLocaleExists('am') => 'am',
      'om' when _dateLocaleExists('om') => 'om',
      _ => 'en',
    };

/// Makes every [DateFormat] built without a locale ("Thu 30 Oct", "Oct 30,
/// 2026") follow the app language [locale].
///
/// A global default is safe here: no DateFormat output goes to the wire,
/// storage, a cache key or an id. The one parser (the server's Date header
/// in api_client.dart) pins `en_US`, and amounts pin `en_US` in
/// etb_format.dart. Number formats built without a locale follow it too;
/// Amharic and Afaan Oromoo write them with the same digits and separators
/// as English.
void applyDateLocale(Locale locale) {
  if (locale.languageCode == 'om') registerOromoDateData();
  Intl.defaultLocale = dateLocaleFor(locale.languageCode);
}

/// Adds Afaan Oromoo (`om`) to intl: month and weekday names with the
/// English date layouts ("Sad 19, 2026"), and the English number symbols, so
/// a [NumberFormat] built without a locale keeps working while `om` is the
/// default locale.
///
/// Does nothing until intl's date data is loaded (Flutter's Material
/// localizations do that at start): registering into the empty table would
/// leave it without English.
void registerOromoDateData() {
  if (!_dateLocaleExists('en') || _dateLocaleExists('om')) return;
  initializeDateFormattingCustom(
    locale: 'om',
    symbols: _oromoDateSymbols,
    patterns: _oromoDatePatterns,
  );
  numberFormatSymbols['om'] ??= numberFormatSymbols['en']!;
  compactNumberSymbols['om'] ??= compactNumberSymbols['en']!;
}

/// [DateFormat.localeExists], false while intl's date data is not loaded
/// (the lookup throws then).
bool _dateLocaleExists(String locale) {
  try {
    return DateFormat.localeExists(locale);
  } on Exception {
    return false;
  }
}

// The names below are the Unicode CLDR `om` locale's (CLDR 46, read from
// ICU 76): months, weekdays and the AM/PM markers. One deviation: CLDR's
// short list has the full "Bitootessa" and "Sadaasa" for March and
// November; "Bit" and "Sad" keep a short date short.
const _oromoMonths = [
  'Amajjii',
  'Guraandhala',
  'Bitootessa',
  'Eebila',
  'Caamsaa',
  'Waxabajjii',
  'Adoolessa',
  'Hagayya',
  'Fulbaana',
  'Onkoloolessa',
  'Sadaasa',
  'Mudde',
];

const _oromoShortMonths = [
  'Ama',
  'Gur',
  'Bit',
  'Elb',
  'Cam',
  'Wax',
  'Ado',
  'Hag',
  'Ful',
  'Onk',
  'Sad',
  'Mud',
];

const _oromoNarrowMonths = [
  'A',
  'G',
  'B',
  'E',
  'C',
  'W',
  'A',
  'H',
  'F',
  'O',
  'S',
  'M',
];

// Sunday first, as intl keeps them.
const _oromoWeekdays = [
  'Dilbata',
  'Wiixata',
  'Kibxata',
  'Roobii',
  'Kamisa',
  'Jimaata',
  'Sanbata',
];

const _oromoShortWeekdays = ['Dil', 'Wix', 'Kib', 'Rob', 'Kam', 'Jim', 'San'];

const _oromoNarrowWeekdays = ['D', 'W', 'K', 'R', 'K', 'J', 'S'];

final DateSymbols _oromoDateSymbols = DateSymbols(
  NAME: 'om',
  ERAS: const ['DhKD', 'DhKB'],
  ERANAMES: const ['Dhaloota Kiristoos Dura', 'Dhaloota Kiristoos Booda'],
  NARROWMONTHS: _oromoNarrowMonths,
  STANDALONENARROWMONTHS: _oromoNarrowMonths,
  MONTHS: _oromoMonths,
  STANDALONEMONTHS: _oromoMonths,
  SHORTMONTHS: _oromoShortMonths,
  STANDALONESHORTMONTHS: _oromoShortMonths,
  WEEKDAYS: _oromoWeekdays,
  STANDALONEWEEKDAYS: _oromoWeekdays,
  SHORTWEEKDAYS: _oromoShortWeekdays,
  STANDALONESHORTWEEKDAYS: _oromoShortWeekdays,
  NARROWWEEKDAYS: _oromoNarrowWeekdays,
  STANDALONENARROWWEEKDAYS: _oromoNarrowWeekdays,
  SHORTQUARTERS: const ['K1', 'K2', 'K3', 'K4'],
  QUARTERS: const ['Kurmaana 1', 'Kurmaana 2', 'Kurmaana 3', 'Kurmaana 4'],
  // Waaree dura, waaree booda.
  AMPMS: const ['WD', 'WB'],
  DATEFORMATS: const ['EEEE, MMMM d, y', 'MMMM d, y', 'MMM d, y', 'M/d/yy'],
  TIMEFORMATS: const [
    'h:mm:ss a zzzz',
    'h:mm:ss a z',
    'h:mm:ss a',
    'h:mm a',
  ],
  DATETIMEFORMATS: const ['{1}, {0}', '{1}, {0}', '{1}, {0}', '{1}, {0}'],
  // Sunday, as for Amharic.
  FIRSTDAYOFWEEK: 6,
  WEEKENDRANGE: const [5, 6],
  FIRSTWEEKCUTOFFDAY: 5,
);

/// intl's English skeleton patterns: an Afaan Oromoo date keeps the layout
/// it had while it was written in English, only the names change.
const Map<String, String> _oromoDatePatterns = {
  'd': 'd',
  'E': 'ccc',
  'EEEE': 'cccc',
  'LLL': 'LLL',
  'LLLL': 'LLLL',
  'M': 'L',
  'Md': 'M/d',
  'MEd': 'EEE, M/d',
  'MMM': 'LLL',
  'MMMd': 'MMM d',
  'MMMEd': 'EEE, MMM d',
  'MMMM': 'LLLL',
  'MMMMd': 'MMMM d',
  'MMMMEEEEd': 'EEEE, MMMM d',
  'QQQ': 'QQQ',
  'QQQQ': 'QQQQ',
  'y': 'y',
  'yM': 'M/y',
  'yMd': 'M/d/y',
  'yMEd': 'EEE, M/d/y',
  'yMMM': 'MMM y',
  'yMMMd': 'MMM d, y',
  'yMMMEd': 'EEE, MMM d, y',
  'yMMMM': 'MMMM y',
  'yMMMMd': 'MMMM d, y',
  'yMMMMEEEEd': 'EEEE, MMMM d, y',
  'yQQQ': 'QQQ y',
  'yQQQQ': 'QQQQ y',
  'H': 'HH',
  'Hm': 'HH:mm',
  'Hms': 'HH:mm:ss',
  'j': 'h a',
  'jm': 'h:mm a',
  'jms': 'h:mm:ss a',
  'jmv': 'h:mm a v',
  'jmz': 'h:mm a z',
  'jz': 'h a z',
  'm': 'm',
  'ms': 'mm:ss',
  's': 's',
  'v': 'v',
  'z': 'z',
  'zzzz': 'zzzz',
  'ZZZZ': 'ZZZZ',
};

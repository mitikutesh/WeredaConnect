import 'dart:ui' show Locale;

import 'package:intl/intl.dart';

/// The intl locale dates are written in for the app language
/// [languageCode]: Amharic month and day names in Amharic, everything else
/// (Afaan Oromoo too, which intl has no date names for) in English.
///
/// Amharic needs intl's date data, which Flutter's Material localizations
/// load at start; until then, and in a bare unit test, it is English.
String dateLocaleFor(String languageCode) =>
    languageCode == 'am' && DateFormat.localeExists('am') ? 'am' : 'en';

/// Makes every [DateFormat] built without a locale ("Thu 30 Oct", "Oct 30,
/// 2026") follow the app language [locale].
///
/// A global default is safe here: no DateFormat output goes to the wire,
/// storage, a cache key or an id. The one parser (the server's Date header
/// in api_client.dart) pins `en_US`, and amounts pin `en_US` in
/// etb_format.dart. Number formats built without a locale follow it too;
/// Amharic writes them with the same digits and separators as English.
void applyDateLocale(Locale locale) {
  Intl.defaultLocale = dateLocaleFor(locale.languageCode);
}

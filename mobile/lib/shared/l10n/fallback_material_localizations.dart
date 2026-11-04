import 'package:flutter/cupertino.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'package:wereda_connect/core/utils/date_locale.dart';

/// Material localizations for the app's languages: Flutter's own for
/// English and Amharic (the back button, date picker, "OK" and "Cancel"
/// in Amharic), English for Afaan Oromoo, which Flutter does not ship.
///
/// Loading them also sets the date locale ([applyDateLocale]), so dates
/// follow the app language from the first frame after a language change.
class FallbackMaterialLocalizationsDelegate
    extends LocalizationsDelegate<MaterialLocalizations> {
  const FallbackMaterialLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) {
    // Support all locales - we'll provide fallback for unsupported ones
    return true;
  }

  @override
  Future<MaterialLocalizations> load(Locale locale) async {
    final MaterialLocalizations localizations;
    if (GlobalMaterialLocalizations.delegate.isSupported(locale)) {
      localizations = await GlobalMaterialLocalizations.delegate.load(locale);
    } else {
      // Afaan Oromoo (om): no Flutter translation, English labels.
      localizations = await GlobalMaterialLocalizations.delegate.load(
        const Locale('en'),
      );
    }
    // After the load above, which brings intl's date data with it.
    applyDateLocale(locale);
    return SynchronousFuture<MaterialLocalizations>(localizations);
  }

  @override
  bool shouldReload(FallbackMaterialLocalizationsDelegate old) => false;
}

/// Cupertino localizations: Flutter's own for English and Amharic, English
/// for Afaan Oromoo.
class FallbackCupertinoLocalizationsDelegate
    extends LocalizationsDelegate<CupertinoLocalizations> {
  const FallbackCupertinoLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) => true;

  @override
  Future<CupertinoLocalizations> load(Locale locale) async {
    if (GlobalCupertinoLocalizations.delegate.isSupported(locale)) {
      return GlobalCupertinoLocalizations.delegate.load(locale);
    }
    // Afaan Oromoo (om): no Flutter translation, English labels.
    return SynchronousFuture<CupertinoLocalizations>(
      await GlobalCupertinoLocalizations.delegate.load(const Locale('en')),
    );
  }

  @override
  bool shouldReload(FallbackCupertinoLocalizationsDelegate old) => false;
}

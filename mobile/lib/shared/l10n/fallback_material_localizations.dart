import 'package:flutter/cupertino.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

/// Custom Material localizations delegate that falls back to English
/// for locales that don't have Material translations (am, om).
///
/// This prevents crashes when using Amharic or Afan Oromo while ensuring
/// Material widgets (buttons, dialogs, date pickers) still work by using
/// English labels as fallback.
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
    // For Amharic and Afan Oromo, use English Material localizations
    if (locale.languageCode == 'am' || locale.languageCode == 'om') {
      return SynchronousFuture<MaterialLocalizations>(
        await GlobalMaterialLocalizations.delegate.load(const Locale('en')),
      );
    }

    // For other locales, use the default Material localizations
    return GlobalMaterialLocalizations.delegate.load(locale);
  }

  @override
  bool shouldReload(FallbackMaterialLocalizationsDelegate old) => false;
}

/// Custom Cupertino localizations delegate with fallback to English
class FallbackCupertinoLocalizationsDelegate
    extends LocalizationsDelegate<CupertinoLocalizations> {
  const FallbackCupertinoLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) => true;

  @override
  Future<CupertinoLocalizations> load(Locale locale) async {
    // For Amharic and Afan Oromo, use English Cupertino localizations
    if (locale.languageCode == 'am' || locale.languageCode == 'om') {
      return SynchronousFuture<CupertinoLocalizations>(
        await GlobalCupertinoLocalizations.delegate.load(const Locale('en')),
      );
    }

    return GlobalCupertinoLocalizations.delegate.load(locale);
  }

  @override
  bool shouldReload(FallbackCupertinoLocalizationsDelegate old) => false;
}

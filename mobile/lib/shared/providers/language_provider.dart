import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Manages app language/locale selection with persistence.
///
/// Supports three languages:
/// - English (en)
/// - Amharic (am) - አማርኛ
/// - Afan Oromo (om) - Afaan Oromoo
///
/// Language preference is persisted to shared preferences and
/// restored on app startup. Falls back to device locale if no
/// preference is saved.
class LanguageProvider extends ChangeNotifier {
  static const String _languageKey = 'selected_language';

  /// Currently selected locale. Defaults to English.
  Locale _locale = const Locale('en');

  /// Gets the current locale
  Locale get locale => _locale;

  /// Called after the locale actually changes, with the new language code
  /// (en, am, om). The app wires this to push the choice to the user's server
  /// profile so the language follows them to other devices (SIM-37). It is only
  /// invoked on a real change, never during [initialize]. A failed push must
  /// never break the UI, so [setLocale] swallows anything it throws.
  Future<void> Function(String languageCode)? onLanguageChanged;

  /// Supported locales
  static const List<Locale> supportedLocales = [
    Locale('en'), // English
    Locale('am'), // Amharic
    Locale('om'), // Afan Oromo
  ];

  /// Initialize language provider and load saved preference
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    final savedLanguage = prefs.getString(_languageKey);

    if (savedLanguage != null) {
      // Restore saved language
      _locale = Locale(savedLanguage);
    } else {
      // No saved preference - use device locale if supported.
      // platformDispatcher.locale usually carries a country/script subtag
      // (e.g. am_ET, en_US), so match on languageCode only and adopt the
      // bare supported locale to keep persistence/equality consistent.
      final deviceLocale = WidgetsBinding.instance.platformDispatcher.locale;
      final match = supportedLocales
          .where((l) => l.languageCode == deviceLocale.languageCode)
          .firstOrNull;
      if (match != null) {
        _locale = match;
      }
      // Otherwise keep default (English)
    }

    notifyListeners();
  }

  /// Change the app language and persist the choice
  Future<void> setLocale(Locale newLocale) async {
    if (!supportedLocales.contains(newLocale)) {
      throw ArgumentError(
        'Unsupported locale: ${newLocale.languageCode}. '
        'Supported: ${supportedLocales.map((l) => l.languageCode).join(", ")}',
      );
    }

    if (_locale == newLocale) {
      return; // No change needed
    }

    _locale = newLocale;
    notifyListeners();

    // Persist to shared preferences
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_languageKey, newLocale.languageCode);

    // SIM-37: let the app sync the choice to the server profile. Best effort:
    // a sync failure must not break language switching.
    final notify = onLanguageChanged;
    if (notify != null) {
      try {
        await notify(newLocale.languageCode);
      } on Object catch (e) {
        debugPrint('[LanguageProvider] language sync hook failed: $e');
      }
    }
  }

  /// Get localized language name for display
  String getLanguageName(Locale locale) {
    return switch (locale.languageCode) {
      'en' => 'English',
      'am' => 'አማርኛ', // Amharic
      'om' => 'Afaan Oromoo', // Afan Oromo
      _ => locale.languageCode,
    };
  }

  /// Get all supported languages with their native names
  List<MapEntry<Locale, String>> get availableLanguages {
    return supportedLocales
        .map((locale) => MapEntry(locale, getLanguageName(locale)))
        .toList();
  }
}

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
      // No saved preference - use device locale if supported
      final deviceLocale = WidgetsBinding.instance.platformDispatcher.locale;
      if (supportedLocales.contains(deviceLocale)) {
        _locale = deviceLocale;
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

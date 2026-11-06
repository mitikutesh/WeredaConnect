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
/// preference is saved. After a sign-in, [applyProfileLanguage] shows the
/// language saved on the user's server profile unless the user picked one on
/// this device (SIM-37).
class LanguageProvider extends ChangeNotifier {
  static const String _languageKey = 'selected_language';

  /// Who stored [_languageKey] (SIM-37): [_sourceUser] when the user picked
  /// the language on this device, [_sourceProfile] when a sign-in took it
  /// from the server profile. A value stored before this key existed has no
  /// source and counts as the user's pick: until then only [setLocale] wrote
  /// [_languageKey], and only for a language the user picked.
  static const String _languageSourceKey = 'selected_language_source';
  static const String _sourceUser = 'user';
  static const String _sourceProfile = 'profile';

  /// Currently selected locale. Defaults to English.
  Locale _locale = const Locale('en');

  /// Gets the current locale
  Locale get locale => _locale;

  /// Called after the locale actually changes, with the new language code
  /// (en, am, om). The app wires this to push the choice to the user's server
  /// profile so the language follows them to other devices (SIM-37). It is only
  /// invoked on a real change, never during [initialize] or
  /// [applyProfileLanguage]. A failed push must never break the UI, so
  /// [setLocale] swallows anything it throws.
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
      // No change to show, but picking the language already shown is still
      // the user's pick on this device: remember it, so a sign-in does not
      // replace it with the profile's (SIM-37).
      final prefs = await SharedPreferences.getInstance();
      if (!_isUserPick(prefs)) {
        await _store(prefs, newLocale.languageCode, _sourceUser);
      }
      return;
    }

    _locale = newLocale;
    notifyListeners();

    // Persist to shared preferences
    final prefs = await SharedPreferences.getInstance();
    await _store(prefs, newLocale.languageCode, _sourceUser);

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

  /// Show the language saved on the user's server profile after a sign-in
  /// (SIM-37), unless the user picked a language on this device.
  ///
  /// The rule: a language the user picked on this device ([setLocale]) wins;
  /// a pick made while signed in was already sent to the profile through
  /// [onLanguageChanged]. Anything else (the device locale default, or a
  /// language an earlier sign-in took from a profile) gives way to
  /// [languageCode], which is then stored as this device's language so the
  /// next start opens in it. It is not sent back to the server:
  /// [onLanguageChanged] is not called.
  ///
  /// A missing or unsupported [languageCode] changes nothing. Returns whether
  /// the profile's language was applied.
  Future<bool> applyProfileLanguage(String? languageCode) async {
    final code = languageCode?.trim().toLowerCase();
    final match =
        supportedLocales.where((l) => l.languageCode == code).firstOrNull;
    if (match == null) return false;

    final prefs = await SharedPreferences.getInstance();
    if (_isUserPick(prefs)) return false;

    if (_locale != match) {
      _locale = match;
      notifyListeners();
    }
    await _store(prefs, match.languageCode, _sourceProfile);
    return true;
  }

  /// Whether the stored language is one the user picked on this device. A
  /// stored language without a source predates [_languageSourceKey] and was
  /// picked by the user (see there).
  static bool _isUserPick(SharedPreferences prefs) =>
      prefs.getString(_languageKey) != null &&
      prefs.getString(_languageSourceKey) != _sourceProfile;

  /// Stores [languageCode] and who chose it. The source goes first: if only
  /// one write lands, a user's pick is never left marked as the profile's.
  static Future<void> _store(
    SharedPreferences prefs,
    String languageCode,
    String source,
  ) async {
    await prefs.setString(_languageSourceKey, source);
    await prefs.setString(_languageKey, languageCode);
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

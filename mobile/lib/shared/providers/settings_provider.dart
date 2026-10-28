import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/utils/ethiopian_clock.dart';

/// Manages app-level settings with persistence via SharedPreferences.
class SettingsProvider extends ChangeNotifier {
  static const String _dataSaverKey = 'data_saver_mode';
  static const String _clockKey = 'clock_preference';

  bool _dataSaverEnabled = false;
  ClockPreference _clockPreference = ClockPreference.auto;

  bool get dataSaverEnabled => _dataSaverEnabled;

  /// LT-03: the clock times are shown on (Auto follows the app language).
  ClockPreference get clockPreference => _clockPreference;

  /// Load persisted settings on startup.
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    _dataSaverEnabled = prefs.getBool(_dataSaverKey) ?? false;
    _clockPreference = ClockPreference.fromName(prefs.getString(_clockKey));
    notifyListeners();
  }

  /// Toggle data saver mode and persist the new value.
  Future<void> setDataSaver({required bool enabled}) async {
    if (_dataSaverEnabled == enabled) return;
    _dataSaverEnabled = enabled;
    notifyListeners();
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_dataSaverKey, enabled);
  }

  /// Set the clock times are shown on and persist it, like the language.
  Future<void> setClockPreference(ClockPreference preference) async {
    if (_clockPreference == preference) return;
    _clockPreference = preference;
    notifyListeners();
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_clockKey, preference.name);
  }
}

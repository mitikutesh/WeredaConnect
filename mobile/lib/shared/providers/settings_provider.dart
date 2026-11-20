import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';

/// Manages app-level settings with persistence via SharedPreferences.
class SettingsProvider extends ChangeNotifier {
  static const String _dataSaverKey = 'data_saver_mode';
  static const String _clockKey = 'clock_preference';
  static const String _lastSignInRoleKey = 'last_sign_in_role';

  bool _dataSaverEnabled = false;
  ClockPreference _clockPreference = ClockPreference.auto;

  bool get dataSaverEnabled => _dataSaverEnabled;

  /// LT-03: the clock times are shown on (Auto follows the app language).
  ClockPreference get clockPreference => _clockPreference;

  UserRole? _lastSignInRole;

  /// The role of the account that last signed in on this device, null
  /// before the first sign-in. Only the login screen's look reads it (which
  /// role's colours it opens in). It is not a credential and decides
  /// nothing: the server tells every account's role at sign-in.
  UserRole? get lastSignInRole => _lastSignInRole;

  /// Load persisted settings on startup.
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    _dataSaverEnabled = prefs.getBool(_dataSaverKey) ?? false;
    _clockPreference = ClockPreference.fromName(prefs.getString(_clockKey));
    final roleName = prefs.getString(_lastSignInRoleKey);
    _lastSignInRole =
        UserRole.values.where((role) => role.name == roleName).firstOrNull;
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

  /// Remember [role] as the one that last signed in here (see
  /// [lastSignInRole]). Stored like the language: shared preferences on
  /// Android, local storage on web.
  Future<void> rememberSignInRole(UserRole role) async {
    if (_lastSignInRole == role) return;
    _lastSignInRole = role;
    notifyListeners();
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_lastSignInRoleKey, role.name);
  }
}

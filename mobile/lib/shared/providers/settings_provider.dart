import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Manages app-level settings with persistence via SharedPreferences.
class SettingsProvider extends ChangeNotifier {
  static const String _dataSaverKey = 'data_saver_mode';

  bool _dataSaverEnabled = false;

  bool get dataSaverEnabled => _dataSaverEnabled;

  /// Load persisted settings on startup.
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    _dataSaverEnabled = prefs.getBool(_dataSaverKey) ?? false;
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
}

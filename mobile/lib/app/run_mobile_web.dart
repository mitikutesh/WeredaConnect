import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

/// Web stub — mobile runner is never called on web.
Future<void> runMobile({
  required FlutterSecureStorage secureStorage,
  required DataUsageTracker dataUsageTracker,
  required LanguageProvider languageProvider,
  required SettingsProvider settingsProvider,
}) async {
  throw UnsupportedError('runMobile() is not available on web');
}

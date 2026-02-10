// ignore: unused_import
import 'package:intl/intl.dart' as intl;
import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String get appName => 'WeredaConnect';

  @override
  String get appTitle => 'WeredaConnect';

  @override
  String get welcomeMessage => 'Welcome to WeredaConnect';

  @override
  String get offlineMode => 'Offline Mode';

  @override
  String get syncPending => 'Sync Pending';

  @override
  String get syncInProgress => 'Syncing...';

  @override
  String get syncComplete => 'Sync Complete';

  @override
  String get syncFailed => 'Sync Failed';

  @override
  String get retry => 'Retry';

  @override
  String get cancel => 'Cancel';

  @override
  String get save => 'Save';

  @override
  String get delete => 'Delete';

  @override
  String get edit => 'Edit';

  @override
  String get settings => 'Settings';

  @override
  String get language => 'Language';

  @override
  String get dataUsage => 'Data Usage';

  @override
  String get dataSaverMode => 'Data Saver Mode';

  @override
  String get kebele => 'Kebele';

  @override
  String get wereda => 'Wereda';

  @override
  String get errorOccurred => 'An error occurred';

  @override
  String get noInternetConnection => 'No internet connection';

  @override
  String get dataWillSyncWhenOnline =>
      'Your data will sync when you\'re back online';

  @override
  String get offlineReady => 'Offline Ready';
}

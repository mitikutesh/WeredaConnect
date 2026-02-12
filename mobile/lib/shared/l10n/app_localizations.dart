import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_am.dart';
import 'app_localizations_en.dart';
import 'app_localizations_om.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
      : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations? of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations);
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
    delegate,
    GlobalMaterialLocalizations.delegate,
    GlobalCupertinoLocalizations.delegate,
    GlobalWidgetsLocalizations.delegate,
  ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('en'),
    Locale('am'),
    Locale('om')
  ];

  /// The application name
  ///
  /// In en, this message translates to:
  /// **'WeredaConnect'**
  String get appName;

  /// The application title shown in app bar
  ///
  /// In en, this message translates to:
  /// **'WeredaConnect'**
  String get appTitle;

  /// Welcome message shown on first launch
  ///
  /// In en, this message translates to:
  /// **'Welcome to WeredaConnect'**
  String get welcomeMessage;

  /// Indicator that the app is currently offline
  ///
  /// In en, this message translates to:
  /// **'Offline Mode'**
  String get offlineMode;

  /// Indicator that there are pending sync operations
  ///
  /// In en, this message translates to:
  /// **'Sync Pending'**
  String get syncPending;

  /// Indicator that sync is currently in progress
  ///
  /// In en, this message translates to:
  /// **'Syncing...'**
  String get syncInProgress;

  /// Indicator that sync has completed successfully
  ///
  /// In en, this message translates to:
  /// **'Sync Complete'**
  String get syncComplete;

  /// Indicator that sync has failed
  ///
  /// In en, this message translates to:
  /// **'Sync Failed'**
  String get syncFailed;

  /// Button to retry a failed operation
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get retry;

  /// Button to cancel an operation
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get cancel;

  /// Button to save data
  ///
  /// In en, this message translates to:
  /// **'Save'**
  String get save;

  /// Button to delete data
  ///
  /// In en, this message translates to:
  /// **'Delete'**
  String get delete;

  /// Button to edit data
  ///
  /// In en, this message translates to:
  /// **'Edit'**
  String get edit;

  /// Settings menu item
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get settings;

  /// Language selection setting
  ///
  /// In en, this message translates to:
  /// **'Language'**
  String get language;

  /// Data usage statistics
  ///
  /// In en, this message translates to:
  /// **'Data Usage'**
  String get dataUsage;

  /// Toggle for data saver mode
  ///
  /// In en, this message translates to:
  /// **'Data Saver Mode'**
  String get dataSaverMode;

  /// Local administrative unit (neighborhood)
  ///
  /// In en, this message translates to:
  /// **'Kebele'**
  String get kebele;

  /// District administrative unit
  ///
  /// In en, this message translates to:
  /// **'Wereda'**
  String get wereda;

  /// Generic error message
  ///
  /// In en, this message translates to:
  /// **'An error occurred'**
  String get errorOccurred;

  /// Error message when offline
  ///
  /// In en, this message translates to:
  /// **'No internet connection'**
  String get noInternetConnection;

  /// Reassurance message for offline operations
  ///
  /// In en, this message translates to:
  /// **'Your data will sync when you\'re back online'**
  String get dataWillSyncWhenOnline;

  /// Indicator that the app is ready to work offline
  ///
  /// In en, this message translates to:
  /// **'Offline Ready'**
  String get offlineReady;

  /// Home navigation tab
  ///
  /// In en, this message translates to:
  /// **'Home'**
  String get home;

  /// Listings navigation tab
  ///
  /// In en, this message translates to:
  /// **'Listings'**
  String get listings;

  /// Profile navigation tab
  ///
  /// In en, this message translates to:
  /// **'Profile'**
  String get profile;

  /// About menu item
  ///
  /// In en, this message translates to:
  /// **'About'**
  String get about;

  /// Logout menu item
  ///
  /// In en, this message translates to:
  /// **'Logout'**
  String get logout;

  /// Crop listings screen title
  ///
  /// In en, this message translates to:
  /// **'Crop Listings'**
  String get cropListings;

  /// Empty state message for crop listings
  ///
  /// In en, this message translates to:
  /// **'No crop listings yet'**
  String get noCropListings;

  /// Button to create a new crop listing
  ///
  /// In en, this message translates to:
  /// **'Create Listing'**
  String get createListing;

  /// Profile screen title
  ///
  /// In en, this message translates to:
  /// **'My Profile'**
  String get myProfile;

  /// Message when user is not logged in
  ///
  /// In en, this message translates to:
  /// **'Not logged in'**
  String get notLoggedIn;

  /// Login button
  ///
  /// In en, this message translates to:
  /// **'Login'**
  String get login;

  /// Feature coming soon message
  ///
  /// In en, this message translates to:
  /// **'Coming Soon'**
  String get comingSoon;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['am', 'en', 'om'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'am':
      return AppLocalizationsAm();
    case 'en':
      return AppLocalizationsEn();
    case 'om':
      return AppLocalizationsOm();
  }

  throw FlutterError(
      'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
      'an issue with the localizations generation tool. Please file an issue '
      'on GitHub with a reproducible sample app and the gen-l10n configuration '
      'that was used.');
}

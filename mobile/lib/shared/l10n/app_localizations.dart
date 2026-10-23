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

  /// Main title on welcome screen
  ///
  /// In en, this message translates to:
  /// **'Welcome to WeredaConnect'**
  String get welcomeTitle;

  /// Greeting on home screen desktop
  ///
  /// In en, this message translates to:
  /// **'Welcome back, {userName}'**
  String welcomeBackUser(String userName);

  /// Feature description for offline capability
  ///
  /// In en, this message translates to:
  /// **'Works Offline'**
  String get worksOffline;

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

  /// Short tagline for welcome screen
  ///
  /// In en, this message translates to:
  /// **'Connecting Farmers, Truckers & Buyers'**
  String get taglineShort;

  /// Feature description for Telebirr payment
  ///
  /// In en, this message translates to:
  /// **'Telebirr Payment'**
  String get telebirrPayment;

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

  /// Button to trigger an immediate sync
  ///
  /// In en, this message translates to:
  /// **'Sync Now'**
  String get syncNow;

  /// Language selection prompt on welcome screen
  ///
  /// In en, this message translates to:
  /// **'Select Your Language'**
  String get selectLanguagePrompt;

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

  /// Edit profile button/menu item
  ///
  /// In en, this message translates to:
  /// **'Edit Profile'**
  String get editProfile;

  /// Settings menu item
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get settings;

  /// Skip button
  ///
  /// In en, this message translates to:
  /// **'Skip'**
  String get skip;

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

  /// Shown when the server refuses a read with 403 for the signed-in role
  ///
  /// In en, this message translates to:
  /// **'You do not have permission to view this.'**
  String get errorNoPermission;

  /// Shown when the TLS handshake or certificate pin check fails while the device is online
  ///
  /// In en, this message translates to:
  /// **'Could not make a secure connection to the server. Update the app or try another network.'**
  String get errorSecureConnectionFailed;

  /// Error message when offline
  ///
  /// In en, this message translates to:
  /// **'No internet connection'**
  String get noInternetConnection;

  /// Shown when an authentication/session error occurs
  ///
  /// In en, this message translates to:
  /// **'Your session expired. Please sign in again.'**
  String get errorSessionExpired;

  /// Shown on a sync/version conflict (HTTP 409)
  ///
  /// In en, this message translates to:
  /// **'This was updated elsewhere. Refresh and try again.'**
  String get errorConflict;

  /// The server refused an order change for good (ORDER_* sync error) or the app refused it locally; on mobile the change was undone
  ///
  /// In en, this message translates to:
  /// **'This order action isn\'t allowed now, so nothing was changed.'**
  String get errorOrderRefused;

  /// The server refused an order placed offline; the app removed the local order
  ///
  /// In en, this message translates to:
  /// **'This order could not be placed, so it was removed. Please order again.'**
  String get errorOrderNotPlaced;

  /// The server refused a queued change for good (listing, trip, delivery, rating, message); it stays on this phone but was never saved
  ///
  /// In en, this message translates to:
  /// **'This change could not be saved on the server.'**
  String get errorChangeNotSaved;

  /// Banner when a marketplace refresh failed and cached data is shown
  ///
  /// In en, this message translates to:
  /// **'Showing saved results — couldn\'t refresh.'**
  String get showingCachedResults;

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

  /// How it works section title
  ///
  /// In en, this message translates to:
  /// **'How it works'**
  String get howItWorks;

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

  /// Next button
  ///
  /// In en, this message translates to:
  /// **'Next'**
  String get next;

  /// Message when user is not logged in
  ///
  /// In en, this message translates to:
  /// **'Not logged in'**
  String get notLoggedIn;

  /// Notifications menu item
  ///
  /// In en, this message translates to:
  /// **'Notifications'**
  String get notifications;

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

  /// Feature description for market connection
  ///
  /// In en, this message translates to:
  /// **'Connect with Local Markets'**
  String get connectWithMarkets;

  /// Create account button
  ///
  /// In en, this message translates to:
  /// **'Create Account'**
  String get createAccount;

  /// Current language display name
  ///
  /// In en, this message translates to:
  /// **'English'**
  String get currentLanguage;

  /// Description for data saver mode
  ///
  /// In en, this message translates to:
  /// **'Reduce data usage'**
  String get dataSaverDescription;

  /// About dialog description
  ///
  /// In en, this message translates to:
  /// **'Offline-first agritech logistics platform'**
  String get aboutDescription;

  /// About dialog tagline
  ///
  /// In en, this message translates to:
  /// **'Connecting Ethiopian farmers, truckers, and buyers'**
  String get aboutTagline;

  /// Active listings count label
  ///
  /// In en, this message translates to:
  /// **'active listings'**
  String get activeListings;

  /// Register button
  ///
  /// In en, this message translates to:
  /// **'Register'**
  String get register;

  /// Phone number input label
  ///
  /// In en, this message translates to:
  /// **'Phone Number'**
  String get phoneNumber;

  /// Password input label
  ///
  /// In en, this message translates to:
  /// **'Password'**
  String get password;

  /// Confirm password input label
  ///
  /// In en, this message translates to:
  /// **'Confirm Password'**
  String get confirmPassword;

  /// Full name input label
  ///
  /// In en, this message translates to:
  /// **'Full Name'**
  String get fullName;

  /// Label for user-chosen marketplace nickname
  ///
  /// In en, this message translates to:
  /// **'Display Name'**
  String get displayName;

  /// Helper text for display name field
  ///
  /// In en, this message translates to:
  /// **'Your marketplace nickname (visible to others)'**
  String get displayNameHelper;

  /// Label for Fayda-verified legal name card
  ///
  /// In en, this message translates to:
  /// **'Verified Legal Name'**
  String get verifiedLegalName;

  /// Role selection label
  ///
  /// In en, this message translates to:
  /// **'Select Role'**
  String get selectRole;

  /// Label for farmer name in order card
  ///
  /// In en, this message translates to:
  /// **'Farmer'**
  String get farmer;

  /// Trucker/transport provider role
  ///
  /// In en, this message translates to:
  /// **'Trucker'**
  String get trucker;

  /// Description for edit profile menu item
  ///
  /// In en, this message translates to:
  /// **'Update your name and details'**
  String get updateDetails;

  /// Label for buyer name in order card
  ///
  /// In en, this message translates to:
  /// **'Buyer'**
  String get buyer;

  /// OTP input label
  ///
  /// In en, this message translates to:
  /// **'Enter Verification Code'**
  String get enterOtp;

  /// OTP sent confirmation message
  ///
  /// In en, this message translates to:
  /// **'Verification code sent to your phone'**
  String get otpSent;

  /// Verify OTP button
  ///
  /// In en, this message translates to:
  /// **'Verify'**
  String get verifyOtp;

  /// Description for my orders menu item
  ///
  /// In en, this message translates to:
  /// **'View your order history'**
  String get viewOrderHistory;

  /// Feature description for voice support
  ///
  /// In en, this message translates to:
  /// **'Voice Support in Your Language'**
  String get voiceSupport;

  /// Resend OTP button
  ///
  /// In en, this message translates to:
  /// **'Resend Code'**
  String get resendOtp;

  /// Sign up button
  ///
  /// In en, this message translates to:
  /// **'Sign Up'**
  String get signUp;

  /// Login prompt on register screen
  ///
  /// In en, this message translates to:
  /// **'Already have an account?'**
  String get alreadyHaveAccount;

  /// Register prompt on login screen
  ///
  /// In en, this message translates to:
  /// **'Don\'t have an account?'**
  String get dontHaveAccount;

  /// Forgot password link
  ///
  /// In en, this message translates to:
  /// **'Forgot Password?'**
  String get forgotPassword;

  /// Get started button
  ///
  /// In en, this message translates to:
  /// **'Get Started'**
  String get getStarted;

  /// Online login button
  ///
  /// In en, this message translates to:
  /// **'Login Online'**
  String get loginOnline;

  /// Offline login button
  ///
  /// In en, this message translates to:
  /// **'Login Offline'**
  String get loginOffline;

  /// Offline mode indicator message
  ///
  /// In en, this message translates to:
  /// **'You are in offline mode'**
  String get offlineModeIndicator;

  /// Password requirements message
  ///
  /// In en, this message translates to:
  /// **'Password must be at least 8 characters with a lowercase letter and a number'**
  String get passwordRequirements;

  /// Error message when the phone number field is left empty
  ///
  /// In en, this message translates to:
  /// **'Phone number is required'**
  String get phoneNumberRequired;

  /// Error message for invalid phone format
  ///
  /// In en, this message translates to:
  /// **'Invalid phone number. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX'**
  String get invalidPhoneFormat;

  /// Error message for invalid credentials
  ///
  /// In en, this message translates to:
  /// **'Invalid phone number or password'**
  String get invalidCredentials;

  /// Password reset: verification code mismatch (AUTH_022)
  ///
  /// In en, this message translates to:
  /// **'The verification code is incorrect. Please check it and try again.'**
  String get resetCodeInvalid;

  /// Password reset: verification code expired (AUTH_023)
  ///
  /// In en, this message translates to:
  /// **'The verification code has expired. Please request a new one.'**
  String get resetCodeExpired;

  /// Error message for duplicate phone number
  ///
  /// In en, this message translates to:
  /// **'This phone number is already registered'**
  String get phoneAlreadyRegistered;

  /// Error message for invalid OTP
  ///
  /// In en, this message translates to:
  /// **'Invalid verification code'**
  String get invalidOtp;

  /// Error message for expired OTP
  ///
  /// In en, this message translates to:
  /// **'Verification code expired. Request a new one'**
  String get otpExpired;

  /// Error message when rate limit exceeded during auth
  ///
  /// In en, this message translates to:
  /// **'Too many attempts. Please try again later.'**
  String get authRateLimitExceeded;

  /// Error message for generic server error during auth
  ///
  /// In en, this message translates to:
  /// **'Server error. Please try again later.'**
  String get authServerError;

  /// Error message when auth service is unavailable
  ///
  /// In en, this message translates to:
  /// **'Authentication service temporarily unavailable. Please try again later.'**
  String get authServiceUnavailable;

  /// Error message when network is unavailable during auth
  ///
  /// In en, this message translates to:
  /// **'No internet connection. Please check your connection.'**
  String get authNetworkError;

  /// Error message when account is locked due to too many failed login attempts
  ///
  /// In en, this message translates to:
  /// **'Account locked due to too many failed attempts. Try again in {duration}.'**
  String authAccountLocked(String duration);

  /// Duration in seconds
  ///
  /// In en, this message translates to:
  /// **'{count} seconds'**
  String durationSeconds(int count);

  /// Duration in minutes
  ///
  /// In en, this message translates to:
  /// **'{count} minutes'**
  String durationMinutes(int count);

  /// Duration in hours
  ///
  /// In en, this message translates to:
  /// **'{count} hours'**
  String durationHours(int count);

  /// Error message when user tries to login but phone is not verified
  ///
  /// In en, this message translates to:
  /// **'Phone number not verified. Please complete OTP verification.'**
  String get phoneNotVerified;

  /// Error message when API credentials are not configured at build time
  ///
  /// In en, this message translates to:
  /// **'App setup incomplete. Please contact support.'**
  String get authApiNotConfigured;

  /// Error message when user tries offline login without prior online login
  ///
  /// In en, this message translates to:
  /// **'Please log in online first to enable offline mode.'**
  String get authOfflineNotAvailable;

  /// Error message when user account is disabled
  ///
  /// In en, this message translates to:
  /// **'Your account has been disabled. Contact support.'**
  String get authAccountDisabled;

  /// Error message when password reset is required before login
  ///
  /// In en, this message translates to:
  /// **'Password reset required. Please reset your password.'**
  String get authPasswordResetRequired;

  /// Error message when OTP verification attempts are exhausted
  ///
  /// In en, this message translates to:
  /// **'Too many failed attempts. Request a new code.'**
  String get authOtpAttemptsExceeded;

  /// Success message after registration
  ///
  /// In en, this message translates to:
  /// **'Registration successful! Please verify your phone'**
  String get registrationSuccess;

  /// Success message after login
  ///
  /// In en, this message translates to:
  /// **'Login successful!'**
  String get loginSuccess;

  /// Logout confirmation dialog message
  ///
  /// In en, this message translates to:
  /// **'Are you sure you want to logout?'**
  String get logoutConfirmation;

  /// Description for notifications menu item
  ///
  /// In en, this message translates to:
  /// **'Manage your alerts'**
  String get manageAlerts;

  /// My listings menu item
  ///
  /// In en, this message translates to:
  /// **'My Listings'**
  String get myListings;

  /// Browse listings tab label for non-farmer roles
  ///
  /// In en, this message translates to:
  /// **'Browse Listings'**
  String get browseListings;

  /// Market bottom nav tab label
  ///
  /// In en, this message translates to:
  /// **'Market'**
  String get market;

  /// Market hub segment showing all crop listings
  ///
  /// In en, this message translates to:
  /// **'Crops'**
  String get marketCrops;

  /// Market hub segment showing all trips
  ///
  /// In en, this message translates to:
  /// **'Transport'**
  String get marketTransport;

  /// Orders bottom nav tab label
  ///
  /// In en, this message translates to:
  /// **'Orders'**
  String get orders;

  /// My orders menu item
  ///
  /// In en, this message translates to:
  /// **'My Orders'**
  String get myOrders;

  /// Option to clear cached data on logout
  ///
  /// In en, this message translates to:
  /// **'Clear offline data?'**
  String get clearOfflineData;

  /// Description for clear offline data option
  ///
  /// In en, this message translates to:
  /// **'You can still access your account offline if you don\'t clear this data'**
  String get clearOfflineDataDescription;

  /// Example Ethiopian phone number
  ///
  /// In en, this message translates to:
  /// **'Example: 0911234567 or +251911234567'**
  String get ethiopianPhoneExample;

  /// Characters (for password requirements)
  ///
  /// In en, this message translates to:
  /// **'characters'**
  String get characters;

  /// Teff crop type
  ///
  /// In en, this message translates to:
  /// **'Teff'**
  String get cropTypeTeff;

  /// Wheat crop type
  ///
  /// In en, this message translates to:
  /// **'Wheat'**
  String get cropTypeWheat;

  /// Barley crop type
  ///
  /// In en, this message translates to:
  /// **'Barley'**
  String get cropTypeBarley;

  /// Maize/corn crop type
  ///
  /// In en, this message translates to:
  /// **'Maize'**
  String get cropTypeMaize;

  /// Chickpea crop type
  ///
  /// In en, this message translates to:
  /// **'Chickpea'**
  String get cropTypeChickpea;

  /// Lentils crop type
  ///
  /// In en, this message translates to:
  /// **'Lentils'**
  String get cropTypeLentils;

  /// Sorghum crop type
  ///
  /// In en, this message translates to:
  /// **'Sorghum'**
  String get cropTypeSorghum;

  /// Coffee crop type
  ///
  /// In en, this message translates to:
  /// **'Coffee'**
  String get cropTypeCoffee;

  /// Sesame crop type
  ///
  /// In en, this message translates to:
  /// **'Sesame'**
  String get cropTypeSesame;

  /// Crop type selection label
  ///
  /// In en, this message translates to:
  /// **'Select Crop Type'**
  String get selectCropType;

  /// Quantity input label in quintals
  ///
  /// In en, this message translates to:
  /// **'Quantity (quintals)'**
  String get quantityInQuintals;

  /// Price input label in Ethiopian Birr per kg
  ///
  /// In en, this message translates to:
  /// **'Price (ETB/kg)'**
  String get priceInBirr;

  /// Location input label
  ///
  /// In en, this message translates to:
  /// **'Location'**
  String get location;

  /// Description input label
  ///
  /// In en, this message translates to:
  /// **'Description'**
  String get description;

  /// Audio note label
  ///
  /// In en, this message translates to:
  /// **'Audio Note'**
  String get audioNote;

  /// Button to record voice description
  ///
  /// In en, this message translates to:
  /// **'Record Audio Note'**
  String get recordAudioNote;

  /// Shown when saving a crop listing with no GPS fix and no place picked from the location list
  ///
  /// In en, this message translates to:
  /// **'GPS is unavailable. Pick your location from the list or turn on GPS.'**
  String get cropLocationCoordinatesRequired;

  /// Validation error for missing crop type
  ///
  /// In en, this message translates to:
  /// **'Please select a crop type'**
  String get cropTypeRequired;

  /// Validation error for missing quantity
  ///
  /// In en, this message translates to:
  /// **'Please enter the quantity'**
  String get quantityRequired;

  /// Validation error when price is not entered
  ///
  /// In en, this message translates to:
  /// **'Price is required'**
  String get priceRequired;

  /// Validation error for missing location
  ///
  /// In en, this message translates to:
  /// **'Please enter the location'**
  String get locationRequired;

  /// Success message after saving listing
  ///
  /// In en, this message translates to:
  /// **'Listing saved successfully'**
  String get listingSaved;

  /// Message after deleting listing
  ///
  /// In en, this message translates to:
  /// **'Listing deleted'**
  String get listingDeleted;

  /// Filter label for all crop types
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get allCrops;

  /// Unit of measurement (100kg)
  ///
  /// In en, this message translates to:
  /// **'quintals'**
  String get quintals;

  /// Ethiopian Birr per kilogram
  ///
  /// In en, this message translates to:
  /// **'ETB/kg'**
  String get etbPerKg;

  /// Button to explore app without login
  ///
  /// In en, this message translates to:
  /// **'Explore as Guest'**
  String get exploreAsGuest;

  /// Error message when user is not authenticated
  ///
  /// In en, this message translates to:
  /// **'You must be logged in to perform this action'**
  String get notAuthenticated;

  /// Error message when password and confirm password don't match
  ///
  /// In en, this message translates to:
  /// **'Passwords do not match'**
  String get passwordsDoNotMatch;

  /// Hint text for OTP input field
  ///
  /// In en, this message translates to:
  /// **'Please enter 6-digit code'**
  String get enterSixDigitCode;

  /// Countdown message before resend OTP button is enabled
  ///
  /// In en, this message translates to:
  /// **'Resend code in {seconds} seconds'**
  String resendCodeCountdown(int seconds);

  /// Status label for active crop listing
  ///
  /// In en, this message translates to:
  /// **'Active'**
  String get listingStatusActive;

  /// Status label for draft crop listing
  ///
  /// In en, this message translates to:
  /// **'Draft'**
  String get listingStatusDraft;

  /// Status label for sold crop listing
  ///
  /// In en, this message translates to:
  /// **'Sold'**
  String get listingStatusSold;

  /// Status label for reserved crop listing (matched with trip, pending pickup)
  ///
  /// In en, this message translates to:
  /// **'Reserved'**
  String get listingStatusReserved;

  /// Status label for expired crop listing
  ///
  /// In en, this message translates to:
  /// **'Expired'**
  String get listingStatusExpired;

  /// Filter label to show all listing statuses
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get listingFilterAll;

  /// Debug button text to create test crop listings
  ///
  /// In en, this message translates to:
  /// **'Seed Test Listings'**
  String get listingSeedTestButton;

  /// Button to navigate back to welcome/landing screen
  ///
  /// In en, this message translates to:
  /// **'Back to Home'**
  String get backToHome;

  /// Title for trip listings screen
  ///
  /// In en, this message translates to:
  /// **'Trip Listings'**
  String get tripListings;

  /// Button to create a new trip
  ///
  /// In en, this message translates to:
  /// **'Create Trip'**
  String get createTrip;

  /// Button to browse available trips
  ///
  /// In en, this message translates to:
  /// **'Browse Trips'**
  String get browseTrips;

  /// Navigation item for user's own trips
  ///
  /// In en, this message translates to:
  /// **'My Trips'**
  String get myTrips;

  /// Title for trip details screen
  ///
  /// In en, this message translates to:
  /// **'Trip Details'**
  String get tripDetails;

  /// Label for origin woreda selection
  ///
  /// In en, this message translates to:
  /// **'From (Origin)'**
  String get fromWoreda;

  /// Label for destination woreda selection
  ///
  /// In en, this message translates to:
  /// **'To (Destination)'**
  String get toWoreda;

  /// Label for departure date picker
  ///
  /// In en, this message translates to:
  /// **'Departure Date'**
  String get departureDate;

  /// Label for truck capacity input in quintals
  ///
  /// In en, this message translates to:
  /// **'Capacity (quintals)'**
  String get capacity;

  /// Label for price per quintal input in Ethiopian Birr
  ///
  /// In en, this message translates to:
  /// **'Price per Quintal (ETB)'**
  String get pricePerQuintal;

  /// Trip status label when trip is open for booking
  ///
  /// In en, this message translates to:
  /// **'Open'**
  String get tripStatusOpen;

  /// Trip status label when trip is matched with a farmer
  ///
  /// In en, this message translates to:
  /// **'Matched'**
  String get tripStatusMatched;

  /// Trip status label when shipment is in transit
  ///
  /// In en, this message translates to:
  /// **'In Transit'**
  String get tripStatusInTransit;

  /// Trip status label when shipment has been delivered
  ///
  /// In en, this message translates to:
  /// **'Delivered'**
  String get tripStatusDelivered;

  /// Trip status label when trip has been cancelled
  ///
  /// In en, this message translates to:
  /// **'Cancelled'**
  String get tripStatusCancelled;

  /// Empty state message when no trips match the filter
  ///
  /// In en, this message translates to:
  /// **'No trips found'**
  String get noTripsFound;

  /// Success message after creating a trip
  ///
  /// In en, this message translates to:
  /// **'Trip created successfully!'**
  String get tripCreatedSuccess;

  /// Success message after cancelling a trip
  ///
  /// In en, this message translates to:
  /// **'Trip cancelled'**
  String get tripCancelledSuccess;

  /// Label for route filter control
  ///
  /// In en, this message translates to:
  /// **'Filter by route'**
  String get filterByRoute;

  /// Label showing remaining truck capacity
  ///
  /// In en, this message translates to:
  /// **'Available capacity'**
  String get availableCapacity;

  /// Placeholder text for departure date picker
  ///
  /// In en, this message translates to:
  /// **'Select departure date'**
  String get selectDepartureDate;

  /// Label for trucker contact information
  ///
  /// In en, this message translates to:
  /// **'Trucker Contact'**
  String get truckerContact;

  /// Validation error when origin woreda is not selected
  ///
  /// In en, this message translates to:
  /// **'Origin woreda is required'**
  String get fromWoredaRequired;

  /// Validation error when destination woreda is not selected
  ///
  /// In en, this message translates to:
  /// **'Destination woreda is required'**
  String get toWoredaRequired;

  /// Validation error when departure date is not selected
  ///
  /// In en, this message translates to:
  /// **'Departure date is required'**
  String get departureDateRequired;

  /// Validation error when capacity is not entered
  ///
  /// In en, this message translates to:
  /// **'Capacity is required'**
  String get capacityRequired;

  /// Validation error for out-of-range capacity value
  ///
  /// In en, this message translates to:
  /// **'Capacity must be between 0.1 and 1000 quintals'**
  String get invalidCapacity;

  /// Validation error for out-of-range price value
  ///
  /// In en, this message translates to:
  /// **'Price must be between 0.01 and 10000 ETB'**
  String get invalidPrice;

  /// Validation error when origin and destination woredas are identical
  ///
  /// In en, this message translates to:
  /// **'Origin and destination cannot be the same'**
  String get sameOriginDestination;

  /// Quick action card title for finding transport/trucking services
  ///
  /// In en, this message translates to:
  /// **'Find Transport'**
  String get findTransport;

  /// Description for my listings menu item on profile screen
  ///
  /// In en, this message translates to:
  /// **'View your crop listings'**
  String get viewCropListings;

  /// Button label to cancel a trip
  ///
  /// In en, this message translates to:
  /// **'Cancel Trip'**
  String get cancelTrip;

  /// Confirmation dialog message for cancelling a trip
  ///
  /// In en, this message translates to:
  /// **'Are you sure you want to cancel this trip? This action cannot be undone.'**
  String get cancelTripConfirmation;

  /// Label for trucker name field
  ///
  /// In en, this message translates to:
  /// **'Trucker Name'**
  String get truckerName;

  /// Label for trucker phone number field
  ///
  /// In en, this message translates to:
  /// **'Trucker Phone'**
  String get truckerPhone;

  /// Label for trip route display
  ///
  /// In en, this message translates to:
  /// **'Route'**
  String get route;

  /// Description for my trips menu item on profile screen
  ///
  /// In en, this message translates to:
  /// **'View your trip listings'**
  String get viewTripListings;

  /// Trips navigation tab label
  ///
  /// In en, this message translates to:
  /// **'Trips'**
  String get trips;

  /// Validation error when trucker name is not entered
  ///
  /// In en, this message translates to:
  /// **'Trucker name is required'**
  String get truckerNameRequired;

  /// Label for vehicle license plate number field
  ///
  /// In en, this message translates to:
  /// **'Vehicle Plate'**
  String get vehiclePlate;

  /// Label for vehicle description field
  ///
  /// In en, this message translates to:
  /// **'Vehicle Description'**
  String get vehicleDescription;

  /// Deliveries navigation tab label
  ///
  /// In en, this message translates to:
  /// **'Deliveries'**
  String get deliveries;

  /// Title for the delivery list screen
  ///
  /// In en, this message translates to:
  /// **'My Deliveries'**
  String get deliveryMyDeliveries;

  /// Title for the delivery detail screen
  ///
  /// In en, this message translates to:
  /// **'Delivery Details'**
  String get deliveryDetails;

  /// Title for the QR scan screen
  ///
  /// In en, this message translates to:
  /// **'Scan QR Code'**
  String get deliveryScanQrCode;

  /// Title for the QR code display screen
  ///
  /// In en, this message translates to:
  /// **'QR Code'**
  String get deliveryShowQrCode;

  /// Title for the dispute screen
  ///
  /// In en, this message translates to:
  /// **'Raise Dispute'**
  String get deliveryRaiseDispute;

  /// Delivery status label for pending state
  ///
  /// In en, this message translates to:
  /// **'Pending'**
  String get deliveryStatusPending;

  /// Delivery status label for picked up state
  ///
  /// In en, this message translates to:
  /// **'Picked Up'**
  String get deliveryStatusPickedUp;

  /// Delivery status label for in transit state
  ///
  /// In en, this message translates to:
  /// **'In Transit'**
  String get deliveryStatusInTransit;

  /// Delivery status label for delivered state
  ///
  /// In en, this message translates to:
  /// **'Delivered'**
  String get deliveryStatusDelivered;

  /// Delivery status label for disputed state
  ///
  /// In en, this message translates to:
  /// **'Disputed'**
  String get deliveryStatusDisputed;

  /// Delivery status label for cancelled state
  ///
  /// In en, this message translates to:
  /// **'Cancelled'**
  String get deliveryStatusCancelled;

  /// Button to confirm delivery pickup
  ///
  /// In en, this message translates to:
  /// **'Confirm Pickup'**
  String get deliveryConfirmPickup;

  /// Button to confirm delivery completion
  ///
  /// In en, this message translates to:
  /// **'Confirm Delivery'**
  String get deliveryConfirmDelivery;

  /// Buyer button to scan the farmer's QR at the farm to confirm a self-pickup collection
  ///
  /// In en, this message translates to:
  /// **'Scan farmer\'s QR to collect'**
  String get deliverySelfPickupConfirm;

  /// Hint shown when the self-pickup scan is disabled because escrow is not yet funded
  ///
  /// In en, this message translates to:
  /// **'Pay into escrow first, then scan at the farm to collect.'**
  String get deliverySelfPickupPayFirst;

  /// Order detail button that opens the delivery already created for this order
  ///
  /// In en, this message translates to:
  /// **'Open delivery'**
  String get orderOpenDelivery;

  /// Buyer button to arrange self-pickup (no trucker) for an order
  ///
  /// In en, this message translates to:
  /// **'Collect it myself'**
  String get orderCollectMyself;

  /// Button to scan QR code
  ///
  /// In en, this message translates to:
  /// **'Scan QR'**
  String get deliveryScanQr;

  /// Button to display QR code
  ///
  /// In en, this message translates to:
  /// **'Show QR Code'**
  String get deliveryShowQr;

  /// Button to submit a dispute
  ///
  /// In en, this message translates to:
  /// **'Submit Dispute'**
  String get deliveryRaiseDisputeAction;

  /// Button to cancel a delivery
  ///
  /// In en, this message translates to:
  /// **'Cancel Delivery'**
  String get deliveryCancel;

  /// Button to capture photo evidence
  ///
  /// In en, this message translates to:
  /// **'Take Photo'**
  String get deliveryTakePhoto;

  /// Label for time data row in pickup/delivery sections
  ///
  /// In en, this message translates to:
  /// **'Time'**
  String get deliveryTimeLabel;

  /// Button to regenerate QR code
  ///
  /// In en, this message translates to:
  /// **'Regenerate QR'**
  String get deliveryRegenerateQr;

  /// Participant label for farmer in delivery
  ///
  /// In en, this message translates to:
  /// **'Farmer'**
  String get deliveryFarmer;

  /// Participant label for trucker in delivery
  ///
  /// In en, this message translates to:
  /// **'Trucker'**
  String get deliveryTrucker;

  /// Participant label for buyer in delivery
  ///
  /// In en, this message translates to:
  /// **'Buyer'**
  String get deliveryBuyer;

  /// GPS accuracy display with distance in meters
  ///
  /// In en, this message translates to:
  /// **'Accuracy: {meters}m'**
  String deliveryGpsAccuracy(String meters);

  /// GPS location successfully captured
  ///
  /// In en, this message translates to:
  /// **'GPS Captured'**
  String get deliveryGpsCaptured;

  /// GPS location not available
  ///
  /// In en, this message translates to:
  /// **'GPS unavailable'**
  String get deliveryGpsUnavailable;

  /// GPS location is within expected range
  ///
  /// In en, this message translates to:
  /// **'Within range'**
  String get deliveryGpsWithinRange;

  /// GPS location is outside expected range
  ///
  /// In en, this message translates to:
  /// **'Out of range'**
  String get deliveryGpsOutOfRange;

  /// GPS capture in progress
  ///
  /// In en, this message translates to:
  /// **'Capturing GPS...'**
  String get deliveryGpsCapturing;

  /// Label for GPS location data row
  ///
  /// In en, this message translates to:
  /// **'GPS'**
  String get deliveryGpsLabel;

  /// Placeholder when GPS location was not captured
  ///
  /// In en, this message translates to:
  /// **'Not captured'**
  String get deliveryGpsNotCaptured;

  /// Location permission was denied by user
  ///
  /// In en, this message translates to:
  /// **'Location permission denied'**
  String get deliveryGpsPermissionDenied;

  /// Instruction on the farmer's QR screen for a self-pickup delivery (the buyer scans it)
  ///
  /// In en, this message translates to:
  /// **'Show this QR code to the buyer when they collect'**
  String get deliveryQrShowInstructionBuyer;

  /// Instruction to show QR code to trucker
  ///
  /// In en, this message translates to:
  /// **'Show this QR code to the trucker'**
  String get deliveryQrShowInstruction;

  /// Note that QR code works without internet
  ///
  /// In en, this message translates to:
  /// **'This QR code works offline. No internet needed.'**
  String get deliveryQrOfflineNote;

  /// QR verification succeeded
  ///
  /// In en, this message translates to:
  /// **'Verification Passed'**
  String get deliveryQrVerified;

  /// QR verification failed
  ///
  /// In en, this message translates to:
  /// **'Verification Failed'**
  String get deliveryQrFailed;

  /// Instruction to point camera at QR code
  ///
  /// In en, this message translates to:
  /// **'Point camera at the QR code'**
  String get deliveryQrPointCamera;

  /// Label for dispute reason input
  ///
  /// In en, this message translates to:
  /// **'Reason for Dispute'**
  String get deliveryDisputeReason;

  /// Label for remaining dispute time
  ///
  /// In en, this message translates to:
  /// **'Time remaining'**
  String get deliveryDisputeTimeRemaining;

  /// Message when dispute window has passed
  ///
  /// In en, this message translates to:
  /// **'Dispute window expired'**
  String get deliveryDisputeExpired;

  /// Warning about dispute review process
  ///
  /// In en, this message translates to:
  /// **'Disputes are reviewed by WeredaConnect. Provide clear details.'**
  String get deliveryDisputeWarning;

  /// Button to submit dispute
  ///
  /// In en, this message translates to:
  /// **'Submit Dispute'**
  String get deliveryDisputeSubmit;

  /// Filter label to show all deliveries
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get deliveryFilterAll;

  /// Filter label for pending deliveries
  ///
  /// In en, this message translates to:
  /// **'Pending'**
  String get deliveryFilterPending;

  /// Filter label for picked up deliveries
  ///
  /// In en, this message translates to:
  /// **'Picked Up'**
  String get deliveryFilterPickedUp;

  /// Filter label for in transit deliveries
  ///
  /// In en, this message translates to:
  /// **'In Transit'**
  String get deliveryFilterInTransit;

  /// Filter label for delivered deliveries
  ///
  /// In en, this message translates to:
  /// **'Delivered'**
  String get deliveryFilterDelivered;

  /// Label for role-based delivery filter
  ///
  /// In en, this message translates to:
  /// **'Filter by Role'**
  String get deliveryFilterRole;

  /// Empty state message when no deliveries exist
  ///
  /// In en, this message translates to:
  /// **'No deliveries yet'**
  String get deliveryNoDeliveries;

  /// Hint text for empty delivery list
  ///
  /// In en, this message translates to:
  /// **'Your deliveries will appear here'**
  String get deliveryNoDeliveriesHint;

  /// Note that feature works without internet
  ///
  /// In en, this message translates to:
  /// **'Works offline'**
  String get deliveryOfflineNote;

  /// Label for delivery creation date
  ///
  /// In en, this message translates to:
  /// **'Created'**
  String get deliveryCreatedAt;

  /// Title for cancel delivery confirmation dialog
  ///
  /// In en, this message translates to:
  /// **'Cancel Delivery?'**
  String get deliveryCancelConfirmTitle;

  /// Message for cancel delivery confirmation dialog
  ///
  /// In en, this message translates to:
  /// **'Are you sure you want to cancel this delivery?'**
  String get deliveryCancelConfirmMessage;

  /// Title for submit dispute confirmation dialog
  ///
  /// In en, this message translates to:
  /// **'Submit Dispute?'**
  String get deliveryDisputeConfirmTitle;

  /// Message for submit dispute confirmation dialog
  ///
  /// In en, this message translates to:
  /// **'Are you sure you want to raise a dispute?'**
  String get deliveryDisputeConfirmMessage;

  /// Label for pickup photo evidence
  ///
  /// In en, this message translates to:
  /// **'Pickup Photo'**
  String get deliveryPhotoPickup;

  /// Label for delivery photo evidence
  ///
  /// In en, this message translates to:
  /// **'Delivery Photo'**
  String get deliveryPhotoDropoff;

  /// Button to add photo evidence
  ///
  /// In en, this message translates to:
  /// **'Add Photo'**
  String get deliveryAddPhoto;

  /// Button to mark delivery as in transit
  ///
  /// In en, this message translates to:
  /// **'Mark In Transit'**
  String get deliveryMarkInTransit;

  /// Confirmation message for marking delivery in transit
  ///
  /// In en, this message translates to:
  /// **'Are you sure you want to mark this delivery as in transit?'**
  String get deliveryInTransitConfirmMessage;

  /// Validation message for empty dispute reason
  ///
  /// In en, this message translates to:
  /// **'Please enter a reason'**
  String get deliveryReasonRequired;

  /// Validation message for too long dispute reason
  ///
  /// In en, this message translates to:
  /// **'Reason must be 500 characters or less'**
  String get deliveryReasonTooLong;

  /// Button to retry after failure
  ///
  /// In en, this message translates to:
  /// **'Try Again'**
  String get deliveryTryAgain;

  /// Delivery identifier with short ID prefix
  ///
  /// In en, this message translates to:
  /// **'Delivery #{id}'**
  String deliveryIdLabel(String id);

  /// Section header for pickup details
  ///
  /// In en, this message translates to:
  /// **'Pickup'**
  String get deliveryPickupSection;

  /// Section header for delivery details
  ///
  /// In en, this message translates to:
  /// **'Delivery'**
  String get deliveryDeliverySection;

  /// Placeholder when delivery has not been picked up yet
  ///
  /// In en, this message translates to:
  /// **'Not yet picked up'**
  String get deliveryNotYetPickedUp;

  /// Placeholder when delivery has not been delivered yet
  ///
  /// In en, this message translates to:
  /// **'Not yet delivered'**
  String get deliveryNotYetDelivered;

  /// Tooltip for camera flash toggle button
  ///
  /// In en, this message translates to:
  /// **'Toggle flash'**
  String get deliveryToggleFlash;

  /// Label showing delivery confirmation date
  ///
  /// In en, this message translates to:
  /// **'Confirmed: {date}'**
  String deliveryConfirmedAtLabel(String date);

  /// Debug button text to create a test delivery
  ///
  /// In en, this message translates to:
  /// **'Seed Test Delivery'**
  String get deliverySeedTestButton;

  /// Navigation label for escrow payments section
  ///
  /// In en, this message translates to:
  /// **'Payments'**
  String get escrowPayments;

  /// Screen title for initiating escrow payment
  ///
  /// In en, this message translates to:
  /// **'Pay for Delivery'**
  String get escrowPayForDelivery;

  /// Screen title for viewing single escrow status
  ///
  /// In en, this message translates to:
  /// **'Payment Status'**
  String get escrowPaymentStatus;

  /// Screen title for payment history list
  ///
  /// In en, this message translates to:
  /// **'My Payments'**
  String get escrowPaymentHistory;

  /// Screen title for raising an escrow dispute
  ///
  /// In en, this message translates to:
  /// **'Raise Dispute'**
  String get escrowRaiseDispute;

  /// Status label when escrow payment is initiated but not yet held
  ///
  /// In en, this message translates to:
  /// **'Payment Pending'**
  String get escrowStatusInitiated;

  /// Status label when funds are held in escrow
  ///
  /// In en, this message translates to:
  /// **'Funds Held Safely'**
  String get escrowStatusEscrowed;

  /// Status label when funds have been released to farmer
  ///
  /// In en, this message translates to:
  /// **'Funds Released'**
  String get escrowStatusReleased;

  /// Status label when escrow transaction is fully settled
  ///
  /// In en, this message translates to:
  /// **'Payment Complete'**
  String get escrowStatusCompleted;

  /// Status label when escrow payment was cancelled
  ///
  /// In en, this message translates to:
  /// **'Cancelled'**
  String get escrowStatusCancelled;

  /// Status label when escrow payment is under dispute
  ///
  /// In en, this message translates to:
  /// **'Under Dispute'**
  String get escrowStatusDisputed;

  /// Status label when escrow payment has been refunded
  ///
  /// In en, this message translates to:
  /// **'Refunded'**
  String get escrowStatusRefunded;

  /// Trust-building message explaining escrow protection
  ///
  /// In en, this message translates to:
  /// **'Your payment is held safely until delivery is confirmed.'**
  String get escrowProtectionInfo;

  /// Label for total payment amount
  ///
  /// In en, this message translates to:
  /// **'Total Amount'**
  String get escrowTotalAmount;

  /// Label for platform commission with percentage rate
  ///
  /// In en, this message translates to:
  /// **'Commission ({rate}%)'**
  String escrowCommission(String rate);

  /// Label for net amount farmer will receive after commission
  ///
  /// In en, this message translates to:
  /// **'Farmer Receives'**
  String get escrowFarmerReceives;

  /// Label for the transport leg the trucker receives from the escrow
  ///
  /// In en, this message translates to:
  /// **'Trucker Receives'**
  String get escrowTruckerReceives;

  /// Button label to confirm and initiate escrow payment
  ///
  /// In en, this message translates to:
  /// **'Confirm Payment'**
  String get escrowConfirmPayment;

  /// Confirmation dialog message before payment
  ///
  /// In en, this message translates to:
  /// **'Pay ETB {amount} to {farmerName}?'**
  String escrowConfirmPaymentDialog(String amount, String farmerName);

  /// Warning shown when payment amount exceeds Fayda ID threshold
  ///
  /// In en, this message translates to:
  /// **'Fayda ID verification required for amounts above ETB 10,000.'**
  String get escrowFaydaWarning;

  /// Info text explaining escrow fund holding period
  ///
  /// In en, this message translates to:
  /// **'Funds held for 7 days maximum. Refunded if not delivered.'**
  String get escrowFundsHeldInfo;

  /// Message shown when payment intent is queued offline
  ///
  /// In en, this message translates to:
  /// **'Payment queued. Will process when you have internet.'**
  String get escrowPaymentQueued;

  /// Success message after payment is initiated
  ///
  /// In en, this message translates to:
  /// **'Payment initiated successfully.'**
  String get escrowPaymentSuccess;

  /// Shown when the Telebirr payment page is being opened
  ///
  /// In en, this message translates to:
  /// **'Opening Telebirr…'**
  String get escrowPaymentOpening;

  /// Shown when the Telebirr H5 checkout URL could not be launched
  ///
  /// In en, this message translates to:
  /// **'Could not open the payment page. Make sure Telebirr or a browser is installed.'**
  String get escrowPaymentOpenFailed;

  /// Shown when an awaited Telebirr payment is confirmed (escrowed)
  ///
  /// In en, this message translates to:
  /// **'Payment confirmed. Funds are held safely.'**
  String get escrowPaymentConfirmed;

  /// Banner shown while polling for async Telebirr payment confirmation
  ///
  /// In en, this message translates to:
  /// **'Waiting for your Telebirr payment to be confirmed…'**
  String get escrowAwaitingPayment;

  /// Button to re-open the Telebirr checkout while awaiting payment
  ///
  /// In en, this message translates to:
  /// **'Open Telebirr payment'**
  String get escrowOpenPaymentAgain;

  /// Label for delivery reference on escrow status screen
  ///
  /// In en, this message translates to:
  /// **'Delivery'**
  String get escrowDeliveryLabel;

  /// Label for farmer name on escrow status screen
  ///
  /// In en, this message translates to:
  /// **'Farmer'**
  String get escrowFarmerLabel;

  /// Label for buyer name on escrow status screen
  ///
  /// In en, this message translates to:
  /// **'Buyer'**
  String get escrowBuyerLabel;

  /// Label for escrow creation date
  ///
  /// In en, this message translates to:
  /// **'Created'**
  String get escrowCreatedLabel;

  /// Label for escrow expiration date
  ///
  /// In en, this message translates to:
  /// **'Expires'**
  String get escrowExpiresLabel;

  /// Label for Telebirr transaction reference number
  ///
  /// In en, this message translates to:
  /// **'Telebirr Ref'**
  String get escrowTelebirrRefLabel;

  /// Warning shown when cached escrow data may be stale
  ///
  /// In en, this message translates to:
  /// **'Status may be outdated. Pull to refresh.'**
  String get escrowMayBeOutdated;

  /// Label showing when escrow data was last refreshed
  ///
  /// In en, this message translates to:
  /// **'Last updated {time}'**
  String escrowLastUpdated(String time);

  /// Filter chip label to show all escrow payments
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get escrowFilterAll;

  /// Filter chip to show payments where user is buyer
  ///
  /// In en, this message translates to:
  /// **'As Buyer'**
  String get escrowFilterBuyer;

  /// Filter chip to show payments where user is farmer
  ///
  /// In en, this message translates to:
  /// **'As Farmer'**
  String get escrowFilterFarmer;

  /// Section header for pending payment intents with count
  ///
  /// In en, this message translates to:
  /// **'Pending ({count})'**
  String escrowPendingSection(int count);

  /// Section header for confirmed payment history
  ///
  /// In en, this message translates to:
  /// **'History'**
  String get escrowHistorySection;

  /// Empty state message when user has no payment history
  ///
  /// In en, this message translates to:
  /// **'No payments yet.'**
  String get escrowNoPayments;

  /// Empty state when no payments match the active filter
  ///
  /// In en, this message translates to:
  /// **'No matching payments.'**
  String get escrowNoMatchingPayments;

  /// Button to cancel a queued payment intent
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get escrowCancelIntent;

  /// Confirmation dialog for cancelling a queued payment intent
  ///
  /// In en, this message translates to:
  /// **'Cancel this queued payment?'**
  String get escrowCancelIntentDialog;

  /// Success message after cancelling a queued payment intent
  ///
  /// In en, this message translates to:
  /// **'Queued payment cancelled.'**
  String get escrowIntentCancelled;

  /// Countdown showing remaining time to raise a dispute
  ///
  /// In en, this message translates to:
  /// **'Time remaining: {hours}h {minutes}m'**
  String escrowDisputeTimeRemaining(int hours, int minutes);

  /// Message when the 48-hour dispute window has passed
  ///
  /// In en, this message translates to:
  /// **'Dispute window expired.'**
  String get escrowDisputeWindowExpired;

  /// Label for dispute reason selection
  ///
  /// In en, this message translates to:
  /// **'Reason'**
  String get escrowDisputeReasonLabel;

  /// Label for optional dispute description text field
  ///
  /// In en, this message translates to:
  /// **'Description (optional)'**
  String get escrowDisputeDescriptionLabel;

  /// Hint text for dispute description input field
  ///
  /// In en, this message translates to:
  /// **'Describe the issue...'**
  String get escrowDisputeDescriptionHint;

  /// Button to submit a dispute on an escrow payment
  ///
  /// In en, this message translates to:
  /// **'Submit Dispute'**
  String get escrowDisputeSubmit;

  /// Confirmation dialog before submitting a dispute
  ///
  /// In en, this message translates to:
  /// **'This will freeze funds pending review. Continue?'**
  String get escrowDisputeConfirmDialog;

  /// Success message after dispute is submitted
  ///
  /// In en, this message translates to:
  /// **'Dispute registered. Platform will review within 48 hours.'**
  String get escrowDisputeSuccess;

  /// Info text about dispute review process
  ///
  /// In en, this message translates to:
  /// **'Disputes are reviewed by the platform within 48 hours.'**
  String get escrowDisputeReviewInfo;

  /// Error message when user tries to raise dispute while offline
  ///
  /// In en, this message translates to:
  /// **'Internet connection required to raise disputes.'**
  String get escrowDisputeRequiresInternet;

  /// Dispute reason: goods were damaged
  ///
  /// In en, this message translates to:
  /// **'Damaged goods'**
  String get escrowReasonDamagedGoods;

  /// Dispute reason: received wrong quantity
  ///
  /// In en, this message translates to:
  /// **'Wrong quantity'**
  String get escrowReasonWrongQuantity;

  /// Dispute reason: received wrong crop type
  ///
  /// In en, this message translates to:
  /// **'Wrong crop type'**
  String get escrowReasonWrongCrop;

  /// Dispute reason: goods were not delivered
  ///
  /// In en, this message translates to:
  /// **'Not delivered'**
  String get escrowReasonNotDelivered;

  /// Dispute reason: quality does not match listing
  ///
  /// In en, this message translates to:
  /// **'Quality mismatch'**
  String get escrowReasonQualityMismatch;

  /// Dispute reason: other reason not listed
  ///
  /// In en, this message translates to:
  /// **'Other'**
  String get escrowReasonOther;

  /// Error when payment amount is outside allowed range
  ///
  /// In en, this message translates to:
  /// **'Amount must be between ETB 100 and ETB 500,000.'**
  String get escrowErrorInvalidAmount;

  /// Error when Fayda ID is required but not verified
  ///
  /// In en, this message translates to:
  /// **'Fayda ID verification required for amounts above ETB 10,000.'**
  String get escrowErrorFaydaRequired;

  /// Error when escrow payment already exists for delivery
  ///
  /// In en, this message translates to:
  /// **'Payment already exists for this delivery.'**
  String get escrowErrorAlreadyExists;

  /// Error when the buyer tries to pay again for an order whose money is already held in escrow (ESCROW_024)
  ///
  /// In en, this message translates to:
  /// **'Payment for this order is already held in escrow.'**
  String get escrowErrorOrderAlreadyFunded;

  /// Error when Telebirr gateway returns an error
  ///
  /// In en, this message translates to:
  /// **'Telebirr service error. Please try again.'**
  String get escrowErrorGateway;

  /// Error when Telebirr service is down
  ///
  /// In en, this message translates to:
  /// **'Telebirr is currently unavailable. Please try later.'**
  String get escrowErrorServiceUnavailable;

  /// Generic fallback error message for escrow operations
  ///
  /// In en, this message translates to:
  /// **'Something went wrong. Please try again.'**
  String get escrowErrorGeneric;

  /// Error when authentication is required for escrow operation
  ///
  /// In en, this message translates to:
  /// **'Please log in to continue.'**
  String get escrowErrorAuthRequired;

  /// Error when a duplicate/conflicting escrow request is detected
  ///
  /// In en, this message translates to:
  /// **'This request is already being processed.'**
  String get escrowErrorConflict;

  /// Error when escrow rate limit is exceeded
  ///
  /// In en, this message translates to:
  /// **'Too many requests. Please wait and try again.'**
  String get escrowErrorRateLimit;

  /// Error when escrow action requires internet but device is offline
  ///
  /// In en, this message translates to:
  /// **'Internet connection required for this action.'**
  String get escrowErrorOffline;

  /// Error when delivery ID is missing or invalid for escrow
  ///
  /// In en, this message translates to:
  /// **'Invalid delivery reference.'**
  String get escrowErrorInvalidDelivery;

  /// Error when user is not authorized for escrow action
  ///
  /// In en, this message translates to:
  /// **'You are not authorized for this action.'**
  String get escrowErrorUnauthorized;

  /// Error when the referenced delivery does not exist
  ///
  /// In en, this message translates to:
  /// **'Delivery not found.'**
  String get escrowErrorDeliveryNotFound;

  /// Error when delivery status does not allow escrow initiation
  ///
  /// In en, this message translates to:
  /// **'Delivery is not in a valid state for payment.'**
  String get escrowErrorInvalidDeliveryState;

  /// Error when dispute reason is invalid
  ///
  /// In en, this message translates to:
  /// **'Please select a valid dispute reason.'**
  String get escrowErrorInvalidDisputeReason;

  /// Error when dispute is raised after 48-hour window
  ///
  /// In en, this message translates to:
  /// **'The dispute window has expired (48 hours after delivery).'**
  String get escrowErrorDisputeWindowExpired;

  /// Error when a duplicate dispute is attempted
  ///
  /// In en, this message translates to:
  /// **'A dispute has already been raised for this payment.'**
  String get escrowErrorDisputeAlreadyExists;

  /// Error when a dispute is raised on a payment an admin is already paying out by hand (ESCROW_038)
  ///
  /// In en, this message translates to:
  /// **'This payment is already being paid out by an admin, so it can no longer be disputed. Contact support.'**
  String get escrowErrorPayoutInProgress;

  /// Error when escrow state transition is invalid
  ///
  /// In en, this message translates to:
  /// **'This action is not allowed in the current payment state.'**
  String get escrowErrorInvalidTransition;

  /// Formatted ETB currency amount
  ///
  /// In en, this message translates to:
  /// **'ETB {amount}'**
  String escrowEtbAmount(String amount);

  /// Message shown when user is not authenticated — prompt to restart
  ///
  /// In en, this message translates to:
  /// **'Please restart the app'**
  String get profileRestartApp;

  /// Error message when profile data cannot be loaded from the database
  ///
  /// In en, this message translates to:
  /// **'Could not load user profile'**
  String get profileLoadError;

  /// Section heading for escrow event timeline on status screen
  ///
  /// In en, this message translates to:
  /// **'Timeline'**
  String get escrowTimeline;

  /// Section heading for dispute info card on escrow status screen
  ///
  /// In en, this message translates to:
  /// **'Dispute Details'**
  String get escrowDisputeDetailsTitle;

  /// Label shown while recording audio
  ///
  /// In en, this message translates to:
  /// **'Recording...'**
  String get recording;

  /// Button to stop audio recording
  ///
  /// In en, this message translates to:
  /// **'Stop Recording'**
  String get stopRecording;

  /// Button to play audio
  ///
  /// In en, this message translates to:
  /// **'Play'**
  String get playAudio;

  /// Button to pause audio playback
  ///
  /// In en, this message translates to:
  /// **'Pause'**
  String get pauseAudio;

  /// Button to delete a voice note recording
  ///
  /// In en, this message translates to:
  /// **'Delete Recording'**
  String get deleteRecording;

  /// Confirmation dialog for deleting a voice note
  ///
  /// In en, this message translates to:
  /// **'Delete this voice note?'**
  String get deleteRecordingConfirm;

  /// Error when microphone permission is denied
  ///
  /// In en, this message translates to:
  /// **'Microphone permission is required to record audio'**
  String get microphonePermissionRequired;

  /// Success message after recording completes
  ///
  /// In en, this message translates to:
  /// **'Voice note recorded'**
  String get audioRecorded;

  /// Warning when recording reaches max duration
  ///
  /// In en, this message translates to:
  /// **'Maximum recording length is 30 seconds'**
  String get audioTooLong;

  /// Hint text shown below the record button indicating the recording time limit
  ///
  /// In en, this message translates to:
  /// **'Maximum 30 seconds'**
  String get audioRecordingLimit;

  /// Button label to re-record a voice note
  ///
  /// In en, this message translates to:
  /// **'Re-record'**
  String get reRecord;

  /// Error message when recording fails
  ///
  /// In en, this message translates to:
  /// **'Recording failed. Please try again.'**
  String get audioRecordingFailed;

  /// Abbreviated seconds display for duration
  ///
  /// In en, this message translates to:
  /// **'{count}s'**
  String secondsAbbreviation(int count);

  /// Label for bytes downloaded counter
  ///
  /// In en, this message translates to:
  /// **'Downloaded'**
  String get dataUsageDownloaded;

  /// Label for bytes uploaded counter
  ///
  /// In en, this message translates to:
  /// **'Uploaded'**
  String get dataUsageUploaded;

  /// Label for combined total bytes
  ///
  /// In en, this message translates to:
  /// **'Total'**
  String get dataUsageTotal;

  /// Button to reset data usage statistics
  ///
  /// In en, this message translates to:
  /// **'Reset'**
  String get dataUsageReset;

  /// Confirmation dialog message before resetting stats
  ///
  /// In en, this message translates to:
  /// **'Clear all data usage statistics?'**
  String get dataUsageResetConfirm;

  /// Label prefix shown before the tracking start date
  ///
  /// In en, this message translates to:
  /// **'Tracking since'**
  String get dataUsageTrackingSince;

  /// Explains that data usage counts only this account's API traffic on this device
  ///
  /// In en, this message translates to:
  /// **'Counts this account\'s requests to WeredaConnect servers from this device. Your phone\'s total data use (app updates, images, other apps) may be higher. Sign-in traffic is not included.'**
  String get dataUsageScopeNote;

  /// Title shown on conflict resolution modal when server overwrites local data
  ///
  /// In en, this message translates to:
  /// **'Data Updated'**
  String get conflictTitle;

  /// Explanation of what happened during a sync conflict
  ///
  /// In en, this message translates to:
  /// **'Another device or the server updated this record. Your local copy has been updated to match.'**
  String get conflictExplanation;

  /// Primary button to acknowledge the conflict resolution
  ///
  /// In en, this message translates to:
  /// **'OK, Got It'**
  String get conflictAccept;

  /// Secondary button to dismiss the conflict notification
  ///
  /// In en, this message translates to:
  /// **'Dismiss'**
  String get conflictDismiss;

  /// Entity type label for crop listing in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Crop Listing'**
  String get conflictEntityCropListing;

  /// Entity type label for delivery in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Delivery'**
  String get conflictEntityDelivery;

  /// Entity type label for trip in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Trip'**
  String get conflictEntityTrip;

  /// Entity type label for order in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Order'**
  String get conflictEntityOrder;

  /// Entity type label for rating in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Rating'**
  String get conflictEntityRating;

  /// Entity type label for payment/escrow in conflict dialog
  ///
  /// In en, this message translates to:
  /// **'Payment'**
  String get conflictEntityPayment;

  /// Section header showing server-side field values
  ///
  /// In en, this message translates to:
  /// **'Updated values from server:'**
  String get conflictServerValues;

  /// Shown when conflict event has no displayable field changes
  ///
  /// In en, this message translates to:
  /// **'No details available.'**
  String get conflictNoDetails;

  /// Banner text for single conflict notification
  ///
  /// In en, this message translates to:
  /// **'Your {entityType} was updated on another device'**
  String conflictBannerSingle(String entityType);

  /// Banner text for multiple conflict notifications
  ///
  /// In en, this message translates to:
  /// **'{count} items were updated on another device'**
  String conflictBannerMultiple(int count);

  /// Action button text on conflict notification banner
  ///
  /// In en, this message translates to:
  /// **'View Changes'**
  String get conflictBannerAction;

  /// App bar title for conflict resolution screen
  ///
  /// In en, this message translates to:
  /// **'Updates from Sync'**
  String get conflictScreenTitle;

  /// Label for user's local version in conflict diff
  ///
  /// In en, this message translates to:
  /// **'Your version'**
  String get conflictYourVersion;

  /// Label for server's current version in conflict diff
  ///
  /// In en, this message translates to:
  /// **'Current version'**
  String get conflictCurrentVersion;

  /// Badge text confirming server version was applied
  ///
  /// In en, this message translates to:
  /// **'Server version applied'**
  String get conflictServerApplied;

  /// Primary button to acknowledge and dismiss all conflicts
  ///
  /// In en, this message translates to:
  /// **'Got It'**
  String get conflictGotIt;

  /// Empty state title when no conflicts exist
  ///
  /// In en, this message translates to:
  /// **'Everything is up to date'**
  String get conflictEmptyTitle;

  /// Empty state subtitle when no conflicts exist
  ///
  /// In en, this message translates to:
  /// **'No changes from other devices.'**
  String get conflictEmptySubtitle;

  /// Message when a conflicting entity was deleted on server
  ///
  /// In en, this message translates to:
  /// **'This {entityType} was removed on another device'**
  String conflictEntityDeleted(String entityType);

  /// Expandable button to show all field changes when more than 4 fields changed
  ///
  /// In en, this message translates to:
  /// **'Show all changes'**
  String get conflictShowAllChanges;

  /// Error when duplicate crop listing detected by server fraud check
  ///
  /// In en, this message translates to:
  /// **'A similar listing already exists. Please update your existing listing instead.'**
  String get fraudDuplicateListing;

  /// Error when the hourly listing limit is exceeded; minutes = time until the limit resets at the next UTC hour
  ///
  /// In en, this message translates to:
  /// **'Too many listings created. You can try again in {minutes, plural, =1{1 minute} other{{minutes} minutes}}.'**
  String fraudVelocityExceeded(int minutes);

  /// Snackbar message after profile is saved successfully
  ///
  /// In en, this message translates to:
  /// **'Profile updated'**
  String get profileSaved;

  /// Empty state title on notifications screen
  ///
  /// In en, this message translates to:
  /// **'No notifications yet'**
  String get noNotificationsYet;

  /// Empty state subtitle on notifications screen
  ///
  /// In en, this message translates to:
  /// **'You\'ll see your alerts here'**
  String get noNotificationsDescription;

  /// Button label and screen title for creating a new delivery
  ///
  /// In en, this message translates to:
  /// **'Create Delivery'**
  String get createDelivery;

  /// Heading above the payment method options on the order payment screen
  ///
  /// In en, this message translates to:
  /// **'Select payment method'**
  String get selectPaymentMethod;

  /// CBE payment method option label
  ///
  /// In en, this message translates to:
  /// **'Commercial Bank of Ethiopia (CBE)'**
  String get paymentMethodCbe;

  /// Telebirr payment method option label
  ///
  /// In en, this message translates to:
  /// **'Telebirr'**
  String get paymentMethodTelebirr;

  /// Notice explaining that payment is mocked/simulated
  ///
  /// In en, this message translates to:
  /// **'Demo mode: payments are simulated. No real transfer is made.'**
  String get mockPaymentNotice;

  /// Instructions shown when Telebirr is the selected (mocked) payment method
  ///
  /// In en, this message translates to:
  /// **'Tap Submit Payment to simulate a Telebirr payment for this order.'**
  String get telebirrPayInstructions;

  /// Label for the optional CBE transaction id field in demo mode
  ///
  /// In en, this message translates to:
  /// **'CBE Transaction ID (optional)'**
  String get transactionIdOptional;

  /// Copy action tooltip
  ///
  /// In en, this message translates to:
  /// **'Copy'**
  String get copy;

  /// Call action button label
  ///
  /// In en, this message translates to:
  /// **'Call'**
  String get call;

  /// Close action button label
  ///
  /// In en, this message translates to:
  /// **'Close'**
  String get close;

  /// Label for the farmer's phone number
  ///
  /// In en, this message translates to:
  /// **'Farmer Phone'**
  String get farmerPhone;

  /// Shown when a farmer's phone number is not available
  ///
  /// In en, this message translates to:
  /// **'Phone number not available'**
  String get phoneNotAvailable;

  /// Button to rate the farmer after a delivery
  ///
  /// In en, this message translates to:
  /// **'Rate Farmer'**
  String get rateFarmer;

  /// Button to rate the trucker after a delivery
  ///
  /// In en, this message translates to:
  /// **'Rate Trucker'**
  String get rateTrucker;

  /// Submit button in the rating dialog
  ///
  /// In en, this message translates to:
  /// **'Submit Rating'**
  String get submitRating;

  /// Hint for the optional rating comment field
  ///
  /// In en, this message translates to:
  /// **'Add a comment (optional)'**
  String get ratingCommentHint;

  /// Snackbar shown after submitting a rating
  ///
  /// In en, this message translates to:
  /// **'Thank you for your rating!'**
  String get ratingThankYou;

  /// Label shown when a counterparty has already been rated
  ///
  /// In en, this message translates to:
  /// **'Rated'**
  String get alreadyRated;

  /// Shown when a user has no ratings yet
  ///
  /// In en, this message translates to:
  /// **'No ratings yet'**
  String get noRatingsYet;

  /// Label for the farmer's aggregate rating
  ///
  /// In en, this message translates to:
  /// **'Farmer Rating'**
  String get farmerRating;

  /// Number of ratings a user has received
  ///
  /// In en, this message translates to:
  /// **'{count} ratings'**
  String ratingsCount(int count);

  /// Button to contact the farmer about a listing
  ///
  /// In en, this message translates to:
  /// **'Contact Farmer'**
  String get contactFarmer;

  /// Label for quantity row in listing detail view
  ///
  /// In en, this message translates to:
  /// **'Quantity'**
  String get detailQuantity;

  /// Label for price row in listing detail view
  ///
  /// In en, this message translates to:
  /// **'Price'**
  String get detailPrice;

  /// Label for creation date row in detail view
  ///
  /// In en, this message translates to:
  /// **'Created'**
  String get detailCreated;

  /// Hint text for minimum price filter input
  ///
  /// In en, this message translates to:
  /// **'Min'**
  String get filterMin;

  /// Hint text for maximum price filter input
  ///
  /// In en, this message translates to:
  /// **'Max'**
  String get filterMax;

  /// Section header for trip selection in create delivery screen
  ///
  /// In en, this message translates to:
  /// **'Select Trip'**
  String get selectTrip;

  /// Section header for buyer info in create delivery screen
  ///
  /// In en, this message translates to:
  /// **'Buyer Information'**
  String get buyerInformation;

  /// Input label for buyer name field
  ///
  /// In en, this message translates to:
  /// **'Buyer Name'**
  String get buyerNameLabel;

  /// Input label for buyer phone or ID field
  ///
  /// In en, this message translates to:
  /// **'Buyer Phone / ID'**
  String get buyerPhoneIdLabel;

  /// Validation error when buyer name is empty
  ///
  /// In en, this message translates to:
  /// **'Buyer name is required'**
  String get buyerNameRequired;

  /// Validation error when buyer phone/ID is empty
  ///
  /// In en, this message translates to:
  /// **'Buyer phone or ID is required'**
  String get buyerIdRequired;

  /// Validation error when no trip is selected
  ///
  /// In en, this message translates to:
  /// **'Please select a trip'**
  String get tripSelectionRequired;

  /// Dropdown hint for trip selection
  ///
  /// In en, this message translates to:
  /// **'Choose a trip'**
  String get chooseTripHint;

  /// Warning when trucker has no open trips for delivery creation
  ///
  /// In en, this message translates to:
  /// **'No open trips available. Create a trip first.'**
  String get noOpenTrips;

  /// Success message after creating a delivery
  ///
  /// In en, this message translates to:
  /// **'Delivery created successfully'**
  String get deliveryCreatedSuccess;

  /// Title for farmer contact info bottom sheet
  ///
  /// In en, this message translates to:
  /// **'Farmer Contact'**
  String get farmerContactInfo;

  /// Snackbar message after copying text to clipboard
  ///
  /// In en, this message translates to:
  /// **'Copied to clipboard'**
  String get copiedToClipboard;

  /// Message shown in forgot password dialog with support contact info
  ///
  /// In en, this message translates to:
  /// **'Please contact support at +251-111-234567 to reset your password.'**
  String get forgotPasswordContactSupport;

  /// Countdown text for OTP resend button
  ///
  /// In en, this message translates to:
  /// **'Resend in {seconds}s'**
  String resendOtpIn(int seconds);

  /// Snackbar message after OTP is resent
  ///
  /// In en, this message translates to:
  /// **'OTP resent successfully'**
  String get otpResent;

  /// Generic OK button label
  ///
  /// In en, this message translates to:
  /// **'OK'**
  String get ok;

  /// Snackbar message shown when user taps How it works chip on welcome screen
  ///
  /// In en, this message translates to:
  /// **'Tutorial carousel coming soon. You can explore the app by creating an account!'**
  String get tutorialComingSoon;

  /// Status description for initiated escrow payment
  ///
  /// In en, this message translates to:
  /// **'Waiting for Telebirr confirmation'**
  String get escrowDescInitiated;

  /// Status description for escrowed payment
  ///
  /// In en, this message translates to:
  /// **'Funds held safely until delivery'**
  String get escrowDescEscrowed;

  /// Status description for released payment
  ///
  /// In en, this message translates to:
  /// **'Funds sent to farmer'**
  String get escrowDescReleased;

  /// Status description for completed escrow
  ///
  /// In en, this message translates to:
  /// **'Transaction settled'**
  String get escrowDescCompleted;

  /// Status description for cancelled escrow
  ///
  /// In en, this message translates to:
  /// **'Payment was cancelled'**
  String get escrowDescCancelled;

  /// Status description for disputed escrow
  ///
  /// In en, this message translates to:
  /// **'Under review by platform'**
  String get escrowDescDisputed;

  /// Status description for refunded escrow
  ///
  /// In en, this message translates to:
  /// **'Funds returned to buyer'**
  String get escrowDescRefunded;

  /// Status label for an escrowed hold whose refund is on its way (order cancelled or hold expired)
  ///
  /// In en, this message translates to:
  /// **'Refund Pending'**
  String get escrowStatusRefundPending;

  /// Status description for an escrowed hold whose refund is on its way
  ///
  /// In en, this message translates to:
  /// **'Order cancelled. Funds are being returned to the buyer'**
  String get escrowDescRefundPending;

  /// Short order reference with ID, shown when an escrow has no delivery yet
  ///
  /// In en, this message translates to:
  /// **'Order #{id}'**
  String escrowOrderShort(String id);

  /// Field label for crop type in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Crop Type'**
  String get conflictFieldCropType;

  /// Field label for quantity in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Quantity'**
  String get conflictFieldQuantity;

  /// Field label for price per kg in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Price/kg'**
  String get conflictFieldPricePerKg;

  /// Field label for status in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Status'**
  String get conflictFieldStatus;

  /// Field label for route in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Route'**
  String get conflictFieldRoute;

  /// Field label for capacity in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Capacity'**
  String get conflictFieldCapacity;

  /// Field label for trip in conflict resolution
  ///
  /// In en, this message translates to:
  /// **'Trip'**
  String get conflictFieldTrip;

  /// Button label for payment confirmation dialog
  ///
  /// In en, this message translates to:
  /// **'Pay'**
  String get escrowPayButton;

  /// Prefix label for dispute reason display
  ///
  /// In en, this message translates to:
  /// **'Reason: {reason}'**
  String escrowDisputeReasonPrefix(String reason);

  /// Delivery reference with ID
  ///
  /// In en, this message translates to:
  /// **'Delivery #{id}'**
  String escrowDeliveryNumber(String id);

  /// Payment recipient label
  ///
  /// In en, this message translates to:
  /// **'To: {name}'**
  String escrowPaymentTo(String name);

  /// Payment amount display
  ///
  /// In en, this message translates to:
  /// **'Payment: ETB {amount}'**
  String escrowPaymentAmount(String amount);

  /// Short delivery reference with ID
  ///
  /// In en, this message translates to:
  /// **'Del. #{id}'**
  String escrowDeliveryShort(String id);

  /// Status label for queued payment intent
  ///
  /// In en, this message translates to:
  /// **'Queued'**
  String get escrowQueuedStatus;

  /// Generic error message shown when an unexpected error occurs
  ///
  /// In en, this message translates to:
  /// **'Something went wrong. Please try again.'**
  String get genericError;

  /// Fallback error for failed registration
  ///
  /// In en, this message translates to:
  /// **'Registration failed. Please try again.'**
  String get authErrorRegistrationFailed;

  /// Fallback error for failed OTP verification
  ///
  /// In en, this message translates to:
  /// **'OTP verification failed. Please try again.'**
  String get authErrorOtpFailed;

  /// Fallback error for failed OTP resend
  ///
  /// In en, this message translates to:
  /// **'Failed to resend OTP. Please try again.'**
  String get authErrorResendOtpFailed;

  /// Fallback error for failed login
  ///
  /// In en, this message translates to:
  /// **'Login failed. Please try again.'**
  String get authErrorLoginFailed;

  /// Fallback error for failed offline login
  ///
  /// In en, this message translates to:
  /// **'Offline login failed. Please log in online first.'**
  String get authErrorOfflineLoginFailed;

  /// Error accessing secure storage
  ///
  /// In en, this message translates to:
  /// **'Error accessing secure storage. Please restart the app.'**
  String get authErrorSecureStorage;

  /// Error accessing database
  ///
  /// In en, this message translates to:
  /// **'Database error. Please restart the app.'**
  String get authErrorDatabase;

  /// Error when user has not confirmed phone number
  ///
  /// In en, this message translates to:
  /// **'Please verify your phone number first.'**
  String get authErrorUserNotConfirmed;

  /// Error when authentication token has expired
  ///
  /// In en, this message translates to:
  /// **'Session expired. Please log in again.'**
  String get authErrorTokenExpired;

  /// Error when delivery ID format is invalid
  ///
  /// In en, this message translates to:
  /// **'Invalid delivery ID'**
  String get deliveryErrorInvalidId;

  /// Error when delivery status does not allow the requested action
  ///
  /// In en, this message translates to:
  /// **'Invalid delivery status for this action'**
  String get deliveryErrorStatusInvalid;

  /// Error when user is not authorized for a delivery action
  ///
  /// In en, this message translates to:
  /// **'You are not authorized for this action'**
  String get deliveryErrorUnauthorized;

  /// Error when dispute window (48 hours) has passed
  ///
  /// In en, this message translates to:
  /// **'Dispute window has expired'**
  String get deliveryErrorDisputeExpired;

  /// Error when dispute reason is missing or invalid
  ///
  /// In en, this message translates to:
  /// **'Please provide a reason'**
  String get deliveryErrorDisputeReasonRequired;

  /// Error when GPS coordinates are invalid
  ///
  /// In en, this message translates to:
  /// **'Invalid GPS coordinates'**
  String get deliveryErrorGpsInvalid;

  /// Generic delivery error message
  ///
  /// In en, this message translates to:
  /// **'Delivery operation failed'**
  String get deliveryErrorGeneric;

  /// Error when delivery cannot be found
  ///
  /// In en, this message translates to:
  /// **'Delivery not found'**
  String get deliveryErrorNotFound;

  /// Pickup/delivery confirmation postponed because the farmer's QR nonce has not synced
  ///
  /// In en, this message translates to:
  /// **'The farmer\'s QR code has not reached the server yet. Ask the farmer to open the QR code while online, then try again.'**
  String get deliveryErrorQrPending;

  /// A delivery cancel refused because the crops were already picked up (only a delivery that is not picked up yet can be cancelled)
  ///
  /// In en, this message translates to:
  /// **'This delivery was already picked up, so it can no longer be cancelled. Contact support if there is a problem.'**
  String get deliveryErrorCancelNotAllowed;

  /// A delivery dispute the server refused (the payment could not be frozen: an admin is paying it out, it is being settled, it is no longer held or it is not on record); nothing was changed. Neutral on purpose: the same text covers every reason
  ///
  /// In en, this message translates to:
  /// **'This dispute can\'t be opened right now. Contact support.'**
  String get deliveryErrorDisputeRefused;

  /// Error when scanned QR code exceeds size limit
  ///
  /// In en, this message translates to:
  /// **'QR code too large'**
  String get deliveryQrTooLarge;

  /// Error when QR code has missing fields
  ///
  /// In en, this message translates to:
  /// **'Incomplete QR data'**
  String get deliveryQrIncomplete;

  /// Error when QR code fields exceed length limits
  ///
  /// In en, this message translates to:
  /// **'Invalid QR data'**
  String get deliveryQrInvalidData;

  /// Error when QR code deliveryId does not match expected delivery
  ///
  /// In en, this message translates to:
  /// **'QR code does not match this delivery'**
  String get deliveryQrWrongDelivery;

  /// Error when QR code cannot be decoded
  ///
  /// In en, this message translates to:
  /// **'Invalid QR code format'**
  String get deliveryQrInvalidFormat;

  /// Error when QR code processing fails unexpectedly
  ///
  /// In en, this message translates to:
  /// **'Could not process QR code'**
  String get deliveryQrProcessFailed;

  /// Error when escrow payment cannot be found
  ///
  /// In en, this message translates to:
  /// **'Payment not found'**
  String get escrowPaymentNotFound;

  /// Error when escrow payment for a specific delivery cannot be found
  ///
  /// In en, this message translates to:
  /// **'Payment not found for this delivery'**
  String get escrowPaymentNotFoundForDelivery;

  /// Loading indicator text shown during auth operations on slow networks
  ///
  /// In en, this message translates to:
  /// **'Please wait...'**
  String get pleaseWait;

  /// Greeting word used in home header
  ///
  /// In en, this message translates to:
  /// **'Selam'**
  String get greeting;

  /// Subtitle under greeting on home screen
  ///
  /// In en, this message translates to:
  /// **'What would you like to do today?'**
  String get homeSubtitle;

  /// Main headline on home banner card
  ///
  /// In en, this message translates to:
  /// **'NO MORE DALLALA'**
  String get bannerHeadline;

  /// Subtitle on home banner card
  ///
  /// In en, this message translates to:
  /// **'Fair prices directly to farmers'**
  String get bannerSubtitle;

  /// Section header for quick action grid
  ///
  /// In en, this message translates to:
  /// **'Quick Actions'**
  String get quickActions;

  /// Section header for overview stats
  ///
  /// In en, this message translates to:
  /// **'Overview'**
  String get overview;

  /// Home dashboard section with the role's counts and shortcuts; every tile opens its screen
  ///
  /// In en, this message translates to:
  /// **'At a Glance'**
  String get atAGlance;

  /// Section header for activity feed
  ///
  /// In en, this message translates to:
  /// **'Recent'**
  String get recentActivity;

  /// Empty state for activity feed
  ///
  /// In en, this message translates to:
  /// **'No recent activity'**
  String get noRecentActivity;

  /// Quick action label for selling
  ///
  /// In en, this message translates to:
  /// **'Sell'**
  String get sell;

  /// Quick action label for buying
  ///
  /// In en, this message translates to:
  /// **'Buy'**
  String get buy;

  /// Quick action label for truckers
  ///
  /// In en, this message translates to:
  /// **'Truckers'**
  String get truckers;

  /// Quick action label for escrow
  ///
  /// In en, this message translates to:
  /// **'Escrow'**
  String get escrow;

  /// Quick action label for available loads
  ///
  /// In en, this message translates to:
  /// **'Loads'**
  String get availableLoads;

  /// Stat label for active farmers count
  ///
  /// In en, this message translates to:
  /// **'Active Farmers'**
  String get activeFarmers;

  /// Stat label for items in transit
  ///
  /// In en, this message translates to:
  /// **'In Transit'**
  String get inTransit;

  /// Stat label for matched loads
  ///
  /// In en, this message translates to:
  /// **'Matched'**
  String get matchedLoads;

  /// Stat label for available crop listings count (buyer dashboard)
  ///
  /// In en, this message translates to:
  /// **'Available Crops'**
  String get farmersBrowsed;

  /// Title for marketplace/browse screen
  ///
  /// In en, this message translates to:
  /// **'Marketplace'**
  String get marketplace;

  /// Subtitle for marketplace screen
  ///
  /// In en, this message translates to:
  /// **'Browse verified farmers'**
  String get browseVerifiedFarmers;

  /// Subtitle for farmer's listing screen
  ///
  /// In en, this message translates to:
  /// **'Manage your listings'**
  String get manageYourListings;

  /// Placeholder text for marketplace search bar
  ///
  /// In en, this message translates to:
  /// **'Search crops, farmers...'**
  String get searchListings;

  /// Crop browse empty state when a search or filter returns no listings (SIM-51)
  ///
  /// In en, this message translates to:
  /// **'No listings match your search'**
  String get noListingsMatchFilters;

  /// Hint under noListingsMatchFilters
  ///
  /// In en, this message translates to:
  /// **'Try another crop, farmer or place, or clear the filters'**
  String get noListingsMatchFiltersHint;

  /// Button that clears the crop browse search, crop chip and price filters
  ///
  /// In en, this message translates to:
  /// **'Clear filters'**
  String get clearFilters;

  /// Subtitle for orders screen header
  ///
  /// In en, this message translates to:
  /// **'Track trades & escrow'**
  String get trackTradesEscrow;

  /// Tab label for active orders
  ///
  /// In en, this message translates to:
  /// **'Active'**
  String get active;

  /// Tab label for completed orders
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get done;

  /// Tab label for all orders
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get all;

  /// Empty state title for orders
  ///
  /// In en, this message translates to:
  /// **'No orders yet'**
  String get noOrders;

  /// Empty state description for orders
  ///
  /// In en, this message translates to:
  /// **'Your trades will appear here'**
  String get ordersWillAppear;

  /// Stat label for trade count
  ///
  /// In en, this message translates to:
  /// **'Trades'**
  String get trades;

  /// Stat label for user rating
  ///
  /// In en, this message translates to:
  /// **'Rating'**
  String get rating;

  /// Stat label for the total ETB currently held in escrow on the user's active orders
  ///
  /// In en, this message translates to:
  /// **'In Escrow (ETB)'**
  String get inEscrowEtb;

  /// Profile menu item for wallet
  ///
  /// In en, this message translates to:
  /// **'Wallet'**
  String get wallet;

  /// Profile menu item for escrow history
  ///
  /// In en, this message translates to:
  /// **'Escrow History'**
  String get escrowHistory;

  /// Profile menu item for ratings
  ///
  /// In en, this message translates to:
  /// **'Ratings'**
  String get ratings;

  /// Profile menu item for documents
  ///
  /// In en, this message translates to:
  /// **'Documents'**
  String get documents;

  /// Subtitle for documents menu item
  ///
  /// In en, this message translates to:
  /// **'ID, Certificates'**
  String get idAndCerts;

  /// Subtitle for settings menu item
  ///
  /// In en, this message translates to:
  /// **'Language, Alerts'**
  String get languageAndAlerts;

  /// Profile menu item for help
  ///
  /// In en, this message translates to:
  /// **'Help'**
  String get help;

  /// Subtitle for help menu item
  ///
  /// In en, this message translates to:
  /// **'FAQ, Support'**
  String get faqAndSupport;

  /// Continue button label in registration flow
  ///
  /// In en, this message translates to:
  /// **'Continue'**
  String get continueButton;

  /// Subtitle for role selection step
  ///
  /// In en, this message translates to:
  /// **'Choose how you\'ll use WeredaConnect'**
  String get selectRoleDescription;

  /// Description for farmer role card
  ///
  /// In en, this message translates to:
  /// **'List crops for sale and find truckers'**
  String get farmerDescription;

  /// Description for trucker role card
  ///
  /// In en, this message translates to:
  /// **'Offer transport and deliver goods'**
  String get truckerDescription;

  /// Description for buyer role card
  ///
  /// In en, this message translates to:
  /// **'Browse and purchase crops from farmers'**
  String get buyerDescription;

  /// Title for Fayida ID step
  ///
  /// In en, this message translates to:
  /// **'Fayida ID Verification'**
  String get faydaIdVerification;

  /// Instructions for Fayida ID step
  ///
  /// In en, this message translates to:
  /// **'Scan your Fayida ID card or enter the number manually'**
  String get faydaIdInstructions;

  /// Label for scan front button
  ///
  /// In en, this message translates to:
  /// **'Scan Front'**
  String get scanFront;

  /// Label for scan back button
  ///
  /// In en, this message translates to:
  /// **'Scan Back'**
  String get scanBack;

  /// Toggle for manual ID entry
  ///
  /// In en, this message translates to:
  /// **'Enter ID manually'**
  String get enterManually;

  /// Label for Fayida ID text field
  ///
  /// In en, this message translates to:
  /// **'Fayida ID Number'**
  String get faydaIdNumber;

  /// Title for driving license step
  ///
  /// In en, this message translates to:
  /// **'Driving License'**
  String get drivingLicense;

  /// Instructions for driving license step
  ///
  /// In en, this message translates to:
  /// **'Scan your driving license to verify your identity'**
  String get drivingLicenseInstructions;

  /// Hint on the trucker driving license step; Continue stays disabled until a photo is taken
  ///
  /// In en, this message translates to:
  /// **'A driving license photo is required to register as a trucker.'**
  String get drivingLicenseRequired;

  /// Title for buyer type selection
  ///
  /// In en, this message translates to:
  /// **'Buyer Type'**
  String get buyerType;

  /// Subtitle for buyer type step
  ///
  /// In en, this message translates to:
  /// **'Are you buying as an individual or company?'**
  String get buyerTypeDescription;

  /// Option for individual buyer
  ///
  /// In en, this message translates to:
  /// **'Private Buyer'**
  String get privateBuyer;

  /// Option for company buyer
  ///
  /// In en, this message translates to:
  /// **'Company'**
  String get companyBuyer;

  /// Label for company name field
  ///
  /// In en, this message translates to:
  /// **'Company Name'**
  String get companyName;

  /// Helper under the company name field; Continue stays disabled until it is filled
  ///
  /// In en, this message translates to:
  /// **'Enter your company name to continue.'**
  String get companyNameRequired;

  /// Title for personal info step
  ///
  /// In en, this message translates to:
  /// **'Personal Info'**
  String get personalInfo;

  /// Subtitle for personal info step
  ///
  /// In en, this message translates to:
  /// **'Enter your details to create an account'**
  String get personalInfoDescription;

  /// Badge label for verified users
  ///
  /// In en, this message translates to:
  /// **'Verified'**
  String get verified;

  /// Button label for scanning driving license
  ///
  /// In en, this message translates to:
  /// **'Scan License'**
  String get scanLicense;

  /// Order status label for funded orders
  ///
  /// In en, this message translates to:
  /// **'Funded'**
  String get statusFunded;

  /// Order step label for placed
  ///
  /// In en, this message translates to:
  /// **'Placed'**
  String get statusPlaced;

  /// Order step label for trucker assigned
  ///
  /// In en, this message translates to:
  /// **'Assigned'**
  String get statusAssigned;

  /// Order step label for in transit (short)
  ///
  /// In en, this message translates to:
  /// **'Transit'**
  String get statusTransit;

  /// Order status label for disputed orders
  ///
  /// In en, this message translates to:
  /// **'Disputed'**
  String get statusDisputed;

  /// Title for a delivery order card
  ///
  /// In en, this message translates to:
  /// **'Delivery #{id}'**
  String deliveryTitle(int id);

  /// Title for a payment order card
  ///
  /// In en, this message translates to:
  /// **'Payment #{id}'**
  String paymentTitle(int id);

  /// Activity feed description for a new listing
  ///
  /// In en, this message translates to:
  /// **'{cropType} listing created'**
  String listingCreatedActivity(String cropType);

  /// Activity feed description for delivery status. {status} is the localized delivery status label, not the enum name
  ///
  /// In en, this message translates to:
  /// **'Delivery {status}'**
  String deliveryStatusActivity(String status);

  /// Relative time label for minutes
  ///
  /// In en, this message translates to:
  /// **'{count}m ago'**
  String minutesAgo(int count);

  /// Relative time label for hours
  ///
  /// In en, this message translates to:
  /// **'{count}h ago'**
  String hoursAgo(int count);

  /// Relative time label for days
  ///
  /// In en, this message translates to:
  /// **'{count}d ago'**
  String daysAgo(int count);

  /// Generic validation error for empty required fields
  ///
  /// In en, this message translates to:
  /// **'This field is required'**
  String get fieldRequired;

  /// Back navigation label
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get back;

  /// Button label to clear all notifications
  ///
  /// In en, this message translates to:
  /// **'Clear all'**
  String get clearAll;

  /// Title for the deliveries tab screen
  ///
  /// In en, this message translates to:
  /// **'My Deliveries'**
  String get myDeliveries;

  /// Title for the payments tab screen
  ///
  /// In en, this message translates to:
  /// **'My Payments'**
  String get myPayments;

  /// Subtitle for the deliveries tab header
  ///
  /// In en, this message translates to:
  /// **'Track your crop pickups & deliveries'**
  String get trackDeliveries;

  /// Subtitle for the payments tab header (farmer/trucker)
  ///
  /// In en, this message translates to:
  /// **'Track your earnings & escrow'**
  String get trackPayments;

  /// Subtitle for the payments tab header (buyer)
  ///
  /// In en, this message translates to:
  /// **'Track your payments & escrow'**
  String get trackBuyerPayments;

  /// Empty state message when no deliveries exist
  ///
  /// In en, this message translates to:
  /// **'No deliveries yet'**
  String get noDeliveries;

  /// Empty state message when no payments exist
  ///
  /// In en, this message translates to:
  /// **'No payments yet'**
  String get noPayments;

  /// Quick action label for truckers to post a trip
  ///
  /// In en, this message translates to:
  /// **'Post Trip'**
  String get postTrip;

  /// Quick action label for buyers to browse the market
  ///
  /// In en, this message translates to:
  /// **'Browse'**
  String get browseMarket;

  /// Subtitle for farmer's listings screen header
  ///
  /// In en, this message translates to:
  /// **'Your crop listings'**
  String get yourCropListings;

  /// Bottom navigation label for the payments tab
  ///
  /// In en, this message translates to:
  /// **'Payments'**
  String get payments;

  /// Quick action label to track deliveries
  ///
  /// In en, this message translates to:
  /// **'Track Delivery'**
  String get trackDelivery;

  /// Quick action label to check payment balance
  ///
  /// In en, this message translates to:
  /// **'Check Balance'**
  String get checkBalance;

  /// Subtitle for orders tab header (farmer/trucker)
  ///
  /// In en, this message translates to:
  /// **'Manage incoming orders'**
  String get manageOrders;

  /// Subtitle for orders tab header (buyer)
  ///
  /// In en, this message translates to:
  /// **'Track your orders'**
  String get trackYourOrders;

  /// Order status label for pending orders
  ///
  /// In en, this message translates to:
  /// **'Pending'**
  String get orderPending;

  /// Order status label for accepted orders
  ///
  /// In en, this message translates to:
  /// **'Accepted'**
  String get orderAccepted;

  /// Order status label for rejected orders
  ///
  /// In en, this message translates to:
  /// **'Rejected'**
  String get orderRejected;

  /// Button label to accept an order
  ///
  /// In en, this message translates to:
  /// **'Accept'**
  String get acceptOrder;

  /// Button label to reject an order
  ///
  /// In en, this message translates to:
  /// **'Reject'**
  String get rejectOrder;

  /// Main headline on splash/landing screen
  ///
  /// In en, this message translates to:
  /// **'Grow it. Move it.\nBuy it. Done.'**
  String get splashHeadline;

  /// Subtitle on splash/landing screen
  ///
  /// In en, this message translates to:
  /// **'One platform for farmers, truckers, and buyers — with secure escrow.'**
  String get splashSubtitle;

  /// Order status label for funded/paid orders
  ///
  /// In en, this message translates to:
  /// **'Funded'**
  String get orderFunded;

  /// Order status label for cancelled orders
  ///
  /// In en, this message translates to:
  /// **'Cancelled'**
  String get orderCancelled;

  /// Order status label for completed orders
  ///
  /// In en, this message translates to:
  /// **'Completed'**
  String get orderCompleted;

  /// Title for Fayda OIDC verification step
  ///
  /// In en, this message translates to:
  /// **'Fayda Identity Verification'**
  String get faydaVerifyTitle;

  /// Description for Fayda OIDC verification step
  ///
  /// In en, this message translates to:
  /// **'Verify your identity using your Fayda digital ID. You will be redirected to the Fayda portal.'**
  String get faydaVerifyDescription;

  /// Button label to start Fayda OIDC verification
  ///
  /// In en, this message translates to:
  /// **'Verify with Fayda'**
  String get verifyWithFayda;

  /// Loading text during Fayda verification
  ///
  /// In en, this message translates to:
  /// **'Verifying with Fayda...'**
  String get faydaVerifying;

  /// Success message after Fayda verification
  ///
  /// In en, this message translates to:
  /// **'Identity Verified'**
  String get faydaVerifiedSuccess;

  /// Error message when Fayda verification fails
  ///
  /// In en, this message translates to:
  /// **'Fayda verification failed. Please try again.'**
  String get faydaVerificationFailed;

  /// Registration Fayda step: explains that Fayda verification is required to create an account
  ///
  /// In en, this message translates to:
  /// **'A verified Fayda ID is required to create an account. It only takes a minute.'**
  String get faydaRequired;

  /// Error when Fayda service cannot be reached
  ///
  /// In en, this message translates to:
  /// **'Fayda service is currently unavailable. Please try again later.'**
  String get faydaUnavailable;

  /// Error when Fayda ID is already used by another user
  ///
  /// In en, this message translates to:
  /// **'This Fayda ID is already linked to another account.'**
  String get faydaAlreadyLinked;

  /// Button label to place a new order
  ///
  /// In en, this message translates to:
  /// **'Place Order'**
  String get placeOrder;

  /// Secondary button opening a listing/match detail screen
  ///
  /// In en, this message translates to:
  /// **'View Details'**
  String get viewDetails;

  /// Label for the crop cost line in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Crop Cost'**
  String get cropCost;

  /// Label for transport cost in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Transport Cost'**
  String get transportCost;

  /// Label for platform commission in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Platform Commission (5%)'**
  String get platformCommission;

  /// Label for total order amount
  ///
  /// In en, this message translates to:
  /// **'Total'**
  String get orderTotal;

  /// Button label to pay for an order
  ///
  /// In en, this message translates to:
  /// **'Pay Now'**
  String get payNow;

  /// Button label to skip payment for now
  ///
  /// In en, this message translates to:
  /// **'Pay Later'**
  String get payLater;

  /// Instructions for CBE bank transfer payment
  ///
  /// In en, this message translates to:
  /// **'Transfer to the Commercial Bank of Ethiopia account below. Use the reference number so we can match your payment.'**
  String get cbePaymentInstructions;

  /// Hint for CBE transaction ID input field
  ///
  /// In en, this message translates to:
  /// **'Enter CBE Transaction ID'**
  String get enterTransactionId;

  /// Button label to submit payment proof
  ///
  /// In en, this message translates to:
  /// **'Submit Payment'**
  String get submitPayment;

  /// Order status label for newly created orders
  ///
  /// In en, this message translates to:
  /// **'Created'**
  String get statusCreated;

  /// Order status label for orders awaiting payment
  ///
  /// In en, this message translates to:
  /// **'Pending Payment'**
  String get statusPendingPayment;

  /// Order status label for orders with submitted payment
  ///
  /// In en, this message translates to:
  /// **'Payment Submitted'**
  String get statusPaymentSubmitted;

  /// Order status label for orders with confirmed pickup
  ///
  /// In en, this message translates to:
  /// **'Pickup Confirmed'**
  String get statusPickupConfirmed;

  /// Order status label for orders in transit
  ///
  /// In en, this message translates to:
  /// **'In Transit'**
  String get statusInTransit;

  /// Order status label for delivered orders
  ///
  /// In en, this message translates to:
  /// **'Delivered'**
  String get statusDelivered;

  /// Order status label for orders being disbursed
  ///
  /// In en, this message translates to:
  /// **'Disbursing'**
  String get statusDisbursing;

  /// Order status label for settled orders
  ///
  /// In en, this message translates to:
  /// **'Settled'**
  String get statusSettled;

  /// Order status label for failed payment orders
  ///
  /// In en, this message translates to:
  /// **'Payment Failed'**
  String get statusPaymentFailed;

  /// Order status label for resolved dispute orders
  ///
  /// In en, this message translates to:
  /// **'Dispute Resolved'**
  String get statusDisputeResolved;

  /// Timeline explanation for the created status
  ///
  /// In en, this message translates to:
  /// **'The buyer placed this order. No money has been paid yet.'**
  String get statusCreatedInfo;

  /// Timeline explanation for the pending payment status
  ///
  /// In en, this message translates to:
  /// **'Waiting for the buyer to start the payment. Orders left unpaid are cancelled automatically after 24 hours.'**
  String get statusPendingPaymentInfo;

  /// Timeline explanation for the payment submitted status
  ///
  /// In en, this message translates to:
  /// **'The buyer sent the payment and it is being verified. The money is not yet confirmed in escrow.'**
  String get statusPaymentSubmittedInfo;

  /// Timeline explanation for the funded status
  ///
  /// In en, this message translates to:
  /// **'The payment is confirmed and held safely in escrow. The ordered quantity is now reserved from the farmer\'s listing.'**
  String get statusFundedInfo;

  /// Timeline explanation for the accepted status
  ///
  /// In en, this message translates to:
  /// **'The farmer accepted the order and will prepare the crops for pickup.'**
  String get orderAcceptedInfo;

  /// Timeline explanation for the pickup confirmed status
  ///
  /// In en, this message translates to:
  /// **'Pickup from the farmer was confirmed with a QR code scan.'**
  String get statusPickupConfirmedInfo;

  /// Timeline explanation for the in transit status
  ///
  /// In en, this message translates to:
  /// **'The crops are on their way to the buyer.'**
  String get statusInTransitInfo;

  /// Timeline explanation for the delivered status
  ///
  /// In en, this message translates to:
  /// **'Delivery was confirmed by the buyer. Escrow will now release the payment to the farmer.'**
  String get statusDeliveredInfo;

  /// Timeline explanation for the disbursing status
  ///
  /// In en, this message translates to:
  /// **'The money is leaving escrow: the farmer, and the trucker if there is one, are being paid.'**
  String get statusDisbursingInfo;

  /// Timeline explanation for the settled status
  ///
  /// In en, this message translates to:
  /// **'Everyone has been paid: the farmer, and the trucker if there is one. The order is complete.'**
  String get statusSettledInfo;

  /// Timeline explanation for the cancelled status
  ///
  /// In en, this message translates to:
  /// **'The order was cancelled. Any money held in escrow is refunded to the buyer.'**
  String get orderCancelledInfo;

  /// Timeline explanation for the rejected status
  ///
  /// In en, this message translates to:
  /// **'The farmer declined this order. Money held in escrow is refunded to the buyer.'**
  String get orderRejectedInfo;

  /// Timeline explanation for the payment failed status
  ///
  /// In en, this message translates to:
  /// **'The payment could not be verified. The buyer can try paying again.'**
  String get statusPaymentFailedInfo;

  /// Timeline explanation for the disputed status
  ///
  /// In en, this message translates to:
  /// **'A dispute was raised. Escrow keeps holding the money until the dispute is resolved.'**
  String get statusDisputedInfo;

  /// Timeline explanation for the dispute resolved status
  ///
  /// In en, this message translates to:
  /// **'The dispute was resolved and the money went to the winning side.'**
  String get statusDisputeResolvedInfo;

  /// Button label to cancel an order
  ///
  /// In en, this message translates to:
  /// **'Cancel Order'**
  String get cancelOrder;

  /// Hint for cancellation reason input
  ///
  /// In en, this message translates to:
  /// **'Reason for cancellation'**
  String get cancelReason;

  /// Title for order detail screen
  ///
  /// In en, this message translates to:
  /// **'Order Details'**
  String get orderDetails;

  /// Section header for price breakdown
  ///
  /// In en, this message translates to:
  /// **'Price Breakdown'**
  String get priceBreakdown;

  /// Label for CBE account number field
  ///
  /// In en, this message translates to:
  /// **'CBE Account Number'**
  String get cbeAccountNumber;

  /// Masked CBE account number for payment transfer
  ///
  /// In en, this message translates to:
  /// **'1000XXXXXXXXXX'**
  String get cbeAccountValue;

  /// Label for transfer reference number
  ///
  /// In en, this message translates to:
  /// **'Reference'**
  String get transferReference;

  /// Label for payment amount
  ///
  /// In en, this message translates to:
  /// **'Amount'**
  String get paymentAmount;

  /// Label for order quantity input
  ///
  /// In en, this message translates to:
  /// **'Quantity (kg)'**
  String get quantity;

  /// Section header for listing summary in order screen
  ///
  /// In en, this message translates to:
  /// **'Listing Summary'**
  String get listingSummary;

  /// Success message after placing an order
  ///
  /// In en, this message translates to:
  /// **'Order placed successfully!'**
  String get orderPlacedSuccess;

  /// Success message after submitting payment
  ///
  /// In en, this message translates to:
  /// **'Payment submitted successfully!'**
  String get paymentSubmittedSuccess;

  /// Success message after cancelling an order
  ///
  /// In en, this message translates to:
  /// **'Order cancelled.'**
  String get orderCancelledSuccess;

  /// Name of the bank for payments
  ///
  /// In en, this message translates to:
  /// **'Commercial Bank of Ethiopia'**
  String get bankName;

  /// Title for payment instructions screen
  ///
  /// In en, this message translates to:
  /// **'Payment Instructions'**
  String get paymentInstructions;

  /// Validation message for empty transaction ID
  ///
  /// In en, this message translates to:
  /// **'Transaction ID is required'**
  String get transactionIdRequired;

  /// Section header for order status timeline
  ///
  /// In en, this message translates to:
  /// **'Order Timeline'**
  String get orderTimeline;

  /// Ethiopian Birr currency format
  ///
  /// In en, this message translates to:
  /// **'{amount} ETB'**
  String etbCurrency(String amount);

  /// Quantity in kilograms format
  ///
  /// In en, this message translates to:
  /// **'{amount} kg'**
  String quantityKg(String amount);

  /// Label for pending orders count on home screen stats
  ///
  /// In en, this message translates to:
  /// **'Pending Orders'**
  String get pendingOrders;

  /// Label for active orders count on home screen stats
  ///
  /// In en, this message translates to:
  /// **'Active Orders'**
  String get activeOrders;

  /// Error message when a crop listing referenced by an order cannot be found locally
  ///
  /// In en, this message translates to:
  /// **'Listing not found'**
  String get listingNotFound;

  /// Message shown when a user tries to view an order they are not part of
  ///
  /// In en, this message translates to:
  /// **'You are not authorized to view this order.'**
  String get unauthorizedAccess;

  /// Title for buyer intent creation screen
  ///
  /// In en, this message translates to:
  /// **'What do you need?'**
  String get whatDoYouNeed;

  /// Subtitle for buyer intent creation screen
  ///
  /// In en, this message translates to:
  /// **'Tell us what you\'re looking for and we\'ll find matches'**
  String get whatDoYouNeedSubtitle;

  /// Button to search for matching crop listings
  ///
  /// In en, this message translates to:
  /// **'Find Matches'**
  String get findMatches;

  /// Label for quantity input on intent form
  ///
  /// In en, this message translates to:
  /// **'Quantity needed (quintals)'**
  String get quantityNeeded;

  /// Label for max price input on intent form
  ///
  /// In en, this message translates to:
  /// **'Maximum price (ETB/kg)'**
  String get maxPrice;

  /// Label for delivery destination on intent form
  ///
  /// In en, this message translates to:
  /// **'Deliver to'**
  String get deliverTo;

  /// Count of matching listings
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =0{No matches found} =1{1 match found} other{{count} matches found}}'**
  String matchesFound(int count);

  /// Empty state when no listings match the intent
  ///
  /// In en, this message translates to:
  /// **'No matches yet'**
  String get noMatchesYet;

  /// Badge showing how many new (not-yet-viewed) matches a buyer intent has
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 new} other{{count} new}}'**
  String newMatchesCount(int count);

  /// Section title for transport suggestions
  ///
  /// In en, this message translates to:
  /// **'Suggested Transport'**
  String get suggestedTransport;

  /// Button to select a trip for transport
  ///
  /// In en, this message translates to:
  /// **'Select This Trip'**
  String get selectThisTrip;

  /// Label for total cost estimate
  ///
  /// In en, this message translates to:
  /// **'Estimated Total'**
  String get estimatedTotal;

  /// Count of available trips
  ///
  /// In en, this message translates to:
  /// **'{count} trips available'**
  String tripsAvailable(int count);

  /// Empty state when no trips match
  ///
  /// In en, this message translates to:
  /// **'No trips yet'**
  String get noTripsYet;

  /// Button to place order with selected transport
  ///
  /// In en, this message translates to:
  /// **'Place Order + Transport'**
  String get placeOrderWithTransport;

  /// Button to place order without transport
  ///
  /// In en, this message translates to:
  /// **'Place Order (no transport)'**
  String get placeOrderNoTransport;

  /// Section title for active buyer intents on home screen
  ///
  /// In en, this message translates to:
  /// **'Active Buying Intents'**
  String get activeBuyingIntents;

  /// Button to create a new buying intent
  ///
  /// In en, this message translates to:
  /// **'New Buying Intent'**
  String get newBuyingIntent;

  /// Label for crop cost in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Crop Cost'**
  String get cropCostLabel;

  /// Label for transport cost in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Transport Cost'**
  String get transportCostLabel;

  /// Label for commission in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Commission (5%)'**
  String get commissionLabel;

  /// Stale data indicator
  ///
  /// In en, this message translates to:
  /// **'Synced {time} ago'**
  String syncedAgo(String time);

  /// Link to browse all crop listings
  ///
  /// In en, this message translates to:
  /// **'Browse All Listings'**
  String get browseAllListings;

  /// Link to browse all trips
  ///
  /// In en, this message translates to:
  /// **'Browse All Trips'**
  String get browseAllTrips;

  /// Confirmation after saving a buying intent
  ///
  /// In en, this message translates to:
  /// **'Saved! We\'ll notify you of new matches'**
  String get intentSaved;

  /// Caption under the Find Matches button before the intent is saved
  ///
  /// In en, this message translates to:
  /// **'Saving this request lets us alert you when new matches arrive'**
  String get intentSaveHint;

  /// Inline validation when no crop type is selected
  ///
  /// In en, this message translates to:
  /// **'Select a crop type'**
  String get selectCropTypeError;

  /// Inline validation for missing/invalid quantity
  ///
  /// In en, this message translates to:
  /// **'Enter the quantity you need'**
  String get enterQuantityError;

  /// Inline validation for an invalid max price
  ///
  /// In en, this message translates to:
  /// **'Enter a valid price or leave it empty'**
  String get enterValidPriceError;

  /// Inline validation for missing delivery woreda
  ///
  /// In en, this message translates to:
  /// **'Enter your delivery woreda'**
  String get enterDeliveryWoredaError;

  /// Helper under the max price field explaining it is optional
  ///
  /// In en, this message translates to:
  /// **'Optional — leave empty to see all prices'**
  String get maxPriceOptionalHint;

  /// Shown instead of a max price when the request has no budget
  ///
  /// In en, this message translates to:
  /// **'Any price'**
  String get anyPrice;

  /// Chip on a match that was found via a substitute crop type
  ///
  /// In en, this message translates to:
  /// **'Similar crop'**
  String get similarCrop;

  /// Chip on a match priced within the buyer's budget
  ///
  /// In en, this message translates to:
  /// **'Within budget'**
  String get withinBudget;

  /// Chip on a match priced in the near-miss band over budget
  ///
  /// In en, this message translates to:
  /// **'Slightly over budget'**
  String get slightlyOverBudget;

  /// Chip on a match whose listing covers the full quantity needed
  ///
  /// In en, this message translates to:
  /// **'Covers full quantity'**
  String get coversFullQuantity;

  /// Chip on a match whose listing covers only part of the quantity needed
  ///
  /// In en, this message translates to:
  /// **'Partial quantity'**
  String get partialQuantity;

  /// Chip on a match close to the buyer's delivery destination
  ///
  /// In en, this message translates to:
  /// **'Near you'**
  String get nearYou;

  /// Menu action to delete a buying request
  ///
  /// In en, this message translates to:
  /// **'Delete Request'**
  String get deleteRequest;

  /// Confirmation dialog before deleting a buying request
  ///
  /// In en, this message translates to:
  /// **'Delete this buying request? You will stop receiving match alerts for it.'**
  String get deleteRequestConfirm;

  /// Snackbar after a buying request is deleted
  ///
  /// In en, this message translates to:
  /// **'Request deleted'**
  String get requestDeleted;

  /// Menu action to mark a buying request as fulfilled
  ///
  /// In en, this message translates to:
  /// **'Mark as Fulfilled'**
  String get markFulfilled;

  /// Snackbar after a buying request is marked fulfilled
  ///
  /// In en, this message translates to:
  /// **'Request marked as fulfilled'**
  String get requestFulfilled;

  /// Helper showing the quintal equivalent of a kg quantity
  ///
  /// In en, this message translates to:
  /// **'= {count} quintals'**
  String quintalEquivalent(String count);

  /// Error under the order quantity field when the buyer asks for more kg than the listing has
  ///
  /// In en, this message translates to:
  /// **'Only {available} kg available. Enter a smaller quantity.'**
  String orderQuantityExceedsStock(String available);

  /// Label for the optional delivery-deadline field on the create-intent screen
  ///
  /// In en, this message translates to:
  /// **'Needed by (optional)'**
  String get neededByDate;

  /// Helper under the needed-by-date field
  ///
  /// In en, this message translates to:
  /// **'We\'ll prefer transport that can deliver in time'**
  String get neededByDateHint;

  /// Placeholder for an unset date field
  ///
  /// In en, this message translates to:
  /// **'Select date'**
  String get selectDate;

  /// Button to clear the selected needed-by date
  ///
  /// In en, this message translates to:
  /// **'Clear'**
  String get clearDate;

  /// Badge text for new matches
  ///
  /// In en, this message translates to:
  /// **'New matches available!'**
  String get newMatchesAvailable;

  /// Quick action label for buyer to find crops
  ///
  /// In en, this message translates to:
  /// **'Find Crops'**
  String get findCrops;

  /// Quick action label for buyers to find available trips
  ///
  /// In en, this message translates to:
  /// **'Find Trips'**
  String get findTrips;

  /// Total label in price breakdown
  ///
  /// In en, this message translates to:
  /// **'Total'**
  String get totalLabel;

  /// Button label to start a matched trip
  ///
  /// In en, this message translates to:
  /// **'Start Trip'**
  String get startTrip;

  /// Confirmation dialog text for starting a trip
  ///
  /// In en, this message translates to:
  /// **'Start this trip? You are confirming pickup.'**
  String get startTripConfirmation;

  /// Success message after starting a trip
  ///
  /// In en, this message translates to:
  /// **'Trip started! Drive safe.'**
  String get tripStartedSuccess;

  /// Button label to mark a trip as delivered
  ///
  /// In en, this message translates to:
  /// **'Mark Delivered'**
  String get markDelivered;

  /// Confirmation dialog text for marking delivery
  ///
  /// In en, this message translates to:
  /// **'Mark as delivered? Confirm drop-off.'**
  String get markDeliveredConfirmation;

  /// Success message after confirming delivery
  ///
  /// In en, this message translates to:
  /// **'Delivery confirmed!'**
  String get tripDeliveredSuccess;

  /// Hero card text when trip is posted but not matched
  ///
  /// In en, this message translates to:
  /// **'Waiting for match...'**
  String get waitingForMatch;

  /// Hero card text when trip is in transit
  ///
  /// In en, this message translates to:
  /// **'Heading to {destination}'**
  String headingTo(String destination);

  /// Button label to post another trip after delivery
  ///
  /// In en, this message translates to:
  /// **'Post Next Trip'**
  String get postNextTrip;

  /// Label for recent route chips section
  ///
  /// In en, this message translates to:
  /// **'Recent Routes'**
  String get recentRoutes;

  /// Hero card text when trucker has no trips
  ///
  /// In en, this message translates to:
  /// **'No active trip'**
  String get noActiveTrip;

  /// CTA button label for idle trucker
  ///
  /// In en, this message translates to:
  /// **'Post a Trip'**
  String get postATrip;

  /// Button label to repeat a previous trip
  ///
  /// In en, this message translates to:
  /// **'Repeat Last Trip'**
  String get repeatLastTrip;

  /// Label for quick-fill route chips on create trip screen
  ///
  /// In en, this message translates to:
  /// **'Quick Fill'**
  String get quickFill;

  /// Generic error title
  ///
  /// In en, this message translates to:
  /// **'Something went wrong'**
  String get somethingWentWrong;

  /// Error subtitle suggesting user retry
  ///
  /// In en, this message translates to:
  /// **'Please check your connection and try again.'**
  String get checkConnectionRetry;

  /// Quick action label for creating a new crop listing
  ///
  /// In en, this message translates to:
  /// **'Sell Crops'**
  String get sellCrops;

  /// Farmer-specific banner headline prompting crop listing
  ///
  /// In en, this message translates to:
  /// **'SELL YOUR HARVEST'**
  String get sellYourHarvest;

  /// Farmer-specific banner subtitle
  ///
  /// In en, this message translates to:
  /// **'List your crops and reach buyers directly'**
  String get tapToListCrops;

  /// Sync status badge when data is up to date
  ///
  /// In en, this message translates to:
  /// **'Synced'**
  String get synced;

  /// Sync status badge when device is offline
  ///
  /// In en, this message translates to:
  /// **'Offline'**
  String get offline;

  /// Subtitle for failed sync notification
  ///
  /// In en, this message translates to:
  /// **'{count} operations need attention'**
  String syncFailureCount(int count);

  /// Subtitle for pending sync notification
  ///
  /// In en, this message translates to:
  /// **'{count} waiting to sync'**
  String syncPendingCount(int count);

  /// Payment notification title with status
  ///
  /// In en, this message translates to:
  /// **'{status} payment'**
  String paymentStatusTitle(String status);

  /// Navigation tab label for farmer's crop listings
  ///
  /// In en, this message translates to:
  /// **'My Crops'**
  String get myCrops;

  /// Tab label for browsing all crop listings
  ///
  /// In en, this message translates to:
  /// **'Browse'**
  String get browseAll;

  /// Tab label for buyer's saved buying intents
  ///
  /// In en, this message translates to:
  /// **'My Requests'**
  String get myRequests;

  /// Instructions on forgot password screen
  ///
  /// In en, this message translates to:
  /// **'Enter your phone number to receive a verification code'**
  String get enterPhoneForReset;

  /// Button to send password reset code
  ///
  /// In en, this message translates to:
  /// **'Send Reset Code'**
  String get sendResetCode;

  /// Message after reset code sent
  ///
  /// In en, this message translates to:
  /// **'Verification code sent to your phone'**
  String get resetCodeSent;

  /// Instructions on reset password screen
  ///
  /// In en, this message translates to:
  /// **'Enter the verification code sent to your phone'**
  String get enterResetCode;

  /// Label for verification code field
  ///
  /// In en, this message translates to:
  /// **'Verification Code'**
  String get verificationCode;

  /// Label for new password field
  ///
  /// In en, this message translates to:
  /// **'New Password'**
  String get newPassword;

  /// Label for confirm new password field
  ///
  /// In en, this message translates to:
  /// **'Confirm New Password'**
  String get confirmNewPassword;

  /// Button to reset password
  ///
  /// In en, this message translates to:
  /// **'Reset Password'**
  String get resetPassword;

  /// Success message after password reset
  ///
  /// In en, this message translates to:
  /// **'Password reset successful! You can now log in.'**
  String get passwordResetSuccess;

  /// Validation error for empty code
  ///
  /// In en, this message translates to:
  /// **'Verification code is required'**
  String get codeRequired;

  /// Validation error for short password
  ///
  /// In en, this message translates to:
  /// **'Password must be at least 8 characters'**
  String get passwordTooShort;

  /// Hint text shown in the place selector typeahead field
  ///
  /// In en, this message translates to:
  /// **'Search woreda or town'**
  String get placeSearchHint;

  /// Shown under the place selector when a query matches no place; the user may still submit the free text they typed
  ///
  /// In en, this message translates to:
  /// **'No match — you can use what you typed'**
  String get placeNoResults;

  /// Accessibility label for the place selector results list
  ///
  /// In en, this message translates to:
  /// **'Place results'**
  String get placeResultsLabel;

  /// Title of the messages inbox / conversations list
  ///
  /// In en, this message translates to:
  /// **'Messages'**
  String get messages;

  /// Empty state title on the messages inbox
  ///
  /// In en, this message translates to:
  /// **'No messages yet'**
  String get noConversationsYet;

  /// Empty state hint on the messages inbox
  ///
  /// In en, this message translates to:
  /// **'Your conversations with farmers, truckers and buyers will appear here.'**
  String get noConversationsHint;

  /// Empty state inside a conversation thread
  ///
  /// In en, this message translates to:
  /// **'No messages yet. Start the conversation below.'**
  String get noMessagesInThread;

  /// Placeholder for the chat message input field
  ///
  /// In en, this message translates to:
  /// **'Type a message…'**
  String get typeAMessage;

  /// Send button label for the chat composer
  ///
  /// In en, this message translates to:
  /// **'Send'**
  String get send;

  /// Action that opens an in-app chat with a counterparty
  ///
  /// In en, this message translates to:
  /// **'Send a message'**
  String get sendMessage;

  /// Short button label to open a chat
  ///
  /// In en, this message translates to:
  /// **'Message'**
  String get messageContact;

  /// Action to chat with the farmer
  ///
  /// In en, this message translates to:
  /// **'Message Farmer'**
  String get messageFarmer;

  /// Action to chat with the trucker
  ///
  /// In en, this message translates to:
  /// **'Message Trucker'**
  String get messageTrucker;

  /// Action to chat with the buyer
  ///
  /// In en, this message translates to:
  /// **'Message Buyer'**
  String get messageBuyer;

  /// Generic contact button / sheet title
  ///
  /// In en, this message translates to:
  /// **'Contact'**
  String get contact;

  /// ESC-48: shown where Call, Copy and Message are hidden because the order's payment is not yet held in escrow (crop and trip detail, closed chat threads, an order before payment)
  ///
  /// In en, this message translates to:
  /// **'Contact opens after payment is held in escrow.'**
  String get contactAfterEscrowNote;

  /// ESC-48: shown on a funded order before the farmer accepts it, where Call, Copy and Message are still hidden
  ///
  /// In en, this message translates to:
  /// **'Contact opens once the farmer accepts the order.'**
  String get contactAfterAcceptNote;

  /// ESC-48: order detail button that opens Call, Copy and Message for the order's buyer
  ///
  /// In en, this message translates to:
  /// **'Contact Buyer'**
  String get contactBuyer;

  /// ESC-48: order detail button that opens Call, Copy and Message for the order's trucker
  ///
  /// In en, this message translates to:
  /// **'Contact Trucker'**
  String get contactTrucker;

  /// ESC-48: shown in a chat thread whose order was cancelled, rejected or refunded, and when a message is refused because contact is not open
  ///
  /// In en, this message translates to:
  /// **'Contact is closed for this conversation.'**
  String get contactClosedNote;

  /// Title of the in-app banner shown when a chat message arrives
  ///
  /// In en, this message translates to:
  /// **'New message'**
  String get newMessage;

  /// Banner title when several chat messages arrive at once
  ///
  /// In en, this message translates to:
  /// **'{count} new messages'**
  String newMessagesCount(int count);

  /// Fallback label for a chat linked to a crop listing
  ///
  /// In en, this message translates to:
  /// **'Crop'**
  String get messageContextCrop;

  /// Fallback label for a chat linked to a trip
  ///
  /// In en, this message translates to:
  /// **'Trip'**
  String get messageContextTrip;

  /// Fallback label for a chat linked to an order
  ///
  /// In en, this message translates to:
  /// **'Order'**
  String get messageContextOrder;

  /// Fallback label for a chat linked to a delivery
  ///
  /// In en, this message translates to:
  /// **'Delivery'**
  String get messageContextDelivery;

  /// Shown when tapping a chat's linked crop/trip that can't be loaded (offline / not synced / not visible)
  ///
  /// In en, this message translates to:
  /// **'Details aren\'t available right now'**
  String get contextUnavailable;

  /// Shown when the server needs a fresh password before a high-value money action (AUTH_040)
  ///
  /// In en, this message translates to:
  /// **'For your safety, confirm it\'s you before this payment.'**
  String get stepUpRequiredMessage;

  /// Explanation when a payment is blocked until the buyer verifies their Fayda ID (ORDER_KYC_REQUIRED)
  ///
  /// In en, this message translates to:
  /// **'Payments above 10,000 ETB need a verified Fayda ID. Verify with Fayda and your payment is sent right after.'**
  String get kycRequiredFaydaMessage;

  /// Explanation when a payment is blocked because the buyer's phone is not verified (ORDER_KYC_REQUIRED)
  ///
  /// In en, this message translates to:
  /// **'Your phone number is not verified yet, so this payment was not sent. Verify with Fayda and your payment is sent right after.'**
  String get kycRequiredPhoneMessage;

  /// Title of the dialog shown when a payment needs identity verification
  ///
  /// In en, this message translates to:
  /// **'Verification needed'**
  String get kycRequiredTitle;

  /// Rate limit message with a retry countdown
  ///
  /// In en, this message translates to:
  /// **'Too many attempts. Try again in {seconds} seconds.'**
  String authRateLimitRetryIn(int seconds);

  /// OTP verification error AUTH_012
  ///
  /// In en, this message translates to:
  /// **'That code is wrong or has expired. Check the SMS or request a new code.'**
  String get otpInvalidOrExpired;

  /// Title of the registration OTP screen
  ///
  /// In en, this message translates to:
  /// **'Confirm your phone'**
  String get otpVerificationTitle;

  /// OTP screen explanation with the destination phone number
  ///
  /// In en, this message translates to:
  /// **'We sent a 6-digit code by SMS to {phone}.'**
  String otpSentTo(String phone);

  /// Hint text inside the OTP input
  ///
  /// In en, this message translates to:
  /// **'6-digit code'**
  String get otpCodeHint;

  /// OTP expiry time notice
  ///
  /// In en, this message translates to:
  /// **'The code expires at {time}.'**
  String otpExpiresAt(String time);

  /// Fayda step-up dialog (AUTH_041): a payment above 10,000 ETB needs a fresh Fayda login instead of the password
  ///
  /// In en, this message translates to:
  /// **'Payments above 10,000 ETB need a Fayda login, not your password. Confirm it\'s you with Fayda and your payment continues right after.'**
  String get faydaStepUpMessage;

  /// Title of the step-up re-authentication dialog
  ///
  /// In en, this message translates to:
  /// **'Confirm it\'s you'**
  String get stepUpTitle;

  /// Read-only phone shown in the step-up dialog
  ///
  /// In en, this message translates to:
  /// **'Phone: {phone}'**
  String stepUpPhoneLabel(String phone);

  /// Confirm button in the step-up dialog
  ///
  /// In en, this message translates to:
  /// **'Confirm'**
  String get stepUpConfirm;

  /// Step-up dialog error when offline
  ///
  /// In en, this message translates to:
  /// **'You need internet to confirm. Connect and try again.'**
  String get stepUpOfflineError;

  /// Snackbar after a successful step-up re-authentication
  ///
  /// In en, this message translates to:
  /// **'Confirmed. Retrying your payment.'**
  String get stepUpDone;

  /// Fayda callback state mismatch or FAYDA_002
  ///
  /// In en, this message translates to:
  /// **'The Fayda session expired. Please start verification again.'**
  String get faydaStateMismatch;

  /// Shown on the Fayda verification screen when the app came back from the browser but the Fayda result has not arrived yet
  ///
  /// In en, this message translates to:
  /// **'Still waiting for Fayda. Finish in the browser and you will be brought back here.'**
  String get faydaStillWaiting;

  /// Success message after linking Fayda from the profile
  ///
  /// In en, this message translates to:
  /// **'Your Fayda ID is now linked to your account.'**
  String get faydaLinkSuccess;

  /// Profile menu subtitle under Verify with Fayda
  ///
  /// In en, this message translates to:
  /// **'Needed for payments above 10,000 ETB'**
  String get faydaNotVerifiedSubtitle;

  /// Profile toggle title for the app lock setting
  ///
  /// In en, this message translates to:
  /// **'App lock'**
  String get appLockTitle;

  /// Profile toggle subtitle for the app lock setting
  ///
  /// In en, this message translates to:
  /// **'Ask for your fingerprint or phone PIN when opening the app'**
  String get appLockSubtitle;

  /// Title on the app lock screen
  ///
  /// In en, this message translates to:
  /// **'WeredaConnect is locked'**
  String get appLockLockedTitle;

  /// Unlock button on the app lock screen
  ///
  /// In en, this message translates to:
  /// **'Unlock'**
  String get appLockUnlock;

  /// System prompt reason when unlocking the app
  ///
  /// In en, this message translates to:
  /// **'Unlock WeredaConnect'**
  String get appLockReason;

  /// System prompt reason before confirming a delivery by QR
  ///
  /// In en, this message translates to:
  /// **'Confirm it\'s you to accept this delivery'**
  String get qrConfirmAuthReason;

  /// Shown when device authentication fails before QR delivery confirm
  ///
  /// In en, this message translates to:
  /// **'Could not confirm it\'s you. The delivery was not confirmed.'**
  String get qrConfirmAuthFailed;

  /// Registration Fayda step: shown after the automatic retries to reach Fayda all failed (FAYDA_003 or no connection)
  ///
  /// In en, this message translates to:
  /// **'We couldn\'t reach Fayda, even after several tries. Check your internet connection and try again in a few minutes.'**
  String get faydaRegistrationUnavailable;

  /// Shown on web when the live Fayda provider does not accept the web callback, so verification must happen in the mobile app
  ///
  /// In en, this message translates to:
  /// **'Fayda verification isn\'t available in the browser yet. Please use the WeredaConnect mobile app.'**
  String get faydaUnavailableOnWeb;

  /// Registration Fayda step: the Fayda login page could not be opened in the browser
  ///
  /// In en, this message translates to:
  /// **'We couldn\'t open the Fayda page. Please try again.'**
  String get faydaCouldNotOpen;

  /// AUTH_021: registration was sent without a valid Fayda verification token; the user is sent back to the Fayda step
  ///
  /// In en, this message translates to:
  /// **'Your Fayda verification is missing or has expired. Please verify with Fayda again to finish creating your account.'**
  String get faydaRequiredToRegister;

  /// Registration Fayda step: shown under the spinner while an automatic retry runs
  ///
  /// In en, this message translates to:
  /// **'Still trying to reach Fayda ({attempt}/{total})...'**
  String faydaRetrying(int attempt, int total);

  /// KYC dialog when the farmer's phone is not verified (buyer cannot fix it)
  ///
  /// In en, this message translates to:
  /// **'The farmer\'s phone number is not verified yet, so this payment was not sent. You can pay once the farmer verifies it.'**
  String get kycFarmerPhoneMessage;

  /// KYC dialog when the farmer has no verified Fayda ID (buyer cannot fix it)
  ///
  /// In en, this message translates to:
  /// **'Payments above 10,000 ETB need the farmer to have a verified Fayda ID too, so this payment was not sent. You can pay once the farmer verifies.'**
  String get kycFarmerFaydaMessage;

  /// KYC dialog when the server could not run the identity check
  ///
  /// In en, this message translates to:
  /// **'We could not check identity verification right now, so this payment was not sent. Please try again later.'**
  String get kycUnavailableMessage;

  /// Snackbar after the Fayda check for a payment was cancelled or failed
  ///
  /// In en, this message translates to:
  /// **'Payment not sent. Your identity was not verified.'**
  String get kycPaymentNotSent;

  /// Snackbar after a KYC-rejected queued payment was sent again following Fayda verification
  ///
  /// In en, this message translates to:
  /// **'Identity verified. Your payment will be sent again.'**
  String get kycPaymentResubmitted;

  /// Snackbar after verification when the rejected payment could not be sent again automatically
  ///
  /// In en, this message translates to:
  /// **'Identity verified. Open the order and submit the payment again.'**
  String get kycVerifiedPayAgain;

  /// Fayda verification screen opened from a blocked payment
  ///
  /// In en, this message translates to:
  /// **'Confirm it\'s you with Fayda. Use the Fayda ID that belongs to this account, and your payment continues right after.'**
  String get faydaPaymentPrompt;

  /// Fayda verification screen opened from the profile (link an existing account)
  ///
  /// In en, this message translates to:
  /// **'Link your Fayda ID to this account. Use your own Fayda ID: it must match this account\'s phone number.'**
  String get faydaLinkPrompt;

  /// Snackbar when Fayda verification for a payment succeeded
  ///
  /// In en, this message translates to:
  /// **'Identity verified. Continuing your payment...'**
  String get faydaPaymentContinues;

  /// FAYDA_005: the account already holds a different Fayda ID
  ///
  /// In en, this message translates to:
  /// **'This Fayda ID does not match the one on your account. Verify with your own Fayda ID.'**
  String get faydaIdMismatch;

  /// FAYDA_006: the Fayda ID's phone number is not the account's phone
  ///
  /// In en, this message translates to:
  /// **'The phone number on this Fayda ID does not match your account. Verify with your own Fayda ID.'**
  String get faydaPhoneMismatch;

  /// FAYDA_007: nothing ties the Fayda ID to this account (no phone number released)
  ///
  /// In en, this message translates to:
  /// **'We could not confirm this Fayda ID belongs to your account. Verify with the Fayda ID registered to your phone number.'**
  String get faydaIdentityUnconfirmed;

  /// Fayda screen (payment blocked for an unverified phone): the Fayda ID matched but Fayda did not prove the account's phone
  ///
  /// In en, this message translates to:
  /// **'Your Fayda ID is confirmed, but Fayda does not list this account\'s phone number, so your phone is still not verified. The payment was not sent.'**
  String get kycPhoneNotProven;

  /// Live payment screen: the button that sends the payment and opens Telebirr (ESC-44)
  ///
  /// In en, this message translates to:
  /// **'Pay with Telebirr'**
  String get payWithTelebirr;

  /// Live payment screen: what happens when the buyer pays (ESC-44)
  ///
  /// In en, this message translates to:
  /// **'You will pay {amount} in Telebirr. When you finish, come back to this app. The payment is confirmed automatically.'**
  String telebirrLivePayInstructions(String amount);

  /// Title of the order's Telebirr checkout screen (ESC-44)
  ///
  /// In en, this message translates to:
  /// **'Telebirr payment'**
  String get orderCheckoutTitle;

  /// Checkout screen: the payment is still queued on the phone (offline, or waiting for a step-up)
  ///
  /// In en, this message translates to:
  /// **'Your payment is waiting to be sent. Telebirr opens once it reaches the server.'**
  String get orderCheckoutWaitingToSend;

  /// Checkout screen: until when the open Telebirr payment can be finished
  ///
  /// In en, this message translates to:
  /// **'You can finish this payment in Telebirr until {time}.'**
  String orderCheckoutOpenUntil(String time);

  /// Checkout screen: the Telebirr payment page expired unpaid
  ///
  /// In en, this message translates to:
  /// **'This Telebirr payment page has closed. Start a new one to pay.'**
  String get orderCheckoutWindowClosed;

  /// Checkout screen button: open a new Telebirr payment after the old page expired
  ///
  /// In en, this message translates to:
  /// **'Start a new Telebirr payment'**
  String get orderCheckoutStartNew;

  /// Checkout screen button: read the order's payment status again
  ///
  /// In en, this message translates to:
  /// **'Check payment status'**
  String get orderCheckoutCheckAgain;

  /// Checkout screen: automatic checks stopped and no payment arrived yet
  ///
  /// In en, this message translates to:
  /// **'We have not received the payment yet. If you paid, tap Check payment status.'**
  String get orderCheckoutStillWaiting;

  /// ESCROW_030: the payment time ran out (20 hours after the payment was submitted)
  ///
  /// In en, this message translates to:
  /// **'The time to pay for this order has run out. The order will be marked as failed, then you can pay again.'**
  String get orderCheckoutTimeRanOut;

  /// ESCROW_030: the order is no longer waiting for payment (cancelled or rejected)
  ///
  /// In en, this message translates to:
  /// **'This order is no longer waiting for payment.'**
  String get orderCheckoutNotWaiting;

  /// ESCROW_030 (422): the order cannot be paid online
  ///
  /// In en, this message translates to:
  /// **'This order can\'t be paid online. Contact support.'**
  String get orderCheckoutNotPayableOnline;

  /// ESCROW_031: no Telebirr page here (mock payments); the app waits for the automatic confirmation
  ///
  /// In en, this message translates to:
  /// **'Payments are confirmed automatically here. Checking your order...'**
  String get orderCheckoutConfirmedAutomatically;

  /// ESCROW_032: a Telebirr payment is being set up, or its answer was lost; blocked until the old page expires
  ///
  /// In en, this message translates to:
  /// **'A Telebirr payment for this order is being set up or was interrupted. If you already paid, it will be confirmed here. Otherwise try again later (it can take up to 2 hours).'**
  String get orderCheckoutAlreadyOpen;

  /// ESCROW_013: Telebirr refused or did not answer when opening the payment
  ///
  /// In en, this message translates to:
  /// **'Telebirr could not start the payment. If you already paid, it will be confirmed here. Otherwise try again.'**
  String get orderCheckoutGatewayProblem;

  /// ESCROW_033: the order is not on the server
  ///
  /// In en, this message translates to:
  /// **'This order is not on the server yet. Try again once it has synced.'**
  String get orderCheckoutOrderMissing;

  /// Checkout screen: the order moved to payment_failed
  ///
  /// In en, this message translates to:
  /// **'This payment could not be confirmed in time. You can pay again.'**
  String get orderCheckoutPaymentFailed;

  /// Checkout screen: the server refused the payment (or the KYC gate held it until the buyer verifies), so it was not sent
  ///
  /// In en, this message translates to:
  /// **'Your payment was not sent. If you were asked to verify your identity, it is sent again once you do; otherwise open the order to pay again.'**
  String get orderCheckoutNotSent;

  /// Shown when the buyer comes back from Telebirr and the app checks the payment
  ///
  /// In en, this message translates to:
  /// **'Checking your payment...'**
  String get orderCheckoutCheckingPayment;

  /// Live payments: the order total is outside what one Telebirr payment takes (ORDER_VALIDATION / ESCROW_001)
  ///
  /// In en, this message translates to:
  /// **'Telebirr takes 100 to 500,000 ETB in one payment. This order\'s total is outside that, so it can\'t be paid with Telebirr.'**
  String get paymentTotalOutOfRange;

  /// Order detail button (buyer, payment submitted): open the order's Telebirr checkout
  ///
  /// In en, this message translates to:
  /// **'Continue payment'**
  String get continuePayment;

  /// Order detail button (buyer, payment failed): pay the order again
  ///
  /// In en, this message translates to:
  /// **'Pay again'**
  String get payAgain;

  /// Order detail row (all roles, accepted order): the time by which the crops must be collected; after it the buyer may cancel (ESC-06)
  ///
  /// In en, this message translates to:
  /// **'Collect by'**
  String get orderPickupDeadline;

  /// Order detail notice to the buyer: the accepted order was not picked up by its deadline, so it may be cancelled for a full refund (ESC-06)
  ///
  /// In en, this message translates to:
  /// **'The crops were not collected in time. You can cancel this order and get all your money back.'**
  String get orderTransportFailedBuyerHint;

  /// Order detail notice to the farmer and the trucker: the accepted order was not picked up by its deadline, so the buyer may cancel it (ESC-06)
  ///
  /// In en, this message translates to:
  /// **'The crops were not collected in time. The buyer can now cancel this order.'**
  String get orderTransportFailedPartyHint;

  /// Cancel dialog body when the buyer cancels an accepted order after a transport failure (ESC-06)
  ///
  /// In en, this message translates to:
  /// **'You get back everything you paid, including transport and the platform commission.'**
  String get cancelOrderTransportFailedBody;

  /// The server (ORDER_CANCEL_NOT_ALLOWED) or the app refused to cancel an accepted order: its pickup deadline has not passed, or the crops were picked up (ESC-06)
  ///
  /// In en, this message translates to:
  /// **'This order can\'t be cancelled now. It can be cancelled only when the crops were not collected in time and nothing was picked up.'**
  String get errorOrderCancelNotAllowed;

  /// The server refused an order response because its deadline passed (ORDER_DEADLINE_PASSED); on mobile the local change was undone
  ///
  /// In en, this message translates to:
  /// **'Too late. The time for this step has passed.'**
  String get errorDeadlinePassed;

  /// The server refused an order because the trip it names can no longer take it (ORDER_TRIP_NOT_ELIGIBLE); the buyer chooses another truck
  ///
  /// In en, this message translates to:
  /// **'That truck is no longer available. Choose again.'**
  String get errorTripNotEligible;

  /// The server refused a replacement truck because no more may be tried for this order (ORDER_TRANSPORT_ATTEMPTS_EXCEEDED); the buyer collects or cancels
  ///
  /// In en, this message translates to:
  /// **'No more trucks can be tried for this order. Collect the crops yourself or cancel for a full refund.'**
  String get errorTransportAttemptsExceeded;

  /// The server refused an order because the trip it names is full (TRIP_CAPACITY_EXCEEDED); on mobile the local order was removed
  ///
  /// In en, this message translates to:
  /// **'This truck has no room left for this order. Choose another truck.'**
  String get errorTripCapacityExceeded;

  /// The server refused a new listing, trip or order accept because the account is restricted for a while (REPUTATION_RESTRICTED); neutral wording, no blame; on mobile the local change was undone
  ///
  /// In en, this message translates to:
  /// **'Your account can\'t do this for now, so this change was not saved. Your current orders continue.'**
  String get errorReputationRestricted;

  /// The server refused a buyer's order because the listing's farmer or the trip's trucker is restricted (REPUTATION_RESTRICTED on order create); on mobile the local order was removed
  ///
  /// In en, this message translates to:
  /// **'This farmer or trucker can\'t take new orders right now, so the order was not placed. Please choose another.'**
  String get errorCounterpartyRestricted;

  /// The server refused a trip change because orders are booked on it (TRIP_HAS_BOOKINGS); on mobile the local trip went back to the server's
  ///
  /// In en, this message translates to:
  /// **'This trip has booked orders, so this change can\'t be made.'**
  String get errorTripHasBookings;

  /// The server refused to remove or reduce a listing that has funded or accepted orders (CROP_HAS_ACTIVE_ORDERS); on mobile the local listing went back to the server's
  ///
  /// In en, this message translates to:
  /// **'This listing has open orders. Handle them first.'**
  String get errorCropHasActiveOrders;

  /// The server refused a change because this app version is below its minimum (APP_UPDATE_REQUIRED); on mobile the local change was undone
  ///
  /// In en, this message translates to:
  /// **'Please update the app to continue. This change was not saved.'**
  String get errorAppUpdateRequired;

  /// The server answered a queued change other than an order (listing, trip, delivery, rating, message, buyer request) with APP_UPDATE_REQUIRED; the app keeps it queued and sends it after the update
  ///
  /// In en, this message translates to:
  /// **'Please update the app. This change is kept on your phone and will be sent after the update.'**
  String get errorAppUpdateRequiredKept;
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

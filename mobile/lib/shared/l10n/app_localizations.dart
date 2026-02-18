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

  /// Role selection label
  ///
  /// In en, this message translates to:
  /// **'Select Role'**
  String get selectRole;

  /// Farmer role
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

  /// Buyer/wholesaler role
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
  /// **'Password must be at least 6 characters'**
  String get passwordRequirements;

  /// Error message for invalid phone format
  ///
  /// In en, this message translates to:
  /// **'Invalid phone number format. Use +251XXXXXXXXX'**
  String get invalidPhoneFormat;

  /// Error message for invalid credentials
  ///
  /// In en, this message translates to:
  /// **'Invalid phone number or password'**
  String get invalidCredentials;

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
  /// **'Example: +251911234567'**
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

  /// Status label for expired crop listing
  ///
  /// In en, this message translates to:
  /// **'Expired'**
  String get listingStatusExpired;

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

  /// Ethiopian Birr currency abbreviation for price display
  ///
  /// In en, this message translates to:
  /// **'ETB'**
  String get etbCurrency;

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

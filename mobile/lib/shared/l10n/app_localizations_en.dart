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
  String get welcomeTitle => 'Welcome to WeredaConnect';

  @override
  String welcomeBackUser(String userName) {
    return 'Welcome back, $userName';
  }

  @override
  String get worksOffline => 'Works Offline';

  @override
  String get offlineMode => 'Offline Mode';

  @override
  String get syncPending => 'Sync Pending';

  @override
  String get taglineShort => 'Connecting Farmers, Truckers & Buyers';

  @override
  String get telebirrPayment => 'Telebirr Payment';

  @override
  String get syncInProgress => 'Syncing...';

  @override
  String get syncComplete => 'Sync Complete';

  @override
  String get syncFailed => 'Sync Failed';

  @override
  String get retry => 'Retry';

  @override
  String get syncNow => 'Sync Now';

  @override
  String get selectLanguagePrompt => 'Select Your Language';

  @override
  String get cancel => 'Cancel';

  @override
  String get save => 'Save';

  @override
  String get delete => 'Delete';

  @override
  String get edit => 'Edit';

  @override
  String get editProfile => 'Edit Profile';

  @override
  String get settings => 'Settings';

  @override
  String get skip => 'Skip';

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
  String get errorSessionExpired =>
      'Your session expired. Please sign in again.';

  @override
  String get errorConflict =>
      'This was updated elsewhere. Refresh and try again.';

  @override
  String get showingCachedResults =>
      'Showing saved results — couldn\'t refresh.';

  @override
  String get dataWillSyncWhenOnline =>
      'Your data will sync when you\'re back online';

  @override
  String get offlineReady => 'Offline Ready';

  @override
  String get home => 'Home';

  @override
  String get howItWorks => 'How it works';

  @override
  String get listings => 'Listings';

  @override
  String get profile => 'Profile';

  @override
  String get about => 'About';

  @override
  String get logout => 'Logout';

  @override
  String get cropListings => 'Crop Listings';

  @override
  String get noCropListings => 'No crop listings yet';

  @override
  String get createListing => 'Create Listing';

  @override
  String get myProfile => 'My Profile';

  @override
  String get next => 'Next';

  @override
  String get notLoggedIn => 'Not logged in';

  @override
  String get notifications => 'Notifications';

  @override
  String get login => 'Login';

  @override
  String get comingSoon => 'Coming Soon';

  @override
  String get connectWithMarkets => 'Connect with Local Markets';

  @override
  String get createAccount => 'Create Account';

  @override
  String get currentLanguage => 'English';

  @override
  String get dataSaverDescription => 'Reduce data usage';

  @override
  String get aboutDescription => 'Offline-first agritech logistics platform';

  @override
  String get aboutTagline =>
      'Connecting Ethiopian farmers, truckers, and buyers';

  @override
  String get activeListings => 'active listings';

  @override
  String get register => 'Register';

  @override
  String get phoneNumber => 'Phone Number';

  @override
  String get password => 'Password';

  @override
  String get confirmPassword => 'Confirm Password';

  @override
  String get fullName => 'Full Name';

  @override
  String get displayName => 'Display Name';

  @override
  String get displayNameHelper =>
      'Your marketplace nickname (visible to others)';

  @override
  String get verifiedLegalName => 'Verified Legal Name';

  @override
  String get selectRole => 'Select Role';

  @override
  String get farmer => 'Farmer';

  @override
  String get trucker => 'Trucker';

  @override
  String get updateDetails => 'Update your name and details';

  @override
  String get buyer => 'Buyer';

  @override
  String get enterOtp => 'Enter Verification Code';

  @override
  String get otpSent => 'Verification code sent to your phone';

  @override
  String get verifyOtp => 'Verify';

  @override
  String get viewOrderHistory => 'View your order history';

  @override
  String get voiceSupport => 'Voice Support in Your Language';

  @override
  String get resendOtp => 'Resend Code';

  @override
  String get signUp => 'Sign Up';

  @override
  String get alreadyHaveAccount => 'Already have an account?';

  @override
  String get dontHaveAccount => 'Don\'t have an account?';

  @override
  String get forgotPassword => 'Forgot Password?';

  @override
  String get getStarted => 'Get Started';

  @override
  String get loginOnline => 'Login Online';

  @override
  String get loginOffline => 'Login Offline';

  @override
  String get offlineModeIndicator => 'You are in offline mode';

  @override
  String get passwordRequirements =>
      'Password must be at least 8 characters with a lowercase letter and a number';

  @override
  String get phoneNumberRequired => 'Phone number is required';

  @override
  String get invalidPhoneFormat =>
      'Invalid phone number. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX';

  @override
  String get invalidCredentials => 'Invalid phone number or password';

  @override
  String get phoneAlreadyRegistered =>
      'This phone number is already registered';

  @override
  String get invalidOtp => 'Invalid verification code';

  @override
  String get otpExpired => 'Verification code expired. Request a new one';

  @override
  String get authRateLimitExceeded =>
      'Too many attempts. Please try again later.';

  @override
  String get authServerError => 'Server error. Please try again later.';

  @override
  String get authServiceUnavailable =>
      'Authentication service temporarily unavailable. Please try again later.';

  @override
  String get authNetworkError =>
      'No internet connection. Please check your connection.';

  @override
  String authAccountLocked(String duration) {
    return 'Account locked due to too many failed attempts. Try again in $duration.';
  }

  @override
  String durationSeconds(int count) {
    return '$count seconds';
  }

  @override
  String durationMinutes(int count) {
    return '$count minutes';
  }

  @override
  String durationHours(int count) {
    return '$count hours';
  }

  @override
  String get phoneNotVerified =>
      'Phone number not verified. Please complete OTP verification.';

  @override
  String get authApiNotConfigured =>
      'App setup incomplete. Please contact support.';

  @override
  String get authOfflineNotAvailable =>
      'Please log in online first to enable offline mode.';

  @override
  String get authAccountDisabled =>
      'Your account has been disabled. Contact support.';

  @override
  String get authPasswordResetRequired =>
      'Password reset required. Please reset your password.';

  @override
  String get authOtpAttemptsExceeded =>
      'Too many failed attempts. Request a new code.';

  @override
  String get registrationSuccess =>
      'Registration successful! Please verify your phone';

  @override
  String get loginSuccess => 'Login successful!';

  @override
  String get logoutConfirmation => 'Are you sure you want to logout?';

  @override
  String get manageAlerts => 'Manage your alerts';

  @override
  String get myListings => 'My Listings';

  @override
  String get browseListings => 'Browse Listings';

  @override
  String get market => 'Market';

  @override
  String get marketCrops => 'Crops';

  @override
  String get marketTransport => 'Transport';

  @override
  String get orders => 'Orders';

  @override
  String get myOrders => 'My Orders';

  @override
  String get clearOfflineData => 'Clear offline data?';

  @override
  String get clearOfflineDataDescription =>
      'You can still access your account offline if you don\'t clear this data';

  @override
  String get ethiopianPhoneExample => 'Example: 0911234567 or +251911234567';

  @override
  String get characters => 'characters';

  @override
  String get cropTypeTeff => 'Teff';

  @override
  String get cropTypeWheat => 'Wheat';

  @override
  String get cropTypeBarley => 'Barley';

  @override
  String get cropTypeMaize => 'Maize';

  @override
  String get cropTypeChickpea => 'Chickpea';

  @override
  String get cropTypeLentils => 'Lentils';

  @override
  String get cropTypeSorghum => 'Sorghum';

  @override
  String get cropTypeCoffee => 'Coffee';

  @override
  String get cropTypeSesame => 'Sesame';

  @override
  String get selectCropType => 'Select Crop Type';

  @override
  String get quantityInQuintals => 'Quantity (quintals)';

  @override
  String get priceInBirr => 'Price (ETB/kg)';

  @override
  String get location => 'Location';

  @override
  String get description => 'Description';

  @override
  String get audioNote => 'Audio Note';

  @override
  String get recordAudioNote => 'Record Audio Note';

  @override
  String get cropTypeRequired => 'Please select a crop type';

  @override
  String get quantityRequired => 'Please enter the quantity';

  @override
  String get priceRequired => 'Price is required';

  @override
  String get locationRequired => 'Please enter the location';

  @override
  String get listingSaved => 'Listing saved successfully';

  @override
  String get listingDeleted => 'Listing deleted';

  @override
  String get allCrops => 'All';

  @override
  String get quintals => 'quintals';

  @override
  String get etbPerKg => 'ETB/kg';

  @override
  String get exploreAsGuest => 'Explore as Guest';

  @override
  String get notAuthenticated => 'You must be logged in to perform this action';

  @override
  String get passwordsDoNotMatch => 'Passwords do not match';

  @override
  String get enterSixDigitCode => 'Please enter 6-digit code';

  @override
  String resendCodeCountdown(int seconds) {
    return 'Resend code in $seconds seconds';
  }

  @override
  String get listingStatusActive => 'Active';

  @override
  String get listingStatusDraft => 'Draft';

  @override
  String get listingStatusSold => 'Sold';

  @override
  String get listingStatusReserved => 'Reserved';

  @override
  String get listingStatusExpired => 'Expired';

  @override
  String get listingFilterAll => 'All';

  @override
  String get listingSeedTestButton => 'Seed Test Listings';

  @override
  String get backToHome => 'Back to Home';

  @override
  String get tripListings => 'Trip Listings';

  @override
  String get createTrip => 'Create Trip';

  @override
  String get browseTrips => 'Browse Trips';

  @override
  String get myTrips => 'My Trips';

  @override
  String get tripDetails => 'Trip Details';

  @override
  String get fromWoreda => 'From (Origin)';

  @override
  String get toWoreda => 'To (Destination)';

  @override
  String get departureDate => 'Departure Date';

  @override
  String get capacity => 'Capacity (quintals)';

  @override
  String get pricePerQuintal => 'Price per Quintal (ETB)';

  @override
  String get tripStatusOpen => 'Open';

  @override
  String get tripStatusMatched => 'Matched';

  @override
  String get tripStatusInTransit => 'In Transit';

  @override
  String get tripStatusDelivered => 'Delivered';

  @override
  String get tripStatusCancelled => 'Cancelled';

  @override
  String get noTripsFound => 'No trips found';

  @override
  String get tripCreatedSuccess => 'Trip created successfully!';

  @override
  String get tripCancelledSuccess => 'Trip cancelled';

  @override
  String get filterByRoute => 'Filter by route';

  @override
  String get availableCapacity => 'Available capacity';

  @override
  String get selectDepartureDate => 'Select departure date';

  @override
  String get truckerContact => 'Trucker Contact';

  @override
  String get fromWoredaRequired => 'Origin woreda is required';

  @override
  String get toWoredaRequired => 'Destination woreda is required';

  @override
  String get departureDateRequired => 'Departure date is required';

  @override
  String get capacityRequired => 'Capacity is required';

  @override
  String get invalidCapacity =>
      'Capacity must be between 0.1 and 1000 quintals';

  @override
  String get invalidPrice => 'Price must be between 0.01 and 10000 ETB';

  @override
  String get sameOriginDestination =>
      'Origin and destination cannot be the same';

  @override
  String get findTransport => 'Find Transport';

  @override
  String get viewCropListings => 'View your crop listings';

  @override
  String get cancelTrip => 'Cancel Trip';

  @override
  String get cancelTripConfirmation =>
      'Are you sure you want to cancel this trip? This action cannot be undone.';

  @override
  String get truckerName => 'Trucker Name';

  @override
  String get truckerPhone => 'Trucker Phone';

  @override
  String get route => 'Route';

  @override
  String get viewTripListings => 'View your trip listings';

  @override
  String get trips => 'Trips';

  @override
  String get truckerNameRequired => 'Trucker name is required';

  @override
  String get vehiclePlate => 'Vehicle Plate';

  @override
  String get vehicleDescription => 'Vehicle Description';

  @override
  String get deliveries => 'Deliveries';

  @override
  String get deliveryMyDeliveries => 'My Deliveries';

  @override
  String get deliveryDetails => 'Delivery Details';

  @override
  String get deliveryScanQrCode => 'Scan QR Code';

  @override
  String get deliveryShowQrCode => 'QR Code';

  @override
  String get deliveryRaiseDispute => 'Raise Dispute';

  @override
  String get deliveryStatusPending => 'Pending';

  @override
  String get deliveryStatusPickedUp => 'Picked Up';

  @override
  String get deliveryStatusInTransit => 'In Transit';

  @override
  String get deliveryStatusDelivered => 'Delivered';

  @override
  String get deliveryStatusDisputed => 'Disputed';

  @override
  String get deliveryStatusCancelled => 'Cancelled';

  @override
  String get deliveryConfirmPickup => 'Confirm Pickup';

  @override
  String get deliveryConfirmDelivery => 'Confirm Delivery';

  @override
  String get deliveryScanQr => 'Scan QR';

  @override
  String get deliveryShowQr => 'Show QR Code';

  @override
  String get deliveryRaiseDisputeAction => 'Submit Dispute';

  @override
  String get deliveryCancel => 'Cancel Delivery';

  @override
  String get deliveryTakePhoto => 'Take Photo';

  @override
  String get deliveryTimeLabel => 'Time';

  @override
  String get deliveryRegenerateQr => 'Regenerate QR';

  @override
  String get deliveryFarmer => 'Farmer';

  @override
  String get deliveryTrucker => 'Trucker';

  @override
  String get deliveryBuyer => 'Buyer';

  @override
  String deliveryGpsAccuracy(String meters) {
    return 'Accuracy: ${meters}m';
  }

  @override
  String get deliveryGpsCaptured => 'GPS Captured';

  @override
  String get deliveryGpsUnavailable => 'GPS unavailable';

  @override
  String get deliveryGpsWithinRange => 'Within range';

  @override
  String get deliveryGpsOutOfRange => 'Out of range';

  @override
  String get deliveryGpsCapturing => 'Capturing GPS...';

  @override
  String get deliveryGpsLabel => 'GPS';

  @override
  String get deliveryGpsNotCaptured => 'Not captured';

  @override
  String get deliveryGpsPermissionDenied => 'Location permission denied';

  @override
  String get deliveryQrShowInstruction => 'Show this QR code to the trucker';

  @override
  String get deliveryQrOfflineNote =>
      'This QR code works offline. No internet needed.';

  @override
  String get deliveryQrVerified => 'Verification Passed';

  @override
  String get deliveryQrFailed => 'Verification Failed';

  @override
  String get deliveryQrPointCamera => 'Point camera at the QR code';

  @override
  String get deliveryDisputeReason => 'Reason for Dispute';

  @override
  String get deliveryDisputeTimeRemaining => 'Time remaining';

  @override
  String get deliveryDisputeExpired => 'Dispute window expired';

  @override
  String get deliveryDisputeWarning =>
      'Disputes are reviewed by WeredaConnect. Provide clear details.';

  @override
  String get deliveryDisputeSubmit => 'Submit Dispute';

  @override
  String get deliveryFilterAll => 'All';

  @override
  String get deliveryFilterPending => 'Pending';

  @override
  String get deliveryFilterPickedUp => 'Picked Up';

  @override
  String get deliveryFilterInTransit => 'In Transit';

  @override
  String get deliveryFilterDelivered => 'Delivered';

  @override
  String get deliveryFilterRole => 'Filter by Role';

  @override
  String get deliveryNoDeliveries => 'No deliveries yet';

  @override
  String get deliveryNoDeliveriesHint => 'Your deliveries will appear here';

  @override
  String get deliveryOfflineNote => 'Works offline';

  @override
  String get deliveryCreatedAt => 'Created';

  @override
  String get deliveryCancelConfirmTitle => 'Cancel Delivery?';

  @override
  String get deliveryCancelConfirmMessage =>
      'Are you sure you want to cancel this delivery?';

  @override
  String get deliveryDisputeConfirmTitle => 'Submit Dispute?';

  @override
  String get deliveryDisputeConfirmMessage =>
      'Are you sure you want to raise a dispute?';

  @override
  String get deliveryPhotoPickup => 'Pickup Photo';

  @override
  String get deliveryPhotoDropoff => 'Delivery Photo';

  @override
  String get deliveryAddPhoto => 'Add Photo';

  @override
  String get deliveryMarkInTransit => 'Mark In Transit';

  @override
  String get deliveryInTransitConfirmMessage =>
      'Are you sure you want to mark this delivery as in transit?';

  @override
  String get deliveryReasonRequired => 'Please enter a reason';

  @override
  String get deliveryReasonTooLong => 'Reason must be 500 characters or less';

  @override
  String get deliveryTryAgain => 'Try Again';

  @override
  String deliveryIdLabel(String id) {
    return 'Delivery #$id';
  }

  @override
  String get deliveryPickupSection => 'Pickup';

  @override
  String get deliveryDeliverySection => 'Delivery';

  @override
  String get deliveryNotYetPickedUp => 'Not yet picked up';

  @override
  String get deliveryNotYetDelivered => 'Not yet delivered';

  @override
  String get deliveryToggleFlash => 'Toggle flash';

  @override
  String deliveryConfirmedAtLabel(String date) {
    return 'Confirmed: $date';
  }

  @override
  String get deliverySeedTestButton => 'Seed Test Delivery';

  @override
  String get escrowPayments => 'Payments';

  @override
  String get escrowPayForDelivery => 'Pay for Delivery';

  @override
  String get escrowPaymentStatus => 'Payment Status';

  @override
  String get escrowPaymentHistory => 'My Payments';

  @override
  String get escrowRaiseDispute => 'Raise Dispute';

  @override
  String get escrowStatusInitiated => 'Payment Pending';

  @override
  String get escrowStatusEscrowed => 'Funds Held Safely';

  @override
  String get escrowStatusReleased => 'Funds Released';

  @override
  String get escrowStatusCompleted => 'Payment Complete';

  @override
  String get escrowStatusCancelled => 'Cancelled';

  @override
  String get escrowStatusDisputed => 'Under Dispute';

  @override
  String get escrowStatusRefunded => 'Refunded';

  @override
  String get escrowProtectionInfo =>
      'Your payment is held safely until delivery is confirmed.';

  @override
  String get escrowTotalAmount => 'Total Amount';

  @override
  String escrowCommission(String rate) {
    return 'Commission ($rate%)';
  }

  @override
  String get escrowFarmerReceives => 'Farmer Receives';

  @override
  String get escrowConfirmPayment => 'Confirm Payment';

  @override
  String escrowConfirmPaymentDialog(String amount, String farmerName) {
    return 'Pay ETB $amount to $farmerName?';
  }

  @override
  String get escrowFaydaWarning =>
      'Fayda ID verification required for amounts above ETB 10,000.';

  @override
  String get escrowFundsHeldInfo =>
      'Funds held for 7 days maximum. Refunded if not delivered.';

  @override
  String get escrowPaymentQueued =>
      'Payment queued. Will process when you have internet.';

  @override
  String get escrowPaymentSuccess => 'Payment initiated successfully.';

  @override
  String get escrowDeliveryLabel => 'Delivery';

  @override
  String get escrowFarmerLabel => 'Farmer';

  @override
  String get escrowBuyerLabel => 'Buyer';

  @override
  String get escrowCreatedLabel => 'Created';

  @override
  String get escrowExpiresLabel => 'Expires';

  @override
  String get escrowTelebirrRefLabel => 'Telebirr Ref';

  @override
  String get escrowMayBeOutdated => 'Status may be outdated. Pull to refresh.';

  @override
  String escrowLastUpdated(String time) {
    return 'Last updated $time';
  }

  @override
  String get escrowFilterAll => 'All';

  @override
  String get escrowFilterBuyer => 'As Buyer';

  @override
  String get escrowFilterFarmer => 'As Farmer';

  @override
  String escrowPendingSection(int count) {
    return 'Pending ($count)';
  }

  @override
  String get escrowHistorySection => 'History';

  @override
  String get escrowNoPayments => 'No payments yet.';

  @override
  String get escrowNoMatchingPayments => 'No matching payments.';

  @override
  String get escrowCancelIntent => 'Cancel';

  @override
  String get escrowCancelIntentDialog => 'Cancel this queued payment?';

  @override
  String get escrowIntentCancelled => 'Queued payment cancelled.';

  @override
  String escrowDisputeTimeRemaining(int hours, int minutes) {
    return 'Time remaining: ${hours}h ${minutes}m';
  }

  @override
  String get escrowDisputeWindowExpired => 'Dispute window expired.';

  @override
  String get escrowDisputeReasonLabel => 'Reason';

  @override
  String get escrowDisputeDescriptionLabel => 'Description (optional)';

  @override
  String get escrowDisputeDescriptionHint => 'Describe the issue...';

  @override
  String get escrowDisputeSubmit => 'Submit Dispute';

  @override
  String get escrowDisputeConfirmDialog =>
      'This will freeze funds pending review. Continue?';

  @override
  String get escrowDisputeSuccess =>
      'Dispute registered. Platform will review within 48 hours.';

  @override
  String get escrowDisputeReviewInfo =>
      'Disputes are reviewed by the platform within 48 hours.';

  @override
  String get escrowDisputeRequiresInternet =>
      'Internet connection required to raise disputes.';

  @override
  String get escrowReasonDamagedGoods => 'Damaged goods';

  @override
  String get escrowReasonWrongQuantity => 'Wrong quantity';

  @override
  String get escrowReasonWrongCrop => 'Wrong crop type';

  @override
  String get escrowReasonNotDelivered => 'Not delivered';

  @override
  String get escrowReasonQualityMismatch => 'Quality mismatch';

  @override
  String get escrowReasonOther => 'Other';

  @override
  String get escrowErrorInvalidAmount =>
      'Amount must be between ETB 100 and ETB 500,000.';

  @override
  String get escrowErrorFaydaRequired =>
      'Fayda ID verification required for amounts above ETB 10,000.';

  @override
  String get escrowErrorAlreadyExists =>
      'Payment already exists for this delivery.';

  @override
  String get escrowErrorGateway => 'Telebirr service error. Please try again.';

  @override
  String get escrowErrorServiceUnavailable =>
      'Telebirr is currently unavailable. Please try later.';

  @override
  String get escrowErrorGeneric => 'Something went wrong. Please try again.';

  @override
  String get escrowErrorAuthRequired => 'Please log in to continue.';

  @override
  String get escrowErrorConflict => 'This request is already being processed.';

  @override
  String get escrowErrorRateLimit =>
      'Too many requests. Please wait and try again.';

  @override
  String get escrowErrorOffline =>
      'Internet connection required for this action.';

  @override
  String get escrowErrorInvalidDelivery => 'Invalid delivery reference.';

  @override
  String get escrowErrorUnauthorized =>
      'You are not authorized for this action.';

  @override
  String get escrowErrorDeliveryNotFound => 'Delivery not found.';

  @override
  String get escrowErrorInvalidDeliveryState =>
      'Delivery is not in a valid state for payment.';

  @override
  String get escrowErrorInvalidDisputeReason =>
      'Please select a valid dispute reason.';

  @override
  String get escrowErrorDisputeWindowExpired =>
      'The dispute window has expired (48 hours after delivery).';

  @override
  String get escrowErrorDisputeAlreadyExists =>
      'A dispute has already been raised for this payment.';

  @override
  String get escrowErrorInvalidTransition =>
      'This action is not allowed in the current payment state.';

  @override
  String escrowEtbAmount(String amount) {
    return 'ETB $amount';
  }

  @override
  String get profileRestartApp => 'Please restart the app';

  @override
  String get profileLoadError => 'Could not load user profile';

  @override
  String get escrowTimeline => 'Timeline';

  @override
  String get escrowDisputeDetailsTitle => 'Dispute Details';

  @override
  String get recording => 'Recording...';

  @override
  String get stopRecording => 'Stop Recording';

  @override
  String get playAudio => 'Play';

  @override
  String get pauseAudio => 'Pause';

  @override
  String get deleteRecording => 'Delete Recording';

  @override
  String get deleteRecordingConfirm => 'Delete this voice note?';

  @override
  String get microphonePermissionRequired =>
      'Microphone permission is required to record audio';

  @override
  String get audioRecorded => 'Voice note recorded';

  @override
  String get audioTooLong => 'Maximum recording length is 30 seconds';

  @override
  String get audioRecordingLimit => 'Maximum 30 seconds';

  @override
  String get reRecord => 'Re-record';

  @override
  String get audioRecordingFailed => 'Recording failed. Please try again.';

  @override
  String secondsAbbreviation(int count) {
    return '${count}s';
  }

  @override
  String get dataUsageDownloaded => 'Downloaded';

  @override
  String get dataUsageUploaded => 'Uploaded';

  @override
  String get dataUsageTotal => 'Total';

  @override
  String get dataUsageReset => 'Reset';

  @override
  String get dataUsageResetConfirm => 'Clear all data usage statistics?';

  @override
  String get dataUsageTrackingSince => 'Tracking since';

  @override
  String get conflictTitle => 'Data Updated';

  @override
  String get conflictExplanation =>
      'Another device or the server updated this record. Your local copy has been updated to match.';

  @override
  String get conflictAccept => 'OK, Got It';

  @override
  String get conflictDismiss => 'Dismiss';

  @override
  String get conflictEntityCropListing => 'Crop Listing';

  @override
  String get conflictEntityDelivery => 'Delivery';

  @override
  String get conflictEntityTrip => 'Trip';

  @override
  String get conflictServerValues => 'Updated values from server:';

  @override
  String get conflictNoDetails => 'No details available.';

  @override
  String conflictBannerSingle(String entityType) {
    return 'Your $entityType was updated on another device';
  }

  @override
  String conflictBannerMultiple(int count) {
    return '$count items were updated on another device';
  }

  @override
  String get conflictBannerAction => 'View Changes';

  @override
  String get conflictScreenTitle => 'Updates from Sync';

  @override
  String get conflictYourVersion => 'Your version';

  @override
  String get conflictCurrentVersion => 'Current version';

  @override
  String get conflictServerApplied => 'Server version applied';

  @override
  String get conflictGotIt => 'Got It';

  @override
  String get conflictEmptyTitle => 'Everything is up to date';

  @override
  String get conflictEmptySubtitle => 'No changes from other devices.';

  @override
  String conflictEntityDeleted(String entityType) {
    return 'This $entityType was removed on another device';
  }

  @override
  String get conflictShowAllChanges => 'Show all changes';

  @override
  String get fraudDuplicateListing =>
      'A similar listing already exists. Please update your existing listing instead.';

  @override
  String get fraudVelocityExceeded =>
      'Too many requests. Please wait a moment and try again.';

  @override
  String get profileSaved => 'Profile updated';

  @override
  String get noNotificationsYet => 'No notifications yet';

  @override
  String get noNotificationsDescription => 'You\'ll see your alerts here';

  @override
  String get createDelivery => 'Create Delivery';

  @override
  String get contactFarmer => 'Contact Farmer';

  @override
  String get detailQuantity => 'Quantity';

  @override
  String get detailPrice => 'Price';

  @override
  String get detailCreated => 'Created';

  @override
  String get filterMin => 'Min';

  @override
  String get filterMax => 'Max';

  @override
  String get selectTrip => 'Select Trip';

  @override
  String get buyerInformation => 'Buyer Information';

  @override
  String get buyerNameLabel => 'Buyer Name';

  @override
  String get buyerPhoneIdLabel => 'Buyer Phone / ID';

  @override
  String get buyerNameRequired => 'Buyer name is required';

  @override
  String get buyerIdRequired => 'Buyer phone or ID is required';

  @override
  String get tripSelectionRequired => 'Please select a trip';

  @override
  String get chooseTripHint => 'Choose a trip';

  @override
  String get noOpenTrips => 'No open trips available. Create a trip first.';

  @override
  String get deliveryCreatedSuccess => 'Delivery created successfully';

  @override
  String get farmerContactInfo => 'Farmer Contact';

  @override
  String get copiedToClipboard => 'Copied to clipboard';

  @override
  String get forgotPasswordContactSupport =>
      'Please contact support at +251-111-234567 to reset your password.';

  @override
  String resendOtpIn(int seconds) {
    return 'Resend in ${seconds}s';
  }

  @override
  String get otpResent => 'OTP resent successfully';

  @override
  String get ok => 'OK';

  @override
  String get tutorialComingSoon =>
      'Tutorial carousel coming soon. You can explore the app by creating an account!';

  @override
  String get escrowDescInitiated => 'Waiting for Telebirr confirmation';

  @override
  String get escrowDescEscrowed => 'Funds held safely until delivery';

  @override
  String get escrowDescReleased => 'Funds sent to farmer';

  @override
  String get escrowDescCompleted => 'Transaction settled';

  @override
  String get escrowDescCancelled => 'Payment was cancelled';

  @override
  String get escrowDescDisputed => 'Under review by platform';

  @override
  String get escrowDescRefunded => 'Funds returned to buyer';

  @override
  String get conflictFieldCropType => 'Crop Type';

  @override
  String get conflictFieldQuantity => 'Quantity';

  @override
  String get conflictFieldPricePerKg => 'Price/kg';

  @override
  String get conflictFieldStatus => 'Status';

  @override
  String get conflictFieldRoute => 'Route';

  @override
  String get conflictFieldCapacity => 'Capacity';

  @override
  String get conflictFieldTrip => 'Trip';

  @override
  String get escrowPayButton => 'Pay';

  @override
  String escrowDisputeReasonPrefix(String reason) {
    return 'Reason: $reason';
  }

  @override
  String escrowDeliveryNumber(String id) {
    return 'Delivery #$id';
  }

  @override
  String escrowPaymentTo(String name) {
    return 'To: $name';
  }

  @override
  String escrowPaymentAmount(String amount) {
    return 'Payment: ETB $amount';
  }

  @override
  String escrowDeliveryShort(String id) {
    return 'Del. #$id';
  }

  @override
  String get escrowQueuedStatus => 'Queued';

  @override
  String get genericError => 'Something went wrong. Please try again.';

  @override
  String get authErrorRegistrationFailed =>
      'Registration failed. Please try again.';

  @override
  String get authErrorOtpFailed => 'OTP verification failed. Please try again.';

  @override
  String get authErrorResendOtpFailed =>
      'Failed to resend OTP. Please try again.';

  @override
  String get authErrorLoginFailed => 'Login failed. Please try again.';

  @override
  String get authErrorOfflineLoginFailed =>
      'Offline login failed. Please log in online first.';

  @override
  String get authErrorSecureStorage =>
      'Error accessing secure storage. Please restart the app.';

  @override
  String get authErrorDatabase => 'Database error. Please restart the app.';

  @override
  String get authErrorUserNotConfirmed =>
      'Please verify your phone number first.';

  @override
  String get authErrorTokenExpired => 'Session expired. Please log in again.';

  @override
  String get deliveryErrorInvalidId => 'Invalid delivery ID';

  @override
  String get deliveryErrorStatusInvalid =>
      'Invalid delivery status for this action';

  @override
  String get deliveryErrorUnauthorized =>
      'You are not authorized for this action';

  @override
  String get deliveryErrorDisputeExpired => 'Dispute window has expired';

  @override
  String get deliveryErrorDisputeReasonRequired => 'Please provide a reason';

  @override
  String get deliveryErrorGpsInvalid => 'Invalid GPS coordinates';

  @override
  String get deliveryErrorGeneric => 'Delivery operation failed';

  @override
  String get deliveryErrorNotFound => 'Delivery not found';

  @override
  String get deliveryQrTooLarge => 'QR code too large';

  @override
  String get deliveryQrIncomplete => 'Incomplete QR data';

  @override
  String get deliveryQrInvalidData => 'Invalid QR data';

  @override
  String get deliveryQrWrongDelivery => 'QR code does not match this delivery';

  @override
  String get deliveryQrInvalidFormat => 'Invalid QR code format';

  @override
  String get deliveryQrProcessFailed => 'Could not process QR code';

  @override
  String get escrowPaymentNotFound => 'Payment not found';

  @override
  String get escrowPaymentNotFoundForDelivery =>
      'Payment not found for this delivery';

  @override
  String get pleaseWait => 'Please wait...';

  @override
  String get greeting => 'Selam';

  @override
  String get homeSubtitle => 'What would you like to do today?';

  @override
  String get bannerHeadline => 'NO MORE DALLALA';

  @override
  String get bannerSubtitle => 'Fair prices directly to farmers';

  @override
  String get quickActions => 'Quick Actions';

  @override
  String get overview => 'Overview';

  @override
  String get recentActivity => 'Recent';

  @override
  String get noRecentActivity => 'No recent activity';

  @override
  String get sell => 'Sell';

  @override
  String get buy => 'Buy';

  @override
  String get truckers => 'Truckers';

  @override
  String get escrow => 'Escrow';

  @override
  String get availableLoads => 'Loads';

  @override
  String get activeFarmers => 'Active Farmers';

  @override
  String get inTransit => 'In Transit';

  @override
  String get matchedLoads => 'Matched';

  @override
  String get farmersBrowsed => 'Available Crops';

  @override
  String get marketplace => 'Marketplace';

  @override
  String get browseVerifiedFarmers => 'Browse verified farmers';

  @override
  String get manageYourListings => 'Manage your listings';

  @override
  String get searchListings => 'Search crops, farmers...';

  @override
  String get trackTradesEscrow => 'Track trades & escrow';

  @override
  String get active => 'Active';

  @override
  String get done => 'Done';

  @override
  String get all => 'All';

  @override
  String get noOrders => 'No orders yet';

  @override
  String get ordersWillAppear => 'Your trades will appear here';

  @override
  String get trades => 'Trades';

  @override
  String get rating => 'Rating';

  @override
  String get escrowPercent => 'Escrow %';

  @override
  String get wallet => 'Wallet';

  @override
  String get escrowHistory => 'Escrow History';

  @override
  String get ratings => 'Ratings';

  @override
  String get documents => 'Documents';

  @override
  String get idAndCerts => 'ID, Certificates';

  @override
  String get languageAndAlerts => 'Language, Alerts';

  @override
  String get help => 'Help';

  @override
  String get faqAndSupport => 'FAQ, Support';

  @override
  String get continueButton => 'Continue';

  @override
  String get selectRoleDescription => 'Choose how you\'ll use WeredaConnect';

  @override
  String get farmerDescription => 'List crops for sale and find truckers';

  @override
  String get truckerDescription => 'Offer transport and deliver goods';

  @override
  String get buyerDescription => 'Browse and purchase crops from farmers';

  @override
  String get faydaIdVerification => 'Fayida ID Verification';

  @override
  String get faydaIdInstructions =>
      'Scan your Fayida ID card or enter the number manually';

  @override
  String get scanFront => 'Scan Front';

  @override
  String get scanBack => 'Scan Back';

  @override
  String get enterManually => 'Enter ID manually';

  @override
  String get faydaIdNumber => 'Fayida ID Number';

  @override
  String get drivingLicense => 'Driving License';

  @override
  String get drivingLicenseInstructions =>
      'Scan your driving license to verify your identity';

  @override
  String get buyerType => 'Buyer Type';

  @override
  String get buyerTypeDescription =>
      'Are you buying as an individual or company?';

  @override
  String get privateBuyer => 'Private Buyer';

  @override
  String get companyBuyer => 'Company';

  @override
  String get companyName => 'Company Name';

  @override
  String get personalInfo => 'Personal Info';

  @override
  String get personalInfoDescription =>
      'Enter your details to create an account';

  @override
  String get verified => 'Verified';

  @override
  String get scanLicense => 'Scan License';

  @override
  String get statusFunded => 'Funded';

  @override
  String get statusPlaced => 'Placed';

  @override
  String get statusAssigned => 'Assigned';

  @override
  String get statusTransit => 'Transit';

  @override
  String get statusDisputed => 'Disputed';

  @override
  String deliveryTitle(int id) {
    return 'Delivery #$id';
  }

  @override
  String paymentTitle(int id) {
    return 'Payment #$id';
  }

  @override
  String listingCreatedActivity(String cropType) {
    return '$cropType listing created';
  }

  @override
  String deliveryStatusActivity(String status) {
    return 'Delivery $status';
  }

  @override
  String minutesAgo(int count) {
    return '${count}m ago';
  }

  @override
  String hoursAgo(int count) {
    return '${count}h ago';
  }

  @override
  String daysAgo(int count) {
    return '${count}d ago';
  }

  @override
  String get fieldRequired => 'This field is required';

  @override
  String get clearAll => 'Clear all';

  @override
  String get myDeliveries => 'My Deliveries';

  @override
  String get myPayments => 'My Payments';

  @override
  String get trackDeliveries => 'Track your crop pickups & deliveries';

  @override
  String get trackPayments => 'Track your earnings & escrow';

  @override
  String get trackBuyerPayments => 'Track your payments & escrow';

  @override
  String get noDeliveries => 'No deliveries yet';

  @override
  String get noPayments => 'No payments yet';

  @override
  String get postTrip => 'Post Trip';

  @override
  String get browseMarket => 'Browse';

  @override
  String get yourCropListings => 'Your crop listings';

  @override
  String get payments => 'Payments';

  @override
  String get trackDelivery => 'Track Delivery';

  @override
  String get checkBalance => 'Check Balance';

  @override
  String get manageOrders => 'Manage incoming orders';

  @override
  String get trackYourOrders => 'Track your orders';

  @override
  String get orderPending => 'Pending';

  @override
  String get orderAccepted => 'Accepted';

  @override
  String get orderRejected => 'Rejected';

  @override
  String get acceptOrder => 'Accept';

  @override
  String get rejectOrder => 'Reject';

  @override
  String get splashHeadline => 'Grow it. Move it.\nBuy it. Done.';

  @override
  String get splashSubtitle =>
      'One platform for farmers, truckers, and buyers — with secure escrow.';

  @override
  String get orderFunded => 'Funded';

  @override
  String get orderCancelled => 'Cancelled';

  @override
  String get orderCompleted => 'Completed';

  @override
  String get faydaVerifyTitle => 'Fayda Identity Verification';

  @override
  String get faydaVerifyDescription =>
      'Verify your identity using your Fayda digital ID. You will be redirected to the Fayda portal.';

  @override
  String get verifyWithFayda => 'Verify with Fayda';

  @override
  String get faydaVerifying => 'Verifying with Fayda...';

  @override
  String get faydaVerifiedSuccess => 'Identity Verified';

  @override
  String get faydaVerificationFailed =>
      'Fayda verification failed. Please try again.';

  @override
  String get faydaRequired =>
      'Verify your Fayda ID to unlock trades above 10,000 ETB. You can skip this and verify later.';

  @override
  String get faydaUnavailable =>
      'Fayda service is currently unavailable. Please try again later.';

  @override
  String get faydaAlreadyLinked =>
      'This Fayda ID is already linked to another account.';

  @override
  String get placeOrder => 'Place Order';

  @override
  String get cropCost => 'Crop Cost';

  @override
  String get transportCost => 'Transport Cost';

  @override
  String get platformCommission => 'Platform Commission (5%)';

  @override
  String get orderTotal => 'Total';

  @override
  String get payNow => 'Pay Now';

  @override
  String get payLater => 'Pay Later';

  @override
  String get cbePaymentInstructions =>
      'Transfer to the Commercial Bank of Ethiopia account below. Use the reference number so we can match your payment.';

  @override
  String get enterTransactionId => 'Enter CBE Transaction ID';

  @override
  String get submitPayment => 'Submit Payment';

  @override
  String get statusCreated => 'Created';

  @override
  String get statusPendingPayment => 'Pending Payment';

  @override
  String get statusPaymentSubmitted => 'Payment Submitted';

  @override
  String get statusPickupConfirmed => 'Pickup Confirmed';

  @override
  String get statusInTransit => 'In Transit';

  @override
  String get statusDelivered => 'Delivered';

  @override
  String get statusDisbursing => 'Disbursing';

  @override
  String get statusSettled => 'Settled';

  @override
  String get statusPaymentFailed => 'Payment Failed';

  @override
  String get statusDisputeResolved => 'Dispute Resolved';

  @override
  String get cancelOrder => 'Cancel Order';

  @override
  String get cancelReason => 'Reason for cancellation';

  @override
  String get orderDetails => 'Order Details';

  @override
  String get priceBreakdown => 'Price Breakdown';

  @override
  String get cbeAccountNumber => 'CBE Account Number';

  @override
  String get cbeAccountValue => '1000XXXXXXXXXX';

  @override
  String get transferReference => 'Reference';

  @override
  String get paymentAmount => 'Amount';

  @override
  String get quantity => 'Quantity (kg)';

  @override
  String get listingSummary => 'Listing Summary';

  @override
  String get orderPlacedSuccess => 'Order placed successfully!';

  @override
  String get paymentSubmittedSuccess => 'Payment submitted successfully!';

  @override
  String get orderCancelledSuccess => 'Order cancelled.';

  @override
  String get bankName => 'Commercial Bank of Ethiopia';

  @override
  String get paymentInstructions => 'Payment Instructions';

  @override
  String get transactionIdRequired => 'Transaction ID is required';

  @override
  String get orderTimeline => 'Order Timeline';

  @override
  String etbCurrency(String amount) {
    return '$amount ETB';
  }

  @override
  String quantityKg(String amount) {
    return '$amount kg';
  }

  @override
  String get pendingOrders => 'Pending Orders';

  @override
  String get activeOrders => 'Active Orders';

  @override
  String get listingNotFound => 'Listing not found';

  @override
  String get unauthorizedAccess => 'You are not authorized to view this order.';

  @override
  String get whatDoYouNeed => 'What do you need?';

  @override
  String get whatDoYouNeedSubtitle =>
      'Tell us what you\'re looking for and we\'ll find matches';

  @override
  String get findMatches => 'Find Matches';

  @override
  String get quantityNeeded => 'Quantity needed (quintals)';

  @override
  String get maxPrice => 'Maximum price (ETB/kg)';

  @override
  String get deliverTo => 'Deliver to';

  @override
  String matchesFound(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count matches found',
      one: '1 match found',
      zero: 'No matches found',
    );
    return '$_temp0';
  }

  @override
  String get noMatchesYet => 'No matches yet';

  @override
  String newMatchesCount(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count new',
      one: '1 new',
    );
    return '$_temp0';
  }

  @override
  String get suggestedTransport => 'Suggested Transport';

  @override
  String get selectThisTrip => 'Select This Trip';

  @override
  String get estimatedTotal => 'Estimated Total';

  @override
  String tripsAvailable(int count) {
    return '$count trips available';
  }

  @override
  String get noTripsYet => 'No trips yet';

  @override
  String get placeOrderWithTransport => 'Place Order + Transport';

  @override
  String get placeOrderNoTransport => 'Place Order (no transport)';

  @override
  String get activeBuyingIntents => 'Active Buying Intents';

  @override
  String get newBuyingIntent => 'New Buying Intent';

  @override
  String get cropCostLabel => 'Crop Cost';

  @override
  String get transportCostLabel => 'Transport Cost';

  @override
  String get commissionLabel => 'Commission (5%)';

  @override
  String syncedAgo(String time) {
    return 'Synced $time ago';
  }

  @override
  String get browseAllListings => 'Browse All Listings';

  @override
  String get browseAllTrips => 'Browse All Trips';

  @override
  String get intentSaved => 'Saved! We\'ll notify you of new matches';

  @override
  String get newMatchesAvailable => 'New matches available!';

  @override
  String get findCrops => 'Find Crops';

  @override
  String get findTrips => 'Find Trips';

  @override
  String get totalLabel => 'Total';

  @override
  String get startTrip => 'Start Trip';

  @override
  String get startTripConfirmation =>
      'Start this trip? You are confirming pickup.';

  @override
  String get tripStartedSuccess => 'Trip started! Drive safe.';

  @override
  String get markDelivered => 'Mark Delivered';

  @override
  String get markDeliveredConfirmation =>
      'Mark as delivered? Confirm drop-off.';

  @override
  String get tripDeliveredSuccess => 'Delivery confirmed!';

  @override
  String get waitingForMatch => 'Waiting for match...';

  @override
  String headingTo(String destination) {
    return 'Heading to $destination';
  }

  @override
  String get postNextTrip => 'Post Next Trip';

  @override
  String get recentRoutes => 'Recent Routes';

  @override
  String get noActiveTrip => 'No active trip';

  @override
  String get postATrip => 'Post a Trip';

  @override
  String get repeatLastTrip => 'Repeat Last Trip';

  @override
  String get quickFill => 'Quick Fill';

  @override
  String get somethingWentWrong => 'Something went wrong';

  @override
  String get checkConnectionRetry =>
      'Please check your connection and try again.';

  @override
  String get sellCrops => 'Sell Crops';

  @override
  String get sellYourHarvest => 'SELL YOUR HARVEST';

  @override
  String get tapToListCrops => 'List your crops and reach buyers directly';

  @override
  String get synced => 'Synced';

  @override
  String get offline => 'Offline';

  @override
  String syncFailureCount(int count) {
    return '$count operations need attention';
  }

  @override
  String syncPendingCount(int count) {
    return '$count waiting to sync';
  }

  @override
  String paymentStatusTitle(String status) {
    return '$status payment';
  }

  @override
  String get myCrops => 'My Crops';

  @override
  String get browseAll => 'Browse';

  @override
  String get myRequests => 'My Requests';

  @override
  String get enterPhoneForReset =>
      'Enter your phone number to receive a verification code';

  @override
  String get sendResetCode => 'Send Reset Code';

  @override
  String get resetCodeSent => 'Verification code sent to your phone';

  @override
  String get enterResetCode => 'Enter the verification code sent to your phone';

  @override
  String get verificationCode => 'Verification Code';

  @override
  String get newPassword => 'New Password';

  @override
  String get confirmNewPassword => 'Confirm New Password';

  @override
  String get resetPassword => 'Reset Password';

  @override
  String get passwordResetSuccess =>
      'Password reset successful! You can now log in.';

  @override
  String get codeRequired => 'Verification code is required';

  @override
  String get passwordTooShort => 'Password must be at least 8 characters';

  @override
  String get placeSearchHint => 'Search woreda or town';

  @override
  String get placeNoResults => 'No match — you can use what you typed';

  @override
  String get placeResultsLabel => 'Place results';
}

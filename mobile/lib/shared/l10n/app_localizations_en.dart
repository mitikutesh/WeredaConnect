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
  String get errorNoPermission => 'You do not have permission to view this.';

  @override
  String get errorSecureConnectionFailed =>
      'Could not make a secure connection to the server. Update the app or try another network.';

  @override
  String get noInternetConnection => 'No internet connection';

  @override
  String get errorSessionExpired =>
      'Your session expired. Please sign in again.';

  @override
  String get errorConflict =>
      'This was updated elsewhere. Refresh and try again.';

  @override
  String get errorOrderRefused =>
      'This order action isn\'t allowed now, so nothing was changed.';

  @override
  String get errorOrderNotPlaced =>
      'This order could not be placed, so it was removed. Please order again.';

  @override
  String get errorChangeNotSaved =>
      'This change could not be saved on the server.';

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
  String aboutMapDataCredit(String credit) {
    return 'Map data: $credit';
  }

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
  String get resetCodeInvalid =>
      'The verification code is incorrect. Please check it and try again.';

  @override
  String get resetCodeExpired =>
      'The verification code has expired. Please request a new one.';

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
  String get cropLocationCoordinatesRequired =>
      'GPS is unavailable. Pick your location from the list or turn on GPS.';

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
  String listsTripBooked(String booked, String capacity) {
    return '$booked of $capacity quintals booked';
  }

  @override
  String get listsRouteFrom => 'From';

  @override
  String get listsRouteTo => 'To';

  @override
  String get listsMyTripsPrivacyNote =>
      'Only you see your trips here. Buyers find them in the Market.';

  @override
  String get listsAllTripsNote => 'Every open trip from every trucker.';

  @override
  String get listsKeepTrip => 'Keep trip';

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
  String get restDeliveryTimeline => 'Delivery Timeline';

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
  String get deliverySelfPickupConfirm => 'Scan farmer\'s QR to collect';

  @override
  String get deliverySelfPickupPayFirst =>
      'Pay into escrow first, then scan at the farm to collect.';

  @override
  String get orderOpenDelivery => 'Open delivery';

  @override
  String get orderCollectMyself => 'Collect it myself';

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
  String get deliveryQrShowInstructionBuyer =>
      'Show this QR code to the buyer when they collect';

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
  String get restNoPhoto => 'No photo';

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
  String get escrowStatusHeld => 'Under Review';

  @override
  String get escrowStatusUnknown => 'Status Unknown';

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
  String get escrowTruckerReceives => 'Trucker Receives';

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
  String get escrowPaymentOpening => 'Opening Telebirr…';

  @override
  String get escrowPaymentOpenFailed =>
      'Could not open the payment page. Make sure Telebirr or a browser is installed.';

  @override
  String get escrowPaymentConfirmed =>
      'Payment confirmed. Funds are held safely.';

  @override
  String get escrowAwaitingPayment =>
      'Waiting for your Telebirr payment to be confirmed…';

  @override
  String get escrowOpenPaymentAgain => 'Open Telebirr payment';

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
  String get escrowErrorOrderAlreadyFunded =>
      'Payment for this order is already held in escrow.';

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
  String get escrowErrorPayoutInProgress =>
      'This payment is already being paid out by an admin, so it can no longer be disputed. Contact support.';

  @override
  String get escrowErrorDeliveryPaymentUnavailable =>
      'Online payment for a delivery is not available here. Pay this order from its order page instead.';

  @override
  String get escrowErrorDisputeBeforePickup =>
      'A dispute can be opened only after the goods are picked up. Before pickup, cancel the order instead, or contact support.';

  @override
  String get escrowErrorInvalidTransition =>
      'This action is not allowed in the current payment state.';

  @override
  String escrowEtbAmount(String amount) {
    return 'ETB $amount';
  }

  @override
  String get profileSectionAccount => 'Account';

  @override
  String get profileSectionMoney => 'Money';

  @override
  String get profileSectionApp => 'App';

  @override
  String get profileSettingsLanguageTime => 'Language and time';

  @override
  String get profileSettingsData => 'Data';

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
  String get dataUsageScopeNote =>
      'Counts this account\'s requests to WeredaConnect servers from this device. Your phone\'s total data use (app updates, images, other apps) may be higher. Sign-in traffic is not included.';

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
  String get conflictEntityOrder => 'Order';

  @override
  String get conflictEntityRating => 'Rating';

  @override
  String get conflictEntityPayment => 'Payment';

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
  String fraudVelocityExceeded(int minutes) {
    String _temp0 = intl.Intl.pluralLogic(
      minutes,
      locale: localeName,
      other: '$minutes minutes',
      one: '1 minute',
    );
    return 'Too many listings created. You can try again in $_temp0.';
  }

  @override
  String get fraudAccountHeld =>
      'Your account is under review, so this action is paused. Contact support.';

  @override
  String get profileSaved => 'Profile updated';

  @override
  String get noNotificationsYet => 'No notifications yet';

  @override
  String get noNotificationsDescription => 'You\'ll see your alerts here';

  @override
  String get createDelivery => 'Create Delivery';

  @override
  String get selectPaymentMethod => 'Select payment method';

  @override
  String get paymentMethodCbe => 'Commercial Bank of Ethiopia (CBE)';

  @override
  String get paymentMethodTelebirr => 'Telebirr';

  @override
  String get mockPaymentNotice =>
      'Demo mode: payments are simulated. No real transfer is made.';

  @override
  String get telebirrPayInstructions =>
      'Tap Submit Payment to simulate a Telebirr payment for this order.';

  @override
  String get transactionIdOptional => 'CBE Transaction ID (optional)';

  @override
  String get copy => 'Copy';

  @override
  String get call => 'Call';

  @override
  String get close => 'Close';

  @override
  String get farmerPhone => 'Farmer Phone';

  @override
  String get phoneNotAvailable => 'Phone number not available';

  @override
  String get rateFarmer => 'Rate Farmer';

  @override
  String get rateTrucker => 'Rate Trucker';

  @override
  String get submitRating => 'Submit Rating';

  @override
  String get ratingCommentHint => 'Add a comment (optional)';

  @override
  String get ratingThankYou => 'Thank you for your rating!';

  @override
  String get alreadyRated => 'Rated';

  @override
  String get noRatingsYet => 'No ratings yet';

  @override
  String get farmerRating => 'Farmer Rating';

  @override
  String ratingsCount(int count) {
    return '$count ratings';
  }

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
  String get escrowDescHeld =>
      'Payment paused while the platform reviews it. The money stays safe.';

  @override
  String get escrowDescUnknown =>
      'Update the app to see the latest status of this payment.';

  @override
  String escrowPayoutExpectedOn(String date) {
    return 'Payout expected on $date';
  }

  @override
  String get escrowStatusRefundPending => 'Refund Pending';

  @override
  String get escrowDescRefundPending =>
      'Order cancelled. Funds are being returned to the buyer';

  @override
  String escrowOrderShort(String id) {
    return 'Order #$id';
  }

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
  String get deliveryErrorQrPending =>
      'The farmer\'s QR code has not reached the server yet. Ask the farmer to open the QR code while online, then try again.';

  @override
  String get deliveryErrorCancelNotAllowed =>
      'This delivery was already picked up, so it can no longer be cancelled. Contact support if there is a problem.';

  @override
  String get deliveryErrorDisputeRefused =>
      'This dispute can\'t be opened right now. Contact support.';

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
  String get atAGlance => 'At a Glance';

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
  String get noListingsMatchFilters => 'No listings match your search';

  @override
  String get noListingsMatchFiltersHint =>
      'Try another crop, farmer or place, or clear the filters';

  @override
  String get clearFilters => 'Clear filters';

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
  String get inEscrowEtb => 'In Escrow (ETB)';

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
  String get drivingLicenseRequired =>
      'A driving license photo is required to register as a trucker.';

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
  String get companyNameRequired => 'Enter your company name to continue.';

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
  String get back => 'Back';

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
  String get listsPaymentsNote =>
      'Each row is one escrow. Tap it for the timeline.';

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
      'A verified Fayda ID is required to create an account. It only takes a minute.';

  @override
  String get faydaUnavailable =>
      'Fayda service is currently unavailable. Please try again later.';

  @override
  String get faydaAlreadyLinked =>
      'This Fayda ID is already linked to another account.';

  @override
  String get placeOrder => 'Place Order';

  @override
  String get viewDetails => 'View Details';

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
  String get statusCreatedInfo =>
      'The buyer placed this order. No money has been paid yet.';

  @override
  String get statusPendingPaymentInfo =>
      'Waiting for the buyer to start the payment. Orders left unpaid are cancelled automatically after 24 hours.';

  @override
  String get statusPaymentSubmittedInfo =>
      'The buyer sent the payment and it is being verified. The money is not yet confirmed in escrow.';

  @override
  String get statusFundedInfo =>
      'The payment is confirmed and held safely in escrow. The ordered quantity is now reserved from the farmer\'s listing.';

  @override
  String get orderAcceptedInfo =>
      'The farmer accepted the order and will prepare the crops for pickup.';

  @override
  String get statusPickupConfirmedInfo =>
      'Pickup from the farmer was confirmed with a QR code scan.';

  @override
  String get statusInTransitInfo => 'The crops are on their way to the buyer.';

  @override
  String get statusDeliveredInfo =>
      'Delivery was confirmed by the buyer. Escrow will now release the payment to the farmer.';

  @override
  String get statusDisbursingInfo =>
      'The money is leaving escrow: the farmer, and the trucker if there is one, are being paid.';

  @override
  String get statusSettledInfo =>
      'Everyone has been paid: the farmer, and the trucker if there is one. The order is complete.';

  @override
  String get orderCancelledInfo =>
      'The order was cancelled. Any money held in escrow is refunded to the buyer.';

  @override
  String get orderRejectedInfo =>
      'The farmer declined this order. Money held in escrow is refunded to the buyer.';

  @override
  String get statusPaymentFailedInfo =>
      'The payment could not be verified. The buyer can try paying again.';

  @override
  String get statusDisputedInfo =>
      'A dispute was raised. Escrow keeps holding the money until the dispute is resolved.';

  @override
  String get statusDisputeResolvedInfo =>
      'The dispute was resolved and the money went to the winning side.';

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
  String restMatchesFor(String crop) {
    return 'Matches for $crop';
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
  String get intentSaveHint =>
      'Saving this request lets us alert you when new matches arrive';

  @override
  String get selectCropTypeError => 'Select a crop type';

  @override
  String get enterQuantityError => 'Enter the quantity you need';

  @override
  String get enterValidPriceError => 'Enter a valid price or leave it empty';

  @override
  String get enterDeliveryWoredaError => 'Enter your delivery woreda';

  @override
  String get maxPriceOptionalHint => 'Optional — leave empty to see all prices';

  @override
  String get anyPrice => 'Any price';

  @override
  String get similarCrop => 'Similar crop';

  @override
  String get withinBudget => 'Within budget';

  @override
  String get slightlyOverBudget => 'Slightly over budget';

  @override
  String get coversFullQuantity => 'Covers full quantity';

  @override
  String get partialQuantity => 'Partial quantity';

  @override
  String get nearYou => 'Near you';

  @override
  String get deleteRequest => 'Delete Request';

  @override
  String get deleteRequestConfirm =>
      'Delete this buying request? You will stop receiving match alerts for it.';

  @override
  String get requestDeleted => 'Request deleted';

  @override
  String get markFulfilled => 'Mark as Fulfilled';

  @override
  String get requestFulfilled => 'Request marked as fulfilled';

  @override
  String quintalEquivalent(String count) {
    return '= $count quintals';
  }

  @override
  String orderQuantityExceedsStock(String available) {
    return 'Only $available kg available. Enter a smaller quantity.';
  }

  @override
  String get neededByDate => 'Needed by (optional)';

  @override
  String get neededByDateHint =>
      'We\'ll prefer transport that can deliver in time';

  @override
  String get selectDate => 'Select date';

  @override
  String get clearDate => 'Clear';

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

  @override
  String get messages => 'Messages';

  @override
  String get noConversationsYet => 'No messages yet';

  @override
  String get noConversationsHint =>
      'Your conversations with farmers, truckers and buyers will appear here.';

  @override
  String get noMessagesInThread =>
      'No messages yet. Start the conversation below.';

  @override
  String get typeAMessage => 'Type a message…';

  @override
  String get send => 'Send';

  @override
  String get sendMessage => 'Send a message';

  @override
  String get messageContact => 'Message';

  @override
  String get messageFarmer => 'Message Farmer';

  @override
  String get messageTrucker => 'Message Trucker';

  @override
  String get messageBuyer => 'Message Buyer';

  @override
  String get contact => 'Contact';

  @override
  String get contactAfterEscrowNote =>
      'Contact opens after payment is held in escrow.';

  @override
  String get contactAfterAcceptNote =>
      'Contact opens once the farmer accepts the order.';

  @override
  String get contactBuyer => 'Contact Buyer';

  @override
  String get contactTrucker => 'Contact Trucker';

  @override
  String get contactClosedNote => 'Contact is closed for this conversation.';

  @override
  String get newMessage => 'New message';

  @override
  String newMessagesCount(int count) {
    return '$count new messages';
  }

  @override
  String get messageContextCrop => 'Crop';

  @override
  String get messageContextTrip => 'Trip';

  @override
  String get messageContextOrder => 'Order';

  @override
  String get messageContextDelivery => 'Delivery';

  @override
  String get contextUnavailable => 'Details aren\'t available right now';

  @override
  String get stepUpRequiredMessage =>
      'For your safety, confirm it\'s you before this payment.';

  @override
  String get kycRequiredFaydaMessage =>
      'Payments above 10,000 ETB need a verified Fayda ID. Verify with Fayda and your payment is sent right after.';

  @override
  String get kycRequiredPhoneMessage =>
      'Your phone number is not verified yet, so this payment was not sent. Verify with Fayda and your payment is sent right after.';

  @override
  String get kycRequiredTitle => 'Verification needed';

  @override
  String authRateLimitRetryIn(int seconds) {
    return 'Too many attempts. Try again in $seconds seconds.';
  }

  @override
  String get otpInvalidOrExpired =>
      'That code is wrong or has expired. Check the SMS or request a new code.';

  @override
  String get otpVerificationTitle => 'Confirm your phone';

  @override
  String otpSentTo(String phone) {
    return 'We sent a 6-digit code by SMS to $phone.';
  }

  @override
  String get otpCodeHint => '6-digit code';

  @override
  String otpExpiresAt(String time) {
    return 'The code expires at $time.';
  }

  @override
  String get faydaStepUpMessage =>
      'Confirm it\'s you with Fayda to continue with this payment. You may also be asked for your password.';

  @override
  String get stepUpTitle => 'Confirm it\'s you';

  @override
  String stepUpPhoneLabel(String phone) {
    return 'Phone: $phone';
  }

  @override
  String get stepUpConfirm => 'Confirm';

  @override
  String get stepUpOfflineError =>
      'You need internet to confirm. Connect and try again.';

  @override
  String get stepUpDone => 'Confirmed. Retrying your payment.';

  @override
  String get faydaStateMismatch =>
      'The Fayda session expired. Please start verification again.';

  @override
  String get faydaStillWaiting =>
      'Still waiting for Fayda. Finish in the browser and you will be brought back here.';

  @override
  String get faydaLinkSuccess => 'Your Fayda ID is now linked to your account.';

  @override
  String get faydaNotVerifiedSubtitle => 'Needed for payments above 10,000 ETB';

  @override
  String get appLockTitle => 'App lock';

  @override
  String get appLockSubtitle =>
      'Ask for your fingerprint or phone PIN when opening the app';

  @override
  String get appLockLockedTitle => 'WeredaConnect is locked';

  @override
  String get appLockUnlock => 'Unlock';

  @override
  String get appLockReason => 'Unlock WeredaConnect';

  @override
  String get qrConfirmAuthReason => 'Confirm it\'s you to accept this delivery';

  @override
  String get qrConfirmAuthFailed =>
      'Could not confirm it\'s you. The delivery was not confirmed.';

  @override
  String get faydaRegistrationUnavailable =>
      'We couldn\'t reach Fayda, even after several tries. Check your internet connection and try again in a few minutes.';

  @override
  String get faydaUnavailableOnWeb =>
      'Fayda verification isn\'t available in the browser yet. Please use the WeredaConnect mobile app.';

  @override
  String get faydaCouldNotOpen =>
      'We couldn\'t open the Fayda page. Please try again.';

  @override
  String get faydaRequiredToRegister =>
      'Your Fayda verification is missing or has expired. Please verify with Fayda again to finish creating your account.';

  @override
  String faydaRetrying(int attempt, int total) {
    return 'Still trying to reach Fayda ($attempt/$total)...';
  }

  @override
  String get kycFarmerPhoneMessage =>
      'The farmer\'s phone number is not verified yet, so this payment was not sent. You can pay once the farmer verifies it.';

  @override
  String get kycFarmerFaydaMessage =>
      'Payments above 10,000 ETB need the farmer to have a verified Fayda ID too, so this payment was not sent. You can pay once the farmer verifies.';

  @override
  String get kycFarmerNotVerifiedMessage =>
      'The farmer\'s identity is not verified yet, so this payment was not sent. You can pay once the farmer verifies.';

  @override
  String get kycUnavailableMessage =>
      'We could not check identity verification right now, so this payment was not sent. Please try again later.';

  @override
  String get kycPaymentNotSent =>
      'Payment not sent. Your identity was not verified.';

  @override
  String get kycPaymentResubmitted =>
      'Identity verified. Your payment will be sent again.';

  @override
  String get kycVerifiedPayAgain =>
      'Identity verified. Open the order and submit the payment again.';

  @override
  String get faydaPaymentPrompt =>
      'Confirm it\'s you with Fayda. Use the Fayda ID that belongs to this account, and your payment continues right after.';

  @override
  String get faydaLinkPrompt =>
      'Link your Fayda ID to this account. Use your own Fayda ID: it must match this account\'s phone number.';

  @override
  String get faydaPaymentContinues =>
      'Identity verified. Continuing your payment...';

  @override
  String get faydaIdMismatch =>
      'This Fayda ID does not match the one on your account. Verify with your own Fayda ID.';

  @override
  String get faydaPhoneMismatch =>
      'The phone number on this Fayda ID does not match your account. Verify with your own Fayda ID.';

  @override
  String get faydaIdentityUnconfirmed =>
      'We could not confirm this Fayda ID belongs to your account. Verify with the Fayda ID registered to your phone number.';

  @override
  String get kycPhoneNotProven =>
      'Your Fayda ID is confirmed, but Fayda does not list this account\'s phone number, so your phone is still not verified. The payment was not sent.';

  @override
  String get payWithTelebirr => 'Pay with Telebirr';

  @override
  String telebirrLivePayInstructions(String amount) {
    return 'You will pay $amount in Telebirr. When you finish, come back to this app. The payment is confirmed automatically.';
  }

  @override
  String get orderCheckoutTitle => 'Telebirr payment';

  @override
  String get orderCheckoutWaitingToSend =>
      'Your payment is waiting to be sent. Telebirr opens once it reaches the server.';

  @override
  String orderCheckoutOpenUntil(String time) {
    return 'You can finish this payment in Telebirr until $time.';
  }

  @override
  String get orderCheckoutWindowClosed =>
      'This Telebirr payment page has closed. Start a new one to pay.';

  @override
  String get orderCheckoutStartNew => 'Start a new Telebirr payment';

  @override
  String get orderCheckoutCheckAgain => 'Check payment status';

  @override
  String get orderCheckoutStillWaiting =>
      'We have not received the payment yet. If you paid, tap Check payment status.';

  @override
  String get orderCheckoutTimeRanOut =>
      'The time to pay for this order has run out. The order will be marked as failed, then you can pay again.';

  @override
  String get orderCheckoutNotWaiting =>
      'This order is no longer waiting for payment.';

  @override
  String get orderCheckoutNotPayableOnline =>
      'This order can\'t be paid online. Contact support.';

  @override
  String get orderCheckoutConfirmedAutomatically =>
      'Payments are confirmed automatically here. Checking your order...';

  @override
  String get orderCheckoutAlreadyOpen =>
      'A Telebirr payment for this order is being set up or was interrupted. If you already paid, it will be confirmed here. Otherwise try again later (it can take up to 2 hours).';

  @override
  String get orderCheckoutGatewayProblem =>
      'Telebirr could not start the payment. If you already paid, it will be confirmed here. Otherwise try again.';

  @override
  String get orderCheckoutOrderMissing =>
      'This order is not on the server yet. Try again once it has synced.';

  @override
  String get orderCheckoutPaymentFailed =>
      'This payment could not be confirmed in time. You can pay again.';

  @override
  String get orderCheckoutNotSent =>
      'Your payment was not sent. If you were asked to verify your identity, it is sent again once you do; otherwise open the order to pay again.';

  @override
  String get orderCheckoutCheckingPayment => 'Checking your payment...';

  @override
  String get paymentTotalOutOfRange =>
      'Telebirr takes 100 to 500,000 ETB in one payment. This order\'s total is outside that, so it can\'t be paid with Telebirr.';

  @override
  String get paymentMethodUnavailable =>
      'This payment method isn\'t available yet. Pay with Telebirr.';

  @override
  String get continuePayment => 'Continue payment';

  @override
  String get payAgain => 'Pay again';

  @override
  String get orderPickupDeadline => 'Collect by';

  @override
  String get orderTransportFailedBuyerHint =>
      'The crops were not collected in time. You can cancel this order and get all your money back.';

  @override
  String get orderTransportFailedPartyHint =>
      'The crops were not collected in time. The buyer can now cancel this order.';

  @override
  String get cancelOrderTransportFailedBody =>
      'You get back everything you paid, including transport and the platform commission.';

  @override
  String get errorOrderCancelNotAllowed =>
      'This order can\'t be cancelled now. It can be cancelled only when the crops were not collected in time and nothing was picked up.';

  @override
  String get errorDeadlinePassed =>
      'Too late. The time for this step has passed.';

  @override
  String get errorTripNotEligible =>
      'That truck is no longer available. Choose again.';

  @override
  String get errorTransportAttemptsExceeded =>
      'No more trucks can be tried for this order. Collect the crops yourself or cancel for a full refund.';

  @override
  String get errorTripCapacityExceeded =>
      'This truck has no room left for this order. Choose another truck.';

  @override
  String get errorReputationRestricted =>
      'Your account can\'t do this for now, so this change was not saved. Your current orders continue.';

  @override
  String get errorCounterpartyRestricted =>
      'This farmer or trucker can\'t take new orders right now, so the order was not placed. Please choose another.';

  @override
  String get errorTripHasBookings =>
      'This trip has booked orders, so this change can\'t be made.';

  @override
  String get errorCropHasActiveOrders =>
      'This listing has open orders. Handle them first.';

  @override
  String get errorAppUpdateRequired =>
      'Please update the app to continue. This change was not saved.';

  @override
  String get errorAppUpdateRequiredKept =>
      'Please update the app. This change is kept on your phone and will be sent after the update.';

  @override
  String get errorContactInText =>
      'Phone numbers are not allowed here. Remove the number: contact opens after payment is held in escrow.';

  @override
  String get authProfileContactInText =>
      'Remove the phone number from your name, woreda or company name. Contact opens after payment is held in escrow.';

  @override
  String get deleteAccount => 'Delete account';

  @override
  String get deleteAccountSubtitle => 'Remove your account and personal data';

  @override
  String get deleteAccountWarning =>
      'Deleting your account is permanent. It cannot be undone.';

  @override
  String get deleteAccountDeletedHeading => 'What is deleted';

  @override
  String get deleteAccountDeletedProfile =>
      'Your profile, name and phone number';

  @override
  String get deleteAccountDeletedFayda => 'The link to your Fayda ID';

  @override
  String get deleteAccountDeletedListings =>
      'Your crop listings, trips and buying intents';

  @override
  String get deleteAccountDeletedMessages => 'Your messages';

  @override
  String get deleteAccountDeletedNotifications => 'Notifications to your phone';

  @override
  String get deleteAccountKeptHeading => 'What is kept, without your name';

  @override
  String get deleteAccountKeptRecords =>
      'Orders, deliveries and payment records';

  @override
  String get deleteAccountKeptReason =>
      'Money records must be kept. Your name on them is replaced with Deleted user.';

  @override
  String get deleteAccountNeedsInternet =>
      'You need internet for this. You may be asked for your password.';

  @override
  String get deleteAccountButton => 'Delete my account';

  @override
  String get deleteAccountConfirmTitle => 'Delete your account?';

  @override
  String get deleteAccountConfirmBody =>
      'This cannot be undone. Your profile, listings and messages will be deleted.';

  @override
  String get deleteAccountDeleting => 'Deleting your account...';

  @override
  String get deleteAccountFinishing => 'Finishing the deletion...';

  @override
  String get deleteAccountDone => 'Your account was deleted.';

  @override
  String get deleteAccountOffline =>
      'You are offline. Connect to the internet to delete your account.';

  @override
  String get deleteAccountNetworkError =>
      'We could not reach the server, so we cannot tell if your account was deleted. Check your connection and try again.';

  @override
  String get deleteAccountIncomplete =>
      'The deletion started but is not finished yet. Try again in a moment.';

  @override
  String get deleteAccountSuspended =>
      'This account is suspended, so it cannot be deleted here. Contact support.';

  @override
  String get deleteAccountFailed =>
      'Your account could not be deleted. Try again later.';

  @override
  String get deleteAccountOtherAccount =>
      'Another account is now signed in here, so nothing was deleted. Reload the page or reopen the app, then try again.';

  @override
  String get deleteAccountBlockedTitle => 'You cannot delete your account yet';

  @override
  String get deleteAccountBlockedIntro => 'Finish or cancel these first:';

  @override
  String deleteAccountBlockerOrder(int count) {
    return 'Active orders: $count';
  }

  @override
  String deleteAccountBlockerEscrow(int count) {
    return 'Payments held in escrow: $count';
  }

  @override
  String deleteAccountBlockerDelivery(int count) {
    return 'Open deliveries: $count';
  }

  @override
  String deleteAccountBlockerDispute(int count) {
    return 'Open disputes: $count';
  }

  @override
  String deleteAccountBlockerPaymentReview(int count) {
    return 'Payments waiting for a refund: $count. Contact support to finish the refund.';
  }

  @override
  String deleteAccountBlockerOther(int count) {
    return 'Other active items: $count';
  }

  @override
  String deleteAccountBlockersMore(int count) {
    return 'And $count more.';
  }

  @override
  String get deletedUser => 'Deleted user';

  @override
  String get statusAwaitingTransport => 'Transport fell through';

  @override
  String get statusAwaitingTransportInfo =>
      'The trucker can\'t carry this order. The money stays held safely. The buyer can cancel for a full refund; without a decision in time the order is cancelled and refunded.';

  @override
  String get orderStatusUpdating => 'Updating';

  @override
  String get orderStatusUpdatingInfo =>
      'This order changed in a way this app version can\'t show yet. Please update the app.';

  @override
  String get commitmentTitle => 'Who has agreed';

  @override
  String get commitmentAccepted => 'Accepted';

  @override
  String commitmentWaitingUntil(String time) {
    return 'Waiting, answer due $time';
  }

  @override
  String get commitmentWaiting => 'Waiting for an answer';

  @override
  String get commitmentDeclined => 'Declined';

  @override
  String get commitmentExpired => 'Did not answer in time';

  @override
  String get commitmentNotAskedYet => 'Asked once the farmer accepts';

  @override
  String get commitmentTripCancelled => 'Trip cancelled';

  @override
  String get commitmentTripFull => 'Trip was full';

  @override
  String get commitmentDroppedOut => 'Dropped out';

  @override
  String commitmentAnswerBy(String time) {
    return 'Please answer by $time.';
  }

  @override
  String get commitmentBuyerWaitingNote =>
      'Your money is held safely. The farmer is asked first, then the trucker. A paid order can\'t be cancelled while they decide.';

  @override
  String get commitmentTruckerNotAskedNote =>
      'The farmer has not accepted yet. You will be asked once they do.';

  @override
  String get truckerAcceptRequest => 'Accept transport';

  @override
  String get truckerDeclineRequest => 'Decline';

  @override
  String get declineReasonTitle => 'Why are you declining?';

  @override
  String get declineReasonDate => 'The date doesn\'t work';

  @override
  String get declineReasonCapacity => 'Not enough room';

  @override
  String get declineReasonPrice => 'The price doesn\'t work';

  @override
  String get declineReasonRoute => 'The route doesn\'t work';

  @override
  String get declineReasonCropNotReady => 'The crops are not ready';

  @override
  String get declineReasonOther => 'Another reason';

  @override
  String get truckerDropOut => 'I can\'t make this delivery';

  @override
  String get truckerDropOutWarning =>
      'The buyer is told and may cancel for a full refund. Dropping out after you accepted counts against you. You will be asked to sign in again.';

  @override
  String get farmerCancelWithoutPenalty => 'Cancel without penalty';

  @override
  String get farmerCancelCommittedWarning =>
      'Cancelling after you accepted counts against you, and the buyer gets a full refund. You will be asked to sign in again.';

  @override
  String get farmerCancelAwaitingTransportBody =>
      'The transport fell through, so cancelling does not count against you. The buyer gets a full refund.';

  @override
  String transportFailedBuyerBody(String deadline) {
    return 'The trucker can\'t carry this order. Cancel for a full refund by $deadline. If you don\'t decide by then, the order is cancelled and refunded for you.';
  }

  @override
  String get transportFailedBuyerBodyNoDeadline =>
      'The trucker can\'t carry this order. You can cancel it for a full refund.';

  @override
  String get transportFailedFarmerBody =>
      'The transport for this order fell through. Please keep the crops until the buyer decides.';

  @override
  String get cancelOrderAwaitingTransportBody =>
      'The money held for this order comes back to you in full.';

  @override
  String get errorResponseNotPending =>
      'This request is not open for you: the farmer has not accepted yet, or it was already answered.';

  @override
  String get transportFailureTruckerDeclined => 'The trucker declined';

  @override
  String get transportFailureTruckerExpired =>
      'The trucker did not answer in time';

  @override
  String get transportFailureTruckerCancelled => 'The trucker dropped out';

  @override
  String get transportFailureTripCancelled => 'The trip was cancelled';

  @override
  String get transportFailureTripFull => 'The trip was full';

  @override
  String get actionNeededTitle => 'Action needed';

  @override
  String get actionAnswerOrder => 'Answer the new order';

  @override
  String get actionAnswerTransport => 'Answer the transport request';

  @override
  String get actionKeepCrops => 'Keep the crops: the buyer is deciding';

  @override
  String get actionSetUpDelivery => 'Set up the delivery';

  @override
  String get actionPay => 'Pay for the order';

  @override
  String get actionPayAgain => 'Payment failed: pay again';

  @override
  String get actionDecideTransport => 'Transport fell through: decide';

  @override
  String get actionCheckDelivery => 'Check the delivery';

  @override
  String actionDueBy(String time) {
    return 'Due $time';
  }

  @override
  String get tripBookings => 'Booked orders';

  @override
  String get tripNoBookings => 'No orders booked yet';

  @override
  String tripRemainingCapacity(String quintals) {
    return '$quintals quintals free';
  }

  @override
  String get chooseTrip => 'Choose a truck';

  @override
  String get notifGenericTitle => 'Order update';

  @override
  String get notifGenericBody => 'Open the order to see what changed.';

  @override
  String get notifPaymentConfirmedTitle => 'Payment received';

  @override
  String get notifPaymentConfirmedBody =>
      'Your payment is held safely. We will tell you when the farmer answers.';

  @override
  String get notifOrderRequestTitle => 'New order';

  @override
  String notifOrderRequestBody(String buyer) {
    return '$buyer paid, and the money is held safely until delivery. Please accept or decline.';
  }

  @override
  String get notifFarmerAcceptedTitle => 'Farmer accepted';

  @override
  String notifFarmerAcceptedBody(String farmer) {
    return '$farmer accepted your order. We are now asking the trucker.';
  }

  @override
  String get notifTransportRequestTitle => 'Transport request';

  @override
  String notifTransportRequestBody(String farmer) {
    return '$farmer accepted an order on your trip. Please accept or decline the transport.';
  }

  @override
  String get notifOrderConfirmedTitle => 'Order confirmed';

  @override
  String get notifOrderConfirmedBody => 'Everyone has agreed to the order.';

  @override
  String get notifOrderRejectedTitle => 'Order declined';

  @override
  String notifOrderRejectedBody(String farmer) {
    return '$farmer declined your order. Your money will be returned to you.';
  }

  @override
  String get notifTransportFailedTitle => 'Transport fell through';

  @override
  String get notifTransportFailedBody =>
      'The trucker can\'t carry your order. Please decide: you can cancel for a full refund.';

  @override
  String get notifTransportFailedKeepCropsTitle => 'Transport fell through';

  @override
  String get notifTransportFailedKeepCropsBody =>
      'The trucker for this order dropped out. Please keep the crops until the buyer decides.';

  @override
  String get notifRequestExpiredTitle => 'Request closed';

  @override
  String get notifRequestExpiredBody =>
      'The request closed because it was not answered in time.';

  @override
  String get notifRequestOnHoldTitle => 'Request on hold';

  @override
  String get notifRequestOnHoldBody =>
      'Transport for this order is not available. The buyer is deciding, so you do not need to answer now.';

  @override
  String get notifOrderCancelledTitle => 'Order cancelled';

  @override
  String get notifOrderCancelledBody =>
      'The order was cancelled. Any money held goes back to the buyer.';

  @override
  String get notifOrderExpiredUnpaidTitle => 'Order closed';

  @override
  String get notifOrderExpiredUnpaidBody =>
      'Your order was closed because it was not paid in time. You can place a new order.';

  @override
  String get notifPaymentFailedTitle => 'Payment failed';

  @override
  String get notifPaymentFailedBody =>
      'Your payment did not go through. Please try again.';

  @override
  String get notifRequestReminderTitle => 'Your answer is needed';

  @override
  String get notifRequestReminderBody =>
      'This order is waiting for your answer.';

  @override
  String get notifPickupOverdueTitle => 'Pickup is late';

  @override
  String get notifPickupOverdueBody =>
      'Your order was not picked up on time. Open it to see your options, including a full refund.';

  @override
  String get notifPickupMissedTitle => 'Pickup missed';

  @override
  String get notifPickupMissedBody =>
      'The order was not picked up on time. The buyer may now cancel it for a refund.';

  @override
  String get notifDeliveryScheduledTitle => 'Transport arranged';

  @override
  String notifDeliveryScheduledBody(String trucker) {
    return '$trucker will carry the order.';
  }

  @override
  String get notifBuyerCollectingTitle => 'Buyer is coming';

  @override
  String notifBuyerCollectingBody(String buyer) {
    return '$buyer will collect the order from you. Please have the crops ready.';
  }

  @override
  String get notifPickedUpTitle => 'Crops picked up';

  @override
  String get notifPickedUpBody => 'Your order was picked up from the farmer.';

  @override
  String get notifHandoffRecordedTitle => 'Handoff recorded';

  @override
  String get notifHandoffRecordedBody =>
      'The handoff is recorded. You are paid after the buyer receives the crops.';

  @override
  String get notifInTransitTitle => 'On the way';

  @override
  String get notifInTransitBody => 'Your order is on the way to you.';

  @override
  String get notifTruckNearbyTitle => 'Truck nearby';

  @override
  String notifTruckNearbyBody(String trucker) {
    return '$trucker is less than 5 km from you. Please be ready to receive the crops.';
  }

  @override
  String get notifDeliveredTitle => 'Order delivered';

  @override
  String get notifDeliveredBody =>
      'Your order was delivered. If something is wrong, report it before the time is up. If not, the farmer is paid.';

  @override
  String get notifDeliveredPayoutPendingTitle => 'Delivered, payment coming';

  @override
  String get notifDeliveredPayoutPendingBody =>
      'The order was delivered. Your payment is sent after the buyer\'s 48 hour check.';

  @override
  String get notifOrderSettledTitle => 'Order complete';

  @override
  String get notifOrderSettledBody =>
      'The order is complete and everyone is paid. Thank you.';

  @override
  String get notifRefundSentTitle => 'Refund sent';

  @override
  String get notifRefundSentBody => 'Your refund has been sent to you.';

  @override
  String get notifPayoutSentTitle => 'Payment sent';

  @override
  String get notifPayoutSentBody =>
      'Your payment has been sent to you. Thank you.';

  @override
  String get notifPayoutBlockedTitle => 'Payment on hold';

  @override
  String notifPayoutBlockedBody(String hint) {
    return 'Your payment is waiting. $hint';
  }

  @override
  String get notifPayoutProcessingTitle => 'Payment being processed';

  @override
  String get notifPayoutProcessingBody =>
      'Our team is sending your payment by hand. It can take a few days.';

  @override
  String get notifDisputeOpenedTitle => 'Problem reported';

  @override
  String get notifDisputeOpenedBody =>
      'A problem was reported on your order. The money stays held until our team decides.';

  @override
  String get notifDisputeReceivedTitle => 'Report received';

  @override
  String get notifDisputeReceivedBody =>
      'We received your report. The money stays held until our team decides.';

  @override
  String get notifDisputeResolvedTitle => 'Decision made';

  @override
  String get notifUnderReviewTitle => 'Payment under review';

  @override
  String get notifUnderReviewBody =>
      'Our team is checking the payment for your order. We will tell you when it is done.';

  @override
  String get notifDisputeResolvedFarmerWins =>
      'Our team has decided: the money goes to the farmer.';

  @override
  String get notifDisputeResolvedBuyerWins =>
      'Our team has decided: the money goes back to the buyer.';

  @override
  String get notifDisputeResolvedBody =>
      'Our team has decided on the reported problem. Open the order for the details.';

  @override
  String get notifPayoutHintFayda => 'Please verify your Fayda ID in the app.';

  @override
  String get notifPayoutHintPhone =>
      'Please verify your phone number in the app.';

  @override
  String get notifPayoutHintProfile =>
      'Please complete your profile in the app.';

  @override
  String get notifPayoutHintOther => 'Open the app to see what is needed.';

  @override
  String get clockSetting => 'Clock';

  @override
  String get clockAuto => 'Auto';

  @override
  String get clockAutoHint => 'Follows the app language';

  @override
  String get clockEthiopian => 'Ethiopian';

  @override
  String get clockInternational => 'International (24-hour)';

  @override
  String clockNight(String time) {
    return '$time at night';
  }

  @override
  String clockMorning(String time) {
    return '$time in the morning';
  }

  @override
  String clockDay(String time) {
    return '$time in the afternoon';
  }

  @override
  String clockEvening(String time) {
    return '$time in the evening';
  }

  @override
  String get liveTripNotificationTitle => 'Sharing the truck\'s location';

  @override
  String get liveTripNotificationText =>
      'Buyers and farmers on this trip see where the truck is. Sharing stops after the last delivery.';

  @override
  String get liveTripNotificationChannel => 'Trip location';

  @override
  String get liveTripConsentTitle => 'Share where the truck is?';

  @override
  String get liveTripConsentLede =>
      'Buyers and farmers who can see the truck worry less and call you less while you drive.';

  @override
  String get liveTripConsentWhoTitle => 'Who sees it';

  @override
  String get liveTripConsentWhoText =>
      'The buyer and farmer of each order on this trip, each only for their own order. Other truckers and buyers browsing trips never see it.';

  @override
  String get liveTripConsentOftenTitle => 'How often';

  @override
  String get liveTripConsentOftenText =>
      'About once an hour and at each QR scan. Every 15 minutes when you are near a drop-off.';

  @override
  String get liveTripConsentStopsTitle => 'When it stops';

  @override
  String get liveTripConsentStopsText =>
      'By itself when the last delivery on this trip is done. You can pause any time.';

  @override
  String get liveTripConsentDataTitle => 'Data';

  @override
  String get liveTripConsentDataText =>
      'About 170 KB a day. With no network, check-ins wait on the phone and send later.';

  @override
  String get liveTripConsentAndroidNote =>
      'Android shows a notification while sharing is on.';

  @override
  String get liveTripConsentWebNote =>
      'On the web, check-ins are sent only while this page is open.';

  @override
  String get liveTripConsentShare => 'Share trip location';

  @override
  String get liveTripConsentDecline => 'Not now, scans only';

  @override
  String get liveTripSharingStarted =>
      'Sharing the truck\'s location for this trip.';

  @override
  String get liveTripScansOnlyChosen =>
      'Scans only for this trip. You can still share from trip mode.';

  @override
  String get tripModeTitle => 'Trip mode';

  @override
  String get tripModeOpen => 'Open trip';

  @override
  String tripModeOrders(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count orders',
      one: '1 order',
    );
    return '$_temp0';
  }

  @override
  String get tripModeSharingLive => 'Sharing your location';

  @override
  String tripModeLastCheckIn(String time) {
    return 'Last check-in $time';
  }

  @override
  String get tripModeNoCheckInYet => 'No check-in yet';

  @override
  String tripModeNextBy(String time) {
    return 'next by $time';
  }

  @override
  String get tripModePrivacy =>
      'Each buyer and farmer sees only the part for their own order.';

  @override
  String get tripModePause => 'Pause';

  @override
  String get tripModeCheckInNow => 'Check in now';

  @override
  String get tripModePausedTitle => 'Sharing paused';

  @override
  String tripModePausedUntil(String time) {
    return 'Comes back by itself at $time';
  }

  @override
  String get tripModeResume => 'Resume now';

  @override
  String get tripModeScansOnlyTitle => 'Scans only';

  @override
  String get tripModeScansOnlyText =>
      'Your location is not shared. QR scans still record pickup and delivery.';

  @override
  String get tripModeEndedTitle => 'Sharing ended';

  @override
  String get tripModeEndedText =>
      'Every delivery on this trip is done, so sharing stopped.';

  @override
  String get tripModeNoTrip =>
      'No trip is on the road. Trip mode starts with your first pickup scan.';

  @override
  String get tripModeNextPickup => 'Next stop: pickup';

  @override
  String get tripModeNextDropoff => 'Next stop: drop-off';

  @override
  String get tripModeArrived => 'I\'ve arrived';

  @override
  String get tripModeStops => 'Stops';

  @override
  String tripModeStopPickup(String name) {
    return 'Pickup: $name';
  }

  @override
  String tripModeStopDropoff(String name) {
    return 'Drop-off: $name';
  }

  @override
  String tripModeScannedAt(String time) {
    return 'scanned $time';
  }

  @override
  String tripModeDeliveredAt(String time) {
    return 'delivered $time';
  }

  @override
  String get tripModeNextLabel => 'Next';

  @override
  String get tripModePayTitle => 'Your pay for this trip';

  @override
  String get tripModePayNote => 'Paid 48 h after each drop-off';

  @override
  String get tripModeCheckInSaved => 'Check-in saved.';

  @override
  String get tripModeNoFix =>
      'No location right now. Check that location is on, then try again.';

  @override
  String get tripModeNotSharing => 'This trip does not share its location.';

  @override
  String get tripModeLoadFailed => 'Could not load the stops of this trip.';

  @override
  String get tripModeOfflineTitle => 'No network here';

  @override
  String tripModeOfflineWaiting(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other:
          '$count check-ins wait on this phone. They send by themselves when you are back in coverage.',
      one:
          '1 check-in waits on this phone. It sends by itself when you are back in coverage.',
      zero:
          'Check-ins wait on this phone and send by themselves when you are back in coverage.',
    );
    return '$_temp0';
  }

  @override
  String get tripModeCardTitle => 'Trip on the road';

  @override
  String get pauseSheetTitle => 'Pause sharing?';

  @override
  String get pauseSheetText =>
      'For a rest stop or the night. Buyers and farmers see that sharing is paused and when it comes back.';

  @override
  String get pauseSheetFor => 'Pause for';

  @override
  String get pauseSheet15 => '15 minutes';

  @override
  String get pauseSheet30 => '30 minutes';

  @override
  String get pauseSheet60 => '1 hour';

  @override
  String pauseSheetOvernight(String time) {
    return 'Overnight, until $time';
  }

  @override
  String pauseSheetBackAt(String time) {
    return 'back at $time';
  }

  @override
  String get pauseSheetNote =>
      'Sharing comes back by itself. QR scans still record their place.';

  @override
  String get pauseSheetConfirm => 'Pause sharing';

  @override
  String get pauseSheetKeep => 'Keep sharing';

  @override
  String get sharingDefaultSetting => 'Trip location';

  @override
  String get sharingDefaultHint =>
      'What happens at the first pickup scan of a trip. The buyer and farmer of each order see the truck until their delivery.';

  @override
  String get sharingDefaultAsk => 'Ask on each trip';

  @override
  String get sharingDefaultShare => 'Always share';

  @override
  String get sharingDefaultScansOnly => 'Never share (scans only)';

  @override
  String get liveTripTitle => 'Live trip';

  @override
  String get liveTripOnTheRoadNow => 'On the road now';

  @override
  String get liveTripWaitingForPickup => 'Waiting for pickup';

  @override
  String get liveTripMapStartsAtPickup =>
      'The map appears when the driver scans the farmer\'s QR.';

  @override
  String liveTripDurationMinutes(int minutes) {
    return '$minutes min';
  }

  @override
  String liveTripDurationHours(int hours, int minutes) {
    return '$hours h $minutes min';
  }

  @override
  String liveTripAgo(String duration) {
    return '$duration ago';
  }

  @override
  String get liveTripJustNow => 'Just now';

  @override
  String liveTripCheckedInAgo(String duration) {
    return 'Checked in $duration ago';
  }

  @override
  String get liveTripCheckedInJustNow => 'Checked in just now';

  @override
  String liveTripNoCheckInFor(String duration) {
    return 'No check-in for $duration';
  }

  @override
  String get liveTripPaused => 'Paused';

  @override
  String liveTripUntil(String time) {
    return 'until $time';
  }

  @override
  String get liveTripScansOnly => 'Scans only';

  @override
  String get liveTripScansOnlyChip => 'Location not shared';

  @override
  String get liveTripSharingEnded => 'Sharing ended at delivery';

  @override
  String get liveTripStatusOnTheRoad => 'On the road';

  @override
  String get liveTripStatusArriving => 'Arriving';

  @override
  String liveTripArrivingAt(String place) {
    return 'Arriving at $place';
  }

  @override
  String get liveTripArrivalWindow => 'Expected arrival';

  @override
  String liveTripKmToGo(String km) {
    return 'About $km km to go, from the last check-in';
  }

  @override
  String get liveTripWiderWindow => 'Wider window: no check-in for a while';

  @override
  String get liveTripNoEta => 'No arrival estimate yet';

  @override
  String get liveTripStepAgreed => 'Agreed';

  @override
  String get liveTripStepPickedUp => 'Picked up';

  @override
  String liveTripEscrowNote(String amount) {
    return 'Your $amount stays in escrow until you scan the driver\'s QR.';
  }

  @override
  String get liveTripEscrowNoteNoAmount =>
      'Your money stays in escrow until you scan the driver\'s QR.';

  @override
  String liveTripLateNote(String trucker) {
    return 'Likely no network on this road. Check-ins saved on $trucker\'s phone will show when it reconnects.';
  }

  @override
  String liveTripPausedNote(String trucker, String time) {
    return '$trucker paused sharing for a rest stop. It turns back on by itself at $time.';
  }

  @override
  String liveTripScansOnlyNote(String trucker) {
    return '$trucker chose not to share the truck\'s location. The QR scans still record the pickup and the delivery.';
  }

  @override
  String liveTripOfflineNote(String time) {
    return 'You are offline. This is the map from $time, it updates when you reconnect.';
  }

  @override
  String get liveTripOfflineNoMap =>
      'You are offline. The map shows when you reconnect.';

  @override
  String get liveTripUnavailable => 'The map cannot be updated right now.';

  @override
  String get liveTripNotFound => 'This trip cannot be found.';

  @override
  String get liveTripTripLogLink => 'Trip log and order';

  @override
  String liveTripCheckIns(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count check-ins',
      one: '1 check-in',
    );
    return '$_temp0';
  }

  @override
  String get liveTripTripLogTitle => 'Trip log';

  @override
  String liveTripKm(String km) {
    return '$km km';
  }

  @override
  String get liveTripStatDone => 'done';

  @override
  String get liveTripStatToGo => 'to go';

  @override
  String get liveTripStatOnTheRoad => 'on the road';

  @override
  String get liveTripStatDriven => 'driven';

  @override
  String get liveTripStatCheckIns => 'check-ins';

  @override
  String get liveTripCheckInCadence => 'Check-ins about every hour';

  @override
  String get liveTripLogPickup => 'Picked up, the farmer\'s QR was scanned';

  @override
  String get liveTripLogHourly => 'Hourly check-in';

  @override
  String get liveTripLogNearStop => 'Check-in near a stop';

  @override
  String get liveTripLogManual => 'The driver checked in';

  @override
  String get liveTripLogArrived => 'The driver arrived at a stop';

  @override
  String get liveTripLogStart => 'Sharing started';

  @override
  String get liveTripLogOtherStop => 'Another stop on this truck';

  @override
  String get liveTripLogCheckIn => 'Check-in';

  @override
  String get liveTripLogPaused => 'Sharing paused';

  @override
  String get liveTripLogLatest => 'latest';

  @override
  String get liveTripCheckInsTitle => 'Check-ins';

  @override
  String get liveTripAboutEveryHour => 'about every hour';

  @override
  String get liveTripLogNext => 'next';

  @override
  String get liveTripLogThen => 'then';

  @override
  String liveTripLogSentLateUntil(String time) {
    return 'Sent late, no network until $time';
  }

  @override
  String get liveTripExpectedToday => 'expected today';

  @override
  String get liveTripLogSentLate => 'Sent late, no network';

  @override
  String get liveTripLogPassed => 'passed';

  @override
  String get liveTripLogAhead => 'Ahead';

  @override
  String get liveTripLogYourDropoff =>
      'Your drop-off: you scan the driver\'s QR here';

  @override
  String get liveTripPickupProof => 'Pickup proof';

  @override
  String get liveTripDropoffProof => 'Drop-off proof';

  @override
  String liveTripQrScannedAt(String time) {
    return 'QR scanned $time';
  }

  @override
  String get liveTripGpsRecorded => 'GPS recorded';

  @override
  String get liveTripInEscrow => 'In escrow';

  @override
  String liveTripPayoutNote(String farmer, String farmerAmount, String trucker,
      String truckerAmount) {
    return '$farmer is paid $farmerAmount and $trucker $truckerAmount, 48 h after you confirm delivery.';
  }

  @override
  String liveTripPayoutNoteFarmerOnly(String farmer, String amount) {
    return '$farmer is paid $amount, 48 h after you confirm delivery.';
  }

  @override
  String get liveTripHelpTitle => 'Something wrong on the way?';

  @override
  String liveTripHelpBody(String trucker, String farmer) {
    return 'A picked-up order can no longer be cancelled. Talk to $trucker or $farmer now. After delivery you have 48 h to report a problem, and the money waits until it is settled.';
  }

  @override
  String liveTripMessageParty(String name) {
    return 'Message $name';
  }

  @override
  String liveTripPrivacyNote(String farmer) {
    return 'Only you and $farmer see where this truck is, from the pickup until your delivery. Other stops are never named.';
  }

  @override
  String get liveTripGreyRingsNote =>
      'Grey rings are other stops on this truck. They are never named.';

  @override
  String get liveTripArrivingTitle => 'Truck arriving';

  @override
  String liveTripArrivingMinutes(String trucker, int minutes) {
    return '$trucker is about $minutes min away';
  }

  @override
  String liveTripArrivingNear(String trucker) {
    return '$trucker is less than 5 km away';
  }

  @override
  String liveTripKmFromDropoff(String km) {
    return '$km km from the drop-off';
  }

  @override
  String get liveTripBeforeYouScan => 'Before you scan';

  @override
  String get liveTripCheckCount => 'Count the sacks and check the total weight';

  @override
  String get liveTripCheckQuality => 'Check the crop is dry and clean';

  @override
  String get liveTripCheckWeigh => 'Weigh a few sacks if you can';

  @override
  String liveTripScanConfirmsNote(String farmer, String trucker) {
    return 'Scanning confirms delivery. $farmer and $trucker are paid after a 48 h check window, unless you report a problem.';
  }

  @override
  String get liveTripScanDriversQr => 'Scan driver\'s QR';

  @override
  String liveTripDeliveredAt(String time) {
    return 'Delivered at $time';
  }

  @override
  String get liveTripEnded => 'This trip has ended';

  @override
  String liveTripReportProblemUntil(String time) {
    return 'Report a problem until $time';
  }

  @override
  String get liveTripHowDidItGo => 'How did it go?';

  @override
  String liveTripFarmerTitle(String crop) {
    return 'Your $crop is on the road';
  }

  @override
  String get liveTripFarmerTitleNoCrop => 'Your order is on the road';

  @override
  String liveTripFarmerTo(String quantity, String buyer) {
    return '$quantity to $buyer';
  }

  @override
  String liveTripArrives(String time) {
    return 'Arrives $time';
  }

  @override
  String get liveTripYourPayment => 'Your payment';

  @override
  String get liveTripPayStepPaid => 'Buyer paid, held in escrow';

  @override
  String get liveTripPayStepHandedOver => 'You handed over, QR scanned';

  @override
  String get liveTripPayStepBuyerConfirms => 'Buyer confirms delivery';

  @override
  String get liveTripPayStepPaidOut => '48 h check window, then paid out';

  @override
  String get liveTripOrderDetailsLink => 'Order details, price and timeline';

  @override
  String get liveTripAfterPickupHint =>
      'After pickup you will see the truck on a map in Deliveries.';

  @override
  String liveTripFrom(String name) {
    return 'From $name';
  }

  @override
  String liveTripMapSemantics(String place) {
    return 'Map of the trip. The truck was last near $place.';
  }

  @override
  String get liveTripMapSemanticsNoPlace => 'Map of the trip';

  @override
  String get liveTripYou => 'You';

  @override
  String get liveTripDropoff => 'Drop-off';

  @override
  String get liveTripPickup => 'Pickup';

  @override
  String liveTripOnTheRoadSince(String time) {
    return 'On the road since $time';
  }

  @override
  String get loginEscrowFooter => 'Payments held safely in escrow';

  @override
  String get loginNoAccountRegister => 'Don\'t have an account? Register';

  @override
  String get otpFaydaNumberNote =>
      'We ask for this only when the number differs from your Fayda record.';

  @override
  String get faydaDigitalIdLabel => 'Digital ID';

  @override
  String get faydaVerifiedWith => 'Verified with Fayda';

  @override
  String get faydaLegalNameNote =>
      'Your legal name comes from Fayda. Nobody can register in your name.';

  @override
  String registrationStepOf(int current, int total) {
    return 'Step $current of $total';
  }

  @override
  String get registrationRoleNextNote =>
      'Next you verify with Fayda, then add your details. Farmers skip the role details step.';

  @override
  String get joinFaydaNeedsInternet => 'Needs internet for this step.';

  @override
  String get joinWeredaHelper => 'Search places and towns, works offline';

  @override
  String get fromFayda => 'From Fayda';

  @override
  String get placeLicenseHere => 'Place the license here';

  @override
  String get retakeLicensePhoto => 'Retake License Photo';

  @override
  String get totalListingValue => 'Total Listing Value';

  @override
  String get checkoutHeldInEscrowNote =>
      'Held in escrow until you confirm delivery';

  @override
  String get placeOrderEscrowNote =>
      'Your money is held safely. The farmer is asked first, then the trucker.';

  @override
  String get deliveriesSearchHint => 'Crop, farmer or driver';

  @override
  String get delivSegmentActive => 'Active';

  @override
  String delivPickupOn(String date) {
    return 'pickup $date';
  }

  @override
  String delivDeliveredOn(String date) {
    return 'Delivered $date';
  }

  @override
  String delivSummaryOnTheRoad(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count on the road',
      one: '1 on the road',
    );
    return '$_temp0';
  }

  @override
  String delivSummaryWaiting(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count waiting for pickup',
      one: '1 waiting for pickup',
    );
    return '$_temp0';
  }

  @override
  String delivSummaryCheckWindow(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count in the check window',
      one: '1 in the check window',
    );
    return '$_temp0';
  }

  @override
  String get savedOnPhoneFirstNote =>
      'Saved on your phone first, sent when you have network.';

  @override
  String get myCropsPrivacyNote =>
      'Only you see your listings here. Buyers see them in Find Crops.';

  @override
  String get homeToday => 'Today';

  @override
  String homeDueToday(String time) {
    return 'Due today $time';
  }

  @override
  String homeActionMore(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more',
      one: '1 more',
    );
    return '$_temp0';
  }

  @override
  String homeNextPickup(String name) {
    return 'Next: pickup at $name';
  }

  @override
  String homeNextDropoff(String name) {
    return 'Next: drop-off at $name';
  }

  @override
  String get homeTripNoneHint => 'Post a trip and buyers can book space on it.';

  @override
  String get homeTripMatchedLine => 'Scan each farmer\'s QR at pickup';

  @override
  String homeTileActiveReserved(int active, int reserved) {
    return '$active active, $reserved reserved';
  }

  @override
  String get homeTileWaitingForYou => 'waiting for you';

  @override
  String get homeTileHeldForYou => 'held for you';

  @override
  String get homeTileHeldInEscrow => 'held in escrow';

  @override
  String get homeTileInTransit => 'in transit';

  @override
  String get homeTileInProgress => 'in progress';

  @override
  String get homeTileAvailableNow => 'available now';

  @override
  String get homeTileBrowseAllTrips => 'all trucks heading out';

  @override
  String homeRecentLine(String event) {
    return 'Recent: $event';
  }

  @override
  String tripPricePerQuintalValue(String price) {
    return '$price ETB per quintal';
  }

  @override
  String createTripRouteDistance(int km) {
    return 'About $km km in a straight line, drawn on your phone';
  }

  @override
  String createTripRoutePreviewLabel(String from, String to) {
    return 'Route preview from $from to $to';
  }

  @override
  String get createTripSwapRoute => 'Swap origin and destination';

  @override
  String get createTripDriverHint =>
      'We could not fill your name and phone from your profile. Please add them.';

  @override
  String get tripSpaceTitle => 'Space on this trip';

  @override
  String tripLoadedOf(String loaded, String capacity) {
    return '$loaded of $capacity quintals loaded';
  }

  @override
  String get tripBookedTermsNote =>
      'Booked trips keep their date, price and route.';

  @override
  String get tripScanAtPickupNote =>
      'On the trip day, scan each farmer\'s QR at pickup. Live trip starts with the first scan.';

  @override
  String get tripCancelBookedNote =>
      'Cancelling hands booked orders back to their buyers, who can cancel for a full refund.';

  @override
  String get orderYouReceive => 'You receive';

  @override
  String get orderYourTransportPay => 'Your transport pay';

  @override
  String get deliveriesNoSearchMatch => 'No delivery matches that name.';

  @override
  String get farmerRejectRefundNote => 'The buyer gets a full refund.';

  @override
  String get farmerAnswerNote =>
      'Contact with the buyer opens when you accept. Rejecting asks why and refunds the buyer.';

  @override
  String get checkoutConfirmItsYouNote =>
      'Payments over 10,000 ETB need Fayda. Smaller ones ask for your password.';

  @override
  String myCropsOrderedOf(String ordered, String total) {
    return '$ordered of $total quintals ordered';
  }

  @override
  String myCropsNoOrdersYet(String quantity) {
    return '$quantity quintals, no orders yet';
  }

  @override
  String get createTripBookingCutoffNote =>
      'Buyers can book until 24 h before you leave.';

  @override
  String myCropsListedOn(String date) {
    return 'listed $date';
  }

  @override
  String get cropsListedToday => 'listed today';

  @override
  String get cropsDrafts => 'Drafts';

  @override
  String get detailYourPay => 'your pay';

  @override
  String get detailBookingWaitingFarmer => 'Waiting for the farmer';

  @override
  String get detailBookingAskedNext => 'you are asked next';

  @override
  String get detailBookingAcceptedContactOpen => 'contact open';

  @override
  String detailBookedOf(String booked, String capacity) {
    return '$booked of $capacity quintals';
  }

  @override
  String detailLeft(String left) {
    return '$left left';
  }

  @override
  String get detailCropHere => 'crop here';

  @override
  String detailFarmerAcceptedAnswerBy(String time) {
    return 'The farmer accepted. Please answer by $time.';
  }

  @override
  String get detailBooked => 'Booked';

  @override
  String detailQuintalsLeft(String quintals) {
    return '$quintals quintals left';
  }

  @override
  String detailKmToYou(int km) {
    return 'About $km km to you';
  }

  @override
  String detailForQuantity(String quantity) {
    return 'For $quantity';
  }

  @override
  String get detailEtbPerQuintalShort => 'ETB/q';

  @override
  String detailFitsTrip(String quintals) {
    return 'fits this trip ($quintals quintals left)';
  }

  @override
  String detailExceedsTrip(String quintals) {
    return 'more than this trip has room for ($quintals quintals left)';
  }

  @override
  String get detailChange => 'Change';

  @override
  String get detailLess => 'Less';

  @override
  String get detailMore => 'More';

  @override
  String get detailGpsPickFromList =>
      'No GPS? Pick your wereda from the list instead.';

  @override
  String get detailNoPhoneNumbersNote =>
      'No phone numbers: contact opens after payment is held in escrow.';

  @override
  String get detailYourFarm => 'Your farm';

  @override
  String get detailNewOrderTitle => 'New order';

  @override
  String detailFromYourListing(String what) {
    return '$what from your listing';
  }

  @override
  String get detailPaidIntoEscrow => 'Paid into escrow';

  @override
  String get detailPickupLabel => 'Pickup';

  @override
  String detailTruckOf(String name) {
    return '$name\'s truck';
  }

  @override
  String get detailBuyerCollects => 'The buyer collects it';

  @override
  String get detailHeldInEscrow => 'Held in escrow';

  @override
  String detailPartyPaid(String party) {
    return '$party paid';
  }

  @override
  String detailPartyRole(String name, String role) {
    return '$name, $role';
  }

  @override
  String get detailYou => 'You';

  @override
  String detailTimelineThen(String steps) {
    return 'Then $steps. Tap a step to read what it means.';
  }

  @override
  String get detailTimelineTapStep => 'Tap a step to read what it means.';

  @override
  String detailListAnd(String head, String last) {
    return '$head and $last';
  }

  @override
  String get detailContactOpensWhenTruckerAccepts =>
      'Contact opens when the trucker accepts';

  @override
  String detailMessageParty(String name) {
    return 'Message $name';
  }

  @override
  String detailCallParty(String name) {
    return 'Call $name';
  }

  @override
  String liveTripLogStopAhead(int minutes) {
    return 'About $minutes min, then on to you';
  }

  @override
  String marketTransportOn(String day) {
    return 'Transport $day';
  }

  @override
  String marketTripsThisWeek(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count trips this week',
      one: '1 trip this week',
    );
    return '$_temp0';
  }

  @override
  String get marketNoTripYet => 'No trip yet';

  @override
  String marketFootnote(String place) {
    return 'Every farmer\'s listing. Transport is matched for $place.';
  }

  @override
  String get marketFootnoteNoPlace => 'Every farmer\'s listing.';

  @override
  String marketQuintalsLeft(String quantity) {
    return '$quantity quintals left';
  }

  @override
  String get marketSearchToggle => 'Search and price filters';

  @override
  String liveTripEscrowAmount(String amount) {
    return '$amount in escrow';
  }

  @override
  String get liveTripReleasePending => 'Release pending';

  @override
  String get liveTripYouConfirmedDelivery => 'You confirmed delivery';

  @override
  String get liveTripCheckWindowEnds => 'Check window ends';

  @override
  String liveTripPartyGets(String name, String amount) {
    return '$name gets $amount';
  }
}

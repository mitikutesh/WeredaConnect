// ignore: unused_import
import 'package:intl/intl.dart' as intl;
import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Oromo (`om`).
class AppLocalizationsOm extends AppLocalizations {
  AppLocalizationsOm([String locale = 'om']) : super(locale);

  @override
  String get appName => 'WeredaConnect';

  @override
  String get appTitle => 'WeredaConnect';

  @override
  String get welcomeMessage => 'Gara WeredaConnect baga nagaan dhuftan';

  @override
  String get welcomeTitle => 'Gara WeredaConnect Baga Nagaan Dhuftan';

  @override
  String welcomeBackUser(String userName) {
    return 'Baga nagaan dhuftan, $userName';
  }

  @override
  String get worksOffline => 'Oflaayiniin Hojjeta';

  @override
  String get offlineMode => 'Haalata Offline';

  @override
  String get syncPending => 'Walsimsiisuun Eegaa Jira';

  @override
  String get taglineShort =>
      'Qonnaan Bultootaa, Konkolaataa fi Bitootaa Walitti Hidhuu';

  @override
  String get telebirrPayment => 'Kaffaltii Telebirr';

  @override
  String get syncInProgress => 'Walsimsiisuun Itti Fufeera...';

  @override
  String get syncComplete => 'Walsimsiisuun Xumurameera';

  @override
  String get syncFailed => 'Walsimsiisuun Hin Milkoofne';

  @override
  String get retry => 'Irra Deebi\'ii Yaali';

  @override
  String get syncNow => 'Amma Walsimsisi';

  @override
  String get selectLanguagePrompt => 'Afaan Kee Filadhu';

  @override
  String get cancel => 'Dhiisi';

  @override
  String get save => 'Olkaa\'i';

  @override
  String get delete => 'Haqi';

  @override
  String get edit => 'Gulaal';

  @override
  String get editProfile => 'Piroofaayilii Gulaaluu';

  @override
  String get settings => 'Qindaa\'ina';

  @override
  String get skip => 'Utaalu';

  @override
  String get language => 'Afaan';

  @override
  String get dataUsage => 'Itti Fayyadama Deetaa';

  @override
  String get dataSaverMode => 'Haalata Deetaa Qusachuu';

  @override
  String get kebele => 'Gandaa';

  @override
  String get wereda => 'Aanaa';

  @override
  String get errorOccurred => 'Dogongora Uumameera';

  @override
  String get errorNoPermission => 'Kana ilaaluuf hayyama hin qabdan.';

  @override
  String get errorSecureConnectionFailed =>
      'Server waliin walqunnamtii nageenya qabu uumuun hin danda\'amne. Appii haaromsaa yookiin neetworkii biraa yaalaa.';

  @override
  String get noInternetConnection => 'Walitti Dhufeenya Interneetii Hin Jiru';

  @override
  String get errorSessionExpired =>
      'Yeroon seensa keessanii xumurameera. Maaloo irra deebi\'aa seenaa.';

  @override
  String get errorConflict =>
      'Kun bakka biraatti jijjiirameera. Maaloo haaromsaa irra deebi\'aa yaalaa.';

  @override
  String get errorOrderRefused =>
      'Tarkaanfiin ajaja kanaa amma hin hayyamamu, kanaaf wanti jijjiirame hin jiru.';

  @override
  String get errorOrderNotPlaced =>
      'Ajajni kun galmaa\'uu hin dandeenye, kanaaf haqameera. Maaloo irra deebi\'aa ajajaa.';

  @override
  String get errorChangeNotSaved =>
      'Jijjiiramni kun sarvarii irratti olkaa\'amuu hin dandeenye.';

  @override
  String get showingCachedResults =>
      'Bu\'aawwan kuufaman agarsiisaa jira — haaromsuun hin dandaʼamne.';

  @override
  String get dataWillSyncWhenOnline =>
      'Yeroo online taatan deetaan keessan walsimsifama';

  @override
  String get offlineReady => 'Offline Qophaa\'eera';

  @override
  String get home => 'Mana';

  @override
  String get howItWorks => 'Akkamitti akka hojjetu';

  @override
  String get listings => 'Tarreewwan';

  @override
  String get profile => 'Ibsa';

  @override
  String get about => 'Waa\'ee';

  @override
  String get logout => 'Ba\'i';

  @override
  String get cropListings => 'Tarreewwan Midhaan';

  @override
  String get noCropListings => 'Tarreen midhaan amma hin jiru';

  @override
  String get createListing => 'Tarree Uumi';

  @override
  String get myProfile => 'Ibsa Koo';

  @override
  String get next => 'Itti Aanuu';

  @override
  String get notLoggedIn => 'Hin seenne';

  @override
  String get notifications => 'Beeksisa';

  @override
  String get login => 'Seeni';

  @override
  String get comingSoon => 'Dhiyoo Dhufa';

  @override
  String get connectWithMarkets => 'Gabaa Naannoo waliin Walquunnamuu';

  @override
  String get createAccount => 'Herrega Uumuu';

  @override
  String get currentLanguage => 'Afaan Oromoo';

  @override
  String get dataSaverDescription => 'Itti fayyadama daataa hir\'isi';

  @override
  String get aboutDescription => 'Waltajjii loojistikii qonnaa offline-first';

  @override
  String get aboutTagline =>
      'Qonnaan buultota Itoophiyaa, konkolaatota fe\'aa fi bitootaa walitti dhufeenya';

  @override
  String aboutMapDataCredit(String credit) {
    return 'Odeeffannoo kaartaa: $credit';
  }

  @override
  String get activeListings => 'tarreewwan socho\'oo';

  @override
  String get register => 'Galmaa\'i';

  @override
  String get phoneNumber => 'Lakkoofsa Bilbilaa';

  @override
  String get password => 'Jecha Icciitii';

  @override
  String get confirmPassword => 'Jecha Icciitii Mirkaneessi';

  @override
  String get fullName => 'Maqaa Guutuu';

  @override
  String get displayName => 'Maqaa Agarsiisaa';

  @override
  String get displayNameHelper => 'Maqaa gabaatti namoota birootiif mul\'atu';

  @override
  String get verifiedLegalName => 'Maqaa Seeraa Mirkanaa\'e';

  @override
  String get selectRole => 'Ga\'ee Filadhu';

  @override
  String get farmer => 'Qotee Bulaa';

  @override
  String get trucker => 'Konkolaataa Fe\'aa';

  @override
  String get updateDetails => 'Maqaa fi ibsa kee haaromsi';

  @override
  String get buyer => 'Bitaa';

  @override
  String get enterOtp => 'Koodii Mirkaneessaa Galchi';

  @override
  String get otpSent =>
      'Koodiin mirkaneessaa gara bilbila keessaniitti ergameera';

  @override
  String get verifyOtp => 'Mirkaneessi';

  @override
  String get viewOrderHistory => 'Seenaa ajaja kee ilaali';

  @override
  String get voiceSupport => 'Afaan Keetiin Deeggarsa Sagalee';

  @override
  String get resendOtp => 'Irra Deebi\'ii Ergi';

  @override
  String get signUp => 'Galmaa\'i';

  @override
  String get alreadyHaveAccount => 'Akkaawuntii qabduu?';

  @override
  String get dontHaveAccount => 'Akkaawuntii hin qabduu?';

  @override
  String get forgotPassword => 'Jecha Icciitii Irraanfatteettaa?';

  @override
  String get getStarted => 'Jalqabi';

  @override
  String get loginOnline => 'Online ta\'ii Seeni';

  @override
  String get loginOffline => 'Offline ta\'ii Seeni';

  @override
  String get offlineModeIndicator => 'Haalata offline keessa jirtu';

  @override
  String get passwordRequirements =>
      'Jecha icciitiin yoo xiqqaate qubeelee 8, qubee xixiqqaa fi lakkoofsa of keessatti qabaachuu qaba';

  @override
  String get phoneNumberRequired => 'Lakkoofsi bilbilaa barbaachisaadha';

  @override
  String get invalidPhoneFormat =>
      'Lakkoofsi bilbilaa sirrii miti. +251XXXXXXXXX, 00251XXXXXXXXX, ykn 0XXXXXXXXX fayyadami';

  @override
  String get invalidCredentials =>
      'Lakkoofsi bilbilaa ykn jecha icciitii sirrii miti';

  @override
  String get resetCodeInvalid =>
      'Koodiin mirkaneessaa sirrii miti. Maaloo mirkaneessitanii irra deebi\'aa yaalaa.';

  @override
  String get resetCodeExpired =>
      'Koodiin mirkaneessaa yeroon isaa darbeera. Maaloo kan haaraa gaafadhaa.';

  @override
  String get phoneAlreadyRegistered =>
      'Lakkoofsi bilbilaa kun duraanuu galmaa\'eera';

  @override
  String get invalidOtp => 'Koodiin mirkaneessaa sirrii miti';

  @override
  String get otpExpired => 'Koodiin mirkaneessaa darbeeera. Haaraa gaafadhu';

  @override
  String get authRateLimitExceeded =>
      'Yaalii baay\'ee. Maaloo booda irra deebi\'ii yaali.';

  @override
  String get authServerError =>
      'Dogoggora sarvaraa. Maaloo booda irra deebi\'ii yaali.';

  @override
  String get authServiceUnavailable =>
      'Tajaajilli mirkaneessuu yeroo ammaa hin argamu. Maaloo booda irra deebi\'ii yaali.';

  @override
  String get authNetworkError =>
      'Interneetiin hin jiru. Maaloo walqunnamtii keessan mirkaneessaa.';

  @override
  String authAccountLocked(String duration) {
    return 'Yaalii baay\'ee waan kufteef herregni keessan cufameera. Erga $duration booda irra deebi\'ii yaali.';
  }

  @override
  String durationSeconds(int count) {
    return 'sekoondii $count';
  }

  @override
  String durationMinutes(int count) {
    return 'daqiiqaa $count';
  }

  @override
  String durationHours(int count) {
    return 'sa\'aatii $count';
  }

  @override
  String get phoneNotVerified =>
      'Lakkoofsi bilbilaa hin mirkanaa\'iin. Maaloo mirkaneessa OTP xumuraa.';

  @override
  String get authApiNotConfigured =>
      'Qophiin appii hin xumuramne. Maaloo gargaarsa quunnamaa.';

  @override
  String get authOfflineNotAvailable =>
      'Haalata offline dandeessisuuf dursa online seenaa.';

  @override
  String get authAccountDisabled =>
      'Herregni keessan cufameera. Gargaarsa quunnamaa.';

  @override
  String get authPasswordResetRequired =>
      'Jecha icciitii haareffachuun barbaachisa. Maaloo jecha icciitii keessan haareffadhaa.';

  @override
  String get authOtpAttemptsExceeded =>
      'Yaalii baay\'ee kufee. Koodii haaraa gaafadhu.';

  @override
  String get registrationSuccess =>
      'Galmaa\'uun milkaa\'eera! Maaloo bilbila keessan mirkaneessaa';

  @override
  String get loginSuccess => 'Seenuun milkaa\'eera!';

  @override
  String get logoutConfirmation => 'Dhugumatti ba\'uu barbaadduu?';

  @override
  String get manageAlerts => 'Beeksisa bulchi';

  @override
  String get myListings => 'Tarree Koo';

  @override
  String get browseListings => 'Tarree Barbaadi';

  @override
  String get market => 'Gabaa';

  @override
  String get marketCrops => 'Midhaan';

  @override
  String get marketTransport => 'Geejjiba';

  @override
  String get orders => 'Ajaja';

  @override
  String get myOrders => 'Ajaja Koo';

  @override
  String get clearOfflineData => 'Daataa offline haqamuu?';

  @override
  String get clearOfflineDataDescription =>
      'Yoo daataa kana hin haqiin offline ta\'anii seenuu dandeessu';

  @override
  String get ethiopianPhoneExample => 'Fakkeenya: 0911234567 ykn +251911234567';

  @override
  String get characters => 'qubeelee';

  @override
  String get cropTypeTeff => 'Xaafii';

  @override
  String get cropTypeWheat => 'Qamadii';

  @override
  String get cropTypeBarley => 'Garbuu';

  @override
  String get cropTypeMaize => 'Boqqolloo';

  @override
  String get cropTypeChickpea => 'Shumburaa';

  @override
  String get cropTypeLentils => 'Misira';

  @override
  String get cropTypeSorghum => 'Mashilaa';

  @override
  String get cropTypeCoffee => 'Buna';

  @override
  String get cropTypeSesame => 'Saliixaa';

  @override
  String get selectCropType => 'Gosa Midhaan Filadhu';

  @override
  String get quantityInQuintals => 'Hamma (kuuntaalii)';

  @override
  String get priceInBirr => 'Gatii (Birrii/kg)';

  @override
  String get location => 'Bakka';

  @override
  String get description => 'Ibsa';

  @override
  String get audioNote => 'Yaadannoo Sagalee';

  @override
  String get recordAudioNote => 'Yaadannoo Sagalee Galmeessi';

  @override
  String get cropLocationCoordinatesRequired =>
      'GPS hin argamu. Bakka keessan tarree keessaa filadhaa ykn GPS banaa.';

  @override
  String get cropTypeRequired => 'Maaloo gosa midhaan filadhaa';

  @override
  String get quantityRequired => 'Maaloo hamma galchaa';

  @override
  String get priceRequired => 'Gatiin barbaachisaadha';

  @override
  String get locationRequired => 'Maaloo bakka galchaa';

  @override
  String get listingSaved => 'Tarreen milkaa\'inaan olkaa\'ameera';

  @override
  String get listingDeleted => 'Tarreen haqameera';

  @override
  String get allCrops => 'Hunda';

  @override
  String get quintals => 'kuuntaalii';

  @override
  String get etbPerKg => 'Birrii/kg';

  @override
  String get exploreAsGuest => 'Akka Keessummaatti Qoradhu';

  @override
  String get notAuthenticated => 'Tarkaanfii kana fudhachuuf seenuu qabdu';

  @override
  String get passwordsDoNotMatch => 'Jecha icciitiin wal hin simne';

  @override
  String get enterSixDigitCode => 'Maaloo koodii lakkoofsota 6 galchaa';

  @override
  String resendCodeCountdown(int seconds) {
    return 'Koodii irra deebi\'ii erguu sekoondii $seconds keessatti';
  }

  @override
  String get listingStatusActive => 'Ka\'aa Jiru';

  @override
  String get listingStatusDraft => 'Qophii';

  @override
  String get listingStatusSold => 'Gurguramee Jira';

  @override
  String get listingStatusReserved => 'Qabamee';

  @override
  String get listingStatusExpired => 'Darbe';

  @override
  String get listingFilterAll => 'Hunda';

  @override
  String get listingSeedTestButton => 'Tarree Qormaataa Uumi';

  @override
  String get backToHome => 'Gara Manatti Deebi\'i';

  @override
  String get tripListings => 'Tarree Imaltuu';

  @override
  String get createTrip => 'Imala Uumi';

  @override
  String get browseTrips => 'Imala Barbaadi';

  @override
  String get myTrips => 'Imala Koo';

  @override
  String get tripDetails => 'Bal\'ina Imaltuu';

  @override
  String get fromWoreda => 'Irraa (Ka\'umsaa)';

  @override
  String get toWoreda => 'Gara (Ga\'umsaa)';

  @override
  String get departureDate => 'Guyyaa Ka\'umsaa';

  @override
  String get capacity => 'Dandeettii (Kuintaalii)';

  @override
  String get pricePerQuintal => 'Gatii Kuintaalii (ETB)';

  @override
  String get tripStatusOpen => 'Banaa';

  @override
  String get tripStatusMatched => 'Walsiimame';

  @override
  String get tripStatusInTransit => 'Karaa Irra';

  @override
  String get tripStatusDelivered => 'Ga\'e';

  @override
  String get tripStatusCancelled => 'Haqame';

  @override
  String get noTripsFound => 'Imalti hin argamne';

  @override
  String get tripCreatedSuccess => 'Imalti milkaa\'inaan uumame!';

  @override
  String get tripCancelledSuccess => 'Imalti haqame';

  @override
  String get filterByRoute => 'Karaan cali';

  @override
  String get availableCapacity => 'Dandeettii jiru';

  @override
  String get selectDepartureDate => 'Guyyaa ka\'umsaa filadhu';

  @override
  String get truckerContact => 'Quunnamtii Konkolaachisaa';

  @override
  String get fromWoredaRequired => 'Aanaan ka\'umsaa barbaachisaadha';

  @override
  String get toWoredaRequired => 'Aanaan ga\'umsaa barbaachisaadha';

  @override
  String get departureDateRequired => 'Guyyaan ka\'umsaa barbaachisaadha';

  @override
  String get capacityRequired => 'Dandeettiin barbaachisaadha';

  @override
  String get invalidCapacity =>
      'Dandeettiin 0.1 hanga 1000 kuintaalii ta\'uu qaba';

  @override
  String get invalidPrice => 'Gatiin 0.01 hanga 10000 ETB ta\'uu qaba';

  @override
  String get sameOriginDestination =>
      'Ka\'umsaa fi ga\'umsaan tokko ta\'uu hin danda\'u';

  @override
  String get findTransport => 'Geejjiba Barbaadi';

  @override
  String get viewCropListings => 'Tarreewwan midhaan kee ilaali';

  @override
  String get cancelTrip => 'Imala Haqi';

  @override
  String get cancelTripConfirmation =>
      'Imala kana haquuf mirkanii? Tarkaanfiin kun deebi\'uu hin danda\'u.';

  @override
  String get truckerName => 'Maqaa Konkolaachisaa';

  @override
  String get truckerPhone => 'Bilbila Konkolaachisaa';

  @override
  String get route => 'Karaa';

  @override
  String get viewTripListings => 'Tarreewwan imaltuu kee ilaali';

  @override
  String get trips => 'Imaltuu';

  @override
  String get truckerNameRequired => 'Maqaan konkolaachisaa barbaachisaadha';

  @override
  String get vehiclePlate => 'Lakkoofsa Gabatee Konkolaataa';

  @override
  String get vehicleDescription => 'Ibsa Konkolaataa';

  @override
  String get deliveries => 'Geejjibaalee';

  @override
  String get deliveryMyDeliveries => 'Geejjibaalee Koo';

  @override
  String get deliveryDetails => 'Bal\'insa Geejjibaa';

  @override
  String get deliveryScanQrCode => 'Koodii QR Iskaan';

  @override
  String get deliveryShowQrCode => 'Koodii QR';

  @override
  String get deliveryRaiseDispute => 'Komii Dhiyeessi';

  @override
  String get deliveryStatusPending => 'Eeggataa';

  @override
  String get deliveryStatusPickedUp => 'Fudhatame';

  @override
  String get deliveryStatusInTransit => 'Daandii Irra';

  @override
  String get deliveryStatusDelivered => 'Geejjibame';

  @override
  String get deliveryStatusDisputed => 'Komii';

  @override
  String get deliveryStatusCancelled => 'Haqame';

  @override
  String get deliveryConfirmPickup => 'Fudhachuu Mirkaneessi';

  @override
  String get deliveryConfirmDelivery => 'Geejjiba Mirkaneessi';

  @override
  String get deliverySelfPickupConfirm =>
      'Fudhachuuf QR qonnaan bulaa iskaanii godhi';

  @override
  String get deliverySelfPickupPayFirst =>
      'Duraan escrow keessa kaffali, ergasii qonnaan bulaa biratti fudhachuuf iskaanii godhi.';

  @override
  String get orderOpenDelivery => 'Geejjiba bani';

  @override
  String get orderCollectMyself => 'Ani ofiin nan fudhadha';

  @override
  String get deliveryScanQr => 'QR Iskaan';

  @override
  String get deliveryShowQr => 'Koodii QR Agarsiisi';

  @override
  String get deliveryRaiseDisputeAction => 'Komii Galchi';

  @override
  String get deliveryCancel => 'Geejjiba Haqi';

  @override
  String get deliveryTakePhoto => 'Suuraa Kaasi';

  @override
  String get deliveryTimeLabel => 'Yeroo';

  @override
  String get deliveryRegenerateQr => 'QR Haaromsi';

  @override
  String get deliveryFarmer => 'Qonnaan Bulaa';

  @override
  String get deliveryTrucker => 'Konkolaachisaa';

  @override
  String get deliveryBuyer => 'Bitaa';

  @override
  String deliveryGpsAccuracy(String meters) {
    return 'Sirrii: ${meters}m';
  }

  @override
  String get deliveryGpsCaptured => 'GPS Qabame';

  @override
  String get deliveryGpsUnavailable => 'GPS Hin Argamne';

  @override
  String get deliveryGpsWithinRange => 'Bal\'ina Keessa';

  @override
  String get deliveryGpsOutOfRange => 'Bal\'ina Alaa';

  @override
  String get deliveryGpsCapturing => 'GPS Qabamaa...';

  @override
  String get deliveryGpsLabel => 'GPS';

  @override
  String get deliveryGpsNotCaptured => 'Hin qabamne';

  @override
  String get deliveryGpsPermissionDenied => 'Hayyama Iddoo Didame';

  @override
  String get deliveryQrShowInstructionBuyer =>
      'Yeroo bitaan fudhatu koodii QR kana bitaaf agarsiisi';

  @override
  String get deliveryQrShowInstruction =>
      'Koodii QR kana konkolaachisaaf agarsiisi';

  @override
  String get deliveryQrOfflineNote =>
      'Koodiin QR kun intarneetii malee hojjata.';

  @override
  String get deliveryQrVerified => 'Mirkaneessiin Darbe';

  @override
  String get deliveryQrFailed => 'Mirkaneessiin Kufame';

  @override
  String get deliveryQrPointCamera => 'Kaameeraa gara koodii QR qajeelchi';

  @override
  String get deliveryDisputeReason => 'Sababii Komii';

  @override
  String get deliveryDisputeTimeRemaining => 'Yeroo Hafee';

  @override
  String get deliveryDisputeExpired => 'Yeroon Komii Darbee';

  @override
  String get deliveryDisputeWarning =>
      'Komiin WeredaConnect\'n ni ilaalama. Ibsa ifaa kenniiti.';

  @override
  String get deliveryDisputeSubmit => 'Komii Galchi';

  @override
  String get deliveryFilterAll => 'Hunda';

  @override
  String get deliveryFilterPending => 'Eeggataa';

  @override
  String get deliveryFilterPickedUp => 'Fudhatame';

  @override
  String get deliveryFilterInTransit => 'Daandii Irra';

  @override
  String get deliveryFilterDelivered => 'Geejjibame';

  @override
  String get deliveryFilterRole => 'Gahee\'n Caalchisi';

  @override
  String get deliveryNoDeliveries => 'Hanga ammaatti geejjiba hin jiru';

  @override
  String get deliveryNoDeliveriesHint =>
      'Geejjibaleen keessan as irratti mul\'atu';

  @override
  String get deliveryOfflineNote => 'Intarneetii malee hojjata';

  @override
  String get deliveryCreatedAt => 'Uumame';

  @override
  String get deliveryCancelConfirmTitle => 'Geejjiba Haquu?';

  @override
  String get deliveryCancelConfirmMessage => 'Geejjiba kana haquu barbaaddaa?';

  @override
  String get deliveryDisputeConfirmTitle => 'Komii Galchuu?';

  @override
  String get deliveryDisputeConfirmMessage => 'Komii dhiyeessuu barbaaddaa?';

  @override
  String get deliveryPhotoPickup => 'Suuraa Fudhachuu';

  @override
  String get deliveryPhotoDropoff => 'Suuraa Geejjibaa';

  @override
  String get deliveryAddPhoto => 'Suuraa Ida\'i';

  @override
  String get deliveryMarkInTransit => 'Daandii Irra Mallatteessi';

  @override
  String get deliveryInTransitConfirmMessage =>
      'Geejjiba kana daandii irra mallatteessuu barbaaddaa?';

  @override
  String get deliveryReasonRequired => 'Maaloo sababii galchaa';

  @override
  String get deliveryReasonTooLong => 'Sababiin arfii 500 hin caalu';

  @override
  String get deliveryTryAgain => 'Irra Deebi\'i Yaali';

  @override
  String deliveryIdLabel(String id) {
    return 'Geejjiba #$id';
  }

  @override
  String get deliveryPickupSection => 'Fudhachuu';

  @override
  String get deliveryDeliverySection => 'Geejjiba';

  @override
  String get deliveryNotYetPickedUp => 'Hanga ammaatti hin fudhatamne';

  @override
  String get deliveryNotYetDelivered => 'Hanga ammaatti hin geejjibamne';

  @override
  String get deliveryToggleFlash => 'Ibsaa jijjiiri';

  @override
  String deliveryConfirmedAtLabel(String date) {
    return 'Mirkanaa\'e: $date';
  }

  @override
  String get deliverySeedTestButton => 'Geejjiba Qormaataa Uumi';

  @override
  String get escrowPayments => 'Kaffaltii';

  @override
  String get escrowPayForDelivery => 'Geejjibaaf Kaffali';

  @override
  String get escrowPaymentStatus => 'Haala Kaffaltii';

  @override
  String get escrowPaymentHistory => 'Kaffaltii Koo';

  @override
  String get escrowRaiseDispute => 'Komii Dhiyeessi';

  @override
  String get escrowStatusInitiated => 'Kaffaltiin Eeggataa';

  @override
  String get escrowStatusEscrowed => 'Maallaqni Nagaan Qabame';

  @override
  String get escrowStatusReleased => 'Maallaqni Gadhiifame';

  @override
  String get escrowStatusCompleted => 'Kaffaltiin Xumurameera';

  @override
  String get escrowStatusCancelled => 'Haqame';

  @override
  String get escrowStatusDisputed => 'Komii Keessa';

  @override
  String get escrowStatusRefunded => 'Deebifameera';

  @override
  String get escrowStatusHeld => 'Ilaalamaa Jira';

  @override
  String get escrowStatusUnknown => 'Haalli Hin Beekamu';

  @override
  String get escrowProtectionInfo =>
      'Kaffaltiin keessan geejjibni mirkanaa\'uu hanga gahutti nagaan qabama.';

  @override
  String get escrowTotalAmount => 'Hamma Waliigalaa';

  @override
  String escrowCommission(String rate) {
    return 'Komishiinii ($rate%)';
  }

  @override
  String get escrowFarmerReceives => 'Qonnaan Bulaan Kan Fudhatu';

  @override
  String get escrowTruckerReceives => 'Konkolaachisaan Kan Fudhatu';

  @override
  String get escrowConfirmPayment => 'Kaffaltii Mirkaneessi';

  @override
  String escrowConfirmPaymentDialog(String amount, String farmerName) {
    return 'Birrii $amount $farmerName\'f kaffaluu?';
  }

  @override
  String get escrowFaydaWarning =>
      'Hamma Birrii 10,000 olii tiif mirkaneessa Fayda ID barbaachisa.';

  @override
  String get escrowFundsHeldInfo =>
      'Maallaqni guyyaa 7 qabama. Yoo hin geejjibamiin deebifama.';

  @override
  String get escrowPaymentQueued =>
      'Kaffaltiin galmaa\'e. Intarneetii yeroo argattan raawwatama.';

  @override
  String get escrowPaymentSuccess => 'Kaffaltiin milkaa\'inaan jalqabame.';

  @override
  String get escrowPaymentOpening => 'Telebirr banaa jira…';

  @override
  String get escrowPaymentOpenFailed =>
      'Fuula kaffaltii banuun hin danda\'amne. Telebirr ykn barbaadaan (browser) akka jiraatu mirkaneeffadhu.';

  @override
  String get escrowPaymentConfirmed =>
      'Kaffaltiin mirkanaa\'e. Maallaqni nagaan qabameera.';

  @override
  String get escrowAwaitingPayment =>
      'Kaffaltiin Telebirr keessan akka mirkanaa\'u eegaa jira…';

  @override
  String get escrowOpenPaymentAgain => 'Kaffaltii Telebirr bani';

  @override
  String get escrowDeliveryLabel => 'Geejjiba';

  @override
  String get escrowFarmerLabel => 'Qonnaan Bulaa';

  @override
  String get escrowBuyerLabel => 'Bitaa';

  @override
  String get escrowCreatedLabel => 'Uumame';

  @override
  String get escrowExpiresLabel => 'Yeroon Darbu';

  @override
  String get escrowTelebirrRefLabel => 'Wabii Telebirr';

  @override
  String get escrowMayBeOutdated =>
      'Haalichi haaromfamuu dhiisuu danda\'a. Haaromsuuf harkisi.';

  @override
  String escrowLastUpdated(String time) {
    return 'Dhumarratti haaromfame $time';
  }

  @override
  String get escrowFilterAll => 'Hunda';

  @override
  String get escrowFilterBuyer => 'Akka Bitaa';

  @override
  String get escrowFilterFarmer => 'Akka Qonnaan Bulaa';

  @override
  String escrowPendingSection(int count) {
    return 'Eeggataa ($count)';
  }

  @override
  String get escrowHistorySection => 'Seenaa';

  @override
  String get escrowNoPayments => 'Hanga ammaatti kaffaltiin hin jiru.';

  @override
  String get escrowNoMatchingPayments => 'Kaffaltiin walsimatu hin argamne.';

  @override
  String get escrowCancelIntent => 'Haqi';

  @override
  String get escrowCancelIntentDialog => 'Kaffaltii eeggataa kana haquu?';

  @override
  String get escrowIntentCancelled => 'Kaffaltiin eeggataa haqameera.';

  @override
  String escrowDisputeTimeRemaining(int hours, int minutes) {
    return 'Yeroo hafee: sa\'a $hours daqiiqaa $minutes';
  }

  @override
  String get escrowDisputeWindowExpired => 'Yeroon komii darbee jira.';

  @override
  String get escrowDisputeReasonLabel => 'Sababii';

  @override
  String get escrowDisputeDescriptionLabel => 'Ibsa (filannoo)';

  @override
  String get escrowDisputeDescriptionHint => 'Rakkoo ibsi...';

  @override
  String get escrowDisputeSubmit => 'Komii Galchi';

  @override
  String get escrowDisputeConfirmDialog =>
      'Kun maallaqa hanga ilaalametti dhaaba. Itti fufuu?';

  @override
  String get escrowDisputeSuccess =>
      'Komiin galmaa\'eera. Waltajjiin sa\'aatii 48 keessatti ilaala.';

  @override
  String get escrowDisputeReviewInfo =>
      'Komiin waltajjiidhaan sa\'aatii 48 keessatti ilaalama.';

  @override
  String get escrowDisputeRequiresInternet =>
      'Komii dhiyeessuuf intarneetiin barbaachisaadha.';

  @override
  String get escrowReasonDamagedGoods => 'Meeshaa miidhame';

  @override
  String get escrowReasonWrongQuantity => 'Hamma dogoggoraa';

  @override
  String get escrowReasonWrongCrop => 'Gosa midhaan dogoggoraa';

  @override
  String get escrowReasonNotDelivered => 'Hin geejjibamne';

  @override
  String get escrowReasonQualityMismatch => 'Qulqullinni wal hin simne';

  @override
  String get escrowReasonOther => 'Kan biroo';

  @override
  String get escrowErrorInvalidAmount =>
      'Hammni Birrii 100 hanga Birrii 500,000 ta\'uu qaba.';

  @override
  String get escrowErrorFaydaRequired =>
      'Hamma Birrii 10,000 olii tiif mirkaneessa Fayda ID barbaachisa.';

  @override
  String get escrowErrorAlreadyExists =>
      'Geejjiba kanaaf kaffaltiin duraan jira.';

  @override
  String get escrowErrorOrderAlreadyFunded =>
      'Kaffaltiin ajaja kanaa duraan eskiroo keessatti qabamee jira.';

  @override
  String get escrowErrorGateway =>
      'Dogoggora tajaajila Telebirr. Maaloo irra deebi\'ii yaali.';

  @override
  String get escrowErrorServiceUnavailable =>
      'Telebirr yeroo ammaa hin argamu. Maaloo booda yaali.';

  @override
  String get escrowErrorGeneric =>
      'Wanti dogoggoraa ta\'eera. Maaloo irra deebi\'ii yaali.';

  @override
  String get escrowErrorAuthRequired => 'Maaloo itti fufuuf seeni.';

  @override
  String get escrowErrorConflict => 'Gaaffiin kun duraan adeemsifamaa jira.';

  @override
  String get escrowErrorRateLimit =>
      'Gaaffiiwwan baay\'ee ergamaniiru. Maaloo turtii booda yaali.';

  @override
  String get escrowErrorOffline => 'Hojii kanaaf interneetiin barbaachisaadha.';

  @override
  String get escrowErrorInvalidDelivery => 'Wabii geejjibsiisaa sirrii miti.';

  @override
  String get escrowErrorUnauthorized => 'Gocha kanaaf mirga hin qabdan.';

  @override
  String get escrowErrorDeliveryNotFound => 'Geejjibsiisni hin argamne.';

  @override
  String get escrowErrorInvalidDeliveryState =>
      'Geejjibsiisni kaffaltiidhaf haala sirrii keessa hin jiru.';

  @override
  String get escrowErrorInvalidDisputeReason =>
      'Maaloo sababa komii sirrii filadhaa.';

  @override
  String get escrowErrorDisputeWindowExpired =>
      'Yeroon komii darbera (sa\'aatii 48 geejjibsiisa booda).';

  @override
  String get escrowErrorDisputeAlreadyExists =>
      'Kaffaltii kanaaf komiin duraan ka\'era.';

  @override
  String get escrowErrorPayoutInProgress =>
      'Kaffaltiin kun bulchaan kaffalamaa waan jiruuf, amma komiin itti ka\'uu hin danda\'u. Deeggarsa qunnamaa.';

  @override
  String get escrowErrorDeliveryPaymentUnavailable =>
      'Kaffaltiin online geejjibsiisaaf asitti hin argamu. Qooda isaa ajaja kana fuula ajajaa irraa kaffalaa.';

  @override
  String get escrowErrorDisputeBeforePickup =>
      'Komiin kan ka\'uu danda\'u erga meeshaan fe\'amee booda qofa. Sana dura ajaja haqaa, ykn deeggarsa qunnamaa.';

  @override
  String get escrowErrorInvalidTransition =>
      'Gochi kun haala kaffaltii ammaa keessatti hin hayyamamu.';

  @override
  String escrowEtbAmount(String amount) {
    return 'Birrii $amount';
  }

  @override
  String get profileRestartApp => 'Maaloo app irra deebi\'i bani';

  @override
  String get profileLoadError => 'Profaayilii fayyadamaa fe\'uu hin dandeenye';

  @override
  String get escrowTimeline => 'Tartiiba Yeroo';

  @override
  String get escrowDisputeDetailsTitle => 'Ibsa Dhibdee';

  @override
  String get recording => 'Waraabamaa jira...';

  @override
  String get stopRecording => 'Waraabbii Dhaabi';

  @override
  String get playAudio => 'Taphadhu';

  @override
  String get pauseAudio => 'Dhaabi';

  @override
  String get deleteRecording => 'Waraabbii Haqi';

  @override
  String get deleteRecordingConfirm => 'Yaadannoo sagalee kana haquu?';

  @override
  String get microphonePermissionRequired =>
      'Sagalee waraabbuuf hayyama maayikiroofoonii barbaachisa';

  @override
  String get audioRecorded => 'Yaadannoon sagalee waraabame';

  @override
  String get audioTooLong => 'Dheerinni waraabbii guddaan sekoondii 30';

  @override
  String get audioRecordingLimit => 'Guddaan sekoondii 30';

  @override
  String get reRecord => 'Irra Deebi\'ii Waraabi';

  @override
  String get audioRecordingFailed =>
      'Waraabbiiin hin milkoofne. Maaloo irra deebi\'ii yaali.';

  @override
  String secondsAbbreviation(int count) {
    return '${count}s';
  }

  @override
  String get dataUsageDownloaded => 'Buufame';

  @override
  String get dataUsageUploaded => 'Ergame';

  @override
  String get dataUsageTotal => 'Waliigalaa';

  @override
  String get dataUsageReset => 'Deebi\'i';

  @override
  String get dataUsageResetConfirm => 'Tilaaloo itti fayyadama daataa haquu?';

  @override
  String get dataUsageTrackingSince => 'Irraa eegalee';

  @override
  String get dataUsageScopeNote =>
      'Kun gaaffii herregni kun meeshaa kana irraa gara sarvaroota WeredaConnect ergu qofa lakkaa\'a. Itti fayyadamni daataa bilbila keessanii waliigalaa (haaromsa appii, suuraalee, appiiwwan biroo) kana caaluu danda\'a. Daataan seensaa (sign-in) hin dabalamne.';

  @override
  String get conflictTitle => 'Deetaan Haaromfame';

  @override
  String get conflictExplanation =>
      'Meeshaan biraa ykn serveriin galmee kana haaromse. Kophi naannoo kee haaromfameera.';

  @override
  String get conflictAccept => 'Tole, Hubadhe';

  @override
  String get conflictDismiss => 'Cufi';

  @override
  String get conflictEntityCropListing => 'Tarree Midhaan';

  @override
  String get conflictEntityDelivery => 'Geejjiba';

  @override
  String get conflictEntityTrip => 'Imala';

  @override
  String get conflictEntityOrder => 'Ajaja';

  @override
  String get conflictEntityRating => 'Madaallii';

  @override
  String get conflictEntityPayment => 'Kaffaltii';

  @override
  String get conflictServerValues => 'Gatii Ammaa';

  @override
  String get conflictNoDetails => 'Bal\'inni hin jiru';

  @override
  String conflictBannerSingle(String entityType) {
    return '$entityType kee meeshaa biraan haaromfame';
  }

  @override
  String conflictBannerMultiple(int count) {
    return 'Galmeeleen $count haaromfaman';
  }

  @override
  String get conflictBannerAction => 'Jijjiirama ilaali';

  @override
  String get conflictScreenTitle => 'Haaromsa';

  @override
  String get conflictYourVersion => 'Versiinkee';

  @override
  String get conflictCurrentVersion => 'Versii Ammaa';

  @override
  String get conflictServerApplied => 'Versii ammaa hojiirra ooleera';

  @override
  String get conflictGotIt => 'Hubadhe';

  @override
  String get conflictEmptyTitle => 'Hundumtuu Haaraa Dha';

  @override
  String get conflictEmptySubtitle => 'Haaromsi hin jiru';

  @override
  String conflictEntityDeleted(String entityType) {
    return '$entityType kun haquudhaan balleeffame';
  }

  @override
  String get conflictShowAllChanges => 'Jijjiirama hunda agarsiisi';

  @override
  String get fraudDuplicateListing =>
      'Tarreen walfakkaataan duraan jira. Maaloo tarree jiru haaromsi.';

  @override
  String fraudVelocityExceeded(int minutes) {
    return 'Tarreewwan baay\'ee uumteetta. Maaloo daqiiqaa $minutes booda irra deebi\'ii yaali.';
  }

  @override
  String get fraudAccountHeld =>
      'Herregni keessan ilaalamaa waan jiruuf, gochi kun yeroof dhaabbateera. Deeggarsa qunnamaa.';

  @override
  String get profileSaved => 'Profaayiliin haaromfame';

  @override
  String get noNotificationsYet => 'Hanga ammaatti beeksisni hin jiru';

  @override
  String get noNotificationsDescription =>
      'Akeekkachiisni keessan asitti mul\'ata';

  @override
  String get createDelivery => 'Ergaa Uumi';

  @override
  String get selectPaymentMethod => 'Mala kaffaltii filadhu';

  @override
  String get paymentMethodCbe => 'Baankii Daldala Itiyoophiyaa (CBE)';

  @override
  String get paymentMethodTelebirr => 'Telebirr';

  @override
  String get mockPaymentNotice =>
      'Haala agarsiisaa: kaffaltiiwwan fakkeenya. Dabarsi dhugaan hin taasifamu.';

  @override
  String get telebirrPayInstructions =>
      'Ajaja kanaaf kaffaltii Telebirr fakkeessuuf Kaffaltii Galchi tuqi.';

  @override
  String get transactionIdOptional => 'Lakkoofsa Daldala CBE (filannoo)';

  @override
  String get copy => 'Garagalchi';

  @override
  String get call => 'Bilbili';

  @override
  String get close => 'Cufi';

  @override
  String get farmerPhone => 'Bilbila Qonnaan Bulaa';

  @override
  String get phoneNotAvailable => 'Lakkoofsi bilbilaa hin argamu';

  @override
  String get rateFarmer => 'Qonnaan Bulaa Sadarkeessi';

  @override
  String get rateTrucker => 'Konkolaachisaa Sadarkeessi';

  @override
  String get submitRating => 'Sadarkaa Galchi';

  @override
  String get ratingCommentHint => 'Yaada dabaluu (filannoo)';

  @override
  String get ratingThankYou => 'Sadarkaa keessaniif galatoomaa!';

  @override
  String get alreadyRated => 'Sadarkeeffameera';

  @override
  String get noRatingsYet => 'Hanga ammaatti sadarkaan hin jiru';

  @override
  String get farmerRating => 'Sadarkaa Qonnaan Bulaa';

  @override
  String ratingsCount(int count) {
    return 'sadarkaa $count';
  }

  @override
  String get contactFarmer => 'Qonnaan Bulaa Quunnamuu';

  @override
  String get detailQuantity => 'Hamma';

  @override
  String get detailPrice => 'Gatii';

  @override
  String get detailCreated => 'Kan Uumame';

  @override
  String get filterMin => 'Xiqqaa';

  @override
  String get filterMax => 'Guddaa';

  @override
  String get selectTrip => 'Imala Filadhu';

  @override
  String get buyerInformation => 'Odeeffannoo Bitaa';

  @override
  String get buyerNameLabel => 'Maqaa Bitaa';

  @override
  String get buyerPhoneIdLabel => 'Bilbila / Eenyummaa Bitaa';

  @override
  String get buyerNameRequired => 'Maqaan bitaa barbaachisaadha';

  @override
  String get buyerIdRequired => 'Bilbilli ykn eenyummaan bitaa barbaachisaadha';

  @override
  String get tripSelectionRequired => 'Maaloo imala filadhaa';

  @override
  String get chooseTripHint => 'Imala filadhaa';

  @override
  String get noOpenTrips => 'Imalii banaa hin jiru. Dursa imala uumaa.';

  @override
  String get deliveryCreatedSuccess => 'Ergaan milkaa\'inaan uumame';

  @override
  String get farmerContactInfo => 'Odeeffannoo Qonnaan Bulaa';

  @override
  String get copiedToClipboard => 'Garagalchamee';

  @override
  String get forgotPasswordContactSupport =>
      'Jecha darbii keessan haaromsuuf gargaarsa +251-111-234567 irratti quunnamaa.';

  @override
  String resendOtpIn(int seconds) {
    return 'Sekondii $seconds keessatti irra deebi\'ii ergi';
  }

  @override
  String get otpResent => 'OTP milkaa\'inaan ergameera';

  @override
  String get ok => 'Tole';

  @override
  String get tutorialComingSoon =>
      'Karuuzeliin barnoota dhiyootti ni dhufa. Herrega uumuudhaan appicha sakatta\'uu dandeessu!';

  @override
  String get escrowDescInitiated => 'Mirkaneessa Telebirr eegaa jira';

  @override
  String get escrowDescEscrowed =>
      'Maallaqni hanga geejjibsiifamutti nagaan qabameera';

  @override
  String get escrowDescReleased => 'Maallaqni qonnaan bultaatti ergameera';

  @override
  String get escrowDescCompleted => 'Daldalli xumurameera';

  @override
  String get escrowDescCancelled => 'Kaffaltiin haqameera';

  @override
  String get escrowDescDisputed => 'Mana maree platformiin ilaalamaa jira';

  @override
  String get escrowDescRefunded => 'Maallaqni bitaadhaaf deebi\'eera';

  @override
  String get escrowDescHeld =>
      'Hanga waltajjiin ilaalutti kaffaltiin dhaabbateera. Maallaqni nagaan jira.';

  @override
  String get escrowDescUnknown =>
      'Haala kaffaltii kanaa isa dhiyoo ilaaluuf appii haaromsaa.';

  @override
  String escrowPayoutExpectedOn(String date) {
    return 'Kaffaltiin $date irratti eegama';
  }

  @override
  String get escrowStatusRefundPending => 'Deebii Eegamaa Jira';

  @override
  String get escrowDescRefundPending =>
      'Ajajni haqameera. Maallaqni gara bitaatti deebi\'aa jira';

  @override
  String escrowOrderShort(String id) {
    return 'Ajaja #$id';
  }

  @override
  String get conflictFieldCropType => 'Gosa Midhaan';

  @override
  String get conflictFieldQuantity => 'Hamma';

  @override
  String get conflictFieldPricePerKg => 'Gatii/kg';

  @override
  String get conflictFieldStatus => 'Haala';

  @override
  String get conflictFieldRoute => 'Karaa';

  @override
  String get conflictFieldCapacity => 'Dandeettii';

  @override
  String get conflictFieldTrip => 'Imala';

  @override
  String get escrowPayButton => 'Kaffalii';

  @override
  String escrowDisputeReasonPrefix(String reason) {
    return 'Sababaa: $reason';
  }

  @override
  String escrowDeliveryNumber(String id) {
    return 'Geejjibsiisa #$id';
  }

  @override
  String escrowPaymentTo(String name) {
    return 'Gara: $name';
  }

  @override
  String escrowPaymentAmount(String amount) {
    return 'Kaffaltii: ETB $amount';
  }

  @override
  String escrowDeliveryShort(String id) {
    return 'Geej. #$id';
  }

  @override
  String get escrowQueuedStatus => 'Tarree irra';

  @override
  String get genericError => 'Rakkoon uumameera. Maaloo irra deebi\'ii yaali.';

  @override
  String get authErrorRegistrationFailed =>
      'Galmeen hin milkoofne. Maaloo irra deebi\'ii yaali.';

  @override
  String get authErrorOtpFailed =>
      'Mirkaneessuu OTP hin milkoofne. Maaloo irra deebi\'ii yaali.';

  @override
  String get authErrorResendOtpFailed =>
      'OTP irra deebi\'ii erguu hin milkoofne. Maaloo irra deebi\'ii yaali.';

  @override
  String get authErrorLoginFailed =>
      'Seenuu hin milkoofne. Maaloo irra deebi\'ii yaali.';

  @override
  String get authErrorOfflineLoginFailed =>
      'Oflaayiniin seenuu hin milkoofne. Maaloo dursa onlaayiniin seeni.';

  @override
  String get authErrorSecureStorage =>
      'Kuusaa nageenya qaqqabuu irratti dogoggora. Maaloo appii irra deebi\'ii jalqabi.';

  @override
  String get authErrorDatabase =>
      'Dogoggora kuusaa deetaa. Maaloo appii irra deebi\'ii jalqabi.';

  @override
  String get authErrorUserNotConfirmed =>
      'Maaloo dursa lakkoofsa bilbilaa keessan mirkaneessaa.';

  @override
  String get authErrorTokenExpired =>
      'Yeroon sesshinii darbe. Maaloo irra deebi\'ii seeni.';

  @override
  String get deliveryErrorInvalidId => 'Eenyummaa geejjibsiisa sirrii miti';

  @override
  String get deliveryErrorStatusInvalid =>
      'Haalli geejjibsiisa gocha kanaaf sirrii miti';

  @override
  String get deliveryErrorUnauthorized => 'Gocha kanaaf mirga hin qabdan';

  @override
  String get deliveryErrorDisputeExpired => 'Yeroon komii darbe';

  @override
  String get deliveryErrorDisputeReasonRequired => 'Maaloo sababaa ibsaa';

  @override
  String get deliveryErrorGpsInvalid => 'Qindeessaa GPS sirrii miti';

  @override
  String get deliveryErrorGeneric => 'Hojiin geejjibsiisa hin milkoofne';

  @override
  String get deliveryErrorNotFound => 'Geejjibsiisni hin argamne';

  @override
  String get deliveryErrorQrPending =>
      'Koodiin QR qonnaan bulaa hanga ammaatti sarvarii hin geenye. Qonnaan bulaan interneetii irratti koodii QR akka banu gaafadhaa, achiis irra deebi\'aa yaalaa.';

  @override
  String get deliveryErrorCancelNotAllowed =>
      'Midhaan waan fudhatameef geejjibni kun amma haqamuu hin danda\'u. Rakkoon yoo jiraate deeggarsa qunnamaa.';

  @override
  String get deliveryErrorDisputeRefused =>
      'Komiin kun amma banamuu hin danda\'u. Deeggarsa qunnamaa.';

  @override
  String get deliveryQrTooLarge => 'QR koodiin baay\'ee guddaadha';

  @override
  String get deliveryQrIncomplete => 'Daataan QR guutuu miti';

  @override
  String get deliveryQrInvalidData => 'Daataan QR sirrii miti';

  @override
  String get deliveryQrWrongDelivery =>
      'QR koodiin geejjibsiisa kana waliin hin malu';

  @override
  String get deliveryQrInvalidFormat => 'Bifa QR koodii sirrii miti';

  @override
  String get deliveryQrProcessFailed => 'QR koodii adeemsisuu hin danda\'amne';

  @override
  String get escrowPaymentNotFound => 'Kaffaltiin hin argamne';

  @override
  String get escrowPaymentNotFoundForDelivery =>
      'Geejjibsiisa kanaaf kaffaltiin hin argamne';

  @override
  String get pleaseWait => 'Maaloo eegaa...';

  @override
  String get greeting => 'Nagaa';

  @override
  String get homeSubtitle => 'Har\'a maal gochuu barbaaddu?';

  @override
  String get bannerHeadline => 'DALLAALAAN HIN BARBAACHISU';

  @override
  String get bannerSubtitle => 'Qonnaan bultoota\'f gatii haqa qabu kallattiin';

  @override
  String get quickActions => 'Tarkaanfiiwwan Ariifataa';

  @override
  String get overview => 'Waliigala';

  @override
  String get atAGlance => 'Ilaalcha Gabaabaa';

  @override
  String get recentActivity => 'Dhiyoo';

  @override
  String get noRecentActivity => 'Sochiin dhiyoo hin jiru';

  @override
  String get sell => 'Gurguri';

  @override
  String get buy => 'Biti';

  @override
  String get truckers => 'Konkolaachiftoonni';

  @override
  String get escrow => 'Eeskiroo';

  @override
  String get availableLoads => 'Fe\'annoo';

  @override
  String get activeFarmers => 'Qonnaan Bultoota';

  @override
  String get inTransit => 'Geejjibamaa jira';

  @override
  String get matchedLoads => 'Walsimatan';

  @override
  String get farmersBrowsed => 'Midhaan Jiran';

  @override
  String get marketplace => 'Gabaa';

  @override
  String get browseVerifiedFarmers => 'Qonnaan bultoota mirkanaa\'an ilaali';

  @override
  String get manageYourListings => 'Tarreeffama kee bulchi';

  @override
  String get searchListings => 'Midhaan, qonnaan bultoota barbaadi...';

  @override
  String get noListingsMatchFilters =>
      'Tarreen barbaacha kee wajjin walsimu hin jiru';

  @override
  String get noListingsMatchFiltersHint =>
      'Midhaan, qonnaan bulaa ykn bakka biraa yaali, ykn calleessitoota haqi';

  @override
  String get clearFilters => 'Calleessitoota haqi';

  @override
  String get trackTradesEscrow => 'Daldalaa fi eeskiroo hordofi';

  @override
  String get active => 'Hojjatamaa';

  @override
  String get done => 'Xumurameera';

  @override
  String get all => 'Hundaa';

  @override
  String get noOrders => 'Ajajni hin jiru';

  @override
  String get ordersWillAppear => 'Daldalaan kee as ni mul\'ata';

  @override
  String get trades => 'Daldalaa';

  @override
  String get rating => 'Sadarkaa';

  @override
  String get inEscrowEtb => 'Eeskiroo Keessaa (ETB)';

  @override
  String get wallet => 'Waaleetii';

  @override
  String get escrowHistory => 'Seenaa Eeskiroo';

  @override
  String get ratings => 'Sadarkaalee';

  @override
  String get documents => 'Sanadoota';

  @override
  String get idAndCerts => 'Eenyummaa, Ragaalee';

  @override
  String get languageAndAlerts => 'Afaan, Beeksisa';

  @override
  String get help => 'Gargaarsa';

  @override
  String get faqAndSupport => 'Gaaffilee, Deeggarsa';

  @override
  String get continueButton => 'Itti fufi';

  @override
  String get selectRoleDescription =>
      'WeredaConnect akkamitti fayyadamtu filadhu';

  @override
  String get farmerDescription =>
      'Midhaan gabaa\'f tarreessi konkolaachiftoota argadhu';

  @override
  String get truckerDescription => 'Geejjiba dhiyeessi meeshaalee geejjibsi';

  @override
  String get buyerDescription =>
      'Qonnaan bultoota irraa midhaan ilaali bitadhu';

  @override
  String get faydaIdVerification => 'Mirkaneessa Eenyummaa Faayidaa';

  @override
  String get faydaIdInstructions =>
      'Kaardii eenyummaa Faayidaa kee iskaani ykn lakkoofsa galchi';

  @override
  String get scanFront => 'Fuuldura Iskaani';

  @override
  String get scanBack => 'Duuba Iskaani';

  @override
  String get enterManually => 'Lakkoofsa eenyummaa galchi';

  @override
  String get faydaIdNumber => 'Lakkoofsa Eenyummaa Faayidaa';

  @override
  String get drivingLicense => 'Hayyama Konkolaachisaa';

  @override
  String get drivingLicenseInstructions =>
      'Eenyummaa kee mirkaneessuuf hayyama konkolaachisaa iskaani';

  @override
  String get drivingLicenseRequired =>
      'Akka konkolaachisaatti galmaa\'uuf suuraan hayyama konkolaachisaa barbaachisaadha.';

  @override
  String get buyerType => 'Gosa Bitaa';

  @override
  String get buyerTypeDescription =>
      'Akka dhuunfaa ykn dhaabbataatti ni bitattu?';

  @override
  String get privateBuyer => 'Bitaa Dhuunfaa';

  @override
  String get companyBuyer => 'Dhaabbata';

  @override
  String get companyName => 'Maqaa Dhaabbataa';

  @override
  String get companyNameRequired =>
      'Itti fufuuf maqaa dhaabbata keessanii galchaa.';

  @override
  String get personalInfo => 'Odeeffannoo Dhuunfaa';

  @override
  String get personalInfoDescription => 'Herrega uumuuf odeeffannoo kee galchi';

  @override
  String get verified => 'Mirkanaa\'e';

  @override
  String get scanLicense => 'Hayyama Iskaani';

  @override
  String get statusFunded => 'Maallaqni Galee';

  @override
  String get statusPlaced => 'Ajajamame';

  @override
  String get statusAssigned => 'Ramadame';

  @override
  String get statusTransit => 'Daandii Irra';

  @override
  String get statusDisputed => 'Falmiirra Jira';

  @override
  String deliveryTitle(int id) {
    return 'Geejjiba #$id';
  }

  @override
  String paymentTitle(int id) {
    return 'Kaffaltii #$id';
  }

  @override
  String listingCreatedActivity(String cropType) {
    return '$cropType tarreeffamni uumame';
  }

  @override
  String deliveryStatusActivity(String status) {
    return 'Geejjiba $status';
  }

  @override
  String minutesAgo(int count) {
    return 'Daqiiqaa $count dura';
  }

  @override
  String hoursAgo(int count) {
    return 'Sa\'aatii $count dura';
  }

  @override
  String daysAgo(int count) {
    return 'Guyyaa $count dura';
  }

  @override
  String get fieldRequired => 'Dirreen kun dirqama';

  @override
  String get back => 'Deebi\'i';

  @override
  String get clearAll => 'Hunda haqi';

  @override
  String get myDeliveries => 'Geejjiba Koo';

  @override
  String get myPayments => 'Kaffaltii Koo';

  @override
  String get trackDeliveries => 'Geejjiba midhaanii kee hordofi';

  @override
  String get trackPayments => 'Galii fi eesciroo kee hordofi';

  @override
  String get trackBuyerPayments => 'Kaffaltii fi eesciroo kee hordofi';

  @override
  String get noDeliveries => 'Hanga ammaatti geejjibni hin jiru';

  @override
  String get noPayments => 'Hanga ammaatti kaffaltiin hin jiru';

  @override
  String get postTrip => 'Imala Galmeessi';

  @override
  String get browseMarket => 'Ilaali';

  @override
  String get yourCropListings => 'Tarree midhaanii kee';

  @override
  String get payments => 'Kaffaltiilee';

  @override
  String get trackDelivery => 'Geejjiba Hordofi';

  @override
  String get checkBalance => 'Herrega Ilaali';

  @override
  String get manageOrders => 'Ajaja dhufu bulchi';

  @override
  String get trackYourOrders => 'Ajaja kee hordofi';

  @override
  String get orderPending => 'Eeggachaa';

  @override
  String get orderAccepted => 'Fudhatame';

  @override
  String get orderRejected => 'Didame';

  @override
  String get acceptOrder => 'Fudhuu';

  @override
  String get rejectOrder => 'Diduu';

  @override
  String get splashHeadline =>
      'Oomishtuu. Geejjibsiistuu.\nBituu. Xumurameera.';

  @override
  String get splashSubtitle =>
      'Waltajjii tokko qonnaan bultootaa, konkolaachiftootaa fi bitootaaf — eesciroo nageenya qabuun.';

  @override
  String get orderFunded => 'Kaffaltii';

  @override
  String get orderCancelled => 'Haqame';

  @override
  String get orderCompleted => 'Xumurameera';

  @override
  String get faydaVerifyTitle => 'Eenyummaa Faayidaan Mirkaneessuu';

  @override
  String get faydaVerifyDescription =>
      'Eenyummaa keessan waraqaa eenyummaa Faayidaa dijitaalaa fayyadamuun mirkaneessaa. Gara portaalii Faayidaatti ni geeffamtu.';

  @override
  String get verifyWithFayda => 'Faayidaan Mirkaneessuu';

  @override
  String get faydaVerifying => 'Faayidaan mirkaneessaa jira...';

  @override
  String get faydaVerifiedSuccess => 'Eenyummaan Mirkanaa\'eera';

  @override
  String get faydaVerificationFailed =>
      'Mirkaneessuu Faayidaa hin milkoofne. Irra deebi\'aatii yaalaa.';

  @override
  String get faydaRequired =>
      'Herrega uumuuf eenyummaan Faayidaa mirkanaa\'e barbaachisaadha. Daqiiqaa tokko qofa fudhata.';

  @override
  String get faydaUnavailable =>
      'Tajaajilli Faayidaa yeroo ammaa hin argamu. Booda irra deebi\'aatii yaalaa.';

  @override
  String get faydaAlreadyLinked =>
      'Waraqaan eenyummaa Faayidaa kun herrega biraatti walqabatee jira.';

  @override
  String get placeOrder => 'Ajaja Galmeessi';

  @override
  String get viewDetails => 'Bal\'ina Ilaali';

  @override
  String get cropCost => 'Gatii Midhaanii';

  @override
  String get transportCost => 'Baasii Geejjibaa';

  @override
  String get platformCommission => 'Komishiinii Waltajjii (5%)';

  @override
  String get orderTotal => 'Waliigala';

  @override
  String get payNow => 'Amma Kaffalii';

  @override
  String get payLater => 'Booda Kaffalii';

  @override
  String get cbePaymentInstructions =>
      'Gara herrega Baankii Daldalaa Itoophiyaa armaan gadiitti dabarsi. Kaffaltii kee walsimsiisuuf lakkoofsa wabii fayyadami.';

  @override
  String get enterTransactionId => 'Lakkoofsa Daldala CBE Galchi';

  @override
  String get submitPayment => 'Kaffaltii Galmeessi';

  @override
  String get statusCreated => 'Uumame';

  @override
  String get statusPendingPayment => 'Kaffaltii Eeggataa';

  @override
  String get statusPaymentSubmitted => 'Kaffaltiin Galmeeffame';

  @override
  String get statusPickupConfirmed => 'Fudhannaan Mirkanaa\'e';

  @override
  String get statusInTransit => 'Geejjibarra Jira';

  @override
  String get statusDelivered => 'Ga\'eera';

  @override
  String get statusDisbursing => 'Raabsamaa Jira';

  @override
  String get statusSettled => 'Xumurameera';

  @override
  String get statusPaymentFailed => 'Kaffaltiin Hin Milkoofne';

  @override
  String get statusDisputeResolved => 'Falmiin Furameera';

  @override
  String get statusCreatedInfo =>
      'Bitataan ajaja kana galcheera. Hanga ammaatti maallaqni hin kaffalamne.';

  @override
  String get statusPendingPaymentInfo =>
      'Bitataan kaffaltii akka jalqabu eegamaa jira. Ajajni hin kaffalamin sa\'aatii 24 booda ofumaan haqama.';

  @override
  String get statusPaymentSubmittedInfo =>
      'Bitataan kaffaltii ergeera; mirkanaa\'aa jira. Maallaqni ammallee eskiroo keessatti hin mirkanoofne.';

  @override
  String get statusFundedInfo =>
      'Kaffaltiin mirkanaa\'ee eskiroo keessatti nagaan qabameera. Hangi ajajame tarree qonnaan bulaa irraa qabameera.';

  @override
  String get orderAcceptedInfo =>
      'Qonnaan bulaan ajaja fudhatee midhaan fuudhamuuf qopheessa.';

  @override
  String get statusPickupConfirmedInfo =>
      'Qonnaan bulaa irraa fuudhuun iskaanii koodii QR tiin mirkanaa\'eera.';

  @override
  String get statusInTransitInfo => 'Midhaan gara bitataatti imalaa jira.';

  @override
  String get statusDeliveredInfo =>
      'Ga\'umsi bitataadhaan mirkanaa\'eera. Eskiroon kaffaltii qonnaan bulaaf gadhiisa.';

  @override
  String get statusDisbursingInfo =>
      'Maallaqni eskiroo irraa ba\'aa jira: qonnaan bulaa fi, yoo jiraate, konkolaataa fe\'aatiif kaffalamaa jira.';

  @override
  String get statusSettledInfo =>
      'Hundi kaffalameera: qonnaan bulaa fi, yoo jiraate, konkolaataa fe\'aa. Ajajni xumurameera.';

  @override
  String get orderCancelledInfo =>
      'Ajajni haqameera. Maallaqni eskiroo keessatti qabame yoo jiraate bitataaf deebi\'a.';

  @override
  String get orderRejectedInfo =>
      'Qonnaan bulaan ajaja kana hin fudhanne. Maallaqni eskiroo keessatti qabame bitataaf deebi\'a.';

  @override
  String get statusPaymentFailedInfo =>
      'Kaffaltiin mirkaneessuun hin dandeenye. Bitataan irra deebi\'ee kaffaluu ni danda\'a.';

  @override
  String get statusDisputedInfo =>
      'Falmiin dhiyaateera. Hanga falmiin furamutti maallaqni eskiroo keessatti qabamee tura.';

  @override
  String get statusDisputeResolvedInfo =>
      'Falmiin furameera; maallaqni gara garee mo\'ateetti darbeera.';

  @override
  String get cancelOrder => 'Ajaja Haquu';

  @override
  String get cancelReason => 'Sababa haquuf';

  @override
  String get orderDetails => 'Ibsa Ajajaa';

  @override
  String get priceBreakdown => 'Caccabsaa Gatii';

  @override
  String get cbeAccountNumber => 'Lakk. Herrega CBE';

  @override
  String get cbeAccountValue => '1000XXXXXXXXXX';

  @override
  String get transferReference => 'Wabii';

  @override
  String get paymentAmount => 'Hamma';

  @override
  String get quantity => 'Hamma (kg)';

  @override
  String get listingSummary => 'Cuunfaa Tarree';

  @override
  String get orderPlacedSuccess => 'Ajajni milkaa\'inaan galmeeffame!';

  @override
  String get paymentSubmittedSuccess => 'Kaffaltiin milkaa\'inaan galmeeffame!';

  @override
  String get orderCancelledSuccess => 'Ajajni haqameera.';

  @override
  String get bankName => 'Baankii Daldalaa Itoophiyaa';

  @override
  String get paymentInstructions => 'Qajeelfama Kaffaltii';

  @override
  String get transactionIdRequired => 'Lakkoofsi daldala barbaachisaadha';

  @override
  String get orderTimeline => 'Sarara Yeroo Ajajaa';

  @override
  String etbCurrency(String amount) {
    return 'Birrii $amount';
  }

  @override
  String quantityKg(String amount) {
    return '$amount kg';
  }

  @override
  String get pendingOrders => 'Ajaja Eeggataa';

  @override
  String get activeOrders => 'Ajaja Hojii Irra Jiran';

  @override
  String get listingNotFound => 'Tarreen hin argamne';

  @override
  String get unauthorizedAccess => 'Ajaja kana ilaaluuf hayyamni hin jiru.';

  @override
  String get whatDoYouNeed => 'Maal barbaaddu?';

  @override
  String get whatDoYouNeedSubtitle =>
      'Waan barbaaddan nutti himaa waan walsimatu isiniif barbaanna';

  @override
  String get findMatches => 'Walsimatu Barbaadi';

  @override
  String get quantityNeeded => 'Hamma barbaadamu (kuuntaalii)';

  @override
  String get maxPrice => 'Gatii ol\'aanaa (Birrii/kg)';

  @override
  String get deliverTo => 'Bakka geejjibaa';

  @override
  String matchesFound(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count walsimatu argame',
      one: '1 walsimatu argame',
      zero: 'Walsimatu hin argamne',
    );
    return '$_temp0';
  }

  @override
  String get noMatchesYet => 'Hanga ammaatti walsimatu hin jiru';

  @override
  String newMatchesCount(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count haaraa',
      one: '1 haaraa',
    );
    return '$_temp0';
  }

  @override
  String get suggestedTransport => 'Geejjiba Yaadame';

  @override
  String get selectThisTrip => 'Imala Kana Fili';

  @override
  String get estimatedTotal => 'Tilmaama Waliigalaa';

  @override
  String tripsAvailable(int count) {
    return '$count imala ni jira';
  }

  @override
  String get noTripsYet => 'Hanga ammaatti imala hin jiru';

  @override
  String get placeOrderWithTransport => 'Ajaja + Geejjiba';

  @override
  String get placeOrderNoTransport => 'Ajaja (geejjiba malee)';

  @override
  String get activeBuyingIntents => 'Fedhii Bittaa Hojii Irra Jiru';

  @override
  String get newBuyingIntent => 'Fedhii Bittaa Haaraa';

  @override
  String get cropCostLabel => 'Gatii Midhaan';

  @override
  String get transportCostLabel => 'Gatii Geejjibaa';

  @override
  String get commissionLabel => 'Komishinii (5%)';

  @override
  String syncedAgo(String time) {
    return '$time dura walsimame';
  }

  @override
  String get browseAllListings => 'Tarree Hunda Ilaali';

  @override
  String get browseAllTrips => 'Imala Hunda Ilaali';

  @override
  String get intentSaved =>
      'Kuufame! Yoo walsimatu haaraan jiraate isin beeksifna';

  @override
  String get intentSaveHint =>
      'Gaaffii kana olkaa\'uun yommuu walsimannaan haaraan dhufu si beeksifna';

  @override
  String get selectCropTypeError => 'Gosa midhaanii filadhu';

  @override
  String get enterQuantityError => 'Hamma barbaaddu galchi';

  @override
  String get enterValidPriceError =>
      'Gatii sirrii galchi yookiin duwwaa dhiisi';

  @override
  String get enterDeliveryWoredaError => 'Aanaa itti geeffamu galchi';

  @override
  String get maxPriceOptionalHint =>
      'Filannoo — gatii hunda arguuf duwwaa dhiisi';

  @override
  String get anyPrice => 'Gatii kamiyyuu';

  @override
  String get similarCrop => 'Midhaan walfakkaatu';

  @override
  String get withinBudget => 'Baajata keessa';

  @override
  String get slightlyOverBudget => 'Baajataa xiqqoo ol';

  @override
  String get coversFullQuantity => 'Hamma guutuu ni uwwisa';

  @override
  String get partialQuantity => 'Hamma gartokkee';

  @override
  String get nearYou => 'Sitti dhiyoo';

  @override
  String get deleteRequest => 'Gaaffii Haqi';

  @override
  String get deleteRequestConfirm =>
      'Gaaffii bittaa kana haquu? Beeksisa walsimannaa isaaf hin argattu.';

  @override
  String get requestDeleted => 'Gaaffiin haqameera';

  @override
  String get markFulfilled => 'Akka guutametti mallatteessi';

  @override
  String get requestFulfilled => 'Gaaffiin akka guutametti mallatteeffameera';

  @override
  String quintalEquivalent(String count) {
    return '= kuntaala $count';
  }

  @override
  String orderQuantityExceedsStock(String available) {
    return 'Kan jiru kg $available qofa. Baay\'ina xiqqaa galchi.';
  }

  @override
  String get neededByDate => 'Guyyaa barbaachisu (filannoo)';

  @override
  String get neededByDateHint =>
      'Geejjiba yeroodhaan geessuu danda\'u ni filanna';

  @override
  String get selectDate => 'Guyyaa filadhu';

  @override
  String get clearDate => 'Haqi';

  @override
  String get newMatchesAvailable => 'Walsimatu haaraa argameera!';

  @override
  String get findCrops => 'Midhaan Barbaadi';

  @override
  String get findTrips => 'Imala Barbaadi';

  @override
  String get totalLabel => 'Waliigala';

  @override
  String get startTrip => 'Imala Jalqabii';

  @override
  String get startTripConfirmation =>
      'Imala kana jalqabdaa? Fe\'annoo fudhachuu mirkaneessita.';

  @override
  String get tripStartedSuccess => 'Imalaan jalqabame! Nagaan oofii.';

  @override
  String get markDelivered => 'Geessuu Mirkaneessi';

  @override
  String get markDeliveredConfirmation =>
      'Akka geeffameetti mallatteessi? Meeshaaleen buufamuu mirkaneessi.';

  @override
  String get tripDeliveredSuccess => 'Geessuu mirkanaa\'e!';

  @override
  String get waitingForMatch => 'Walsimatu eegamaa jira...';

  @override
  String headingTo(String destination) {
    return 'Gara $destination deemaa jira';
  }

  @override
  String get postNextTrip => 'Imala Itti Aanuu Maxxansi';

  @override
  String get recentRoutes => 'Karaalee Dhiyoo';

  @override
  String get noActiveTrip => 'Imalli hojjatamaa jiru hin jiru';

  @override
  String get postATrip => 'Imala Maxxansi';

  @override
  String get repeatLastTrip => 'Imala Darbe Irra Deebi\'i';

  @override
  String get quickFill => 'Guuttaa Ariifataa';

  @override
  String get somethingWentWrong => 'Wanti tokko dogoggora ta\'e';

  @override
  String get checkConnectionRetry =>
      'Maaloo walqunnamtii keessan mirkaneessaa irra deebi\'aa yaalaatii.';

  @override
  String get sellCrops => 'Midhaan Gurguri';

  @override
  String get sellYourHarvest => 'OOMISHA KEESSAN GURGURAA';

  @override
  String get tapToListCrops =>
      'Midhaan keessan galmeessaa bittoota kallattiin argadhaa';

  @override
  String get synced => 'Walsimame';

  @override
  String get offline => 'Oflaayinii';

  @override
  String syncFailureCount(int count) {
    return '$count hojiiwwan xiyyeeffannoo barbaadu';
  }

  @override
  String syncPendingCount(int count) {
    return '$count walsimsisuuf eeggataa jiru';
  }

  @override
  String paymentStatusTitle(String status) {
    return 'Kaffaltii $status';
  }

  @override
  String get myCrops => 'Midhaan Koo';

  @override
  String get browseAll => 'Barbaadi';

  @override
  String get myRequests => 'Gaaffii Koo';

  @override
  String get enterPhoneForReset =>
      'Koodii mirkaneessaa argachuuf lakkoofsa bilbilaa keessan galchaa';

  @override
  String get sendResetCode => 'Koodii Jijjiirraa Ergi';

  @override
  String get resetCodeSent =>
      'Koodiin mirkaneessaa gara bilbila keessaniitti ergameera';

  @override
  String get enterResetCode =>
      'Koodii mirkaneessaa gara bilbila keessaniitti ergame galchaa';

  @override
  String get verificationCode => 'Koodii Mirkaneessaa';

  @override
  String get newPassword => 'Jecha Darbii Haaraa';

  @override
  String get confirmNewPassword => 'Jecha Darbii Haaraa Mirkaneessi';

  @override
  String get resetPassword => 'Jecha Darbii Jijjiiri';

  @override
  String get passwordResetSuccess =>
      'Jecha darbii milkaa\'inaan jijjiirameera! Amma seenuu dandeessu.';

  @override
  String get codeRequired => 'Koodiin mirkaneessaa barbaachisaadha';

  @override
  String get passwordTooShort => 'Jecha darbii xiqqaatu qubee 8 qabaachuu qaba';

  @override
  String get placeSearchHint => 'Aanaa ykn magaalaa barbaadi';

  @override
  String get placeNoResults =>
      'Wal-fakkaataan hin jirne — waan barreessite fayyadamuu dandeessa';

  @override
  String get placeResultsLabel => 'Bu\'aawwan iddoo';

  @override
  String get messages => 'Ergaawwan';

  @override
  String get noConversationsYet => 'Hanga ammaatti ergaan hin jiru';

  @override
  String get noConversationsHint =>
      'Marii qonnaan bultoota, konkolaachiftootaa fi bittootaa wajjin gootan asitti mul\'ata.';

  @override
  String get noMessagesInThread =>
      'Hanga ammaatti ergaan hin jiru. Gaditti marii jalqabi.';

  @override
  String get typeAMessage => 'Ergaa barreessi…';

  @override
  String get send => 'Ergi';

  @override
  String get sendMessage => 'Ergaa ergi';

  @override
  String get messageContact => 'Ergaa';

  @override
  String get messageFarmer => 'Qonnaan bulaaf ergaa ergi';

  @override
  String get messageTrucker => 'Konkolaachisaaf ergaa ergi';

  @override
  String get messageBuyer => 'Bittaaf ergaa ergi';

  @override
  String get contact => 'Quunnamtii';

  @override
  String get contactAfterEscrowNote =>
      'Quunnamtiin kan banamu erga kaffaltiin Eeskiroo keessatti qabamee booda.';

  @override
  String get contactAfterAcceptNote =>
      'Quunnamtiin kan banamu erga qonnaan bulaan ajaja fudhatee booda.';

  @override
  String get contactBuyer => 'Bitaa Quunnamuu';

  @override
  String get contactTrucker => 'Konkolaachisaa Quunnamuu';

  @override
  String get contactClosedNote => 'Quunnamtiin haasaa kanaaf cufameera.';

  @override
  String get newMessage => 'Ergaa haaraa';

  @override
  String newMessagesCount(int count) {
    return 'Ergaawwan haaraa $count';
  }

  @override
  String get messageContextCrop => 'Midhaan';

  @override
  String get messageContextTrip => 'Imala';

  @override
  String get messageContextOrder => 'Ajaja';

  @override
  String get messageContextDelivery => 'Geejjiba';

  @override
  String get contextUnavailable => 'Bal\'inaaleen amma hin argaman';

  @override
  String get stepUpRequiredMessage =>
      'Nageenya keessaniif, kaffaltii kana dura isin ta\'uu keessan mirkaneessaa.';

  @override
  String get kycRequiredFaydaMessage =>
      'Kaffaltiin qarshii 10,000 ol ta\'e ID Faayidaa mirkanaa\'e barbaada. Faayidaan mirkaneessaa, kaffaltiin keessan battaluma sanatti ni ergama.';

  @override
  String get kycRequiredPhoneMessage =>
      'Lakkoofsi bilbila keessanii ammallee hin mirkanoofne, kanaaf kaffaltiin kun hin ergamne. Faayidaan mirkaneessaa, kaffaltiin keessan battaluma sanatti ni ergama.';

  @override
  String get kycRequiredTitle => 'Mirkaneessuun barbaachisa';

  @override
  String authRateLimitRetryIn(int seconds) {
    return 'Yaaliin baay\'ee ta\'eera. Sekondii $seconds booda irra deebi\'aa yaalaa.';
  }

  @override
  String get otpInvalidOrExpired =>
      'Koodiin kun dogoggora ykn yeroon isaa darbeera. SMS ilaalaa ykn koodii haaraa gaafadhaa.';

  @override
  String get otpVerificationTitle => 'Bilbila keessan mirkaneessaa';

  @override
  String otpSentTo(String phone) {
    return 'Koodii lakkoofsa 6 qabu SMS\'n gara $phone ergineerra.';
  }

  @override
  String get otpCodeHint => 'Koodii lakkoofsa 6';

  @override
  String otpExpiresAt(String time) {
    return 'Koodiin sa\'aatii $time irratti dhumata.';
  }

  @override
  String get faydaStepUpMessage =>
      'Kaffaltii kana itti fufuuf isin ta\'uu keessan Faayidaadhaan mirkaneessaa. Jecha iccitii keessanis gaafatamuu dandeessu.';

  @override
  String get stepUpTitle => 'Isin ta\'uu keessan mirkaneessaa';

  @override
  String stepUpPhoneLabel(String phone) {
    return 'Bilbila: $phone';
  }

  @override
  String get stepUpConfirm => 'Mirkaneessi';

  @override
  String get stepUpOfflineError =>
      'Mirkaneessuuf interneetii barbaaddu. Walqunnamaa irra deebi\'aa yaalaa.';

  @override
  String get stepUpDone =>
      'Mirkanaa\'eera. Kaffaltii keessan irra deebi\'amaa jira.';

  @override
  String get faydaStateMismatch =>
      'Yeroon Faayidaa darbeera. Maaloo mirkaneessa irra deebi\'aa jalqabaa.';

  @override
  String get faydaStillWaiting =>
      'Ammallee Faayidaa eeggachaa jira. Brawuzara keessatti xumuraa, achii as deebifamtu.';

  @override
  String get faydaLinkSuccess =>
      'ID Faayidaa keessan amma herrega keessan waliin walqabateera.';

  @override
  String get faydaNotVerifiedSubtitle =>
      'Kaffaltii qarshii 10,000 ol ta\'eef barbaachisa';

  @override
  String get appLockTitle => 'Cufaa appii';

  @override
  String get appLockSubtitle =>
      'Yeroo appii bantan quba ykn PIN bilbilaa gaafadhu';

  @override
  String get appLockLockedTitle => 'WeredaConnect cufameera';

  @override
  String get appLockUnlock => 'Bani';

  @override
  String get appLockReason => 'WeredaConnect bani';

  @override
  String get qrConfirmAuthReason =>
      'Geejjiba kana fudhachuuf isin ta\'uu keessan mirkaneessaa';

  @override
  String get qrConfirmAuthFailed =>
      'Isin ta\'uu keessan mirkaneessuun hin danda\'amne. Geejjibni hin mirkanoofne.';

  @override
  String get faydaRegistrationUnavailable =>
      'Yeroo hedduu yaallus Faayidaa argachuu hin dandeenye. Walqunnamtii interneetii keessanii ilaalaatii daqiiqaa muraasa booda irra deebi\'aa yaalaa.';

  @override
  String get faydaUnavailableOnWeb =>
      'Mirkaneessi Faayidaa ammaaf baraabsaa (browser) irratti hin argamu. Maaloo appii moobaayilaa WeredaConnect fayyadamaa.';

  @override
  String get faydaCouldNotOpen =>
      'Fuula Faayidaa banuu hin dandeenye. Maaloo irra deebi\'aa yaalaa.';

  @override
  String get faydaRequiredToRegister =>
      'Mirkaneessi Faayidaa keessan hin jiru ykn yeroon isaa darbeera. Herrega keessan xumuruuf maaloo Faayidaan irra deebi\'aa mirkaneessaa.';

  @override
  String faydaRetrying(int attempt, int total) {
    return 'Ammas Faayidaa argachuuf yaalaa jirra ($attempt/$total)...';
  }

  @override
  String get kycFarmerPhoneMessage =>
      'Lakkoofsi bilbila qonnaan bulaa ammallee hin mirkanoofne, kanaaf kaffaltiin kun hin ergamne. Erga qonnaan bulaan mirkaneessee booda kaffaluu dandeessu.';

  @override
  String get kycFarmerFaydaMessage =>
      'Kaffaltiin qarshii 10,000 ol ta\'e qonnaan bulaanis ID Faayidaa mirkanaa\'e akka qabaatu barbaada, kanaaf kaffaltiin kun hin ergamne. Erga qonnaan bulaan mirkaneessee booda kaffaluu dandeessu.';

  @override
  String get kycFarmerNotVerifiedMessage =>
      'Eenyummaan qonnaan bulaa ammallee hin mirkanoofne, kanaaf kaffaltiin kun hin ergamne. Erga qonnaan bulaan mirkaneessee booda kaffaluu dandeessu.';

  @override
  String get kycUnavailableMessage =>
      'Amma mirkaneessa eenyummaa ilaaluu hin dandeenye, kanaaf kaffaltiin kun hin ergamne. Maaloo booda irra deebi\'aa yaalaa.';

  @override
  String get kycPaymentNotSent =>
      'Kaffaltiin hin ergamne. Eenyummaan keessan hin mirkanoofne.';

  @override
  String get kycPaymentResubmitted =>
      'Eenyummaan keessan mirkanaa\'eera. Kaffaltiin keessan irra deebi\'ee ni ergama.';

  @override
  String get kycVerifiedPayAgain =>
      'Eenyummaan keessan mirkanaa\'eera. Ajaja banaa kaffaltii irra deebi\'aa galchaa.';

  @override
  String get faydaPaymentPrompt =>
      'Isin ta\'uu keessan Faayidaan mirkaneessaa. ID Faayidaa herrega kanaa fayyadamaa, kaffaltiin keessan battaluma sanatti itti fufa.';

  @override
  String get faydaLinkPrompt =>
      'ID Faayidaa keessan herrega kana waliin walqabsiisaa. ID Faayidaa keessan fayyadamaa: lakkoofsa bilbila herrega kanaa waliin wal simuu qaba.';

  @override
  String get faydaPaymentContinues =>
      'Eenyummaan keessan mirkanaa\'eera. Kaffaltii keessan itti fufaa jirra...';

  @override
  String get faydaIdMismatch =>
      'ID Faayidaa kun kan herrega keessan irra jiru waliin wal hin simu. ID Faayidaa keessaniin mirkaneessaa.';

  @override
  String get faydaPhoneMismatch =>
      'Lakkoofsi bilbila ID Faayidaa kana irra jiru herrega keessan waliin wal hin simu. ID Faayidaa keessaniin mirkaneessaa.';

  @override
  String get faydaIdentityUnconfirmed =>
      'ID Faayidaa kun kan herrega keessanii ta\'uu isaa mirkaneessuu hin dandeenye. ID Faayidaa lakkoofsa bilbila keessaniin galmaa\'een mirkaneessaa.';

  @override
  String get kycPhoneNotProven =>
      'ID Faayidaa keessan mirkanaa\'eera, garuu Faayidaan lakkoofsa bilbila herrega kanaa hin qabu, kanaaf bilbilli keessan ammallee hin mirkanoofne. Kaffaltiin hin ergamne.';

  @override
  String get payWithTelebirr => 'Telebirr\'n kaffalaa';

  @override
  String telebirrLivePayInstructions(String amount) {
    return '$amount Telebirr\'n ni kaffaltu. Yeroo xumurtan gara appii kanaatti deebi\'aa. Kaffaltiin ofumaan ni mirkanaa\'a.';
  }

  @override
  String get orderCheckoutTitle => 'Kaffaltii Telebirr';

  @override
  String get orderCheckoutWaitingToSend =>
      'Kaffaltiin keessan ergamuuf eegaa jira. Yeroo sarvarii bira gahu Telebirr ni banama.';

  @override
  String orderCheckoutOpenUntil(String time) {
    return 'Kaffaltii kana Telebirr keessatti hanga $time tti xumuruu dandeessu.';
  }

  @override
  String get orderCheckoutWindowClosed =>
      'Fuulli kaffaltii Telebirr kun cufameera. Kaffaluuf haaraa jalqabaa.';

  @override
  String get orderCheckoutStartNew => 'Kaffaltii Telebirr haaraa jalqabaa';

  @override
  String get orderCheckoutCheckAgain => 'Haala kaffaltii ilaalaa';

  @override
  String get orderCheckoutStillWaiting =>
      'Hanga ammaatti kaffaltii hin arganne. Yoo kaffaltan, Haala kaffaltii ilaalaa tuqaa.';

  @override
  String get orderCheckoutTimeRanOut =>
      'Yeroon ajaja kanaaf kaffaluu darbeera. Ajajni kun akka hin milkoofnetti ni mallattaa\'a, achiis irra deebitanii kaffaluu dandeessu.';

  @override
  String get orderCheckoutNotWaiting =>
      'Ajajni kun kana booda kaffaltii hin eeggatu.';

  @override
  String get orderCheckoutNotPayableOnline =>
      'Ajajni kun toora interneetiin kaffalamuu hin danda\'u. Deeggarsa qunnamaa.';

  @override
  String get orderCheckoutConfirmedAutomatically =>
      'Asitti kaffaltiin ofumaan mirkanaa\'a. Ajaja keessan ilaalaa jirra...';

  @override
  String get orderCheckoutAlreadyOpen =>
      'Kaffaltiin Telebirr ajaja kanaa qophaa\'aa jira ykn addaan citeera. Yoo duraan kaffaltan, asitti ni mirkanaa\'a. Yoo hin taane, booda irra deebi\'aa yaalaa (hanga sa\'aatii 2 fudhachuu danda\'a).';

  @override
  String get orderCheckoutGatewayProblem =>
      'Telebirr kaffaltii jalqabuu hin dandeenye. Yoo duraan kaffaltan, asitti ni mirkanaa\'a. Yoo hin taane, irra deebi\'aa yaalaa.';

  @override
  String get orderCheckoutOrderMissing =>
      'Ajajni kun ammallee sarvarii irra hin jiru. Erga walsimee booda irra deebi\'aa yaalaa.';

  @override
  String get orderCheckoutPaymentFailed =>
      'Kaffaltiin kun yeroon mirkanaa\'uu hin dandeenye. Irra deebitanii kaffaluu dandeessu.';

  @override
  String get orderCheckoutNotSent =>
      'Kaffaltiin keessan hin ergamne. Yoo eenyummaa keessan akka mirkaneessitan gaafatamtan, erga mirkaneessitanii booda irra deebi\'ee ni ergama; yoo hin taane, irra deebitanii kaffaluuf ajaja banaa.';

  @override
  String get orderCheckoutCheckingPayment =>
      'Kaffaltii keessan ilaalaa jirra...';

  @override
  String get paymentTotalOutOfRange =>
      'Telebirr kaffaltii tokkoon qarshii 100 hanga 500,000 fudhata. Waliigalli ajaja kanaa kanaan ala waan ta\'eef, Telebirr\'n kaffalamuu hin danda\'u.';

  @override
  String get paymentMethodUnavailable =>
      'Malli kaffaltii kun ammaaf hin jiru. Telebirr\'n kaffalaa.';

  @override
  String get continuePayment => 'Kaffaltii itti fufaa';

  @override
  String get payAgain => 'Irra deebi\'aa kaffalaa';

  @override
  String get orderPickupDeadline => 'Guyyaa dhumaa fudhannaa';

  @override
  String get orderTransportFailedBuyerHint =>
      'Midhaan yeroodhaan hin fudhatamne. Ajaja kana haqxanii maallaqa kaffaltan hunda deebifachuu dandeessu.';

  @override
  String get orderTransportFailedPartyHint =>
      'Midhaan yeroodhaan hin fudhatamne. Bitataan amma ajaja kana haquu danda\'a.';

  @override
  String get cancelOrderTransportFailedBody =>
      'Kan kaffaltan hundi, baasii geejjibaa fi komishinii waltajjii dabalatee, isinii deebi\'a.';

  @override
  String get errorOrderCancelNotAllowed =>
      'Ajajni kun amma haqamuu hin danda\'u. Kan haqamu midhaan yeroodhaan yoo hin fudhatamin fi wanti tokkollee yoo hin fudhatamin qofa.';

  @override
  String get errorDeadlinePassed =>
      'Yeroon darbeera. Yeroon tarkaanfii kanaa xumurameera.';

  @override
  String get errorTripNotEligible =>
      'Konkolaataan fe\'aa sun amma hin argamu. Irra deebi\'aa filadhaa.';

  @override
  String get errorTransportAttemptsExceeded =>
      'Ajaja kanaaf konkolaataan fe\'aa biraa yaalamuu hin danda\'u. Midhaan ofiin fudhadhaa, yookiin ajaja haqxanii maallaqa kaffaltan hunda deebifadhaa.';

  @override
  String get errorTripCapacityExceeded =>
      'Konkolaataan fe\'aa kun ajaja kanaaf iddoo hin qabu. Konkolaataa fe\'aa biraa filadhaa.';

  @override
  String get errorReputationRestricted =>
      'Herregni keessan yeroo ammaaf kana gochuu hin danda\'u, kanaaf jijjiiramni kun hin olkaa\'amne. Ajajni keessan kan amma jiran itti fufu.';

  @override
  String get errorCounterpartyRestricted =>
      'Qonnaan bultaan yookiin konkolaataan fe\'aa kun amma ajaja haaraa fudhachuu hin danda\'u, kanaaf ajajni hin galmoofne. Maaloo kan biraa filadhaa.';

  @override
  String get errorTripHasBookings =>
      'Imalli kun ajajoota qabame waan qabuuf, jijjiiramni kun hin danda\'amu.';

  @override
  String get errorCropHasActiveOrders =>
      'Tarreen kun ajajoota banaa qaba. Dura isaan xumuraa.';

  @override
  String get errorAppUpdateRequired =>
      'Itti fufuuf maaloo appii haaromsaa. Jijjiiramni kun hin olkaa\'amne.';

  @override
  String get errorAppUpdateRequiredKept =>
      'Maaloo appii haaromsaa. Jijjiiramni kun bilbila keessan irratti kuufameera, haaromsa booda ni ergama.';

  @override
  String get errorContactInText =>
      'Lakkoofsi bilbilaa asitti hin hayyamamu. Lakkoofsa kana haqi; quunnamtiin kan banamu erga kaffaltiin Eeskiroo keessatti qabamee booda.';

  @override
  String get authProfileContactInText =>
      'Lakkoofsa bilbilaa maqaa keessan, aanaa ykn maqaa dhaabbataa keessaa haqi. Quunnamtiin kan banamu erga kaffaltiin Eeskiroo keessatti qabamee booda.';

  @override
  String get deleteAccount => 'Herrega haqi';

  @override
  String get deleteAccountSubtitle =>
      'Herrega fi odeeffannoo dhuunfaa keessan haqaa';

  @override
  String get deleteAccountWarning =>
      'Herrega haquun yeroo hundaafi. Deebisuun hin danda\'amu.';

  @override
  String get deleteAccountDeletedHeading => 'Kan haqamu';

  @override
  String get deleteAccountDeletedProfile =>
      'Ibsa, maqaa fi lakkoofsa bilbilaa keessan';

  @override
  String get deleteAccountDeletedFayda =>
      'Walitti hidhamiinsa eenyummaa Faayidaa keessanii';

  @override
  String get deleteAccountDeletedListings =>
      'Tarreewwan midhaan, imala fi fedhii bittaa keessan';

  @override
  String get deleteAccountDeletedMessages => 'Ergaawwan keessan';

  @override
  String get deleteAccountDeletedNotifications =>
      'Beeksisa gara bilbila keessanii ergamu';

  @override
  String get deleteAccountKeptHeading => 'Kan kuufamu, maqaa keessan malee';

  @override
  String get deleteAccountKeptRecords =>
      'Galmee ajajaa, geejjibaa fi kaffaltii';

  @override
  String get deleteAccountKeptReason =>
      'Galmeen maallaqaa kuufamuu qaba. Maqaan keessan isaan irratti jiru Fayyadamaa haqame jedhamee bakka bu\'a.';

  @override
  String get deleteAccountNeedsInternet =>
      'Kanaaf interneetii barbaaddu. Jecha icciitii keessan akka galchitan isin gaafatamuu danda\'a.';

  @override
  String get deleteAccountButton => 'Herrega koo haqi';

  @override
  String get deleteAccountConfirmTitle => 'Herregni keessan haa haqamu?';

  @override
  String get deleteAccountConfirmBody =>
      'Kana deebisuun hin danda\'amu. Ibsi, tarreewwanii fi ergaawwan keessan ni haqamu.';

  @override
  String get deleteAccountDeleting => 'Herregni keessan haqamaa jira...';

  @override
  String get deleteAccountFinishing => 'Haquun xumuramaa jira...';

  @override
  String get deleteAccountDone => 'Herregni keessan haqameera.';

  @override
  String get deleteAccountOffline =>
      'Interneetii hin qabdan. Herrega keessan haquuf interneetiin walqunnamaa.';

  @override
  String get deleteAccountNetworkError =>
      'Sarvarii argachuu hin dandeenye, kanaaf herregni keessan haqamuu isaa beekuu hin dandeenyu. Walqunnamtii keessan mirkaneessaatii irra deebi\'aa yaalaa.';

  @override
  String get deleteAccountIncomplete =>
      'Haquun jalqabameera garuu ammallee hin xumuramne. Yeroo muraasa booda irra deebi\'aa yaalaa.';

  @override
  String get deleteAccountSuspended =>
      'Herregni kun dhorkameera, kanaaf asitti haqamuu hin danda\'u. Deeggarsa qunnamaa.';

  @override
  String get deleteAccountFailed =>
      'Herregni keessan haqamuu hin dandeenye. Booda irra deebi\'aa yaalaa.';

  @override
  String get deleteAccountOtherAccount =>
      'Asitti amma herregni biraa seeneera, kanaaf homtuu hin haqamne. Fuula irra deebi\'aa fe\'aa ykn appii irra deebi\'aa banaa, achiis irra deebi\'aa yaalaa.';

  @override
  String get deleteAccountBlockedTitle =>
      'Herrega keessan ammallee haquu hin dandeessan';

  @override
  String get deleteAccountBlockedIntro => 'Dura kanneen xumuraa ykn haqaa:';

  @override
  String deleteAccountBlockerOrder(int count) {
    return 'Ajajoota hojii irra jiran: $count';
  }

  @override
  String deleteAccountBlockerEscrow(int count) {
    return 'Kaffaltiiwwan Eeskiroo keessatti qabaman: $count';
  }

  @override
  String deleteAccountBlockerDelivery(int count) {
    return 'Geejjiboota hin xumuramne: $count';
  }

  @override
  String deleteAccountBlockerDispute(int count) {
    return 'Komiiwwan hin furamne: $count';
  }

  @override
  String deleteAccountBlockerPaymentReview(int count) {
    return 'Kaffaltiiwwan deebii eeggatan: $count. Deebii xumuruuf deeggarsa qunnamaa.';
  }

  @override
  String deleteAccountBlockerOther(int count) {
    return 'Wantoota biroo hojii irra jiran: $count';
  }

  @override
  String deleteAccountBlockersMore(int count) {
    return 'Fi $count dabalataa.';
  }

  @override
  String get deletedUser => 'Fayyadamaa haqame';

  @override
  String get statusAwaitingTransport => 'Geejjibni hin argamne';

  @override
  String get statusAwaitingTransportInfo =>
      'Konkolaataan fe\'aa ajaja kana fe\'uu hin danda\'u. Maallaqni nageenyaan qabamee tura. Bitataan haquun maallaqa guutuu deebifachuu danda\'a; yeroodhaan yoo hin murteessine ajajni ni haqama, maallaqni ni deebi\'a.';

  @override
  String get orderStatusUpdating => 'Haaromfamaa jira';

  @override
  String get orderStatusUpdatingInfo =>
      'Ajajni kun haala appiin kun ammaaf agarsiisuu hin dandeenyeen jijjiirameera. Maaloo appii haaromsaa.';

  @override
  String get commitmentTitle => 'Eenyutu waliigale';

  @override
  String get commitmentAccepted => 'Fudhateera';

  @override
  String commitmentWaitingUntil(String time) {
    return 'Eegamaa jira, deebiin hanga $time';
  }

  @override
  String get commitmentWaiting => 'Deebii eegaa jira';

  @override
  String get commitmentDeclined => 'Dideera';

  @override
  String get commitmentExpired => 'Yeroodhaan hin deebisne';

  @override
  String get commitmentNotAskedYet =>
      'Erga qonnaan bulaan fudhatee booda gaafatama';

  @override
  String get commitmentTripCancelled => 'Imalli haqameera';

  @override
  String get commitmentTripFull => 'Imalli guutameera';

  @override
  String get commitmentDroppedOut => 'Addaan kute';

  @override
  String commitmentAnswerBy(String time) {
    return 'Maaloo hanga $time tti deebisaa.';
  }

  @override
  String get commitmentBuyerWaitingNote =>
      'Maallaqni keessan nageenyaan qabameera. Jalqaba qonnaan bulaan, achi booda konkolaataan fe\'aa gaafatama. Yeroo isaan murteessan ajaja kaffalame haquun hin danda\'amu.';

  @override
  String get commitmentTruckerNotAskedNote =>
      'Qonnaan bulaan ammallee hin fudhanne. Yeroo inni fudhatu isin gaafatamtu.';

  @override
  String get truckerAcceptRequest => 'Geejjiba fudhadhu';

  @override
  String get truckerDeclineRequest => 'Didi';

  @override
  String get declineReasonTitle => 'Maaliif diddu?';

  @override
  String get declineReasonDate => 'Guyyaan naaf hin mijatu';

  @override
  String get declineReasonCapacity => 'Bakki gahaan hin jiru';

  @override
  String get declineReasonPrice => 'Gatiin naaf hin mijatu';

  @override
  String get declineReasonRoute => 'Karaan naaf hin mijatu';

  @override
  String get declineReasonCropNotReady => 'Midhaan qophaa\'aa miti';

  @override
  String get declineReasonOther => 'Sababa biraa';

  @override
  String get truckerDropOut => 'Geejjiba kana hin danda\'u';

  @override
  String get truckerDropOutWarning =>
      'Bitataaf ni himama, haquun maallaqa guutuu deebifachuu danda\'a. Erga fudhattanii booda addaan kutuun isin irratti lakkaa\'ama. Irra deebi\'uun akka seentan ni gaafatamtu.';

  @override
  String get farmerCancelWithoutPenalty => 'Adabbii malee haqi';

  @override
  String get farmerCancelCommittedWarning =>
      'Erga fudhattanii booda haquun isin irratti lakkaa\'ama, bitataanis maallaqa guutuu deebifata. Irra deebi\'uun akka seentan ni gaafatamtu.';

  @override
  String get farmerCancelAwaitingTransportBody =>
      'Geejjibni waan hin argamneef haquun isin irratti hin lakkaa\'amu. Bitataan maallaqa guutuu deebifata.';

  @override
  String transportFailedBuyerBody(String deadline) {
    return 'Konkolaataan fe\'aa ajaja kana fe\'uu hin danda\'u. Hanga $deadline tti haqaatii maallaqa guutuu deebifadhaa. Hanga sanatti yoo hin murteessine ajajni ni haqama, maallaqni keessan ni deebi\'a.';
  }

  @override
  String get transportFailedBuyerBodyNoDeadline =>
      'Konkolaataan fe\'aa ajaja kana fe\'uu hin danda\'u. Haquun maallaqa guutuu deebifachuu dandeessu.';

  @override
  String get transportFailedFarmerBody =>
      'Geejjibni ajaja kanaa hin argamne. Hanga bitataan murteessutti maaloo midhaan qabadhaa.';

  @override
  String get cancelOrderAwaitingTransportBody =>
      'Maallaqni ajaja kanaaf qabame guutummaatti isiniif deebi\'a.';

  @override
  String get errorResponseNotPending =>
      'Gaaffiin kun isiniif banaa miti: qonnaan bulaan ammallee hin fudhanne, ykn duraan deebii argateera.';

  @override
  String get transportFailureTruckerDeclined => 'Konkolaataan fe\'aa dideera';

  @override
  String get transportFailureTruckerExpired =>
      'Konkolaataan fe\'aa yeroodhaan hin deebisne';

  @override
  String get transportFailureTruckerCancelled =>
      'Konkolaataan fe\'aa addaan kuteera';

  @override
  String get transportFailureTripCancelled => 'Imalli haqameera';

  @override
  String get transportFailureTripFull => 'Imalli guutameera';

  @override
  String get actionNeededTitle => 'Tarkaanfiin barbaachisa';

  @override
  String get actionAnswerOrder => 'Ajaja haaraaf deebii kennaa';

  @override
  String get actionAnswerTransport => 'Gaaffii geejjibaaf deebii kennaa';

  @override
  String get actionKeepCrops => 'Midhaan qabadhaa: bitataan murteessaa jira';

  @override
  String get actionSetUpDelivery => 'Geejjiba qopheessaa';

  @override
  String get actionPay => 'Ajajaaf kaffalaa';

  @override
  String get actionPayAgain =>
      'Kaffaltiin hin milkoofne: irra deebi\'aa kaffalaa';

  @override
  String get actionDecideTransport => 'Geejjibni hin argamne: murteessaa';

  @override
  String get actionCheckDelivery => 'Wanta ga\'e mirkaneessaa';

  @override
  String actionDueBy(String time) {
    return 'Hanga $time';
  }

  @override
  String get tripBookings => 'Ajajoota qabaman';

  @override
  String get tripNoBookings => 'Ammallee ajajni qabame hin jiru';

  @override
  String tripRemainingCapacity(String quintals) {
    return 'Kuntaala $quintals duwwaa';
  }

  @override
  String get chooseTrip => 'Konkolaataa filadhaa';

  @override
  String get notifGenericTitle => 'Haalli ajajaa jijjiirameera';

  @override
  String get notifGenericBody => 'Maal akka jijjiirame ilaaluuf ajaja banaa.';

  @override
  String get notifPaymentConfirmedTitle => 'Kaffaltiin gaheera';

  @override
  String get notifPaymentConfirmedBody =>
      'Kaffaltiin keessan nageenyaan qabameera. Yeroo qonnaan bulaan deebii kennu isinitti himna.';

  @override
  String get notifOrderRequestTitle => 'Ajaja haaraa';

  @override
  String notifOrderRequestBody(String buyer) {
    return '$buyer kaffaleera; maallaqni hanga geejjibni xumuramutti nageenyaan qabama. Maaloo fudhadhaa ykn didaa.';
  }

  @override
  String get notifFarmerAcceptedTitle => 'Qonnaan bulaan fudhateera';

  @override
  String notifFarmerAcceptedBody(String farmer) {
    return '$farmer ajaja keessan fudhateera. Amma konkolaataa fe\'aa gaafachaa jirra.';
  }

  @override
  String get notifTransportRequestTitle => 'Gaaffii geejjibaa';

  @override
  String notifTransportRequestBody(String farmer) {
    return '$farmer ajaja imala keessanii fudhateera. Maaloo geejjiba fudhadhaa ykn didaa.';
  }

  @override
  String get notifOrderConfirmedTitle => 'Ajajni mirkanaa\'eera';

  @override
  String get notifOrderConfirmedBody =>
      'Qaamoleen hundi ajaja irratti waliigalaniiru.';

  @override
  String get notifOrderRejectedTitle => 'Ajajni didameera';

  @override
  String notifOrderRejectedBody(String farmer) {
    return '$farmer ajaja keessan hin fudhanne. Maallaqni keessan isiniif deebi\'a.';
  }

  @override
  String get notifTransportFailedTitle => 'Geejjibni hin argamne';

  @override
  String get notifTransportFailedBody =>
      'Konkolaataan fe\'aa ajaja keessan fe\'uu hin danda\'u. Maaloo murteessaa: haquun maallaqa guutuu deebifachuu dandeessu.';

  @override
  String get notifTransportFailedKeepCropsTitle => 'Geejjibni addaan citeera';

  @override
  String get notifTransportFailedKeepCropsBody =>
      'Konkolaataan fe\'aa ajaja kanaa dhiiseera. Hanga bitataan murteessutti maaloo midhaan qabadhaa.';

  @override
  String get notifRequestExpiredTitle => 'Gaaffiin cufameera';

  @override
  String get notifRequestExpiredBody =>
      'Yeroon deebii waan darbeef gaaffiin cufameera.';

  @override
  String get notifRequestOnHoldTitle => 'Gaaffiin yeroof dhaabateera';

  @override
  String get notifRequestOnHoldBody =>
      'Ajaja kanaaf geejjibni hin argamne. Bitataan murteessaa waan jiruuf amma deebii kennuun isin hin barbaachisu.';

  @override
  String get notifOrderCancelledTitle => 'Ajajni haqameera';

  @override
  String get notifOrderCancelledBody =>
      'Ajajni haqameera. Maallaqni qabame yoo jiraate bitataaf deebi\'a.';

  @override
  String get notifOrderExpiredUnpaidTitle => 'Ajajni cufameera';

  @override
  String get notifOrderExpiredUnpaidBody =>
      'Ajajni keessan yeroodhaan waan hin kaffalamneef cufameera. Ajaja haaraa galchuu dandeessu.';

  @override
  String get notifPaymentFailedTitle => 'Kaffaltiin hin milkoofne';

  @override
  String get notifPaymentFailedBody =>
      'Kaffaltiin keessan hin milkoofne. Maaloo irra deebi\'aa yaalaa.';

  @override
  String get notifRequestReminderTitle => 'Deebiin keessan barbaachisa';

  @override
  String get notifRequestReminderBody =>
      'Ajajni kun deebii keessan eegaa jira.';

  @override
  String get notifPickupOverdueTitle => 'Midhaan yeroodhaan hin fudhatamne';

  @override
  String get notifPickupOverdueBody =>
      'Ajajni keessan yeroodhaan hin fudhatamne. Filannoowwan keessan, maallaqa guutuu deebifachuu dabalatee, ilaaluuf banaa.';

  @override
  String get notifPickupMissedTitle => 'Fudhannaan darbeera';

  @override
  String get notifPickupMissedBody =>
      'Ajajni yeroodhaan hin fudhatamne. Bitataan amma haquun maallaqa deebifachuu danda\'a.';

  @override
  String get notifDeliveryScheduledTitle => 'Geejjibni qophaa\'eera';

  @override
  String notifDeliveryScheduledBody(String trucker) {
    return '$trucker ajaja ni geessa.';
  }

  @override
  String get notifBuyerCollectingTitle => 'Bitataan dhufaa jira';

  @override
  String notifBuyerCollectingBody(String buyer) {
    return '$buyer ajaja isin biraa ni fudhata. Maaloo midhaan qopheessaa.';
  }

  @override
  String get notifPickedUpTitle => 'Midhaan fudhatameera';

  @override
  String get notifPickedUpBody =>
      'Ajajni keessan qonnaan bulaa biraa fudhatameera.';

  @override
  String get notifHandoffRecordedTitle => 'Kenniinsi galmeeffameera';

  @override
  String get notifHandoffRecordedBody =>
      'Kenniinsi galmeeffameera. Erga bitataan midhaan fudhatee booda isiniif ni kaffalama.';

  @override
  String get notifInTransitTitle => 'Daandii irra jira';

  @override
  String get notifInTransitBody =>
      'Ajajni keessan gara keessan daandii irra jira.';

  @override
  String get notifTruckNearbyTitle => 'Konkolaataan dhihaateera';

  @override
  String notifTruckNearbyBody(String trucker) {
    return '$trucker isin irraa km 5 gadi fagaata. Midhaan fudhachuuf qophaa\'aa.';
  }

  @override
  String get notifDeliveredTitle => 'Ajajni ga\'eera';

  @override
  String get notifDeliveredBody =>
      'Ajajni keessan ga\'eera. Rakkoon yoo jiraate yeroon osoo hin darbin gabaasaa; yoo hin jiraanne qonnaan bulaaf ni kaffalama.';

  @override
  String get notifDeliveredPayoutPendingTitle =>
      'Ga\'eera, kaffaltiin ni dhufa';

  @override
  String get notifDeliveredPayoutPendingBody =>
      'Ajajni ga\'eera. Mirkaneessa bitataa sa\'aatii 48 booda kaffaltiin keessan ni ergama.';

  @override
  String get notifOrderSettledTitle => 'Ajajni xumurameera';

  @override
  String get notifOrderSettledBody =>
      'Ajajni xumurameera; hundi kaffalameera. Galatoomaa.';

  @override
  String get notifRefundSentTitle => 'Maallaqni deebi\'eera';

  @override
  String get notifRefundSentBody => 'Maallaqni keessan isiniif deebi\'eera.';

  @override
  String get notifPayoutSentTitle => 'Kaffaltiin ergameera';

  @override
  String get notifPayoutSentBody =>
      'Kaffaltiin keessan isiniif ergameera. Galatoomaa.';

  @override
  String get notifPayoutBlockedTitle => 'Kaffaltiin dhaabateera';

  @override
  String notifPayoutBlockedBody(String hint) {
    return 'Kaffaltiin keessan eegaa jira. $hint';
  }

  @override
  String get notifPayoutProcessingTitle => 'Kaffaltiin hojjetamaa jira';

  @override
  String get notifPayoutProcessingBody =>
      'Gareen keenya kaffaltii keessan harkaan ergaa jira. Guyyoota muraasa fudhachuu danda\'a.';

  @override
  String get notifDisputeOpenedTitle => 'Rakkoon gabaafameera';

  @override
  String get notifDisputeOpenedBody =>
      'Ajaja keessan irratti rakkoon gabaafameera. Hanga gareen keenya murteessutti maallaqni qabamee tura.';

  @override
  String get notifDisputeReceivedTitle => 'Gabaasni keessan nu gaheera';

  @override
  String get notifDisputeReceivedBody =>
      'Gabaasni keessan nu gaheera. Hanga gareen keenya murteessutti maallaqni qabamee tura.';

  @override
  String get notifDisputeResolvedTitle => 'Murteen kennameera';

  @override
  String get notifUnderReviewTitle => 'Kaffaltiin sakatta\'amaa jira';

  @override
  String get notifUnderReviewBody =>
      'Kaffaltiin ajaja keessanii gareen keenyaan sakatta\'amaa jira. Yeroo xumuramu isinitti himna.';

  @override
  String get notifDisputeResolvedFarmerWins =>
      'Gareen keenya murteesseera: maallaqni qonnaan bulaaf kaffalama.';

  @override
  String get notifDisputeResolvedBuyerWins =>
      'Gareen keenya murteesseera: maallaqni bitataaf deebi\'a.';

  @override
  String get notifDisputeResolvedBody =>
      'Gareen keenya rakkoo gabaafame irratti murteesseera. Bal\'ina isaa ilaaluuf ajaja banaa.';

  @override
  String get notifPayoutHintFayda =>
      'Maaloo appii keessatti Fayda ID keessan mirkaneessaa.';

  @override
  String get notifPayoutHintPhone =>
      'Maaloo appii keessatti lakkoofsa bilbilaa keessan mirkaneessaa.';

  @override
  String get notifPayoutHintProfile =>
      'Maaloo appii keessatti odeeffannoo keessan guutaa.';

  @override
  String get notifPayoutHintOther =>
      'Maal akka barbaachisu ilaaluuf appii banaa.';

  @override
  String get clockSetting => 'Sa\'aatii';

  @override
  String get clockAuto => 'Ofumaan';

  @override
  String get clockAutoHint => 'Afaan appii hordofa';

  @override
  String get clockEthiopian => 'Kan Itoophiyaa';

  @override
  String get clockInternational => 'Idil-addunyaa (sa\'aatii 24)';

  @override
  String clockNight(String time) {
    return '$time halkan';
  }

  @override
  String clockMorning(String time) {
    return '$time ganama';
  }

  @override
  String clockDay(String time) {
    return '$time waaree booda';
  }

  @override
  String clockEvening(String time) {
    return '$time galgala';
  }

  @override
  String get liveTripNotificationTitle => 'Bakki konkolaataa qoodamaa jira';

  @override
  String get liveTripNotificationText =>
      'Bitoonni fi qonnaan bultoonni imala kanaa bakka konkolaataan jiru ni argu. Qooduun erga geejjibni dhumaa xumuramee booda ni dhaabata.';

  @override
  String get liveTripNotificationChannel => 'Bakka imalaa';

  @override
  String get liveTripConsentTitle => 'Bakki konkolaataan jiru haa qoodamu?';

  @override
  String get liveTripConsentLede =>
      'Bitoonni fi qonnaan bultoonni konkolaataa arguu danda\'an yaaddoon isaanii ni hir\'ata, yeroo konkolaachiftanis xiqqoo isin bilbilu.';

  @override
  String get liveTripConsentWhoTitle => 'Eenyutu arga';

  @override
  String get liveTripConsentWhoText =>
      'Bitaa fi qonnaan bulaa ajaja imala kanaa tokkoon tokkoo, tokkoon isaanii ajaja ofii qofaaf. Konkolaachiftoonni biroo fi bitoonni imaltoota ilaalan gonkumaa hin argan.';

  @override
  String get liveTripConsentOftenTitle => 'Irra deddeebii';

  @override
  String get liveTripConsentOftenText =>
      'Sa\'aatii tokkotti al tokko gara, akkasumas iskaanii QR hundatti. Yeroo bakka buusaatti dhihaattan daqiiqaa 15 hundaa.';

  @override
  String get liveTripConsentStopsTitle => 'Yoom dhaabata';

  @override
  String get liveTripConsentStopsText =>
      'Yeroo geejjibni dhumaa imala kanaa xumuramu ofumaan dhaabata. Yeroo barbaaddanitti yeroof dhaabuu dandeessu.';

  @override
  String get liveTripConsentDataTitle => 'Deetaa';

  @override
  String get liveTripConsentDataText =>
      'Guyyaatti gara KB 170. Neetworkiin yoo hin jiraanne, gabaasni bakkaa bilbila irra eegee booda ergama.';

  @override
  String get liveTripConsentAndroidNote =>
      'Yeroo qooduun banaa ta\'etti Android beeksisa ni agarsiisa.';

  @override
  String get liveTripConsentWebNote =>
      'Weebii irratti, gabaasni bakkaa kan ergamu yeroo fuulli kun banaa ta\'e qofa.';

  @override
  String get liveTripConsentShare => 'Bakka imalaa qoodi';

  @override
  String get liveTripConsentDecline => 'Amma miti, iskaanii qofa';

  @override
  String get liveTripSharingStarted =>
      'Bakki konkolaataa imala kanaaf qoodamaa jira.';

  @override
  String get liveTripScansOnlyChosen =>
      'Imala kanaaf iskaanii qofa. Ammas fuula haala imalaa irraa qooduu dandeessu.';

  @override
  String get tripModeTitle => 'Haala imalaa';

  @override
  String get tripModeOpen => 'Imala bani';

  @override
  String tripModeOrders(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'ajajoota $count',
      one: 'ajaja 1',
    );
    return '$_temp0';
  }

  @override
  String get tripModeSharingLive => 'Bakki keessan qoodamaa jira';

  @override
  String tripModeLastCheckIn(String time) {
    return 'Gabaasa dhumaa $time';
  }

  @override
  String get tripModeNoCheckInYet => 'Hanga ammaatti gabaasni hin jiru';

  @override
  String tripModeNextBy(String time) {
    return 'kan itti aanu $time';
  }

  @override
  String get tripModePrivacy =>
      'Bitaan fi qonnaan bulaan tokkoon tokkoon isaanii kutaa ajaja ofii qofa argu.';

  @override
  String get tripModePause => 'Yeroof dhaabi';

  @override
  String get tripModeCheckInNow => 'Amma gabaasi';

  @override
  String get tripModePausedTitle => 'Qooduun yeroof dhaabateera';

  @override
  String tripModePausedUntil(String time) {
    return '$time ofumaan deebi\'a';
  }

  @override
  String get tripModeResume => 'Amma itti fufi';

  @override
  String get tripModeScansOnlyTitle => 'Iskaanii qofa';

  @override
  String get tripModeScansOnlyText =>
      'Bakki keessan hin qoodamu. Iskaaniin QR ammas fudhachuu fi geessuu ni galmeessa.';

  @override
  String get tripModeEndedTitle => 'Qooduun xumurameera';

  @override
  String get tripModeEndedText =>
      'Geejjibni imala kanaa hundi waan xumurameef qooduun dhaabateera.';

  @override
  String get tripModeNoTrip =>
      'Imalli karaa irra jiru hin jiru. Haalli imalaa iskaanii fudhachuu jalqabaatiin jalqaba.';

  @override
  String get tripModeNextPickup => 'Dhaabbii itti aanu: fudhachuu';

  @override
  String get tripModeNextDropoff => 'Dhaabbii itti aanu: buusuu';

  @override
  String get tripModeArrived => 'Ga\'eera';

  @override
  String get tripModeStops => 'Dhaabbiiwwan';

  @override
  String tripModeStopPickup(String name) {
    return 'Fudhachuu: $name';
  }

  @override
  String tripModeStopDropoff(String name) {
    return 'Buusuu: $name';
  }

  @override
  String tripModeScannedAt(String time) {
    return '$time iskaan godhame';
  }

  @override
  String tripModeDeliveredAt(String time) {
    return '$time ga\'e';
  }

  @override
  String get tripModeNextLabel => 'Itti aanu';

  @override
  String get tripModePayTitle => 'Kaffaltii keessan imala kanaaf';

  @override
  String get tripModePayNote => 'Buusii hundaa booda sa\'aatii 48tti kaffalama';

  @override
  String get tripModeCheckInSaved => 'Gabaasni olkaa\'ameera.';

  @override
  String get tripModeNoFix =>
      'Amma bakka argachuun hin danda\'amne. Tajaajilli bakkaa banaa ta\'uu mirkaneessaatii irra deebi\'aa yaalaa.';

  @override
  String get tripModeNotSharing => 'Imalli kun bakka isaa hin qoodu.';

  @override
  String get tripModeLoadFailed =>
      'Dhaabbiiwwan imala kanaa fe\'uun hin danda\'amne.';

  @override
  String get tripModeOfflineTitle => 'Asitti neetworkiin hin jiru';

  @override
  String tripModeOfflineWaiting(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other:
          'Gabaasni $count bilbila kana irra eegaa jira. Yeroo neetworkiin deebi\'u ofumaan ergamu.',
      one:
          'Gabaasni 1 bilbila kana irra eegaa jira. Yeroo neetworkiin deebi\'u ofumaan ergama.',
      zero:
          'Gabaasni bilbila kana irra eegee yeroo neetworkiin deebi\'u ofumaan ergama.',
    );
    return '$_temp0';
  }

  @override
  String get tripModeCardTitle => 'Imala karaa irra jiru';

  @override
  String get pauseSheetTitle => 'Qooduu yeroof dhaabuu?';

  @override
  String get pauseSheetText =>
      'Boqonnaaf ykn halkaniif. Bitoonni fi qonnaan bultoonni akka qooduun dhaabate fi yoom akka deebi\'u ni argu.';

  @override
  String get pauseSheetFor => 'Yeroo hammamiif';

  @override
  String get pauseSheet15 => 'Daqiiqaa 15';

  @override
  String get pauseSheet30 => 'Daqiiqaa 30';

  @override
  String get pauseSheet60 => 'Sa\'aatii 1';

  @override
  String pauseSheetOvernight(String time) {
    return 'Halkan guutuu, hanga $time';
  }

  @override
  String pauseSheetBackAt(String time) {
    return '$time deebi\'a';
  }

  @override
  String get pauseSheetNote =>
      'Qooduun ofumaan deebi\'a. Iskaaniin QR ammas bakka isaa ni galmeessa.';

  @override
  String get pauseSheetConfirm => 'Qooduu yeroof dhaabi';

  @override
  String get pauseSheetKeep => 'Qooduu itti fufi';

  @override
  String get sharingDefaultSetting => 'Bakka imalaa';

  @override
  String get sharingDefaultHint =>
      'Iskaanii fudhachuu jalqabaa imalaa irratti kan ta\'u. Bitaan fi qonnaan bulaan ajaja tokkoon tokkoo hanga geejjiba isaaniitti konkolaataa ni argu.';

  @override
  String get sharingDefaultAsk => 'Imala hundatti na gaafadhu';

  @override
  String get sharingDefaultShare => 'Yeroo hunda qoodi';

  @override
  String get sharingDefaultScansOnly => 'Gonkumaa hin qoodin (iskaanii qofa)';

  @override
  String get liveTripTitle => 'Imala kallattii';

  @override
  String get liveTripOnTheRoadNow => 'Amma karaa irra';

  @override
  String get liveTripWaitingForPickup => 'Fe\'umsa eeggachaa';

  @override
  String get liveTripMapStartsAtPickup =>
      'Yeroo konkolaachisaan QR qonnaan bulaa iskaan godhu kaartaan ni mul\'ata.';

  @override
  String liveTripDurationMinutes(int minutes) {
    return 'daqiiqaa $minutes';
  }

  @override
  String liveTripDurationHours(int hours, int minutes) {
    return 'sa\'aatii $hours daqiiqaa $minutes';
  }

  @override
  String liveTripAgo(String duration) {
    return '$duration dura';
  }

  @override
  String get liveTripJustNow => 'Amma';

  @override
  String liveTripCheckedInAgo(String duration) {
    return '$duration dura gabaase';
  }

  @override
  String get liveTripCheckedInJustNow => 'Amma gabaase';

  @override
  String liveTripNoCheckInFor(String duration) {
    return 'Yeroo $duration gabaasni hin jiru';
  }

  @override
  String get liveTripPaused => 'Dhaabbateera';

  @override
  String liveTripUntil(String time) {
    return 'hanga $time';
  }

  @override
  String get liveTripScansOnly => 'Iskaanii qofa';

  @override
  String get liveTripScansOnlyChip => 'Bakki hin qoodamne';

  @override
  String get liveTripSharingEnded => 'Qooduun yeroo geejjibaatti dhaabbate';

  @override
  String get liveTripStatusOnTheRoad => 'Geejjibarra Jira';

  @override
  String get liveTripStatusArriving => 'Ga\'aa jira';

  @override
  String liveTripArrivingAt(String place) {
    return 'Yeroo $place ga\'u';
  }

  @override
  String get liveTripArrivalWindow => 'Yeroo ga\'u';

  @override
  String liveTripKmToGo(String km) {
    return 'Gara km $km hafe, gabaasa dhumaa irraa';
  }

  @override
  String get liveTripWiderWindow =>
      'Yeroon bal\'ateera: yeroo muraasaaf gabaasni hin dhufne';

  @override
  String get liveTripNoEta => 'Tilmaamni yeroo ga\'umsaa ammallee hin jiru';

  @override
  String get liveTripStepAgreed => 'Waliigalan';

  @override
  String get liveTripStepPickedUp => 'Fe\'ame';

  @override
  String liveTripEscrowNote(String amount) {
    return '$amount keessan hanga QR konkolaachisaa iskaan gootanitti Eeskiroo keessa tursiifama.';
  }

  @override
  String get liveTripEscrowNoteNoAmount =>
      'Maallaqni keessan hanga QR konkolaachisaa iskaan gootanitti Eeskiroo keessa tursiifama.';

  @override
  String liveTripLateNote(String trucker) {
    return 'Daandii kana irratti neetworkiin hin jiru ta\'a. Gabaasni bilbila $trucker irratti kuufame yeroo walqunnamu ni mul\'ata.';
  }

  @override
  String liveTripPausedNote(String trucker, String time) {
    return '$trucker boqonnaaf qooduu dhaabaniiru. $time irratti ofumaan deebi\'ee ni jalqaba.';
  }

  @override
  String liveTripScansOnlyNote(String trucker) {
    return '$trucker bakka konkolaataa qooduu dhiisuu filataniiru. Iskaaniin QR ammallee fe\'umsaa fi geejjiba ni galmeessa.';
  }

  @override
  String liveTripOfflineNote(String time) {
    return 'Toora ala jirtu. Kun kaartaa $time ti, yeroo walqunnamtan ni haaromfama.';
  }

  @override
  String get liveTripOfflineNoMap =>
      'Toora ala jirtu. Kaartaan yeroo walqunnamtan ni mul\'ata.';

  @override
  String get liveTripUnavailable => 'Kaartaan amma haaromfamuu hin dandeenye.';

  @override
  String get liveTripNotFound => 'Imalli kun hin argamne.';

  @override
  String get liveTripTripLogLink => 'Galmee imalaa fi ajaja';

  @override
  String liveTripCheckIns(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'gabaasa $count',
    );
    return '$_temp0';
  }

  @override
  String get liveTripTripLogTitle => 'Galmee imalaa';

  @override
  String liveTripKm(String km) {
    return 'km $km';
  }

  @override
  String get liveTripStatDone => 'deemame';

  @override
  String get liveTripStatToGo => 'hafe';

  @override
  String get liveTripStatOnTheRoad => 'imala irra';

  @override
  String get liveTripStatDriven => 'oofame';

  @override
  String get liveTripStatCheckIns => 'gabaasa';

  @override
  String get liveTripCheckInCadence => 'Gabaasni gara sa\'aatii hundaa';

  @override
  String get liveTripLogPickup => 'Fe\'ame, QR qonnaan bulaa iskaan godhame';

  @override
  String get liveTripLogHourly => 'Gabaasa sa\'aatii';

  @override
  String get liveTripLogNearStop => 'Gabaasa bakka dhaabbii bira';

  @override
  String get liveTripLogManual => 'Konkolaachisaan gabaase';

  @override
  String get liveTripLogArrived => 'Konkolaachisaan bakka dhaabbii ga\'e';

  @override
  String get liveTripLogStart => 'Qooduun jalqabe';

  @override
  String get liveTripLogOtherStop => 'Dhaabbii biraa konkolaataa kanaa';

  @override
  String get liveTripLogCheckIn => 'Gabaasa';

  @override
  String get liveTripLogPaused => 'Qooduun dhaabbateera';

  @override
  String get liveTripLogLatest => 'isa dhumaa';

  @override
  String get liveTripCheckInsTitle => 'Gabaasota';

  @override
  String get liveTripAboutEveryHour => 'gara sa\'aatii hundaa';

  @override
  String get liveTripLogNext => 'itti aanu';

  @override
  String get liveTripLogThen => 'ergasii';

  @override
  String liveTripLogSentLateUntil(String time) {
    return 'Booda ergame · hanga $time neetworkiin hin turre';
  }

  @override
  String get liveTripExpectedToday => 'har\'a eegama';

  @override
  String get liveTripLogSentLate => 'Booda ergame, neetworkiin hin turre';

  @override
  String get liveTripLogPassed => 'darbe';

  @override
  String get liveTripLogAhead => 'Fuuldura';

  @override
  String get liveTripLogYourDropoff =>
      'Bakka itti fudhattan: asitti QR konkolaachisaa iskaan gootu';

  @override
  String get liveTripPickupProof => 'Ragaa fe\'umsaa';

  @override
  String get liveTripDropoffProof => 'Ragaa geejjibaa';

  @override
  String liveTripQrScannedAt(String time) {
    return 'QR kan iskaan godhame $time';
  }

  @override
  String get liveTripGpsRecorded => 'GPS galmaa\'eera';

  @override
  String get liveTripInEscrow => 'Eeskiroo keessa';

  @override
  String liveTripPayoutNote(String farmer, String farmerAmount, String trucker,
      String truckerAmount) {
    return 'Erga geejjiba mirkaneessitanii sa\'aatii 48 booda ni kaffalama: $farmer $farmerAmount, $trucker $truckerAmount.';
  }

  @override
  String liveTripPayoutNoteFarmerOnly(String farmer, String amount) {
    return 'Erga geejjiba mirkaneessitanii sa\'aatii 48 booda $farmer $amount ni kaffalamu.';
  }

  @override
  String get liveTripHelpTitle => 'Karaa irratti rakkoon jiraa?';

  @override
  String liveTripHelpBody(String trucker, String farmer) {
    return 'Ajajni fe\'ame kana booda haqamuu hin danda\'u. Amma $trucker ykn $farmer haasofsiisaa. Erga geejjibamee booda rakkoo gabaasuuf sa\'aatii 48 qabdu, maallaqnis hanga dhimmichi furamutti eeggata.';
  }

  @override
  String liveTripMessageParty(String name) {
    return '${name}f ergaa ergi';
  }

  @override
  String liveTripPrivacyNote(String farmer) {
    return 'Konkolaataan kun eessa akka jiru kan argitan isinii fi $farmer qofa, fe\'umsa irraa hanga geejjiba keessaniitti. Dhaabbiiwwan biroo gonkumaa hin moggaafaman.';
  }

  @override
  String get liveTripGreyRingsNote =>
      'Geengoon daalachaa dhaabbiiwwan biroo konkolaataa kanaati. Gonkumaa hin moggaafaman.';

  @override
  String get liveTripArrivingTitle => 'Konkolaataan ga\'aa jira';

  @override
  String liveTripArrivingMinutes(String trucker, int minutes) {
    return '$trucker gara daqiiqaa $minutes fagaatu';
  }

  @override
  String liveTripArrivingNear(String trucker) {
    return '$trucker km 5 gadi fagaatu';
  }

  @override
  String liveTripKmFromDropoff(String km) {
    return 'bakka geejjibaa irraa km $km';
  }

  @override
  String get liveTripBeforeYouScan => 'Osoo iskaan hin godhiin dura';

  @override
  String get liveTripCheckCount =>
      'Kuusaawwan lakkaa\'aa, ulfaatina waliigalaa mirkaneeffadhaa';

  @override
  String get liveTripCheckQuality =>
      'Midhaan gogaa fi qulqulluu ta\'uu mirkaneeffadhaa';

  @override
  String get liveTripCheckWeigh => 'Yoo dandeessan kuusaa muraasa madaalaa';

  @override
  String liveTripScanConfirmsNote(String farmer, String trucker) {
    return 'Iskaan gochuun geejjiba mirkaneessa. Yoo rakkoo hin gabaasne, $farmer fi $trucker yeroo mirkaneessaa sa\'aatii 48 booda ni kaffalamu.';
  }

  @override
  String get liveTripScanDriversQr => 'QR konkolaachisaa iskaan godhaa';

  @override
  String liveTripDeliveredAt(String time) {
    return '$time geejjibame';
  }

  @override
  String get liveTripEnded => 'Imalli kun xumurameera';

  @override
  String liveTripReportProblemUntil(String time) {
    return 'Hanga $time rakkoo gabaasaa';
  }

  @override
  String get liveTripHowDidItGo => 'Akkam ture?';

  @override
  String liveTripFarmerTitle(String crop) {
    return '${crop}n keessan karaa irra jira';
  }

  @override
  String get liveTripFarmerTitleNoCrop => 'Ajajni keessan karaa irra jira';

  @override
  String liveTripFarmerTo(String quantity, String buyer) {
    return '$quantity gara $buyer';
  }

  @override
  String liveTripArrives(String time) {
    return 'Yeroo ga\'u $time';
  }

  @override
  String get liveTripYourPayment => 'Kaffaltii keessan';

  @override
  String get liveTripPayStepPaid => 'Bitaan kaffaleera, Eeskiroo keessa qabame';

  @override
  String get liveTripPayStepHandedOver => 'Dabarsitaniittu, QR iskaan godhame';

  @override
  String get liveTripPayStepBuyerConfirms => 'Bitaan geejjiba mirkaneessa';

  @override
  String get liveTripPayStepPaidOut =>
      'Yeroo mirkaneessaa sa\'aatii 48, sana booda ni kaffalama';

  @override
  String get liveTripOrderDetailsLink => 'Ibsa ajajaa, gatii fi sarara yeroo';

  @override
  String get liveTripAfterPickupHint =>
      'Erga fe\'amee booda konkolaataa kaartaa irratti Geejjibaalee keessatti ni argitu.';

  @override
  String liveTripFrom(String name) {
    return '$name irraa';
  }

  @override
  String liveTripMapSemantics(String place) {
    return 'Kaartaa imalaa. Konkolaataan dhuma irratti $place bira ture.';
  }

  @override
  String get liveTripMapSemanticsNoPlace => 'Kaartaa imalaa';

  @override
  String get liveTripYou => 'Isin';

  @override
  String get liveTripDropoff => 'Bakka geejjibaa';

  @override
  String get liveTripPickup => 'Bakka fe\'umsaa';

  @override
  String liveTripOnTheRoadSince(String time) {
    return '$time irraa kaasee karaa irra';
  }

  @override
  String get loginEscrowFooter =>
      'Kaffaltiin Eeskiroo keessatti nageenyaan qabama';

  @override
  String get loginNoAccountRegister => 'Akkaawuntii hin qabduu? Galmaa\'i';

  @override
  String get otpFaydaNumberNote =>
      'Kana kan gaafannu yoo lakkoofsi galmee Fayda keessanii irraa adda ta\'e qofa.';

  @override
  String get faydaDigitalIdLabel => 'Eenyummeessaa Dijitaalaa';

  @override
  String get faydaVerifiedWith => 'Fayda\'n mirkanaa\'e';

  @override
  String get faydaLegalNameNote =>
      'Maqaan seeraa keessan Fayda irraa dhufa. Namni kamiyyuu maqaa keessaniin galmaa\'uu hin danda\'u.';

  @override
  String registrationStepOf(int current, int total) {
    return 'Tarkaanfii $current kan $total';
  }

  @override
  String get registrationRoleNextNote =>
      'Itti aansuun Fayda\'n mirkaneessitu, achiis odeeffannoo keessan galchitu. Qonnaan bultoonni tarkaanfii odeeffannoo gahee ni darbu.';

  @override
  String get fromFayda => 'Fayda irraa';

  @override
  String get placeLicenseHere => 'Hayyama konkolaachisaa asitti kaa\'aa';

  @override
  String get retakeLicensePhoto => 'Suuraa hayyamaa irra deebi\'ii kaasi';

  @override
  String get totalListingValue => 'Gatii Waliigalaa Tarree';

  @override
  String get checkoutHeldInEscrowNote =>
      'Hanga geessuu mirkaneessitanitti Eeskiroo keessatti qabama';

  @override
  String get placeOrderEscrowNote =>
      'Maallaqni keessan nageenyaan qabama. Duraan qonnaan bulaan, itti aansee konkolaachisaan ni gaafatama.';

  @override
  String get deliveriesSearchHint => 'Qonnaan bulaa, bitaa ykn konkolaachisaa';

  @override
  String get savedOnPhoneFirstNote =>
      'Duraan bilbila keessan irratti olkaa\'ama, yeroo neetworkiin jiru ni ergama.';

  @override
  String get myCropsPrivacyNote =>
      'Tarree keessan asitti kan argitu isin qofa. Bitoonni Midhaan Barbaadi keessatti argu.';

  @override
  String get homeToday => 'Har\'a';

  @override
  String tripPricePerQuintalValue(String price) {
    return 'Kuntaala tokkoof Birrii $price';
  }

  @override
  String createTripRouteDistance(int km) {
    return 'Sarara qajeelaan gara km $km, bilbila keessan irratti kan kaafame';
  }

  @override
  String createTripRoutePreviewLabel(String from, String to) {
    return 'Agarsiisa karaa $from irraa gara $to';
  }

  @override
  String get createTripSwapRoute => 'Ka\'umsaa fi bakka gahumsaa wal jijjiiri';

  @override
  String get createTripDriverHint =>
      'Maqaa fi bilbila keessan piroofaayilii irraa guutuu hin dandeenye. Maaloo galchaa.';

  @override
  String get tripSpaceTitle => 'Bakka imala kanarra jiru';

  @override
  String tripLoadedOf(String loaded, String capacity) {
    return 'Kuntaala $capacity keessaa $loaded qabameera';
  }

  @override
  String get tripBookedTermsNote =>
      'Imalli qabaman guyyaa, gatii fi karaa isaanii hin jijjiiran.';

  @override
  String get tripScanAtPickupNote =>
      'Guyyaa imalaa, bakka fe\'umsaatti QR qonnaan bulaa hundaa iskaanii godhaa. Imalli kallattii iskaanii jalqabaatiin jalqaba.';

  @override
  String get tripCancelBookedNote =>
      'Haquun ajajawwan qabaman gara bitoota isaaniitti deebisa; isaanis maallaqa guutuu deebifachuuf haquu danda\'u.';

  @override
  String get orderYouReceive => 'Kan isinii gahu';

  @override
  String get orderYourTransportPay => 'Kaffaltii geejjibaa keessan';

  @override
  String get deliveriesNoSearchMatch =>
      'Geessituun maqaa sanaan walsimu hin jiru.';

  @override
  String get farmerRejectRefundNote => 'Bitaan maallaqa guutuu deebifata.';

  @override
  String get farmerAnswerNote =>
      'Quunnamtiin bitaa wajjin yeroo fudhattan bana. Diduun sababa gaafata, bitaanis maallaqa deebifata.';

  @override
  String get checkoutConfirmItsYouNote =>
      'Kaffaltiin Birrii 10,000 ol Fayda barbaada. Kanneen xixiqqoon jecha icciitii keessan gaafatu.';

  @override
  String myCropsOrderedOf(String ordered, String total) {
    return 'Kuntaala $total keessaa $ordered ajajameera';
  }

  @override
  String myCropsNoOrdersYet(String quantity) {
    return 'Kuntaala $quantity, hanga ammaatti ajajni hin jiru';
  }

  @override
  String get createTripBookingCutoffNote =>
      'Bitoonni hanga sa\'aatii 24 osoo hin ka\'iin dura qabachuu danda\'u.';

  @override
  String myCropsListedOn(String date) {
    return '$date tarreeffame';
  }

  @override
  String liveTripLogStopAhead(int minutes) {
    return 'Gara daqiiqaa $minutes, achiis gara keessanitti';
  }

  @override
  String marketTransportOn(String day) {
    return 'Geejjiba $day';
  }

  @override
  String marketTripsThisWeek(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Torban kana imala $count',
      one: 'Torban kana imala 1',
    );
    return '$_temp0';
  }

  @override
  String get marketNoTripYet => 'Hanga ammaatti imalli hin jiru';

  @override
  String liveTripEscrowAmount(String amount) {
    return '$amount Eeskiroo keessa';
  }

  @override
  String get liveTripReleasePending => 'Kaffaltiin ammallee hin gadhiifamne';

  @override
  String get liveTripYouConfirmedDelivery => 'Geejjiba mirkaneessitaniittu';

  @override
  String get liveTripCheckWindowEnds => 'Yeroon mirkaneessaa xumurama';

  @override
  String liveTripPartyGets(String name, String amount) {
    return '$name $amount argata';
  }
}

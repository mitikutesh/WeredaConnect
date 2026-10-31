import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripListingRepository extends Mock
    implements BaseTripListingRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeCropListingEvent extends Fake implements CropListingEvent {}

class FakeCropListingState extends Fake implements CropListingState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

CropListing _listing({
  String id = 'listing-1',
  String farmerId = 'farmer-1',
  String farmerName = 'Abebe',
}) =>
    CropListing()
      ..listingId = id
      ..farmerId = farmerId
      ..farmerName = farmerName
      ..cropType = 'teff'
      ..quantity = 10
      ..pricePerKg = 45
      ..location = (LatLng()
        ..latitude = 9.0
        ..longitude = 38.7)
      ..locationName = 'Bishoftu'
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt = DateTime.now()
      ..updatedAt = DateTime.now()
      ..version = 1;

void main() {
  late MockAuthBloc authBloc;
  late MockCropListingBloc cropListingBloc;
  late MockRatingRepository ratingRepo;
  late RatingBloc ratingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeCropListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    cropListingBloc = MockCropListingBloc();
    ratingRepo = MockRatingRepository();
    when(() => ratingRepo.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
    ratingBloc = RatingBloc(repository: ratingRepo);

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _testTokens,
        role: UserRole.farmer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget({
    bool showHeader = true,
    bool ownerMode = false,
    bool embedded = false,
    Locale? locale,
    OrderBloc? orderBloc,
    TripListingBloc? tripBloc,
  }) {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
        if (orderBloc != null) BlocProvider<OrderBloc>.value(value: orderBloc),
        if (tripBloc != null)
          BlocProvider<TripListingBloc>.value(value: tripBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: Scaffold(
          body: BrowseListingsScreen(
            showHeader: showHeader,
            ownerMode: ownerMode,
            embedded: embedded,
          ),
        ),
      ),
    );
  }

  group('BrowseListingsScreen', () {
    testWidgets('shows empty state when no listings', (tester) async {
      when(() => cropListingBloc.state).thenReturn(
        const CropListingsLoaded(listings: []),
      );
      when(() => cropListingBloc.stream).thenAnswer(
        (_) => Stream.value(const CropListingsLoaded(listings: [])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('No crop listings yet'), findsOneWidget);
    });

    testWidgets('shows loading indicator', (tester) async {
      when(() => cropListingBloc.state).thenReturn(const CropListingLoading());
      when(() => cropListingBloc.stream).thenAnswer(
        (_) => Stream.value(const CropListingLoading()),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows filter chips', (tester) async {
      when(() => cropListingBloc.state).thenReturn(
        const CropListingsLoaded(listings: []),
      );
      when(() => cropListingBloc.stream).thenAnswer(
        (_) => Stream.value(const CropListingsLoaded(listings: [])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Crop type filter "All" chip
      expect(find.text('All'), findsOneWidget);
      expect(find.text('Teff'), findsOneWidget);
      expect(find.text('Wheat'), findsOneWidget);
    });
  });

  group('BrowseListingsScreen seller ratings (SIM-19)', () {
    void loaded(List<CropListing> listings) {
      final state = CropListingsLoaded(listings: listings);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
    }

    testWidgets(
        'shows the seller real average and count, never the old placeholder',
        (tester) async {
      when(() => ratingRepo.getRatingSummary('farmer-1')).thenAnswer(
        (_) async => const RatingSummary(average: 4.6, count: 12),
      );
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('4.6 (12)'), findsOneWidget);
      expect(find.text('4.5'), findsNothing);
      expect(find.text('Escrow'), findsNothing);
    });

    testWidgets('the list ends clear of the floating create button (LT-01)',
        (tester) async {
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      final list = tester.widget<ListView>(
        find.descendant(
          of: find.byType(RefreshIndicator),
          matching: find.byType(ListView),
        ),
      );
      expect(
        list.padding!.resolve(TextDirection.ltr).bottom,
        Breakpoints.fabClearance,
      );
    });

    const privacyNote =
        'Only you see your listings here. Buyers see them in Find Crops.';

    // Buyer · Crop detail and transport board: a buyer browsing Find Crops
    // gets transport suggested to their own wereda.
    for (final (role, woreda, expected) in [
      (UserRole.buyer, 'Merkato', 'Merkato'),
      (UserRole.buyer, '  ', null),
      (UserRole.farmer, 'Meki', null),
    ]) {
      testWidgets(
          '${role.name} with wereda "$woreda" opens the detail with '
          'delivery wereda $expected', (tester) async {
        when(() => authBloc.state).thenReturn(
          AuthAuthenticated(
            userId: '${role.name}-001',
            tokens: _testTokens,
            role: role,
            woreda: woreda,
          ),
        );
        loaded([_listing()]);

        await tester.pumpWidget(buildTestWidget());
        await tester.pumpAndSettle();
        await tester.tap(find.byType(ListingCard).first);
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 400));

        final detail = tester.widget<CropListingDetailScreen>(
          find.byType(CropListingDetailScreen),
        );
        expect(detail.deliveryWoreda, expected);
      });
    }

    testWidgets('My Crops ends with who can see the listings', (tester) async {
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget(ownerMode: true));
      await tester.pumpAndSettle();

      expect(find.text(privacyNote), findsOneWidget);
    });

    testWidgets('a price per kg of 1000 or more groups its thousands',
        (tester) async {
      loaded([_listing()..pricePerKg = 1250]);

      await tester.pumpWidget(buildTestWidget(ownerMode: true));
      await tester.pumpAndSettle();

      expect(find.text('1,250'), findsOneWidget);
    });

    // MyCrops board: "32 of 60 quintals ordered". Paid orders took their
    // quantity off the listing, so the total is what is left plus them.
    testWidgets('My Crops cards say how much is ordered', (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _testTokens,
          role: UserRole.farmer,
        ),
      );
      final orderBloc = MockOrderBloc();
      final orders = MockOrderRepository();
      when(() => orderBloc.repository).thenReturn(orders);
      when(() => orderBloc.state).thenReturn(const OrderInitial());
      when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
      Order order(String listingId, double kg, OrderStatus status) => Order()
        ..orderId = '$listingId-$kg-${status.name}'
        ..listingId = listingId
        ..buyerId = 'b'
        ..farmerId = 'farmer-001'
        ..cropType = 'teff'
        ..quantity = kg
        ..pricePerUnit = 40
        ..totalPrice = kg * 40
        ..status = status
        ..createdAt = DateTime(2026, 10)
        ..version = 1;
      when(() => orders.getOrdersByFarmer('farmer-001')).thenAnswer(
        (_) async => [
          order('wheat', 3000, OrderStatus.funded),
          order('wheat', 200, OrderStatus.inTransit),
          order('wheat', 500, OrderStatus.rejected), // stock put back
          order('wheat', 900, OrderStatus.pendingPayment), // not paid
        ],
      );
      final wheat = _listing(id: 'wheat', farmerId: 'farmer-001')
        ..quantity = 28;
      final chickpea = _listing(id: 'chickpea', farmerId: 'farmer-001')
        ..quantity = 8;
      loaded([wheat, chickpea]);

      await tester.pumpWidget(
        buildTestWidget(ownerMode: true, orderBloc: orderBloc),
      );
      await tester.pumpAndSettle();

      expect(find.text('32 of 60 quintals ordered'), findsOneWidget);
      expect(find.text('8 quintals, no orders yet'), findsOneWidget);
    });

    // MyCrops board: "Almaz Bekele · Meki" over "My Crops", and each card
    // says where and when it was listed instead of the farmer's own name.
    testWidgets('My Crops header and card lines', (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _testTokens,
          role: UserRole.farmer,
          userName: 'Almaz Bekele',
          woreda: 'Meki',
        ),
      );
      loaded([
        _listing(id: 'a', farmerId: 'farmer-001', farmerName: 'Almaz Bekele')
          ..createdAt = DateTime.utc(2026, 1, 26, 9),
      ]);

      await tester.pumpWidget(buildTestWidget(ownerMode: true));
      await tester.pumpAndSettle();

      expect(find.text('My Crops'), findsOneWidget);
      expect(find.text('Almaz Bekele · Meki'), findsOneWidget);
      expect(find.text('Bishoftu · listed Jan 26'), findsOneWidget);
      // The board's "Farmer" pill beside the title.
      expect(find.text('Farmer'), findsOneWidget);
      expect(find.textContaining('Almaz Bekele, Bishoftu'), findsNothing);
    });

    // Buyer · Find crops board: each card says what transport there is
    // this week from the farm to the buyer's wereda.
    testWidgets('buyer cards show the transport this week', (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
          woreda: 'Addis Ababa',
        ),
      );
      final tripBloc = MockTripListingBloc();
      final trips = MockTripListingRepository();
      when(() => tripBloc.repository).thenReturn(trips);
      when(() => tripBloc.state).thenReturn(const TripListingInitial());
      when(() => tripBloc.stream).thenAnswer((_) => const Stream.empty());
      final today = DateTime.now().toUtc();
      Trip trip(String id, String from) => Trip()
        ..tripId = id
        ..truckerId = 't1'
        ..fromWoreda = from
        ..toWoreda = 'Addis Ababa'
        ..departureDate = today.add(const Duration(days: 1))
        ..capacity = 50
        ..remainingQuintals = 18
        ..pricePerQuintal = 150
        ..status = TripStatus.open
        ..matchedListings = <String>[]
        ..createdAt = today
        ..updatedAt = today
        ..version = 1;
      when(
        () => trips.getOpenTrips(
          fromWoreda: any(named: 'fromWoreda'),
          toWoreda: any(named: 'toWoreda'),
        ),
      ).thenAnswer(
        (_) async =>
            [trip('m1', 'Meki'), trip('k1', 'Koka'), trip('k2', 'Koka')],
      );
      loaded([
        _listing(id: 'meki')..locationName = 'Meki',
        _listing(id: 'koka')..locationName = 'Koka',
        _listing(id: 'bichena')..locationName = 'Bichena',
      ]);

      await tester.pumpWidget(buildTestWidget(tripBloc: tripBloc));
      await tester.pumpAndSettle();

      // The pill, not the footnote's "Transport is matched for ...".
      expect(find.textContaining(RegExp(r'^Transport ')), findsOneWidget);
      expect(find.textContaining('18 quintals free'), findsOneWidget);
      expect(find.text('2 trips this week'), findsOneWidget);
      expect(find.text('No trip yet'), findsOneWidget);
    });

    // Market board: "Every farmer's listing, sorted by the best match for
    // Merkato."
    testWidgets('buyer listings sort by the best transport match, none lost',
        (tester) async {
      // Tall enough for the four cards and the footnote under them.
      tester.view.physicalSize = const Size(800, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
          woreda: 'Addis Ababa',
        ),
      );
      final tripBloc = MockTripListingBloc();
      final trips = MockTripListingRepository();
      when(() => tripBloc.repository).thenReturn(trips);
      when(() => tripBloc.state).thenReturn(const TripListingInitial());
      when(() => tripBloc.stream).thenAnswer((_) => const Stream.empty());
      final today = DateTime.now().toUtc();
      Trip trip(String id, String from, {int inDays = 1}) => Trip()
        ..tripId = id
        ..truckerId = 't1'
        ..fromWoreda = from
        ..toWoreda = 'Addis Ababa'
        ..departureDate = today.add(Duration(days: inDays))
        ..capacity = 50
        ..remainingQuintals = 18
        ..pricePerQuintal = 150
        ..status = TripStatus.open
        ..matchedListings = <String>[]
        ..createdAt = today
        ..updatedAt = today
        ..version = 1;
      when(
        () => trips.getOpenTrips(
          fromWoreda: any(named: 'fromWoreda'),
          toWoreda: any(named: 'toWoreda'),
        ),
      ).thenAnswer(
        // Meki: this week. Bichena: a later trip. Koka: none.
        (_) async => [trip('m1', 'Meki'), trip('b1', 'Bichena', inDays: 12)],
      );
      loaded([
        _listing(id: 'koka-1', farmerName: 'Kebede')..locationName = 'Koka',
        _listing(id: 'bichena', farmerName: 'Tolosa')..locationName = 'Bichena',
        _listing(id: 'koka-2', farmerName: 'Hawi')..locationName = 'Koka',
        _listing(id: 'meki', farmerName: 'Almaz')..locationName = 'Meki',
      ]);

      await tester.pumpWidget(buildTestWidget(tripBloc: tripBloc));
      await tester.pumpAndSettle();

      // Every listing stays (the buyer sees them all); the best transport
      // first, and the two Koka ones keep their order.
      expect(find.byType(ListingCard), findsNWidgets(4));
      final tops = [
        'Almaz, Meki',
        'Tolosa, Bichena',
        'Kebede, Koka',
        'Hawi, Koka',
      ].map((text) => tester.getTopLeft(find.text(text)).dy).toList();
      expect(tops, orderedEquals([...tops]..sort()));
      expect(
        find.text(
          "Every farmer's listing, sorted by the best match for Addis Ababa.",
        ),
        findsOneWidget,
      );
    });

    testWidgets('a buyer without a place reads the transport match footnote',
        (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
        ),
      );
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(
        find.text(
          "Every farmer's listing, sorted by the best transport match.",
        ),
        findsOneWidget,
      );
    });

    testWidgets('a farmer reads no trips for the cards', (tester) async {
      final tripBloc = MockTripListingBloc();
      final trips = MockTripListingRepository();
      when(() => tripBloc.repository).thenReturn(trips);
      when(() => tripBloc.state).thenReturn(const TripListingInitial());
      when(() => tripBloc.stream).thenAnswer((_) => const Stream.empty());
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget(tripBloc: tripBloc));
      await tester.pumpAndSettle();

      verifyNever(
        () => trips.getOpenTrips(
          fromWoreda: any(named: 'fromWoreda'),
          toWoreda: any(named: 'toWoreda'),
        ),
      );
      expect(find.text('No trip yet'), findsNothing);
    });

    testWidgets('buyer browse has no My Crops privacy note', (tester) async {
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text(privacyNote), findsNothing);
    });

    testWidgets('hides the rating when the seller has none', (tester) async {
      loaded([_listing()]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.star), findsNothing);
      expect(find.text('Escrow'), findsNothing);
      expect(find.text('Abebe'), findsNothing);
      expect(find.textContaining('Abebe'), findsOneWidget);
    });

    testWidgets('fetches each seller aggregate once for the whole list',
        (tester) async {
      loaded([
        _listing(id: 'l1'),
        _listing(id: 'l2'),
        _listing(id: 'l3', farmerId: 'farmer-2', farmerName: 'Kebede'),
      ]);

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      verify(() => ratingRepo.getRatingSummary('farmer-1')).called(1);
      verify(() => ratingRepo.getRatingSummary('farmer-2')).called(1);
    });
  });

  group('BrowseListingsScreen load errors (SIM-23)', () {
    Future<AppLocalizations> pumpError(
      WidgetTester tester,
      AppErrorCode code,
    ) async {
      final state = CropListingError(message: 'x', code: code);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      return AppLocalizations.delegate.load(const Locale('en'));
    }

    testWidgets('a 403 says no permission, not waiting for network',
        (tester) async {
      final l10n = await pumpError(tester, AppErrorCode.forbidden);
      expect(find.text(l10n.errorNoPermission), findsOneWidget);
      expect(find.text(l10n.dataWillSyncWhenOnline), findsNothing);
    });

    testWidgets('a connectivity failure keeps the offline copy',
        (tester) async {
      final l10n = await pumpError(tester, AppErrorCode.network);
      expect(find.text(l10n.dataWillSyncWhenOnline), findsOneWidget);
    });

    testWidgets('a TLS failure names the secure connection', (tester) async {
      final l10n = await pumpError(tester, AppErrorCode.secureConnection);
      expect(find.text(l10n.errorSecureConnectionFailed), findsOneWidget);
      expect(find.text(l10n.dataWillSyncWhenOnline), findsNothing);
    });
  });

  group('BrowseListingsScreen crop names (SIM-22)', () {
    testWidgets('card title is the localized crop name, not the stored key',
        (tester) async {
      final state = CropListingsLoaded(listings: [_listing()]);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Market board: "Teff · 10 quintals left".
      expect(
        find.descendant(
          of: find.byType(ListingCard),
          matching: find.text('Teff · 10 quintals left'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byType(ListingCard),
          matching: find.textContaining('teff'),
        ),
        findsNothing,
      );
    });

    testWidgets('Amharic shows the Amharic crop name on the card',
        (tester) async {
      final state = CropListingsLoaded(listings: [_listing()]);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
      final am = await AppLocalizations.delegate.load(const Locale('am'));

      await tester.pumpWidget(buildTestWidget(locale: const Locale('am')));
      await tester.pumpAndSettle();

      final inCard = find.byType(ListingCard);
      expect(
        find.descendant(
          of: inCard,
          matching: find.textContaining(am.cropTypeTeff),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(of: inCard, matching: find.textContaining('teff')),
        findsNothing,
      );
      expect(
        find.descendant(of: inCard, matching: find.textContaining('Teff')),
        findsNothing,
      );
    });
  });

  group('BrowseListingsScreen showHeader: false', () {
    setUp(() {
      when(() => cropListingBloc.state).thenReturn(
        const CropListingsLoaded(listings: []),
      );
      when(() => cropListingBloc.stream).thenAnswer(
        (_) => Stream.value(const CropListingsLoaded(listings: [])),
      );
    });

    testWidgets('hides the page header when showHeader is false',
        (tester) async {
      await tester.pumpWidget(
        buildTestWidget(showHeader: false, embedded: true),
      );
      await tester.pumpAndSettle();

      expect(find.byType(FwPageHeader), findsNothing);
    });

    testWidgets('shows compact search bar when showHeader is false',
        (tester) async {
      await tester.pumpWidget(
        buildTestWidget(showHeader: false, embedded: true),
      );
      await tester.pumpAndSettle();

      // The compact search bar renders a TextField with the search hint.
      expect(find.byType(TextField), findsWidgets);
      expect(find.text('Search crops, farmers...'), findsOneWidget);
    });

    testWidgets('shows the page header by default (showHeader: true)',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byType(FwPageHeader), findsOneWidget);
      // The search box waits behind the header's button (Market board).
      expect(find.text('Search crops, farmers...'), findsNothing);
      await tester.tap(find.byIcon(Icons.search));
      await tester.pumpAndSettle();
      expect(find.text('Search crops, farmers...'), findsOneWidget);
    });

    testWidgets(
        'does not show compact search bar in ownerMode when showHeader is false',
        (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _testTokens,
          role: UserRole.farmer,
        ),
      );

      await tester.pumpWidget(
        buildTestWidget(showHeader: false, ownerMode: true, embedded: true),
      );
      await tester.pumpAndSettle();

      // No header and no compact bar in ownerMode
      expect(find.byType(FwPageHeader), findsNothing);
      expect(find.text('Search crops, farmers...'), findsNothing);
    });
  });

  group('BrowseListingsScreen search (SIM-51)', () {
    late AppLocalizations l10n;

    setUp(() async {
      l10n = await AppLocalizations.delegate.load(const Locale('en'));
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
        ),
      );
    });

    void emptyLoaded() {
      const state = CropListingsLoaded(listings: []);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
    }

    /// Opens the search box and price range behind the header's button.
    Future<void> openFilters(WidgetTester tester) async {
      await tester.tap(find.byIcon(Icons.search).first);
      await tester.pumpAndSettle();
    }

    /// Types into the search box and lets the 300 ms debounce fire.
    Future<void> search(WidgetTester tester, String text) async {
      await tester.enterText(find.byType(TextField).first, text);
      await tester.pump(const Duration(milliseconds: 350));
    }

    testWidgets('the header search box sends the text as searchQuery',
        (tester) async {
      emptyLoaded();
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await openFilters(tester);

      await search(tester, 'Abebe');

      verify(
        () =>
            cropListingBloc.add(const LoadActiveListings(searchQuery: 'Abebe')),
      ).called(1);
    });

    testWidgets('the compact search box sends the text as searchQuery',
        (tester) async {
      emptyLoaded();
      await tester.pumpWidget(
        buildTestWidget(showHeader: false, embedded: true),
      );
      await tester.pumpAndSettle();

      await search(tester, 'teff');

      verify(
        () =>
            cropListingBloc.add(const LoadActiveListings(searchQuery: 'teff')),
      ).called(1);
    });

    testWidgets('a search with no hits says so, not that we are offline',
        (tester) async {
      emptyLoaded();
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await openFilters(tester);

      await search(tester, 'Abebe');
      await tester.pumpAndSettle();

      expect(find.text(l10n.noListingsMatchFilters), findsOneWidget);
      expect(find.text(l10n.noListingsMatchFiltersHint), findsOneWidget);
      expect(find.text(l10n.clearFilters), findsOneWidget);
      expect(find.text(l10n.dataWillSyncWhenOnline), findsNothing);
      expect(find.text(l10n.noCropListings), findsNothing);
    });

    testWidgets('Clear filters empties the box and reloads everything',
        (tester) async {
      emptyLoaded();
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await openFilters(tester);
      await search(tester, 'Abebe');
      await tester.pumpAndSettle();
      clearInteractions(cropListingBloc);

      await tester.tap(find.text(l10n.clearFilters));
      await tester.pumpAndSettle();

      final box = tester.widget<TextField>(find.byType(TextField).first);
      expect(box.controller!.text, isEmpty);
      verify(() => cropListingBloc.add(const LoadActiveListings())).called(1);
      expect(find.text(l10n.noCropListings), findsOneWidget);
      expect(find.text(l10n.clearFilters), findsNothing);
    });

    testWidgets('an empty list with no filter keeps the original empty state',
        (tester) async {
      emptyLoaded();
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text(l10n.noCropListings), findsOneWidget);
      expect(find.text(l10n.noListingsMatchFilters), findsNothing);
      expect(find.text(l10n.clearFilters), findsNothing);
    });

    // MyCrops board: the farmer filters their own listings with the
    // Active / Reserved / Sold / Drafts switch, with counts, on the phone
    // (no reload).
    testWidgets('owner mode: the status switch counts and filters the listings',
        (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _testTokens,
          role: UserRole.farmer,
        ),
      );
      final active = _listing(id: 'a', farmerId: 'farmer-001');
      final reserved = _listing(id: 'r', farmerId: 'farmer-001')
        ..cropType = 'wheat'
        ..status = ListingStatus.reserved;
      final state = CropListingsLoaded(listings: [active, reserved]);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget(ownerMode: true));
      await tester.pumpAndSettle();

      expect(find.byType(FwSegmented<ListingStatus>), findsOneWidget);
      expect(find.text('Active · 1'), findsOneWidget);
      expect(find.text('Reserved · 1'), findsOneWidget);
      expect(find.text('Sold'), findsOneWidget);
      expect(find.text('Drafts'), findsOneWidget);
      expect(find.text('Expired'), findsNothing);
      // Active is the first segment: only the active listing shows, and no
      // crop chips in My Crops.
      expect(find.text('Teff'), findsOneWidget); // the card title only
      expect(find.text('Wheat'), findsNothing);

      await tester.tap(find.text('Reserved · 1'));
      await tester.pumpAndSettle();
      expect(find.text('Wheat'), findsOneWidget);
      expect(find.text('Teff'), findsNothing);
      verifyNever(
        () => cropListingBloc.add(any(that: isA<FilterCropListings>())),
      );

      await tester.tap(find.text('Sold'));
      await tester.pumpAndSettle();
      expect(find.text(l10n.noListingsMatchFilters), findsOneWidget);
      expect(find.text(l10n.clearFilters), findsNothing);

      await tester.tap(find.text('Active · 1'));
      await tester.pumpAndSettle();
      expect(find.text('Teff'), findsOneWidget);
    });

    testWidgets('Retry keeps the visible search', (tester) async {
      const state = CropListingError(message: 'x', code: AppErrorCode.network);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await openFilters(tester);
      await search(tester, 'abebe');
      clearInteractions(cropListingBloc);

      await tester.tap(find.text(l10n.retry));
      await tester.pump();

      verify(
        () =>
            cropListingBloc.add(const LoadActiveListings(searchQuery: 'abebe')),
      ).called(1);
    });
  });
}

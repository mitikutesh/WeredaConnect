import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockTripListingBloc extends Mock implements TripListingBloc {}

class _MockTripRepository extends Mock implements BaseTripListingRepository {}

class _MockCropListingBloc extends Mock implements CropListingBloc {}

class _MockCropRepository extends Mock implements BaseCropListingRepository {}

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

class _FakeCropListingEvent extends Fake implements CropListingEvent {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// B-2: a buyer who finds a trip can use it. Find crops on this route opens
/// the market at the truck's starting place with the truck chosen, so a
/// listing opens with Place Order + Transport on that truck. Farmers and
/// other truckers only read the trip; the owner keeps the owner actions.
void main() {
  late _MockAuthBloc authBloc;
  late _MockTripListingBloc tripBloc;
  late _MockCropListingBloc cropBloc;
  late _MockCropRepository cropRepo;

  // Today in Ethiopia, the day after and the day before.
  final today = startOfDayInEthiopia(DateTime.now().toUtc());
  Trip? lastTrip;

  Trip trip({DateTime? departure, double? remaining}) => lastTrip = Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Trucker Tesfaye'
    ..truckerPhone = ''
    ..fromWoreda = 'Adama'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = departure ?? today.add(const Duration(days: 2))
    ..capacity = 50
    ..remainingQuintals = remaining
    ..pricePerQuintal = 100
    ..status = TripStatus.open
    ..matchedListings = <String>[]
    ..createdAt = DateTime.utc(2026, 10, 20)
    ..updatedAt = DateTime.utc(2026, 10, 20)
    ..version = 1;

  CropListing listing(String id, String place) => CropListing()
    ..listingId = id
    ..farmerId = 'farmer-$id'
    ..farmerName = 'Farmer $id'
    ..cropType = 'teff'
    ..quantity = 30
    ..pricePerKg = 80
    ..location = (LatLng()
      ..latitude = 8.54
      ..longitude = 39.27)
    ..locationName = place
    ..description = ''
    ..status = ListingStatus.active
    ..createdAt = DateTime.now()
    ..updatedAt = DateTime.now()
    ..version = 1;

  setUpAll(() => registerFallbackValue(_FakeCropListingEvent()));

  setUp(() {
    authBloc = _MockAuthBloc();
    tripBloc = _MockTripListingBloc();
    cropBloc = _MockCropListingBloc();
    cropRepo = _MockCropRepository();
    final tripRepo = _MockTripRepository();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripBloc.state).thenReturn(const TripListingInitial());
    when(() => tripBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripBloc.repository).thenReturn(tripRepo);
    when(() => tripRepo.getOpenTrips()).thenAnswer((_) async => const []);
    // The listing reads the truck again by id; here it has not changed.
    when(() => tripRepo.getTripById(any())).thenAnswer((_) async => lastTrip);
    when(() => cropBloc.repository).thenReturn(cropRepo);
    when(() => cropBloc.state).thenReturn(const CropListingInitial());
    when(() => cropBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => cropRepo.getActiveListings(
        cropTypeFilter: any(named: 'cropTypeFilter'),
        searchQuery: any(named: 'searchQuery'),
        priceMin: any(named: 'priceMin'),
        priceMax: any(named: 'priceMax'),
      ),
    ).thenAnswer(
      (_) async => [listing('at-adama', 'Adama'), listing('at-meki', 'Meki')],
    );
  });

  void signIn(String userId, UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: userId,
        tokens: _tokens,
        role: role,
        woreda: 'Merkato',
      ),
    );
  }

  Widget screen(Trip trip) {
    final ratings = _MockRatingRepository();
    when(() => ratings.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<TripListingBloc>.value(value: tripBloc),
        BlocProvider<CropListingBloc>.value(value: cropBloc),
        BlocProvider<RatingBloc>(
          create: (_) => RatingBloc(repository: ratings),
        ),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: TripDetailScreen(trip: trip),
      ),
    );
  }

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  const findCrops = 'Find crops on this route';

  group('who gets the action', () {
    testWidgets('a buyer gets it, with where the crops are', (tester) async {
      tallView(tester);
      signIn('buyer-001', UserRole.buyer);

      await tester.pumpWidget(screen(trip()));
      await tester.pumpAndSettle();

      expect(find.text(findCrops), findsOneWidget);
      expect(
        find.text('Crops listed in Adama. Order one with this truck.'),
        findsOneWidget,
      );
    });

    testWidgets('a farmer does not', (tester) async {
      tallView(tester);
      signIn('farmer-001', UserRole.farmer);

      await tester.pumpWidget(screen(trip()));
      await tester.pumpAndSettle();

      expect(find.text(findCrops), findsNothing);
    });

    testWidgets('another trucker does not', (tester) async {
      tallView(tester);
      signIn('trucker-999', UserRole.trucker);

      await tester.pumpWidget(screen(trip()));
      await tester.pumpAndSettle();

      expect(find.text(findCrops), findsNothing);
    });

    testWidgets('the owner keeps the owner actions instead', (tester) async {
      tallView(tester);
      signIn('trucker-001', UserRole.trucker);

      await tester.pumpWidget(screen(trip()));
      await tester.pumpAndSettle();

      expect(find.text(findCrops), findsNothing);
      expect(find.text('Cancel Trip'), findsOneWidget);
    });
  });

  group('a truck that cannot take an order: the button is off, saying why', () {
    for (final (label, build, message) in [
      (
        'its day is over',
        () => trip(departure: today.subtract(const Duration(days: 1))),
        'This truck already left. Choose another truck.',
      ),
      (
        'it is full',
        () => trip(remaining: 0),
        'This truck has no room left for this order. Choose another truck.',
      ),
      (
        'it is no longer open',
        () => trip()..status = TripStatus.matched,
        'That truck is no longer available. Choose again.',
      ),
    ]) {
      testWidgets(label, (tester) async {
        tallView(tester);
        signIn('buyer-001', UserRole.buyer);

        await tester.pumpWidget(screen(build()));
        await tester.pumpAndSettle();

        final button = tester.widget<FilledButton>(
          find.byKey(const Key('trip_detail_find_crops')),
        );
        expect(button.onPressed, isNull);
        expect(find.text(message), findsOneWidget);
        expect(
          find.text('Crops listed in Adama. Order one with this truck.'),
          findsNothing,
        );

        await tester.tap(find.text(findCrops));
        await tester.pumpAndSettle();
        expect(find.byType(BrowseListingsScreen), findsNothing);
        expect(find.byType(SnackBar), findsNothing);
      });
    }
  });

  // L-7: the header pill reads as the My Trips card for a trip whose day
  // passed.
  group('header pill', () {
    testWidgets("the trucker's own trip whose day passed reads Date passed",
        (tester) async {
      tallView(tester);
      signIn('trucker-001', UserRole.trucker);

      await tester.pumpWidget(
        screen(trip(departure: today.subtract(const Duration(days: 3)))),
      );
      await tester.pumpAndSettle();

      expect(find.text('Date passed'), findsOneWidget);
    });

    testWidgets('a trip still ahead shows no pill while open', (tester) async {
      tallView(tester);
      signIn('trucker-001', UserRole.trucker);

      await tester.pumpWidget(screen(trip()));
      await tester.pumpAndSettle();

      expect(find.text('Date passed'), findsNothing);
      expect(find.text('Open'), findsNothing);
    });

    testWidgets('a delivered trip keeps its status', (tester) async {
      tallView(tester);
      signIn('trucker-001', UserRole.trucker);

      await tester.pumpWidget(
        screen(
          trip(departure: today.subtract(const Duration(days: 3)))
            ..status = TripStatus.delivered,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Date passed'), findsNothing);
      expect(find.text('Delivered'), findsOneWidget);
    });
  });

  testWidgets(
      'opens the crops at the starting place, a listing with the truck chosen',
      (tester) async {
    tallView(tester);
    signIn('buyer-001', UserRole.buyer);
    final truck = trip();

    await tester.pumpWidget(screen(truck));
    await tester.pumpAndSettle();
    await tester.tap(find.text(findCrops));
    await tester.pumpAndSettle();

    final market = tester.widget<BrowseListingsScreen>(
      find.byType(BrowseListingsScreen),
    );
    expect(market.originTrip, same(truck));
    expect(find.text('Crops in Adama'), findsOneWidget);
    // Only the listing where the truck starts; Meki is not on the route.
    expect(find.text('Farmer at-adama, Adama'), findsOneWidget);
    expect(find.textContaining('Farmer at-meki'), findsNothing);
    expect(
      find.text('Crops listed in Adama, where this truck starts. '
          'Order one with transport to book this truck.'),
      findsOneWidget,
    );
    // The route's own bloc served the list, not the Market tab's.
    verifyNever(() => cropBloc.add(any()));

    await tester.tap(find.text('Farmer at-adama, Adama'));
    await tester.pumpAndSettle();

    final detail = tester.widget<CropListingDetailScreen>(
      find.byType(CropListingDetailScreen),
    );
    expect(detail.preselectedTrip, same(truck));
    expect(detail.deliveryWoreda, 'Addis Ababa');
    expect(find.text('Place Order + Transport'), findsOneWidget);
  });

  testWidgets('nothing listed where the truck starts says so', (tester) async {
    tallView(tester);
    signIn('buyer-001', UserRole.buyer);
    when(
      () => cropRepo.getActiveListings(
        cropTypeFilter: any(named: 'cropTypeFilter'),
        searchQuery: any(named: 'searchQuery'),
        priceMin: any(named: 'priceMin'),
        priceMax: any(named: 'priceMax'),
      ),
    ).thenAnswer((_) async => [listing('at-meki', 'Meki')]);

    await tester.pumpWidget(screen(trip()));
    await tester.pumpAndSettle();
    await tester.tap(find.text(findCrops));
    await tester.pumpAndSettle();

    expect(find.text('No crops listed in Adama right now'), findsOneWidget);
    expect(
      find.text('New listings show here as farmers add them.'),
      findsOneWidget,
    );
    expect(find.textContaining('Farmer at-meki'), findsNothing);
  });
}

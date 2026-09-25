import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

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
  }) {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
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

  group('BrowseListingsScreen showHeader: false', () {
    setUp(() {
      when(() => cropListingBloc.state).thenReturn(
        const CropListingsLoaded(listings: []),
      );
      when(() => cropListingBloc.stream).thenAnswer(
        (_) => Stream.value(const CropListingsLoaded(listings: [])),
      );
    });

    testWidgets('hides GreenHeader when showHeader is false', (tester) async {
      await tester.pumpWidget(
        buildTestWidget(showHeader: false, embedded: true),
      );
      await tester.pumpAndSettle();

      expect(find.byType(GreenHeader), findsNothing);
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

    testWidgets('shows GreenHeader by default (showHeader: true)',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byType(GreenHeader), findsOneWidget);
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
      expect(find.byType(GreenHeader), findsNothing);
      expect(find.text('Search crops, farmers...'), findsNothing);
    });
  });
}

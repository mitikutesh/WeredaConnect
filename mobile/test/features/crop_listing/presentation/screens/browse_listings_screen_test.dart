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
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';

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
    Locale? locale,
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

      expect(find.widgetWithText(ListingCard, 'Teff'), findsOneWidget);
      expect(find.widgetWithText(ListingCard, 'teff'), findsNothing);
    });

    testWidgets('Amharic shows the Amharic crop name on the card',
        (tester) async {
      final state = CropListingsLoaded(listings: [_listing()]);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
      final am = await AppLocalizations.delegate.load(const Locale('am'));

      await tester.pumpWidget(buildTestWidget(locale: const Locale('am')));
      await tester.pumpAndSettle();

      expect(find.widgetWithText(ListingCard, am.cropTypeTeff), findsOneWidget);
      expect(find.widgetWithText(ListingCard, 'teff'), findsNothing);
      expect(find.widgetWithText(ListingCard, 'Teff'), findsNothing);
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

    testWidgets('owner mode: a crop chip with no hits shows the no-match state',
        (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _testTokens,
          role: UserRole.farmer,
        ),
      );
      emptyLoaded();
      await tester.pumpWidget(buildTestWidget(ownerMode: true));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Teff'));
      await tester.pumpAndSettle();

      verify(
        () => cropListingBloc.add(
          const FilterCropListings(farmerId: 'farmer-001', cropType: 'teff'),
        ),
      ).called(1);
      expect(find.text(l10n.noListingsMatchFilters), findsOneWidget);
      expect(find.text(l10n.clearFilters), findsOneWidget);
    });

    testWidgets('Retry keeps the visible search', (tester) async {
      const state = CropListingError(message: 'x', code: AppErrorCode.network);
      when(() => cropListingBloc.state).thenReturn(state);
      when(() => cropListingBloc.stream).thenAnswer((_) => Stream.value(state));
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
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

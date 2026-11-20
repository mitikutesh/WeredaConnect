import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeBuyerIntentEvent extends Fake implements BuyerIntentEvent {}

class FakeBuyerIntentState extends Fake implements BuyerIntentState {}

class FakeCropListingEvent extends Fake implements CropListingEvent {}

class FakeCropListingState extends Fake implements CropListingState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

void main() {
  late MockAuthBloc authBloc;
  late MockBuyerIntentBloc buyerIntentBloc;
  late MockCropListingBloc cropListingBloc;
  late RatingBloc ratingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeBuyerIntentEvent());
    registerFallbackValue(FakeBuyerIntentState());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeCropListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    buyerIntentBloc = MockBuyerIntentBloc();
    cropListingBloc = MockCropListingBloc();
    final ratingRepo = MockRatingRepository();
    when(() => ratingRepo.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
    ratingBloc = RatingBloc(repository: ratingRepo);

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _testTokens,
        role: UserRole.buyer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentInitial());
    when(() => buyerIntentBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropListingBloc.state).thenReturn(
      const CropListingsLoaded(listings: []),
    );
    when(() => cropListingBloc.stream).thenAnswer(
      (_) => Stream.value(const CropListingsLoaded(listings: [])),
    );
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<BuyerIntentBloc>.value(value: buyerIntentBloc),
        BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        BlocProvider<RatingBloc>.value(value: ratingBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(body: FindCropsScreen()),
      ),
    );
  }

  group('FindCropsScreen', () {
    testWidgets('renders Browse and My Requests tab labels', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Browse'), findsOneWidget);
      expect(find.text('My Requests'), findsOneWidget);
    });

    testWidgets('the header asks for the buyer sketch under its buttons',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      final header = tester.widget<FwPageHeader>(find.byType(FwPageHeader));
      expect(header.sketchRole, UserRole.buyer);
      expect(header.sketchBelowTrailing, isTrue);
      expect(header.trailing, isNotNull);
    });

    testWidgets('the tabs are the board segmented switch (Market board)',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byType(FwSegmented<int>), findsOneWidget);
      expect(find.byType(TabBar), findsNothing);
      // Both bodies stay alive behind the switch, the hidden one off stage.
      expect(find.byType(Offstage), findsWidgets);
    });

    testWidgets(
        'one page header: the embedded BrowseListingsScreen does not add '
        'its own', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byType(FwPageHeader), findsOneWidget);
      expect(find.text('Find Crops'), findsOneWidget);
    });

    testWidgets(
        'search and price filters stay behind the header button (board)',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Search crops, farmers...'), findsNothing);
      expect(find.text('Min'), findsNothing);

      await tester.tap(find.byIcon(Icons.search));
      await tester.pumpAndSettle();
      expect(find.text('Search crops, farmers...'), findsOneWidget);
      expect(find.text('Min'), findsOneWidget);
      expect(find.text('Max'), findsOneWidget);

      await tester.tap(find.byIcon(Icons.search).first);
      await tester.pumpAndSettle();
      expect(find.text('Search crops, farmers...'), findsNothing);
    });

    testWidgets('My Requests counts the active intents', (tester) async {
      final intent = BuyerIntent()
        ..intentId = 'intent-001'
        ..buyerId = 'buyer-001'
        ..cropType = 'wheat'
        ..quantityNeeded = 100
        ..maxPricePerKg = 30
        ..deliveryWoreda = 'Adama'
        ..status = BuyerIntentStatus.active
        ..createdAt = DateTime.utc(2026)
        ..updatedAt = DateTime.utc(2026)
        ..version = 1;
      final state = ActiveIntentsLoaded(intents: [intent]);
      when(() => buyerIntentBloc.state).thenReturn(state);
      when(() => buyerIntentBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('My Requests · 1'), findsOneWidget);
    });

    testWidgets('My Requests tab shows loading indicator when loading',
        (tester) async {
      when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentLoading());
      when(() => buyerIntentBloc.stream).thenAnswer(
        (_) => Stream.value(const BuyerIntentLoading()),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pump();

      // Switch to the My Requests tab and advance past the tab animation.
      // pumpAndSettle is not used here because CircularProgressIndicator
      // animates indefinitely and would cause it to time out.
      await tester.tap(find.text('My Requests'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 500));

      expect(find.byType(CircularProgressIndicator), findsWidgets);
    });

    testWidgets('My Requests tab shows empty state when no intents',
        (tester) async {
      when(() => buyerIntentBloc.state).thenReturn(
        const ActiveIntentsLoaded(intents: []),
      );
      when(() => buyerIntentBloc.stream).thenAnswer(
        (_) => Stream.value(const ActiveIntentsLoaded(intents: [])),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Switch to the My Requests tab
      await tester.tap(find.text('My Requests'));
      await tester.pumpAndSettle();

      expect(find.text('No matches yet'), findsOneWidget);
    });

    testWidgets('My Requests list ends clear of the floating create button',
        (tester) async {
      // LT-01: on web this screen is the buyer's tab under the shell FAB.
      final intent = BuyerIntent()
        ..intentId = 'intent-001'
        ..buyerId = 'buyer-001'
        ..cropType = 'wheat'
        ..quantityNeeded = 100
        ..maxPricePerKg = 30
        ..deliveryWoreda = 'Adama'
        ..status = BuyerIntentStatus.active
        ..createdAt = DateTime.utc(2026)
        ..updatedAt = DateTime.utc(2026)
        ..version = 1;
      final state = ActiveIntentsLoaded(intents: [intent]);
      when(() => buyerIntentBloc.state).thenReturn(state);
      when(() => buyerIntentBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await tester.tap(find.text('My Requests · 1'));
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
  });

  // MarketM board.
  group('page M', () {
    BuyerIntent intent() => BuyerIntent()
      ..intentId = 'intent-001'
      ..buyerId = 'buyer-001'
      ..cropType = 'wheat'
      ..quantityNeeded = 20
      ..maxPricePerKg = 45
      ..deliveryWoreda = 'Addis Ababa'
      ..matchCount = 3
      ..status = BuyerIntentStatus.active
      ..createdAt = DateTime.utc(2026)
      ..updatedAt = DateTime.utc(2026)
      ..version = 1;

    testWidgets('the eyebrow reads "Deliver to" with the buyer\'s place',
        (tester) async {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
          woreda: 'Addis Ababa',
        ),
      );
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Deliver to Addis Ababa'), findsOneWidget);
      expect(find.byIcon(Icons.place_outlined), findsOneWidget);
      expect(find.text('Find Crops'), findsOneWidget);
    });

    testWidgets('a request reads kilograms first, its matches as a pill',
        (tester) async {
      final state = ActiveIntentsLoaded(intents: [intent()]);
      when(() => buyerIntentBloc.state).thenReturn(state);
      when(() => buyerIntentBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await tester.tap(find.text('My Requests · 1'));
      await tester.pumpAndSettle();

      expect(find.text('Wheat · 2,000 kg (20 quintals)'), findsOneWidget);
      expect(find.widgetWithText(FwPill, '3 matches found'), findsOneWidget);
      // The link to add another request scrolls with the list.
      expect(find.text('New Buying Intent'), findsOneWidget);
    });

    testWidgets('search hides with the Browse segment', (tester) async {
      final state = ActiveIntentsLoaded(intents: [intent()]);
      when(() => buyerIntentBloc.state).thenReturn(state);
      when(() => buyerIntentBloc.stream).thenAnswer((_) => Stream.value(state));

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      expect(find.byIcon(Icons.search), findsOneWidget);

      await tester.tap(find.text('My Requests · 1'));
      await tester.pumpAndSettle();
      expect(find.byIcon(Icons.search), findsNothing);
    });
  });
}

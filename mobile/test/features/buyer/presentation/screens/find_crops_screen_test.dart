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
import 'package:wereda_connect/shared/widgets/green_header.dart';

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

    testWidgets(
        'only one GreenHeader in the tree — embedded BrowseListingsScreen '
        'does not render its own header', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // FindCropsScreen owns exactly one GreenHeader.
      // BrowseListingsScreen(showHeader: false) must not add a second one.
      expect(find.byType(GreenHeader), findsOneWidget);
    });

    testWidgets('TabBarView uses NeverScrollableScrollPhysics', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(
        find.byWidgetPredicate(
          (w) => w is TabBarView && w.physics is NeverScrollableScrollPhysics,
        ),
        findsOneWidget,
      );
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
      await tester.tap(find.text('My Requests'));
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
}

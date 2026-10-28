import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

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
  late MockSyncExecutor syncExecutor;
  late MockTripListingBloc tripListingBloc;

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
    syncExecutor = MockSyncExecutor();
    tripListingBloc = MockTripListingBloc();

    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentInitial());
    when(() => buyerIntentBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropListingBloc.state).thenReturn(
      const CropListingsLoaded(listings: []),
    );
    when(() => cropListingBloc.stream).thenAnswer(
      (_) => Stream.value(const CropListingsLoaded(listings: [])),
    );
    when(() => syncExecutor.onSyncResult).thenAnswer(
      (_) => const Stream.empty(),
    );
    when(() => syncExecutor.isSyncing).thenReturn(false);
  });

  /// Tab indexes the dashboard asked the shell to switch to.
  late List<int> tabRequests;

  Widget buildHomeScreen(UserRole role) {
    tabRequests = [];
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: '${role.name}-001',
        tokens: _testTokens,
        role: role,
      ),
    );
    return Provider<SyncExecutor>.value(
      value: syncExecutor,
      child: MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<BuyerIntentBloc>.value(value: buyerIntentBloc),
          BlocProvider<CropListingBloc>.value(value: cropListingBloc),
          BlocProvider<TripListingBloc>.value(value: tripListingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: HomeScreen(isOnline: false, onNavigateToTab: tabRequests.add),
          ),
        ),
      ),
    );
  }

  /// Tall tablet surface (3-column grid) so every tile is on screen. The
  /// phone layout's header needs the messaging providers, so it's skipped.
  void useTallSurface(WidgetTester tester) {
    tester.view.physicalSize = const Size(800, 2000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  testWidgets('the page ends clear of the floating create button (LT-01)',
      (tester) async {
    useTallSurface(tester);
    await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
    await tester.pumpAndSettle();

    expect(
      find.byWidgetPredicate(
        (w) => w is SizedBox && w.height == Breakpoints.fabClearance,
      ),
      findsOneWidget,
    );
  });

  testWidgets('one At a Glance section replaces Overview + Quick Actions',
      (tester) async {
    useTallSurface(tester);
    await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
    await tester.pumpAndSettle();

    expect(find.text('At a Glance'), findsOneWidget);
    expect(find.text('Overview'), findsNothing);
    expect(find.text('Quick Actions'), findsNothing);
  });

  group('HomeScreen dashboard: buyer role', () {
    testWidgets('shows the buyer tiles', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.buyer));
      await tester.pumpAndSettle();

      for (final label in [
        'Find Crops',
        'Find Trips',
        'Active Orders',
        'Escrow',
        'Deliveries',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('My Crops'), findsNothing);
      expect(find.text('My Trips'), findsNothing);
    });

    testWidgets('Find Crops tile navigates to FindCropsScreen', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.buyer));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Find Crops'));
      await tester.pumpAndSettle();

      expect(find.byType(FindCropsScreen), findsOneWidget);
    });

    testWidgets('count tiles open their tabs', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.buyer));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Active Orders'));
      await tester.tap(find.text('Deliveries'));
      await tester.pump();

      // Buyer bar: Home · Market · Orders · Deliveries
      expect(tabRequests, [2, 3]);
    });
  });

  group('HomeScreen dashboard: farmer role', () {
    testWidgets('shows the farmer tiles, not buyer ones', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
      await tester.pumpAndSettle();

      for (final label in [
        'Sell Crops',
        'My Crops',
        'Pending Orders',
        'Escrow',
        'Deliveries',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('Find Crops'), findsNothing);
      expect(find.text('Find Trips'), findsNothing);
    });

    testWidgets('count tiles open their tabs', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Pending Orders'));
      await tester.tap(find.text('Deliveries'));
      await tester.pump();

      // Farmer bar: Home · My Crops · Orders · Deliveries
      expect(tabRequests, [2, 3]);
    });
  });

  group('HomeScreen dashboard: trucker role', () {
    testWidgets('shows the trucker tiles', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.trucker));
      await tester.pumpAndSettle();

      for (final label in [
        'My Trips',
        'In Transit',
        'Matched',
        'Deliveries',
        'My Orders',
        'My Payments',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('Find Crops'), findsNothing);
      expect(find.text('Sell Crops'), findsNothing);
    });

    testWidgets('tab tiles open their tabs', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.trucker));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Deliveries'));
      await tester.tap(find.text('My Payments'));
      await tester.pump();

      // Trucker bar: Home · My Trips · Deliveries · Payments
      expect(tabRequests, [2, 3]);
    });
  });
}

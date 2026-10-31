import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/place.dart';
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
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeBuyerIntentEvent extends Fake implements BuyerIntentEvent {}

class FakeBuyerIntentState extends Fake implements BuyerIntentState {}

class FakeCropListingEvent extends Fake implements CropListingEvent {}

class FakeCropListingState extends Fake implements CropListingState {}

class FakeInboxEvent extends Fake implements InboxEvent {}

class FakeInboxState extends Fake implements InboxState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

void main() {
  late MockAuthBloc authBloc;
  late MockBuyerIntentBloc buyerIntentBloc;
  late MockCropListingBloc cropListingBloc;
  late MockInboxBloc inboxBloc;
  late MockSyncExecutor syncExecutor;
  late MockTripListingBloc tripListingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeBuyerIntentEvent());
    registerFallbackValue(FakeBuyerIntentState());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeCropListingState());
    registerFallbackValue(FakeInboxEvent());
    registerFallbackValue(FakeInboxState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    buyerIntentBloc = MockBuyerIntentBloc();
    cropListingBloc = MockCropListingBloc();
    inboxBloc = MockInboxBloc();
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
    // The header's Messages button loads the inbox for its badge.
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => syncExecutor.onSyncResult).thenAnswer(
      (_) => const Stream.empty(),
    );
    when(() => syncExecutor.isSyncing).thenReturn(false);
    // Find Crops reads the open trips for its cards' transport line.
    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
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
          BlocProvider<InboxBloc>.value(value: inboxBloc),
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

  /// A tall surface so every tile is on screen; 800 wide renders the same
  /// board column centred by FwContentWidth, 390 the phone board.
  void useTallSurface(WidgetTester tester, {double width = 800}) {
    tester.view.physicalSize = Size(width, 2000);
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

  testWidgets('one AT A GLANCE section label replaces Overview + Quick Actions',
      (tester) async {
    useTallSurface(tester);
    await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
    await tester.pumpAndSettle();

    expect(find.text('AT A GLANCE'), findsOneWidget);
    expect(find.text('Overview'), findsNothing);
    expect(find.text('Quick Actions'), findsNothing);
  });

  testWidgets('the phone board renders the same column (header, tiles)',
      (tester) async {
    useTallSurface(tester, width: 390);
    await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
    await tester.pumpAndSettle();

    expect(find.text('Welcome back, '), findsOneWidget);
    expect(find.text('Synced'), findsNothing);
    expect(find.text('Offline'), findsOneWidget);
    expect(find.byIcon(Icons.notifications_outlined), findsOneWidget);
    expect(find.text('AT A GLANCE'), findsOneWidget);
    expect(find.text('My Crops'), findsOneWidget);
  });

  group('home place line', () {
    const places = [
      Place(
        id: 'gn-1',
        en: 'Mek’ī',
        display: 'Meki',
        am: 'መቂ',
        region: 'Oromia',
        alts: ['Meki', 'Meqi'],
      ),
      Place(id: 'gn-2', en: 'Adama'),
      Place(id: 'gn-3', en: 'Dire Dawa', region: 'Dire Dawa'),
    ];

    test('the woreda with its region when the place list knows it', () {
      expect(homePlaceLine('Meki', places, 'en'), 'Meki · Oromia');
      expect(homePlaceLine('Meki, Oromia', places, 'en'), 'Meki · Oromia');
      expect(homePlaceLine('Meqi', places, 'am'), 'መቂ · Oromia');
    });

    test('the woreda as stored when unknown or without a region', () {
      expect(homePlaceLine('Adama', places, 'en'), 'Adama');
      expect(homePlaceLine('Dire Dawa', places, 'en'), 'Dire Dawa');
      expect(homePlaceLine('Somewhere', places, 'en'), 'Somewhere');
      expect(homePlaceLine('', places, 'en'), '');
    });
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

      // Farmer · Home board: four tiles, My Crops first; selling is the
      // floating button, not a tile or a banner.
      for (final label in [
        'My Crops',
        'Pending Orders',
        'Escrow',
        'Deliveries',
      ]) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('Sell Crops'), findsNothing);
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

      // Trucker · Home board: three tiles, the trip card in the header.
      for (final label in ['My Trips', 'Matched', 'Escrow']) {
        expect(find.text(label), findsOneWidget, reason: label);
      }
      expect(find.text('In Transit'), findsNothing);
      expect(find.text('My Orders'), findsNothing);
      expect(find.text('My Payments'), findsNothing);
      expect(find.text('Find Crops'), findsNothing);
      expect(find.text('Sell Crops'), findsNothing);
      expect(find.text('Today'), findsOneWidget);
      expect(find.text('No active trip'), findsOneWidget);
      // The board's dark header has no icons: the bell, with Messages and
      // Profile in its sheet, is on the trucker's other tabs.
      expect(find.byIcon(Icons.notifications_outlined), findsNothing);
      expect(find.byIcon(Icons.chat_bubble_outline), findsNothing);
      expect(find.byIcon(Icons.person_outline), findsNothing);
    });

    testWidgets('tab tiles open their tabs', (tester) async {
      useTallSurface(tester);
      await tester.pumpWidget(buildHomeScreen(UserRole.trucker));
      await tester.pumpAndSettle();

      await tester.tap(find.text('My Trips'));
      await tester.tap(find.text('Escrow'));
      await tester.pump();

      // Trucker bar: Home · My Trips · Deliveries · Payments
      expect(tabRequests, [1, 3]);
    });
  });
}

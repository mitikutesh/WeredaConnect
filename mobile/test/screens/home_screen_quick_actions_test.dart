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
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

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
  });

  Widget buildHomeScreen(UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: role == UserRole.buyer ? 'buyer-001' : 'farmer-001',
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
        ],
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: HomeScreen(isOnline: false)),
        ),
      ),
    );
  }

  group('HomeScreen quick actions — buyer role', () {
    testWidgets('shows Find Crops quick action tile', (tester) async {
      await tester.pumpWidget(buildHomeScreen(UserRole.buyer));
      await tester.pumpAndSettle();

      expect(find.text('Find Crops'), findsOneWidget);
    });

    testWidgets('Find Crops tile navigates to FindCropsScreen', (tester) async {
      await tester.pumpWidget(buildHomeScreen(UserRole.buyer));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Find Crops'));
      await tester.pumpAndSettle();

      expect(find.byType(FindCropsScreen), findsOneWidget);
    });
  });

  group('HomeScreen quick actions — farmer role', () {
    testWidgets('does not show Find Crops quick action tile', (tester) async {
      await tester.pumpWidget(buildHomeScreen(UserRole.farmer));
      await tester.pumpAndSettle();

      // Farmer sees "My Crops", not "Find Crops"
      expect(find.text('Find Crops'), findsNothing);
      expect(find.text('My Crops'), findsOneWidget);
    });
  });
}

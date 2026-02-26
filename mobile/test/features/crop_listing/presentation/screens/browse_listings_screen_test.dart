import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeCropListingEvent extends Fake implements CropListingEvent {}

class FakeCropListingState extends Fake implements CropListingState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

void main() {
  late MockAuthBloc authBloc;
  late MockCropListingBloc cropListingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeCropListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    cropListingBloc = MockCropListingBloc();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(userId: 'farmer-001', tokens: _testTokens, role: UserRole.farmer),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<CropListingBloc>.value(value: cropListingBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(body: BrowseListingsScreen()),
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

      // Farmer mode: status filter "All" + crop type filter "All" = 2
      expect(find.text('All'), findsNWidgets(2));
      expect(find.text('Teff'), findsOneWidget);
      expect(find.text('Wheat'), findsOneWidget);

      // Status filter chips should be visible for farmer
      expect(find.text('Draft'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Reserved'), findsOneWidget);
    });
  });
}

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
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
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _testTokens,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropListingBloc.state).thenReturn(const CropListingInitial());
    when(() => cropListingBloc.stream).thenAnswer((_) => const Stream.empty());
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
        home: CreateCropListingScreen(),
      ),
    );
  }

  group('CreateCropListingScreen', () {
    testWidgets('renders form fields', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Verify crop type grid is rendered
      expect(find.text('Select Crop Type'), findsOneWidget);

      // Verify form fields
      expect(find.text('Quantity (quintals)'), findsOneWidget);
      expect(find.text('Price (ETB/kg)'), findsOneWidget);
      expect(find.text('Location'), findsOneWidget);
      expect(find.text('Description'), findsOneWidget);

      // Verify save button
      expect(find.text('Save'), findsOneWidget);

      // Verify audio note placeholder
      expect(find.text('Record Audio Note'), findsOneWidget);
    });

    testWidgets('shows validation errors on empty submit', (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Scroll to make Save button visible, then tap
      await tester.ensureVisible(find.text('Save'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      // Should show validation errors (matching l10n strings)
      expect(find.text('Please enter the quantity'), findsOneWidget);
      expect(find.text('Price is required'), findsOneWidget);
      expect(find.text('Please enter the location'), findsOneWidget);
    });
  });
}

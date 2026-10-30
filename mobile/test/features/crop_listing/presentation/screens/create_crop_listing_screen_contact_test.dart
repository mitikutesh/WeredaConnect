import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockCropListingBloc extends Mock implements CropListingBloc {}

class _MockGpsCaptureService extends Mock implements GpsCaptureService {}

class _FakeAuthEvent extends Fake implements AuthEvent {}

class _FakeAuthState extends Fake implements AuthState {}

class _FakeCropListingEvent extends Fake implements CropListingEvent {}

class _FakeCropListingState extends Fake implements CropListingState {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

const _contactInText = 'Phone numbers are not allowed here. Remove the '
    'number: contact opens after payment is held in escrow.';

/// FUP-49: the crop form refuses a phone number in Location or Description
/// before anything is queued (offline-first), and explains the server's
/// refusal on web.
void main() {
  late _MockAuthBloc authBloc;
  late _MockCropListingBloc cropBloc;
  late _MockGpsCaptureService gps;
  late StreamController<CropListingState> states;

  setUpAll(() {
    registerFallbackValue(_FakeAuthEvent());
    registerFallbackValue(_FakeAuthState());
    registerFallbackValue(_FakeCropListingEvent());
    registerFallbackValue(_FakeCropListingState());
  });

  setUp(() {
    authBloc = _MockAuthBloc();
    cropBloc = _MockCropListingBloc();
    gps = _MockGpsCaptureService();
    states = StreamController<CropListingState>.broadcast();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _tokens,
        role: UserRole.farmer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropBloc.state).thenReturn(const CropListingInitial());
    when(() => cropBloc.stream).thenAnswer((_) => states.stream);
    when(() => gps.captureCurrentPosition()).thenAnswer(
      (_) async => const GpsCaptureResult.success(
        latitude: 9.0,
        longitude: 38.7,
        accuracy: 10.0,
      ),
    );
  });

  tearDown(() => states.close());

  Widget app() => MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<CropListingBloc>.value(value: cropBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CreateCropListingScreen(gpsCaptureService: gps),
        ),
      );

  Future<void> fillAndSave(
    WidgetTester tester, {
    String location = 'Bishoftu',
    String description = '',
  }) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('create_listing_crop_teff')));
    await tester.enterText(
      find.byKey(const Key('create_listing_quantity')),
      '10',
    );
    await tester.enterText(
      find.byKey(const Key('create_listing_price')),
      '50',
    );
    await tester.enterText(
      find.descendant(
        of: find.byKey(const Key('create_listing_location')),
        matching: find.byType(TextFormField),
      ),
      location,
    );
    await tester.enterText(
      find.byKey(const Key('create_listing_description')),
      description,
    );
    await tester.pumpAndSettle();
    // The place suggestions hang below the location field, over the pinned
    // Save; a picker closes them, so close them here too.
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Save'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save'));
    // One frame: a save that passes shows a spinner that never settles.
    await tester.pump();
  }

  for (final description in [
    'White teff, call 0911 23 45 67',
    'ለበለጠ መረጃ 0911234567 ይደውሉ',
    'Bilbilaa +251 911 234 567 irratti bilbilaa',
  ]) {
    testWidgets('a number in Description blocks save: $description',
        (tester) async {
      await fillAndSave(tester, description: description);

      expect(find.text(_contactInText), findsOneWidget);
      verifyNever(() => cropBloc.add(any()));
    });
  }

  testWidgets('a number in Location blocks save', (tester) async {
    await fillAndSave(tester, location: 'Bishoftu 0712345678');

    expect(find.text(_contactInText), findsOneWidget);
    verifyNever(() => cropBloc.add(any()));
  });

  testWidgets('the long refusal text wraps instead of being clipped',
      (tester) async {
    await fillAndSave(tester, description: 'Call 0911234567');

    final error = tester.widget<Text>(find.text(_contactInText));
    expect(error.maxLines, 4);
  });

  testWidgets('prices, quantities and dates in Description still save',
      (tester) async {
    await fillAndSave(
      tester,
      description: 'Harvested 09-10-2018, 500 quintals at 1,200 ETB',
    );

    expect(find.text(_contactInText), findsNothing);
    final event = verify(() => cropBloc.add(captureAny())).captured.single
        as CreateCropListing;
    expect(
      event.description,
      'Harvested 09-10-2018, 500 quintals at 1,200 ETB',
    );
  });

  testWidgets('the server refusal (web) is explained', (tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();

    states.add(
      const CropListingError(
        message: 'CROP_CONTACT_IN_TEXT: Remove the phone number from woreda.',
        code: AppErrorCode.contactInText,
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.text(_contactInText), findsOneWidget);
  });
}

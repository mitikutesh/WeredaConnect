import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_picker.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';
import '../../../../helpers/crop_photo_fixtures.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockGpsCaptureService extends Mock implements GpsCaptureService {}

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
  late MockGpsCaptureService mockGpsService;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeCropListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    cropListingBloc = MockCropListingBloc();
    mockGpsService = MockGpsCaptureService();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _testTokens,
        role: UserRole.farmer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropListingBloc.state).thenReturn(const CropListingInitial());
    when(() => cropListingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => mockGpsService.captureCurrentPosition()).thenAnswer(
      (_) async => const GpsCaptureResult.success(
        latitude: 9.0,
        longitude: 38.7,
        accuracy: 10.0,
      ),
    );
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<CropListingBloc>.value(value: cropListingBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: CreateCropListingScreen(
          gpsCaptureService: mockGpsService,
          photoPicker: FakeCropPhotoPicker(),
        ),
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

      // Verify save button and the offline save note under it
      expect(find.text('Save'), findsOneWidget);
      expect(
        find.text('Saved on your phone first, sent when you have network.'),
        findsOneWidget,
      );

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

    testWidgets('shows GPS captured indicator when GPS succeeds',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.textContaining('GPS Captured'), findsOneWidget);
      expect(find.byIcon(Icons.check), findsOneWidget);
    });

    testWidgets('shows GPS unavailable indicator when GPS fails',
        (tester) async {
      when(() => mockGpsService.captureCurrentPosition()).thenAnswer(
        (_) async =>
            const GpsCaptureResult.unavailable('Location services disabled'),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('GPS unavailable'), findsOneWidget);
      expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);
      expect(find.byIcon(Icons.refresh), findsOneWidget);
    });
  });

  group('Fraud error handling', () {
    Widget buildFraudTestWidget(
      StreamController<CropListingState> controller,
    ) {
      // Override stream mock to use the given controller
      when(() => cropListingBloc.stream).thenAnswer((_) => controller.stream);

      return MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CreateCropListingScreen(
            gpsCaptureService: mockGpsService,
            photoPicker: FakeCropPhotoPicker(),
          ),
        ),
      );
    }

    testWidgets(
        'shows duplicate listing snackbar on FRAUD_DUPLICATE_LISTING error',
        (tester) async {
      final controller = StreamController<CropListingState>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(buildFraudTestWidget(controller));
      await tester.pumpAndSettle();

      // Emit fraud duplicate listing error from BLoC
      controller.add(
        const CropListingError(
          message: 'FRAUD_DUPLICATE_LISTING: A similar listing already exists.',
        ),
      );
      await tester.pump();
      await tester.pump();

      // Verify the localized fraud message is shown in a snackbar
      expect(
        find.text(
          'A similar listing already exists. '
          'Please update your existing listing instead.',
        ),
        findsOneWidget,
      );
    });

    testWidgets(
        'shows velocity exceeded snackbar on FRAUD_VELOCITY_EXCEEDED error',
        (tester) async {
      final controller = StreamController<CropListingState>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(buildFraudTestWidget(controller));
      await tester.pumpAndSettle();

      // Emit fraud velocity exceeded error from BLoC
      controller.add(
        const CropListingError(
          message: 'FRAUD_VELOCITY_EXCEEDED: Too many listings.',
        ),
      );
      await tester.pump();
      await tester.pump();

      // Verify the localized fraud message is shown in a snackbar with a
      // minutes countdown. The exact count depends on the wall clock, so
      // match the pattern instead of a fixed value.
      expect(
        find.textContaining(
          RegExp(
            r'Too many listings created\. '
            r'You can try again in \d+ minutes?\.',
          ),
        ),
        findsOneWidget,
      );
    });

    testWidgets('shows generic error for non-fraud errors', (tester) async {
      final controller = StreamController<CropListingState>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(buildFraudTestWidget(controller));
      await tester.pumpAndSettle();

      // Emit a generic error from BLoC (unknown code → generic localized message)
      controller.add(
        const CropListingError(message: 'Something went wrong'),
      );
      await tester.pump();
      await tester.pump();

      // Verify the localized generic error is shown (not the raw exception text,
      // and not a fraud-specific message).
      expect(find.text('An error occurred'), findsOneWidget);
      expect(find.text('Something went wrong'), findsNothing);
    });
  });

  group('Save button decoupled from shared bloc loading', () {
    Widget buildStreamWidget(StreamController<CropListingState> controller) {
      when(() => cropListingBloc.stream).thenAnswer((_) => controller.stream);
      return MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CreateCropListingScreen(
            gpsCaptureService: mockGpsService,
            photoPicker: FakeCropPhotoPicker(),
          ),
        ),
      );
    }

    testWidgets(
        'stays enabled when the shared bloc emits CropListingLoading from '
        'a sibling screen (not this submit)', (tester) async {
      final controller = StreamController<CropListingState>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(buildStreamWidget(controller));
      await tester.pumpAndSettle();

      // The still-mounted "My Crops" list re-dispatches LoadCropListings after
      // a create, driving the SHARED bloc into loading. This screen's Save
      // button must NOT gray out — it tracks only its own submission.
      controller.add(const CropListingLoading());
      await tester.pump();

      final save = find.widgetWithText(FilledButton, 'Save');
      expect(save, findsOneWidget);
      expect(tester.widget<FilledButton>(save).onPressed, isNotNull);
    });
  });

  group('Coordinates when GPS is unavailable (SIM-43)', () {
    setUp(() {
      when(() => mockGpsService.captureCurrentPosition()).thenAnswer(
        (_) async =>
            const GpsCaptureResult.unavailable('Location services disabled'),
      );
    });

    Future<void> fillForm(WidgetTester tester) async {
      await tester.tap(find.byKey(const Key('create_listing_crop_teff')));
      await addTestPhotos(tester);
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
        'Bishoftu',
      );
      await tester.pumpAndSettle();
    }

    Future<void> tapSave(WidgetTester tester) async {
      // The place suggestions hang below the location field, over the
      // pinned Save; a picker closes them, so close them here too.
      FocusManager.instance.primaryFocus?.unfocus();
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text('Save'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save'));
      await tester.pump();
    }

    testWidgets('uses the picked place centroid instead of 0,0',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await fillForm(tester);

      tester.widget<PlaceSelector>(find.byType(PlaceSelector)).onPlaceSelected!(
        const Place(id: 'gn-1', en: 'Bishoftu', lat: 8.75, lng: 38.98),
      );
      await tester.pump();
      await tapSave(tester);

      final event = verify(() => cropListingBloc.add(captureAny()))
          .captured
          .single as CreateCropListing;
      expect(event.latitude, 8.75);
      expect(event.longitude, 38.98);
      expect(event.placeId, 'gn-1');
    });

    testWidgets('blocks save with a clear message when no coordinates',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();
      await fillForm(tester);
      await tapSave(tester);

      verifyNever(() => cropListingBloc.add(any()));
      expect(
        find.text(
          'GPS is unavailable. Pick your location from the list or turn on '
          'GPS.',
        ),
        findsOneWidget,
      );
    });
  });

  group('Photos (crop-listing-api.md 1.6.0)', () {
    Widget buildWith(
      FakeCropPhotoPicker picker, {
      Stream<CropListingState>? states,
    }) {
      if (states != null) {
        when(() => cropListingBloc.stream).thenAnswer((_) => states);
      }
      return MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<CropListingBloc>.value(value: cropListingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CreateCropListingScreen(
            gpsCaptureService: mockGpsService,
            photoPicker: picker,
          ),
        ),
      );
    }

    Future<void> fillRest(WidgetTester tester) async {
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
        'Bishoftu',
      );
      await tester.pumpAndSettle();
      FocusManager.instance.primaryFocus?.unfocus();
      await tester.pumpAndSettle();
    }

    Future<void> tapSave(WidgetTester tester) async {
      await tester.ensureVisible(find.text('Save'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save'));
      await tester.pump();
    }

    testWidgets('shows the required section with both sources', (tester) async {
      await tester.pumpWidget(buildWith(FakeCropPhotoPicker()));
      await tester.pumpAndSettle();

      expect(find.text('Photos of your crop'), findsOneWidget);
      expect(find.text('0 of 4'), findsOneWidget);
      expect(find.text('Take photo'), findsOneWidget);
      expect(find.text('From gallery'), findsOneWidget);
    });

    testWidgets('refuses to save without photos', (tester) async {
      await tester.pumpWidget(buildWith(FakeCropPhotoPicker()));
      await tester.pumpAndSettle();
      await fillRest(tester);
      await tapSave(tester);
      await tester.pumpAndSettle();

      verifyNever(() => cropListingBloc.add(any()));
      expect(find.text('Add at least 2 photos of your crop.'), findsOneWidget);
    });

    testWidgets('one photo is not enough; the error goes at the second',
        (tester) async {
      await tester.pumpWidget(buildWith(FakeCropPhotoPicker()));
      await tester.pumpAndSettle();
      await fillRest(tester);
      await addTestPhotos(tester, 1);
      await tapSave(tester);
      await tester.pumpAndSettle();

      verifyNever(() => cropListingBloc.add(any()));
      expect(find.text('Add at least 2 photos of your crop.'), findsOneWidget);

      await addTestPhotos(tester, 1);
      expect(find.text('Add at least 2 photos of your crop.'), findsNothing);
      expect(find.text('2 of 4'), findsOneWidget);
    });

    testWidgets('sends the photos and the form\'s listing id', (tester) async {
      await tester.pumpWidget(buildWith(FakeCropPhotoPicker()));
      await tester.pumpAndSettle();
      await fillRest(tester);
      await addTestPhotos(tester, 3);
      await tapSave(tester);

      final event = verify(() => cropListingBloc.add(captureAny()))
          .captured
          .single as CreateCropListing;
      expect(event.photos, hasLength(3));
      expect(event.photos.map((p) => p.photoId).toSet(), hasLength(3));
      expect(event.listingId, isNotNull);
    });

    testWidgets('tags the cover and removes a photo', (tester) async {
      await tester.pumpWidget(buildWith(FakeCropPhotoPicker()));
      await tester.pumpAndSettle();
      await addTestPhotos(tester);

      expect(find.text('Cover'), findsOneWidget);
      expect(find.byKey(const Key('create_listing_photo_1')), findsOneWidget);

      final remove = find.byTooltip('Remove photo').last;
      await tester.ensureVisible(remove);
      await tester.pumpAndSettle();
      await tester.tap(remove);
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('create_listing_photo_1')), findsNothing);
      expect(find.text('1 of 4'), findsOneWidget);
    });

    testWidgets('the gallery asks only for the free slots and stops at 4',
        (tester) async {
      final picker = FakeCropPhotoPicker(galleryCount: 4);
      await tester.pumpWidget(buildWith(picker));
      await tester.pumpAndSettle();
      await addTestPhotos(tester, 1);

      final gallery = find.byKey(const Key('create_listing_photo_gallery'));
      await tester.ensureVisible(gallery);
      await tester.pumpAndSettle();
      await tester.tap(gallery);
      await tester.pumpAndSettle();

      expect(picker.galleryLimits, [3]);
      expect(find.text('4 of 4'), findsOneWidget);
      // Full: both sources are off.
      final camera = tester.widget<ButtonStyleButton>(
        find.byKey(const Key('create_listing_photo_camera')),
      );
      expect(camera.onPressed, isNull);
    });

    testWidgets('explains a file that is not a usable photo', (tester) async {
      await tester.pumpWidget(
        buildWith(FakeCropPhotoPicker(issue: CropPhotoPickIssue.unsupported)),
      );
      await tester.pumpAndSettle();
      await addTestPhotos(tester, 1);

      expect(
        find.text(
          'That file is not a photo we can use. Choose a JPEG, PNG or WebP '
          'photo.',
        ),
        findsOneWidget,
      );
    });

    testWidgets('a failed web photo upload has its own message',
        (tester) async {
      final controller = StreamController<CropListingState>.broadcast();
      addTearDown(controller.close);
      await tester.pumpWidget(
        buildWith(FakeCropPhotoPicker(), states: controller.stream),
      );
      await tester.pumpAndSettle();

      controller.add(
        const CropListingError(
          message: 'CROP_PHOTO_UPLOAD_FAILED: 1 of 2 photos did not upload',
        ),
      );
      await tester.pump();
      await tester.pump();

      expect(
        find.text(
          'The photos did not upload. Check your connection and save again.',
        ),
        findsOneWidget,
      );
    });
  });
}

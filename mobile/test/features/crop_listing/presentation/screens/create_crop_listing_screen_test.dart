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
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

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

    testWidgets('shows GPS captured indicator when GPS succeeds',
        (tester) async {
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('GPS Captured'), findsOneWidget);
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
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
      expect(find.byIcon(Icons.warning_amber), findsOneWidget);
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
          home: CreateCropListingScreen(gpsCaptureService: mockGpsService),
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

      final save = find.widgetWithText(ElevatedButton, 'Save');
      expect(save, findsOneWidget);
      expect(tester.widget<ElevatedButton>(save).onPressed, isNotNull);
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
      await tester.tap(find.byIcon(CropType.teff.icon));
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Quantity (quintals)'),
        '10',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Price (ETB/kg)'),
        '50',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Location'),
        'Bishoftu',
      );
      await tester.pumpAndSettle();
    }

    Future<void> tapSave(WidgetTester tester) async {
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
}

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/create_delivery_screen.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class MockTripListingRepository extends Mock implements TripListingRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDeliveryState extends Fake implements DeliveryState {}

class FakeTripListingEvent extends Fake implements TripListingEvent {}

class FakeTripListingState extends Fake implements TripListingState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

CropListing _makeListing() {
  return CropListing()
    ..listingId = 'test-listing'
    ..farmerId = 'farmer-001'
    ..cropType = 'teff'
    ..quantity = 50.0
    ..pricePerKg = 85.0
    ..location = (LatLng()
      ..latitude = 9.0
      ..longitude = 38.7)
    ..locationName = 'Bishoftu'
    ..description = 'Test description'
    ..audioNoteRef = null
    ..status = ListingStatus.active
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

Trip _makeTrip({
  String tripId = 'trip-001',
  TripStatus status = TripStatus.open,
}) {
  return Trip()
    ..tripId = tripId
    ..truckerId = 'trucker-001'
    ..truckerName = 'Abebe'
    ..truckerPhone = '+251912345678'
    ..fromWoreda = 'Bishoftu'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = DateTime(2026, 3)
    ..capacity = 100.0
    ..pricePerQuintal = 200.0
    ..status = status
    ..matchedListings = []
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

void main() {
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockTripListingBloc tripListingBloc;
  late MockTripListingRepository tripRepository;
  late StreamController<DeliveryState> deliveryStreamController;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
    registerFallbackValue(FakeTripListingEvent());
    registerFallbackValue(FakeTripListingState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    tripListingBloc = MockTripListingBloc();
    tripRepository = MockTripListingRepository();
    deliveryStreamController = StreamController<DeliveryState>.broadcast();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'trucker-001',
        tokens: _testTokens,
        role: UserRole.trucker,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());

    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream)
        .thenAnswer((_) => deliveryStreamController.stream);

    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => tripListingBloc.repository).thenReturn(tripRepository);
  });

  tearDown(() {
    deliveryStreamController.close();
  });

  Widget buildScreen({CropListing? listing, bool withNavStack = false}) {
    final screen = CreateDeliveryScreen(listing: listing ?? _makeListing());

    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        BlocProvider<TripListingBloc>.value(value: tripListingBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: withNavStack
            ? Builder(
                builder: (context) {
                  // Push the screen on top so pop() has a route to go back to
                  WidgetsBinding.instance.addPostFrameCallback((_) {
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(builder: (_) => screen),
                    );
                  });
                  return const Scaffold(body: SizedBox.shrink());
                },
              )
            : screen,
      ),
    );
  }

  group('CreateDeliveryScreen', () {
    testWidgets('renders listing summary card with crop info', (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) async => [_makeTrip()]);

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Listing summary shows crop type name
      expect(find.text('Teff'), findsOneWidget);
      // Shows quantity with quintals
      expect(find.textContaining('50.0'), findsOneWidget);
      // Shows location
      expect(find.text('Bishoftu'), findsOneWidget);
      // Shows farmer ID
      expect(find.text('farmer-001'), findsOneWidget);
    });

    testWidgets('shows loading spinner while trips loading', (tester) async {
      // Make getTripsByTrucker never complete so trips stay loading
      final completer = Completer<List<Trip>>();
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) => completer.future);

      await tester.pumpWidget(buildScreen());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('validates buyer name required', (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) async => [_makeTrip()]);

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Tap submit without filling any fields
      final submitButton = find.widgetWithText(FilledButton, 'Save');
      await tester.ensureVisible(submitButton);
      await tester.tap(submitButton);
      await tester.pumpAndSettle();

      expect(find.text('Buyer name is required'), findsOneWidget);
    });

    testWidgets('validates buyer ID required', (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) async => [_makeTrip()]);

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Fill buyer name only
      final buyerNameField = find.widgetWithText(TextFormField, 'Buyer Name');
      await tester.enterText(buyerNameField, 'Test Buyer');

      // Tap submit
      final submitButton = find.widgetWithText(FilledButton, 'Save');
      await tester.ensureVisible(submitButton);
      await tester.tap(submitButton);
      await tester.pumpAndSettle();

      expect(find.text('Buyer phone or ID is required'), findsOneWidget);
    });

    testWidgets('shows success snackbar on DeliveryCreated state',
        (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) async => [_makeTrip()]);

      // Use nav stack so Navigator.pop() doesn't remove the root
      await tester.pumpWidget(buildScreen(withNavStack: true));
      await tester.pumpAndSettle();

      // Emit DeliveryCreated state
      final delivery = Delivery()
        ..deliveryId = 'del-001'
        ..tripId = 'trip-001'
        ..listingId = 'test-listing'
        ..farmerId = 'farmer-001'
        ..farmerName = 'farmer-001'
        ..truckerId = 'trucker-001'
        ..truckerName = 'Abebe'
        ..buyerId = 'buyer-001'
        ..buyerName = 'Test Buyer'
        ..qrToken = 'qr-token'
        ..status = DeliveryStatus.pending
        ..createdAt = DateTime(2026, 2, 20)
        ..updatedAt = DateTime(2026, 2, 20)
        ..version = 1;

      when(() => deliveryBloc.state)
          .thenReturn(DeliveryCreated(delivery: delivery));
      deliveryStreamController.add(DeliveryCreated(delivery: delivery));
      await tester.pumpAndSettle();

      expect(find.text('Delivery created successfully'), findsOneWidget);
    });

    testWidgets('shows error snackbar on DeliveryError state', (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001'))
          .thenAnswer((_) async => [_makeTrip()]);

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Emit DeliveryError state
      const errorState = DeliveryError(message: 'Failed to create delivery');
      when(() => deliveryBloc.state).thenReturn(errorState);
      deliveryStreamController.add(errorState);
      await tester.pumpAndSettle();

      // Error is localized via localizeDeliveryError; null code maps to generic
      expect(find.text('Delivery operation failed'), findsOneWidget);
    });

    // SIM-50: 25 Oct picked in Addis Ababa comes back from the server as
    // 2026-10-24T21:00:00.000Z; the trip picker shows 25 Oct.
    testWidgets('trip picker shows the departure day the trucker picked',
        (tester) async {
      when(() => tripRepository.getTripsByTrucker('trucker-001')).thenAnswer(
        (_) async => [
          _makeTrip()
            ..departureDate = DateTime.parse('2026-10-24T21:00:00.000Z'),
        ],
      );

      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();
      final picker = find.byType(DropdownButtonFormField<Trip>);
      await tester.ensureVisible(picker);
      await tester.tap(picker);
      await tester.pumpAndSettle();

      expect(
        find.text('Bishoftu -> Addis Ababa (Oct 25, 2026)'),
        findsWidgets,
      );
      expect(
        find.text('Bishoftu -> Addis Ababa (Oct 24, 2026)'),
        findsNothing,
      );
    });
  });
}

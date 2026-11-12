import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

/// ESC-31: a buyer who orders from a farmer or trucker tagged Low reputation
/// is asked first. Without the tag (the server sends no reputation copy
/// while its flag is off) the order is placed as before, with no dialog.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockRatingRepository ratingRepository;
  late List<CreateOrder> created;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    ratingRepository = MockRatingRepository();
    created = [];
    when(() => ratingRepository.getRatingSummary(any())).thenAnswer(
      (_) async => const RatingSummary(average: 4.9, count: 7),
    );
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is CreateOrder) created.add(event);
    });
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: AuthTokens(
          accessToken: 'fake-access-token',
          refreshToken: 'fake-refresh-token',
          idToken: 'fake-id-token',
        ),
        role: UserRole.buyer,
        userName: 'Meron Alemu',
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  final stamp = DateTime.utc(2026, 11, 7, 9);

  CropListing listing({String? tier}) => CropListing()
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..cropType = 'lentils'
    ..quantity = 10
    ..pricePerKg = 50
    ..locationName = 'Bahir Dar'
    ..ownerTier = tier
    ..ownerReputationAt = tier == null ? null : stamp;

  Trip trip({String? tier, double? rating, int? count}) => Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Abebe'
    ..fromWoreda = 'Adama'
    ..toWoreda = 'Bishoftu'
    ..departureDate = DateTime.now().toUtc().add(const Duration(days: 2))
    ..capacity = 200
    ..remainingQuintals = 120
    ..pricePerQuintal = 100
    ..ownerTier = tier
    ..ownerEffectiveRating = rating
    ..ownerRatingCount = count
    ..ownerReputationAt = tier == null ? null : stamp
    ..status = TripStatus.open;

  Future<void> open(
    WidgetTester tester, {
    required CropListing listing,
    Trip? selectedTrip,
  }) async {
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<RatingBloc>(
            create: (_) => RatingBloc(repository: ratingRepository),
          ),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: PlaceOrderScreen(
            listing: listing,
            farmerName: 'Kedir',
            selectedTrip: selectedTrip,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> placeOrder(WidgetTester tester) async {
    final button = find.widgetWithText(FilledButton, 'Place Order');
    await tester.ensureVisible(button);
    await tester.tap(button);
    await tester.pumpAndSettle();
  }

  final dialog = find.byKey(const Key('low_reputation_order_dialog'));

  testWidgets('no tag: the order is placed with no dialog', (tester) async {
    await open(tester, listing: listing());
    await placeOrder(tester);

    expect(dialog, findsNothing);
    expect(created, hasLength(1));
  });

  testWidgets('a good farmer and a good trucker: no dialog', (tester) async {
    await open(
      tester,
      listing: listing(tier: 'good'),
      selectedTrip: trip(tier: 'good'),
    );
    await placeOrder(tester);

    expect(dialog, findsNothing);
    expect(created, hasLength(1));
  });

  testWidgets('a low farmer: asked, and Back places nothing', (tester) async {
    await open(tester, listing: listing(tier: 'low'));
    await placeOrder(tester);

    expect(dialog, findsOneWidget);
    expect(find.text('Order anyway?'), findsOneWidget);
    expect(
      find.text(
        'This farmer has a Low reputation label. They recently cancelled '
        'or missed several paid orders. You can still order.',
      ),
      findsOneWidget,
    );
    expect(created, isEmpty);

    await tester.tap(find.byKey(const Key('low_reputation_order_back')));
    await tester.pumpAndSettle();

    expect(dialog, findsNothing);
    expect(created, isEmpty);
    // Still on the screen: the buyer can change their mind.
    expect(find.widgetWithText(FilledButton, 'Place Order'), findsOneWidget);
  });

  testWidgets('a low farmer: Order anyway places the order once',
      (tester) async {
    await open(tester, listing: listing(tier: 'low'));
    await placeOrder(tester);
    await tester.tap(find.byKey(const Key('low_reputation_order_continue')));
    await tester.pumpAndSettle();

    expect(dialog, findsNothing);
    expect(created, hasLength(1));
    expect(created.single.listingId, 'listing-001');
  });

  testWidgets('dismissing the dialog places nothing', (tester) async {
    await open(tester, listing: listing(tier: 'low'));
    await placeOrder(tester);
    await tester.tapAt(const Offset(5, 5));
    await tester.pumpAndSettle();

    expect(dialog, findsNothing);
    expect(created, isEmpty);
  });

  testWidgets('a low trucker is named, not the farmer', (tester) async {
    await open(
      tester,
      listing: listing(tier: 'good'),
      selectedTrip: trip(tier: 'low'),
    );
    await placeOrder(tester);

    expect(
      find.textContaining('This trucker has a Low reputation label.'),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const Key('low_reputation_order_continue')));
    await tester.pumpAndSettle();
    expect(created.single.tripId, 'trip-001');
  });

  testWidgets('both low: one dialog names both', (tester) async {
    await open(
      tester,
      listing: listing(tier: 'low'),
      selectedTrip: trip(tier: 'low'),
    );
    await placeOrder(tester);

    expect(dialog, findsOneWidget);
    expect(
      find.textContaining('This farmer and this trucker both have'),
      findsOneWidget,
    );
  });

  testWidgets('a low trucker on a self-pickup order is not asked about',
      (tester) async {
    // No trip chosen: only the farmer counts.
    await open(tester, listing: listing(tier: 'good'));
    await placeOrder(tester);

    expect(dialog, findsNothing);
    expect(created, hasLength(1));
  });

  group('the trucker rating on the transport line', () {
    testWidgets('a trip without the copy reads the aggregate as before',
        (tester) async {
      await open(tester, listing: listing(), selectedTrip: trip());

      verify(() => ratingRepository.getRatingSummary('trucker-001')).called(1);
      expect(find.textContaining(' · 4.9'), findsOneWidget);
    });

    testWidgets('a trip with the copy shows it and makes no call',
        (tester) async {
      await open(
        tester,
        listing: listing(),
        selectedTrip: trip(tier: 'good', rating: 4.2, count: 5),
      );

      verifyNever(() => ratingRepository.getRatingSummary(any()));
      expect(find.textContaining(' · 4.2'), findsOneWidget);
    });

    testWidgets('a trip with the copy and no ratings shows none',
        (tester) async {
      await open(
        tester,
        listing: listing(),
        selectedTrip: trip(tier: 'good', count: 0),
      );

      verifyNever(() => ratingRepository.getRatingSummary(any()));
      expect(find.textContaining(' · 4.'), findsNothing);
    });
  });
}

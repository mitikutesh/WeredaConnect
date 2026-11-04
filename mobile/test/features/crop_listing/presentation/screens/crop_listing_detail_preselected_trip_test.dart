import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockOrderBloc extends Mock implements OrderBloc {}

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

class _FakeOrderEvent extends Fake implements OrderEvent {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// B-2: a listing opened from a buyer's Find crops on this route has that
/// truck chosen for transport when it can still take the order, and falls
/// back to choosing a truck, saying why, when it cannot.
void main() {
  late _MockAuthBloc authBloc;
  late _MockOrderBloc orderBloc;
  late List<CreateOrder> created;

  final today = startOfDayInEthiopia(DateTime.now().toUtc());

  setUpAll(() => registerFallbackValue(_FakeOrderEvent()));

  setUp(() {
    authBloc = _MockAuthBloc();
    orderBloc = _MockOrderBloc();
    created = [];
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _tokens,
        role: UserRole.buyer,
        userName: 'Selam Traders',
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is CreateOrder) created.add(event);
    });
  });

  Trip truck({DateTime? departure}) => Trip()
    ..tripId = 'trip-001'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Trucker Tesfaye'
    ..truckerPhone = ''
    ..fromWoreda = 'Adama'
    ..toWoreda = 'Addis Ababa'
    ..departureDate = departure ?? today.add(const Duration(days: 2))
    ..capacity = 50
    ..remainingQuintals = 40
    ..pricePerQuintal = 100
    ..status = TripStatus.open
    ..matchedListings = <String>[]
    ..createdAt = DateTime.utc(2026, 10, 20)
    ..updatedAt = DateTime.utc(2026, 10, 20)
    ..version = 1;

  final listing = CropListing()
    ..listingId = 'listing-1'
    ..farmerId = 'farmer-1'
    ..farmerName = 'Almaz'
    ..cropType = 'teff'
    ..quantity = 20
    ..pricePerKg = 80
    ..location = (LatLng()
      ..latitude = 8.54
      ..longitude = 39.27)
    ..locationName = 'Adama'
    ..description = ''
    ..status = ListingStatus.active
    ..createdAt = DateTime.utc(2026, 11)
    ..updatedAt = DateTime.utc(2026, 11)
    ..version = 1;

  Widget screen(Trip trip, {UserRole role = UserRole.buyer}) {
    final ratings = _MockRatingRepository();
    when(() => ratings.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<OrderBloc>.value(value: orderBloc),
        BlocProvider<RatingBloc>(
          create: (_) => RatingBloc(repository: ratings),
        ),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: CropListingDetailScreen(
          listing: listing,
          currentUserId: 'buyer-001',
          currentUserRole: role,
          deliveryWoreda: role == UserRole.buyer ? 'Addis Ababa' : null,
          quantityNeeded: 20,
          preselectedTrip: trip,
        ),
      ),
    );
  }

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  SuggestedTripCard card(WidgetTester tester) =>
      tester.widget<SuggestedTripCard>(find.byType(SuggestedTripCard));

  testWidgets('a truck that can take the order is chosen', (tester) async {
    tallView(tester);
    await tester.pumpWidget(screen(truck()));
    await tester.pumpAndSettle();

    expect(card(tester).isSelected, isTrue);
    expect(find.text('Place Order + Transport'), findsOneWidget);
    // 20 quintals x 100 ETB.
    expect(find.text('2,000 ETB'), findsOneWidget);
  });

  testWidgets('a truck whose day is over is not: the buyer is told why',
      (tester) async {
    tallView(tester);
    final left = truck(departure: today.subtract(const Duration(days: 1)));
    await tester.pumpWidget(screen(left));
    await tester.pumpAndSettle();

    expect(
      find.text('This truck already left. Choose another truck.'),
      findsOneWidget,
    );
    expect(find.byType(SuggestedTripCard), findsNothing);
    expect(find.text('Place Order + Transport'), findsNothing);
    expect(find.text('Place Order'), findsOneWidget);
  });

  testWidgets('a farmer is never given a truck', (tester) async {
    tallView(tester);
    await tester.pumpWidget(screen(truck(), role: UserRole.farmer));
    await tester.pumpAndSettle();

    expect(find.byType(SuggestedTripCard), findsNothing);
    expect(find.text('Place Order + Transport'), findsNothing);
  });

  testWidgets('a truck that closed by Place Order is unselected on return',
      (tester) async {
    tallView(tester);
    final chosen = truck();
    await tester.pumpWidget(screen(chosen));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Place Order + Transport'));
    await tester.pumpAndSettle();
    expect(find.byType(PlaceOrderScreen), findsOneWidget);

    // Filled up while the buyer was choosing the quantity.
    chosen.status = TripStatus.matched;
    await tester.tap(find.widgetWithText(FilledButton, 'Place Order'));
    await tester.pumpAndSettle();

    expect(created, isEmpty);
    expect(find.byType(PlaceOrderScreen), findsNothing);
    expect(
      find.text('That truck is no longer available. Choose again.'),
      findsOneWidget,
    );
    expect(find.byType(SuggestedTripCard), findsNothing);
    expect(find.text('Place Order + Transport'), findsNothing);
  });
}

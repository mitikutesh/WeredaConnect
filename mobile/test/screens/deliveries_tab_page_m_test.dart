import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/dashed_border.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

import '../features/live_trip/live_trip_harness.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

/// Design canvas page M: the deliveries tab (the buyer's landing tab) with
/// its three section cards, and the empty state in each role's own words.
void main() {
  late MockAuthBloc authBloc;
  late LiveTripDeps deps;
  late FakeTrackRepository tracks;

  setUpAll(() {
    PlaceRepository.instance.loadFromStringForTest(
      '[{"id":"gn-330491","en":"Mojo","lat":8.5868,"lng":39.1211,'
      '"population":61300}]',
    );
  });

  setUp(() {
    authBloc = MockAuthBloc();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    deps = LiveTripDeps(order: liveOrder());
    tracks = FakeTrackRepository(track: recentTrack());
  });

  Future<void> pumpTab(
    WidgetTester tester, {
    required UserRole role,
    required String userId,
    required List<Delivery> deliveries,
    ValueChanged<int>? onNavigateToTab,
    Size size = const Size(420, 1400),
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: userId,
        tokens: const AuthTokens(
          accessToken: 'a',
          refreshToken: 'r',
          idToken: 'i',
        ),
        role: role,
      ),
    );
    when(() => deps.deliveryBloc.state)
        .thenReturn(DeliveriesLoaded(deliveries: deliveries));
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ...deps.providers(tracks),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          theme: AppTheme.light(role: role),
          home: Scaffold(
            body: DeliveriesTabScreen(onNavigateToTab: onNavigateToTab),
          ),
        ),
      ),
    );
    await settleReads(tester);
  }

  List<Delivery> three() => [
        liveDelivery()
          ..cropType = 'wheat'
          ..quantity = 2000
          ..pickupPlace = 'Meki',
        liveDelivery(id: 'delivery-102', status: DeliveryStatus.pending)
          ..cropType = 'teff'
          ..quantity = 1500
          ..pickupPlannedAt = DateTime.utc(2026, 10, 30, 6),
        liveDelivery(
          id: 'delivery-103',
          status: DeliveryStatus.delivered,
          confirmedAt:
              DateTime.now().toUtc().subtract(const Duration(hours: 2)),
        )
          ..cropType = 'chickpea'
          ..quantity = 800,
      ];

  group('the three section cards', () {
    testWidgets('buyer: the map card, the dashed card, the sand card',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: three(),
      );

      expect(find.text('ON THE ROAD NOW'), findsOneWidget);
      expect(find.text('WAITING FOR PICKUP'), findsOneWidget);
      expect(find.text('DELIVERED'), findsOneWidget);
      expect(find.text('Active · 2'), findsOneWidget);
      expect(find.text('Delivered · 1'), findsOneWidget);

      // On the road now: the raised card whose top is the map.
      final road = find.byType(OnTheRoadCard);
      expect(road, findsOneWidget);
      Finder onRoad(Finder what) => find.descendant(of: road, matching: what);
      expect(onRoad(find.byType(TrackMap)), findsOneWidget);
      expect(onRoad(find.text('Checked in 28 min ago')), findsOneWidget);
      expect(onRoad(find.text('Open map')), findsOneWidget);
      expect(onRoad(find.text('Wheat · 2,000 kg')), findsOneWidget);
      expect(
        onRoad(find.text('Almaz Bekele, Meki · Dawit Tesfaye')),
        findsOneWidget,
      );
      expect(onRoad(find.text('Arriving')), findsOneWidget);
      // The previous design's route ribbon is gone from the buyer's card.
      expect(onRoad(find.byType(RouteRibbon)), findsNothing);
      // The window is in the buyer's own colour.
      final window = tester.widget<Text>(
        find.descendant(
          of: find.byKey(const Key('on_the_road_window_delivery-101')),
          matching: find.byType(Text),
        ),
      );
      expect(window.style!.color, RoleColors.forRole(UserRole.buyer).primary);

      // Waiting for pickup: only a dashed edge.
      final waiting = find.byKey(const Key('delivery_card_waiting'));
      expect(waiting, findsOneWidget);
      expect(
        find.descendant(of: waiting, matching: find.text('Teff · 1,500 kg')),
        findsOneWidget,
      );
      expect(find.byType(DashedBorderBox), findsOneWidget);
      expect(
        find.text("The map appears once the driver scans the farmer's QR."),
        findsOneWidget,
      );

      // Delivered: sand, Release pending, nothing to do.
      final delivered = find.byKey(const Key('delivery_card_delivered'));
      expect(delivered, findsOneWidget);
      expect(
        find.descendant(of: delivered, matching: find.text('Release pending')),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('delivery_card_nothing_to_do')),
        findsOneWidget,
      );
      expect(find.textContaining('Report a problem until'), findsOneWidget);
      expect(find.text('Open'), findsOneWidget);
    });

    testWidgets('trucker: the same stages, never a map', (tester) async {
      await pumpTab(
        tester,
        role: UserRole.trucker,
        userId: truckerId,
        deliveries: three(),
      );

      expect(find.text('ON THE ROAD NOW'), findsOneWidget);
      expect(find.byType(OnTheRoadCard), findsNothing);
      expect(find.byType(TrackMap), findsNothing);
      expect(find.text('Open map'), findsNothing);
      expect(tracks.fetches, 0);
      expect(find.byKey(const Key('delivery_card_waiting')), findsOneWidget);
      expect(find.byKey(const Key('delivery_card_delivered')), findsOneWidget);
      expect(find.textContaining('The map appears'), findsNothing);
    });

    testWidgets('farmer: the sand card with the ribbon, no map',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.farmer,
        userId: farmerId,
        deliveries: three(),
      );

      expect(find.byType(OnTheRoadCard), findsOneWidget);
      expect(find.byType(RouteRibbon), findsOneWidget);
      expect(find.byType(TrackMap), findsNothing);
      expect(tracks.readers, [farmerId]);
    });

    testWidgets('the header draws the role\'s sketch beside the title',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: three(),
        // Room for the title in the test font beside the sketch.
        size: const Size(800, 1400),
      );
      final sketch = tester.widget<RoleSketch>(
        find.byKey(const Key('deliveries_header_sketch')),
      );
      expect(sketch.role, UserRole.buyer);
    });

    testWidgets('320 px and double text size: nothing overflows',
        (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: three(),
        size: const Size(320, 2400),
      );
      expect(tester.takeException(), isNull);
      // Rule 5 of the sketches: none with large text.
      expect(find.byKey(const Key('deliveries_header_sketch')), findsNothing);
    });
  });

  group('no deliveries', () {
    testWidgets('buyer: Find crops goes to Market, See my orders to Orders',
        (tester) async {
      final went = <int>[];
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: const [],
        onNavigateToTab: went.add,
      );

      expect(find.byKey(const Key('deliveries_empty')), findsOneWidget);
      expect(find.text('Nothing on the road yet'), findsOneWidget);
      expect(
        find.text(
          'When a driver picks up one of your orders, you can follow the '
          'truck here.',
        ),
        findsOneWidget,
      );
      // One sketch per screen: the large one, in the buyer's drawing.
      final sketch = tester.widget<RoleSketch>(find.byType(RoleSketch));
      expect(sketch.role, UserRole.buyer);
      expect(sketch.width, 200);
      // No switch over nothing.
      expect(find.text('All'), findsNothing);

      await tester.tap(find.text('Find crops'));
      await tester.tap(find.text('See my orders'));
      expect(went, [
        roleTabIndex(UserRole.buyer, RoleTab.market),
        roleTabIndex(UserRole.buyer, RoleTab.orders),
      ]);
    });

    testWidgets('farmer: their own words and sketch, See my orders',
        (tester) async {
      final went = <int>[];
      await pumpTab(
        tester,
        role: UserRole.farmer,
        userId: farmerId,
        deliveries: const [],
        onNavigateToTab: went.add,
      );

      expect(find.text('Nothing on the road yet'), findsOneWidget);
      expect(
        find.text(
          'When a driver picks up one of your crops, you can follow the '
          'truck here.',
        ),
        findsOneWidget,
      );
      expect(
        tester.widget<RoleSketch>(find.byType(RoleSketch)).role,
        UserRole.farmer,
      );
      // Finding crops is the buyer's.
      expect(find.text('Find crops'), findsNothing);

      await tester.tap(find.text('See my orders'));
      expect(went, [roleTabIndex(UserRole.farmer, RoleTab.orders)]);
    });

    testWidgets('trucker: their own words and sketch, See my trips',
        (tester) async {
      final went = <int>[];
      await pumpTab(
        tester,
        role: UserRole.trucker,
        userId: truckerId,
        deliveries: const [],
        onNavigateToTab: went.add,
      );

      expect(
        find.text('When you pick up a load, its delivery shows here.'),
        findsOneWidget,
      );
      expect(
        tester.widget<RoleSketch>(find.byType(RoleSketch)).role,
        UserRole.trucker,
      );
      expect(find.text('Find crops'), findsNothing);
      expect(find.text('See my orders'), findsNothing);

      await tester.tap(find.text('See my trips'));
      expect(went, [roleTabIndex(UserRole.trucker, RoleTab.myTrips)]);
    });

    testWidgets('with no way to switch tabs there are no dead buttons',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: const [],
      );

      expect(find.text('Nothing on the road yet'), findsOneWidget);
      expect(find.text('Find crops'), findsNothing);
      expect(find.text('See my orders'), findsNothing);
    });

    testWidgets('a switch that hides them all is not the empty state',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: [liveDelivery()],
      );
      await tester.tap(find.text('Delivered'));
      await tester.pump();

      expect(find.text('No deliveries yet'), findsOneWidget);
      expect(find.byKey(const Key('deliveries_empty')), findsNothing);
    });
  });
}

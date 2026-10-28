import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/on_the_road_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

import '../features/live_trip/live_trip_harness.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

/// LT-11: the deliveries tab puts the viewer's own deliveries on the road
/// first as live trip cards, for the buyer and the farmer only. The trucker's
/// list is unchanged and never reads a track.
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
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: DeliveriesTabScreen()),
        ),
      ),
    );
    await settleReads(tester);
  }

  final onTheRoad = liveDelivery();
  final waiting =
      liveDelivery(id: 'delivery-102', status: DeliveryStatus.pending);

  testWidgets('buyer: On the road now first, the waiting one says when',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.buyer,
      userId: buyerId,
      deliveries: [waiting, onTheRoad],
    );
    expect(find.text('On the road now'), findsOneWidget);
    expect(find.byType(OnTheRoadCard), findsOneWidget);
    expect(find.text('Wheat · 2,000 kg'), findsOneWidget);
    expect(find.text('Checked in 28 min ago'), findsOneWidget);
    final eta = tracks.track!.eta!;
    final l10n = lookupAppLocalizations(const Locale('en'));
    expect(
      find.text(
        ClockFormat.resolve(l10n, ClockPreference.auto).range(eta.from, eta.to),
      ),
      findsOneWidget,
    );
    expect(find.byType(DeliveryCard), findsOneWidget);
    expect(
      find.text("The map appears when the driver scans the farmer's QR."),
      findsOneWidget,
    );
    expect(tracks.readers, [buyerId]);
    // The list shows no map; the map is on the live trip screen.
    expect(find.byType(TrackMap), findsNothing);
  });

  testWidgets('buyer: a card opens the live trip', (tester) async {
    await pumpTab(
      tester,
      role: UserRole.buyer,
      userId: buyerId,
      deliveries: [onTheRoad],
    );
    await tester.tap(find.byType(OnTheRoadCard));
    await tester.pumpAndSettle();
    expect(find.byType(LiveTripScreen), findsOneWidget);
  });

  testWidgets('farmer: only their own order, as "on the road"', (tester) async {
    await pumpTab(
      tester,
      role: UserRole.farmer,
      userId: farmerId,
      deliveries: [
        onTheRoad,
        liveDelivery(id: 'delivery-201', farmer: 'farmer-002'),
        waiting,
      ],
    );
    expect(find.byType(OnTheRoadCard), findsOneWidget);
    expect(find.text('Your Wheat is on the road'), findsOneWidget);
    // Another farmer's delivery (it cannot reach this list from the server)
    // still never gets a live card, and the waiting note is the buyer's.
    expect(find.byKey(const Key('on_the_road_delivery-201')), findsNothing);
    expect(
      find.text("The map appears when the driver scans the farmer's QR."),
      findsNothing,
    );
    expect(tracks.readers, [farmerId]);
  });

  testWidgets('trucker: the list is unchanged, no live trip, no read',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.trucker,
      userId: truckerId,
      deliveries: [onTheRoad, waiting],
    );
    expect(find.text('On the road now'), findsNothing);
    expect(find.byType(OnTheRoadCard), findsNothing);
    expect(find.byType(DeliveryCard), findsNWidgets(2));
    expect(find.byType(TrackMap), findsNothing);
    expect(tracks.fetches, 0);
  });

  testWidgets('desktop: the live trip shows beside the list (web)',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.buyer,
      userId: buyerId,
      deliveries: [onTheRoad, liveDelivery(id: 'delivery-103')],
      size: const Size(1400, 1000),
    );
    expect(find.byType(OnTheRoadCard), findsNWidgets(2));
    final pane = tester.widget<LiveTripView>(find.byType(LiveTripView));
    expect(pane.delivery.deliveryId, 'delivery-101');
    expect(find.byType(TrackMap), findsOneWidget);

    await tester.tap(find.byKey(const Key('on_the_road_delivery-103')));
    await settleReads(tester);
    expect(
      tester
          .widget<LiveTripView>(find.byType(LiveTripView))
          .delivery
          .deliveryId,
      'delivery-103',
    );
    expect(find.byType(LiveTripScreen), findsNothing);
  });

  testWidgets('desktop trucker: no pane', (tester) async {
    await pumpTab(
      tester,
      role: UserRole.trucker,
      userId: truckerId,
      deliveries: [onTheRoad],
      size: const Size(1400, 1000),
    );
    expect(find.byType(LiveTripView), findsNothing);
    expect(tracks.fetches, 0);
  });
}

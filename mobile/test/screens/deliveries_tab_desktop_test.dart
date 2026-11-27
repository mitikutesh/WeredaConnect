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
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

import '../features/live_trip/live_trip_harness.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

/// The deliveries tab from a desktop width, as the Web · Buyer deliveries
/// board (page M) draws it: one white search box, white list cards with the
/// selected one edged in the role's colour, the list a narrow column beside
/// the pane; a role with no pane keeps the header over its centred column.
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
    Size size = const Size(1220, 1000),
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
          theme: AppTheme.light(role: role),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(body: DeliveriesTabScreen()),
        ),
      ),
    );
    await settleReads(tester);
  }

  Delivery waiting() =>
      liveDelivery(id: 'delivery-102', status: DeliveryStatus.pending)
        ..cropType = 'teff'
        ..quantity = 1500;

  Delivery inCheckWindow() => liveDelivery(
        id: 'delivery-105',
        status: DeliveryStatus.delivered,
        confirmedAt: DateTime.now().toUtc().subtract(const Duration(hours: 2)),
      )
        ..cropType = 'chickpea'
        ..quantity = 800;

  ShapeBorder? shapeOf(WidgetTester tester, String deliveryId) => tester
      .widget<Material>(
        find
            .descendant(
              of: find.byKey(ValueKey('delivery-$deliveryId')),
              matching: find.byType(Material),
            )
            .first,
      )
      .shape;

  testWidgets('buyer: the search is one white box with a bare field in it',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.buyer,
      userId: buyerId,
      deliveries: [liveDelivery(), waiting()],
    );

    final field = find.byKey(const Key('deliveries_search'));
    expect(field, findsOneWidget);
    final decoration = tester.widget<TextField>(field).decoration!;
    // The theme's filled input would draw a second box inside the first.
    expect(decoration.filled, isFalse);
    expect(decoration.enabledBorder, InputBorder.none);
    expect(decoration.focusedBorder, InputBorder.none);
    expect(decoration.hintText, 'Crop, farmer or driver');

    BoxDecoration box() => tester
        .widget<Container>(
          find.ancestor(of: field, matching: find.byType(Container)).first,
        )
        .decoration! as BoxDecoration;
    expect(box().color, AppTheme.surfaceWhite);
    expect((box().border! as Border).top.color, AppTheme.line);
    expect(tester.getSize(field).width, lessThanOrEqualTo(300));

    // Keyboard focus shows on the box, in the role's colour.
    await tester.tap(field);
    await tester.pump();
    expect((box().border! as Border).top.color, RoleColors.buyer.primary);
    expect((box().border! as Border).top.width, 2);

    await tester.enterText(field, 'teff');
    await settleReads(tester);
    expect(find.text('Teff · 1,500 kg'), findsOneWidget);
    expect(find.text('Wheat · 2,000 kg'), findsNothing);
  });

  testWidgets('buyer: white list cards, the one in the pane edged in green',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.buyer,
      userId: buyerId,
      deliveries: [waiting(), inCheckWindow()],
    );

    // Nothing is on the road: the first card with a pane is selected.
    final selected = shapeOf(tester, 'delivery-102')! as RoundedRectangleBorder;
    expect(selected.side.color, RoleColors.buyer.primary);
    expect(selected.side.width, 2);
    expect(selected.borderRadius, BorderRadius.circular(18));
    final other = shapeOf(tester, 'delivery-105')! as RoundedRectangleBorder;
    expect(other.side.color, AppTheme.line);
    expect(other.side.width, 1);
    for (final id in ['delivery-102', 'delivery-105']) {
      final card = tester.widget<Material>(
        find
            .descendant(
              of: find.byKey(ValueKey('delivery-$id')),
              matching: find.byType(Material),
            )
            .first,
      );
      expect(card.color, AppTheme.surfaceWhite);
    }

    // The money still waits: the board's orange tint chip.
    final pill = tester.widget<FwPill>(
      find.ancestor(
        of: find.text('Release pending'),
        matching: find.byType(FwPill),
      ),
    );
    expect(pill.background, AppTheme.orangeTint);
    expect(pill.foreground, AppTheme.orangeInk);

    // A tap moves the selection (and the pane) to that delivery.
    await tester.tap(find.byKey(const ValueKey('delivery-delivery-105')));
    await settleReads(tester);
    expect(
      (shapeOf(tester, 'delivery-105')! as RoundedRectangleBorder).side.width,
      2,
    );
    expect(
      (shapeOf(tester, 'delivery-102')! as RoundedRectangleBorder).side.width,
      1,
    );
  });

  for (final width in const [1060.0, 1220.0, 1700.0]) {
    testWidgets('buyer at $width px: a narrow list, the pane gets the rest',
        (tester) async {
      await pumpTab(
        tester,
        role: UserRole.buyer,
        userId: buyerId,
        deliveries: [liveDelivery(), waiting()],
        size: Size(width, 1000),
      );

      final list = tester.getSize(find.byType(DeliveryCard).first).width;
      expect(list, inInclusiveRange(300, 380));
      final pane = tester.getRect(find.byType(LiveTripView));
      // Room for the pane's two cards side by side.
      expect(pane.width, greaterThanOrEqualTo(660));
      // The board's 32 px page margin on both sides.
      expect(tester.getTopLeft(find.byType(DeliveryCard).first).dx, 32);
      expect(pane.right, width - 32);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('trucker (no pane): the header sits over the centred column',
      (tester) async {
    await pumpTab(
      tester,
      role: UserRole.trucker,
      userId: truckerId,
      deliveries: [liveDelivery()],
    );

    expect(find.byType(LiveTripView), findsNothing);
    final title = tester.getTopLeft(find.text('Deliveries'));
    final card = tester.getTopLeft(find.byType(DeliveryCard).first);
    expect(title.dx, card.dx);
    final search = tester.getRect(
      find
          .ancestor(
            of: find.byKey(const Key('deliveries_search')),
            matching: find.byType(Container),
          )
          .first,
    );
    expect(
      search.right,
      tester.getRect(find.byType(DeliveryCard).first).right,
    );
  });
}

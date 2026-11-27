import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

const _note = 'Merkato, near Anwar Mosque';

/// A trip order the server runs commitments for, both parties agreed.
Order _order(OrderStatus status, {String? note = _note}) => Order()
  ..orderId = 'order-001'
  ..listingId = 'listing-001'
  ..buyerId = 'buyer-001'
  ..farmerId = 'farmer-001'
  ..buyerName = 'Buyer Bee'
  ..farmerName = 'Farmer Fikru'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 85
  ..totalPrice = 8500
  ..status = status
  ..createdAt = DateTime.utc(2026, 10, 26)
  ..version = 3
  ..tripId = 'trip-001'
  ..truckerId = 'trucker-001'
  ..truckerName = 'Trucker Tesfaye'
  ..fulfillment = 'trucker'
  ..farmerResponse = 'accepted'
  ..truckerResponse = 'accepted'
  ..contactOpen = true
  ..dropoffNote = note;

/// LT-12: the order detail shows the buyer's drop-off landmark to every
/// party that received it, and lets the buyer add or change it until pickup
/// (the same screen on mobile and web).
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockRatingBloc ratingBloc;
  late List<OrderEvent> added;
  late StreamController<OrderState> states;
  final en = lookupAppLocalizations(const Locale('en'));

  setUpAll(() {
    registerFallbackValue(FakeOrderEvent());
    registerFallbackValue(FakeDeliveryEvent());
  });

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    ratingBloc = MockRatingBloc();
    added = [];
    states = StreamController<OrderState>.broadcast();
    final ratingRepo = MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => orderBloc.stream).thenAnswer((_) => states.stream);
    when(() => orderBloc.add(any())).thenAnswer((invocation) {
      added.add(invocation.positionalArguments.first as OrderEvent);
    });
    when(() => orderBloc.isClosed).thenReturn(false);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    final deliveryRepo = MockDeliveryRepository();
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => null);
  });

  tearDown(() => states.close());

  Future<void> show(
    WidgetTester tester,
    Order order,
    UserRole role,
    String userId,
  ) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
    );
    when(() => orderBloc.state).thenReturn(SingleOrderLoaded(order: order));
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
          BlocProvider<RatingBloc>.value(value: ratingBloc),
        ],
        child: const MaterialApp(
          locale: Locale('en'),
          localizationsDelegates: [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: OrderDetailScreen(orderId: 'order-001'),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final card = find.byKey(const Key('order_dropoff_note'));
  final edit = find.byKey(const Key('order_dropoff_note_edit'));

  group('the buyer', () {
    testWidgets('changes the landmark before pickup', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );

      expect(
        find.descendant(of: card, matching: find.text(_note)),
        findsOneWidget,
      );
      expect(
        find.descendant(of: edit, matching: find.text(en.edit)),
        findsOneWidget,
      );

      await tester.ensureVisible(edit);
      await tester.tap(edit);
      await tester.pumpAndSettle();

      final field = find.byKey(const Key('dropoff_note_field'));
      expect(tester.widget<TextField>(field).controller!.text, _note);
      await tester.enterText(field, 'Kality, behind the bus station');
      await tester.pump();
      await tester.tap(find.byKey(const Key('dropoff_note_save')));
      await tester.pumpAndSettle();

      final change = added.whereType<UpdateDropoffNote>().single;
      expect(change.orderId, 'order-001');
      expect(change.buyerId, 'buyer-001');
      expect(change.dropoffNote, 'Kality, behind the bus station');
    });

    testWidgets('adds one when there is none', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, note: null),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.dropoffNoteEmpty), findsOneWidget);
      expect(
        find.descendant(of: edit, matching: find.text(en.dropoffNoteAdd)),
        findsOneWidget,
      );
    });

    testWidgets('cancelling the sheet changes nothing', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );

      await tester.ensureVisible(edit);
      await tester.tap(edit);
      await tester.pumpAndSettle();
      await tester.tap(find.text(en.cancel));
      await tester.pumpAndSettle();

      expect(added.whereType<UpdateDropoffNote>(), isEmpty);
    });

    testWidgets('cannot save a phone number', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );

      await tester.ensureVisible(edit);
      await tester.tap(edit);
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('dropoff_note_field')),
        'Gate 2, call 0911234567',
      );
      await tester.pump();

      expect(find.text(en.errorContactInText), findsOneWidget);
      final save = tester.widget<FilledButton>(
        find.byKey(const Key('dropoff_note_save')),
      );
      expect(save.onPressed, isNull);
    });

    testWidgets('after pickup sees it, without Edit', (tester) async {
      await show(
        tester,
        _order(OrderStatus.inTransit),
        UserRole.buyer,
        'buyer-001',
      );

      expect(
        find.descendant(of: card, matching: find.text(_note)),
        findsOneWidget,
      );
      expect(edit, findsNothing);
    });

    testWidgets('a saved change is said and the order read again',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted),
        UserRole.buyer,
        'buyer-001',
      );
      added.clear();

      states.add(const DropoffNoteSaved());
      await tester.pumpAndSettle();

      expect(find.text(en.dropoffNoteSaved), findsOneWidget);
      expect(
        added.whereType<LoadOrderById>().single.orderId,
        'order-001',
      );
    });
  });

  testWidgets('the farmer sees it, without Edit', (tester) async {
    await show(
      tester,
      _order(OrderStatus.accepted),
      UserRole.farmer,
      'farmer-001',
    );

    expect(
      find.descendant(of: card, matching: find.text(_note)),
      findsOneWidget,
    );
    expect(edit, findsNothing);
  });

  testWidgets('the committed trucker sees it, without Edit', (tester) async {
    await show(
      tester,
      _order(OrderStatus.accepted),
      UserRole.trucker,
      'trucker-001',
    );

    expect(
      find.descendant(of: card, matching: find.text(_note)),
      findsOneWidget,
    );
    expect(edit, findsNothing);
  });

  testWidgets('a trucker the server sent no note to sees no card',
      (tester) async {
    await show(
      tester,
      _order(OrderStatus.funded, note: null),
      UserRole.trucker,
      'trucker-001',
    );

    expect(card, findsNothing);
  });
}

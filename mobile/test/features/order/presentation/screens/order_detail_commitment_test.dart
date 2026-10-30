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

/// A funded or later trip order the server runs commitments for.
Order _order(
  OrderStatus status, {
  String farmer = 'pending',
  String trucker = 'not_asked',
  bool commitments = true,
  String? transportFailure,
}) =>
    Order()
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
      ..fulfillment = commitments ? 'trucker' : null
      ..farmerResponse = commitments ? farmer : null
      ..truckerResponse = commitments ? trucker : null
      ..farmerDeadlineAt = DateTime.utc(2026, 10, 27, 8)
      ..truckerDeadlineAt =
          trucker == 'pending' ? DateTime.utc(2026, 10, 28, 8) : null
      ..buyerChoiceDeadlineAt = status == OrderStatus.awaitingTransport
          ? DateTime.utc(2026, 10, 29, 8)
          : null
      ..transportFailure = transportFailure
      ..contactOpen = true;

/// ESC-15: the order detail offers each party exactly the answer it owes.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockRatingBloc ratingBloc;
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
    final ratingRepo = MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);
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

  Future<void> show(
    WidgetTester tester,
    Order order,
    UserRole role,
    String userId, {
    Locale locale = const Locale('en'),
  }) async {
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
        child: MaterialApp(
          locale: locale,
          // The app's delegates: Afan Oromo has no built-in Material strings.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: const OrderDetailScreen(orderId: 'order-001'),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('farmer', () {
    testWidgets('answers a new paid order, with its deadline', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
      );

      expect(find.text(en.commitmentTitle), findsOneWidget);
      expect(find.text(en.acceptOrder), findsOneWidget);
      expect(find.text(en.rejectOrder), findsOneWidget);
      expect(find.textContaining('Please answer by'), findsOneWidget);
      expect(find.text(en.commitmentNotAskedYet), findsOneWidget);
      // The farmer cannot cancel a paid order before answering.
      expect(find.text(en.cancelOrder), findsNothing);
    });

    // Farmer · Answer a new order board: the note, and a reject that asks
    // why (farmer reasons only) and says the buyer is refunded.
    testWidgets('rejecting asks why, then sends the reason', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
      );
      expect(find.text(en.farmerAnswerNote), findsOneWidget);

      await tester.ensureVisible(find.text(en.rejectOrder));
      await tester.tap(find.text(en.rejectOrder));
      await tester.pumpAndSettle();

      expect(find.text(en.farmerRejectRefundNote), findsOneWidget);
      expect(find.text(en.declineReasonDate), findsOneWidget);
      expect(find.text(en.declineReasonPrice), findsOneWidget);
      expect(find.text(en.declineReasonCropNotReady), findsOneWidget);
      expect(find.text(en.declineReasonOther), findsOneWidget);
      expect(find.text(en.declineReasonCapacity), findsNothing);
      expect(find.text(en.declineReasonRoute), findsNothing);
      verifyNever(() => orderBloc.add(any(that: isA<RejectOrder>())));

      await tester.tap(find.text(en.declineReasonPrice));
      await tester.pumpAndSettle();

      final sent = verify(
        () => orderBloc.add(captureAny(that: isA<RejectOrder>())),
      ).captured.single as RejectOrder;
      expect(sent.declineReason, 'price');
      expect(sent.farmerId, 'farmer-001');
    });

    testWidgets('dismissing the reasons rejects nothing', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
      );
      await tester.ensureVisible(find.text(en.rejectOrder));
      await tester.tap(find.text(en.rejectOrder));
      await tester.pumpAndSettle();
      await tester.tap(find.text(en.cancel).last);
      await tester.pumpAndSettle();

      verifyNever(() => orderBloc.add(any(that: isA<RejectOrder>())));
    });

    testWidgets('after accepting, waits for the trucker', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, farmer: 'accepted', trucker: 'pending'),
        UserRole.farmer,
        'farmer-001',
      );

      expect(find.text(en.acceptOrder), findsNothing);
      expect(find.text(en.rejectOrder), findsNothing);
      expect(find.textContaining('Waiting, answer due'), findsOneWidget);
    });

    testWidgets('cancels without penalty after the transport fell through',
        (tester) async {
      await show(
        tester,
        _order(
          OrderStatus.awaitingTransport,
          farmer: 'accepted',
          trucker: 'declined',
          transportFailure: 'trucker_declined',
        ),
        UserRole.farmer,
        'farmer-001',
      );

      expect(find.text(en.farmerCancelWithoutPenalty), findsOneWidget);
      expect(find.textContaining(en.transportFailedFarmerBody), findsOneWidget);
      expect(
        find.textContaining(en.transportFailureTruckerDeclined),
        findsOneWidget,
      );
    });
  });

  group('trucker', () {
    testWidgets('is not asked before the farmer accepts', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.trucker,
        'trucker-001',
      );

      expect(find.text(en.truckerAcceptRequest), findsNothing);
      expect(find.text(en.truckerDeclineRequest), findsNothing);
      expect(find.text(en.commitmentTruckerNotAskedNote), findsOneWidget);
      // Not committed: no contact with the parties yet.
      expect(find.text(en.contactBuyer), findsNothing);
    });

    testWidgets('accepts the transport request', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, farmer: 'accepted', trucker: 'pending'),
        UserRole.trucker,
        'trucker-001',
      );

      await tester.tap(find.text(en.truckerAcceptRequest));
      await tester.pump();

      verify(
        () => orderBloc.add(
          any(
            that: isA<TruckerAcceptOrder>()
                .having((e) => e.truckerId, 'truckerId', 'trucker-001'),
          ),
        ),
      ).called(1);
    });

    testWidgets('declines with a reason', (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, farmer: 'accepted', trucker: 'pending'),
        UserRole.trucker,
        'trucker-001',
      );

      await tester.tap(find.text(en.truckerDeclineRequest));
      await tester.pumpAndSettle();
      expect(find.text(en.declineReasonTitle), findsOneWidget);
      await tester.tap(find.text(en.declineReasonCapacity));
      await tester.pumpAndSettle();

      verify(
        () => orderBloc.add(
          any(
            that: isA<TruckerDeclineOrder>()
                .having((e) => e.declineReason, 'reason', 'capacity'),
          ),
        ),
      ).called(1);
    });

    testWidgets('may drop out of an accepted order before pickup',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted, farmer: 'accepted', trucker: 'accepted'),
        UserRole.trucker,
        'trucker-001',
      );

      expect(find.text(en.truckerDropOut), findsOneWidget);
      await tester.tap(find.text(en.truckerDropOut));
      await tester.pumpAndSettle();
      expect(find.text(en.truckerDropOutWarning), findsOneWidget);
    });

    testWidgets('no drop-out on a server without commitments', (tester) async {
      await show(
        tester,
        _order(OrderStatus.accepted, commitments: false),
        UserRole.trucker,
        'trucker-001',
      );

      expect(find.text(en.truckerDropOut), findsNothing);
      expect(find.text(en.commitmentTitle), findsNothing);
    });
  });

  group('buyer', () {
    testWidgets('cannot cancel while the parties decide, and is told why',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.cancelOrder), findsNothing);
      expect(find.text(en.commitmentBuyerWaitingNote), findsOneWidget);
    });

    testWidgets('still cancels a funded order on a server without commitments',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, commitments: false),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.cancelOrder), findsOneWidget);
      expect(find.text(en.commitmentTitle), findsNothing);
    });

    testWidgets('cancels for a full refund after the transport fell through',
        (tester) async {
      await show(
        tester,
        _order(
          OrderStatus.awaitingTransport,
          farmer: 'accepted',
          trucker: 'expired',
          transportFailure: 'trucker_expired',
        ),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.statusAwaitingTransport), findsWidgets);
      expect(
        find.textContaining('Cancel for a full refund by'),
        findsOneWidget,
      );
      await tester.tap(find.text(en.cancelOrder));
      await tester.pumpAndSettle();
      expect(find.text(en.cancelOrderAwaitingTransportBody), findsOneWidget);
    });
  });

  testWidgets('a status this build does not know shows Updating, no actions',
      (tester) async {
    await show(
      tester,
      _order(OrderStatus.unknown),
      UserRole.buyer,
      'buyer-001',
    );

    // The summary card's status chip says so (the timeline may too).
    expect(
      find.descendant(
        of: find.byKey(const Key('order_summary_hero')),
        matching: find.text(en.orderStatusUpdating),
      ),
      findsOneWidget,
    );
    expect(find.text(en.cancelOrder), findsNothing);
    expect(find.text(en.payNow), findsNothing);
  });

  // LT-03: deadlines on the viewer's clock. 08:00 UTC is 11:00 in Ethiopia,
  // 5:00 in the morning on the Ethiopian clock.
  group('Ethiopian clock', () {
    final am = lookupAppLocalizations(const Locale('am'));
    final om = lookupAppLocalizations(const Locale('om'));

    String withSmall24h(String sentence, String time, String time24) =>
        sentence.replaceFirst(time, '$time ($time24)');

    testWidgets('English keeps the 24-hour clock, no companion',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
      );
      expect(find.text(en.commitmentAnswerBy('Oct 27 11:00')), findsOneWidget);
    });

    testWidgets('farmer in Amharic: the answer deadline with the 24-hour time',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
        locale: const Locale('am'),
      );
      const time = 'Oct 27 ከጠዋቱ 5:00';
      expect(
        find.text(withSmall24h(am.commitmentAnswerBy(time), time, '11:00')),
        findsOneWidget,
      );
    });

    testWidgets('trucker in Amharic: the answer deadline with the 24-hour time',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded, farmer: 'accepted', trucker: 'pending'),
        UserRole.trucker,
        'trucker-001',
        locale: const Locale('am'),
      );
      const time = 'Oct 28 ከጠዋቱ 5:00';
      expect(
        find.text(withSmall24h(am.commitmentAnswerBy(time), time, '11:00')),
        findsOneWidget,
      );
    });

    testWidgets('buyer in Afaan Oromo: who is waiting, and until when',
        (tester) async {
      await show(
        tester,
        _order(OrderStatus.funded),
        UserRole.buyer,
        'buyer-001',
        locale: const Locale('om'),
      );
      const time = 'Oct 27 5:00 ganama';
      expect(
        find.text(withSmall24h(om.commitmentWaitingUntil(time), time, '11:00')),
        findsOneWidget,
      );
    });

    testWidgets('buyer in Amharic: the refund choice deadline after a failure',
        (tester) async {
      await show(
        tester,
        _order(
          OrderStatus.awaitingTransport,
          farmer: 'accepted',
          trucker: 'declined',
          transportFailure: 'trucker_declined',
        ),
        UserRole.buyer,
        'buyer-001',
        locale: const Locale('am'),
      );
      const time = 'Oct 29 ከጠዋቱ 5:00';
      expect(
        find.textContaining(
          withSmall24h(am.transportFailedBuyerBody(time), time, '11:00'),
        ),
        findsOneWidget,
      );
    });
  });

  // Buyer · Order detail and Farmer · Answer a new order boards: the summary
  // card shows each party their own money.
  group('summary card', () {
    Order priced(OrderStatus status) => _order(status)
      ..cropCost = 8000
      ..transportCost = 300
      ..commissionAmount = 425
      ..orderTotal = 8725;

    Finder inHero(String text) => find.descendant(
          of: find.byKey(const Key('order_summary_hero')),
          matching: find.text(text),
        );

    testWidgets('the farmer sees what they receive', (tester) async {
      await show(
        tester,
        priced(OrderStatus.funded),
        UserRole.farmer,
        'farmer-001',
      );

      // Farmer · Answer a new order: the header names the new order, the
      // card shows what they receive, the buyer and that the money is in.
      expect(inHero(en.detailNewOrderTitle), findsOneWidget);
      expect(find.text(en.orderYouReceive), findsOneWidget);
      expect(find.text(en.etbCurrency('8,000')), findsOneWidget);
      expect(find.text('Buyer Bee'), findsOneWidget);
      expect(find.text(en.detailPaidIntoEscrow), findsOneWidget);
    });

    testWidgets('the buyer sees the total and the farmer', (tester) async {
      await show(
        tester,
        priced(OrderStatus.funded),
        UserRole.buyer,
        'buyer-001',
      );

      // Buyer · Order detail: the money held closes the page.
      expect(find.text(en.detailHeldInEscrow), findsOneWidget);
      expect(find.text(en.etbCurrency('8,725')), findsOneWidget);
      expect(inHero(en.liveTripFrom('Farmer Fikru')), findsOneWidget);
    });

    testWidgets('the trucker sees the transport pay', (tester) async {
      await show(
        tester,
        priced(OrderStatus.accepted),
        UserRole.trucker,
        'trucker-001',
      );

      expect(find.text(en.orderYourTransportPay), findsOneWidget);
      expect(find.text(en.etbCurrency('300')), findsOneWidget);
    });

    testWidgets('no escrow line before the money is in', (tester) async {
      await show(
        tester,
        priced(OrderStatus.created),
        UserRole.buyer,
        'buyer-001',
      );

      expect(find.text(en.detailHeldInEscrow), findsNothing);
      expect(find.text(en.orderTotal), findsOneWidget);
    });
  });
}

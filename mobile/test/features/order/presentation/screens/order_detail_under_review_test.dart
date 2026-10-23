import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class MockRatingBloc extends Mock implements RatingBloc {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockEscrowRepository extends Mock implements BaseEscrowRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

const _buyerId = 'buyer-001';
const _farmerId = 'farmer-001';
const _truckerId = 'trucker-001';
const _escrowId = 'escrow-order-order-001';

Order _order(OrderStatus status, {DateTime? pickupDeadlineAt}) => Order()
  ..orderId = 'order-001'
  ..listingId = 'listing-001'
  ..buyerId = _buyerId
  ..farmerId = _farmerId
  ..truckerId = _truckerId
  ..tripId = 'trip-001'
  ..buyerName = 'Buyer Bee'
  ..farmerName = 'Farmer Fikru'
  ..truckerName = 'Trucker Tesfaye'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 85
  ..totalPrice = 8500
  ..status = status
  ..createdAt = DateTime(2026, 10, 2)
  ..version = 1
  ..pickupDeadlineAt = pickupDeadlineAt;

Payment _escrow(PaymentStatus status) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = _escrowId
    ..deliveryId = ''
    ..orderId = 'order-001'
    ..farmerId = _farmerId
    ..buyerId = _buyerId
    ..amount = 8500
    ..commission = 212.5
    ..commissionRate = 0.025
    ..farmerPayout = 8287.5
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..version = 1;
}

/// FRD-06: order detail reads the order's escrow; when it is held the buyer,
/// the farmer and the trucker see "Under Review", and the actions the server
/// refuses while held (the farmer's Accept, the transport-failure cancel) are
/// not offered.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;
  late MockRatingBloc ratingBloc;
  late MockEscrowBloc escrowBloc;
  late MockEscrowRepository escrowRepo;
  late StreamController<OrderState> orderStates;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    orderStates = StreamController<OrderState>.broadcast();
    when(() => orderBloc.stream).thenAnswer((_) => orderStates.stream);
    when(() => orderBloc.add(any())).thenReturn(null);
    when(() => orderBloc.isClosed).thenReturn(false);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());

    deliveryBloc = MockDeliveryBloc();
    final deliveryRepo = MockDeliveryRepository();
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => null);

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

    escrowBloc = MockEscrowBloc();
    escrowRepo = MockEscrowRepository();
    when(() => escrowBloc.repository).thenReturn(escrowRepo);
    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => escrowBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  tearDown(() => orderStates.close());

  void signIn(UserRole role, String userId) =>
      when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: userId,
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
          userName: 'User',
        ),
      );

  final en = lookupAppLocalizations(const Locale('en'));
  final underReview = en.escrowStatusHeld.toUpperCase();

  void escrowIs(PaymentStatus? status) =>
      when(() => escrowRepo.refreshEscrow(_escrowId)).thenAnswer(
        (_) async => status == null ? null : _escrow(status),
      );

  /// Opens the screen the way the orders tab does: a fresh bloc that loads
  /// and then emits the order. [alreadyLoaded]: the bloc holds the order
  /// from the start and emits nothing new.
  Future<void> open(
    WidgetTester tester,
    Order order, {
    bool alreadyLoaded = false,
    bool withEscrow = true,
  }) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => orderBloc.state).thenReturn(
      alreadyLoaded ? SingleOrderLoaded(order: order) : const OrderLoading(),
    );
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
          BlocProvider<RatingBloc>.value(value: ratingBloc),
          if (withEscrow) BlocProvider<EscrowBloc>.value(value: escrowBloc),
        ],
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: Locale('en'),
          home: OrderDetailScreen(orderId: 'order-001'),
        ),
      ),
    );
    // The route-scoped bloc's load lands after the screen is up.
    if (!alreadyLoaded) orderStates.add(SingleOrderLoaded(order: order));
    await tester.pumpAndSettle();
  }

  FilledButton acceptButton(WidgetTester tester) => tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, en.acceptOrder),
      );

  for (final (role, userId) in const [
    (UserRole.buyer, _buyerId),
    (UserRole.farmer, _farmerId),
    (UserRole.trucker, _truckerId),
  ]) {
    testWidgets('${role.name}: a held order escrow reads Under Review',
        (tester) async {
      signIn(role, userId);
      escrowIs(PaymentStatus.held);

      await open(tester, _order(OrderStatus.delivered));

      expect(find.text(underReview), findsOneWidget);
      expect(find.text(en.escrowDescHeld), findsOneWidget);
      verify(() => escrowRepo.refreshEscrow(_escrowId)).called(1);
    });

    testWidgets('${role.name}: an escrowed order escrow shows no badge',
        (tester) async {
      signIn(role, userId);
      escrowIs(PaymentStatus.escrowed);

      await open(tester, _order(OrderStatus.delivered));

      expect(find.text(underReview), findsNothing);
    });
  }

  testWidgets('farmer: a funded order under review offers Reject, not Accept',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);
    escrowIs(PaymentStatus.held);

    await open(tester, _order(OrderStatus.funded));

    expect(find.text(underReview), findsOneWidget);
    expect(find.text(en.rejectOrder), findsOneWidget);
    expect(find.text(en.acceptOrder), findsNothing);
  });

  testWidgets('farmer: a funded order not under review offers both (control)',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);
    escrowIs(PaymentStatus.escrowed);

    await open(tester, _order(OrderStatus.funded));

    expect(find.text(en.rejectOrder), findsOneWidget);
    expect(find.text(en.acceptOrder), findsOneWidget);
  });

  testWidgets('buyer: no transport-failure cancel while under review',
      (tester) async {
    signIn(UserRole.buyer, _buyerId);
    escrowIs(PaymentStatus.held);
    final passed = DateTime.now().toUtc().subtract(const Duration(hours: 1));

    await open(
      tester,
      _order(OrderStatus.accepted, pickupDeadlineAt: passed),
    );

    expect(find.text(underReview), findsOneWidget);
    expect(find.text(en.cancelOrder), findsNothing);
    expect(find.text(en.orderTransportFailedBuyerHint), findsNothing);
  });

  testWidgets('buyer: a funded order under review can still be cancelled',
      (tester) async {
    signIn(UserRole.buyer, _buyerId);
    escrowIs(PaymentStatus.held);

    await open(tester, _order(OrderStatus.funded));

    expect(find.text(underReview), findsOneWidget);
    expect(find.text(en.cancelOrder), findsOneWidget);
  });

  testWidgets('an unpaid order is not looked up', (tester) async {
    signIn(UserRole.buyer, _buyerId);
    escrowIs(PaymentStatus.held);

    await open(tester, _order(OrderStatus.created));

    expect(find.text(underReview), findsNothing);
    verifyNever(() => escrowRepo.refreshEscrow(any()));
  });

  testWidgets('farmer: Accept waits for the hold check, then is enabled',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);
    final answer = Completer<Payment?>();
    when(() => escrowRepo.refreshEscrow(_escrowId))
        .thenAnswer((_) => answer.future);

    await open(tester, _order(OrderStatus.funded));
    expect(acceptButton(tester).onPressed, isNull);

    answer.complete(_escrow(PaymentStatus.escrowed));
    await tester.pumpAndSettle();
    expect(acceptButton(tester).onPressed, isNotNull);
    expect(find.text(underReview), findsNothing);
  });

  testWidgets('farmer: a failed hold check enables Accept (offline works)',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);
    when(() => escrowRepo.refreshEscrow(_escrowId))
        .thenThrow(StateError('offline'));

    await open(tester, _order(OrderStatus.funded));

    expect(acceptButton(tester).onPressed, isNotNull);
    expect(find.text(underReview), findsNothing);
  });

  testWidgets('farmer: no escrow side above the screen enables Accept',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);

    await open(tester, _order(OrderStatus.funded), withEscrow: false);

    expect(acceptButton(tester).onPressed, isNotNull);
  });

  testWidgets('an older hold check that answers last is ignored',
      (tester) async {
    signIn(UserRole.buyer, _buyerId);
    final first = Completer<Payment?>();
    var calls = 0;
    when(() => escrowRepo.refreshEscrow(_escrowId)).thenAnswer((_) {
      calls++;
      return calls == 1
          ? first.future
          : Future.value(_escrow(PaymentStatus.escrowed));
    });

    await open(tester, _order(OrderStatus.delivered));
    // A reload (the contact pull) emits the order again: a second check.
    orderStates.add(SingleOrderLoaded(order: _order(OrderStatus.delivered)));
    await tester.pumpAndSettle();
    first.complete(_escrow(PaymentStatus.held));
    await tester.pumpAndSettle();

    expect(calls, 2);
    expect(find.text(underReview), findsNothing);
  });

  testWidgets('a settled order is not looked up, even with a held copy',
      (tester) async {
    signIn(UserRole.farmer, _farmerId);
    escrowIs(PaymentStatus.held);

    await open(tester, _order(OrderStatus.settled));

    expect(find.text(underReview), findsNothing);
    verifyNever(() => escrowRepo.refreshEscrow(any()));
  });

  testWidgets('a bloc that already holds the order is still checked',
      (tester) async {
    signIn(UserRole.trucker, _truckerId);
    escrowIs(PaymentStatus.held);

    await open(tester, _order(OrderStatus.delivered), alreadyLoaded: true);

    expect(find.text(underReview), findsOneWidget);
  });

  testWidgets('a failed lookup shows no badge', (tester) async {
    signIn(UserRole.buyer, _buyerId);
    when(() => escrowRepo.refreshEscrow(_escrowId))
        .thenThrow(StateError('offline'));

    await open(tester, _order(OrderStatus.delivered));

    expect(find.text(underReview), findsNothing);
  });
}

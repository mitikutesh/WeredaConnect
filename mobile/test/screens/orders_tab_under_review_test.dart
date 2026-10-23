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
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockEscrowRepository extends Mock implements BaseEscrowRepository {}

class FakeOrderEvent extends Fake implements OrderEvent {}

Order _order(String id, OrderStatus status) => Order()
  ..orderId = id
  ..listingId = 'listing-1'
  ..buyerId = 'buyer-1'
  ..farmerId = 'farmer-1'
  ..truckerId = 'trucker-1'
  ..buyerName = 'Buyer'
  ..farmerName = 'Farmer'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 50
  ..totalPrice = 5000
  ..status = status
  ..createdAt = DateTime(2026, 10, 2)
  ..version = 1;

Payment _heldEscrow(String orderId) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = 'escrow-order-$orderId'
    ..deliveryId = ''
    ..orderId = orderId
    ..farmerId = 'farmer-1'
    ..buyerId = 'buyer-1'
    ..amount = 5000
    ..commission = 125
    ..commissionRate = 0.025
    ..farmerPayout = 4875
    ..status = PaymentStatus.held
    ..createdAt = now
    ..updatedAt = now
    ..version = 1;
}

/// FRD-06: the order list asks once for this party's held escrows and marks
/// those orders "Under Review", for the buyer, the farmer and the trucker.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;
  late MockEscrowBloc escrowBloc;
  late MockEscrowRepository escrowRepo;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    escrowBloc = MockEscrowBloc();
    escrowRepo = MockEscrowRepository();
    when(() => orderBloc.state).thenReturn(
      OrdersLoaded(
        orders: [
          _order('ord-held', OrderStatus.funded),
          _order('ord-ok', OrderStatus.funded),
        ],
      ),
    );
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => escrowBloc.repository).thenReturn(escrowRepo);
    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => escrowBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => escrowRepo.refreshHistory(
        userId: any(named: 'userId'),
        statusFilter: any(named: 'statusFilter'),
        roleFilter: any(named: 'roleFilter'),
        limit: any(named: 'limit'),
        nextToken: any(named: 'nextToken'),
      ),
    ).thenAnswer((_) async => [_heldEscrow('ord-held')]);
  });

  void signIn(UserRole role) => when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: '${role.name}-1',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
          userName: 'User',
        ),
      );

  Widget screen() => MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<EscrowBloc>.value(value: escrowBloc),
        ],
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: Locale('en'),
          home: Scaffold(body: OrdersTabScreen()),
        ),
      );

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 3000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  final en = lookupAppLocalizations(const Locale('en'));

  for (final role in const [
    UserRole.buyer,
    UserRole.farmer,
    UserRole.trucker,
  ]) {
    testWidgets('${role.name}: only the held order reads Under Review',
        (tester) async {
      tallView(tester);
      signIn(role);

      await tester.pumpWidget(screen());
      await tester.pumpAndSettle();

      expect(find.text(en.escrowStatusHeld), findsOneWidget);
      verify(
        () => escrowRepo.refreshHistory(
          userId: '${role.name}-1',
          statusFilter: 'held',
          roleFilter: role.name,
          limit: 50,
        ),
      ).called(1);
    });
  }

  testWidgets('farmer: Accept is offered on the other order only',
      (tester) async {
    tallView(tester);
    signIn(UserRole.farmer);

    await tester.pumpWidget(screen());
    await tester.pumpAndSettle();

    expect(find.text(en.rejectOrder), findsNWidgets(2));
    expect(find.text(en.acceptOrder), findsOneWidget);
  });

  void heldReadAnswers(Future<List<Payment>> Function() answer) => when(
        () => escrowRepo.refreshHistory(
          userId: any(named: 'userId'),
          statusFilter: any(named: 'statusFilter'),
          roleFilter: any(named: 'roleFilter'),
          limit: any(named: 'limit'),
          nextToken: any(named: 'nextToken'),
        ),
      ).thenAnswer((_) => answer());

  List<FilledButton> acceptButtons(WidgetTester tester) => tester
      .widgetList<FilledButton>(
        find.widgetWithText(FilledButton, en.acceptOrder),
      )
      .toList();

  testWidgets('a held copy of a settled order shows no badge', (tester) async {
    tallView(tester);
    signIn(UserRole.buyer);
    when(() => orderBloc.state).thenReturn(
      OrdersLoaded(orders: [_order('ord-held', OrderStatus.settled)]),
    );

    await tester.pumpWidget(screen());
    await tester.pumpAndSettle();

    // The cache (offline, or a failed refresh) still says held; the order
    // moved on with its money.
    expect(find.text(en.escrowStatusHeld), findsNothing);
  });

  testWidgets('farmer: Accept waits for the held read, then is enabled',
      (tester) async {
    tallView(tester);
    signIn(UserRole.farmer);
    final answer = Completer<List<Payment>>();
    heldReadAnswers(() => answer.future);

    await tester.pumpWidget(screen());
    await tester.pumpAndSettle();
    expect(acceptButtons(tester), hasLength(2));
    for (final button in acceptButtons(tester)) {
      expect(button.onPressed, isNull);
    }

    answer.complete(const []);
    await tester.pumpAndSettle();
    for (final button in acceptButtons(tester)) {
      expect(button.onPressed, isNotNull);
    }
  });

  testWidgets('farmer: a failed held read enables Accept', (tester) async {
    tallView(tester);
    signIn(UserRole.farmer);
    heldReadAnswers(() => Future.error(StateError('offline')));

    await tester.pumpWidget(screen());
    await tester.pumpAndSettle();

    expect(find.text(en.escrowStatusHeld), findsNothing);
    expect(acceptButtons(tester), hasLength(2));
    for (final button in acceptButtons(tester)) {
      expect(button.onPressed, isNotNull);
    }
  });

  testWidgets('a filter tap does not ask again', (tester) async {
    tallView(tester);
    signIn(UserRole.buyer);

    await tester.pumpWidget(screen());
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilterChip, en.statusFunded).first);
    await tester.pumpAndSettle();

    verify(
      () => escrowRepo.refreshHistory(
        userId: any(named: 'userId'),
        statusFilter: any(named: 'statusFilter'),
        roleFilter: any(named: 'roleFilter'),
        limit: any(named: 'limit'),
      ),
    ).called(1);
  });
}

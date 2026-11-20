import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class MockOrderRepository extends Mock implements BaseOrderRepository {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// ESC-09: paid-out orders move on from delivered with their money, so the
/// Orders tab filters Disbursing and Settled too.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    when(() => orderBloc.state).thenReturn(const OrdersLoaded(orders: []));
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _tokens,
        role: UserRole.buyer,
        userName: 'Buyer',
      ),
    );
  });

  Widget buildScreen(Locale locale) => MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          // The app's delegates: Afan Oromo has no built-in Material strings.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: const Scaffold(body: OrdersTabScreen()),
        ),
      );

  // A wide window: the pills still sit in one phone-width row.
  void useDesktop(WidgetTester tester) {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  testWidgets('shows Disbursing and Settled chips next to Delivered',
      (tester) async {
    useDesktop(tester);
    await tester.pumpWidget(buildScreen(const Locale('en')));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    for (final label in [
      en.deliveryFilterDelivered,
      en.statusDisbursing,
      en.statusSettled,
    ]) {
      expect(find.widgetWithText(FwFilterPill, label), findsOneWidget);
    }
  });

  for (final (label, status) in [
    ('Disbursing', 'disbursing'),
    ('Settled', 'settled'),
  ]) {
    testWidgets('the $label chip filters on $status', (tester) async {
      useDesktop(tester);
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      final pill = find.widgetWithText(FwFilterPill, label);
      await tester.ensureVisible(pill);
      await tester.tap(pill);
      await tester.pump();

      verify(
        () => orderBloc.add(
          FilterOrders(
            userId: 'buyer-001',
            role: 'buyer',
            statusFilter: status,
          ),
        ),
      ).called(1);
    });
  }

  // Home's Pending Orders tile counts the orders waiting for the farmer's
  // answer; the farmer's Orders tab has the same view as a chip.
  group('Waiting for you', () {
    void signInFarmer() {
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _tokens,
          role: UserRole.farmer,
          userName: 'Farmer',
        ),
      );
    }

    tearDown(() => ordersFilterRequest.value = null);

    testWidgets('a farmer chip, after All, that filters on their turn',
        (tester) async {
      useDesktop(tester);
      signInFarmer();
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      final pill = find.widgetWithText(FwFilterPill, 'Waiting for you');
      expect(pill, findsOneWidget);
      expect(
        tester.getTopLeft(pill).dx,
        lessThan(
          tester.getTopLeft(find.widgetWithText(FwFilterPill, 'Pending')).dx,
        ),
      );
      await tester.tap(pill);
      await tester.pump();

      verify(
        () => orderBloc.add(
          const FilterOrders(
            userId: 'farmer-001',
            role: 'farmer',
            statusFilter: ordersFilterYourTurn,
          ),
        ),
      ).called(1);
    });

    testWidgets('no such chip for a buyer', (tester) async {
      useDesktop(tester);
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      expect(
          find.widgetWithText(FwFilterPill, 'Waiting for you'), findsNothing);
    });

    testWidgets('opens on it when Home asked (the Pending Orders tile)',
        (tester) async {
      useDesktop(tester);
      signInFarmer();
      ordersFilterRequest.value = ordersFilterYourTurn;
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      verify(
        () => orderBloc.add(
          const FilterOrders(
            userId: 'farmer-001',
            role: 'farmer',
            statusFilter: ordersFilterYourTurn,
          ),
        ),
      ).called(1);
      verifyNever(
        () => orderBloc.add(
          const LoadUserOrders(userId: 'farmer-001', role: 'farmer'),
        ),
      );
      expect(ordersFilterRequest.value, isNull);
    });

    testWidgets('a tab kept alive switches when Home asks again',
        (tester) async {
      useDesktop(tester);
      signInFarmer();
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      ordersFilterRequest.value = ordersFilterYourTurn;
      await tester.pump();

      verify(
        () => orderBloc.add(
          const FilterOrders(
            userId: 'farmer-001',
            role: 'farmer',
            statusFilter: ordersFilterYourTurn,
          ),
        ),
      ).called(1);
      expect(ordersFilterRequest.value, isNull);
    });
  });

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('the chips are labelled in ${locale.languageCode}',
        (tester) async {
      useDesktop(tester);
      await tester.pumpWidget(buildScreen(locale));
      await tester.pumpAndSettle();

      final l10n = lookupAppLocalizations(locale);
      for (final label in [l10n.statusDisbursing, l10n.statusSettled]) {
        expect(find.widgetWithText(FwFilterPill, label), findsOneWidget);
      }
    });
  }

  // E-5 and E-6: an Accept from a card says whether it was sent or waits
  // on the phone; a failed one keeps the list and says why.
  group('farmer answers from a card', () {
    late StreamController<OrderState> orderStream;
    late MockConnectivityMonitor monitor;
    final en = lookupAppLocalizations(const Locale('en'));
    final order = Order()
      ..orderId = 'order-1'
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-001'
      ..farmerId = 'farmer-001'
      ..buyerName = 'Buyer Bee'
      ..farmerName = 'Farmer Fikru'
      ..cropType = 'teff'
      ..quantity = 100
      ..pricePerUnit = 85
      ..totalPrice = 8500
      ..cropCost = 8500
      ..status = OrderStatus.funded
      ..farmerResponse = 'pending'
      ..createdAt = DateTime.utc(2026, 11)
      ..version = 1;

    setUp(() {
      orderStream = StreamController<OrderState>.broadcast();
      monitor = MockConnectivityMonitor();
      when(() => monitor.isConnected()).thenAnswer((_) async => true);
      when(() => orderBloc.stream).thenAnswer((_) => orderStream.stream);
      when(() => orderBloc.state).thenReturn(OrdersLoaded(orders: [order]));
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'farmer-001',
          tokens: _tokens,
          role: UserRole.farmer,
          userName: 'Farmer',
        ),
      );
    });

    tearDown(() => orderStream.close());

    Future<void> pumpList(WidgetTester tester) async {
      tester.view.physicalSize = const Size(390, 1000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        Provider<ConnectivityMonitor>.value(
          value: monitor,
          child: buildScreen(const Locale('en')),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('a failed accept keeps the list and says why', (tester) async {
      await pumpList(tester);
      await tester.tap(find.text(en.acceptOrder));
      verify(
        () => orderBloc.add(
          const AcceptOrder(orderId: 'order-1', farmerId: 'farmer-001'),
        ),
      ).called(1);

      orderStream.add(const OrderLoading());
      await tester.pump();
      await tester.pump();
      expect(find.text(en.acceptOrder), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsNothing);

      orderStream.add(
        const OrderError(message: 'offline', code: AppErrorCode.network),
      );
      await tester.pump();
      await tester.pump();
      // The card is still there, not the error view with its Retry.
      expect(find.text(en.acceptOrder), findsOneWidget);
      expect(find.text(en.retry), findsNothing);
      expect(
        find.text(localizeErrorCode(AppErrorCode.network, en)),
        findsOneWidget,
      );
    });

    testWidgets('Reject waits while an Accept from the card runs',
        (tester) async {
      await pumpList(tester);
      await tester.tap(find.text(en.acceptOrder));

      orderStream.add(const OrderLoading());
      await tester.pump();
      await tester.pump();

      OutlinedButton reject() => tester.widget<OutlinedButton>(
            find.widgetWithText(OutlinedButton, en.rejectOrder),
          );
      FilledButton accept() => tester.widget<FilledButton>(
            find.widgetWithText(FilledButton, en.acceptOrder),
          );
      expect(reject().onPressed, isNull);
      expect(accept().onPressed, isNull);

      // No reason sheet can open, so no second (opposite) answer.
      verifyNever(() => orderBloc.add(any(that: isA<RejectOrder>())));

      // The accept failed: both answers are offered again.
      orderStream.add(
        const OrderError(message: 'offline', code: AppErrorCode.network),
      );
      await tester.pump();
      await tester.pump();
      expect(reject().onPressed, isNotNull);
      expect(accept().onPressed, isNotNull);
    });

    for (final (online, message) in [
      (true, en.orderAcceptedSent),
      (false, en.savedOnPhoneWillSend),
    ]) {
      testWidgets('an accept ${online ? 'online' : 'offline'}: "$message"',
          (tester) async {
        when(() => monitor.isConnected()).thenAnswer((_) async => online);
        await pumpList(tester);
        await tester.tap(find.text(en.acceptOrder));

        orderStream.add(const OrderAccepted());
        await tester.pump();
        await tester.pump();

        expect(find.text(message), findsOneWidget);
        // The list is read again.
        verify(
          () => orderBloc.add(
            const LoadUserOrders(userId: 'farmer-001', role: 'farmer'),
          ),
        ).called(2);
      });
    }

    testWidgets('a load error that is not a card answer shows the error view',
        (tester) async {
      await pumpList(tester);

      orderStream.add(
        const OrderError(message: 'offline', code: AppErrorCode.network),
      );
      await tester.pump();
      await tester.pump();

      expect(find.text(en.retry), findsOneWidget);
      expect(find.text(en.acceptOrder), findsNothing);
    });
  });

  // The buyer has no Home tab (design canvas page M): what waits for the
  // buyer leads the Orders list as Action needed cards.
  group('the buyer\'s action needed cards', () {
    final en = lookupAppLocalizations(const Locale('en'));
    late MockOrderRepository repository;

    Order order(String id, OrderStatus status, {int day = 1}) => Order()
      ..orderId = id
      ..listingId = 'listing-$id'
      ..buyerId = 'buyer-001'
      ..farmerId = 'farmer-001'
      ..buyerName = 'Buyer Bee'
      ..farmerName = 'Farmer Fikru'
      ..cropType = 'teff'
      ..quantity = 1500
      ..pricePerUnit = 85
      ..totalPrice = 127500
      ..status = status
      ..createdAt = DateTime.utc(2026, 11, day)
      ..version = 1;

    late List<Order> orders;

    setUp(() {
      orders = [
        order('paid', OrderStatus.funded, day: 4),
        order('unpaid', OrderStatus.created, day: 3),
        order('failed', OrderStatus.paymentFailed, day: 2),
        order('arrived', OrderStatus.delivered),
      ];
      repository = MockOrderRepository();
      when(() => orderBloc.repository).thenReturn(repository);
      when(
        () => repository.getOrdersByUser(any(), role: any(named: 'role')),
      ).thenAnswer((_) async => orders);
      when(() => repository.ordersWithQueuedCheckIns(any(), any()))
          .thenAnswer((i) async => i.positionalArguments.first as List<Order>);
      when(() => orderBloc.state)
          .thenAnswer((_) => OrdersLoaded(orders: orders));
    });

    Future<void> pumpPhone(WidgetTester tester) async {
      tester.view.physicalSize = const Size(390, 2400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();
    }

    testWidgets('pay, pay again and check the delivery lead the list',
        (tester) async {
      await pumpPhone(tester);

      expect(find.byType(ActionNeededList), findsOneWidget);
      expect(find.text(en.actionNeededTitle.toUpperCase()), findsNWidgets(3));
      expect(find.text(en.actionPay), findsOneWidget);
      expect(find.text(en.actionPayAgain), findsOneWidget);
      expect(find.text(en.actionCheckDelivery), findsOneWidget);
      // Above every order of the list, the paid one that waits for nothing
      // from the buyer included.
      final firstCard = tester.getTopLeft(find.byType(OrderCard).first).dy;
      for (final label in [
        en.actionPay,
        en.actionPayAgain,
        en.actionCheckDelivery,
      ]) {
        expect(tester.getTopLeft(find.text(label)).dy, lessThan(firstCard));
      }
      expect(find.byType(OrderCard), findsNWidgets(4));
    });

    testWidgets('kilograms lead on the order cards, the quintals follow',
        (tester) async {
      await pumpPhone(tester);

      expect(
        find.textContaining('1,500 kg (15 quintals)'),
        findsNWidgets(4),
      );
    });

    testWidgets('nothing waits: no card, the first order is not pushed down',
        (tester) async {
      orders = [order('paid', OrderStatus.funded)];
      await pumpPhone(tester);

      expect(find.text(en.actionNeededTitle.toUpperCase()), findsNothing);
      expect(find.byType(OrderCard), findsOneWidget);
    });

    testWidgets('a status filter shows the orders alone', (tester) async {
      await pumpPhone(tester);
      final pill = find.widgetWithText(FwFilterPill, en.orderFunded);
      await tester.ensureVisible(pill);
      await tester.tap(pill);
      await tester.pumpAndSettle();

      expect(find.byType(ActionNeededList), findsNothing);
    });

    for (final role in [UserRole.farmer, UserRole.trucker]) {
      testWidgets('the ${role.name} reads theirs on Home, not here',
          (tester) async {
        when(() => authBloc.state).thenReturn(
          AuthAuthenticated(
            userId: role == UserRole.farmer ? 'farmer-001' : 'trucker-001',
            tokens: _tokens,
            role: role,
            userName: 'Someone',
          ),
        );
        await pumpPhone(tester);

        expect(find.byType(ActionNeededList), findsNothing);
        expect(find.byType(OrderCard), findsNWidgets(4));
        // The list leads with its first order, raised.
        expect(
          tester
              .widget<FwCard>(
                find.descendant(
                  of: find.byType(OrderCard).first,
                  matching: find.byType(FwCard),
                ),
              )
              .raised,
          isTrue,
        );
      });
    }
  });
}

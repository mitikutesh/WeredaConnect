import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockOrderBloc extends Mock implements OrderBloc {}

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

class _FakeOrderEvent extends Fake implements OrderEvent {}

/// Where the buyer ends after paying (persona simulation 2026-11-19).
///
/// BUG-17: an order opened by its own address on web (a link, a reload) is
/// the only page of the shell. The payment screen used to pop twice when the
/// payment went through, which removed that page and left a blank app.
/// BUG-21: paying straight after Place order ended on the market list.
/// BUG-23, BUG-19: one password prompt and one resubmission; a lost
/// connection says so and offers the payment again.
void main() {
  const orderId = '1fd6a372-7531-4755-b095-8d6cc4989d30';

  late _MockOrderBloc bloc;
  late StreamController<OrderState> states;
  late List<SubmitPayment> submitted;

  setUpAll(() => registerFallbackValue(_FakeOrderEvent()));

  setUp(() {
    bloc = _MockOrderBloc();
    states = StreamController<OrderState>.broadcast();
    submitted = [];
    when(() => bloc.state).thenReturn(const OrderInitial());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is SubmitPayment) submitted.add(event);
    });
  });

  tearDown(() => unawaited(states.close()));

  Order order() => Order()
    ..orderId = orderId
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..cropType = 'maize'
    ..quantity = 100
    ..pricePerUnit = 28
    ..totalPrice = 2800
    ..orderTotal = 3060
    ..status = OrderStatus.created
    ..createdAt = DateTime.utc(2026, 11, 19)
    ..version = 1;

  /// The web shell's shape: the Orders tab and an order's own page, whose
  /// stand-in opens the payment the way the order detail does.
  GoRouter shellRouter({required String initialLocation}) => GoRouter(
        initialLocation: initialLocation,
        routes: [
          ShellRoute(
            builder: (context, state, child) => child,
            routes: [
              GoRoute(
                path: '/orders',
                builder: (_, __) => const Scaffold(body: Text('orders tab')),
              ),
              GoRoute(
                path: '/orders/:orderId',
                builder: (context, state) => Scaffold(
                  body: Column(
                    children: [
                      Text('order ${state.pathParameters['orderId']}'),
                      TextButton(
                        onPressed: () => leaveOrderDetail(context),
                        child: const Text('back'),
                      ),
                      TextButton(
                        onPressed: () => Navigator.push(
                          context,
                          MaterialPageRoute<void>(
                            builder: (_) => PaymentInstructionsScreen(
                              order: order(),
                              fromOrderDetail: true,
                            ),
                          ),
                        ),
                        child: const Text('pay now'),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ],
      );

  Future<void> pumpRouter(WidgetTester tester, GoRouter router) async {
    addTearDown(router.dispose);
    await tester.pumpWidget(
      BlocProvider<OrderBloc>.value(
        value: bloc,
        child: MaterialApp.router(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          routerConfig: router,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  String location(GoRouter router) =>
      router.routerDelegate.currentConfiguration.uri.toString();

  Future<void> submit(WidgetTester tester) async {
    await tester.ensureVisible(find.text('Submit Payment'));
    await tester.tap(find.text('Submit Payment'));
    await tester.pump();
  }

  group('an order opened by its own address (BUG-17)', () {
    testWidgets('paying ends on the order, not on an empty app',
        (tester) async {
      final router = shellRouter(initialLocation: '/orders/$orderId');
      await pumpRouter(tester, router);

      await tester.tap(find.text('pay now'));
      await tester.pumpAndSettle();
      expect(find.byType(PaymentInstructionsScreen), findsOneWidget);

      await submit(tester);
      expect(submitted, hasLength(1));
      states.add(const PaymentSubmitted());
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.byType(PaymentInstructionsScreen), findsNothing);
      // The order's page is still there, under its own address.
      expect(find.text('order $orderId'), findsOneWidget);
      expect(location(router), '/orders/$orderId');
      expect(find.text('Payment submitted successfully!'), findsOneWidget);
    });

    testWidgets('Back with no page underneath goes to the Orders tab',
        (tester) async {
      final router = shellRouter(initialLocation: '/orders/$orderId');
      await pumpRouter(tester, router);

      await tester.tap(find.text('back'));
      await tester.pumpAndSettle();

      expect(find.text('orders tab'), findsOneWidget);
      expect(location(router), '/orders');
    });

    testWidgets('Back with the Orders tab underneath pops to it',
        (tester) async {
      final router = shellRouter(initialLocation: '/orders');
      await pumpRouter(tester, router);
      router.push<void>('/orders/$orderId').ignore();
      await tester.pumpAndSettle();
      expect(find.text('order $orderId'), findsOneWidget);

      await tester.tap(find.text('back'));
      await tester.pumpAndSettle();

      expect(find.text('orders tab'), findsOneWidget);
      expect(find.text('order $orderId'), findsNothing);
    });
  });

  group('paying straight after Place order', () {
    late _MockOrderRepository orders;

    setUp(() {
      orders = _MockOrderRepository();
      when(() => bloc.repository).thenReturn(orders);
      when(() => orders.getOrderById(any())).thenAnswer((_) async => null);
    });

    Future<void> openFromMarket(WidgetTester tester, {AuthBloc? auth}) async {
      Widget app = BlocProvider<OrderBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                // As Place order hands over to the payment.
                onPressed: () => Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => PaymentInstructionsScreen(order: order()),
                  ),
                ),
                child: const Text('market list'),
              ),
            ),
          ),
        ),
      );
      if (auth != null) {
        app = BlocProvider<AuthBloc>.value(value: auth, child: app);
      }
      await tester.pumpWidget(app);
      await tester.tap(find.text('market list'));
      await tester.pumpAndSettle();
    }

    testWidgets('a paid order opens its detail, not the market (BUG-21)',
        (tester) async {
      await openFromMarket(tester);

      await submit(tester);
      states.add(const PaymentSubmitted());
      await tester.pumpAndSettle();

      expect(find.byType(PaymentInstructionsScreen), findsNothing);
      final detail = tester.widget<OrderDetailScreen>(
        find.byType(OrderDetailScreen),
      );
      expect(detail.orderId, orderId);
      expect(find.text('market list'), findsNothing);
      verify(() => orders.getOrderById(orderId)).called(greaterThan(0));
    });

    testWidgets(
        'a lost connection says so, and Retry sends the same payment '
        '(BUG-19)', (tester) async {
      await openFromMarket(tester);

      await submit(tester);
      states.add(
        const OrderError(message: 'offline', code: AppErrorCode.network),
      );
      await tester.pumpAndSettle();

      expect(find.text('No internet connection'), findsOneWidget);
      expect(find.text('An error occurred'), findsNothing);
      // The buyer stays on the payment and can send it again.
      expect(find.byType(PaymentInstructionsScreen), findsOneWidget);
      await tester.tap(find.text('Retry'));
      await tester.pump();

      expect(submitted, hasLength(2));
      expect(submitted[1].orderId, submitted[0].orderId);
      expect(submitted[1].cbeTransactionId, submitted[0].cbeTransactionId);
    });

    testWidgets('another failure offers no Retry', (tester) async {
      await openFromMarket(tester);

      await submit(tester);
      states.add(
        const OrderError(message: 'refused', code: AppErrorCode.orderRefused),
      );
      await tester.pump();

      expect(find.byType(SnackBarAction), findsNothing);
    });

    testWidgets(
        'AUTH_040: one password prompt, and nothing is sent again until it '
        'is answered (BUG-23)', (tester) async {
      final auth = _MockAuthBloc();
      when(() => auth.stream).thenAnswer((_) => const Stream.empty());
      when(() => auth.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-1',
          tokens: AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: UserRole.buyer,
          phoneNumber: '+251900100003',
        ),
      );
      await openFromMarket(tester, auth: auth);

      // The first submit is how the client learns a password is wanted.
      await submit(tester);
      expect(submitted, hasLength(1));
      const refusal = OrderError(
        message: 'AUTH_040: Confirm your password',
        code: AppErrorCode.stepUpRequired,
      );
      states.add(refusal);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('step_up_password_field')), findsOneWidget);

      // The same refusal again does not stack a second prompt.
      states.add(refusal);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('step_up_password_field')), findsOneWidget);
      expect(submitted, hasLength(1));

      // Declined: the payment is not sent again.
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('step_up_password_field')), findsNothing);
      expect(submitted, hasLength(1));
    });
  });
}

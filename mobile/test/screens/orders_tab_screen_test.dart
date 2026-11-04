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
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

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
}

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_return_listener.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockOrderBloc extends Mock implements OrderBloc {}

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

class _MockSync extends Mock implements MarketplaceSync {}

/// ESC-44: Telebirr sends the buyer back after the app was killed; with no
/// checkout screen open, the app checks the buyer's waiting orders.
void main() {
  late _MockAuthBloc auth;
  late _MockOrderBloc orderBloc;
  late _MockOrderRepository orders;
  late _MockSync sync;
  late StreamController<Uri> links;

  final returnLink = Uri.parse('weredaconnect://payment/complete');

  Order waiting() => Order()
    ..orderId = 'order-1'
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 24.5
    ..totalPrice = 2450
    ..status = OrderStatus.paymentSubmitted
    ..createdAt = DateTime(2026, 10, 9)
    ..version = 2;

  void signIn(UserRole role) {
    when(() => auth.state).thenReturn(
      AuthAuthenticated(
        userId: 'buyer-1',
        tokens: const AuthTokens(
          accessToken: 'a',
          refreshToken: 'r',
          idToken: 'i',
        ),
        role: role,
        userName: 'Buyer',
      ),
    );
  }

  setUp(() {
    auth = _MockAuthBloc();
    orderBloc = _MockOrderBloc();
    orders = _MockOrderRepository();
    sync = _MockSync();
    links = StreamController<Uri>.broadcast();
    when(() => auth.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.repository).thenReturn(orders);
    when(
      () => orders.getOrdersByBuyer(
        'buyer-1',
        statusFilter: OrderStatus.paymentSubmitted.name,
      ),
    ).thenAnswer((_) async => [waiting()]);
    when(() => sync.pullOrder('order-1'))
        .thenAnswer((_) async => waiting()..status = OrderStatus.funded);
    signIn(UserRole.buyer);
  });

  tearDown(() => links.close());

  Future<void> pump(WidgetTester tester) async {
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider<MarketplaceSync?>.value(value: sync),
          BlocProvider<AuthBloc>.value(value: auth),
          BlocProvider<OrderBloc>.value(value: orderBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: PaymentReturnListener(
              links: links.stream,
              child: const Text('app'),
            ),
          ),
        ),
      ),
    );
  }

  testWidgets('checks the waiting orders and confirms a funded one',
      (tester) async {
    await pump(tester);

    links.add(returnLink);
    await tester.pump();
    expect(find.text('Checking your payment...'), findsOneWidget);
    // Let the first snackbar show and time out; the confirmation follows.
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 5));
    await tester.pumpAndSettle();

    verify(() => sync.pullOrder('order-1')).called(1);
    expect(
      find.text('Payment confirmed. Funds are held safely.'),
      findsOneWidget,
    );
  });

  testWidgets('an open checkout screen handles the link itself',
      (tester) async {
    TelebirrReturnWatchers.add();
    addTearDown(TelebirrReturnWatchers.remove);
    await pump(tester);

    links.add(returnLink);
    await tester.pumpAndSettle();

    verifyNever(() => sync.pullOrder(any()));
    expect(find.text('Checking your payment...'), findsNothing);
  });

  testWidgets('farmers and other links are ignored', (tester) async {
    signIn(UserRole.farmer);
    await pump(tester);

    links
      ..add(returnLink)
      ..add(Uri.parse('weredaconnect://fayda-callback?code=x'));
    await tester.pumpAndSettle();

    verifyNever(() => sync.pullOrder(any()));
    expect(find.text('Checking your payment...'), findsNothing);
  });
}

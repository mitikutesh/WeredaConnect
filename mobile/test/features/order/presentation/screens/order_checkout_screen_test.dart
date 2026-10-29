import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:url_launcher/link.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_checkout_screen.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class _MockEscrow extends Mock implements BaseEscrowRepository {}

/// ESC-44: the checkout screen opens Telebirr (Android on its own, web from
/// a link), reads the order on a backoff while in the foreground and comes
/// back at once from Telebirr.
void main() {
  const orderId = 'order-1';
  final serverNow = DateTime.utc(2026, 10, 9, 10);

  late _MockEscrow escrow;
  late OrderStatus serverStatus;
  late int reads;
  late Duration monotonic;
  late List<Uri> launched;
  late StreamController<Uri> links;
  late StreamController<void> network;
  late SecurityEvents events;

  Order order() => Order()
    ..orderId = orderId
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 24.5
    ..totalPrice = 2450
    ..orderTotal = 2448.75
    ..status = OrderStatus.paymentSubmitted
    ..createdAt = DateTime(2026, 10, 9)
    ..version = 2;

  void opens({Duration window = const Duration(minutes: 130)}) {
    when(() => escrow.openOrderCheckout(orderId)).thenAnswer(
      (_) async => OrderCheckoutOpened(
        toPayUrl: 'https://pay.example/checkout?prepay_id=P1',
        openUntil: serverNow.add(window),
        amount: 2448.75,
        serverDate: serverNow,
      ),
    );
  }

  void fails(String code, int status) {
    when(() => escrow.openOrderCheckout(orderId))
        .thenAnswer((_) async => OrderCheckoutFailed(code, status, 'x'));
  }

  OrderCheckoutBloc bloc() => OrderCheckoutBloc(
        orderId: orderId,
        escrow: escrow,
        readServerOrder: (_) async {
          reads++;
          return order()..status = serverStatus;
        },
        securityEvents: events,
        now: () => DateTime(2026, 10, 9, 13),
        monotonic: () => monotonic,
      );

  Future<void> open(
    WidgetTester tester, {
    bool isWeb = false,
    String locale = 'en',
  }) async {
    await tester.pumpWidget(
      MaterialApp(
        // The app's delegates: om has no Material localizations of its own.
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        locale: Locale(locale),
        home: OrderCheckoutScreen(
          order: order(),
          bloc: bloc(),
          isWeb: isWeb,
          returnLinks: links.stream,
          connectivityRestored: network.stream,
          launcher: (uri) async {
            launched.add(uri);
            return true;
          },
        ),
      ),
    );
    await tester.pump();
    await tester.pump();
  }

  setUp(() {
    escrow = _MockEscrow();
    serverStatus = OrderStatus.paymentSubmitted;
    reads = 0;
    monotonic = Duration.zero;
    launched = [];
    links = StreamController<Uri>.broadcast();
    network = StreamController<void>.broadcast();
    events = SecurityEvents.forTesting();
  });

  tearDown(() async {
    await links.close();
    await network.close();
  });

  /// Leave the screen so its timers stop before the test ends.
  Future<void> leave(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump();
  }

  testWidgets('Android opens Telebirr on its own, once per page',
      (tester) async {
    opens();
    await open(tester);

    expect(launched, hasLength(1));
    expect(launched.single.queryParameters['prepay_id'], 'P1');
    expect(
      find.text('Waiting for your Telebirr payment to be confirmed…'),
      findsOneWidget,
    );

    // A read later keeps the same page: no second launch.
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(launched, hasLength(1));

    // "Open Telebirr again" reopens the page in hand, no new request.
    await tester.tap(find.text('Open Telebirr payment'));
    await tester.pump();
    expect(launched, hasLength(2));
    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await leave(tester);
  });

  // LT-03: the checkout window on the viewer's clock, in Ethiopia time.
  for (final locale in ['en', 'am', 'om']) {
    testWidgets('$locale: the payment window shows on the viewer clock',
        (tester) async {
      opens();
      await open(tester, locale: locale);

      final l10n = lookupAppLocalizations(Locale(locale));
      // The device clock reads 13:00 and the page is open for 130 minutes.
      final until = DateTime(2026, 10, 9, 13).add(const Duration(minutes: 130));
      final label = ClockFormat.resolve(l10n, ClockPreference.auto)
          .deadline(until, sentence: l10n.orderCheckoutOpenUntil);
      expect(label.time24 == null, locale == 'en');
      expect(find.text(label.toString()), findsOneWidget);
      await leave(tester);
    });
  }

  testWidgets('web never opens on its own; it offers a real link',
      (tester) async {
    opens();
    await open(tester, isWeb: true);

    expect(launched, isEmpty);
    final link = tester.widget<Link>(find.byType(Link));
    expect(link.uri.toString(), contains('prepay_id=P1'));
    expect(link.target, LinkTarget.blank);
    await leave(tester);
  });

  testWidgets('reads back off: 5, 5, 10 seconds', (tester) async {
    opens();
    await open(tester);
    final base = reads;

    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 1);
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 2);
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 2, reason: 'the third read waits 10 seconds');
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 3);
    await leave(tester);
  });

  testWidgets('no reads in the background; back from Telebirr reads at once',
      (tester) async {
    opens();
    await open(tester);
    final base = reads;

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
    await tester.pump(const Duration(minutes: 2));
    expect(reads, base);

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    await tester.pump();
    expect(reads, base + 1);
    await leave(tester);
  });

  testWidgets('"inactive" alone (a dialog, the app switcher) keeps reading',
      (tester) async {
    opens();
    await open(tester);
    final base = reads;

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 1);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await leave(tester);
  });

  testWidgets('the network coming back reads at once, once', (tester) async {
    opens();
    await open(tester);
    final base = reads;

    network.add(null);
    await tester.pump();
    await tester.pump();

    expect(reads, base + 1);
    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await leave(tester);
  });

  testWidgets('the return link reads at once', (tester) async {
    opens();
    await open(tester);
    final base = reads;

    links.add(Uri.parse('weredaconnect://payment/complete'));
    await tester.pump();
    await tester.pump();
    expect(reads, base + 1);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=x'));
    await tester.pump();
    expect(reads, base + 1);
    await leave(tester);
  });

  testWidgets('paid: says so and offers Done', (tester) async {
    opens();
    await open(tester);

    serverStatus = OrderStatus.funded;
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    await tester.pump();

    expect(
      find.text('Payment confirmed. Funds are held safely.'),
      findsWidgets,
    );
    expect(find.text('Done'), findsOneWidget);
    final base = reads;
    await tester.pump(const Duration(minutes: 1));
    expect(reads, base, reason: 'no reads after the order is paid');
    await leave(tester);
  });

  testWidgets('the page expires: Open again is gone, a new payment is offered',
      (tester) async {
    opens(window: const Duration(minutes: 3));
    await open(tester);
    expect(find.text('Open Telebirr payment'), findsOneWidget);

    monotonic = const Duration(minutes: 4);
    await tester.pump(const Duration(minutes: 3, seconds: 1));
    await tester.pump();
    await tester.pump();

    expect(find.text('Open Telebirr payment'), findsNothing);
    expect(find.text('Start a new Telebirr payment'), findsOneWidget);
    await leave(tester);
  });

  testWidgets('after 10 minutes the reads stop; Check payment status reads',
      (tester) async {
    opens();
    await open(tester);

    await tester.pump(const Duration(minutes: 11));
    await tester.pump();
    final stopped = reads;
    expect(find.text('Check payment status'), findsOneWidget);
    await tester.pump(const Duration(minutes: 2));
    expect(reads, stopped);

    await tester.ensureVisible(find.text('Check payment status'));
    await tester.tap(find.text('Check payment status'));
    await tester.pump();
    await tester.pump();
    expect(reads, stopped + 1);
    await leave(tester);
  });

  testWidgets('ESCROW_032 explains the wait and keeps reading', (tester) async {
    fails('ESCROW_032', 409);
    await open(tester);

    expect(
      find.textContaining('being set up or was interrupted'),
      findsOneWidget,
    );
    expect(find.text('Retry'), findsOneWidget);
    final base = reads;
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(reads, base + 1);
    await leave(tester);
  });

  testWidgets('ESCROW_030 while still submitted: the payment time ran out',
      (tester) async {
    fails('ESCROW_030', 409);
    await open(tester);

    expect(
      find.textContaining('The time to pay for this order has run out'),
      findsOneWidget,
    );
    expect(find.text('Done'), findsOneWidget);
    await leave(tester);
  });

  testWidgets('ESCROW_030 and the order failed meanwhile: Pay again',
      (tester) async {
    when(() => escrow.openOrderCheckout(orderId)).thenAnswer((_) async {
      serverStatus = OrderStatus.paymentFailed;
      return const OrderCheckoutFailed('ESCROW_030', 409, 'not waiting');
    });
    await open(tester);

    expect(
      find.text(
        'This payment could not be confirmed in time. You can pay again.',
      ),
      findsOneWidget,
    );
    expect(find.text('Pay again'), findsOneWidget);
    await leave(tester);
  });

  testWidgets('a queued payment held for a step-up offers Confirm it is you',
      (tester) async {
    events
      ..stepUpDeclined()
      ..emit(const StepUpRequired());
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: OrderCheckoutScreen(
          order: order(),
          bloc: OrderCheckoutBloc(
            orderId: orderId,
            escrow: escrow,
            readServerOrder: (_) async => null,
            readLocalOrder: (_) async => order(),
            securityEvents: events,
          ),
          isWeb: false,
          returnLinks: links.stream,
        ),
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(
      find.text(
        'Your payment is waiting to be sent. Telebirr opens once it reaches '
        'the server.',
      ),
      findsOneWidget,
    );
    // The button, besides the card that explains the check.
    expect(
      find.widgetWithText(FilledButton, "Confirm it's you"),
      findsOneWidget,
    );
    verifyNever(() => escrow.openOrderCheckout(any()));
    await leave(tester);
  });

  for (final locale in ['am', 'om']) {
    testWidgets('renders in $locale', (tester) async {
      fails('ESCROW_032', 409);
      await open(tester, locale: locale);

      final l10n = await AppLocalizations.delegate.load(Locale(locale));
      expect(find.text(l10n.orderCheckoutTitle), findsOneWidget);
      expect(find.text(l10n.orderCheckoutAlreadyOpen), findsOneWidget);
      await leave(tester);
    });
  }

  // Telebirr payment board: the buyer learns how the payment will ask them
  // to confirm it is them before it asks.
  testWidgets('explains the Fayda and password check up front', (tester) async {
    fails('ESCROW_032', 409);
    await open(tester);

    expect(
      find.descendant(
        of: find.byKey(const Key('checkout_confirm_its_you')),
        matching: find.text(
          'Payments over 10,000 ETB need Fayda. '
          'Smaller ones ask for your password.',
        ),
      ),
      findsOneWidget,
    );
    await leave(tester);
  });
}

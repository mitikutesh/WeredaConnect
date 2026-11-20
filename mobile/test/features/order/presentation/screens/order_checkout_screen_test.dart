import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:url_launcher/link.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_checkout_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_cards.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class _MockEscrow extends Mock implements BaseEscrowRepository {}

class _MockOrderBloc extends Mock implements OrderBloc {}

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

  // Telebirr payment board: "68,400 ETB", cents only when there are.
  testWidgets('the amount is grouped by thousands', (tester) async {
    opens();
    await open(tester);

    expect(find.text('2,448.75 ETB'), findsOneWidget);
    await leave(tester);
  });

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

  // PaymentFailedM: the full state, not a line in the status card.
  testWidgets('ESCROW_030 and the order failed meanwhile: the failed state',
      (tester) async {
    when(() => escrow.openOrderCheckout(orderId)).thenAnswer((_) async {
      serverStatus = OrderStatus.paymentFailed;
      return const OrderCheckoutFailed('ESCROW_030', 409, 'not waiting');
    });
    await open(tester);

    expect(find.byKey(const Key('payment_failed_state')), findsOneWidget);
    expect(find.text('Not paid'), findsOneWidget);
    expect(find.text('Payment did not go through'), findsOneWidget);
    expect(
      find.text(
        'No money was taken from your Telebirr account, and your order is '
        'still saved.',
      ),
      findsOneWidget,
    );
    // The state replaces the cards; its two actions are the only ones.
    expect(find.byType(PaymentAmountCard), findsNothing);
    expect(find.byType(PaymentStatusCard), findsNothing);
    expect(
      find.widgetWithText(FilledButton, 'Try payment again'),
      findsOneWidget,
    );
    expect(find.widgetWithText(OutlinedButton, 'Pay Later'), findsOneWidget);
    // The close button has a name for screen readers.
    expect(find.bySemanticsLabel('Close'), findsOneWidget);
    await leave(tester);
  });

  testWidgets('the failed state: Try payment again opens the payment step',
      (tester) async {
    when(() => escrow.openOrderCheckout(orderId)).thenAnswer((_) async {
      serverStatus = OrderStatus.paymentFailed;
      return const OrderCheckoutFailed('ESCROW_030', 409, 'not waiting');
    });
    final orders = _MockOrderBloc();
    when(() => orders.state).thenReturn(const OrderInitial());
    when(() => orders.stream).thenAnswer((_) => const Stream.empty());
    await tester.pumpWidget(
      BlocProvider<OrderBloc>.value(
        value: orders,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: OrderCheckoutScreen(
            order: order(),
            bloc: bloc(),
            isWeb: false,
            returnLinks: links.stream,
            connectivityRestored: network.stream,
          ),
        ),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.tap(find.text('Try payment again'));
    await tester.pumpAndSettle();

    expect(find.byType(PaymentInstructionsScreen), findsOneWidget);
    expect(find.byType(OrderCheckoutScreen), findsNothing);
    await leave(tester);
  });

  // PaymentOfflineM.
  group('no network while paying', () {
    testWidgets('the checkout request did not reach the server',
        (tester) async {
      fails('ESCROW_OFFLINE', 0);
      await open(tester);

      expect(find.byKey(const Key('payment_offline_state')), findsOneWidget);
      expect(find.text('No network'), findsOneWidget);
      expect(find.text('No internet connection'), findsOneWidget);
      expect(
        find.text(
          'Payment needs a connection. Your order is saved on this phone.',
        ),
        findsOneWidget,
      );
      expect(find.byType(PaymentStatusCard), findsNothing);
      expect(
        find.widgetWithText(OutlinedButton, 'Back to order'),
        findsOneWidget,
      );

      // Retry asks for the checkout again; with a network it opens.
      opens();
      await tester.tap(find.widgetWithText(FilledButton, 'Retry'));
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const Key('payment_offline_state')), findsNothing);
      expect(
        find.text('Waiting for your Telebirr payment to be confirmed…'),
        findsOneWidget,
      );
      await leave(tester);
    });

    late StreamController<bool> online;
    setUp(() => online = StreamController<bool>.broadcast());
    tearDown(() => online.close());

    Future<void> openQueued(
      WidgetTester tester, {
      bool stepUp = false,
    }) async {
      if (stepUp) {
        events
          ..stepUpDeclined()
          ..emit(const StepUpRequired());
      }
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
            online: online.stream,
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
    }

    const waitingToSend =
        'Your payment is waiting to be sent. Telebirr opens once it reaches '
        'the server.';

    testWidgets('a payment waiting on a phone with no network', (tester) async {
      await openQueued(tester);
      // Online as far as the phone knows: the waiting card.
      expect(find.text(waitingToSend), findsOneWidget);

      online.add(false);
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const Key('payment_offline_state')), findsOneWidget);
      expect(find.text(waitingToSend), findsNothing);

      // The network is back: the waiting card again, nothing was dropped.
      online.add(true);
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const Key('payment_offline_state')), findsNothing);
      expect(find.text(waitingToSend), findsOneWidget);
      verifyNever(() => escrow.openOrderCheckout(any()));
      await leave(tester);
    });

    testWidgets('a queued payment held for a step-up keeps its card offline',
        (tester) async {
      await openQueued(tester, stepUp: true);
      online.add(false);
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const Key('payment_offline_state')), findsNothing);
      expect(
        find.widgetWithText(FilledButton, "Confirm it's you"),
        findsOneWidget,
      );
      await leave(tester);
    });
  });

  // PaymentM: what the buyer must do, and where the money goes.
  testWidgets('waiting for Telebirr says to approve there', (tester) async {
    opens();
    await open(tester);

    expect(
      find.text(
        'You need to approve the payment in Telebirr. Then the money is '
        'held in escrow until you confirm delivery.',
      ),
      findsOneWidget,
    );
    // The page's one orange action is the theme's button; Pay Later is the
    // outlined pill under it.
    final openButton =
        find.widgetWithText(FilledButton, 'Open Telebirr payment');
    expect(tester.widget<FilledButton>(openButton).style, isNull);
    expect(find.widgetWithText(OutlinedButton, 'Pay Later'), findsOneWidget);
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

  // PaymentM: "Confirmed with Fayda", only once it happened.
  group('the confirmed-with pill', () {
    final pill = find.byKey(const Key('checkout_confirmed_with'));

    Future<void> openQueuedForStepUp(
      WidgetTester tester, {
      required bool withFayda,
      StepUpMethod? confirmedWith,
    }) async {
      events
        ..stepUpDeclined()
        ..emit(StepUpRequired(withFayda: withFayda));
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
            securityEvents: events,
            confirmedWith: confirmedWith,
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
    }

    testWidgets('not shown before anything was confirmed', (tester) async {
      fails('ESCROW_032', 409);
      await open(tester);

      expect(pill, findsNothing);
      await leave(tester);
    });

    testWidgets('a pending Fayda step-up is not named as confirmed',
        (tester) async {
      await openQueuedForStepUp(tester, withFayda: true);

      expect(
        find.widgetWithText(FilledButton, "Confirm it's you"),
        findsOneWidget,
      );
      expect(pill, findsNothing);
      await leave(tester);
    });

    for (final (method, words) in [
      (StepUpMethod.fayda, 'Confirmed with Fayda'),
      (StepUpMethod.password, 'Confirmed with your password'),
    ]) {
      testWidgets('shown once the step-up completes: $words', (tester) async {
        await openQueuedForStepUp(
          tester,
          withFayda: method == StepUpMethod.fayda,
        );
        expect(pill, findsNothing);

        // What a finished prompt reports (this screen's or the shell's).
        events.stepUpCompleted(method: method);
        await tester.pump();
        await tester.pump();

        expect(pill, findsOneWidget);
        expect(find.text(words), findsOneWidget);
        await leave(tester);
      });
    }

    testWidgets('a completion that names no method shows nothing',
        (tester) async {
      await openQueuedForStepUp(tester, withFayda: false);

      events.stepUpCompleted();
      await tester.pump();
      await tester.pump();

      expect(pill, findsNothing);
      await leave(tester);
    });

    testWidgets('carried over from the page before, gone when asked again',
        (tester) async {
      fails('ESCROW_032', 409);
      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: OrderCheckoutScreen(
            order: order(),
            bloc: bloc(),
            isWeb: false,
            returnLinks: links.stream,
            connectivityRestored: network.stream,
            securityEvents: events,
            confirmedWith: StepUpMethod.password,
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
      expect(find.text('Confirmed with your password'), findsOneWidget);
      await leave(tester);

      // A queued payment that asks again starts without the pill.
      await openQueuedForStepUp(
        tester,
        withFayda: true,
        confirmedWith: StepUpMethod.password,
      );
      expect(pill, findsNothing);
      await leave(tester);
    });
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

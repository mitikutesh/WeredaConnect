import 'dart:async';
import 'dart:collection';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';

class _MockEscrow extends Mock implements BaseEscrowRepository {}

/// ESC-44: the live order checkout follows the server's order, and requests
/// a checkout only when the buyer asked for one.
void main() {
  const orderId = 'order-1';
  final serverNow = DateTime.utc(2026, 10, 9, 10);
  final deviceNow = DateTime(2026, 10, 9, 13); // EAT, the device's clock

  late _MockEscrow escrow;
  late Queue<Object?> serverReads;
  late Queue<Object?> localReads;
  late Duration monotonic;
  late SecurityEvents events;

  Order order(OrderStatus status) => Order()
    ..orderId = orderId
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..cropType = 'teff'
    ..quantity = 100
    ..pricePerUnit = 24.5
    ..totalPrice = 2450
    ..orderTotal = 2448.75
    ..status = status
    ..createdAt = DateTime(2026, 10, 9)
    ..version = 2;

  Future<Order?> next(Queue<Object?> reads) async {
    final answer = reads.isEmpty ? null : reads.removeFirst();
    if (answer is Exception) throw answer;
    return answer as Order?;
  }

  OrderCheckoutBloc bloc({bool mobile = false}) => OrderCheckoutBloc(
        orderId: orderId,
        escrow: escrow,
        readServerOrder: (_) => next(serverReads),
        readLocalOrder: mobile ? (_) => next(localReads) : null,
        securityEvents: events,
        now: () => deviceNow,
        monotonic: () => monotonic,
      );

  OrderCheckoutOpened opened({
    String url = 'https://pay.example/checkout?prepay_id=P1',
    Duration window = const Duration(minutes: 130),
    bool isExisting = false,
    DateTime? serverDate,
    bool withServerDate = true,
  }) =>
      OrderCheckoutOpened(
        toPayUrl: url,
        openUntil: serverNow.add(window),
        amount: 2448.75,
        isExisting: isExisting,
        serverDate: withServerDate ? (serverDate ?? serverNow) : null,
      );

  void answers(List<OrderCheckoutResult> results) {
    final queue = Queue.of(results);
    when(() => escrow.openOrderCheckout(orderId))
        .thenAnswer((_) async => queue.removeFirst());
  }

  Matcher awaiting({int token = 1, Duration? timeLeft}) =>
      isA<CheckoutAwaitingPayment>()
          .having((s) => s.launchToken, 'launchToken', token)
          .having((s) => s.amount, 'amount', 2448.75)
          .having(
            (s) => s.timeLeft,
            'timeLeft',
            timeLeft ?? const Duration(minutes: 130),
          );

  setUp(() {
    escrow = _MockEscrow();
    serverReads = Queue();
    localReads = Queue();
    monotonic = Duration.zero;
    events = SecurityEvents.forTesting();
  });

  test('poll delay backs off from 5 to 30 seconds', () {
    expect(
      [for (var i = 0; i < 9; i++) orderCheckoutPollDelay(i).inSeconds],
      [5, 5, 10, 10, 15, 20, 30, 30, 30],
    );
  });

  test('payment_submitted: requests the checkout once, then awaits payment',
      () async {
    serverReads.add(order(OrderStatus.paymentSubmitted));
    answers([opened()]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );

    final state = b.state as CheckoutAwaitingPayment;
    expect(state.toPayUrl, contains('prepay_id=P1'));
    expect(state.payableUntil, deviceNow.add(const Duration(minutes: 130)));
    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await b.close();
  });

  test('funded on the first read (mock payments): paid, no checkout', () async {
    serverReads.add(order(OrderStatus.funded));
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(b.stream, emits(isA<CheckoutPaid>()));

    verifyNever(() => escrow.openOrderCheckout(any()));
    await b.close();
  });

  test('ESCROW_031: waits for the automatic funding, then paid', () async {
    serverReads.addAll([
      order(OrderStatus.paymentSubmitted),
      order(OrderStatus.paymentSubmitted),
      order(OrderStatus.funded),
    ]);
    answers([const OrderCheckoutFailed('ESCROW_031', 409, 'mock')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        isA<CheckoutConfirmingAutomatically>(),
      ]),
    );
    b.add(const CheckoutRefreshed());
    await pumpEventQueue();
    expect(b.state, const CheckoutConfirmingAutomatically());
    b.add(const CheckoutRefreshed());
    await expectLater(b.stream, emits(isA<CheckoutPaid>()));

    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await b.close();
  });

  test('poll ticks after a network error never request a checkout', () async {
    serverReads.addAll(
      List.filled(4, order(OrderStatus.paymentSubmitted)),
    );
    answers([const OrderCheckoutFailed('ESCROW_OFFLINE', null, 'offline')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.offline),
      ]),
    );
    for (var i = 0; i < 3; i++) {
      b.add(const CheckoutRefreshed());
      await pumpEventQueue();
    }

    expect(b.state, const CheckoutProblem(CheckoutIssue.offline));
    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await b.close();
  });

  test('the network coming back retries once', () async {
    serverReads.addAll(
      List.filled(3, order(OrderStatus.paymentSubmitted)),
    );
    answers([
      const OrderCheckoutFailed('ESCROW_OFFLINE', null, 'offline'),
      opened(),
    ]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.offline),
      ]),
    );
    b.add(const CheckoutConnectivityRestored());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );
    b.add(const CheckoutConnectivityRestored());
    await pumpEventQueue();

    verify(() => escrow.openOrderCheckout(orderId)).called(2);
    await b.close();
  });

  test('a retry tap after a network error requests again', () async {
    serverReads.addAll(
      List.filled(2, order(OrderStatus.paymentSubmitted)),
    );
    answers([
      const OrderCheckoutFailed('ESCROW_OFFLINE', null, 'offline'),
      opened(),
    ]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.offline),
      ]),
    );
    b.add(const CheckoutOpenRequested());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );
    await b.close();
  });

  group('ESCROW_030 is told apart by the order', () {
    test('still payment_submitted: the payment time ran out', () async {
      serverReads.addAll([
        order(OrderStatus.paymentSubmitted),
        order(OrderStatus.paymentSubmitted),
      ]);
      answers([const OrderCheckoutFailed('ESCROW_030', 409, 'ran out')]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([
          isA<CheckoutOpening>(),
          const CheckoutStopped(CheckoutIssue.timeRanOut),
        ]),
      );
      await b.close();
    });

    test('now payment_failed: the buyer can pay again', () async {
      serverReads.addAll([
        order(OrderStatus.paymentSubmitted),
        order(OrderStatus.paymentFailed),
      ]);
      answers([const OrderCheckoutFailed('ESCROW_030', 409, 'not waiting')]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([
          isA<CheckoutOpening>(),
          const CheckoutStopped(CheckoutIssue.paymentFailed),
        ]),
      );
      await b.close();
    });

    test('422: cannot be paid online', () async {
      serverReads.add(order(OrderStatus.paymentSubmitted));
      answers([const OrderCheckoutFailed('ESCROW_030', 422, 'no farmer')]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([
          isA<CheckoutOpening>(),
          const CheckoutStopped(CheckoutIssue.notPayableOnline),
        ]),
      );
      await b.close();
    });
  });

  test('ESCROW_001: the total is out of range', () async {
    serverReads.add(order(OrderStatus.paymentSubmitted));
    answers([const OrderCheckoutFailed('ESCROW_001', 400, 'amount')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutStopped(CheckoutIssue.totalOutOfRange),
      ]),
    );
    await b.close();
  });

  test('ESCROW_032: keeps reading the order and is paid when it lands',
      () async {
    serverReads.addAll([
      order(OrderStatus.paymentSubmitted),
      order(OrderStatus.paymentSubmitted),
      order(OrderStatus.funded),
    ]);
    answers([const OrderCheckoutFailed('ESCROW_032', 409, 'busy')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.alreadyOpen),
      ]),
    );
    expect((b.state as CheckoutProblem).polls, isTrue);
    b.add(const CheckoutRefreshed());
    await pumpEventQueue();
    expect(b.state, const CheckoutProblem(CheckoutIssue.alreadyOpen));
    b.add(const CheckoutRefreshed());
    await expectLater(b.stream, emits(isA<CheckoutPaid>()));

    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await b.close();
  });

  test('ESCROW_024: already funded is paid', () async {
    serverReads.add(order(OrderStatus.paymentSubmitted));
    answers([const OrderCheckoutFailed('ESCROW_024', 409, 'funded')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), isA<CheckoutPaid>()]),
    );
    await b.close();
  });

  test('ESCROW_013, then a retry opens the page', () async {
    serverReads.addAll([
      order(OrderStatus.paymentSubmitted),
      order(OrderStatus.paymentSubmitted),
    ]);
    answers([
      const OrderCheckoutFailed('ESCROW_013', 502, 'telebirr'),
      opened(),
    ]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.gateway),
      ]),
    );
    expect((b.state as CheckoutProblem).canRetry, isTrue);
    b.add(const CheckoutOpenRequested());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );
    await b.close();
  });

  test('ESCROW_011 offers no retry', () async {
    serverReads.add(order(OrderStatus.paymentSubmitted));
    answers([const OrderCheckoutFailed('ESCROW_011', 429, 'slow down')]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutProblem(CheckoutIssue.rateLimited),
      ]),
    );
    expect((b.state as CheckoutProblem).canRetry, isFalse);
    await b.close();
  });

  test('a page in hand is reused: reads request nothing', () async {
    serverReads.addAll(
      List.filled(3, order(OrderStatus.paymentSubmitted)),
    );
    answers([opened()]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );
    monotonic = const Duration(minutes: 1);
    b.add(const CheckoutRefreshed());
    await expectLater(
      b.stream,
      emits(awaiting(timeLeft: const Duration(minutes: 129))),
    );

    verify(() => escrow.openOrderCheckout(orderId)).called(1);
    await b.close();
  });

  group('the page expires', () {
    test('window closed; a new payment re-reads the order first (paid)',
        () async {
      serverReads.addAll([
        order(OrderStatus.paymentSubmitted),
        order(OrderStatus.paymentSubmitted),
        order(OrderStatus.funded),
      ]);
      answers([opened()]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      monotonic = const Duration(minutes: 131);
      b.add(const CheckoutRefreshed());
      await expectLater(b.stream, emits(isA<CheckoutWindowClosed>()));
      b.add(const CheckoutOpenRequested());
      await expectLater(b.stream, emits(isA<CheckoutPaid>()));

      verify(() => escrow.openOrderCheckout(orderId)).called(1);
      await b.close();
    });

    test('a new payment when the order still waits opens a new page', () async {
      serverReads.addAll(
        List.filled(3, order(OrderStatus.paymentSubmitted)),
      );
      answers([opened(), opened(url: 'https://pay.example/second')]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      monotonic = const Duration(minutes: 131);
      b.add(const CheckoutRefreshed());
      await expectLater(b.stream, emits(isA<CheckoutWindowClosed>()));
      b.add(const CheckoutOpenRequested());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting(token: 2)]),
      );
      expect(
        (b.state as CheckoutAwaitingPayment).toPayUrl,
        'https://pay.example/second',
      );
      await b.close();
    });
  });

  group('the page window is measured on the server clock', () {
    test('the Date header wins over a wrong device clock', () async {
      serverReads.add(order(OrderStatus.paymentSubmitted));
      // The server says 30 minutes are left; the device clock is hours off.
      answers([
        opened(
          window: const Duration(minutes: 30),
          isExisting: true,
        ),
      ]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([
          isA<CheckoutOpening>(),
          awaiting(timeLeft: const Duration(minutes: 30)),
        ]),
      );
      expect(
        (b.state as CheckoutAwaitingPayment).payableUntil,
        deviceNow.add(const Duration(minutes: 30)),
      );
      await b.close();
    });

    test(
        'no Date header and a device clock that cannot explain the answer: '
        'a new page gets the full window', () async {
      serverReads.add(order(OrderStatus.paymentSubmitted));
      // deviceNow (10:00 UTC) is before serverNow + 130 min, so shift the
      // page into the device's past to model a clock that runs far ahead.
      answers([
        OrderCheckoutOpened(
          toPayUrl: 'https://pay.example/x',
          openUntil: deviceNow.toUtc().subtract(const Duration(hours: 5)),
          amount: 2448.75,
        ),
      ]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      await b.close();
    });
  });

  group('mobile: the payment is queued on the phone', () {
    test('waits to send, then opens once it is on the server', () async {
      serverReads.addAll([null, order(OrderStatus.paymentSubmitted)]);
      localReads.add(order(OrderStatus.paymentSubmitted));
      answers([opened()]);
      final b = bloc(mobile: true);

      b.add(const CheckoutStarted());
      await expectLater(b.stream, emits(const CheckoutWaitingToSend()));
      verifyNever(() => escrow.openOrderCheckout(any()));
      b.add(const CheckoutRefreshed());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      await b.close();
    });

    test('a step-up nobody confirmed is offered (Fayda)', () async {
      events
        ..stepUpDeclined()
        ..emit(const StepUpRequired(withFayda: true));
      serverReads.add(null);
      localReads.add(order(OrderStatus.paymentSubmitted));
      final b = bloc(mobile: true);

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emits(
          const CheckoutWaitingToSend(
            stepUpPending: true,
            stepUpWithFayda: true,
          ),
        ),
      );
      await b.close();
    });

    test('refused and put back (ESC-12, KYC): not sent', () async {
      serverReads.add(order(OrderStatus.created));
      localReads.add(order(OrderStatus.created));
      final b = bloc(mobile: true);

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emits(const CheckoutStopped(CheckoutIssue.notSent)),
      );
      verifyNever(() => escrow.openOrderCheckout(any()));
      await b.close();
    });

    test('a KYC resubmit after "not sent" still opens the page', () async {
      serverReads.addAll([
        order(OrderStatus.created),
        null,
        order(OrderStatus.paymentSubmitted),
      ]);
      localReads.addAll([
        order(OrderStatus.created),
        order(OrderStatus.paymentSubmitted),
      ]);
      answers([opened()]);
      final b = bloc(mobile: true);

      b.add(const CheckoutStarted());
      await expectLater(
        b.stream,
        emits(const CheckoutStopped(CheckoutIssue.notSent)),
      );
      b.add(const CheckoutRefreshed());
      await expectLater(b.stream, emits(const CheckoutWaitingToSend()));
      b.add(const CheckoutRefreshed());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      await b.close();
    });
  });

  group('web: the server read is eventually consistent', () {
    test('a stale read right after paying waits instead of stopping', () async {
      serverReads.addAll([
        order(OrderStatus.created),
        order(OrderStatus.paymentSubmitted),
      ]);
      answers([opened()]);
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(b.stream, emits(const CheckoutWaitingToSend()));
      b.add(const CheckoutRefreshed());
      await expectLater(
        b.stream,
        emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
      );
      await b.close();
    });

    test('still not submitted after the grace reads: not sent', () async {
      serverReads.addAll(List.filled(4, order(OrderStatus.created)));
      final b = bloc();

      b.add(const CheckoutStarted());
      await expectLater(b.stream, emits(const CheckoutWaitingToSend()));
      for (var i = 0; i < 3; i++) {
        b.add(const CheckoutRefreshed());
        await pumpEventQueue();
      }

      expect(b.state, const CheckoutStopped(CheckoutIssue.notSent));
      verifyNever(() => escrow.openOrderCheckout(any()));
      await b.close();
    });
  });

  test('cancelled: stops without a checkout', () async {
    serverReads.add(order(OrderStatus.cancelled));
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emits(const CheckoutStopped(CheckoutIssue.notWaiting)),
    );
    verifyNever(() => escrow.openOrderCheckout(any()));
    await b.close();
  });

  test('no such order on the server: not found', () async {
    serverReads.add(const NotFoundException());
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emits(const CheckoutStopped(CheckoutIssue.notFound)),
    );
    await b.close();
  });

  test('a failed read keeps the page in hand and flags offline', () async {
    serverReads.addAll([
      order(OrderStatus.paymentSubmitted),
      const NetworkException('offline'),
    ]);
    answers([opened()]);
    final b = bloc();

    b.add(const CheckoutStarted());
    await expectLater(
      b.stream,
      emitsInOrder([isA<CheckoutOpening>(), awaiting()]),
    );
    b.add(const CheckoutRefreshed());
    await expectLater(
      b.stream,
      emits(
        isA<CheckoutAwaitingPayment>()
            .having((s) => s.offline, 'offline', isTrue),
      ),
    );
    await b.close();
  });

  test('refreshes that arrive while a request runs are dropped', () async {
    final gate = Completer<OrderCheckoutResult>();
    serverReads.addAll(
      List.filled(5, order(OrderStatus.paymentSubmitted)),
    );
    when(() => escrow.openOrderCheckout(orderId))
        .thenAnswer((_) => gate.future);
    final b = bloc();

    b
      ..add(const CheckoutStarted())
      ..add(const CheckoutRefreshed())
      ..add(const CheckoutRefreshed());
    await pumpEventQueue();
    gate.complete(opened());
    await pumpEventQueue();

    expect(b.state, awaiting());
    // Only the start read the order; both refreshes were dropped.
    expect(serverReads, hasLength(4));
    await b.close();
  });
}

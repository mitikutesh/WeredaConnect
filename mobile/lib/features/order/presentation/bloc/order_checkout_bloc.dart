import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_state.dart';

export 'package:wereda_connect/features/order/presentation/bloc/order_checkout_event.dart';
export 'package:wereda_connect/features/order/presentation/bloc/order_checkout_state.dart';

/// Reads an order. For [OrderCheckoutBloc.readServerOrder]: the server's
/// copy, or null while a local change of the order is still queued (mobile);
/// errors propagate.
typedef OrderReader = Future<Order?> Function(String orderId);

/// Statuses that mean the order is paid: funded, or further along.
const Set<OrderStatus> paidOrderStatuses = {
  OrderStatus.funded,
  OrderStatus.accepted,
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
  OrderStatus.delivered,
  OrderStatus.disbursing,
  OrderStatus.settled,
  OrderStatus.disputed,
  OrderStatus.disputeResolved,
};

/// Delay before the next order read, by how many reads ran since the last
/// trigger (screen open, Telebirr opened, app resumed, return link, a tap):
/// 5, 5, 10, 10, 15, 20, then 30 seconds. Cheap on 3G (a few KB a minute).
Duration orderCheckoutPollDelay(int attempt) {
  const steps = [5, 5, 10, 10, 15, 20];
  return Duration(seconds: attempt < steps.length ? steps[attempt] : 30);
}

/// How long after the last trigger the screen keeps reading the order on
/// its own.
const orderCheckoutPollCap = Duration(minutes: 10);

/// The live Telebirr checkout of one order (ESC-44).
///
/// What happens is decided by the server's order, never by the build: a
/// `funded` (or later) order is paid; a `payment_submitted` one is paid
/// through `POST /escrow/order-checkout`; the webhook funds it and the screen
/// polls the order until it is.
///
/// Money safety: a checkout is requested only by [CheckoutStarted.autoOpen]
/// (once, the first time the order is `payment_submitted` with no page in
/// hand), by [CheckoutOpenRequested] (a button), or by one
/// [CheckoutConnectivityRestored] after a request failed offline. A poll
/// never requests one, so the velocity limit only counts what the buyer
/// asked for. "Open Telebirr again" reuses the page in hand.
class OrderCheckoutBloc extends Bloc<OrderCheckoutEvent, OrderCheckoutState> {
  final String orderId;
  final BaseEscrowRepository _escrow;
  final OrderReader _readServerOrder;
  final OrderReader? _readLocalOrder;
  final SecurityEvents _securityEvents;
  final DateTime Function() _now;
  final Duration Function() _monotonic;

  /// The first `payment_submitted` read with no page in hand requests a
  /// checkout without a tap. Covers a payment that syncs late (offline,
  /// step-up, a KYC resubmit). Consumed by the first request.
  bool _pendingAutoOpen = false;

  /// A request failed offline: retry it once when the network comes back.
  bool _retryOnReconnect = false;

  /// `ESCROW_031`: mock payments; only wait for `funded`.
  bool _confirmingAutomatically = false;

  _CheckoutPage? _page;
  int _launchToken = 0;

  /// Reads that may still show the order from before the payment: the
  /// server's order read is eventually consistent, so right after a payment
  /// is sent it can briefly answer the old status.
  int _staleReadsLeft = 3;

  /// Longest a checkout stays payable: Telebirr's 120 minutes plus the
  /// server's 10-minute margin (escrow-protocol 1.5.0).
  static const _maxWindow = Duration(minutes: 130);

  /// Assumed time left on a page returned again when neither the server's
  /// clock nor the device's can be trusted.
  static const _unknownWindow = Duration(minutes: 10);

  /// [readServerOrder]: mobile `MarketplaceSync.pullOrder`, web
  /// `WebOrderRepository.fetchOrder`. [readLocalOrder]: mobile only (Isar),
  /// to tell a queued payment from a refused one. [monotonic] measures the
  /// page's time left without the device's wall clock.
  OrderCheckoutBloc({
    required this.orderId,
    required BaseEscrowRepository escrow,
    required OrderReader readServerOrder,
    OrderReader? readLocalOrder,
    SecurityEvents? securityEvents,
    DateTime Function()? now,
    Duration Function()? monotonic,
  })  : _escrow = escrow,
        _readServerOrder = readServerOrder,
        _readLocalOrder = readLocalOrder,
        _securityEvents = securityEvents ?? SecurityEvents.instance,
        _now = now ?? DateTime.now,
        _monotonic = monotonic ?? _stopwatchClock(),
        super(const CheckoutChecking()) {
    on<OrderCheckoutEvent>(_onEvent, transformer: _sequential);
  }

  static Duration Function() _stopwatchClock() {
    final stopwatch = Stopwatch()..start();
    return () => stopwatch.elapsed;
  }

  /// One event at a time, in order.
  static Stream<OrderCheckoutEvent> _sequential(
    Stream<OrderCheckoutEvent> events,
    Stream<OrderCheckoutEvent> Function(OrderCheckoutEvent) mapper,
  ) =>
      events.asyncExpand(mapper);

  /// Events added but not finished yet.
  int _unfinished = 0;

  /// A refresh that arrives while another event waits or runs is dropped:
  /// that one reads the order anyway, and a slow 3G read must not pile up
  /// a queue of polls.
  @override
  void add(OrderCheckoutEvent event) {
    if (event is CheckoutRefreshed && _unfinished > 0) return;
    _unfinished++;
    super.add(event);
  }

  Future<void> _onEvent(
    OrderCheckoutEvent event,
    Emitter<OrderCheckoutState> emit,
  ) async {
    try {
      await _handle(event, emit);
    } finally {
      _unfinished--;
    }
  }

  Future<void> _handle(
    OrderCheckoutEvent event,
    Emitter<OrderCheckoutState> emit,
  ) async {
    switch (event) {
      case CheckoutStarted(:final autoOpen):
        _pendingAutoOpen = autoOpen;
        await _evaluate(emit);
      case CheckoutRefreshed():
        await _evaluate(emit);
      case CheckoutOpenRequested():
        _retryOnReconnect = false;
        await _evaluate(emit, openRequested: true);
      case CheckoutConnectivityRestored():
        if (!_retryOnReconnect) return;
        _retryOnReconnect = false;
        await _evaluate(emit, openRequested: true);
    }
  }

  /// Read the order and act on the server's status.
  Future<void> _evaluate(
    Emitter<OrderCheckoutState> emit, {
    bool openRequested = false,
  }) async {
    final Order? server;
    try {
      server = await _readServerOrder(orderId);
    } on NotFoundException {
      emit(const CheckoutStopped(CheckoutIssue.notFound));
      return;
    } on Object {
      _emitOffline(emit);
      return;
    }

    if (server == null) {
      await _emitQueued(emit);
      return;
    }
    await _act(server, emit, openRequested: openRequested);
  }

  /// Mobile: a local change of the order is still queued.
  Future<void> _emitQueued(Emitter<OrderCheckoutState> emit) async {
    Order? local;
    try {
      local = await _readLocalOrder?.call(orderId);
    } on Object {
      local = null;
    }
    if (local != null && local.status != OrderStatus.paymentSubmitted) {
      // Something else is queued (a cancel, say): this payment is not.
      emit(_stoppedFor(local.status));
      return;
    }
    final stepUp = _securityEvents.pendingStepUp;
    emit(
      CheckoutWaitingToSend(
        stepUpPending: stepUp != null,
        stepUpWithFayda: stepUp?.withFayda ?? false,
      ),
    );
  }

  Future<void> _act(
    Order server,
    Emitter<OrderCheckoutState> emit, {
    required bool openRequested,
  }) async {
    final status = server.status;
    if (paidOrderStatuses.contains(status)) {
      _page = null;
      emit(const CheckoutPaid());
      return;
    }
    if (status != OrderStatus.paymentSubmitted) {
      final mayBeStale = status == OrderStatus.created ||
          status == OrderStatus.pendingPayment ||
          status == OrderStatus.paymentFailed;
      if (_pendingAutoOpen && mayBeStale && _staleReadsLeft > 0) {
        _staleReadsLeft--;
        await _emitQueued(emit);
        return;
      }
      emit(_stoppedFor(status));
      return;
    }

    if (_confirmingAutomatically) {
      emit(const CheckoutConfirmingAutomatically());
      return;
    }
    final page = _page;
    if (page != null && page.isOpen(_monotonic())) {
      emit(_awaiting(page));
      return;
    }
    if (openRequested || _pendingAutoOpen) {
      _pendingAutoOpen = false;
      await _open(emit);
      return;
    }
    // Waiting for payment with no page to offer. Keep a problem or a stop
    // the buyer still has to read; otherwise offer a new payment.
    final current = state;
    if (current is CheckoutProblem || current is CheckoutStopped) {
      emit(current);
      return;
    }
    emit(const CheckoutWindowClosed());
  }

  Future<void> _open(Emitter<OrderCheckoutState> emit) async {
    emit(const CheckoutOpening());
    final result = await _escrow.openOrderCheckout(orderId);
    switch (result) {
      case OrderCheckoutOpened():
        final page = _CheckoutPage.from(
          result,
          receivedAt: _monotonic(),
          deviceNow: _now(),
        );
        _page = page;
        _launchToken++;
        emit(_awaiting(page));
      case OrderCheckoutFailed():
        await _onFailure(result, emit);
    }
  }

  Future<void> _onFailure(
    OrderCheckoutFailed failure,
    Emitter<OrderCheckoutState> emit,
  ) async {
    switch (failure.code) {
      case 'ESCROW_024':
        // Already funded.
        _page = null;
        emit(const CheckoutPaid());
      case 'ESCROW_030' when failure.statusCode == 422:
        emit(const CheckoutStopped(CheckoutIssue.notPayableOnline));
      case 'ESCROW_030':
        // Not waiting for payment, or the payment time ran out: the order
        // tells which, no message parsing.
        final Order? server;
        try {
          server = await _readServerOrder(orderId);
        } on Object {
          emit(const CheckoutStopped(CheckoutIssue.notWaiting));
          return;
        }
        if (server == null) {
          await _emitQueued(emit);
        } else if (server.status == OrderStatus.paymentSubmitted) {
          emit(const CheckoutStopped(CheckoutIssue.timeRanOut));
        } else {
          await _act(server, emit, openRequested: false);
        }
      case 'ESCROW_001':
        emit(const CheckoutStopped(CheckoutIssue.totalOutOfRange));
      case 'ESCROW_031':
        _confirmingAutomatically = true;
        emit(const CheckoutConfirmingAutomatically());
      case 'ESCROW_032':
        emit(const CheckoutProblem(CheckoutIssue.alreadyOpen));
      case 'ESCROW_013':
        emit(const CheckoutProblem(CheckoutIssue.gateway));
      case 'ESCROW_011':
        emit(const CheckoutProblem(CheckoutIssue.rateLimited));
      case 'ESCROW_033' when failure.statusCode == 404:
        emit(const CheckoutStopped(CheckoutIssue.notFound));
      case 'ESCROW_005':
        emit(const CheckoutStopped(CheckoutIssue.sessionExpired));
      case 'ESCROW_025' || 'ESCROW_026' || 'FRAUD_ACCOUNT_HELD':
        emit(const CheckoutStopped(CheckoutIssue.unauthorized));
      case 'ESCROW_OFFLINE':
        _retryOnReconnect = true;
        emit(const CheckoutProblem(CheckoutIssue.offline));
      default:
        emit(const CheckoutProblem(CheckoutIssue.unavailable));
    }
  }

  /// The order read failed: keep the page in hand and say so; otherwise
  /// show the offline problem (polling goes on).
  void _emitOffline(Emitter<OrderCheckoutState> emit) {
    final current = state;
    if (current is CheckoutAwaitingPayment) {
      final page = _page;
      if (page != null && page.isOpen(_monotonic())) {
        emit(_awaiting(page, offline: true));
        return;
      }
    }
    if (current is CheckoutProblem ||
        current is CheckoutConfirmingAutomatically ||
        current is CheckoutWaitingToSend) {
      return;
    }
    emit(const CheckoutProblem(CheckoutIssue.offline));
  }

  CheckoutAwaitingPayment _awaiting(
    _CheckoutPage page, {
    bool offline = false,
  }) {
    final left = page.timeLeft(_monotonic());
    return CheckoutAwaitingPayment(
      toPayUrl: page.toPayUrl,
      amount: page.amount,
      payableUntil: page.payableUntilOnDevice,
      timeLeft: left,
      launchToken: _launchToken,
      offline: offline,
    );
  }

  static CheckoutStopped _stoppedFor(OrderStatus status) => switch (status) {
        OrderStatus.paymentFailed =>
          const CheckoutStopped(CheckoutIssue.paymentFailed),
        OrderStatus.cancelled ||
        OrderStatus.rejected =>
          const CheckoutStopped(CheckoutIssue.notWaiting),
        // created / pending_payment and nothing queued: the payment was
        // refused and put back (ESC-12, KYC).
        _ => const CheckoutStopped(CheckoutIssue.notSent),
      };
}

/// A Telebirr page in hand. Its end is kept on a monotonic clock, so a
/// device clock that is wrong or changes cannot keep a dead page "open".
class _CheckoutPage {
  final String toPayUrl;
  final double amount;
  final Duration expiresAt;
  final DateTime payableUntilOnDevice;

  const _CheckoutPage({
    required this.toPayUrl,
    required this.amount,
    required this.expiresAt,
    required this.payableUntilOnDevice,
  });

  /// The time left is `openUntil` minus the server's clock when it answered
  /// (`Date`). Without that header (web hides it), the device clock is used,
  /// and an answer it cannot explain falls back to the known window.
  factory _CheckoutPage.from(
    OrderCheckoutOpened opened, {
    required Duration receivedAt,
    required DateTime deviceNow,
  }) {
    final serverNow = opened.serverDate ?? deviceNow.toUtc();
    var left = opened.openUntil.toUtc().difference(serverNow);
    if (opened.serverDate == null &&
        (left.isNegative || left > OrderCheckoutBloc._maxWindow)) {
      left = opened.isExisting
          ? OrderCheckoutBloc._unknownWindow
          : OrderCheckoutBloc._maxWindow;
    }
    if (left.isNegative) left = Duration.zero;
    return _CheckoutPage(
      toPayUrl: opened.toPayUrl,
      amount: opened.amount,
      expiresAt: receivedAt + left,
      payableUntilOnDevice: deviceNow.add(left),
    );
  }

  bool isOpen(Duration monotonicNow) => monotonicNow < expiresAt;

  Duration timeLeft(Duration monotonicNow) {
    final left = expiresAt - monotonicNow;
    return left.isNegative ? Duration.zero : left;
  }
}

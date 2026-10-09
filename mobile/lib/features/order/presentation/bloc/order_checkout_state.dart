import 'package:equatable/equatable.dart';

/// Why the checkout stopped, or what went wrong while it keeps watching the
/// order. Each one has its own message (`order_checkout_l10n.dart`).
enum CheckoutIssue {
  /// The order moved to `payment_failed`: the buyer can pay again.
  paymentFailed,

  /// The order was cancelled or rejected (or is otherwise not waiting).
  notWaiting,

  /// `ESCROW_030` while the order is still `payment_submitted`: no new
  /// payment 20 hours after it was submitted.
  timeRanOut,

  /// `422 ESCROW_030`: missing farmer, or the buyer is the farmer.
  notPayableOnline,

  /// `ESCROW_001`: outside the 100 to 500,000 ETB one payment can take.
  totalOutOfRange,

  /// The server has no such order (`404`).
  notFound,

  /// The payment never reached the server: it was refused (ESC-12, KYC) and
  /// the local order was put back.
  notSent,

  /// `401`: the session is gone.
  sessionExpired,

  /// `403`: not this buyer's order, not a buyer, or the account is held.
  unauthorized,

  /// `ESCROW_032`: a payment is being set up, or Telebirr's answer was lost;
  /// blocked until the old checkout ends (up to about 130 minutes).
  alreadyOpen,

  /// `ESCROW_013`: Telebirr refused or did not answer.
  gateway,

  /// `ESCROW_011`: too many payment attempts.
  rateLimited,

  /// `ESCROW_012` or another server error.
  unavailable,

  /// The request did not reach the server.
  offline,
}

/// The order's live Telebirr checkout, as the buyer sees it (ESC-44).
sealed class OrderCheckoutState extends Equatable {
  const OrderCheckoutState();

  /// Whether the screen should keep reading the order.
  bool get polls => false;

  @override
  List<Object?> get props => [];
}

/// Reading the order for the first time.
class CheckoutChecking extends OrderCheckoutState {
  const CheckoutChecking();
}

/// Mobile: the payment is still queued on the phone (offline, sync pending,
/// or held for a step-up). Only local reads until it is sent.
class CheckoutWaitingToSend extends OrderCheckoutState {
  /// A queued payment asked for a step-up nobody confirmed yet; the screen
  /// offers "Confirm it's you" (Fayda when [stepUpWithFayda]).
  final bool stepUpPending;
  final bool stepUpWithFayda;

  const CheckoutWaitingToSend({
    this.stepUpPending = false,
    this.stepUpWithFayda = false,
  });

  @override
  bool get polls => true;

  @override
  List<Object?> get props => [stepUpPending, stepUpWithFayda];
}

/// `POST /escrow/order-checkout` is in flight.
class CheckoutOpening extends OrderCheckoutState {
  const CheckoutOpening();
}

/// The Telebirr page is open; the app waits for the webhook to fund the
/// order.
class CheckoutAwaitingPayment extends OrderCheckoutState {
  /// The Telebirr H5 page. Transient: never stored.
  final String toPayUrl;

  /// The server's order total.
  final double amount;

  /// [payableUntil] on the device's clock, for display.
  final DateTime payableUntil;

  /// How long the page stays payable, measured when this state was made.
  final Duration timeLeft;

  /// Goes up with every new URL, so the screen opens Telebirr once per URL.
  final int launchToken;

  /// The last order read failed (offline); the screen says so.
  final bool offline;

  const CheckoutAwaitingPayment({
    required this.toPayUrl,
    required this.amount,
    required this.payableUntil,
    required this.timeLeft,
    required this.launchToken,
    this.offline = false,
  });

  @override
  bool get polls => true;

  @override
  List<Object?> get props =>
      [toPayUrl, amount, payableUntil, timeLeft, launchToken, offline];
}

/// `ESCROW_031`: no hosted checkout here (mock payments); the order is
/// confirmed automatically, so the app only waits for `funded`.
class CheckoutConfirmingAutomatically extends OrderCheckoutState {
  const CheckoutConfirmingAutomatically();

  @override
  bool get polls => true;
}

/// The Telebirr page expired (or none is open) and the order still waits
/// for payment: the buyer may start a new one.
class CheckoutWindowClosed extends OrderCheckoutState {
  const CheckoutWindowClosed();
}

/// Something went wrong opening the checkout. The app keeps reading the
/// order, because a payment may still land.
class CheckoutProblem extends OrderCheckoutState {
  final CheckoutIssue issue;

  /// Whether a retry button makes sense ([CheckoutIssue.rateLimited] has
  /// none: it would be refused again).
  bool get canRetry => issue != CheckoutIssue.rateLimited;

  const CheckoutProblem(this.issue);

  @override
  bool get polls => true;

  @override
  List<Object?> get props => [issue];
}

/// The order is funded (or further along): paid.
class CheckoutPaid extends OrderCheckoutState {
  const CheckoutPaid();
}

/// The checkout cannot go on; [issue] says why.
class CheckoutStopped extends OrderCheckoutState {
  final CheckoutIssue issue;

  const CheckoutStopped(this.issue);

  @override
  List<Object?> get props => [issue];
}

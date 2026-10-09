import 'package:equatable/equatable.dart';

/// Events of the order checkout (ESC-44).
sealed class OrderCheckoutEvent extends Equatable {
  const OrderCheckoutEvent();

  @override
  List<Object?> get props => [];
}

/// The screen opened. [autoOpen]: the buyer just paid or tapped "Continue
/// payment", so the first time the server says `payment_submitted` the
/// checkout is requested without another tap.
class CheckoutStarted extends OrderCheckoutEvent {
  final bool autoOpen;

  const CheckoutStarted({this.autoOpen = true});

  @override
  List<Object?> get props => [autoOpen];
}

/// Read the order again: a poll, an app resume, a return link, or "Check
/// payment status". Never requests a checkout on its own, except the one
/// [CheckoutStarted.autoOpen] still owes.
class CheckoutRefreshed extends OrderCheckoutEvent {
  const CheckoutRefreshed();
}

/// The buyer tapped "Start a new Telebirr payment" or a retry button: read
/// the order again, then request a checkout if it still waits for payment.
class CheckoutOpenRequested extends OrderCheckoutEvent {
  const CheckoutOpenRequested();
}

/// Mobile: the network came back. Retries a checkout request that failed
/// offline, once.
class CheckoutConnectivityRestored extends OrderCheckoutEvent {
  const CheckoutConnectivityRestored();
}

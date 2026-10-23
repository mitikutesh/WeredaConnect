import 'package:wereda_connect/core/models/payment.dart';

// FRD-13 (escrow-protocol.md 1.32.0): the server pays an escrow out no
// earlier than its `releaseNotBefore`, the end of the dispute window or, for
// a new or flagged payee, a later payout delay. Only the farmer's and the
// trucker's reads carry the date, and the screens never say why it is later.

/// When the payee can expect the payout of [payment], or null when there is
/// nothing to promise: the money is no longer held (paid out, refunded,
/// disputed, under review), its refund to the buyer is on the way, or the
/// server sent no date (the buyer's reads, or an older server).
DateTime? expectedPayoutAt(Payment payment) {
  final at = payment.releaseNotBefore;
  if (at == null ||
      payment.status != PaymentStatus.escrowed ||
      payment.isRefundPending) {
    return null;
  }
  return at;
}

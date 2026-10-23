/// Escrow status wire-format parsing.
///
/// The server's escrow `status` is an open set (escrow-protocol.md 1.7.0): a
/// client must not fail on a value it does not know and must not show it as
/// a state that offers actions. `PaymentStatus.values.byName` threw on a new
/// value (`dispute_resolved` already broke escrow detail and history on
/// mobile; FRD-03 adds `held`), and the web mapper showed it as `initiated`.
library;

import 'package:wereda_connect/core/models/payment.dart';

/// The [PaymentStatus] for a server escrow `status`.
///
/// - Every known name maps to itself (`held` included).
/// - `dispute_resolved` (the admin's verdict is recorded and the hourly sweep
///   settles it) reads as [PaymentStatus.disputed]: the money is still frozen
///   and the party has nothing to do.
/// - Anything else, or no value, reads as [PaymentStatus.unknown]: neutral,
///   with no actions. Payment rows are never sent back to the server, so the
///   fallback never overwrites the server's value.
PaymentStatus paymentStatusFromWire(String? wire) {
  if (wire == 'dispute_resolved') return PaymentStatus.disputed;
  return PaymentStatus.values.asNameMap()[wire] ?? PaymentStatus.unknown;
}

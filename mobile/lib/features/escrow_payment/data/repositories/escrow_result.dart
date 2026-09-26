import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

/// Result type for escrow operations.
///
/// Uses sealed class pattern for exhaustive switch matching:
/// - [EscrowSuccess]: Server confirmed the operation.
/// - [EscrowQueued]: Intent queued locally for offline sync.
/// - [EscrowError]: Operation failed with an error code.
sealed class EscrowResult {
  const EscrowResult();
}

/// Server confirmed the escrow operation.
class EscrowSuccess extends EscrowResult {
  final Payment payment;

  /// Async gateways only: the Telebirr H5 checkout URL the buyer must open to
  /// pay. Present when the escrow is `initiated` (real gateway); null for the
  /// mock (which returns `escrowed` directly) and for non-initiate operations.
  /// Transient — never persisted on [Payment].
  final String? toPayUrl;

  const EscrowSuccess(this.payment, {this.toPayUrl});
}

/// Payment intent queued locally (offline, will sync when online).
class EscrowQueued extends EscrowResult {
  final PaymentIntent intent;
  const EscrowQueued(this.intent);
}

/// Escrow operation failed.
class EscrowError extends EscrowResult {
  final String code;
  final String message;
  const EscrowError(this.code, this.message);
}

/// The server's own `ESCROW_*` code when the error envelope carried one, else
/// [fallback] for the HTTP class. SIM-24: a 409 is `ESCROW_009` (a second
/// escrow on the delivery) or `ESCROW_024` (funds already held for the order);
/// the status alone cannot tell them apart and the UI keys its copy on the code.
String escrowCodeOf(ApiException e, String fallback) {
  final code = e.code;
  return code != null && code.startsWith('ESCROW_') ? code : fallback;
}

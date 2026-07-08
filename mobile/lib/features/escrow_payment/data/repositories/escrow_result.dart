import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';

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

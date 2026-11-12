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

/// Result of `POST /escrow/order-checkout` (ESC-44). Online only: a checkout
/// is never queued for later, so there is no queued variant.
sealed class OrderCheckoutResult {
  const OrderCheckoutResult();
}

/// The live Telebirr checkout is open: a new one, or the same one again
/// ([isExisting]).
class OrderCheckoutOpened extends OrderCheckoutResult {
  /// The Telebirr H5 page. Transient: never persisted.
  final String toPayUrl;

  /// When the checkout stops being payable, on the server's clock.
  final DateTime openUntil;

  /// The server's order total, what Telebirr charges.
  final double amount;
  final bool isExisting;

  /// The server's clock when it answered (`Date` header), or null when the
  /// platform hides it (web). Used to measure [openUntil] without trusting
  /// the device clock.
  final DateTime? serverDate;

  const OrderCheckoutOpened({
    required this.toPayUrl,
    required this.openUntil,
    required this.amount,
    this.isExisting = false,
    this.serverDate,
  });
}

/// The checkout was refused or could not be reached. [statusCode] is null for
/// network errors and timeouts ([code] `ESCROW_OFFLINE`); `ESCROW_030` needs
/// it (409 not waiting or time ran out, 422 cannot be paid online).
class OrderCheckoutFailed extends OrderCheckoutResult {
  final String code;
  final int? statusCode;
  final String message;

  const OrderCheckoutFailed(this.code, this.statusCode, this.message);
}

/// Map a typed [ApiException] from `POST /escrow/order-checkout` to an
/// [OrderCheckoutFailed], keeping the server's code (`ESCROW_*`, or
/// `FRAUD_ACCOUNT_HELD`) and the HTTP status. Shared by the mobile and web
/// escrow repositories.
OrderCheckoutFailed orderCheckoutFailureOf(ApiException e) {
  if (e is NetworkException || e is TimeoutException) {
    return OrderCheckoutFailed('ESCROW_OFFLINE', null, e.message);
  }
  final code = e.code;
  final fallback = switch (e.statusCode) {
    401 => 'ESCROW_005',
    403 => 'ESCROW_025',
    429 => 'ESCROW_011',
    _ => 'ESCROW_012',
  };
  return OrderCheckoutFailed(
    code != null && code.isNotEmpty ? code : fallback,
    e.statusCode,
    e.message,
  );
}

/// The server's own `ESCROW_*` code when the error envelope carried one, else
/// [fallback] for the HTTP class. SIM-24: a 409 is `ESCROW_009` (a second
/// escrow on the delivery) or `ESCROW_024` (funds already held for the order);
/// the status alone cannot tell them apart and the UI keys its copy on the code.
/// `FRAUD_ACCOUNT_HELD` (a 403) is kept too: the account is under review, which
/// is not "sign in again".
///
/// One refinement (FUP-77): a dispute refused because the buyer's refund is
/// already scheduled comes as the shared `ESCROW_INVALID_TRANSITION`; it is
/// told apart by the server's text and read as
/// [escrowDisputeRefundScheduledCode], so the screen can say why.
String escrowCodeOf(ApiException e, String fallback) {
  final code = e.code;
  if (code == 'ESCROW_INVALID_TRANSITION' &&
      e.message.contains(_refundScheduledToken)) {
    return escrowDisputeRefundScheduledCode;
  }
  return code != null &&
          (code.startsWith('ESCROW_') || code == fraudAccountHeldCode)
      ? code
      : fallback;
}

/// Client-side code for `409 ESCROW_INVALID_TRANSITION` on
/// `POST /escrow/dispute` when the refusal is "a refund to the buyer is
/// scheduled" (FUP-32). The server has no code of its own for it
/// (escrow-protocol.md 2.7.0 pins the wording).
const String escrowDisputeRefundScheduledCode =
    'ESCROW_INVALID_TRANSITION_REFUND_SCHEDULED';

const String _refundScheduledToken = 'refund to the buyer is scheduled';

/// Whether the server's answer to a queued escrow call can never change for
/// the same request, so sending it again only burns retries (FUP-70).
///
/// The rule the sync executor uses for a refused batch: `400` and `422` are
/// permanent. Any other `4xx` is permanent only when it carries the server's
/// own `ESCROW_*` code (a typed refusal such as `403 ESCROW_026`), and
/// never for `401` (the session is refreshed), `404`, `408` or `429` (retry
/// later). A `404` is never permanent: `404 ESCROW_008` (delivery not found)
/// is what a payment queued offline gets when it is sent before `/sync`
/// wrote its delivery, and the next run finds it. `5xx`, network errors and
/// timeouts stay retryable. Step-up (`AUTH_040`, `AUTH_041`) and `409` are
/// not failures and are handled by the caller before this.
bool isPermanentEscrowRefusal(ApiException e) {
  final status = e.statusCode;
  if (status == null || status < 400 || status > 499) return false;
  if (status == 400 || status == 422) return true;
  if (status == 401 || status == 404 || status == 408 || status == 429) {
    return false;
  }
  return e.code?.startsWith('ESCROW_') ?? false;
}

/// The code a failed queued escrow call is stored and shown under (FUP-70):
/// the server's own code when it sent one, else a client code for the kind
/// of failure. Never the server's English text.
String escrowFailureCodeOf(Object error) {
  if (error is! ApiException) return 'ESCROW_CLIENT';
  final fallback = switch (error) {
    NetworkException() => 'ESCROW_OFFLINE',
    // A timeout happens while online, so "offline" would be wrong. The
    // escrow texts have none for a timeout: the generic "try again" one.
    TimeoutException() => 'ESCROW_CLIENT',
    RateLimitException() => 'ESCROW_011',
    _ => switch (error.statusCode) {
        502 => 'ESCROW_013',
        503 => 'ESCROW_014',
        401 => 'ESCROW_005',
        _ => 'ESCROW_CLIENT',
      },
  };
  return escrowCodeOf(error, fallback);
}

import 'dart:async';
import 'dart:collection';

/// Something background work cannot settle alone: a prompt the user must
/// answer before a money action can go through, or a refusal the app must
/// explain.
sealed class SecurityEvent {
  const SecurityEvent();
}

/// The server wants the user to confirm it is them before a money action:
/// a fresh password (AUTH_040), or for a payment above 10,000 ETB a fresh
/// Fayda login (AUTH_041, [withFayda]).
class StepUpRequired extends SecurityEvent {
  const StepUpRequired({this.withFayda = false});

  /// True when only a Fayda login satisfies the server; the password
  /// prompt would not help.
  final bool withFayda;
}

/// Whose verification a KYC rejection says is missing.
///
/// The server writes `ORDER_KYC_REQUIRED: <reason>: <text>`
/// (specs/contracts/sync-protocol.md). Only the buyer-side reasons can be
/// fixed by the buyer verifying; for the others, sending the buyer to Fayda
/// would verify them and still leave the payment blocked.
enum KycReason {
  buyerPhone,
  buyerFayda,
  farmerPhone,
  farmerFayda,
  unavailable;

  /// True when verifying the signed-in buyer can unblock the payment.
  bool get buyerCanFix => this == buyerPhone || this == buyerFayda;

  static const _prefix = 'ORDER_KYC_REQUIRED:';

  /// Reads the reason token; falls back to the wording of servers that
  /// predate it.
  static KycReason fromMessage(String message) {
    final at = message.indexOf(_prefix);
    final rest = at < 0 ? message : message.substring(at + _prefix.length);
    final token = rest.trimLeft().split(':').first.trim();
    switch (token) {
      case 'buyer_phone':
        return KycReason.buyerPhone;
      case 'buyer_fayda':
        return KycReason.buyerFayda;
      case 'farmer_phone':
        return KycReason.farmerPhone;
      case 'farmer_fayda':
        return KycReason.farmerFayda;
      case 'unavailable':
        return KycReason.unavailable;
    }
    final text = rest.toLowerCase();
    if (text.contains('unavailable')) return KycReason.unavailable;
    final phone = text.contains('phone');
    if (text.contains('farmer')) {
      return phone ? KycReason.farmerPhone : KycReason.farmerFayda;
    }
    return phone ? KycReason.buyerPhone : KycReason.buyerFayda;
  }
}

/// A payment was blocked by the KYC gate (ORDER_KYC_REQUIRED).
class KycRequired extends SecurityEvent {
  /// The order whose payment was rejected.
  final String orderId;

  /// The server message, carrying the [KycReason].
  final String message;

  /// The rejected submission, so it can be sent again once the buyer is
  /// verified. Null when the operation did not carry them.
  final String? paymentMethod;
  final String? paymentReference;

  const KycRequired({
    required this.orderId,
    required this.message,
    this.paymentMethod,
    this.paymentReference,
  });

  KycReason get reason => KycReason.fromMessage(message);

  /// True when the rejected payment can be submitted again as it was.
  bool get canResubmit =>
      (paymentMethod?.isNotEmpty ?? false) &&
      (paymentReference?.isNotEmpty ?? false);
}

/// The server refused a queued operation for good
/// (specs/contracts/sync-protocol.md 1.10.0): the sync queue stopped
/// retrying it (and undid the local change of an order), so the user must be
/// told why.
class SyncOperationRejected extends SecurityEvent {
  /// The sync entity type (`order`, ...).
  final String entityType;
  final String entityId;

  /// The refused sync action (`create`, `update`, `delete`).
  final String action;

  /// The server message (`ORDER_VALIDATION: ...`); English, never shown.
  final String message;

  /// Whose operation it was (the idempotency key's user), so another user
  /// signed in on a shared device is not shown it. Null when unknown.
  final String? userId;

  const SyncOperationRejected({
    required this.entityType,
    required this.entityId,
    required this.action,
    required this.message,
    this.userId,
  });
}

/// App-wide bus between background workers (sync executor, escrow intent
/// sync) and the UI.
///
/// Workers raise [SecurityEvent]s they cannot resolve on their own; the
/// `SecurityPromptListener` above the authenticated shell shows the matching
/// dialog or message. After a successful step-up the UI calls
/// [stepUpCompleted] and the workers retry their pending operations.
///
/// A [SyncOperationRejected] raised while nobody listens (the first sync
/// runs before the signed-in shell mounts the listener) is kept, up to
/// [maxUndeliveredRejections], and delivered to the first listener, so a
/// change made offline and refused on the next launch is still explained.
/// Logout drops them ([clearKeptRejections]).
///
/// When the user cancels the step-up prompt, further [StepUpRequired] events
/// are dropped for [stepUpCooldown] so every later background sync
/// (enqueue, connectivity, periodic) does not reopen the dialog. A step-up
/// the user starts themselves (the pay button) still prompts, and a
/// successful one ends the quiet period.
class SecurityEvents {
  SecurityEvents._({DateTime Function()? now}) : _now = now ?? DateTime.now;

  /// Shared instance used by the app.
  static final SecurityEvents instance = SecurityEvents._();

  /// A separate instance for tests, with an optional clock.
  factory SecurityEvents.forTesting({DateTime Function()? now}) =>
      SecurityEvents._(now: now);

  /// How long a cancelled step-up prompt stays quiet.
  static const stepUpCooldown = Duration(minutes: 10);

  final DateTime Function() _now;
  DateTime? _stepUpDeclinedAt;

  /// How many undelivered [SyncOperationRejected] events are kept; the
  /// oldest is dropped beyond that.
  static const maxUndeliveredRejections = 10;

  final Queue<SyncOperationRejected> _undeliveredRejections =
      ListQueue<SyncOperationRejected>();

  late final StreamController<SecurityEvent> _events =
      StreamController<SecurityEvent>.broadcast(
    onListen: _deliverKeptRejections,
  );
  final StreamController<void> _stepUpCompleted =
      StreamController<void>.broadcast();

  /// Events the UI should prompt for.
  Stream<SecurityEvent> get events => _events.stream;

  /// Fires after the user re-authenticated; pending money ops should retry.
  Stream<void> get onStepUpCompleted => _stepUpCompleted.stream;

  /// Raise an event for the UI. A [StepUpRequired] inside the cooldown
  /// after a cancelled prompt is dropped. A [SyncOperationRejected] with no
  /// listener is kept for the first one.
  void emit(SecurityEvent event) {
    if (event is StepUpRequired && stepUpSuppressed) return;
    if (_events.isClosed) return;
    if (event is SyncOperationRejected && !_events.hasListener) {
      if (_undeliveredRejections.length >= maxUndeliveredRejections) {
        _undeliveredRejections.removeFirst();
      }
      _undeliveredRejections.add(event);
      return;
    }
    _events.add(event);
  }

  void _deliverKeptRejections() {
    while (_undeliveredRejections.isNotEmpty) {
      _events.add(_undeliveredRejections.removeFirst());
    }
  }

  /// Drop the kept [SyncOperationRejected] events (logout), so the next
  /// account signed in on this device is not shown them.
  void clearKeptRejections() => _undeliveredRejections.clear();

  /// True while a cancelled step-up prompt keeps background re-prompts quiet.
  bool get stepUpSuppressed {
    final declinedAt = _stepUpDeclinedAt;
    return declinedAt != null && _now().difference(declinedAt) < stepUpCooldown;
  }

  /// The user cancelled the background step-up prompt.
  void stepUpDeclined() => _stepUpDeclinedAt = _now();

  /// Report a successful step-up re-authentication.
  void stepUpCompleted() {
    _stepUpDeclinedAt = null;
    if (!_stepUpCompleted.isClosed) _stepUpCompleted.add(null);
  }
}

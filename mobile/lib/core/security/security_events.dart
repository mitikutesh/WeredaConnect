import 'dart:async';

/// Something the user must act on before a money action can go through.
sealed class SecurityEvent {
  const SecurityEvent();
}

/// The server wants a fresh password before a high-value action (AUTH_040).
class StepUpRequired extends SecurityEvent {
  const StepUpRequired();
}

/// A payment was blocked by the KYC gate (ORDER_KYC_REQUIRED).
class KycRequired extends SecurityEvent {
  /// The order whose payment was rejected.
  final String orderId;

  /// The server message, used to tell a phone problem from a Fayda one.
  final String message;

  const KycRequired({required this.orderId, required this.message});

  /// True when the server says the phone (not Fayda) is the problem.
  bool get isPhoneNotVerified => isPhoneProblem(message);

  /// Whether a KYC rejection [message] blames the phone rather than Fayda.
  /// Shared by the background prompt and the payment screen.
  static bool isPhoneProblem(String message) =>
      message.toLowerCase().contains('phone');
}

/// App-wide bus between background workers (sync executor, escrow intent
/// sync) and the UI.
///
/// Workers raise [SecurityEvent]s they cannot resolve on their own; the
/// `SecurityPromptListener` above the authenticated shell shows the matching
/// dialog. After a successful step-up the UI calls [stepUpCompleted] and the
/// workers retry their pending operations.
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

  final StreamController<SecurityEvent> _events =
      StreamController<SecurityEvent>.broadcast();
  final StreamController<void> _stepUpCompleted =
      StreamController<void>.broadcast();

  /// Events the UI should prompt for.
  Stream<SecurityEvent> get events => _events.stream;

  /// Fires after the user re-authenticated; pending money ops should retry.
  Stream<void> get onStepUpCompleted => _stepUpCompleted.stream;

  /// Raise an event for the UI. A [StepUpRequired] inside the cooldown
  /// after a cancelled prompt is dropped.
  void emit(SecurityEvent event) {
    if (event is StepUpRequired && stepUpSuppressed) return;
    if (!_events.isClosed) _events.add(event);
  }

  /// True while a cancelled step-up prompt keeps background re-prompts quiet.
  bool get stepUpSuppressed {
    final declinedAt = _stepUpDeclinedAt;
    return declinedAt != null &&
        _now().difference(declinedAt) < stepUpCooldown;
  }

  /// The user cancelled the background step-up prompt.
  void stepUpDeclined() => _stepUpDeclinedAt = _now();

  /// Report a successful step-up re-authentication.
  void stepUpCompleted() {
    _stepUpDeclinedAt = null;
    if (!_stepUpCompleted.isClosed) _stepUpCompleted.add(null);
  }
}

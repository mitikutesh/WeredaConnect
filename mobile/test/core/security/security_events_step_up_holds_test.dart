import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/security/security_events.dart';

/// FUP-24: `pendingStepUp` follows the operations held for it.
void main() {
  late SecurityEvents events;

  setUp(() => events = SecurityEvents.forTesting());

  void raise(String holder, {bool withFayda = false}) {
    events
      ..holdStepUp(holder, withFayda: withFayda)
      ..emit(StepUpRequired(withFayda: withFayda));
  }

  test('cleared once the held operation left the queue', () {
    raise('sync:op-1');
    expect(events.pendingStepUp, isNotNull);

    events.releaseStepUpHolds(['sync:op-1']);

    expect(events.pendingStepUp, isNull);
    expect(events.stepUpHolders('sync:'), isEmpty);
  });

  test('kept while another held operation still waits', () {
    raise('sync:op-1');
    raise('intent:7');

    events.releaseStepUpHolds(['sync:op-1']);

    expect(events.pendingStepUp, isNotNull);
    expect(events.stepUpHolders('sync:'), isEmpty);
    expect(events.stepUpHolders('intent:'), ['intent:7']);

    events.releaseStepUpHolds(['intent:7']);
    expect(events.pendingStepUp, isNull);
  });

  test('asks for Fayda only while an operation that needs it waits', () {
    raise('sync:op-1');
    raise('sync:op-2', withFayda: true);
    expect(events.pendingStepUp?.withFayda, isTrue);

    events.releaseStepUpHolds(['sync:op-2']);

    expect(events.pendingStepUp?.withFayda, isFalse);
  });

  test('kept after a cancelled prompt (ESC-44)', () {
    raise('sync:op-1');

    events.stepUpDeclined();

    expect(events.stepUpSuppressed, isTrue);
    expect(events.pendingStepUp, isNotNull);
    expect(events.stepUpHolders('sync:'), ['sync:op-1']);
  });

  test('releasing an operation that was never held changes nothing', () {
    raise('sync:op-1');

    events.releaseStepUpHolds(['sync:other', 'intent:1']);

    expect(events.pendingStepUp, isNotNull);
  });

  test('a step-up raised without a named operation is left alone', () {
    events
      ..emit(const StepUpRequired())
      ..releaseStepUpHolds(['sync:op-1']);

    expect(events.pendingStepUp, isNotNull);
  });

  test('a completed step-up and a logout forget the holds', () {
    raise('sync:op-1');
    events.stepUpCompleted();
    expect(events.pendingStepUp, isNull);
    expect(events.stepUpHolders('sync:'), isEmpty);

    raise('sync:op-2');
    events.clearKeptRejections();
    expect(events.pendingStepUp, isNull);
    expect(events.stepUpHolders('sync:'), isEmpty);
  });
}

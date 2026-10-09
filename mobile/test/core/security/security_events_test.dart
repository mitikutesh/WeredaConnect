import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/security/security_events.dart';

SyncOperationRejected _rejected(String orderId) => SyncOperationRejected(
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      message: 'ORDER_VALIDATION: no',
    );

/// Polls [condition] for up to 5 seconds instead of sleeping a fixed time.
Future<void> _waitFor(bool Function() condition) async {
  final deadline = DateTime.now().add(const Duration(seconds: 5));
  while (!condition() && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// ESC-12: the first sync can run before the signed-in shell mounts its
/// listener, so a rejection raised then must still be explained.
void main() {
  test('a rejection with no listener is delivered to the first one', () async {
    final events = SecurityEvents.forTesting()
      ..emit(_rejected('o-1'))
      ..emit(_rejected('o-2'));

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await _waitFor(() => received.length >= 2);
    await sub.cancel();

    expect(
      received.cast<SyncOperationRejected>().map((e) => e.entityId),
      ['o-1', 'o-2'],
    );
  });

  test('at most ${SecurityEvents.maxUndeliveredRejections} are kept, newest',
      () async {
    final events = SecurityEvents.forTesting();
    const total = SecurityEvents.maxUndeliveredRejections + 2;
    for (var i = 0; i < total; i++) {
      events.emit(_rejected('o-$i'));
    }

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await _waitFor(
      () => received.length >= SecurityEvents.maxUndeliveredRejections,
    );
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();

    expect(received, hasLength(SecurityEvents.maxUndeliveredRejections));
    expect((received.first as SyncOperationRejected).entityId, 'o-2');
    expect((received.last as SyncOperationRejected).entityId, 'o-${total - 1}');
  });

  test('kept rejections are delivered once, not to a later listener', () async {
    final events = SecurityEvents.forTesting()..emit(_rejected('o-1'));
    final first = <SecurityEvent>[];
    final firstSub = events.events.listen(first.add);
    await _waitFor(() => first.isNotEmpty);
    await firstSub.cancel();

    final second = <SecurityEvent>[];
    final secondSub = events.events.listen(second.add);
    await Future<void>.delayed(Duration.zero);
    await secondSub.cancel();

    expect(first, hasLength(1));
    expect(second, isEmpty);
  });

  test('with a listener, a rejection is delivered straight away', () async {
    final events = SecurityEvents.forTesting();
    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);

    events.emit(_rejected('o-1'));
    await _waitFor(() => received.isNotEmpty);
    await sub.cancel();

    expect(received, hasLength(1));
  });

  test('prompts are not kept: a step-up with no listener is dropped', () async {
    final events = SecurityEvents.forTesting()
      ..emit(const StepUpRequired())
      ..emit(const KycRequired(orderId: 'o-1', message: 'ORDER_KYC_REQUIRED'));

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();

    expect(received, isEmpty);
  });
}

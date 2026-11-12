import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/security/security_events.dart';

import '../../helpers/wait_for.dart';

SyncOperationRejected _rejected(String orderId) => SyncOperationRejected(
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      message: 'ORDER_VALIDATION: no',
    );

/// ESC-12: the first sync can run before the signed-in shell mounts its
/// listener, so a rejection raised then must still be explained.
void main() {
  test('a rejection with no listener is delivered to the first one', () async {
    final events = SecurityEvents.forTesting()
      ..emit(_rejected('o-1'))
      ..emit(_rejected('o-2'));

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await waitFor(() => received.length >= 2);
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
    await waitFor(
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
    await waitFor(() => first.isNotEmpty);
    await firstSub.cancel();

    final second = <SecurityEvent>[];
    final secondSub = events.events.listen(second.add);
    await Future<void>.delayed(Duration.zero);
    await secondSub.cancel();

    expect(first, hasLength(1));
    expect(second, isEmpty);
  });

  test('logout drops kept rejections (clearKeptRejections)', () async {
    final events = SecurityEvents.forTesting()
      ..emit(_rejected('o-1'))
      ..clearKeptRejections();

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();

    expect(received, isEmpty);
  });

  test('with a listener, a rejection is delivered straight away', () async {
    final events = SecurityEvents.forTesting();
    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);

    events.emit(_rejected('o-1'));
    await waitFor(() => received.isNotEmpty);
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

  group('ESC-44', () {
    test('a step-up nobody confirmed is remembered, also while quiet', () {
      final events = SecurityEvents.forTesting()..stepUpDeclined();
      expect(events.pendingStepUp, isNull);

      events.emit(const StepUpRequired(withFayda: true));

      expect(events.stepUpSuppressed, isTrue);
      expect(events.pendingStepUp?.withFayda, isTrue);
    });

    test('a completed step-up or a logout forgets it', () {
      final events = SecurityEvents.forTesting()..emit(const StepUpRequired());
      expect(events.pendingStepUp, isNotNull);

      events.stepUpCompleted();
      expect(events.pendingStepUp, isNull);

      events
        ..emit(const StepUpRequired())
        ..clearKeptRejections();
      expect(events.pendingStepUp, isNull);
    });

    test('a live payment (no reference) can be submitted again', () {
      const live = KycRequired(
        orderId: 'o-1',
        message: 'ORDER_KYC_REQUIRED: buyer_fayda: verify',
        paymentMethod: 'telebirr',
      );
      const noMethod = KycRequired(
        orderId: 'o-1',
        message: 'ORDER_KYC_REQUIRED: buyer_fayda: verify',
        paymentReference: 'MOCK-CBE-1',
      );

      expect(live.canResubmit, isTrue);
      expect(noMethod.canResubmit, isFalse);
    });
  });

  // ESC-12 part B: a refusal for an old app build leaves a flag the shell
  // can read later (ESC-25 banner); no client version gating.
  group('APP_UPDATE_REQUIRED (FUP-01)', () {
    SyncOperationRejected appTooOld(String entityType) => SyncOperationRejected(
          entityType: entityType,
          entityId: 'x-1',
          action: 'create',
          message: 'APP_UPDATE_REQUIRED: minimum 2.0.0',
        );

    test('sets the flag even with no listener, and fires per refusal',
        () async {
      final events = SecurityEvents.forTesting();
      expect(events.appUpdateRequired, isFalse);

      events.emit(appTooOld('order'));
      expect(events.appUpdateRequired, isTrue);

      final fired = <void>[];
      final sub = events.onAppUpdateRequired.listen(fired.add);
      events.emit(appTooOld('trip'));
      await waitFor(() => fired.isNotEmpty);
      await sub.cancel();

      expect(fired, hasLength(1));
      // The refusal itself is still kept for the snackbar.
      final received = <SecurityEvent>[];
      final eventsSub = events.events.listen(received.add);
      await waitFor(() => received.length >= 2);
      await eventsSub.cancel();
      expect(received, hasLength(2));
    });

    test('logout keeps it (the build is still old)', () {
      final events = SecurityEvents.forTesting()..emit(appTooOld('order'));

      events.clearKeptRejections();

      expect(events.appUpdateRequired, isTrue);
    });

    test('other refusals leave it unset', () {
      final events = SecurityEvents.forTesting()
        ..emit(_rejected('o-1'))
        ..emit(
          const SyncOperationRejected(
            entityType: 'trip',
            entityId: 't-1',
            action: 'update',
            message: 'TRIP_HAS_BOOKINGS: booked',
          ),
        )
        ..emit(const StepUpRequired());

      expect(events.appUpdateRequired, isFalse);
    });
  });

  // FUP-75: the flag outlives a restart on the same build only.
  group('APP_UPDATE_REQUIRED kept per build (FUP-75)', () {
    // Not the running build's number, so the tests hold after a bump.
    const oldBuild = 7;
    const newBuild = 8;
    const store = AppUpdateFlagStore();
    const order = SyncOperationRejected(
      entityType: 'order',
      entityId: 'o-1',
      action: 'update',
      message: 'APP_UPDATE_REQUIRED: too old',
    );

    Future<int?> kept() async =>
        (await SharedPreferences.getInstance()).getInt(AppUpdateFlagStore.key);

    setUp(() => SharedPreferences.setMockInitialValues({}));

    test('a refusal keeps the running build', () async {
      SecurityEvents.forTesting(appUpdateStore: store, appBuild: oldBuild)
          .emit(order);

      await waitFor(() async => await kept() == oldBuild);
      expect(await kept(), oldBuild);
    });

    test('a web refusal raises and keeps it too', () async {
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: oldBuild,
      );
      final fired = <void>[];
      final sub = events.onAppUpdateRequired.listen(fired.add);

      events.reportAppUpdateRequired();
      await waitFor(() async => fired.isNotEmpty && await kept() == oldBuild);
      await sub.cancel();

      expect(events.appUpdateRequired, isTrue);
      expect(fired, hasLength(1));
      expect(await kept(), oldBuild);
    });

    test('a restart on the same build brings it back and fires', () async {
      SharedPreferences.setMockInitialValues({
        AppUpdateFlagStore.key: oldBuild,
      });
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: oldBuild,
      );
      final fired = <void>[];
      final sub = events.onAppUpdateRequired.listen(fired.add);
      expect(events.appUpdateRequired, isFalse);

      await events.restoreAppUpdateRequired();
      await waitFor(() => fired.isNotEmpty);
      await sub.cancel();

      expect(events.appUpdateRequired, isTrue);
      expect(fired, hasLength(1));
      expect(await kept(), oldBuild);
    });

    test('a new build forgets it', () async {
      SharedPreferences.setMockInitialValues({
        AppUpdateFlagStore.key: oldBuild,
      });
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: newBuild,
      );

      await events.restoreAppUpdateRequired();

      expect(events.appUpdateRequired, isFalse);
      expect(await kept(), isNull);
    });

    test('a new build refused before the read keeps its own number', () async {
      SharedPreferences.setMockInitialValues({
        AppUpdateFlagStore.key: oldBuild,
      });
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: newBuild,
      );

      final restoring = events.restoreAppUpdateRequired();
      events.emit(order);
      await restoring;
      await waitFor(() async => await kept() == newBuild);

      expect(events.appUpdateRequired, isTrue);
      expect(await kept(), newBuild);
    });

    test('nothing kept: nothing raised, nothing written', () async {
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: oldBuild,
      );

      await events.restoreAppUpdateRequired();

      expect(events.appUpdateRequired, isFalse);
      expect(await kept(), isNull);
    });

    test('reads once per run', () async {
      final events = SecurityEvents.forTesting(
        appUpdateStore: store,
        appBuild: oldBuild,
      );
      await events.restoreAppUpdateRequired();
      // Written by another run after this one read: not read again.
      SharedPreferences.setMockInitialValues({
        AppUpdateFlagStore.key: oldBuild,
      });

      await events.restoreAppUpdateRequired();

      expect(events.appUpdateRequired, isFalse);
    });

    test('without a store it stays in memory only', () async {
      SharedPreferences.setMockInitialValues({
        AppUpdateFlagStore.key: oldBuild,
      });
      final events = SecurityEvents.forTesting(appBuild: oldBuild)..emit(order);

      await events.restoreAppUpdateRequired();

      expect(events.appUpdateRequired, isTrue);
      expect(await kept(), oldBuild);
    });
  });
}

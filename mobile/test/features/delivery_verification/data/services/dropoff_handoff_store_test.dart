import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';

class _MockStorage extends Mock implements FlutterSecureStorage {}

/// Secure storage whose every call yields to the event loop before it acts,
/// so calls from different stores interleave as they do in the app (the
/// scan screen keeping one handoff while trip mode or the check-in engine
/// drops another).
class _InterleavingStorage extends Fake implements FlutterSecureStorage {
  final Map<String, String> values = {};

  Future<void> _turn() => Future<void>.delayed(Duration.zero);

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    await _turn();
    return values[key];
  }

  @override
  Future<Map<String, String>> readAll({
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    await _turn();
    return Map.of(values);
  }

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    await _turn();
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    await _turn();
    values.remove(key);
  }
}

/// T-3: the trucker's phone keeps the pickup handoff for the drop-off QR,
/// across restarts, one entry per account and delivery, the newest twenty.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  /// A clock that moves on a millisecond at each reading.
  DateTime Function() ticking() {
    var at = DateTime.utc(2026, 11, 4, 6);
    return () => at = at.add(const Duration(milliseconds: 1));
  }

  test('keeps the token and the code per trucker and delivery', () async {
    final store = SecureDropoffHandoffStore();
    await store.keep(
      'trucker-1',
      'd-1',
      const KeptHandoff(token: 'tok', code: '757555978'),
    );

    // A new store reads what the last one wrote (the app restarted).
    final read = await SecureDropoffHandoffStore().read('trucker-1', 'd-1');
    expect(read?.token, 'tok');
    expect(read?.code, '757555978');
    expect(await store.read('trucker-2', 'd-1'), isNull);
    expect(await store.read('trucker-1', 'd-2'), isNull);
  });

  test('a typed pickup keeps only its code', () async {
    final store = SecureDropoffHandoffStore();
    await store.keep('t', 'd-1', const KeptHandoff(code: '340404994'));

    final read = await store.read('t', 'd-1');
    expect(read?.token, isNull);
    expect(read?.code, '340404994');
  });

  test('nothing to keep is not written', () async {
    final store = SecureDropoffHandoffStore();
    await store.keep('t', 'd-1', const KeptHandoff());

    expect(await store.read('t', 'd-1'), isNull);
  });

  test('keeps the newest twenty deliveries per trucker', () async {
    final store = SecureDropoffHandoffStore(now: ticking());
    for (var i = 0; i < 22; i++) {
      await store.keep('t', 'd-$i', KeptHandoff(code: '$i'));
    }
    // Another trucker's entries do not count against this one.
    await store.keep('u', 'd-0', const KeptHandoff(code: 'u'));
    // Keeping one again makes it the newest.
    await store.keep('t', 'd-2', const KeptHandoff(code: 'again'));

    expect(await store.read('t', 'd-0'), isNull);
    expect(await store.read('t', 'd-1'), isNull);
    expect((await store.read('t', 'd-2'))?.code, 'again');
    expect((await store.read('t', 'd-3'))?.code, '3');
    expect((await store.read('t', 'd-21'))?.code, '21');
    expect((await store.read('u', 'd-0'))?.code, 'u');

    // One more pushes out the oldest, d-3, not the renewed d-2.
    await store.keep('t', 'd-22', const KeptHandoff(code: '22'));
    expect(await store.read('t', 'd-3'), isNull);
    expect((await store.read('t', 'd-2'))?.code, 'again');
  });

  test('an unreadable entry reads as nothing', () async {
    FlutterSecureStorage.setMockInitialValues({
      'dropoff_handoff|t|d-1': '{bad',
    });

    expect(await SecureDropoffHandoffStore().read('t', 'd-1'), isNull);
  });

  test('keep and forget at the same time never undo each other', () async {
    // The scan screen keeps d-new while trip mode and the check-in engine
    // drop the done d-old and d-done, each through its own store.
    for (var round = 0; round < 10; round++) {
      final storage = _InterleavingStorage();
      final scan = SecureDropoffHandoffStore(storage: storage);
      final tripMode = SecureDropoffHandoffStore(storage: storage);
      final engine = SecureDropoffHandoffStore(storage: storage);
      await scan.keep('t', 'd-old', const KeptHandoff(code: 'old'));
      await scan.keep('t', 'd-done', const KeptHandoff(code: 'done'));

      await Future.wait([
        if (round.isEven)
          scan.keep('t', 'd-new', const KeptHandoff(code: 'new')),
        tripMode.forget('t', ['d-old']),
        engine.forget('t', ['d-done']),
        if (round.isOdd)
          scan.keep('t', 'd-new', const KeptHandoff(code: 'new')),
      ]);

      expect((await scan.read('t', 'd-new'))?.code, 'new', reason: '$round');
      expect(await scan.read('t', 'd-old'), isNull, reason: '$round');
      expect(await scan.read('t', 'd-done'), isNull, reason: '$round');
    }
  });

  test('a storage that cannot be listed loses nothing', () async {
    final storage = _MockStorage();
    when(
      () => storage.write(key: any(named: 'key'), value: any(named: 'value')),
    ).thenAnswer((_) async {});
    when(() => storage.readAll()).thenThrow(PlatformException(code: 'store'));
    final store = SecureDropoffHandoffStore(storage: storage);

    await store.keep('t', 'd-1', const KeptHandoff(code: '1'));
    await store.forgetAll();

    // The new entry is written; nothing is deleted on a failed listing.
    verify(
      () => storage.write(
        key: 'dropoff_handoff|t|d-1',
        value: any(named: 'value'),
      ),
    ).called(1);
    verifyNever(() => storage.delete(key: any(named: 'key')));
  });

  test('nothing it holds is logged, only the error type', () async {
    final storage = _MockStorage();
    when(() => storage.read(key: any(named: 'key'))).thenThrow(
      const FormatException('Unexpected character', 'eyJkaWQiOiJzZWNyZXQ'),
    );
    final logs = <String?>[];
    final original = debugPrint;
    debugPrint = (message, {wrapWidth}) => logs.add(message);
    addTearDown(() => debugPrint = original);

    expect(
      await SecureDropoffHandoffStore(storage: storage).read('t', 'd-1'),
      isNull,
    );

    expect(logs, ['[Delivery] kept handoff unreadable: FormatException']);
    expect(logs.join(), isNot(contains('eyJ')));
  });

  test('forget drops the done deliveries only', () async {
    final store = SecureDropoffHandoffStore();
    for (final id in ['d-1', 'd-2', 'd-3']) {
      await store.keep('t', id, KeptHandoff(code: id));
    }

    await store.forget('t', ['d-1', 'd-3', 'd-9']);

    expect(await store.read('t', 'd-1'), isNull);
    expect((await store.read('t', 'd-2'))?.code, 'd-2');
    expect(await store.read('t', 'd-3'), isNull);
  });

  test('forgetAll drops every kept handoff and nothing else', () async {
    FlutterSecureStorage.setMockInitialValues({
      'device_qr_signing_key': 'key',
    });
    final store = SecureDropoffHandoffStore();
    await store.keep('t-1', 'd-1', const KeptHandoff(code: '1'));
    await store.keep('t-2', 'd-2', const KeptHandoff(code: '2'));

    await store.forgetAll();

    expect(await store.read('t-1', 'd-1'), isNull);
    expect(await store.read('t-2', 'd-2'), isNull);
    expect(
      await const FlutterSecureStorage().read(key: 'device_qr_signing_key'),
      'key',
    );
  });

  test('handoffs are needed until the delivery leaves the road for good', () {
    expect(handoffNoLongerNeeded(DeliveryStatus.pickedUp), isFalse);
    expect(handoffNoLongerNeeded(DeliveryStatus.inTransit), isFalse);
    expect(handoffNoLongerNeeded(DeliveryStatus.delivered), isTrue);
    expect(handoffNoLongerNeeded(DeliveryStatus.cancelled), isTrue);
    expect(handoffNoLongerNeeded(DeliveryStatus.disputed), isTrue);
  });
}

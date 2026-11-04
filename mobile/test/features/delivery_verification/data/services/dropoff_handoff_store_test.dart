import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';

class _MockStorage extends Mock implements FlutterSecureStorage {}

/// T-3: the trucker's phone keeps the pickup handoff for the drop-off QR,
/// across restarts, per account and delivery, the newest twenty.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

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

  test('keeps the newest twenty deliveries', () async {
    final store = SecureDropoffHandoffStore();
    for (var i = 0; i < 22; i++) {
      await store.keep('t', 'd-$i', KeptHandoff(code: '$i'));
    }
    // Keeping one again makes it the newest.
    await store.keep('t', 'd-2', const KeptHandoff(code: 'again'));

    expect(await store.read('t', 'd-0'), isNull);
    expect(await store.read('t', 'd-1'), isNull);
    expect((await store.read('t', 'd-2'))?.code, 'again');
    expect((await store.read('t', 'd-3'))?.code, '3');
    expect((await store.read('t', 'd-21'))?.code, '21');

    // One more pushes out the oldest, d-3, not the renewed d-2.
    await store.keep('t', 'd-22', const KeptHandoff(code: '22'));
    expect(await store.read('t', 'd-3'), isNull);
    expect((await store.read('t', 'd-2'))?.code, 'again');
  });

  test('an unreadable entry reads as nothing', () async {
    FlutterSecureStorage.setMockInitialValues({'dropoff_handoffs_t': '{bad'});

    expect(await SecureDropoffHandoffStore().read('t', 'd-1'), isNull);
  });

  test('a storage that cannot be read is not written over', () async {
    // Writing now would replace the other kept handoffs with this one.
    final storage = _MockStorage();
    when(() => storage.read(key: any(named: 'key')))
        .thenThrow(PlatformException(code: 'keystore'));
    final store = SecureDropoffHandoffStore(storage: storage);

    await store.keep('t', 'd-1', const KeptHandoff(code: '1'));
    await store.forget('t', ['d-2']);

    verifyNever(
      () => storage.write(
        key: any(named: 'key'),
        value: any(named: 'value'),
      ),
    );
    verifyNever(() => storage.delete(key: any(named: 'key')));
  });

  test('nothing it holds is logged', () async {
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

    // Only the error's type is logged.
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

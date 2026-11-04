import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';

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
}

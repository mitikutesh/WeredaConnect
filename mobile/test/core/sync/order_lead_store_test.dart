import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/sync/order_lead_store.dart';

/// FUP-122: the order lead the trip pull keeps for the offline matching.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues(<String, Object>{}));

  test('unknown until a pull stored a lead', () async {
    expect(await OrderLeadStore().read(), isNull);
  });

  test('keeps the lead across instances (app restarts)', () async {
    await OrderLeadStore().write(24);

    expect(await OrderLeadStore().read(), 24);
  });

  test('a later value replaces it, and 0 is kept as 0', () async {
    final store = OrderLeadStore();
    await store.write(24);
    await store.write(0);

    expect(await store.read(), 0);
  });

  test('null forgets it (a server that sent none)', () async {
    final store = OrderLeadStore();
    await store.write(24);
    await store.write(null);

    expect(await store.read(), isNull);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.containsKey(OrderLeadStore.key), isFalse);
  });

  test('a failing store reads as unknown and never throws', () async {
    final store = OrderLeadStore(
      preferences: () => Future<SharedPreferences>.error(StateError('no')),
    );

    await store.write(24);
    expect(await store.read(), isNull);
  });

  test('a value of another type reads as unknown', () async {
    SharedPreferences.setMockInitialValues(
      <String, Object>{OrderLeadStore.key: 'twenty-four'},
    );

    expect(await OrderLeadStore().read(), isNull);
  });
}

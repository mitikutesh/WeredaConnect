import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/device_identity.dart';
import 'package:wereda_connect/core/auth/device_name_parser.dart';

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

void main() {
  late MockSecureStorage storage;
  final uuidV4 = RegExp(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$',
  );

  setUp(() {
    storage = MockSecureStorage();
    when(
      () => storage.write(
        key: any(named: 'key'),
        value: any(named: 'value'),
      ),
    ).thenAnswer((_) async {});
  });

  test('creates a UUID v4 once and stores it under device_id', () async {
    when(() => storage.read(key: 'device_id')).thenAnswer((_) async => null);
    final identity = DeviceIdentity(storage: storage);

    final first = await identity.deviceId();
    final second = await identity.deviceId();

    expect(first, matches(uuidV4));
    expect(second, first);
    verify(() => storage.write(key: 'device_id', value: first)).called(1);
  });

  test('reuses the stored id across instances', () async {
    when(() => storage.read(key: 'device_id'))
        .thenAnswer((_) async => 'stored-id');

    final id = await DeviceIdentity(storage: storage).deviceId();

    expect(id, 'stored-id');
    verifyNever(
      () => storage.write(
        key: any(named: 'key'),
        value: any(named: 'value'),
      ),
    );
  });

  test('falls back to an in-memory id when secure storage fails', () async {
    when(() => storage.read(key: 'device_id')).thenThrow(Exception('boom'));

    final id = await DeviceIdentity(storage: storage).deviceId();

    expect(id, matches(uuidV4));
  });

  test('headers carry X-Device-Id and X-Device-Name', () async {
    when(() => storage.read(key: 'device_id')).thenAnswer((_) async => 'id-1');

    final headers =
        await DeviceIdentity(storage: storage, deviceName: 'Android phone')
            .headers();

    expect(headers, {'X-Device-Id': 'id-1', 'X-Device-Name': 'Android phone'});
  });

  test('default device name is a short platform label', () {
    final name = DeviceIdentity(storage: storage).deviceName;
    expect(name, isNotEmpty);
    expect(name.length, lessThan(30));
  });

  group('describeUserAgent', () {
    test('keeps only browser and OS families', () {
      expect(
        describeUserAgent(
          'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
          '(KHTML, like Gecko) Chrome/126.0 Safari/537.36',
        ),
        'Chrome on Windows',
      );
      expect(
        describeUserAgent(
          'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) '
          'AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/604.1',
        ),
        'Safari on iOS',
      );
      expect(
        describeUserAgent(
          'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/126.0 '
          'Safari/537.36 Edg/126.0',
        ),
        'Edge on Windows',
      );
      expect(
        describeUserAgent(
          'Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko Firefox/128.0',
        ),
        'Firefox on Linux',
      );
      expect(describeUserAgent('curl/8.0'), 'Browser');
    });
  });
}

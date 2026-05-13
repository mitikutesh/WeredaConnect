// Platform-pair signature parity test for `field_encryptor_web.dart`.
//
// The web stub is selected by conditional import only on web targets. This
// test imports it directly so any drift between the mobile FieldEncryptor's
// public surface and the web stub's surface fails at test time, not at
// `flutter build web` time. If a future change renames or drops any of
// `collection`/`column`/`rowKey`, this test stops compiling.
//
// The test asserts compile-time signature shape only — no behavioral
// assertions beyond identity (web stub returns input unchanged).

import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/field_encryptor_web.dart';

void main() {
  group('FieldEncryptor (web stub) — signature parity', () {
    final encryptor = FieldEncryptor.instance;

    test('encrypt accepts AAD named params and returns plaintext', () async {
      final out = await encryptor.encrypt(
        'plaintext',
        collection: 'User',
        column: 'displayName',
        rowKey: 'sub-A',
      );
      expect(out, equals('plaintext'));
    });

    test('decrypt accepts AAD named params and returns ciphertext', () async {
      final out = await encryptor.decrypt(
        'ciphertext-bytes',
        collection: 'User',
        column: 'displayName',
        rowKey: 'sub-A',
      );
      expect(out, equals('ciphertext-bytes'));
    });

    test('initialize accepts a key list', () {
      // 32-byte key (matches mobile expectations).
      encryptor.initialize(List<int>.filled(32, 0));
      // Web stub keeps isInitialized false by design.
      expect(encryptor.isInitialized, isFalse);
    });

    test('reset and resetInstance are callable', () {
      encryptor.reset();
      FieldEncryptor.resetInstance();
      // Re-fetching the singleton should produce a new instance after reset.
      final fresh = FieldEncryptor.instance;
      expect(fresh, isA<FieldEncryptor>());
    });
  });
}

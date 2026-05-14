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
import 'package:wereda_connect/core/database/base_field_encryptor.dart';
import 'package:wereda_connect/core/database/field_encryptor_web.dart';

void main() {
  group('FieldEncryptor (web stub) — signature parity', () {
    final encryptor = FieldEncryptor.instance;

    test('web stub is a BaseFieldEncryptor (subtype canary)', () {
      // Pins that repositories typed against BaseFieldEncryptor accept
      // the web stub. If FieldEncryptor stops extending the abstract
      // base, this fails before the build-web step does.
      expect(encryptor, isA<BaseFieldEncryptor>());
    });

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

    test('initialize sets isInitialized true and accepts legacy flag', () {
      // Reset to a known-false state before this test, since other tests
      // in this group (or earlier runs of this one) may have flipped it.
      FieldEncryptor.resetInstance();
      final fresh = FieldEncryptor.instance;
      expect(fresh.isInitialized, isFalse);

      // 32-byte key (matches mobile expectations).
      fresh.initialize(
        List<int>.filled(32, 0),
        legacyPlaintextAllowed: true,
      );
      // Web stub now flips isInitialized to true for parity with mobile.
      expect(fresh.isInitialized, isTrue);
    });

    test('initialize rejects wrong-length key (parity with mobile)', () {
      // The real FieldEncryptor throws on != 32 bytes. The stub validates
      // the same way so a test that passes a malformed key fails on web
      // and on mobile identically — no silent platform divergence.
      expect(
        () => encryptor.initialize(List<int>.filled(16, 0)),
        throwsA(isA<ArgumentError>()),
      );
      expect(
        () => encryptor.initialize(<int>[]),
        throwsA(isA<ArgumentError>()),
      );
    });

    test('reset clears isInitialized', () {
      FieldEncryptor.resetInstance();
      final fresh = FieldEncryptor.instance;
      fresh.initialize(List<int>.filled(32, 0));
      expect(fresh.isInitialized, isTrue);
      fresh.reset();
      expect(fresh.isInitialized, isFalse);
    });

    test('resetInstance produces a fresh singleton', () {
      FieldEncryptor.resetInstance();
      final a = FieldEncryptor.instance;
      a.initialize(List<int>.filled(32, 0));
      FieldEncryptor.resetInstance();
      final b = FieldEncryptor.instance;
      // New instance starts uninitialized.
      expect(identical(a, b), isFalse);
      expect(b.isInitialized, isFalse);
    });
  });
}

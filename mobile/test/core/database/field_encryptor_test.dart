import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/field_encryptor.dart';

void main() {
  group('FieldEncryptor', () {
    late FieldEncryptor encryptor;

    setUp(() {
      FieldEncryptor.resetInstance();
      encryptor = FieldEncryptor.instance;
    });

    tearDown(() {
      FieldEncryptor.resetInstance();
    });

    List<int> generateKey() {
      final random = Random.secure();
      return List<int>.generate(32, (_) => random.nextInt(256));
    }

    test('singleton returns same instance', () {
      final a = FieldEncryptor.instance;
      final b = FieldEncryptor.instance;
      expect(identical(a, b), isTrue);
    });

    test('isInitialized is false before initialize()', () {
      expect(encryptor.isInitialized, isFalse);
    });

    test('isInitialized is true after initialize()', () {
      encryptor.initialize(generateKey());
      expect(encryptor.isInitialized, isTrue);
    });

    test('initialize rejects key with wrong length', () {
      expect(
        () => encryptor.initialize([1, 2, 3]),
        throwsA(isA<ArgumentError>()),
      );
    });

    test('encrypt returns plaintext when not initialized', () async {
      const plaintext = '+251911234567';
      final result = await encryptor.encrypt(plaintext);
      expect(result, equals(plaintext));
    });

    test('decrypt returns ciphertext when not initialized', () async {
      const ciphertext = 'someBase64Data==';
      final result = await encryptor.decrypt(ciphertext);
      expect(result, equals(ciphertext));
    });

    test('encrypt produces different output than plaintext', () async {
      encryptor.initialize(generateKey());
      const plaintext = '+251911234567';
      final encrypted = await encryptor.encrypt(plaintext);
      expect(encrypted, isNot(equals(plaintext)));
    });

    test('decrypt reverses encrypt', () async {
      encryptor.initialize(generateKey());
      const plaintext = '+251911234567';
      final encrypted = await encryptor.encrypt(plaintext);
      final decrypted = await encryptor.decrypt(encrypted);
      expect(decrypted, equals(plaintext));
    });

    test('encrypt/decrypt works with Amharic text', () async {
      encryptor.initialize(generateKey());
      const plaintext = '\u12a0\u1260\u1260 \u12a8\u1260\u12f0'; // Amharic name
      final encrypted = await encryptor.encrypt(plaintext);
      expect(encrypted, isNot(equals(plaintext)));
      final decrypted = await encryptor.decrypt(encrypted);
      expect(decrypted, equals(plaintext));
    });

    test('encrypt/decrypt works with empty string', () async {
      encryptor.initialize(generateKey());
      const plaintext = '';
      final encrypted = await encryptor.encrypt(plaintext);
      final decrypted = await encryptor.decrypt(encrypted);
      expect(decrypted, equals(plaintext));
    });

    test('each encryption produces unique ciphertext (random nonce)', () async {
      encryptor.initialize(generateKey());
      const plaintext = 'test-data';
      final encrypted1 = await encryptor.encrypt(plaintext);
      final encrypted2 = await encryptor.encrypt(plaintext);
      // Same plaintext should produce different ciphertext due to random nonce
      expect(encrypted1, isNot(equals(encrypted2)));
      // But both should decrypt to the same plaintext
      expect(await encryptor.decrypt(encrypted1), equals(plaintext));
      expect(await encryptor.decrypt(encrypted2), equals(plaintext));
    });

    test('decrypt returns non-base64 input as-is (legacy data)', () async {
      encryptor.initialize(generateKey());
      // This is not valid base64, so it should be returned unchanged
      const legacyData = '+251911234567';
      final result = await encryptor.decrypt(legacyData);
      expect(result, equals(legacyData));
    });

    test('decrypt returns short base64 as-is (too short for AES-GCM)',
        () async {
      encryptor.initialize(generateKey());
      // Valid base64 but too short to be AES-GCM (needs 12+0+16 = 28 bytes min)
      const shortData = 'AAAA'; // 3 bytes
      final result = await encryptor.decrypt(shortData);
      expect(result, equals(shortData));
    });

    test('reset clears initialization state', () {
      encryptor.initialize(generateKey());
      expect(encryptor.isInitialized, isTrue);
      encryptor.reset();
      expect(encryptor.isInitialized, isFalse);
    });

    test('resetInstance creates new singleton', () {
      final original = FieldEncryptor.instance;
      FieldEncryptor.resetInstance();
      final newInstance = FieldEncryptor.instance;
      expect(identical(original, newInstance), isFalse);
    });

    test('data encrypted with one key cannot be decrypted with another',
        () async {
      // Encrypt with key 1
      encryptor.initialize(generateKey());
      const plaintext = 'sensitive-pii-data';
      final encrypted = await encryptor.encrypt(plaintext);

      // Try to decrypt with key 2
      FieldEncryptor.resetInstance();
      final encryptor2 = FieldEncryptor.instance;
      encryptor2.initialize(generateKey());

      // Should return ciphertext as-is (MAC check fails, falls back)
      final result = await encryptor2.decrypt(encrypted);
      expect(result, equals(encrypted));
    });
  });
}

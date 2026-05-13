import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/field_encryptor.dart';

void main() {
  group('FieldEncryptor', () {
    late FieldEncryptor encryptor;

    // Representative AAD trio used by tests that don't care about the values.
    // For tests that DO care (cross-row / cross-column swap), they specify
    // their own trio inline.
    const aadCollection = 'TestCollection';
    const aadColumn = 'testField';
    const aadRowKey = 'test-row-1';

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
      final result = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(result, equals(plaintext));
    });

    test('decrypt returns ciphertext when not initialized', () async {
      const ciphertext = 'someBase64Data==';
      final result = await encryptor.decrypt(
        ciphertext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(result, equals(ciphertext));
    });

    test('encrypt produces different output than plaintext', () async {
      encryptor.initialize(generateKey());
      const plaintext = '+251911234567';
      final encrypted = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(encrypted, isNot(equals(plaintext)));
    });

    test('decrypt reverses encrypt with matching AAD', () async {
      encryptor.initialize(generateKey());
      const plaintext = '+251911234567';
      final encrypted = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      final decrypted = await encryptor.decrypt(
        encrypted,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(decrypted, equals(plaintext));
    });

    test('encrypt/decrypt works with Amharic text', () async {
      encryptor.initialize(generateKey());
      const plaintext = 'አበበ ከበደ'; // Amharic name
      final encrypted = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(encrypted, isNot(equals(plaintext)));
      final decrypted = await encryptor.decrypt(
        encrypted,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(decrypted, equals(plaintext));
    });

    test('encrypt/decrypt works with empty string', () async {
      encryptor.initialize(generateKey());
      const plaintext = '';
      final encrypted = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      final decrypted = await encryptor.decrypt(
        encrypted,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(decrypted, equals(plaintext));
    });

    test('each encryption produces unique ciphertext (random nonce)',
        () async {
      encryptor.initialize(generateKey());
      const plaintext = 'test-data';
      final encrypted1 = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      final encrypted2 = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      // Same plaintext + same AAD should still produce different ciphertext
      // due to fresh nonce on every call.
      expect(encrypted1, isNot(equals(encrypted2)));
      // Both should decrypt to the same plaintext under matching AAD.
      expect(
        await encryptor.decrypt(
          encrypted1,
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        equals(plaintext),
      );
      expect(
        await encryptor.decrypt(
          encrypted2,
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        equals(plaintext),
      );
    });

    test('decrypt returns non-base64 input as-is (legacy data)', () async {
      encryptor.initialize(generateKey());
      const legacyData = '+251911234567';
      final result = await encryptor.decrypt(
        legacyData,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(result, equals(legacyData));
    });

    test('decrypt returns short base64 as-is (too short for AES-GCM)',
        () async {
      encryptor.initialize(generateKey());
      const shortData = 'AAAA'; // 3 bytes — below 28-byte GCM minimum
      final result = await encryptor.decrypt(
        shortData,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
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
      encryptor.initialize(generateKey());
      const plaintext = 'sensitive-pii-data';
      final encrypted = await encryptor.encrypt(
        plaintext,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );

      // Try to decrypt with a different key.
      FieldEncryptor.resetInstance();
      final encryptor2 = FieldEncryptor.instance;
      encryptor2.initialize(generateKey());

      // S1-01 keeps the fail-open behavior on MAC failure (changed in S1-02).
      // For now, the call returns the ciphertext as-is.
      final result = await encryptor2.decrypt(
        encrypted,
        collection: aadCollection,
        column: aadColumn,
        rowKey: aadRowKey,
      );
      expect(result, equals(encrypted));
    });

    // ─── AAD binding (S1-01 acceptance) ────────────────────────────────

    group('AAD binding', () {
      test('cross-row swap is rejected: decrypt of row-A blob with row-B AAD '
          'fails MAC and falls back to ciphertext', () async {
        encryptor.initialize(generateKey());
        const plaintext = 'Abebe Kebede';
        final blobRowA = await encryptor.encrypt(
          plaintext,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );

        // Attacker plants blobRowA into User row sub-B's displayName.
        // S1-01 leaves the fail-open behavior in place; the bytes come
        // back as-is because the MAC check fails. S1-02 will throw.
        final result = await encryptor.decrypt(
          blobRowA,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-B',
        );
        expect(result, equals(blobRowA),
            reason: 'MAC check should fail under AAD mismatch — '
                'fail-open returns input unchanged until S1-02');
        expect(result, isNot(equals(plaintext)),
            reason: 'plaintext must NEVER leak under AAD mismatch');
      });

      test('cross-column swap is rejected: phone-column blob fails as a '
          'displayName-column read', () async {
        encryptor.initialize(generateKey());
        const phone = '+251911234567';
        final phoneBlob = await encryptor.encrypt(
          phone,
          collection: 'User',
          column: 'phoneNumber',
          rowKey: 'sub-A',
        );

        final result = await encryptor.decrypt(
          phoneBlob,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(result, equals(phoneBlob),
            reason: 'AAD mismatch → MAC fail → fail-open returns ciphertext');
        expect(result, isNot(equals(phone)),
            reason: 'plaintext must NEVER leak under column-AAD mismatch');
      });

      test('cross-collection swap is rejected: User blob fails as a '
          'CropListing read', () async {
        encryptor.initialize(generateKey());
        const name = 'Abebe Kebede';
        final userBlob = await encryptor.encrypt(
          name,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );

        final result = await encryptor.decrypt(
          userBlob,
          collection: 'CropListing',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(result, equals(userBlob));
        expect(result, isNot(equals(name)));
      });

      test('matching AAD trio decrypts cleanly even after a swap attempt',
          () async {
        encryptor.initialize(generateKey());
        const plaintext = 'Abebe Kebede';
        final blob = await encryptor.encrypt(
          plaintext,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );

        final result = await encryptor.decrypt(
          blob,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(result, equals(plaintext));
      });
    });

    // ─── AAD validation (S1-01 review #1: delimiter collision) ─────────

    group('AAD validation', () {
      setUp(() => encryptor.initialize(generateKey()));

      test('encrypt throws ArgumentError if collection contains ":"',
          () async {
        expect(
          () => encryptor.encrypt('any',
              collection: 'User:Extra',
              column: 'displayName',
              rowKey: 'sub-A'),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError if column contains ":"', () async {
        expect(
          () => encryptor.encrypt('any',
              collection: 'User',
              column: 'display:Name',
              rowKey: 'sub-A'),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError if rowKey contains ":"', () async {
        expect(
          () => encryptor.encrypt('any',
              collection: 'User',
              column: 'displayName',
              rowKey: 'sub:A'),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('decrypt throws ArgumentError on colon in any AAD component',
          () async {
        // Build a valid ciphertext, then try to decrypt under a bad AAD trio.
        final blob = await encryptor.encrypt(
          'plain',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => encryptor.decrypt(
            blob,
            collection: 'User:Tampered',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });
    });
  });
}

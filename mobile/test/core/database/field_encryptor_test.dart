import 'dart:convert';
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

    test('encrypt throws StateError when not initialized', () async {
      // S1-02 fail-closed: previously this returned plaintext.
      expect(
        () => encryptor.encrypt(
          '+251911234567',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<StateError>()),
      );
    });

    test('decrypt throws StateError when not initialized', () async {
      // S1-02 fail-closed: previously this returned the input unchanged.
      expect(
        () => encryptor.decrypt(
          'someBase64Data==',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<StateError>()),
      );
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

    test('each encryption produces unique ciphertext (random nonce)', () async {
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

    test('decrypt throws on unprefixed input (downgrade attack closed)',
        () async {
      // S1-02 fail-closed: previously returned input unchanged.
      encryptor.initialize(generateKey());
      expect(
        () => encryptor.decrypt(
          '+251911234567',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<FieldDecryptionException>()),
      );
    });

    test('decrypt throws on invalid base64 body', () async {
      encryptor.initialize(generateKey());
      expect(
        () => encryptor.decrypt(
          'enc:v1:!!!not-base64!!!',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<FieldDecryptionException>()),
      );
    });

    test('decrypt throws on truncated body (< 28 bytes)', () async {
      encryptor.initialize(generateKey());
      // Valid base64 ('AAAA' = 3 bytes), prefixed, but below GCM minimum.
      expect(
        () => encryptor.decrypt(
          'enc:v1:AAAA',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<FieldDecryptionException>()),
      );
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

      // S1-02 fail-closed: MAC failure now throws instead of returning input.
      expect(
        () => encryptor2.decrypt(
          encrypted,
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        ),
        throwsA(isA<FieldDecryptionException>()),
      );
    });

    // ─── AAD binding (S1-02 fail-closed) ───────────────────────────────

    group('AAD binding', () {
      test('cross-row swap throws FieldDecryptionException', () async {
        encryptor.initialize(generateKey());
        final blobRowA = await encryptor.encrypt(
          'Abebe Kebede',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );

        // Attacker plants blobRowA into User row sub-B's displayName.
        // S1-02 fail-closed: MAC failure under AAD mismatch throws.
        expect(
          () => encryptor.decrypt(
            blobRowA,
            collection: 'User',
            column: 'displayName',
            rowKey: 'sub-B',
          ),
          throwsA(isA<FieldDecryptionException>()),
          reason: 'plaintext must NEVER leak under row-AAD mismatch',
        );
      });

      test('cross-column swap throws FieldDecryptionException', () async {
        encryptor.initialize(generateKey());
        final phoneBlob = await encryptor.encrypt(
          '+251911234567',
          collection: 'User',
          column: 'phoneNumber',
          rowKey: 'sub-A',
        );

        expect(
          () => encryptor.decrypt(
            phoneBlob,
            collection: 'User',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<FieldDecryptionException>()),
          reason: 'plaintext must NEVER leak under column-AAD mismatch',
        );
      });

      test('cross-collection swap throws FieldDecryptionException', () async {
        encryptor.initialize(generateKey());
        final userBlob = await encryptor.encrypt(
          'Abebe Kebede',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );

        expect(
          () => encryptor.decrypt(
            userBlob,
            collection: 'CropListing',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<FieldDecryptionException>()),
        );
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

      test('encrypt throws ArgumentError if collection contains ":"', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: 'User:Extra',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError if column contains ":"', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: 'User',
            column: 'display:Name',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError if rowKey contains ":"', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: 'User',
            column: 'displayName',
            rowKey: 'sub:A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('decrypt throws ArgumentError if collection contains ":"', () async {
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

      test('decrypt throws ArgumentError if column contains ":"', () async {
        final blob = await encryptor.encrypt(
          'plain',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => encryptor.decrypt(
            blob,
            collection: 'User',
            column: 'display:Name',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('decrypt throws ArgumentError if rowKey contains ":"', () async {
        final blob = await encryptor.encrypt(
          'plain',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => encryptor.decrypt(
            blob,
            collection: 'User',
            column: 'displayName',
            rowKey: 'sub:A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError on empty collection', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: '',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError on empty column', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: 'User',
            column: '',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt throws ArgumentError on empty rowKey', () async {
        expect(
          () => encryptor.encrypt(
            'any',
            collection: 'User',
            column: 'displayName',
            rowKey: '',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });
    });

    // ─── Prefix + fail-closed contract (S1-02) ─────────────────────────

    group('cipher prefix + fail-closed', () {
      test('encrypt output starts with enc:v1: prefix', () async {
        encryptor.initialize(generateKey());
        final out = await encryptor.encrypt(
          'hello',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        );
        expect(out, startsWith(FieldEncryptor.cipherPrefix));
        expect(FieldEncryptor.cipherPrefix, equals('enc:v1:'));
      });

      test('decrypt throws on tampered MAC byte', () async {
        encryptor.initialize(generateKey());
        final blob = await encryptor.encrypt(
          'Abebe',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        );
        // Flip the last MAC byte.
        final body = base64Decode(
          blob.substring(FieldEncryptor.cipherPrefix.length),
        );
        body[body.length - 1] ^= 0x01;
        final tampered = '${FieldEncryptor.cipherPrefix}${base64Encode(body)}';
        expect(
          () => encryptor.decrypt(
            tampered,
            collection: aadCollection,
            column: aadColumn,
            rowKey: aadRowKey,
          ),
          throwsA(isA<FieldDecryptionException>()),
        );
      });

      test('FieldDecryptionException toString reveals reason without leaks',
          () async {
        const ex = FieldDecryptionException(
          FieldDecryptionReason.macVerificationFailed,
        );
        expect(ex.toString(), contains('MAC verification failed'));
        // Must not leak rowKey or plaintext content into the exception.
        expect(ex.toString(), isNot(contains('sub-')));
        expect(ex.toString(), isNot(contains('Abebe')));
      });
    });

    // ─── Legacy plaintext mode (HS-3 migration escape hatch, gated) ────

    group('legacyPlaintextAllowed flag', () {
      test('defaults to false: unprefixed input on decrypt throws', () async {
        encryptor.initialize(generateKey());
        expect(
          () => encryptor.decrypt(
            'plaintext-row',
            collection: aadCollection,
            column: aadColumn,
            rowKey: aadRowKey,
          ),
          throwsA(isA<FieldDecryptionException>()),
        );
      });

      test('when true, unprefixed input is returned unchanged', () async {
        encryptor.initialize(generateKey(), legacyPlaintextAllowed: true);
        final out = await encryptor.decrypt(
          'plaintext-row',
          collection: aadCollection,
          column: aadColumn,
          rowKey: aadRowKey,
        );
        expect(out, equals('plaintext-row'));
      });

      test('reset clears the legacy flag', () async {
        encryptor.initialize(generateKey(), legacyPlaintextAllowed: true);
        // Sanity: flag is on.
        expect(
          await encryptor.decrypt(
            'plain',
            collection: aadCollection,
            column: aadColumn,
            rowKey: aadRowKey,
          ),
          equals('plain'),
        );

        encryptor.reset();
        encryptor.initialize(generateKey()); // no flag → default false
        expect(
          () => encryptor.decrypt(
            'plain',
            collection: aadCollection,
            column: aadColumn,
            rowKey: aadRowKey,
          ),
          throwsA(isA<FieldDecryptionException>()),
        );
      });

      test('legacy mode does not weaken AAD validation', () async {
        encryptor.initialize(generateKey(), legacyPlaintextAllowed: true);
        // Empty collection still rejected — _aad is upstream of legacy check.
        expect(
          () => encryptor.decrypt(
            'plain',
            collection: '',
            column: aadColumn,
            rowKey: aadRowKey,
          ),
          throwsA(isA<ArgumentError>()),
        );
      });
    });
  });
}

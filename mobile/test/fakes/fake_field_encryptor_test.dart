import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/base_field_encryptor.dart';
import 'package:wereda_connect/core/database/field_encryptor.dart';

import 'fake_field_encryptor.dart';

/// Tests pin the contract that future repository tests (S2-01, S2-04)
/// will rely on. The fake is drop-in for the real [BaseFieldEncryptor]
/// and exercises the same AAD propagation surface.
void main() {
  group('FakeFieldEncryptor', () {
    late FakeFieldEncryptor fake;

    setUp(() {
      fake = FakeFieldEncryptor();
    });

    test('is a BaseFieldEncryptor (subtype check)', () {
      expect(fake, isA<BaseFieldEncryptor>());
    });

    test('production FieldEncryptor is also a BaseFieldEncryptor', () {
      // Confirms the abstract base is shared with production — repositories
      // typed against BaseFieldEncryptor accept both.
      expect(FieldEncryptor.instance, isA<BaseFieldEncryptor>());
    });

    test('isInitialized flips after initialize()', () {
      expect(fake.isInitialized, isFalse);
      fake.initialize(List<int>.filled(32, 0));
      expect(fake.isInitialized, isTrue);
    });

    test('reset() clears initialized state and legacy flag', () {
      fake.initialize(List<int>.filled(32, 0), legacyPlaintextAllowed: true);
      expect(fake.isInitialized, isTrue);
      expect(fake.legacyFlag, isTrue);

      fake.reset();
      expect(fake.isInitialized, isFalse);
      expect(fake.legacyFlag, isFalse);
    });

    test('legacy flag is recorded for assertion (default false)', () {
      fake.initialize(List<int>.filled(32, 0));
      expect(fake.legacyFlag, isFalse);
    });

    test('legacy flag is recorded for assertion (when true)', () {
      fake.initialize(List<int>.filled(32, 0), legacyPlaintextAllowed: true);
      expect(fake.legacyFlag, isTrue);
    });

    group('encrypt / decrypt', () {
      setUp(() => fake.initialize(List<int>.filled(32, 0)));

      test('encrypt emits the AAD trio in the output (substring assertion)',
          () async {
        final out = await fake.encrypt(
          'plaintext',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(out, equals('fake:User:displayName:sub-A:plaintext'));
        // Repository tests can grep for this prefix to confirm AAD threading.
        expect(out, startsWith('fake:User:displayName:sub-A:'));
      });

      test('round-trip with matching AAD recovers plaintext', () async {
        final blob = await fake.encrypt(
          'Abebe Kebede',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        final back = await fake.decrypt(
          blob,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(back, equals('Abebe Kebede'));
      });

      test('decrypt throws on AAD mismatch (collection)', () async {
        final blob = await fake.encrypt(
          'p',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => fake.decrypt(
            blob,
            collection: 'CropListing',
            column: 'displayName',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('decrypt throws on AAD mismatch (column)', () async {
        final blob = await fake.encrypt(
          'p',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => fake.decrypt(
            blob,
            collection: 'User',
            column: 'phoneNumber',
            rowKey: 'sub-A',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('decrypt throws on AAD mismatch (rowKey)', () async {
        final blob = await fake.encrypt(
          'p',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(
          () => fake.decrypt(
            blob,
            collection: 'User',
            column: 'displayName',
            rowKey: 'sub-B',
          ),
          throwsA(isA<ArgumentError>()),
        );
      });

      test('encrypt is deterministic (no nonce)', () async {
        // Unlike the real FieldEncryptor (fresh nonce per call), the fake
        // produces identical output for identical inputs. Useful for tests
        // that want to compare stored values without re-decrypting.
        final a = await fake.encrypt(
          'p',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        final b = await fake.encrypt(
          'p',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(a, equals(b));
      });

      test('encrypt handles empty plaintext', () async {
        final out = await fake.encrypt(
          '',
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(out, equals('fake:User:displayName:sub-A:'));
        final back = await fake.decrypt(
          out,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(back, equals(''));
      });

      test('encrypt preserves unicode (Amharic)', () async {
        const pt = 'አበበ ከበደ';
        final blob = await fake.encrypt(
          pt,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        final back = await fake.decrypt(
          blob,
          collection: 'User',
          column: 'displayName',
          rowKey: 'sub-A',
        );
        expect(back, equals(pt));
      });
    });
  });
}

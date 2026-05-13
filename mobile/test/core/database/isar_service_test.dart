import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/isar_service.dart';

/// Mocked flutter_secure_storage method channel for orchestration tests.
/// The plugin's channel name and method set are stable across v9.x.
const _secureStorageChannel =
    MethodChannel('plugins.it_nomads.com/flutter_secure_storage');

/// Set up the mock channel with a per-test in-memory store. Call from
/// `setUp` in the relevant test group.
void _installSecureStorageMock(Map<String, String> store) {
  TestDefaultBinaryMessengerBinding.instance!.defaultBinaryMessenger
      .setMockMethodCallHandler(_secureStorageChannel, (call) async {
    final args = call.arguments as Map?;
    final key = args?['key'] as String?;
    switch (call.method) {
      case 'read':
        return store[key];
      case 'write':
        store[key!] = args!['value'] as String;
        return null;
      case 'delete':
        store.remove(key);
        return null;
      case 'readAll':
        return Map<String, String>.from(store);
      case 'deleteAll':
        store.clear();
        return null;
      default:
        return null;
    }
  });
}

void main() {
  // Required for any flutter_test that touches a method channel.
  TestWidgetsFlutterBinding.ensureInitialized();

  group('IsarService — key-integrity primitives (S1-03)', () {
    // The S1-03 implementation exposes the two pure helpers
    // (`computeTagForTesting`, `constantTimeEqForTesting`) so they can be
    // unit-tested without mocking secure storage or opening Isar.
    //
    // The orchestration of _getEncryptionKey (legacy-upgrade, corrupt-
    // recovery, tag-mismatch) is exercised by mocking flutter_secure_storage
    // via the platform method channel. End-to-end "tamper-then-launch →
    // wipe" on real devices is covered by integration tests (tracked
    // alongside S5-04's manual smoke test on a Tecno Spark 8 device).

    group('_computeTag (HMAC-SHA256(key, label))', () {
      test('produces a 32-byte output (SHA-256 digest length)', () {
        final key = List<int>.generate(32, (i) => i);
        final tag = IsarService.computeTagForTesting(key);
        expect(tag.length, equals(32));
      });

      test('is deterministic for the same key', () {
        final key = List<int>.generate(32, (i) => i);
        final t1 = IsarService.computeTagForTesting(key);
        final t2 = IsarService.computeTagForTesting(key);
        expect(t1, equals(t2));
      });

      test('differs across different keys', () {
        final keyA = List<int>.generate(32, (i) => i);
        final keyB = List<int>.generate(32, (i) => i ^ 0xFF);
        expect(
          IsarService.computeTagForTesting(keyA),
          isNot(equals(IsarService.computeTagForTesting(keyB))),
        );
      });

      test('matches an independent HMAC-SHA256 computation', () {
        // Confirms the algorithm/parameters match the public spec.
        final key = List<int>.generate(32, (i) => i + 1);
        const label = 'wereda-isar-key-v1';
        final expected =
            Hmac(sha256, key).convert(utf8.encode(label)).bytes;
        expect(IsarService.computeTagForTesting(key), equals(expected));
      });

      test('exhibits avalanche on a one-bit input flip', () {
        // Cryptographic-strength avalanche — a one-bit input difference
        // produces a wildly different output. Tightened lower bound from
        // 10 to 16 (half the digest); HMAC-SHA256 routinely produces
        // ~50% bit-flip rates, so 16/32 is still a loose check that won't
        // false-flag on healthy implementations.
        final keyA = List<int>.generate(32, (i) => i);
        final keyB = List<int>.from(keyA)..[0] ^= 0x01;
        final tagA = IsarService.computeTagForTesting(keyA);
        final tagB = IsarService.computeTagForTesting(keyB);
        var diff = 0;
        for (var i = 0; i < tagA.length; i++) {
          if (tagA[i] != tagB[i]) diff++;
        }
        expect(diff, greaterThanOrEqualTo(16));
      });
    });

    group('_constantTimeEq', () {
      test('returns true on equal-length, equal-content lists', () {
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2], [0, 1, 2]),
          isTrue,
        );
      });

      test('returns false if a MIDDLE byte differs', () {
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2, 3], [0, 9, 2, 3]),
          isFalse,
        );
      });

      test('returns false if the FIRST byte differs', () {
        // Catches the common "early-return-on-first-mismatch" bug.
        expect(
          IsarService.constantTimeEqForTesting([1, 1, 2], [0, 1, 2]),
          isFalse,
        );
      });

      test('returns false if the LAST byte differs', () {
        // Catches an off-by-one in the loop bound.
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2, 3], [0, 1, 2, 4]),
          isFalse,
        );
      });

      test('returns false on length mismatch (without leaking via length only)',
          () {
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2], [0, 1]),
          isFalse,
        );
        expect(
          IsarService.constantTimeEqForTesting([0, 1], [0, 1, 2]),
          isFalse,
        );
      });

      test('returns true on two empty lists', () {
        expect(IsarService.constantTimeEqForTesting([], []), isTrue);
      });

      test('returns true on equal 32-byte HMAC-shaped inputs', () {
        final a = List<int>.generate(32, (i) => i * 3 % 256);
        final b = List<int>.generate(32, (i) => i * 3 % 256);
        expect(IsarService.constantTimeEqForTesting(a, b), isTrue);
      });
    });

    group('KeyIntegrityFailure', () {
      test('toString format is exactly "KeyIntegrityFailure: <reason>"', () {
        // Locks the contract: the exception string carries the reason
        // literal and nothing else. Any future maintainer interpolating
        // bytes / IDs / PII into toString() will break this test.
        const ex = KeyIntegrityFailure('HMAC tag mismatch on key read');
        expect(
          ex.toString(),
          equals('KeyIntegrityFailure: HMAC tag mismatch on key read'),
        );
      });

      test('toString rejects 32-byte hex sequences', () {
        // Property check: a 64-hex-char substring would indicate a raw
        // tag or key leaked into the exception. Reason strings are all
        // fixed literals in this PR; this test guards future contributors.
        const ex = KeyIntegrityFailure('HMAC tag mismatch on key read');
        expect(
          RegExp(r'[0-9a-fA-F]{64}').hasMatch(ex.toString()),
          isFalse,
        );
      });
    });
  });

  group('_getEncryptionKey orchestration (S1-03 — review I-1/I-2/I-3)', () {
    // Each test installs a fresh per-test mock of flutter_secure_storage.
    late Map<String, String> store;
    final keyName = IsarService.keyStorageNameForTesting;
    final tagName = IsarService.tagStorageNameForTesting;

    setUp(() {
      store = {};
      _installSecureStorageMock(store);
    });

    tearDown(() {
      TestDefaultBinaryMessengerBinding.instance!.defaultBinaryMessenger
          .setMockMethodCallHandler(_secureStorageChannel, null);
    });

    test('I-1: matching tag returns the persisted key (no throw)', () async {
      // Seed a valid key + a matching tag.
      final key = List<int>.generate(32, (i) => i + 1);
      final tag = IsarService.computeTagForTesting(key);
      store[keyName] = base64Encode(key);
      store[tagName] = base64Encode(tag);

      final result = await IsarService.instance.getEncryptionKeyForTesting();
      expect(result, equals(key));
      // Tag entry unchanged after a successful read.
      expect(store[tagName], equals(base64Encode(tag)));
    });

    test('I-1: mismatched tag throws KeyIntegrityFailure', () async {
      // Seed a valid key but a wrong tag (tag for a DIFFERENT key).
      final key = List<int>.generate(32, (i) => i + 1);
      final wrongKey = List<int>.generate(32, (i) => i + 99);
      final wrongTag = IsarService.computeTagForTesting(wrongKey);
      store[keyName] = base64Encode(key);
      store[tagName] = base64Encode(wrongTag);

      await expectLater(
        IsarService.instance.getEncryptionKeyForTesting(),
        throwsA(isA<KeyIntegrityFailure>()),
      );
    });

    test('I-2: legacy install (key present, tag absent) writes a tag and '
        'returns the existing key', () async {
      // Pre-S1-03 shape: key entry only, no tag.
      final key = List<int>.generate(32, (i) => i * 2 % 256);
      store[keyName] = base64Encode(key);
      expect(store.containsKey(tagName), isFalse);

      final result = await IsarService.instance.getEncryptionKeyForTesting();
      // Returns the same key bytes — Policy A trusts the existing key.
      expect(result, equals(key));
      // A tag was minted that matches the existing key.
      expect(store[tagName], isNotNull);
      final writtenTag = base64Decode(store[tagName]!);
      expect(writtenTag, equals(IsarService.computeTagForTesting(key)));
    });

    test('I-3: corrupt-base64 key wipes and regenerates (NOT '
        'KeyIntegrityFailure)', () async {
      store[keyName] = '!!!not-base64!!!';
      store[tagName] = base64Encode(List<int>.filled(32, 0));

      // Must complete normally with a freshly generated key.
      final result = await IsarService.instance.getEncryptionKeyForTesting();
      expect(result.length, equals(32));
      // The corrupt entries are gone; the new entries reflect the new key.
      expect(store[keyName], equals(base64Encode(result)));
      expect(
        base64Decode(store[tagName]!),
        equals(IsarService.computeTagForTesting(result)),
      );
    });

    test('I-3: wrong-length key wipes and regenerates (NOT '
        'KeyIntegrityFailure)', () async {
      // 16-byte key (valid base64, but wrong length for AES-256).
      store[keyName] = base64Encode(List<int>.filled(16, 1));
      store[tagName] = base64Encode(List<int>.filled(32, 0));

      final result = await IsarService.instance.getEncryptionKeyForTesting();
      expect(result.length, equals(32));
      expect(store[keyName], equals(base64Encode(result)));
    });

    test('corrupt-base64 tag wipes and regenerates (NOT KeyIntegrityFailure)',
        () async {
      // Symmetric with corrupt-key handling (S-6 from the review).
      final key = List<int>.generate(32, (i) => i);
      store[keyName] = base64Encode(key);
      store[tagName] = '!!!not-base64!!!';

      final result = await IsarService.instance.getEncryptionKeyForTesting();
      expect(result.length, equals(32));
      // New entries reflect a fresh key, not the original.
      expect(result, isNot(equals(key)));
      expect(store[keyName], equals(base64Encode(result)));
    });

    test('fresh install (no entries) generates key + tag', () async {
      expect(store, isEmpty);
      final result = await IsarService.instance.getEncryptionKeyForTesting();
      expect(result.length, equals(32));
      expect(store[keyName], equals(base64Encode(result)));
      expect(
        base64Decode(store[tagName]!),
        equals(IsarService.computeTagForTesting(result)),
      );
    });
  });
}

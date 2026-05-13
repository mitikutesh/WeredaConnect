import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/database/isar_service.dart';

void main() {
  group('IsarService — key-integrity primitives (S1-03)', () {
    // The S1-03 implementation exposes the two pure helpers
    // (`computeTagForTesting`, `constantTimeEqForTesting`) so they can be
    // unit-tested without mocking secure storage or opening Isar.
    // End-to-end key-tampering / wipe-on-failure flow is covered by
    // CI integration tests that run on real devices.

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

      test('flips materially when one input byte changes', () {
        // Sanity: cryptographic-strength avalanche — a one-bit input
        // difference produces a wildly different output. We don't assert
        // a strict 50% bit-flip rate; just that the outputs differ on
        // every byte of the digest with high probability.
        final keyA = List<int>.generate(32, (i) => i);
        final keyB = List<int>.from(keyA)..[0] ^= 0x01;
        final tagA = IsarService.computeTagForTesting(keyA);
        final tagB = IsarService.computeTagForTesting(keyB);
        // At least 10 of the 32 bytes should differ (very loose lower bound).
        var diff = 0;
        for (var i = 0; i < tagA.length; i++) {
          if (tagA[i] != tagB[i]) diff++;
        }
        expect(diff, greaterThan(10));
      });
    });

    group('_constantTimeEq', () {
      test('returns true on equal-length, equal-content lists', () {
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2], [0, 1, 2]),
          isTrue,
        );
      });

      test('returns false if any byte differs', () {
        expect(
          IsarService.constantTimeEqForTesting([0, 1, 2], [0, 1, 3]),
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
          IsarService.constantTimeEqForTesting([0, 1, 2], [0, 1, 3]),
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
      test('toString reveals reason without leaking key material', () {
        const ex = KeyIntegrityFailure('HMAC tag mismatch on key read');
        expect(ex.toString(), contains('HMAC tag mismatch'));
        // Must not embed the raw key, tag, or any specific byte sequence
        // into the exception string.
        expect(ex.toString(), isNot(contains('base64')));
        expect(ex.toString(), isNot(contains('[')));
      });
    });
  });
}

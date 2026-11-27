import 'dart:convert';

import 'package:crypto/crypto.dart';

/// The typed fallback for a QR handoff (usability audit T-1,
/// `delivery-verification-api.md` 1.22.0): nine digits a person types when
/// the camera or the screen fails. Digits only, so any keyboard can type it,
/// the Ethiopic ones included.
///
/// The first eight digits are derived from the delivery's QR nonce and the
/// delivery id, the ninth is a Damm check digit: the phone catches every
/// single wrong digit and every swap of two neighbours before anything is
/// sent. The server derives the same code from the nonce it stores (the
/// server `HandoffCode`, byte for byte) and is the one that accepts it.
///
/// Derivation: HMAC-SHA256 with the UTF-8 nonce as key over
/// `wc-handoff-code:v1:{deliveryId}`, the first 8 bytes read big-endian
/// modulo 10^8, left-padded to 8 digits, then the Damm digit. Shared code:
/// the arithmetic stays below 2^53, so web computes the same digits.
abstract final class HandoffCode {
  /// Digits in a code, the check digit included.
  static const length = 9;

  static const _context = 'wc-handoff-code:v1:';

  /// The Damm quasigroup of order 10.
  static const _damm = [
    [0, 3, 1, 7, 5, 9, 8, 6, 4, 2],
    [7, 0, 9, 2, 1, 5, 4, 8, 6, 3],
    [4, 2, 0, 6, 8, 7, 1, 3, 5, 9],
    [1, 7, 5, 0, 9, 8, 3, 4, 2, 6],
    [6, 1, 2, 3, 0, 4, 5, 9, 7, 8],
    [3, 6, 7, 4, 2, 0, 9, 5, 8, 1],
    [5, 8, 6, 9, 7, 2, 0, 1, 3, 4],
    [8, 9, 4, 5, 3, 6, 2, 0, 1, 7],
    [9, 4, 3, 8, 6, 1, 7, 2, 0, 5],
    [2, 5, 8, 1, 4, 3, 6, 7, 9, 0],
  ];

  /// The nine-digit code of [nonce] for [deliveryId].
  static String derive({required String nonce, required String deliveryId}) {
    final digest = Hmac(sha256, utf8.encode(nonce))
        .convert(utf8.encode('$_context$deliveryId'))
        .bytes;
    var value = 0;
    for (var i = 0; i < 8; i++) {
      value = (value * 256 + digest[i]) % 100000000;
    }
    final body = value.toString().padLeft(8, '0');
    return '$body${checkDigit(body)}';
  }

  /// The Damm check digit of a string of ASCII digits.
  static int checkDigit(String digits) {
    var interim = 0;
    for (final unit in digits.codeUnits) {
      interim = _damm[interim][unit - 0x30];
    }
    return interim;
  }

  /// Nine ASCII digits whose check digit is right.
  static bool isValid(String code) =>
      code.length == length &&
      code.codeUnits.every((u) => u >= 0x30 && u <= 0x39) &&
      checkDigit(code) == 0;

  /// What a person typed, as ASCII digits: spaces and dashes dropped, and
  /// Arabic-Indic or Persian digits (some keyboards send them) mapped.
  static String normalize(String input) {
    final out = StringBuffer();
    for (final rune in input.runes) {
      if (rune >= 0x30 && rune <= 0x39) {
        out.writeCharCode(rune);
      } else if (rune >= 0x0660 && rune <= 0x0669) {
        out.writeCharCode(0x30 + rune - 0x0660);
      } else if (rune >= 0x06F0 && rune <= 0x06F9) {
        out.writeCharCode(0x30 + rune - 0x06F0);
      }
    }
    return out.toString();
  }

  /// "123 456 789", easier to copy from a screen.
  static String format(String code) => code.length != length
      ? code
      : '${code.substring(0, 3)} ${code.substring(3, 6)} '
          '${code.substring(6)}';
}

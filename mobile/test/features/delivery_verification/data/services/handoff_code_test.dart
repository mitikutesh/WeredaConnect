import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';

/// T-1: the typed handoff code. The vectors are the server's
/// (server/tests/Services/HandoffCodeTests.cs): both sides derive the same
/// nine digits from the same nonce.
void main() {
  group('derive', () {
    const vectors = [
      ('farmer-nonce-1', 'del-fup42', '757555978'),
      ('nonce-abc-123', 'delivery-001-abcd-efgh', '340404994'),
      (
        'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
        'bad5e4d5-37bc-4bd1-a216-1301cf9781f1',
        '649509129',
      ),
    ];

    for (final (nonce, deliveryId, expected) in vectors) {
      test('matches the server for $deliveryId', () {
        expect(
          HandoffCode.derive(nonce: nonce, deliveryId: deliveryId),
          expected,
        );
      });
    }

    test('depends on the nonce and the delivery, and passes its own check', () {
      final code = HandoffCode.derive(nonce: 'n-1', deliveryId: 'd-1');
      expect(code, matches(RegExp(r'^[0-9]{9}$')));
      expect(HandoffCode.isValid(code), isTrue);
      expect(HandoffCode.derive(nonce: 'n-2', deliveryId: 'd-1'), isNot(code));
      expect(HandoffCode.derive(nonce: 'n-1', deliveryId: 'd-2'), isNot(code));
    });
  });

  group('Damm check digit', () {
    test('the textbook example: 572 has 4', () {
      expect(HandoffCode.checkDigit('572'), 4);
      expect(HandoffCode.checkDigit('5724'), 0);
    });

    test('catches every single wrong digit', () {
      const code = '757555978';
      for (var i = 0; i < code.length; i++) {
        for (var d = 0; d <= 9; d++) {
          if ('$d' == code[i]) continue;
          final typo = code.replaceRange(i, i + 1, '$d');
          expect(HandoffCode.isValid(typo), isFalse, reason: typo);
        }
      }
    });

    test('catches every swap of two neighbours', () {
      const code = '340404994';
      for (var i = 0; i < code.length - 1; i++) {
        if (code[i] == code[i + 1]) continue;
        final swapped = code.replaceRange(i, i + 2, '${code[i + 1]}${code[i]}');
        expect(HandoffCode.isValid(swapped), isFalse, reason: swapped);
      }
    });

    test('refuses anything but nine ASCII digits', () {
      for (final bad in ['', '75755597', '7575559780', '75755597a', '٧٥٧']) {
        expect(HandoffCode.isValid(bad), isFalse, reason: bad);
      }
    });
  });

  test('normalize keeps the digits a keyboard sent, mapped to ASCII', () {
    expect(HandoffCode.normalize('757 555-978'), '757555978');
    // Arabic-Indic and Persian digits.
    expect(HandoffCode.normalize('٧٥٧۵۵۵978'), '757555978');
    // Ethiopic numerals are not positional digits: dropped.
    expect(HandoffCode.normalize('፩፪3'), '3');
  });

  test('format groups the nine digits by three', () {
    expect(HandoffCode.format('757555978'), '757 555 978');
    expect(HandoffCode.format('123'), '123');
  });
}

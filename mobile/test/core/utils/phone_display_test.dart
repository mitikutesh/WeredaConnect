import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/phone_utils.dart';

/// Register OTP board: "We sent a 6-digit code by SMS to +251 911 234 567."
void main() {
  const nbsp = ' ';

  test('groups an Ethiopian number in any accepted format', () {
    const grouped = '+251${nbsp}911${nbsp}234${nbsp}567';
    expect(groupPhoneForDisplay('+251911234567'), grouped);
    expect(groupPhoneForDisplay('0911234567'), grouped);
    expect(groupPhoneForDisplay('00251911234567'), grouped);
  });

  test('leaves anything else as it is', () {
    expect(groupPhoneForDisplay('12345'), '12345');
    expect(groupPhoneForDisplay(''), '');
  });

  // Welcome and Register personal boards: "0911 234 567" in the field.
  group('typing a number', () {
    final formatter = EthiopianPhoneInputFormatter();
    TextEditingValue type(String text, {int? cursor}) =>
        formatter.formatEditUpdate(
          TextEditingValue.empty,
          TextEditingValue(
            text: text,
            selection: TextSelection.collapsed(offset: cursor ?? text.length),
          ),
        );

    test('groups a local, an E.164 and a 00 number', () {
      expect(type('0911234567').text, '0911 234 567');
      expect(type('+251911234567').text, '+251 911 234 567');
      expect(type('00251911234567').text, '00251 911 234 567');
    });

    test('a partial number is grouped as far as it goes', () {
      expect(type('0911').text, '0911');
      expect(type('09112').text, '0911 2');
      expect(type('+2519').text, '+251 9');
    });

    test('keeps only a leading plus and digits', () {
      expect(type('09-11 23a4').text, '0911 234');
      expect(type('0+911').text, '0911');
    });

    test('the cursor stays after the digit it followed', () {
      // Typed "5" after "09112" in "0911 2|34": the cursor follows the 5.
      final v = type('091125 34', cursor: 6);
      expect(v.text, '0911 253 4');
      expect(v.selection.baseOffset, 7);
    });

    test('the spaces do not reach the server', () {
      expect(normalizeEthiopianPhone(type('0911234567').text), '+251911234567');
    });
  });
}

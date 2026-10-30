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
}

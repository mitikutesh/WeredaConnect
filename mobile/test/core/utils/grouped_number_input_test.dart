import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/grouped_number_input.dart';

/// Place order board: the typed quantity reads "1,500" kg.
void main() {
  final formatter = GroupedNumberInputFormatter();
  TextEditingValue type(String text, {int? cursor}) =>
      formatter.formatEditUpdate(
        TextEditingValue.empty,
        TextEditingValue(
          text: text,
          selection: TextSelection.collapsed(offset: cursor ?? text.length),
        ),
      );

  test('groups thousands as it is typed', () {
    expect(type('1500').text, '1,500');
    expect(type('1234567').text, '1,234,567');
    expect(type('150').text, '150');
  });

  test('keeps up to two decimals and drops anything else', () {
    expect(type('1500.257').text, '1,500.25');
    expect(type('1a5b0c0').text, '1,500');
    expect(type('12.3.4').text, '12.34');
  });

  test('a cleared field stays empty', () {
    expect(type('').text, '');
  });

  test('the cursor stays after the digit it followed', () {
    // Typed "9" after "15" in "15|00".
    final v = type('15900', cursor: 3);
    expect(v.text, '15,900');
    expect(v.selection.baseOffset, 4);
  });

  test('the value reads back without the commas', () {
    expect(parseGroupedNumber('1,500.5'), 1500.5);
    expect(parseGroupedNumber(''), isNull);
  });
}

import 'package:flutter/services.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';

/// Groups thousands while a quantity is typed ("1,500"), as the boards
/// print it, with up to [decimals] decimals after a dot. Keeps the cursor
/// after the same digit. Read the value back with [parseGroupedNumber].
class GroupedNumberInputFormatter extends TextInputFormatter {
  GroupedNumberInputFormatter({this.decimals = 2});

  final int decimals;

  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) {
    final text = newValue.text;
    final cursor = newValue.selection.baseOffset.clamp(0, text.length);
    final whole = StringBuffer();
    final fraction = StringBuffer();
    var dot = false;
    var kept = 0;
    var keptBeforeCursor = 0;
    for (var i = 0; i < text.length; i++) {
      final c = text[i];
      final isDigit = c.codeUnitAt(0) >= 0x30 && c.codeUnitAt(0) <= 0x39;
      if (c == '.' && !dot && decimals > 0) {
        dot = true;
      } else if (isDigit && !dot) {
        whole.write(c);
      } else if (isDigit && dot && fraction.length < decimals) {
        fraction.write(c);
      } else {
        continue;
      }
      kept++;
      if (i < cursor) keptBeforeCursor = kept;
    }
    final digits = whole.toString().replaceFirst(RegExp(r'^0+(?=\d)'), '');
    final grouped = digits.isEmpty ? '' : wholeNumber(int.parse(digits));
    final result = dot ? '$grouped.$fraction' : grouped;
    // Leading zeros dropped above are not in [result]; walk it to the same
    // count of kept characters (digits and the dot), skipping commas.
    final dropped = whole.length - digits.length;
    var seen = 0;
    var offset = 0;
    final target = (keptBeforeCursor - dropped).clamp(0, kept);
    while (offset < result.length && seen < target) {
      if (result[offset] != ',') seen++;
      offset++;
    }
    return TextEditingValue(
      text: result,
      selection: TextSelection.collapsed(offset: offset),
    );
  }
}

/// The value of a field grouped by [GroupedNumberInputFormatter] ("1,500.5"
/// is 1500.5); null when it holds no number.
double? parseGroupedNumber(String text) =>
    double.tryParse(text.replaceAll(',', ''));

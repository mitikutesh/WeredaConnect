import 'package:flutter/services.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';

/// Groups thousands while a quantity is typed ("1,500"), as the boards
/// print it, with up to [decimals] decimals after a dot. Keeps the cursor
/// after the same digit. Read the value back with [parseGroupedNumber].
class GroupedNumberInputFormatter extends TextInputFormatter {
  GroupedNumberInputFormatter({this.decimals = 2});

  final int decimals;

  /// Whole digits kept: a 64-bit int and the web's 2^53 both hold 15.
  static const int maxWholeDigits = 15;

  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) {
    newValue = deleteAcrossSeparator(oldValue, newValue, ',');
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
      } else if (isDigit && !dot && whole.length < maxWholeDigits) {
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

/// [value] the way the field shows it: grouped thousands and at most two
/// decimals, none when whole ("1,500", "255.5"). Never rounds a fraction
/// up to the next whole number, so a quantity capped at the stock stays
/// within it.
String formatGroupedNumber(num value) => _grouped.format(value);

final NumberFormat _grouped = NumberFormat('#,##0.##', 'en_US');

/// The value of a field grouped by [GroupedNumberInputFormatter] ("1,500.5"
/// is 1500.5); null when it holds no number.
double? parseGroupedNumber(String text) =>
    double.tryParse(text.replaceAll(',', ''));

/// A backspace that removed only a separator the formatter put in ("0911 |"
/// to "0911|") would be undone by regrouping, so it removes the character
/// before the separator instead, as the user meant.
TextEditingValue deleteAcrossSeparator(
  TextEditingValue oldValue,
  TextEditingValue newValue,
  String separator,
) {
  final old = oldValue.text;
  final cursor = newValue.selection.baseOffset;
  final removedOne = newValue.text.length == old.length - 1 &&
      newValue.selection.isCollapsed &&
      cursor >= 0 &&
      cursor < old.length &&
      old[cursor] == separator &&
      newValue.text == old.substring(0, cursor) + old.substring(cursor + 1);
  if (!removedOne || cursor == 0) return newValue;
  final text =
      newValue.text.substring(0, cursor - 1) + newValue.text.substring(cursor);
  return TextEditingValue(
    text: text,
    selection: TextSelection.collapsed(offset: cursor - 1),
  );
}

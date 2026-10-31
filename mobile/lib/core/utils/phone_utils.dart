/// Ethiopian phone number normalization and validation utilities.
///
/// Accepts three input formats:
/// - `+251XXXXXXXXX` (international E.164)
/// - `00251XXXXXXXXX` (international with 00 prefix)
/// - `0XXXXXXXXX` (local Ethiopian format)
///
/// All formats normalize to canonical E.164: `+251XXXXXXXXX`.
library;

import 'package:flutter/services.dart';

final _e164Regex = RegExp(r'^\+251\d{9}$');
final _digit = RegExp(r'\d');

/// Normalize an Ethiopian phone number to E.164 format (`+251XXXXXXXXX`).
///
/// Returns the normalized phone string, or `null` if the input
/// cannot be recognized as a valid Ethiopian phone number.
String? normalizeEthiopianPhone(String? input) {
  if (input == null || input.isEmpty) return null;

  final cleaned = input.trim().replaceAll(RegExp(r'[\s\-]'), '');

  // Format 1: +251XXXXXXXXX (already E.164)
  if (cleaned.startsWith('+251') && cleaned.length == 13) {
    return _e164Regex.hasMatch(cleaned) ? cleaned : null;
  }

  // Format 2: 00251XXXXXXXXX
  if (cleaned.startsWith('00251') && cleaned.length == 14) {
    final normalized = '+251${cleaned.substring(5)}';
    return _e164Regex.hasMatch(normalized) ? normalized : null;
  }

  // Format 3: 0XXXXXXXXX (local)
  if (cleaned.startsWith('0') && cleaned.length == 10) {
    final normalized = '+251${cleaned.substring(1)}';
    return _e164Regex.hasMatch(normalized) ? normalized : null;
  }

  return null;
}

/// Validate whether a raw phone input is a recognizable Ethiopian phone number.
///
/// This accepts all three formats without normalizing.
bool isValidEthiopianPhone(String? input) {
  return normalizeEthiopianPhone(input) != null;
}

/// An Ethiopian number the way the boards print it: "+251 911 234 567",
/// with no-break spaces so a wrap never splits it. Anything that is not an
/// Ethiopian number is returned unchanged.
String groupPhoneForDisplay(String input) {
  final e164 = normalizeEthiopianPhone(input);
  if (e164 == null) return input;
  const space = ' ';
  return '+251$space${e164.substring(4, 7)}$space'
      '${e164.substring(7, 10)}$space${e164.substring(10)}';
}

/// Groups an Ethiopian number while it is typed, as the boards show it:
/// "0911 234 567", "+251 911 234 567" or "00251 911 234 567". Keeps only a
/// leading "+" and digits, and the cursor after the same digit it followed.
/// [normalizeEthiopianPhone] drops the spaces again before anything is sent.
class EthiopianPhoneInputFormatter extends TextInputFormatter {
  @override
  TextEditingValue formatEditUpdate(
    TextEditingValue oldValue,
    TextEditingValue newValue,
  ) {
    final text = newValue.text;
    final cursor = newValue.selection.baseOffset.clamp(0, text.length);
    final raw = StringBuffer();
    var rawCursor = 0;
    for (var i = 0; i < text.length; i++) {
      final c = text[i];
      final keep = (c == '+' && raw.isEmpty) || _digit.hasMatch(c);
      if (!keep) continue;
      raw.write(c);
      if (i < cursor) rawCursor = raw.length;
    }
    final grouped = groupPhoneDigits(raw.toString());
    // Walk the grouped text until as many kept characters as before the
    // cursor have passed.
    var seen = 0;
    var offset = 0;
    while (offset < grouped.length && seen < rawCursor) {
      if (grouped[offset] != ' ') seen++;
      offset++;
    }
    return TextEditingValue(
      text: grouped,
      selection: TextSelection.collapsed(offset: offset),
    );
  }
}

/// "0911234567" as "0911 234 567", "+251911234567" as "+251 911 234 567";
/// a partial number is grouped as far as it goes.
String groupPhoneDigits(String raw) {
  final String head;
  final String rest;
  if (raw.startsWith('+251')) {
    head = '+251';
    rest = raw.substring(4);
  } else if (raw.startsWith('00251')) {
    head = '00251';
    rest = raw.substring(5);
  } else if (raw.startsWith('0')) {
    if (raw.length <= 4) return raw;
    head = raw.substring(0, 4);
    rest = raw.substring(4);
  } else {
    return raw;
  }
  final groups = <String>[head];
  for (var i = 0; i < rest.length; i += 3) {
    groups.add(rest.substring(i, (i + 3).clamp(0, rest.length)));
  }
  return groups.where((g) => g.isNotEmpty).join(' ');
}

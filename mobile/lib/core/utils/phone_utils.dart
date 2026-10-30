/// Ethiopian phone number normalization and validation utilities.
///
/// Accepts three input formats:
/// - `+251XXXXXXXXX` (international E.164)
/// - `00251XXXXXXXXX` (international with 00 prefix)
/// - `0XXXXXXXXX` (local Ethiopian format)
///
/// All formats normalize to canonical E.164: `+251XXXXXXXXX`.
library;

final _e164Regex = RegExp(r'^\+251\d{9}$');

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

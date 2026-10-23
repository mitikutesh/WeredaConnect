/// FUP-49: finds an Ethiopian mobile number in listing text that every buyer
/// reads (crop description and location, trip route and vehicle fields).
///
/// ESC-48 keeps contact closed until the money is held in escrow, so a number
/// typed into that text would reopen the channel. The server refuses it with
/// `CROP_CONTACT_IN_TEXT` / `TRIP_CONTACT_IN_TEXT`; the forms run this same
/// check first so the user sees it before anything is queued (offline-first).
/// Kept rule for rule identical to `server/src/Validation/ContactInText.cs`;
/// the rule is defined in `specs/contracts/crop-listing-api.md` 1.4.0.
/// Pure Dart, no platform imports: safe on web.
library;

/// Longest digit string a number can take: 00251, a trunk 0, then 9 digits.
const int _maxNumberDigits = 15;

/// Most separator characters between two digit groups of one number
/// ("0911 - 23").
const int _maxGapLength = 3;

const int _hyphen = 0x2D;
const int _dot = 0x2E;
const int _space = 0x20;

/// Whether [text] holds a mobile number: a 9 or 7 followed by 8 digits, after
/// a trunk 0, after 251 or 00251 (with or without the trunk 0), or bare,
/// written with or without spaces, dots, dashes, parentheses or a plus
/// between the digit groups.
bool containsPhoneNumber(String? text) {
  if (text == null || text.isEmpty) return false;

  // Invisible format characters are dropped first, so they neither split a
  // number nor count toward the gap between its groups ("0911", a zero-width
  // space, "223344").
  final scanned = _withoutFormatCharacters(text);
  final groups = _readDigitGroups(scanned);
  final count = groups.length;

  // linked[i]: group i continues group i - 1 (the gap between them is 1 to 3
  // separators). gapChar[i]: that gap when it is one character, else 0.
  final linked = List<bool>.filled(count, false);
  final gapChar = List<int>.filled(count, 0);
  for (var i = 1; i < count; i++) {
    final gapStart = groups[i - 1].end;
    final gapLength = groups[i].start - gapStart;
    linked[i] = gapLength <= _maxGapLength &&
        _isSeparatorRun(scanned, gapStart, gapLength);
    gapChar[i] = gapLength == 1 ? scanned.codeUnitAt(gapStart) : 0;
  }

  // A date never joins a number: without this, "09-10-2018 50 quintals"
  // reads as 0910201850. A date is three groups joined by the same single '-',
  // '.' or space. With a 4-digit year it is a date even when that separator
  // goes on ("01-09-2017-30-09-2017", "09 10 2017 50 quintals"); with a
  // 2-digit year only when it does not, so the tail of "0911-12-10-15" stays a
  // number.
  final isDate = List<bool>.filled(count, false);
  for (var i = 0; i + 2 < count; i++) {
    final sep = gapChar[i + 1];
    final first = groups[i].digits;
    final last = groups[i + 2].digits;
    if ((sep == _hyphen || sep == _dot || sep == _space) &&
        gapChar[i + 2] == sep &&
        _isDateShape(first, groups[i + 1].digits, last) &&
        (_isYear(first) ||
            _isYear(last) ||
            (gapChar[i] != sep && (i + 3 >= count || gapChar[i + 3] != sep)))) {
      isDate[i] = isDate[i + 1] = isDate[i + 2] = true;
    }
  }

  // Chains: maximal runs of linked groups that are not part of a date.
  var start = 0;
  while (start < count) {
    if (isDate[start]) {
      start++;
      continue;
    }
    var end = start;
    while (end + 1 < count && linked[end + 1] && !isDate[end + 1]) {
      end++;
    }
    if (_chainHoldsNumber(groups, start, end)) return true;
    start = end + 1;
  }
  return false;
}

class _DigitGroup {
  const _DigitGroup(this.digits, this.start, this.end);

  final String digits;
  final int start;
  final int end;
}

/// Whether a chain holds a number. A number with a trunk 0 or a country code
/// may start and end on any group border inside the chain ("45 0911234567").
/// A bare 9-digit number must be the whole chain: "750 000 - 800 000 birr"
/// holds 750000800 but is a price range.
bool _chainHoldsNumber(List<_DigitGroup> groups, int first, int last) {
  for (var a = first; a <= last; a++) {
    var digits = '';
    for (var b = a; b <= last; b++) {
      digits += groups[b].digits;
      if (digits.length > _maxNumberDigits) break;
      if (_isPrefixedMobileNumber(digits) ||
          (a == first && b == last && _isSubscriberNumber(digits, 0))) {
        return true;
      }
    }
  }
  return false;
}

/// Invisible format characters: soft hyphen, zero-width space, joiners and
/// direction marks, word joiner and invisible operators, zero-width no-break
/// space (byte order mark).
bool _isFormatCharacter(int c) =>
    c == 0x00AD ||
    c == 0xFEFF ||
    (c >= 0x200B && c <= 0x200F) ||
    (c >= 0x2060 && c <= 0x2064);

String _withoutFormatCharacters(String text) {
  if (!text.codeUnits.any(_isFormatCharacter)) return text;
  return String.fromCharCodes(
    text.codeUnits.where((c) => !_isFormatCharacter(c)),
  );
}

/// Maximal digit runs, with every digit form mapped to ASCII.
List<_DigitGroup> _readDigitGroups(String text) {
  final groups = <_DigitGroup>[];
  var i = 0;
  while (i < text.length) {
    if (_digitValue(text.codeUnitAt(i)) < 0) {
      i++;
      continue;
    }
    final start = i;
    final digits = StringBuffer();
    while (i < text.length) {
      final d = _digitValue(text.codeUnitAt(i));
      if (d < 0) break;
      digits.writeCharCode(0x30 + d);
      i++;
    }
    groups.add(_DigitGroup(digits.toString(), start, i));
  }
  return groups;
}

/// 0-9 for ASCII, Arabic-Indic, Extended Arabic-Indic and fullwidth digits;
/// 1-9 for the Ethiopic digits (Ethiopic has no zero); -1 for anything else.
int _digitValue(int c) {
  if (c >= 0x0030 && c <= 0x0039) return c - 0x0030;
  if (c >= 0x0660 && c <= 0x0669) return c - 0x0660; // Arabic-Indic
  if (c >= 0x06F0 && c <= 0x06F9) return c - 0x06F0; // Extended Arabic-Indic
  if (c >= 0xFF10 && c <= 0xFF19) return c - 0xFF10; // fullwidth
  if (c >= 0x1369 && c <= 0x1371) return c - 0x1369 + 1; // Ethiopic 1 to 9
  return -1;
}

/// Characters people put between the digit groups of a phone number,
/// including the Unicode spaces a keyboard or a paste can bring. Comma, slash
/// and colon are not among them: they separate prices, fractions and times.
bool _isSeparator(int c) =>
    // space, tab, line breaks
    c == 0x0020 ||
    c == 0x0009 ||
    c == 0x000D ||
    c == 0x000A ||
    // no-break spaces, Ethiopic wordspace
    c == 0x00A0 ||
    c == 0x202F ||
    c == 0x1361 ||
    // typographic spaces
    (c >= 0x2000 && c <= 0x200A) ||
    // math, ideographic and Ogham spaces
    c == 0x205F ||
    c == 0x3000 ||
    c == 0x1680 ||
    // . - ( ) +
    c == 0x002E ||
    c == 0x002D ||
    c == 0x0028 ||
    c == 0x0029 ||
    c == 0x002B ||
    // Unicode hyphens and dashes
    (c >= 0x2010 && c <= 0x2015) ||
    // minus sign
    c == 0x2212;

bool _isSeparatorRun(String text, int start, int length) {
  for (var i = start; i < start + length; i++) {
    if (!_isSeparator(text.codeUnitAt(i))) return false;
  }
  return true;
}

/// Day and month (1-31) before a 2- or 4-digit year, or after a 4-digit year.
bool _isDateShape(String a, String b, String c) =>
    (_isDayOrMonth(a) && _isDayOrMonth(b) && (c.length == 2 || _isYear(c))) ||
    (_isYear(a) && _isDayOrMonth(b) && _isDayOrMonth(c));

bool _isDayOrMonth(String digits) {
  if (digits.length != 1 && digits.length != 2) return false;
  final value = int.parse(digits);
  return value >= 1 && value <= 31;
}

bool _isYear(String digits) =>
    digits.length == 4 && (digits.startsWith('19') || digits.startsWith('20'));

/// (00251 | 251) 0? subscriber, or 0 subscriber.
bool _isPrefixedMobileNumber(String digits) {
  final i = digits.startsWith('00251')
      ? 5
      : digits.startsWith('251')
          ? 3
          : 0;
  if (digits.length - i == 10 && digits.codeUnitAt(i) == 0x30) {
    return _isSubscriberNumber(digits, i + 1);
  }
  return i > 0 && _isSubscriberNumber(digits, i);
}

/// The 9 digits after the prefix: a 9 (Ethio Telecom) or 7 (Safaricom), then
/// 8 digits.
bool _isSubscriberNumber(String digits, int from) {
  if (digits.length - from != 9) return false;
  final first = digits.codeUnitAt(from);
  return first == 0x39 || first == 0x37;
}

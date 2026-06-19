/// Text normalization for place search.
///
/// Latin input is lowercased and stripped of common accents/diacritics so that
/// e.g. "Ādīs" matches "adis". Non-Latin scripts — notably Ge'ez (Amharic) and
/// any other codepoint not in the fold map — are left untouched so Amharic
/// input matches Amharic data directly.
///
/// This is the Dart counterpart of the JS idiom
/// `str.normalize("NFD").replace(/[̀-ͯ]/g, "")`. Dart's core `String`
/// does not expose Unicode NFD normalization, so we fold an explicit table of
/// precomposed Latin letters instead — no runtime dependency, and Ge'ez is
/// guaranteed to pass through verbatim.
String normalizeForSearch(String input) {
  final lower = input.toLowerCase();
  final buf = StringBuffer();
  for (final rune in lower.runes) {
    buf.write(_latinFold[rune] ?? String.fromCharCode(rune));
  }
  return buf.toString().replaceAll(_whitespace, ' ').trim();
}

final RegExp _whitespace = RegExp(r'\s+');

/// Maps precomposed lowercase Latin codepoints to their base letter. Built once.
final Map<int, String> _latinFold = _buildFold();

Map<int, String> _buildFold() {
  // Each base letter lists the accented lowercase forms that fold onto it.
  const groups = <String, String>{
    'a': 'àáâãäåāăą',
    'c': 'çćĉċč',
    'd': 'ďđ',
    'e': 'èéêëēĕėęě',
    'g': 'ĝğġģ',
    'h': 'ĥħ',
    'i': 'ìíîïĩīĭįı',
    'j': 'ĵ',
    'k': 'ķ',
    'l': 'ĺļľŀł',
    'n': 'ñńņňŉ',
    'o': 'òóôõöøōŏő',
    'r': 'ŕŗř',
    's': 'śŝşš',
    't': 'ţťŧ',
    'u': 'ùúûüũūŭůűų',
    'w': 'ŵ',
    'y': 'ýÿŷ',
    'z': 'źżž',
  };
  final map = <int, String>{};
  groups.forEach((base, accented) {
    for (final rune in accented.runes) {
      map[rune] = base;
    }
  });
  return map;
}

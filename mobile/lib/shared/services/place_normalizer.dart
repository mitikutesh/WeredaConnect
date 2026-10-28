/// Text normalization for place search and place matching.
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

/// Display form of a GeoNames place name: transliteration marks dropped,
/// Latin diacritics folded, case kept, whitespace collapsed.
///
/// GeoNames writes Ethiopian ejectives and the glottal stop with apostrophe
/// marks and long vowels with macrons ("Mek’ī", "‘Alem T’ēna", "Mek'ele"),
/// which read as noise to users. This gives "Meki", "Alem Tena" and
/// "Mekele". Ge'ez passes through verbatim. Use it for English (Latin)
/// GeoNames names only: Afaan Oromo writes a glottal stop with an apostrophe
/// that belongs to the word ("Bu'ee").
///
/// `clean_display_name` in `scripts/build_ethiopia_places.py` is the twin
/// used when merging the place overrides; keep the two in lockstep.
String cleanPlaceName(String input) {
  final buf = StringBuffer();
  for (final rune in input.replaceAll(_placeMarks, '').runes) {
    buf.write(_latinFoldCased[rune] ?? String.fromCharCode(rune));
  }
  return buf.toString().replaceAll(_whitespace, ' ').trim();
}

/// Search key for place typeahead: [normalizeForSearch] with transliteration
/// marks dropped, so "Meki", "Mek'i" and "Mek’ī" all give "meki".
///
/// Matches `search_key` in `scripts/build_ethiopia_places.py`. Unlike
/// [normalizeWoreda] it keeps hyphens and any region suffix, and it does not
/// feed server matching, so changing it never affects the server mirror.
String placeSearchKey(String input) =>
    normalizeForSearch(input.replaceAll(_placeMarks, ''));

/// GeoNames transliteration marks: straight and typographic apostrophes, the
/// modifier letter apostrophe and turned comma, and the backtick.
final RegExp _placeMarks = RegExp("['‘’ʼʻ`]");

/// [_latinFold] plus the uppercase accented forms, folding to an uppercase
/// base ("Ā" to "A"), for [cleanPlaceName], which keeps the case.
final Map<int, String> _latinFoldCased = _buildCasedFold();

Map<int, String> _buildCasedFold() {
  final map = <int, String>{..._latinFold};
  _latinFold.forEach((rune, base) {
    final upper = String.fromCharCode(rune).toUpperCase();
    // Skip letters without a distinct one-letter uppercase form ("ı", "ŉ").
    if (upper.runes.length != 1 || upper.runes.first < 0x80) return;
    map[upper.runes.first] = base.toUpperCase();
  });
  return map;
}

/// Canonical comparison key for free-text woreda / place names.
///
/// Place strings reach the matcher from sources that never agree on spelling:
/// the place picker writes the GeoNames form ("Ādama"), quick-fill chips and
/// manual typing write whatever the user entered ("Adama", "Adama, Oromia",
/// "adama "), and synced records carry all of the above. This is the ONE rule
/// every woreda comparison goes through (trip suggestions, the geo proxy in
/// `MatchScorer`, centroid lookup). The server mirrors it in
/// `server/src/Helpers/WoredaNames.cs`; keep the two in lockstep.
///
/// 1. keep only the place segment before a `, Region` / `(Region)` suffix
///    (Latin or Ethiopic comma, round or square bracket);
/// 2. lowercase, fold Latin diacritics and collapse whitespace
///    ([normalizeForSearch]);
/// 3. drop apostrophes (GeoNames "Debre Mark’os") and treat hyphens, slashes
///    and underscores as spaces.
///
/// Ge'ez passes through verbatim, so Amharic input still matches Amharic data.
String normalizeWoreda(String input) {
  var s = input;
  final cut = s.indexOf(_woredaSuffixStart);
  if (cut >= 0) s = s.substring(0, cut);
  s = normalizeForSearch(s);
  s = s.replaceAll(_woredaApostrophes, '').replaceAll(_woredaSeparators, ' ');
  return s.replaceAll(_whitespace, ' ').trim();
}

/// Start of a disambiguating suffix: `,` or `፣` (Ethiopic comma), `(`, `[`.
final RegExp _woredaSuffixStart = RegExp(r'[,፣(\[]');

/// Straight and typographic apostrophes, the modifier letter apostrophe used
/// by GeoNames transliterations, and the backtick.
final RegExp _woredaApostrophes = RegExp("['‘’ʼ`]");

final RegExp _woredaSeparators = RegExp(r'[-_/]');

/// How two free-text woreda names relate once normalised.
enum WoredaMatch { none, partial, exact }

/// Compares two raw woreda names after [normalizeWoreda].
///
/// `exact` when the keys are equal; `partial` when every word of the shorter
/// name appears as a whole word in the longer one ("Adama" vs "Adama Zone",
/// "Sodo" vs "Wolaita Sodo"); `none` otherwise, including when either side
/// is blank. Whole-word containment replaces the raw substring rule, which
/// matched "Ambo" inside "Gambo".
WoredaMatch compareWoredas(String a, String b) =>
    compareWoredaKeys(normalizeWoreda(a), normalizeWoreda(b));

/// [compareWoredas] for keys that were already passed through
/// [normalizeWoreda]. Lets a caller normalise a fixed side once when
/// comparing it against many candidates.
WoredaMatch compareWoredaKeys(String keyA, String keyB) {
  if (keyA.isEmpty || keyB.isEmpty) return WoredaMatch.none;
  if (keyA == keyB) return WoredaMatch.exact;
  final wordsA = keyA.split(' ');
  final wordsB = keyB.split(' ');
  final (shorter, longer) =
      wordsA.length <= wordsB.length ? (wordsA, wordsB) : (wordsB, wordsA);
  return shorter.every(longer.contains)
      ? WoredaMatch.partial
      : WoredaMatch.none;
}

using System.Text;

namespace WeredaConnect.Server.Helpers;

/// <summary>How two free-text woreda names relate once normalised.</summary>
public enum WoredaMatch
{
    None,
    Partial,
    Exact,
}

/// <summary>
/// Canonical comparison key for free-text woreda / place names.
///
/// Place strings arrive in spellings that never agree: the mobile place picker
/// writes the GeoNames form ("Ādama"), typed input writes "Adama" or
/// "Adama, Oromia", and synced records carry all of the above. This is the ONE
/// rule the server applies when it compares woreda strings (the geo proxy in
/// <see cref="MatchScoring"/>, used by GET /match and the crop match stream).
///
/// ⚠️ MUST stay in lockstep with the Dart helper at
/// mobile/lib/shared/services/place_normalizer.dart (normalizeWoreda +
/// compareWoredas): same suffix cut, same explicit diacritic fold table, same
/// whole-word partial rule. A deliberate explicit fold table (rather than
/// Unicode NFD) keeps both sides folding exactly the same characters.
///
/// 1. keep only the place segment before a ", Region" / "(Region)" suffix
///    (Latin or Ethiopic comma, round or square bracket);
/// 2. lowercase, fold Latin diacritics, collapse whitespace;
/// 3. drop apostrophes (GeoNames "Debre Mark’os") and treat hyphens, slashes
///    and underscores as spaces.
///
/// Ge'ez passes through verbatim. AOT-safe: no regex, no reflection.
/// </summary>
public static class WoredaNames
{
    private const int EthiopicComma = 0x1363;

    /// <summary>Maps precomposed lowercase Latin code points to their base letter.</summary>
    private static readonly Dictionary<int, string> LatinFold = BuildFold();

    /// <summary>Normalised comparison key for <paramref name="input"/>; empty when blank.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        var pendingSpace = false;
        foreach (var rune in input.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == ',' || value == EthiopicComma || value == '(' || value == '[')
            {
                break;
            }

            if (IsApostrophe(value))
            {
                continue;
            }

            if (value == '-' || value == '_' || value == '/' || Rune.IsWhiteSpace(rune))
            {
                pendingSpace = true;
                continue;
            }

            var lower = Rune.ToLowerInvariant(rune);
            if (pendingSpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            pendingSpace = false;
            sb.Append(LatinFold.TryGetValue(lower.Value, out var folded) ? folded : lower.ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compares two raw woreda names after <see cref="Normalize"/>: Exact when
    /// the keys are equal; Partial when every word of the shorter name appears
    /// as a whole word in the longer one ("Adama" vs "Adama Zone"); None
    /// otherwise, including when either side is blank. Whole-word containment
    /// replaces the raw substring rule, which matched "Ambo" inside "Gambo".
    /// </summary>
    public static WoredaMatch Compare(string? a, string? b)
        => CompareKeys(Normalize(a), Normalize(b));

    /// <summary><see cref="Compare"/> for keys already passed through <see cref="Normalize"/>.</summary>
    public static WoredaMatch CompareKeys(string keyA, string keyB)
    {
        if (keyA.Length == 0 || keyB.Length == 0)
        {
            return WoredaMatch.None;
        }

        if (string.Equals(keyA, keyB, StringComparison.Ordinal))
        {
            return WoredaMatch.Exact;
        }

        var wordsA = keyA.Split(' ');
        var wordsB = keyB.Split(' ');
        var (shorter, longer) = wordsA.Length <= wordsB.Length ? (wordsA, wordsB) : (wordsB, wordsA);
        foreach (var word in shorter)
        {
            if (Array.IndexOf(longer, word) < 0)
            {
                return WoredaMatch.None;
            }
        }

        return WoredaMatch.Partial;
    }

    /// <summary>Straight and typographic apostrophes, the modifier letter apostrophe, and the backtick.</summary>
    private static bool IsApostrophe(int value)
        => value == '\'' || value == 0x2018 || value == 0x2019 || value == 0x02BC || value == '`';

    private static Dictionary<int, string> BuildFold()
    {
        // Each base letter lists the accented lowercase forms that fold onto it.
        // Identical to the Dart table in place_normalizer.dart.
        var groups = new (string Base, string Accented)[]
        {
            ("a", "àáâãäåāăą"),
            ("c", "çćĉċč"),
            ("d", "ďđ"),
            ("e", "èéêëēĕėęě"),
            ("g", "ĝğġģ"),
            ("h", "ĥħ"),
            ("i", "ìíîïĩīĭįı"),
            ("j", "ĵ"),
            ("k", "ķ"),
            ("l", "ĺļľŀł"),
            ("n", "ñńņňŉ"),
            ("o", "òóôõöøōŏő"),
            ("r", "ŕŗř"),
            ("s", "śŝşš"),
            ("t", "ţťŧ"),
            ("u", "ùúûüũūŭůűų"),
            ("w", "ŵ"),
            ("y", "ýÿŷ"),
            ("z", "źżž"),
        };

        var map = new Dictionary<int, string>();
        foreach (var (baseLetter, accented) in groups)
        {
            foreach (var rune in accented.EnumerateRunes())
            {
                map[rune.Value] = baseLetter;
            }
        }

        return map;
    }
}

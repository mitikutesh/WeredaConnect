using System.Text;

namespace WeredaConnect.Server.Validation;

/// <summary>
/// FUP-49: finds an Ethiopian mobile number in listing text that every buyer reads (crop
/// description and place names, trip vehicle and route fields). ESC-48 keeps contact closed
/// until the money is held in escrow, so a number typed into that text would reopen the
/// channel. A linear hand scanner (no regex, nothing to backtrack), kept rule for rule
/// identical to the client copy in mobile/lib/core/utils/contact_in_text.dart so the form
/// refuses exactly what the server refuses. The rule is defined in crop-listing-api.md 1.4.0.
/// </summary>
public static class ContactInText
{
    /// <summary>Longest digit string a number can take: 00251, a trunk 0, then 9 digits.</summary>
    private const int MaxNumberDigits = 15;

    /// <summary>Most separator characters between two digit groups of one number ("0911 - 23").</summary>
    private const int MaxGapLength = 3;

    /// <summary>
    /// Whether <paramref name="text"/> holds a mobile number: a 9 or 7 followed by 8 digits,
    /// after a trunk 0, after 251 or 00251 (with or without the trunk 0), or bare, written with
    /// or without spaces, dots, dashes, parentheses or a plus between the digit groups.
    /// </summary>
    public static bool ContainsPhoneNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Invisible format characters are dropped first, so they neither split a number nor
        // count toward the gap between its groups ("0911", a zero-width space, "223344").
        text = WithoutFormatCharacters(text);
        var groups = ReadDigitGroups(text);
        var count = groups.Count;

        // linked[i]: group i continues group i - 1 (the gap between them is 1 to 3 separators).
        // gapChar[i]: that gap when it is one character, else 0.
        var linked = new bool[count];
        var gapChar = new int[count];
        for (var i = 1; i < count; i++)
        {
            var gapStart = groups[i - 1].End;
            var gapLength = groups[i].Start - gapStart;
            linked[i] = gapLength <= MaxGapLength && IsSeparatorRun(text, gapStart, gapLength);
            gapChar[i] = gapLength == 1 ? text[gapStart] : 0;
        }

        // A date never joins a number: without this, "09-10-2018 50 quintals" reads as
        // 0910201850. A date is three groups joined by the same single '-', '.' or space. With a
        // 4-digit year it is a date even when that separator goes on ("01-09-2017-30-09-2017",
        // "09 10 2017 50 quintals"); with a 2-digit year only when it does not, so the tail of
        // "0911-12-10-15" stays a number.
        var isDate = new bool[count];
        for (var i = 0; i + 2 < count; i++)
        {
            var sep = gapChar[i + 1];
            var first = groups[i].Digits;
            var last = groups[i + 2].Digits;
            if ((sep == '-' || sep == '.' || sep == ' ') && gapChar[i + 2] == sep
                && IsDateShape(first, groups[i + 1].Digits, last)
                && (IsYear(first) || IsYear(last)
                    || (gapChar[i] != sep && (i + 3 >= count || gapChar[i + 3] != sep))))
            {
                isDate[i] = isDate[i + 1] = isDate[i + 2] = true;
            }
        }

        // Chains: maximal runs of linked groups that are not part of a date.
        var start = 0;
        while (start < count)
        {
            if (isDate[start])
            {
                start++;
                continue;
            }

            var end = start;
            while (end + 1 < count && linked[end + 1] && !isDate[end + 1])
            {
                end++;
            }

            if (ChainHoldsNumber(groups, start, end))
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    /// <summary>
    /// The fields of <paramref name="fields"/> whose text in <paramref name="data"/> holds a
    /// phone number, in the order given (so the refusal text is the same for the same payload).
    /// On update, pass <paramref name="storedValue"/>: a value equal to the stored one is an
    /// unchanged echo and is not checked, so a listing stored before this rule can still change
    /// its status.
    /// </summary>
    public static List<string> FieldsWithPhoneNumber(
        Dictionary<string, object> data,
        IEnumerable<string> fields,
        Func<string, string?>? storedValue = null)
    {
        var found = new List<string>();
        foreach (var field in fields)
        {
            if (!data.TryGetValue(field, out var value) || value == null)
            {
                continue;
            }

            var text = value.ToString();
            if (storedValue != null && string.Equals(text, storedValue(field), StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsPhoneNumber(text))
            {
                found.Add(field);
            }
        }

        return found;
    }

    private readonly record struct DigitGroup(string Digits, int Start, int End);

    /// <summary>
    /// Whether a chain holds a number. A number with a trunk 0 or a country code may start and
    /// end on any group border inside the chain ("45 0911234567"). A bare 9-digit number must be
    /// the whole chain: "750 000 - 800 000 birr" holds 750000800 but is a price range.
    /// </summary>
    private static bool ChainHoldsNumber(List<DigitGroup> groups, int first, int last)
    {
        for (var a = first; a <= last; a++)
        {
            var digits = "";
            for (var b = a; b <= last; b++)
            {
                digits += groups[b].Digits;
                if (digits.Length > MaxNumberDigits)
                {
                    break;
                }

                if (IsPrefixedMobileNumber(digits)
                    || (a == first && b == last && IsSubscriberNumber(digits, 0)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Invisible format characters: soft hyphen, zero-width space, joiners and direction marks,
    /// word joiner and invisible operators, zero-width no-break space (byte order mark).
    /// </summary>
    private static bool IsFormatCharacter(char c) => (int)c switch
    {
        0x00AD or 0xFEFF => true,
        >= 0x200B and <= 0x200F => true,
        >= 0x2060 and <= 0x2064 => true,
        _ => false,
    };

    private static string WithoutFormatCharacters(string text)
    {
        StringBuilder? kept = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsFormatCharacter(text[i]))
            {
                kept ??= new StringBuilder(text, 0, i, text.Length);
            }
            else
            {
                kept?.Append(text[i]);
            }
        }

        return kept?.ToString() ?? text;
    }

    /// <summary>Maximal digit runs, with every digit form mapped to ASCII.</summary>
    private static List<DigitGroup> ReadDigitGroups(string text)
    {
        var groups = new List<DigitGroup>();
        var i = 0;
        while (i < text.Length)
        {
            if (DigitValue(text[i]) < 0)
            {
                i++;
                continue;
            }

            var start = i;
            var digits = new StringBuilder();
            while (i < text.Length && DigitValue(text[i]) is var d and >= 0)
            {
                digits.Append((char)('0' + d));
                i++;
            }

            groups.Add(new DigitGroup(digits.ToString(), start, i));
        }

        return groups;
    }

    /// <summary>
    /// 0-9 for ASCII, Arabic-Indic, Extended Arabic-Indic and fullwidth digits; 1-9 for the
    /// Ethiopic digits (Ethiopic has no zero); -1 for anything else.
    /// </summary>
    private static int DigitValue(char c) => (int)c switch
    {
        >= 0x0030 and <= 0x0039 => c - 0x0030,
        >= 0x0660 and <= 0x0669 => c - 0x0660, // Arabic-Indic
        >= 0x06F0 and <= 0x06F9 => c - 0x06F0, // Extended Arabic-Indic
        >= 0xFF10 and <= 0xFF19 => c - 0xFF10, // fullwidth
        >= 0x1369 and <= 0x1371 => c - 0x1369 + 1, // Ethiopic one to nine
        _ => -1,
    };

    /// <summary>
    /// Characters people put between the digit groups of a phone number, including the Unicode
    /// spaces a keyboard or a paste can bring. Comma, slash and colon are not among them: they
    /// separate prices, fractions and times.
    /// </summary>
    private static bool IsSeparator(char c) => (int)c switch
    {
        0x0020 or 0x0009 or 0x000D or 0x000A => true, // space, tab, line breaks
        0x00A0 or 0x202F or 0x1361 => true, // no-break spaces, Ethiopic wordspace
        >= 0x2000 and <= 0x200A => true, // typographic spaces
        0x205F or 0x3000 or 0x1680 => true, // math, ideographic and Ogham spaces
        0x002E or 0x002D or 0x0028 or 0x0029 or 0x002B => true, // . - ( ) +
        >= 0x2010 and <= 0x2015 => true, // Unicode hyphens and dashes
        0x2212 => true, // minus sign
        _ => false,
    };

    private static bool IsSeparatorRun(string text, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (!IsSeparator(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Day and month (1-31) before a 2- or 4-digit year, or after a 4-digit year.</summary>
    private static bool IsDateShape(string a, string b, string c) =>
        (IsDayOrMonth(a) && IsDayOrMonth(b) && (c.Length == 2 || IsYear(c)))
        || (IsYear(a) && IsDayOrMonth(b) && IsDayOrMonth(c));

    private static bool IsDayOrMonth(string digits) =>
        digits.Length is 1 or 2 && int.Parse(digits) is >= 1 and <= 31;

    private static bool IsYear(string digits) =>
        digits.Length == 4 && (digits.StartsWith("19", StringComparison.Ordinal)
                               || digits.StartsWith("20", StringComparison.Ordinal));

    /// <summary>(00251 | 251) 0? subscriber, or 0 subscriber.</summary>
    private static bool IsPrefixedMobileNumber(string digits)
    {
        var i = digits.StartsWith("00251", StringComparison.Ordinal) ? 5
            : digits.StartsWith("251", StringComparison.Ordinal) ? 3
            : 0;
        if (digits.Length - i == 10 && digits[i] == '0')
        {
            return IsSubscriberNumber(digits, i + 1);
        }

        return i > 0 && IsSubscriberNumber(digits, i);
    }

    /// <summary>The 9 digits after the prefix: a 9 (Ethio Telecom) or 7 (Safaricom), then 8 digits.</summary>
    private static bool IsSubscriberNumber(string digits, int from) =>
        digits.Length - from == 9 && (digits[from] == '9' || digits[from] == '7');
}

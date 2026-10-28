using System.Globalization;
using System.Text;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-21: the push title and body of every notification type in English (<c>en</c>), Amharic
/// (<c>am</c>, Ethiopic script) and Afaan Oromoo (<c>om</c>, Latin script), rendered at send time
/// in the recipient's preferred language. Plain, respectful words for a farmer, a trucker or a
/// buyer on a small phone; the inbox renders the same type and params on the device.
///
/// Templates:
/// - <c>{token}</c> is a param rendered for reading: <c>{buyer}</c>, <c>{farmer}</c>,
///   <c>{trucker}</c> (display names), <c>{crop}</c> (the crop's local name), <c>{qty}</c>
///   (<c>500 kg</c>), <c>{amount}</c> (<c>9,640 ETB</c>, <c>9,640 ብር</c>), <c>{deadline}</c> (Ethiopia time:
///   <c>27 Oct 18:00</c> in en, on the Ethiopian clock in am and om, <c>27 ኦክቶ ከምሽቱ 12:00</c>,
///   <c>27 Onk 12:00 galgala</c>), <c>{pickup}</c> (<c>28 Oct</c>), <c>{decision}</c> (the dispute
///   verdict as a sentence), <c>{hint}</c> (what a held payout needs, from the reason code), and for
///   <c>truck_nearby</c> (LT-07) <c>{km}</c> (<c>3.2 km</c>, <c>3.2 ኪ.ሜ</c>, <c>km 3.2</c>) and
///   <c>{minutes}</c> (a whole number, never a clock time);
/// - <c>[ ... ]</c> is an optional part, dropped whole when any token in it has no value, so a
///   missing param never leaves a dangling word; a missing token outside one renders as nothing;
/// - an unknown language renders <see cref="FallbackLanguage"/>, an unknown type a generic line.
///
/// Never a phone number: names are scrubbed of digit runs before they are placed. The body is
/// capped below 140 characters.
/// </summary>
public static class PushText
{
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "am", "om" };

    /// <summary>The language of a recipient whose preference is missing or unknown.</summary>
    public const string FallbackLanguage = "am";

    /// <summary>Longest body; a longer rendering (very long names) is cut with an ellipsis.</summary>
    public const int MaxBodyLength = 139;

    /// <summary>Longest title.</summary>
    public const int MaxTitleLength = 65;

    /// <summary>Longest name placed in a text; a longer one is cut with an ellipsis.</summary>
    public const int MaxNameLength = 30;

    /// <summary>East Africa Time, UTC+3 all year (no daylight saving).</summary>
    public static readonly TimeSpan EthiopiaOffset = TimeSpan.FromHours(3);

    /// <summary>A digit run this long inside a name is treated as a phone number and removed.</summary>
    private const int PhoneDigits = 7;

    private const string Ellipsis = "…";

    /// <summary>Punctuation that sits on the word before it (Latin and Ethiopic).</summary>
    private const string TightPunctuation = ".,;:!?።፣፤፥";

    /// <summary>The push title and body of <paramref name="type"/> in <paramref name="language"/>.</summary>
    public static (string Title, string Body) Render(string type, string? language, IReadOnlyDictionary<string, string> p)
    {
        var lang = LanguageOf(language);
        var templates = lang switch
        {
            "en" => En,
            "om" => Om,
            _ => Am
        };
        if (!templates.TryGetValue(type, out var template))
        {
            template = Generic[lang];
        }

        return (Cap(Fill(template.Title, lang, p), MaxTitleLength), Cap(Fill(template.Body, lang, p), MaxBodyLength));
    }

    /// <summary>The supported language of a stored preference (<c>en</c>, <c>EN</c>, <c>en-US</c>), else the fallback.</summary>
    public static string LanguageOf(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return FallbackLanguage;
        }

        var code = language.Trim();
        var cut = code.IndexOfAny(new[] { '-', '_' });
        if (cut > 0)
        {
            code = code[..cut];
        }

        code = code.ToLowerInvariant();
        return Languages.Contains(code) ? code : FallbackLanguage;
    }

    /// <summary>
    /// An ETB amount for reading, with the unit the app shows in that language: <c>9640.00</c> is
    /// <c>9,640 ETB</c> (en), <c>9,640 ብር</c> (am), <c>9,640 Birrii</c> (om); <c>9640.5</c> keeps
    /// two decimals.
    /// </summary>
    public static string FormatAmount(string language, string? amountEtb)
    {
        if (!decimal.TryParse(amountEtb, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return string.Empty;
        }

        var format = value == decimal.Truncate(value) ? "#,0" : "#,0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + CurrencyUnits[Index(language)];
    }

    /// <summary>
    /// An ISO UTC time in Ethiopia time: the Gregorian day and month in the language, then the
    /// time on the clock the language reads (<see cref="FormatClock"/>): <c>27 Oct 18:00</c> (en),
    /// <c>27 ኦክቶ ከምሽቱ 12:00</c> (am), <c>27 Onk 12:00 galgala</c> (om).
    /// </summary>
    public static string FormatDeadline(string language, string? iso)
        => TryEthiopiaTime(iso, out var local)
            ? $"{FormatDay(language, local)} {FormatClock(language, local.Hour, local.Minute)}"
            : string.Empty;

    /// <summary>An ISO UTC date in Ethiopia time, day and month only: <c>28 Oct</c>.</summary>
    public static string FormatDate(string language, string? iso)
        => TryEthiopiaTime(iso, out var local) ? FormatDay(language, local) : string.Empty;

    /// <summary>
    /// LT-04: an Ethiopia wall-clock time (24-hour <paramref name="hour"/>, 0 to 23) as the
    /// language reads it. Amharic and Afaan Oromo read the Ethiopian clock, whose day starts at 6
    /// in the morning, always with the part of the day: <c>ከቀኑ 7:20</c> (am, the part first) and
    /// <c>7:20 waaree booda</c> (om, the part after). English keeps the 24-hour clock: <c>13:20</c>.
    /// Spec live-trip.md section 9. The cases in
    /// <c>specs/contracts/fixtures/ethiopian-clock-cases.json</c> are shared with the app's
    /// <c>EthiopianClock</c>, so a notification and the screen it opens read the same time.
    /// </summary>
    internal static string FormatClock(string language, int hour, int minute)
        => Index(language) == 0
            ? TwentyFourHourTime(hour, minute)
            : WithDayPart(language, hour, EthiopianTime(hour, minute));

    /// <summary>
    /// LT-04: a window between two Ethiopia wall-clock times. On the Ethiopian clock the part of
    /// the day is said once when both ends share it (<c>ከቀኑ 7:20-8:00</c>) and at each end when it
    /// changes (<c>ከቀኑ 11:30-ከምሽቱ 12:30</c>); English reads <c>13:20-14:00</c>.
    /// </summary>
    internal static string FormatClockRange(string language, int fromHour, int fromMinute, int toHour, int toMinute)
    {
        if (Index(language) == 0)
        {
            return $"{TwentyFourHourTime(fromHour, fromMinute)}-{TwentyFourHourTime(toHour, toMinute)}";
        }

        return DayPart(fromHour) == DayPart(toHour)
            ? WithDayPart(language, fromHour, $"{EthiopianTime(fromHour, fromMinute)}-{EthiopianTime(toHour, toMinute)}")
            : $"{FormatClock(language, fromHour, fromMinute)}-{FormatClock(language, toHour, toMinute)}";
    }

    // ===== rendering =====

    /// <summary>The template with its tokens placed and its optional parts kept or dropped, then tidied.</summary>
    private static string Fill(string template, string language, IReadOnlyDictionary<string, string> p)
    {
        var output = new StringBuilder(template.Length + 48);
        var start = 0;
        while (start < template.Length)
        {
            var open = template.IndexOf('[', start);
            if (open < 0)
            {
                output.Append(Substitute(template[start..], language, p, out _));
                break;
            }

            output.Append(Substitute(template[start..open], language, p, out _));
            var close = template.IndexOf(']', open + 1);
            if (close < 0)
            {
                close = template.Length;
            }

            var optional = Substitute(template[(open + 1)..close], language, p, out var complete);
            if (complete)
            {
                output.Append(optional);
            }

            start = close + 1;
        }

        return Tidy(output.ToString());
    }

    /// <summary>Places every <c>{token}</c>; <paramref name="complete"/> is false when one had no value.</summary>
    private static string Substitute(string text, string language, IReadOnlyDictionary<string, string> p, out bool complete)
    {
        complete = true;
        var output = new StringBuilder(text.Length + 32);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                output.Append(text, i, text.Length - i);
                break;
            }

            output.Append(text, i, open - i);
            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                break; // a stray brace is dropped with the rest of the part
            }

            var value = Token(text[(open + 1)..close], language, p);
            if (value.Length == 0)
            {
                complete = false;
            }

            output.Append(value);
            i = close + 1;
        }

        return output.ToString();
    }

    private static string Token(string token, string language, IReadOnlyDictionary<string, string> p) => token switch
    {
        "buyer" => Name(Get(p, NotificationParams.BuyerName)),
        "farmer" => Name(Get(p, NotificationParams.FarmerName)),
        "trucker" => Name(Get(p, NotificationParams.TruckerName)),
        "crop" => Crop(language, Get(p, NotificationParams.CropType)),
        "qty" => Quantity(language, Get(p, NotificationParams.QuantityKg)),
        "amount" => FormatAmount(language, Get(p, NotificationParams.AmountEtb)),
        "deadline" => FormatDeadline(language, Get(p, NotificationParams.DeadlineAt)),
        "pickup" => FormatDate(language, Get(p, NotificationParams.PickupDate)),
        "decision" => Pick(Decisions, language, Get(p, NotificationParams.Resolution)),
        "hint" => Pick(PayoutHints, language, Get(p, NotificationParams.Reason)),
        "km" => Distance(language, Get(p, NotificationParams.DistanceKm)),
        "minutes" => Minutes(Get(p, NotificationParams.Minutes)),
        _ => string.Empty
    };

    private static string? Get(IReadOnlyDictionary<string, string> p, string key)
        => p.TryGetValue(key, out var value) ? value : null;

    /// <summary>One space between words, none before punctuation, none at the ends.</summary>
    private static string Tidy(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (output.Length > 0 && output[^1] != ' ')
                {
                    output.Append(' ');
                }

                continue;
            }

            if (TightPunctuation.Contains(c) && output.Length > 0 && output[^1] == ' ')
            {
                output.Length--;
            }

            output.Append(c);
        }

        return output.ToString().Trim();
    }

    private static string Cap(string text, int max)
        => text.Length <= max ? text : Cut(text, max - 1) + Ellipsis;

    /// <summary>The first <paramref name="length"/> characters, one fewer when that would split a surrogate pair.</summary>
    private static string Cut(string text, int length)
    {
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length].TrimEnd();
    }

    /// <summary>A display name as <see cref="ScrubName"/> leaves it, cut to <see cref="MaxNameLength"/>.</summary>
    internal static string Name(string? raw) => Cap(ScrubName(raw), MaxNameLength);

    /// <summary>Characters that join the parts of a written phone number.</summary>
    private const string PhoneSeparators = "+-()./_";

    /// <summary>
    /// A display name safe to show another party: all numerals and phone separators go when it holds
    /// <see cref="PhoneDigits"/> or more of them in total (however they are separated, Ethiopic
    /// numerals included), and template, control and format characters (a right-to-left override
    /// could reorder the rest of the text) go always. The inbox stores names through this too.
    /// </summary>
    public static string ScrubName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var dropNumbers = raw.Count(char.IsNumber) >= PhoneDigits;
        var output = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (dropNumbers && (char.IsNumber(c) || PhoneSeparators.Contains(c)))
            {
                output.Append(' ');
                continue;
            }

            if (c is '{' or '}' or '[' or ']' || char.IsControl(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            output.Append(c);
        }

        return Tidy(output.ToString());
    }

    private static string Crop(string language, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        return CropNames.TryGetValue(code.Trim().ToLowerInvariant(), out var names)
            ? names[Index(language)]
            : Name(code);
    }

    /// <summary>A quantity in kilograms with the unit the language writes: <c>500 kg</c>, <c>500 ኪ.ግ</c>, <c>kg 500</c>.</summary>
    private static string Quantity(string language, string? kg)
    {
        if (!decimal.TryParse(kg, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return string.Empty;
        }

        var number = value.ToString("#,0.##", CultureInfo.InvariantCulture);
        return language switch
        {
            "en" => number + " kg",
            "om" => "kg " + number,
            _ => number + " ኪ.ግ"
        };
    }

    /// <summary>LT-07: a distance in kilometres, one decimal, with the unit the language writes: <c>3.2 km</c>, <c>3.2 ኪ.ሜ</c>, <c>km 3.2</c>.</summary>
    private static string Distance(string language, string? km)
    {
        if (!decimal.TryParse(km, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return string.Empty;
        }

        var number = value.ToString("0.0", CultureInfo.InvariantCulture);
        return language switch
        {
            "en" => number + " km",
            "om" => "km " + number,
            _ => number + " ኪ.ሜ"
        };
    }

    /// <summary>LT-07: a whole number of minutes above zero; anything else drops its optional part.</summary>
    private static string Minutes(string? minutes)
        => int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Pick(Dictionary<string, string[]> sentences, string language, string? code)
        => sentences.TryGetValue(code ?? string.Empty, out var byLanguage)
            ? byLanguage[Index(language)]
            : sentences[string.Empty][Index(language)];

    private static bool TryEthiopiaTime(string? iso, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(iso)
            || !DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            return false;
        }

        local = utc + EthiopiaOffset;
        return true;
    }

    private static string FormatDay(string language, DateTime local)
        => $"{local.Day.ToString(CultureInfo.InvariantCulture)} {Months[Index(language)][local.Month - 1]}";

    /// <summary>The 24-hour clock: <c>13:20</c>, <c>02:00</c>.</summary>
    private static string TwentyFourHourTime(int hour, int minute)
        => $"{hour.ToString("00", CultureInfo.InvariantCulture)}:{minute.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The Ethiopian clock without the part of the day: the hour is (h - 6) mod 12, written
    /// (h + 6) % 12 so it never goes negative, with 0 shown as 12; <c>13:20</c> is <c>7:20</c>.
    /// </summary>
    private static string EthiopianTime(int hour, int minute)
    {
        var h = (hour + 6) % 12;
        return $"{(h == 0 ? 12 : h).ToString(CultureInfo.InvariantCulture)}:{minute.ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The part of the day of a 24-hour hour: 0 night (0 to 5), 1 morning (6 to 11), 2 day (12 to 17), 3 evening (18 to 23).</summary>
    private static int DayPart(int hour) => hour / 6;

    /// <summary>The part of the day where the language says it: before the time in am, after it in om.</summary>
    private static string WithDayPart(string language, int hour, string time)
        => Index(language) == 2
            ? $"{time} {OmDayParts[DayPart(hour)]}"
            : $"{AmDayParts[DayPart(hour)]} {time}";

    /// <summary>Column of a language in the per-language arrays: en, am, om.</summary>
    private static int Index(string language) => language switch
    {
        "en" => 0,
        "om" => 2,
        _ => 1
    };

    // ===== words =====

    /// <summary>The currency word after an amount (en, am, om), as the app's price labels write it.</summary>
    private static readonly string[] CurrencyUnits = { "ETB", "ብር", "Birrii" };

    private static readonly string[][] Months =
    {
        new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
        new[] { "ጃን", "ፌብ", "ማርች", "ኤፕሪ", "ሜይ", "ጁን", "ጁላይ", "ኦገስ", "ሴፕቴ", "ኦክቶ", "ኖቬም", "ዲሴም" },
        new[] { "Ama", "Gur", "Bit", "Elb", "Cam", "Wax", "Ado", "Hag", "Ful", "Onk", "Sad", "Mud" }
    };

    /// <summary>The parts of the day on the Ethiopian clock, by <see cref="DayPart"/>: night, morning, day, evening.</summary>
    private static readonly string[] AmDayParts = { "ከሌሊቱ", "ከጠዋቱ", "ከቀኑ", "ከምሽቱ" };

    /// <summary>The same parts in Afaan Oromoo, said after the time.</summary>
    private static readonly string[] OmDayParts = { "halkan", "ganama", "waaree booda", "galgala" };

    /// <summary>The crop codes order create accepts, as the app's own crop names (en, am, om).</summary>
    private static readonly Dictionary<string, string[]> CropNames = new(StringComparer.Ordinal)
    {
        { "teff", new[] { "teff", "ጤፍ", "xaafii" } },
        { "wheat", new[] { "wheat", "ስንዴ", "qamadii" } },
        { "barley", new[] { "barley", "ገብስ", "garbuu" } },
        { "maize", new[] { "maize", "በቆሎ", "boqqolloo" } },
        { "chickpea", new[] { "chickpeas", "ሽምብራ", "shumburaa" } },
        { "lentils", new[] { "lentils", "ምስር", "misira" } },
        { "sorghum", new[] { "sorghum", "ማሽላ", "mashilaa" } },
        { "coffee", new[] { "coffee", "ቡና", "buna" } },
        { "sesame", new[] { "sesame", "ሰሊጥ", "saliixaa" } }
    };

    /// <summary>The dispute verdict (<c>resolution</c>) as a sentence; the empty key is the fallback.</summary>
    private static readonly Dictionary<string, string[]> Decisions = new(StringComparer.Ordinal)
    {
        {
            "farmer_wins",
            new[] { "The money goes to the farmer.", "ገንዘቡ ለገበሬው ይከፈላል።", "Maallaqni qonnaan bulaaf kaffalama." }
        },
        {
            "buyer_wins",
            new[] { "The money goes back to the buyer.", "ገንዘቡ ለገዢው ይመለሳል።", "Maallaqni bitataaf deebi'a." }
        },
        {
            string.Empty,
            new[] { "Open the app for the details.", "ዝርዝሩን በመተግበሪያው ይመልከቱ።", "Bal'ina isaa appii keessatti ilaalaa." }
        }
    };

    /// <summary>What a held payout needs, by payout-block reason; the empty key is the fallback.</summary>
    private static readonly Dictionary<string, string[]> PayoutHints = new(StringComparer.Ordinal)
    {
        {
            "fayda_required",
            new[]
            {
                "Please verify your Fayda ID in the app.",
                "እባክዎ በመተግበሪያው የፋይዳ መታወቂያዎን ያረጋግጡ።",
                "Maaloo appii keessatti Fayda ID keessan mirkaneessaa."
            }
        },
        {
            "phone_unverified",
            new[]
            {
                "Please verify your phone number in the app.",
                "እባክዎ በመተግበሪያው ስልክ ቁጥርዎን ያረጋግጡ።",
                "Maaloo appii keessatti lakkoofsa bilbilaa keessan mirkaneessaa."
            }
        },
        {
            "profile_missing",
            new[]
            {
                "Please complete your profile in the app.",
                "እባክዎ በመተግበሪያው መገለጫዎን ያሟሉ።",
                "Maaloo appii keessatti odeeffannoo keessan guutaa."
            }
        },
        {
            string.Empty,
            new[]
            {
                "Open the app to see what is needed.",
                "ምን እንደሚያስፈልግ ለማየት መተግበሪያውን ይክፈቱ።",
                "Maal akka barbaachisu ilaaluuf appii banaa."
            }
        }
    };

    // ===== templates =====

    private readonly record struct Template(string Title, string Body);

    private static readonly Dictionary<string, Template> Generic = new(StringComparer.Ordinal)
    {
        { "en", new("Order update", "Open the app to see what changed.") },
        { "am", new("የትዕዛዝ መረጃ", "ምን እንደተለወጠ ለማየት መተግበሪያውን ይክፈቱ።") },
        { "om", new("Haalli ajajaa jijjiirameera", "Maal akka jijjiirame ilaaluuf appii banaa.") }
    };

    private static readonly Dictionary<string, Template> En = new(StringComparer.Ordinal)
    {
        [NotificationTypes.PaymentConfirmed] = new(
            "Payment received",
            "Your payment[ of {amount}][ for {qty} {crop}] is held safely. We will tell you when the farmer answers."),
        [NotificationTypes.OrderRequest] = new(
            "New order[: {qty} {crop}]",
            "Buyer {buyer} paid[ {amount}], held safely until delivery. Please accept or decline[ by {deadline}]."),
        [NotificationTypes.FarmerAccepted] = new(
            "Farmer accepted",
            "Farmer {farmer} accepted your order[ of {qty} {crop}]. We are now asking the trucker."),
        [NotificationTypes.TransportRequest] = new(
            "Transport request[: {qty} {crop}]",
            "Farmer {farmer} accepted an order on your trip. Please accept or decline[ by {deadline}]."),
        [NotificationTypes.OrderConfirmed] = new(
            "Order confirmed",
            "Everyone has agreed to the order[ of {qty} {crop}].[ Pickup: {pickup}.]"),
        [NotificationTypes.OrderRejected] = new(
            "Order declined",
            "Farmer {farmer} declined your order[ of {qty} {crop}]. Your money will be returned to you."),
        [NotificationTypes.TransportFailed] = new(
            "Transport fell through",
            "The trucker cannot carry your order[ of {qty} {crop}]. Please decide[ by {deadline}]. You can cancel for a full refund."),
        [NotificationTypes.TransportFailedKeepCrops] = new(
            "Transport fell through",
            "The trucker for the order[ of {qty} {crop}] dropped out. Please keep the crops until the buyer decides."),
        [NotificationTypes.RequestExpired] = new(
            "Request closed",
            "The request for the order[ of {qty} {crop}] closed because it was not answered in time."),
        [NotificationTypes.RequestOnHold] = new(
            "Request on hold",
            "Transport for the order[ of {qty} {crop}] is not available. The buyer is deciding, so you do not need to answer now."),
        [NotificationTypes.OrderCancelled] = new(
            "Order cancelled",
            "The order[ of {qty} {crop}] was cancelled. Any money held is returned to the buyer."),
        [NotificationTypes.OrderExpiredUnpaid] = new(
            "Order closed",
            "Your order[ of {qty} {crop}] was closed because it was not paid in time. You can place a new order."),
        [NotificationTypes.PaymentFailed] = new(
            "Payment failed",
            "Your payment[ of {amount}] did not go through. Please try again in the app."),
        [NotificationTypes.RequestReminder] = new(
            "Reminder: your answer is needed",
            "The order[ of {qty} {crop}] is waiting for your answer.[ Please answer by {deadline}.]"),
        [NotificationTypes.PickupOverdue] = new(
            "Pickup is late",
            "Your order[ of {qty} {crop}] was not picked up on time. Open the app to see your options, including a full refund."),
        [NotificationTypes.PickupMissed] = new(
            "Pickup missed",
            "The order[ of {qty} {crop}] was not picked up on time. The buyer may now cancel it for a refund."),
        [NotificationTypes.DeliveryScheduled] = new(
            "Transport arranged",
            "Trucker {trucker} will carry the order[ of {qty} {crop}].[ Pickup: {pickup}.]"),
        [NotificationTypes.BuyerCollecting] = new(
            "Buyer is coming",
            "Buyer {buyer} will collect the order[ of {qty} {crop}] from you. Please have the crops ready."),
        [NotificationTypes.PickedUp] = new(
            "Crops picked up",
            "Your order[ of {qty} {crop}] was picked up from the farmer."),
        [NotificationTypes.HandoffRecorded] = new(
            "Handoff recorded",
            "The handoff[ of {qty} {crop}] is recorded. You are paid after the buyer receives the crops."),
        [NotificationTypes.InTransit] = new(
            "On the way",
            "Your order[ of {qty} {crop}] is on the way to you."),
        [NotificationTypes.TruckNearby] = new(
            "Truck nearby",
            "Trucker {trucker} is near you[, {km} away][, arriving in about {minutes} minutes]. Please be ready to receive the crops."),
        [NotificationTypes.Delivered] = new(
            "Order delivered",
            "Your order[ of {qty} {crop}] was delivered. If something is wrong, report it[ by {deadline}]. If not, the farmer is paid."),
        [NotificationTypes.DeliveredPayoutPending] = new(
            "Delivered, payment coming",
            "The order[ of {qty} {crop}] was delivered. Your payment is sent after the buyer's 48 hour check."),
        [NotificationTypes.OrderSettled] = new(
            "Order complete",
            "The order[ of {qty} {crop}] is complete and everyone is paid. Thank you."),
        [NotificationTypes.RefundSent] = new(
            "Refund sent",
            "Your refund[ of {amount}] has been sent to you."),
        [NotificationTypes.PayoutSent] = new(
            "Payment sent",
            "Your payment[ of {amount}] has been sent to you. Thank you."),
        [NotificationTypes.PayoutBlocked] = new(
            "Payment on hold",
            "Your payment[ of {amount}] is waiting. {hint}"),
        [NotificationTypes.PayoutProcessing] = new(
            "Payment being processed",
            "Our team is sending your payment[ of {amount}] by hand. It can take a few days."),
        [NotificationTypes.DisputeOpened] = new(
            "Problem reported",
            "A problem was reported on your order[ of {qty} {crop}]. The money stays held until our team decides."),
        [NotificationTypes.DisputeReceived] = new(
            "Report received",
            "We received your report. The money stays held until our team decides."),
        [NotificationTypes.DisputeResolved] = new(
            "Decision made",
            "Our team has decided on the reported problem. {decision}"),
        [NotificationTypes.UnderReview] = new(
            "Payment under review",
            "Our team is checking the payment for your order. We will tell you when it is done.")
    };

    private static readonly Dictionary<string, Template> Am = new(StringComparer.Ordinal)
    {
        [NotificationTypes.PaymentConfirmed] = new(
            "ክፍያዎ ደርሷል",
            "[ለ{qty} {crop} ]የከፈሉት ገንዘብ[ {amount}] በጥንቃቄ ተይዟል። ገበሬው መልስ ሲሰጥ እናሳውቅዎታለን።"),
        [NotificationTypes.OrderRequest] = new(
            "አዲስ ትዕዛዝ[: {qty} {crop}]",
            "ገዢ {buyer}[ {amount}] ከፍለዋል፤ ገንዘቡ እስከ ርክክብ በጥንቃቄ ይቆያል። እባክዎ[ እስከ {deadline}] ይቀበሉ ወይም ውድቅ ያድርጉ።"),
        [NotificationTypes.FarmerAccepted] = new(
            "ገበሬው ትዕዛዝዎን ተቀብለዋል",
            "ገበሬ {farmer}[ የ{qty} {crop}] ትዕዛዝዎን ተቀብለዋል። አሁን የጭነት መኪና ሹፌሩን እየጠየቅን ነው።"),
        [NotificationTypes.TransportRequest] = new(
            "የማጓጓዝ ጥያቄ[: {qty} {crop}]",
            "ገበሬ {farmer} በጉዞዎ ላይ ያለ ትዕዛዝ ተቀብለዋል። እባክዎ[ እስከ {deadline}] ይቀበሉ ወይም ውድቅ ያድርጉ።"),
        [NotificationTypes.OrderConfirmed] = new(
            "ትዕዛዙ ተረጋግጧል",
            "ሁሉም ወገኖች[ የ{qty} {crop}] ትዕዛዙን አረጋግጠዋል።[ የሚነሳበት ቀን: {pickup}።]"),
        [NotificationTypes.OrderRejected] = new(
            "ትዕዛዙ ውድቅ ሆኗል",
            "ገበሬ {farmer}[ የ{qty} {crop}] ትዕዛዝዎን አልተቀበሉም። ገንዘብዎ ይመለስልዎታል።"),
        [NotificationTypes.TransportFailed] = new(
            "ማጓጓዣ አልተገኘም",
            "የጭነት መኪና ሹፌሩ[ የ{qty} {crop}] ትዕዛዝዎን ማጓጓዝ አልቻለም። እባክዎ[ እስከ {deadline}] ይወስኑ፤ ከሰረዙ ገንዘብዎ ሙሉ በሙሉ ይመለሳል።"),
        [NotificationTypes.TransportFailedKeepCrops] = new(
            "ማጓጓዣው ቀርቷል",
            "የጭነት መኪና ሹፌሩ ትዕዛዙን አቋርጧል።[ ትዕዛዝ: {qty} {crop}።] ገዢው እስኪወስን ድረስ እባክዎ ሰብሉን ይያዙ።"),
        [NotificationTypes.RequestExpired] = new(
            "ጥያቄው ተዘግቷል",
            "በጊዜው መልስ ስላልተሰጠ ጥያቄው ተዘግቷል።[ ትዕዛዝ: {qty} {crop}።]"),
        [NotificationTypes.RequestOnHold] = new(
            "ጥያቄው ለጊዜው ቆሟል",
            "ለዚህ ትዕዛዝ ማጓጓዣ አልተገኘም።[ ትዕዛዝ: {qty} {crop}።] ገዢው እየወሰነ ስለሆነ አሁን መልስ መስጠት አያስፈልግዎትም።"),
        [NotificationTypes.OrderCancelled] = new(
            "ትዕዛዙ ተሰርዟል",
            "[የ{qty} {crop} ]ትዕዛዙ ተሰርዟል። የተያዘ ገንዘብ ካለ ለገዢው ይመለሳል።"),
        [NotificationTypes.OrderExpiredUnpaid] = new(
            "ትዕዛዙ ተዘግቷል",
            "[የ{qty} {crop} ]ትዕዛዝዎ በጊዜው ስላልተከፈለ ተዘግቷል። አዲስ ትዕዛዝ ማስገባት ይችላሉ።"),
        [NotificationTypes.PaymentFailed] = new(
            "ክፍያው አልተሳካም",
            "ክፍያዎ[ {amount}] አልተሳካም። እባክዎ በመተግበሪያው እንደገና ይሞክሩ።"),
        [NotificationTypes.RequestReminder] = new(
            "ማስታወሻ: መልስዎ ያስፈልጋል",
            "[የ{qty} {crop} ]ትዕዛዙ መልስዎን እየጠበቀ ነው።[ እባክዎ እስከ {deadline} ይመልሱ።]"),
        [NotificationTypes.PickupOverdue] = new(
            "ሰብሉ በጊዜው አልተነሳም",
            "[የ{qty} {crop} ]ትዕዛዝዎ በጊዜው አልተነሳም። አማራጮችዎን ለማየት መተግበሪያውን ይክፈቱ፤ ገንዘብዎን ሙሉ በሙሉ ማስመለስ ይችላሉ።"),
        [NotificationTypes.PickupMissed] = new(
            "የመጫኛ ጊዜው አልፏል",
            "[የ{qty} {crop} ]ትዕዛዙ በጊዜው አልተነሳም። ገዢው አሁን ትዕዛዙን ሰርዞ ገንዘቡን ማስመለስ ይችላል።"),
        [NotificationTypes.DeliveryScheduled] = new(
            "ማጓጓዣ ተዘጋጅቷል",
            "የጭነት መኪና ሹፌር {trucker}[ የ{qty} {crop}] ትዕዛዙን ያጓጉዛሉ።[ የሚነሳበት ቀን: {pickup}።]"),
        [NotificationTypes.BuyerCollecting] = new(
            "ገዢው ሊወስዱ ይመጣሉ",
            "ገዢ {buyer}[ የ{qty} {crop}] ትዕዛዙን ከእርስዎ ይወስዳሉ። እባክዎ ሰብሉን ያዘጋጁ።"),
        [NotificationTypes.PickedUp] = new(
            "ሰብሉ ተነስቷል",
            "[የ{qty} {crop} ]ትዕዛዝዎ ከገበሬው ተነስቷል።"),
        [NotificationTypes.HandoffRecorded] = new(
            "ርክክቡ ተመዝግቧል",
            "[የ{qty} {crop} ]ርክክቡ ተመዝግቧል። ገዢው ሰብሉን ከተረከበ በኋላ ይከፈልዎታል።"),
        [NotificationTypes.InTransit] = new(
            "በመንገድ ላይ ነው",
            "[የ{qty} {crop} ]ትዕዛዝዎ ወደ እርስዎ በመንገድ ላይ ነው።"),
        [NotificationTypes.TruckNearby] = new(
            "የጭነት መኪናው ቀርቧል",
            "የጭነት መኪና ሹፌር {trucker} ወደ እርስዎ ቀርበዋል[፤ {km} ይቀራቸዋል][፤ በ{minutes} ደቂቃ ገደማ ይደርሳሉ]። እባክዎ ሰብሉን ለመረከብ ይዘጋጁ።"),
        [NotificationTypes.Delivered] = new(
            "ትዕዛዝዎ ደርሷል",
            "[የ{qty} {crop} ]ትዕዛዝዎ ደርሷል። ችግር ካለ[ እስከ {deadline}] ያሳውቁ፤ ካልሆነ ገበሬው ይከፈላቸዋል።"),
        [NotificationTypes.DeliveredPayoutPending] = new(
            "ደርሷል፤ ክፍያዎ ይመጣል",
            "[የ{qty} {crop} ]ትዕዛዙ ደርሷል። የገዢው የ48 ሰዓት ማረጋገጫ ካለቀ በኋላ ክፍያዎ ይላካል።"),
        [NotificationTypes.OrderSettled] = new(
            "ትዕዛዙ ተጠናቋል",
            "[የ{qty} {crop} ]ትዕዛዙ ተጠናቋል፤ ሁሉም ተከፍሏቸዋል። እናመሰግናለን።"),
        [NotificationTypes.RefundSent] = new(
            "ገንዘብዎ ተመልሷል",
            "ተመላሽ ገንዘብዎ[ {amount}] ተልኮልዎታል።"),
        [NotificationTypes.PayoutSent] = new(
            "ክፍያ ተልኳል",
            "ክፍያዎ[ {amount}] ተልኮልዎታል። እናመሰግናለን።"),
        [NotificationTypes.PayoutBlocked] = new(
            "ክፍያዎ ቆሟል",
            "ክፍያዎ[ {amount}] እየጠበቀ ነው። {hint}"),
        [NotificationTypes.PayoutProcessing] = new(
            "ክፍያዎ እየተሰራ ነው",
            "ቡድናችን ክፍያዎን[ {amount}] በእጅ እየላከ ነው። ጥቂት ቀናት ሊወስድ ይችላል።"),
        [NotificationTypes.DisputeOpened] = new(
            "ችግር ሪፖርት ተደርጓል",
            "[የ{qty} {crop} ]ትዕዛዝዎ ላይ ችግር ሪፖርት ተደርጓል። ቡድናችን እስኪወስን ድረስ ገንዘቡ ተይዞ ይቆያል።"),
        [NotificationTypes.DisputeReceived] = new(
            "ሪፖርትዎ ደርሶናል",
            "ሪፖርትዎ ደርሶናል። ቡድናችን እስኪወስን ድረስ ገንዘቡ ተይዞ ይቆያል።"),
        [NotificationTypes.DisputeResolved] = new(
            "ውሳኔ ተሰጥቷል",
            "ቡድናችን ሪፖርት በተደረገው ችግር ላይ ወስኗል። {decision}"),
        [NotificationTypes.UnderReview] = new(
            "ክፍያው እየተገመገመ ነው",
            "የትዕዛዝዎ ክፍያ በቡድናችን እየተመረመረ ነው። ሲጠናቀቅ እናሳውቅዎታለን።")
    };

    private static readonly Dictionary<string, Template> Om = new(StringComparer.Ordinal)
    {
        [NotificationTypes.PaymentConfirmed] = new(
            "Kaffaltiin gaheera",
            "Kaffaltiin keessan[ {amount}] nageenyaan qabameera.[ Ajaja: {crop} {qty}.] Yeroo qonnaan bulaan deebii kennu isinitti himna."),
        [NotificationTypes.OrderRequest] = new(
            "Ajaja haaraa[: {crop} {qty}]",
            "Bitataan {buyer}[ {amount}] kaffaleera, nageenyaan qabameera. Maaloo[ hanga {deadline} tti] fudhadhaa ykn didaa."),
        [NotificationTypes.FarmerAccepted] = new(
            "Qonnaan bulaan fudhateera",
            "Qonnaan bulaan {farmer} ajaja keessan[ {crop} {qty}] fudhateera. Amma konkolaataa fe'aa gaafachaa jirra."),
        [NotificationTypes.TransportRequest] = new(
            "Gaaffii geejjibaa[: {crop} {qty}]",
            "Qonnaan bulaan {farmer} ajaja imala keessanii fudhateera. Maaloo[ hanga {deadline} tti] fudhadhaa ykn didaa."),
        [NotificationTypes.OrderConfirmed] = new(
            "Ajajni mirkanaa'eera",
            "Qaamoleen hundi ajaja[ {crop} {qty}] irratti waliigalaniiru.[ Guyyaa fudhannaa: {pickup}.]"),
        [NotificationTypes.OrderRejected] = new(
            "Ajajni didameera",
            "Qonnaan bulaan {farmer} ajaja keessan[ {crop} {qty}] hin fudhanne. Maallaqni keessan isiniif deebi'a."),
        [NotificationTypes.TransportFailed] = new(
            "Geejjibni hin argamne",
            "Konkolaataan ajaja keessan fe'uu hin danda'u. Maaloo[ hanga {deadline} tti] murteessaa. Yoo haqxan maallaqni guutuun deebi'a."),
        [NotificationTypes.TransportFailedKeepCrops] = new(
            "Geejjibni addaan citeera",
            "Konkolaataan fe'aa ajaja[ {crop} {qty}] dhiiseera. Hanga bitataan murteessutti maaloo midhaan qabadhaa."),
        [NotificationTypes.RequestExpired] = new(
            "Gaaffiin cufameera",
            "Yeroon deebii waan darbeef gaaffiin ajaja[ {crop} {qty}] cufameera."),
        [NotificationTypes.RequestOnHold] = new(
            "Gaaffiin yeroof dhaabateera",
            "Ajaja[ {crop} {qty}] geejjibni hin argamne, kanaaf bitataan murteessaa jira. Amma deebii kennuun isin hin barbaachisu."),
        [NotificationTypes.OrderCancelled] = new(
            "Ajajni haqameera",
            "Ajajni[ {crop} {qty}] haqameera. Maallaqni qabame yoo jiraate bitataaf deebi'a."),
        [NotificationTypes.OrderExpiredUnpaid] = new(
            "Ajajni cufameera",
            "Ajajni keessan[ {crop} {qty}] yeroodhaan waan hin kaffalamneef cufameera. Ajaja haaraa galchuu dandeessu."),
        [NotificationTypes.PaymentFailed] = new(
            "Kaffaltiin hin milkoofne",
            "Kaffaltiin keessan[ {amount}] hin milkoofne. Maaloo appii keessatti irra deebi'aa yaalaa."),
        [NotificationTypes.RequestReminder] = new(
            "Yaadachiisa: deebiin keessan barbaachisa",
            "Ajajni[ {crop} {qty}] deebii keessan eegaa jira.[ Maaloo hanga {deadline} tti deebisaa.]"),
        [NotificationTypes.PickupOverdue] = new(
            "Midhaan yeroodhaan hin fudhatamne",
            "Ajajni keessan[ {crop} {qty}] yeroodhaan hin fudhatamne. Appii banaa: ajaja haqxanii maallaqa guutuu deebifachuu dandeessu."),
        [NotificationTypes.PickupMissed] = new(
            "Fudhannaan darbeera",
            "Ajajni[ {crop} {qty}] yeroodhaan hin fudhatamne. Bitataan amma ajaja haqee maallaqa deebifachuu danda'a."),
        [NotificationTypes.DeliveryScheduled] = new(
            "Geejjibni qophaa'eera",
            "Konkolaataan fe'aa {trucker} ajaja[ {crop} {qty}] ni geessa.[ Guyyaa fudhannaa: {pickup}.]"),
        [NotificationTypes.BuyerCollecting] = new(
            "Bitataan dhufaa jira",
            "Bitataan {buyer} ajaja[ {crop} {qty}] isin biraa ni fudhata. Maaloo midhaan qopheessaa."),
        [NotificationTypes.PickedUp] = new(
            "Midhaan fudhatameera",
            "Ajajni keessan[ {crop} {qty}] qonnaan bulaa biraa fudhatameera."),
        [NotificationTypes.HandoffRecorded] = new(
            "Kenniinsi galmeeffameera",
            "Kenniinsi ajaja[ {crop} {qty}] galmeeffameera. Erga bitataan midhaan fudhatee booda isiniif ni kaffalama."),
        [NotificationTypes.InTransit] = new(
            "Daandii irra jira",
            "Ajajni keessan[ {crop} {qty}] gara keessan daandii irra jira."),
        [NotificationTypes.TruckNearby] = new(
            "Konkolaataan dhihaateera",
            "Konkolaataan fe'aa {trucker} dhihaateera[, {km} hafa][, gara daqiiqaa {minutes} keessatti ga'a]. Midhaan fudhachuuf qophaa'aa."),
        [NotificationTypes.Delivered] = new(
            "Ajajni ga'eera",
            "Ajajni[ {crop} {qty}] ga'eera. Rakkoon yoo jiraate[ hanga {deadline} tti] gabaasaa; yoo hin jiraanne qonnaan bulaaf kaffalama."),
        [NotificationTypes.DeliveredPayoutPending] = new(
            "Ga'eera, kaffaltiin ni dhufa",
            "Ajajni[ {crop} {qty}] ga'eera. Mirkaneessa bitataa sa'aatii 48 booda kaffaltiin keessan ni ergama."),
        [NotificationTypes.OrderSettled] = new(
            "Ajajni xumurameera",
            "Ajajni[ {crop} {qty}] xumurameera; hundi kaffalameera. Galatoomaa."),
        [NotificationTypes.RefundSent] = new(
            "Maallaqni deebi'eera",
            "Maallaqni keessan[ {amount}] isiniif deebi'eera."),
        [NotificationTypes.PayoutSent] = new(
            "Kaffaltiin ergameera",
            "Kaffaltiin keessan[ {amount}] isiniif ergameera. Galatoomaa."),
        [NotificationTypes.PayoutBlocked] = new(
            "Kaffaltiin dhaabateera",
            "Kaffaltiin keessan[ {amount}] eegaa jira. {hint}"),
        [NotificationTypes.PayoutProcessing] = new(
            "Kaffaltiin hojjetamaa jira",
            "Gareen keenya kaffaltii keessan[ {amount}] harkaan ergaa jira. Guyyoota muraasa fudhachuu danda'a."),
        [NotificationTypes.DisputeOpened] = new(
            "Rakkoon gabaafameera",
            "Ajaja keessan[ {crop} {qty}] irratti rakkoon gabaafameera. Hanga gareen keenya murteessutti maallaqni qabamee tura."),
        [NotificationTypes.DisputeReceived] = new(
            "Gabaasni keessan nu gaheera",
            "Gabaasni keessan nu gaheera. Hanga gareen keenya murteessutti maallaqni qabamee tura."),
        [NotificationTypes.DisputeResolved] = new(
            "Murteen kennameera",
            "Gareen keenya rakkoo gabaafame irratti murteesseera. {decision}"),
        [NotificationTypes.UnderReview] = new(
            "Kaffaltiin sakatta'amaa jira",
            "Kaffaltiin ajaja keessanii gareen keenyaan sakatta'amaa jira. Yeroo xumuramu isinitti himna.")
    };
}

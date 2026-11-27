using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-19: the AI advisory reviewer's configuration (fraud-detection-architecture.md section 21). Every value has a
/// default; the kill switch is off unless <c>FRAUD_AI_REVIEW_ENABLED</c> is <c>true</c>.
/// </summary>
internal sealed record FraudAdvisorySettings(
    bool Enabled,
    string ReviewerFunction,
    int MaxReviewsPerRun,
    int LookbackHours,
    int MaxCandidates = FraudAdvisorySettings.DefaultMaxCandidates)
{
    internal const int DefaultMaxReviewsPerRun = 5;
    internal const int MaxReviewsPerRunCap = 20;
    internal const int DefaultLookbackHours = 2;
    internal const int LookbackHoursCap = 24;

    /// <summary>Distinct users scored per run, at most (one bounded Query each).</summary>
    internal const int DefaultMaxCandidates = 50;

    public static FraudAdvisorySettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>
    /// The kill switch is on only for the exact value <c>true</c> (any case). A count that is not an integer in
    /// range keeps its default, so a typo cannot make one run review hundreds of users.
    /// </summary>
    internal static FraudAdvisorySettings From(Func<string, string?> env) => new(
        string.Equals(env("FRAUD_AI_REVIEW_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
        env("FRAUD_AI_REVIEWER_FUNCTION") ?? "",
        Read(env, "FRAUD_AI_REVIEW_MAX_PER_RUN", DefaultMaxReviewsPerRun, 1, MaxReviewsPerRunCap),
        Read(env, "FRAUD_AI_REVIEW_LOOKBACK_HOURS", DefaultLookbackHours, 1, LookbackHoursCap));

    private static int Read(Func<string, string?> env, string name, int fallback, int min, int max) =>
        int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>
/// FRD-19 (fraud-detection-architecture.md section 21): the pure part of the AI advisory review. Who is reviewed,
/// the redacted timeline the reviewer is sent, the checks on its answer, and the row it leaves. No I/O, so every
/// rule is testable without DynamoDB or Bedrock.
/// </summary>
internal static partial class FraudAdvisory
{
    internal const string RunToken = "FRAUD_AI_REVIEW_RUN";
    internal const string OffToken = "FRAUD_AI_REVIEW_OFF";
    internal const string UnconfiguredToken = "FRAUD_AI_REVIEW_UNCONFIGURED";
    internal const string FailedToken = "FRAUD_AI_REVIEW_FAILED";
    internal const string WrittenToken = "FRAUD_AI_REVIEW_WRITTEN";

    /// <summary>The concern levels the reviewer may answer, lowest first.</summary>
    internal static readonly string[] Concerns = { "none", "low", "medium", "high" };

    /// <summary>Longest explanation kept, in characters (the admin reads it on the user's group).</summary>
    internal const int MaxExplanationLength = 600;

    internal const int MaxSignals = 50;
    internal const int MaxEntities = 8;
    internal const int MaxEventsPerEntity = 20;
    internal const int MaxEvents = 100;

    /// <summary>
    /// FUP-145: most entries of the label map kept on the advisory row (<c>aiLabels</c>). The timeline labels at
    /// most <see cref="MaxSignals"/> signal entities and <see cref="MaxEntities"/> event entities, so the cap is
    /// never reached; it bounds the row all the same.
    /// </summary>
    internal const int MaxLabels = 60;

    /// <summary>FUP-145: the longest real id kept in the label map; a longer one is left out, never cut.</summary>
    internal const int MaxLabelIdLength = 128;

    /// <summary>The entity kinds whose transaction audit events are read for the timeline.</summary>
    internal static readonly string[] EventEntityTypes = { "order", "delivery", "escrow" };

    /// <summary>
    /// A user is reviewed only when the rules alone put them at HIGH or above: the enforcement score (no advisory
    /// weight) reaches <c>RISK_TIER_HIGH</c>, or the tier is CRITICAL (which never rests on advisory weight). So an
    /// earlier advisory can never be the reason for the next review.
    /// </summary>
    internal static bool RulesPutAtHighOrAbove(RiskScoreResult score, RiskScoreSettings settings) =>
        score.Source == RiskScoreSource.Computed
        && RiskScoreCalculator.RulesTier(score, settings) is RiskTier.High or RiskTier.Critical;

    /// <summary>
    /// The weight of one advisory row: <c>none</c> 0, <c>low</c> 3, <c>medium</c> 6, <c>high</c> 10, and never
    /// more than <c>RISK_ADVISORY_CAP</c>. Never negative: AI can add a step-up, never lower a score.
    /// </summary>
    internal static int WeightFor(string concern, int advisoryCap) =>
        Math.Max(0, Math.Min(advisoryCap, concern switch
        {
            "high" => 10,
            "medium" => 6,
            "low" => 3,
            _ => 0
        }));

    /// <summary>
    /// The advisory row's key: the start of the UTC day, so a user gets at most one review a day and a replay
    /// (a second run, a retried invocation) meets the conditional put. The score counts it like any row.
    /// </summary>
    internal static string DayKey(DateTime nowUtc)
    {
        var day = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        return $"{day.ToString("o", CultureInfo.InvariantCulture)}#{FraudDetectionService.AdvisorySignalType}";
    }

    /// <summary>
    /// The signal entities whose transaction audit events go into the timeline: orders, deliveries and escrows,
    /// newest signal first, at most <see cref="MaxEntities"/>.
    /// </summary>
    internal static List<(string EntityType, string EntityId)> EventEntities(
        IEnumerable<Dictionary<string, AttributeValue>> signalRows)
    {
        var entities = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in signalRows.Where(IsReviewedSignalRow))
        {
            var type = Str(row, "entityType");
            var id = Str(row, "entityId");
            if (type == null || string.IsNullOrEmpty(id) || !EventEntityTypes.Contains(type) || !seen.Add(id))
            {
                continue;
            }

            entities.Add((type, id));
            if (entities.Count == MaxEntities)
            {
                break;
            }
        }

        return entities;
    }

    /// <summary>
    /// The redacted timeline. Signal rows (newest first, the window only, no credit, whitelist or earlier advisory
    /// rows) keep their time, type, class, weight and review state; the transaction audit events of their entities
    /// keep their time, action, states and whether the reviewed user, another user, an admin or the system acted.
    /// Every id becomes a label such as <c>order-1</c>; descriptions, metadata, IP addresses, amounts and every
    /// free-text field are never read into it. Server values are cut to <c>[A-Za-z0-9_.:-]</c>.
    /// </summary>
    internal static AdvisoryReviewRequest BuildTimeline(
        string userId,
        Dictionary<string, AttributeValue>? userRow,
        IReadOnlyList<Dictionary<string, AttributeValue>> signalRows,
        bool signalsTruncated,
        IReadOnlyList<(string EntityType, string EntityId, IReadOnlyList<Dictionary<string, AttributeValue>> Events)> events,
        bool eventsTruncated,
        RiskScoreResult score,
        RiskScoreSettings riskSettings,
        DateTime nowUtc) =>
        BuildTimeline(
            userId, userRow, signalRows, signalsTruncated, events, eventsTruncated, score, riskSettings, nowUtc, out _);

    /// <summary>
    /// The redacted timeline, as the overload above builds it,
    /// and, apart from the request, the label map (FUP-145): which real id each label of this timeline stands for
    /// (<c>order-1</c> to the order id). The map is for the advisory row only, so an admin can tell what the
    /// explanation cites. It is never part of the request, so it never reaches the reviewer or the model.
    /// </summary>
    internal static AdvisoryReviewRequest BuildTimeline(
        string userId,
        Dictionary<string, AttributeValue>? userRow,
        IReadOnlyList<Dictionary<string, AttributeValue>> signalRows,
        bool signalsTruncated,
        IReadOnlyList<(string EntityType, string EntityId, IReadOnlyList<Dictionary<string, AttributeValue>> Events)> events,
        bool eventsTruncated,
        RiskScoreResult score,
        RiskScoreSettings riskSettings,
        DateTime nowUtc,
        out IReadOnlyDictionary<string, string> labelMap)
    {
        var labels = new EntityLabels();
        var since = nowUtc.AddDays(-riskSettings.WindowDays).ToString("o", CultureInfo.InvariantCulture);

        var kept = signalRows
            .Where(IsReviewedSignalRow)
            .Where(row => string.CompareOrdinal(Str(row, "signalKey"), since) >= 0)
            .ToList();
        var signals = kept.Take(MaxSignals).Select(row =>
        {
            var type = SignalType(row);
            var entityId = Str(row, "entityId");
            return new AdvisorySignal
            {
                At = Time(Str(row, "createdAt")) ?? Time(KeyTime(Str(row, "signalKey"))) ?? "unknown",
                Type = Token(type),
                Class = FraudDetectionService.IsSoftSignal(type) ? "soft" : "hard",
                Weight = (int)Math.Clamp(Num(row, "weight") ?? FraudDetectionService.SignalWeight(type), 0, 1000),
                Status = Str(row, "status") == "reviewed" ? "reviewed" : "pending",
                Resolution = Str(row, "resolution") is { Length: > 0 } resolution ? Token(resolution) : null,
                Entity = string.IsNullOrEmpty(entityId) ? null : labels.Of(Str(row, "entityType"), entityId)
            };
        }).ToList();

        var allEvents = new List<AdvisoryEvent>();
        foreach (var (entityType, entityId, rows) in events)
        {
            var label = labels.Of(entityType, entityId);
            allEvents.AddRange(rows.Take(MaxEventsPerEntity).Select(row => new AdvisoryEvent
            {
                At = Time(Str(row, "timestamp")) ?? "unknown",
                Entity = label,
                EntityType = Token(Str(row, "entityType") ?? entityType),
                Action = Token(Str(row, "action")),
                From = Str(row, "fromState") is { Length: > 0 } from ? Token(from) : null,
                To = Str(row, "toState") is { Length: > 0 } to ? Token(to) : null,
                Actor = ActorOf(Str(row, "actor"), Str(row, "actorRole"), userId),
                ActorRole = Str(row, "actorRole") is { Length: > 0 } role ? Token(role) : null
            }));
        }

        var ordered = allEvents.OrderBy(e => e.At, StringComparer.Ordinal).ToList();
        if (ordered.Count > MaxEvents)
        {
            ordered = ordered.Skip(ordered.Count - MaxEvents).ToList();
            eventsTruncated = true;
        }

        labelMap = labels.Map();
        return new AdvisoryReviewRequest
        {
            Subject = Subject(userRow, score, nowUtc),
            Signals = signals,
            Events = ordered,
            WindowDays = riskSettings.WindowDays,
            SignalsTruncated = signalsTruncated || kept.Count > MaxSignals,
            EventsTruncated = eventsTruncated
        };
    }

    /// <summary>
    /// Checks the reviewer's answer before anything is written: it must be <c>ok</c>, with a known concern and a
    /// non-empty explanation. The explanation is cleaned (<see cref="CleanExplanation"/>). A refused answer gives
    /// the reason for the log line, a server value.
    /// </summary>
    internal static bool TryReadVerdict(
        AdvisoryReviewReply? reply, out string concern, out string explanation, out string reason)
    {
        concern = "";
        explanation = "";
        if (reply == null)
        {
            reason = "empty_reply";
            return false;
        }

        if (!reply.Ok)
        {
            reason = reply.Error is { Length: > 0 } error ? Token(error) : "not_ok";
            return false;
        }

        if (reply.Concern == null || !Concerns.Contains(reply.Concern))
        {
            reason = "bad_concern";
            return false;
        }

        explanation = CleanExplanation(reply.Explanation);
        if (explanation.Length == 0)
        {
            reason = "empty_explanation";
            return false;
        }

        concern = reply.Concern;
        reason = "";
        return true;
    }

    /// <summary>
    /// The model's explanation as the admin will read it: control and format characters (bidirectional overrides
    /// included) dropped, whitespace collapsed to single spaces, at most <see cref="MaxExplanationLength"/>
    /// characters, and any run of 9 or more digits (a phone number or an id the model made up) replaced.
    /// </summary>
    internal static string CleanExplanation(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxExplanationLength));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length >= MaxExplanationLength)
            {
                break;
            }
        }

        return LongDigitRun().Replace(builder.ToString(), "[number]").Trim();
    }

    /// <summary>
    /// FUP-145: whether <paramref name="label"/> has the shape of a timeline label: a known kind, a hyphen and a
    /// number of at most 4 digits (<c>order-1</c>). Checked when the map is read back from a row.
    /// </summary>
    internal static bool IsEntityLabel(string? label) => label != null && EntityLabel().IsMatch(label);

    /// <summary>FUP-145: the kind in a timeline label (<c>order</c> of <c>order-1</c>).</summary>
    internal static string LabelKind(string label) => label[..label.LastIndexOf('-')];

    /// <summary>FUP-145: the number in a timeline label (1 of <c>order-1</c>), for the order of the list.</summary>
    internal static int LabelNumber(string label) =>
        int.Parse(label[(label.LastIndexOf('-') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The model id kept on the row, as a server value.</summary>
    internal static string? ModelLabel(string? model) => string.IsNullOrEmpty(model) ? null : Token(model, 96);

    /// <summary>A server value for the timeline or a log line: <c>[A-Za-z0-9_.:-]</c>, cut at 48 characters.</summary>
    internal static string Token(string? value, int maxLength = 48)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var token = new string(value
            .Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or ':')
            .Take(maxLength)
            .ToArray());
        return token.Length == 0 ? "-" : token;
    }

    /// <summary>
    /// A signal row the reviewer is shown: a signal (no <c>recordType</c>, or <c>signal</c>) that is not an earlier
    /// advisory, so AI output never feeds the next review. Credit and whitelist rows are left out.
    /// </summary>
    private static bool IsReviewedSignalRow(Dictionary<string, AttributeValue> row) =>
        Str(row, "recordType") is null or FraudDetectionService.RecordTypeSignal
        && Str(row, "signalKey") is { Length: > 0 }
        && SignalType(row) != FraudDetectionService.AdvisorySignalType;

    private static AdvisorySubject Subject(Dictionary<string, AttributeValue>? userRow, RiskScoreResult score, DateTime nowUtc)
    {
        var role = userRow == null ? null : Str(userRow, "role");
        int? ageDays = null;
        if (userRow != null
            && DateTime.TryParse(Str(userRow, "createdAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
        {
            ageDays = Math.Max(0, (int)(nowUtc - created).TotalDays);
        }

        bool? fayda = userRow != null && userRow.TryGetValue("faydaVerified", out var value) && value.IsBOOLSet
            ? value.BOOL
            : null;

        return new AdvisorySubject
        {
            Role = role is "farmer" or "trucker" or "buyer" ? role : "unknown",
            AccountAgeDays = ageDays,
            FaydaVerified = fayda,
            RiskTier = score.Tier == RiskTier.Critical ? "critical" : "high",
            Score = score.Score,
            EnforcementScore = score.EnforcementScore,
            ScoreTruncated = score.Truncated
        };
    }

    /// <summary>
    /// Who acted, without saying who: the reviewed user (<c>self</c>), an admin, the system (no actor,
    /// <c>system</c> or the payment gateway) or another user (<c>other</c>).
    /// </summary>
    private static string ActorOf(string? actor, string? actorRole, string userId)
    {
        if (!string.IsNullOrEmpty(actor) && string.Equals(actor, userId, StringComparison.Ordinal))
        {
            return "self";
        }

        if (string.Equals(actorRole, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return "admin";
        }

        return string.IsNullOrEmpty(actor) || actor is "system" or "telebirr" ? "system" : "other";
    }

    private static string SignalType(Dictionary<string, AttributeValue> row)
    {
        if (Str(row, "signalType") is { Length: > 0 } type)
        {
            return type;
        }

        var key = Str(row, "signalKey") ?? "";
        var separator = key.IndexOf('#');
        return separator >= 0 ? key[(separator + 1)..] : "";
    }

    private static string? KeyTime(string? signalKey)
    {
        if (signalKey == null)
        {
            return null;
        }

        var separator = signalKey.IndexOf('#');
        return separator > 0 ? signalKey[..separator] : signalKey;
    }

    /// <summary>An ISO time as <c>yyyy-MM-ddTHH:mm:ssZ</c>; null when it does not parse.</summary>
    private static string? Time(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : null;

    private static string? Str(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value) ? value.S : null;

    private static long? Num(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value)
        && long.TryParse(value.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    [GeneratedRegex(@"\d{9,}")]
    private static partial Regex LongDigitRun();

    [GeneratedRegex(@"^(order|delivery|escrow|crop|trip|user|entity)-[0-9]{1,4}$")]
    private static partial Regex EntityLabel();

    /// <summary>Pseudonymous entity labels, numbered per kind in first-appearance order, for one request.</summary>
    private sealed class EntityLabels
    {
        private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Of(string? entityType, string entityId)
        {
            if (_labels.TryGetValue(entityId, out var label))
            {
                return label;
            }

            var kind = entityType is "order" or "delivery" or "escrow" or "crop" or "trip" or "user" ? entityType : "entity";
            var next = _counts.GetValueOrDefault(kind) + 1;
            _counts[kind] = next;
            label = $"{kind}-{next.ToString(CultureInfo.InvariantCulture)}";
            _labels[entityId] = label;
            return label;
        }

        /// <summary>
        /// FUP-145: label to real id, in first-appearance order, at most <see cref="MaxLabels"/> entries; an id
        /// longer than <see cref="MaxLabelIdLength"/> is left out.
        /// </summary>
        public IReadOnlyDictionary<string, string> Map()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (entityId, label) in _labels)
            {
                if (map.Count == MaxLabels)
                {
                    break;
                }

                if (entityId.Length <= MaxLabelIdLength)
                {
                    map[label] = entityId;
                }
            }

            return map;
        }
    }
}

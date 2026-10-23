namespace WeredaConnect.Server.Services;

using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

/// <summary>
/// Risk score configuration (fraud-detection-architecture.md section 14.5). The defaults are the
/// constants on <see cref="FraudDetectionService"/>; each environment variable overrides one.
/// </summary>
internal sealed record RiskScoreSettings(
    int WindowDays,
    int TierMedium,
    int TierHigh,
    int TierCritical,
    int CreditCap,
    int AdvisoryCap,
    int MaxPages,
    int RejectTypeCap = FraudDetectionService.DefaultRiskRejectTypeCap)
{
    public static RiskScoreSettings Default { get; } = new(
        FraudDetectionService.DefaultRiskWindowDays,
        FraudDetectionService.DefaultRiskTierMedium,
        FraudDetectionService.DefaultRiskTierHigh,
        FraudDetectionService.DefaultRiskTierCritical,
        FraudDetectionService.DefaultRiskCreditCap,
        FraudDetectionService.DefaultRiskAdvisoryCap,
        FraudDetectionService.DefaultRiskMaxPages,
        FraudDetectionService.DefaultRiskRejectTypeCap);

    public static RiskScoreSettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>
    /// A value that is not an integer in range keeps its default. The window is capped at
    /// <see cref="FraudDetectionService.SignalTtlDays"/>, since the table's TTL deletes older rows
    /// anyway, and the page count at <see cref="FraudDetectionService.RiskMaxPagesCap"/>, so a
    /// typo cannot turn one score into a long read. Tier bounds that are not strictly increasing
    /// all fall back, so one typo cannot reorder the tiers.
    /// </summary>
    internal static RiskScoreSettings From(Func<string, string?> env)
    {
        var window = Math.Min(Read(env, "RISK_WINDOW_DAYS", Default.WindowDays, min: 1), FraudDetectionService.SignalTtlDays);
        var medium = Read(env, "RISK_TIER_MEDIUM", Default.TierMedium, min: 1);
        var high = Read(env, "RISK_TIER_HIGH", Default.TierHigh, min: 1);
        var critical = Read(env, "RISK_TIER_CRITICAL", Default.TierCritical, min: 1);
        if (!(medium < high && high < critical))
        {
            (medium, high, critical) = (Default.TierMedium, Default.TierHigh, Default.TierCritical);
        }

        return new RiskScoreSettings(
            window,
            medium,
            high,
            critical,
            Read(env, "RISK_CREDIT_CAP", Default.CreditCap, min: 0),
            Read(env, "RISK_ADVISORY_CAP", Default.AdvisoryCap, min: 0),
            Math.Min(Read(env, "RISK_MAX_PAGES", Default.MaxPages, min: 1), FraudDetectionService.RiskMaxPagesCap),
            Read(env, "RISK_REJECT_TYPE_CAP", Default.RejectTypeCap, min: 0));
    }

    private static int Read(Func<string, string?> env, string name, int fallback, int min) =>
        int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min
            ? value
            : fallback;
}

/// <summary>
/// The score of section 14.2 over one user's fraud-signals rows, fed newest first as the Query
/// returns them. No I/O, so every rule is testable without DynamoDB.
/// </summary>
internal sealed class RiskScoreCalculator
{
    private readonly RiskScoreSettings _settings;
    private readonly DateTime _nowUtc;
    private readonly HashSet<string> _countedBuckets = new(StringComparer.Ordinal);

    // FRD-08: weight counted per refused entity, which a later-read approved row of that entity can
    // still clear, and the entities an approved row has cleared.
    private readonly Dictionary<string, (string Type, long Weight)> _entityBuckets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clearedEntities = new(StringComparer.Ordinal);

    // FRD-08: counted weight of the capped reject types per type, all of it and the part no later
    // row can clear.
    private readonly Dictionary<string, long> _cappedWeight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cappedFixedWeight = new(StringComparer.Ordinal);

    // Counted weight outside the capped types: the part no later row can clear, and the part that
    // sits in entity buckets.
    private long _signalWeight;
    private long _clearableWeight;
    private long _advisoryWeight;
    private long _creditWeight;
    private bool _whitelistActive;

    public RiskScoreCalculator(RiskScoreSettings settings, DateTime nowUtc)
    {
        _settings = settings;
        _nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        // Same format as the writer's signalKey prefix (DateTime "o", UTC), so the string
        // comparison of the key condition is a time comparison.
        SinceKey = _nowUtc.AddDays(-settings.WindowDays).ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>Lower bound of the window on <c>signalKey</c>.</summary>
    public string SinceKey { get; }

    /// <summary>
    /// The counted non-credit, non-advisory weight reached RISK_TIER_CRITICAL + RISK_CREDIT_CAP.
    /// Credits cannot offset more than the cap, so no unread row can lower the tier below
    /// CRITICAL and the read may stop (section 14.3). Only weight no unread row can take back
    /// counts here: an FRD-08 entity bucket can still be cleared by an older approved row.
    /// </summary>
    public bool ReachedCritical =>
        _signalWeight + CappedSum(_cappedFixedWeight) >= (long)_settings.TierCritical + _settings.CreditCap;

    /// <summary>Rows read whose <c>recordType</c> the scorer does not know; counted as signals.</summary>
    public int UnknownRecordRows { get; private set; }

    /// <summary>The first unknown <c>recordType</c> read, for the log line.</summary>
    public string? FirstUnknownRecordType { get; private set; }

    public void Add(Dictionary<string, AttributeValue> row)
    {
        var signalKey = Str(row, "signalKey");
        if (signalKey == null)
        {
            return;
        }

        var recordType = Str(row, "recordType");
        switch (recordType)
        {
            case "whitelist":
                // Its key "~whitelist" sorts after every ISO key, so a newest-first read
                // meets it before any signal row.
                _whitelistActive = IsAfterNow(Str(row, "whitelistUntil"));
                return;
            case "credit":
                if (InWindow(signalKey))
                {
                    _creditWeight += Num(row, "weight") ?? 0;
                }

                return;
            case null or "signal":
                break; // rows without recordType are legacy signal rows
            default:
                // A row kind this scorer does not know counts as a signal (fails closed), and
                // the caller logs it, so a new writer the scorer was not taught about shows up.
                UnknownRecordRows++;
                FirstUnknownRecordType ??= recordType;
                break;
        }

        if (!InWindow(signalKey))
        {
            return;
        }

        var signalType = Str(row, "signalType") ?? TypeFromKey(signalKey);

        // FRD-08 reject types count once per refused entity over the window, since a stuck
        // operation is retried in every hour the client reconnects.
        var entityBucket = FraudDetectionService.CountsOncePerEntity(signalType)
            && Str(row, "entityId") is { Length: > 0 } entityId
                ? $"{signalType}|entity:{entityId}"
                : null;

        // Only an admin's approve (false positive) or whitelist clears a signal. A reject
        // confirms fraud and keeps counting; so does any other string the action handler
        // accepts today (section 19).
        if (Str(row, "resolution") is "approve" or "whitelist")
        {
            // FRD-08: an approved refusal clears its whole entity, newer rows of it included, so
            // approving one row of a stuck operation clears that operation.
            if (entityBucket != null && _clearedEntities.Add(entityBucket)
                && _entityBuckets.Remove(entityBucket, out var counted))
            {
                Count(counted.Type, -counted.Weight, clearable: true);
            }

            return;
        }

        if (_whitelistActive && FraudDetectionService.IsSoftSignal(signalType))
        {
            return;
        }

        // Burst dedup: an offline batch can produce many rejections of one type in one hour.
        // The first counted row read (the newest) takes the slot.
        if (entityBucket != null && _clearedEntities.Contains(entityBucket))
        {
            return;
        }

        if (!_countedBuckets.Add(entityBucket ?? $"{signalType}|{HourBucket(signalKey)}"))
        {
            return;
        }

        // Only credit rows may lower the score, and only within the cap.
        var weight = Math.Max(0, Num(row, "weight") ?? FraudDetectionService.SignalWeight(signalType));
        if (signalType == FraudDetectionService.AdvisorySignalType)
        {
            _advisoryWeight += weight;
            return;
        }

        if (entityBucket != null)
        {
            _entityBuckets[entityBucket] = (signalType, weight);
        }

        Count(signalType, weight, clearable: entityBucket != null);
    }

    private void Count(string signalType, long weight, bool clearable)
    {
        if (FraudDetectionService.IsCappedRejectType(signalType))
        {
            _cappedWeight[signalType] = _cappedWeight.GetValueOrDefault(signalType) + weight;
            if (!clearable)
            {
                _cappedFixedWeight[signalType] = _cappedFixedWeight.GetValueOrDefault(signalType) + weight;
            }
        }
        else if (clearable)
        {
            _clearableWeight += weight;
        }
        else
        {
            _signalWeight += weight;
        }
    }

    /// <summary>FRD-08: each capped reject type adds at most <c>RISK_REJECT_TYPE_CAP</c>.</summary>
    private long CappedSum(Dictionary<string, long> byType) =>
        byType.Values.Sum(w => Math.Min(w, (long)_settings.RejectTypeCap));

    public RiskScoreResult Result(bool truncated)
    {
        var credits = Math.Max(_creditWeight, -(long)_settings.CreditCap);
        var advisory = Math.Min(_advisoryWeight, _settings.AdvisoryCap);
        var signalWeight = _signalWeight + _clearableWeight + CappedSum(_cappedWeight);
        var score = Math.Max(0, signalWeight + advisory + credits);
        // CRITICAL never rests on AI weight (section 21).
        var enforcement = Math.Max(0, score - advisory);
        return new RiskScoreResult
        {
            Score = (int)Math.Min(score, int.MaxValue),
            EnforcementScore = (int)Math.Min(enforcement, int.MaxValue),
            Tier = TierFor(score, enforcement, truncated, _settings),
            Truncated = truncated
        };
    }

    /// <summary>Section 16; bounds are inclusive lower bounds.</summary>
    internal static RiskTier TierFor(long score, long enforcementScore, bool truncated, RiskScoreSettings settings) =>
        truncated || enforcementScore >= settings.TierCritical ? RiskTier.Critical
        : score >= settings.TierHigh ? RiskTier.High
        : score >= settings.TierMedium ? RiskTier.Medium
        : RiskTier.Low;

    // Same comparison as the key condition; repeated here so a row the Query should not have
    // returned never counts.
    private bool InWindow(string signalKey) => string.CompareOrdinal(signalKey, SinceKey) >= 0;

    private bool IsAfterNow(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
        && at > _nowUtc;

    private static string HourBucket(string signalKey)
    {
        var separator = signalKey.IndexOf('#');
        var iso = separator > 0 ? signalKey[..separator] : signalKey;
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture)
            : signalKey; // unparsable: a bucket of its own, so it is never deduplicated away
    }

    private static string TypeFromKey(string signalKey)
    {
        var separator = signalKey.IndexOf('#');
        return separator >= 0 ? signalKey[(separator + 1)..] : "";
    }

    private static string? Str(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value) ? value.S : null;

    private static long? Num(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value)
        && long.TryParse(value.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}

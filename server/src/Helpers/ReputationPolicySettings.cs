namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-28: the reputation policy numbers (reputation-architecture.md 5.9). They live in one SSM
/// String parameter, <c>/{prefix}/reputation/policy</c> (JSON), read once per Lambda container and
/// parsed with <see cref="JsonDocument"/> (AOT safe). A key that is missing, not a number or out of
/// range keeps its default and logs <c>REPUTATION_POLICY_INVALID key=&lt;k&gt;</c> (alarmed).
/// </summary>
public sealed record ReputationPolicySettings
{
    /// <summary>The log token the monitoring stack alarms on.</summary>
    public const string InvalidToken = "REPUTATION_POLICY_INVALID";

    /// <summary>The env var holding the SSM parameter name.</summary>
    public const string ParameterEnv = "REPUTATION_POLICY_PARAM";

    public static ReputationPolicySettings Defaults { get; } = new();

    public int DeclinePoints { get; init; } = 1;
    public int ExpiryPoints { get; init; } = 1;
    public int CancelPoints { get; init; } = 3;
    public int LateCancelPoints { get; init; } = 4;
    public int TripCancelPoints { get; init; } = 3;
    public int TripLateCancelPoints { get; init; } = 4;
    public int NoShowPoints { get; init; } = 5;
    public int OverduePoints { get; init; } = 5;
    public int LateCancelHours { get; init; } = 24;
    public int PointsLifeDays { get; init; } = 90;
    public int PairCapDays { get; init; } = 30;
    public int DailyCapPoints { get; init; } = 6;

    /// <summary>0 turns the short-window rule off.</summary>
    public int ExpiryMinWindowHours { get; init; } = 12;

    public int RequestPointsMax { get; init; } = 9;
    public int WarningAt { get; init; } = 3;
    public int LowAt { get; init; } = 6;
    public int RestrictAt { get; init; } = 10;
    public int RestrictionDays { get; init; } = 14;
    public int RepeatWindowDays { get; init; } = 365;
    public decimal RatingDeductionPerPoint { get; init; } = 0.1m;
    public decimal RatingDeductionMax { get; init; } = 1.0m;
    public int ReliabilityMinOrders { get; init; } = 3;
    public decimal EarnBackMinOrderEtb { get; init; } = 500m;
    public int EarnBackPer30Days { get; init; } = 2;
    public int EarnBackBuyerRepeatDays { get; init; } = 90;
    public int AppealWindowDays { get; init; } = 7;
    public int TagMaxDays { get; init; } = 90;
    public int AdminRestrictMaxDays { get; init; } = 365;
    public int BuyerDeclineSignalCount { get; init; } = 3;
    public int BuyerDeclineSignalDays { get; init; } = 30;
    public int EventsReadCap { get; init; } = 2000;

    /// <summary>
    /// The settings from the parameter's JSON. Each key that is missing, not a number or out of its
    /// range keeps its default and is reported through <paramref name="onInvalid"/> (the key name).
    /// Thresholds that break their order (<c>warningAt &lt; lowAt &lt; restrictAt</c>,
    /// <c>requestPointsMax &lt; restrictAt</c>) all fall back to their defaults.
    /// </summary>
    public static ReputationPolicySettings Parse(string? json, Action<string>? onInvalid = null)
    {
        var report = onInvalid ?? (_ => { });
        JsonElement root = default;
        var hasRoot = false;
        JsonDocument? doc = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    root = doc.RootElement;
                    hasRoot = true;
                }
            }
        }
        catch (JsonException)
        {
            hasRoot = false;
        }

        try
        {
            if (!hasRoot)
            {
                report("parameter");
                return Defaults;
            }

            var d = Defaults;
            int Int(string key, int fallback, int min, int max)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDecimal(out var number) && number == decimal.Truncate(number)
                    && number >= min && number <= max)
                {
                    return (int)number;
                }

                report(key);
                return fallback;
            }

            decimal Dec(string key, decimal fallback, decimal min, decimal max)
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDecimal(out var number) && number >= min && number <= max)
                {
                    return number;
                }

                report(key);
                return fallback;
            }

            var parsed = new ReputationPolicySettings
            {
                DeclinePoints = Int("declinePoints", d.DeclinePoints, 0, 10),
                ExpiryPoints = Int("expiryPoints", d.ExpiryPoints, 0, 10),
                CancelPoints = Int("cancelPoints", d.CancelPoints, 0, 10),
                LateCancelPoints = Int("lateCancelPoints", d.LateCancelPoints, 0, 10),
                TripCancelPoints = Int("tripCancelPoints", d.TripCancelPoints, 0, 10),
                TripLateCancelPoints = Int("tripLateCancelPoints", d.TripLateCancelPoints, 0, 10),
                NoShowPoints = Int("noShowPoints", d.NoShowPoints, 0, 20),
                OverduePoints = Int("overduePoints", d.OverduePoints, 0, 20),
                LateCancelHours = Int("lateCancelHours", d.LateCancelHours, 1, 168),
                PointsLifeDays = Int("pointsLifeDays", d.PointsLifeDays, 7, 365),
                PairCapDays = Int("pairCapDays", d.PairCapDays, 1, 365),
                DailyCapPoints = Int("dailyCapPoints", d.DailyCapPoints, 1, 50),
                ExpiryMinWindowHours = Int("expiryMinWindowHours", d.ExpiryMinWindowHours, 0, 24),
                RequestPointsMax = Int("requestPointsMax", d.RequestPointsMax, 1, 100),
                WarningAt = Int("warningAt", d.WarningAt, 1, 50),
                LowAt = Int("lowAt", d.LowAt, 2, 50),
                RestrictAt = Int("restrictAt", d.RestrictAt, 3, 100),
                RestrictionDays = Int("restrictionDays", d.RestrictionDays, 1, 90),
                RepeatWindowDays = Int("repeatWindowDays", d.RepeatWindowDays, 30, 730),
                RatingDeductionPerPoint = Dec("ratingDeductionPerPoint", d.RatingDeductionPerPoint, 0m, 1m),
                RatingDeductionMax = Dec("ratingDeductionMax", d.RatingDeductionMax, 0m, 4m),
                ReliabilityMinOrders = Int("reliabilityMinOrders", d.ReliabilityMinOrders, 1, 50),
                EarnBackMinOrderEtb = Dec("earnBackMinOrderEtb", d.EarnBackMinOrderEtb, 0m, 100_000m),
                EarnBackPer30Days = Int("earnBackPer30Days", d.EarnBackPer30Days, 0, 30),
                EarnBackBuyerRepeatDays = Int("earnBackBuyerRepeatDays", d.EarnBackBuyerRepeatDays, 0, 365),
                AppealWindowDays = Int("appealWindowDays", d.AppealWindowDays, 1, 30),
                TagMaxDays = Int("tagMaxDays", d.TagMaxDays, 1, 365),
                AdminRestrictMaxDays = Int("adminRestrictMaxDays", d.AdminRestrictMaxDays, 1, 730),
                BuyerDeclineSignalCount = Int("buyerDeclineSignalCount", d.BuyerDeclineSignalCount, 2, 50),
                BuyerDeclineSignalDays = Int("buyerDeclineSignalDays", d.BuyerDeclineSignalDays, 1, 365),
                EventsReadCap = Int("eventsReadCap", d.EventsReadCap, 100, 10_000)
            };

            if (parsed.WarningAt < parsed.LowAt && parsed.LowAt < parsed.RestrictAt
                && parsed.RequestPointsMax < parsed.RestrictAt)
            {
                return parsed;
            }

            foreach (var key in new[] { "warningAt", "lowAt", "restrictAt", "requestPointsMax" })
            {
                report(key);
            }

            return parsed with
            {
                WarningAt = d.WarningAt,
                LowAt = d.LowAt,
                RestrictAt = d.RestrictAt,
                RequestPointsMax = d.RequestPointsMax
            };
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static ReputationPolicySettings? _cached;

    /// <summary>
    /// The settings of this Lambda container: the SSM parameter named by
    /// <c>REPUTATION_POLICY_PARAM</c>, read once. An unset env, an unreadable parameter or bad JSON
    /// gives the defaults (logged with <see cref="InvalidToken"/>).
    /// </summary>
    public static ReputationPolicySettings LoadForContainer(Action<string> log)
    {
        if (_cached != null)
        {
            return _cached;
        }

        var name = Environment.GetEnvironmentVariable(ParameterEnv);
        string? json = null;
        if (string.IsNullOrEmpty(name))
        {
            log($"{InvalidToken} key=parameter reason=unset");
        }
        else
        {
            try
            {
                json = AwsParameterResolver.GetSecretOrParameterValue(name);
            }
            catch (Exception ex)
            {
                log($"{InvalidToken} key=parameter reason=unreadable [{ex.GetType().Name}]");
            }
        }

        var settings = json == null
            ? Defaults
            : Parse(json, key => log($"{InvalidToken} key={key}"));
        _cached = settings;
        return settings;
    }

    /// <summary>Test seam: forget the container's settings.</summary>
    internal static void ResetForTests() => _cached = null;

    /// <summary>The points life as a span.</summary>
    public TimeSpan PointsLife => TimeSpan.FromDays(PointsLifeDays);
}

/// <summary>ESC-28: the two reputation flags (reputation-api.md section 11), env vars set by the CDK.</summary>
public static class ReputationFlags
{
    public const string PenaltiesEnv = "REPUTATION_PENALTIES_ENABLED";
    public const string TimeoutPenaltiesEnv = "REPUTATION_TIMEOUT_PENALTIES_ENABLED";

    /// <summary><c>reputationPenalties</c>: the stream records events and the daily job runs. Off by default.</summary>
    public static bool PenaltiesEnabled => IsOn(PenaltiesEnv);

    public const string PublicTagsEnv = "REPUTATION_PUBLIC_TAGS_ENABLED";

    /// <summary>
    /// ESC-29, <c>reputationPublicTags</c>: buyers get the tier, the effective rating and the
    /// reliability (reputation-api.md sections 3 and 7, rating-api.md 1.2.0). Off by default.
    /// </summary>
    public static bool PublicTagsEnabled => IsOn(PublicTagsEnv);

    /// <summary><c>timeoutPenalties</c>: <c>RESPONSE_EXPIRED</c> carries its point. Off by default.</summary>
    public static bool TimeoutPenaltiesEnabled => IsOn(TimeoutPenaltiesEnv);

    private static bool IsOn(string env)
        => string.Equals(Environment.GetEnvironmentVariable(env), "true", StringComparison.OrdinalIgnoreCase);
}

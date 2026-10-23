using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-13: how long after delivery confirm a payee waits (fraud-detection-architecture.md section 17.5).
/// Each environment variable overrides one default and is read on every decision, like
/// <see cref="RiskScoreSettings"/>.
/// </summary>
internal sealed record PayoutDelaySettings(
    int TrustedHours,
    int NewHours,
    int FlaggedHours,
    int TrustedMinAccountDays,
    int TrustedMinCompleted)
{
    /// <summary>No delay can be longer, so a typo cannot freeze a payout for long.</summary>
    public const int MaxDelayHours = 168;

    /// <summary>Completed escrows are counted among the payee's newest escrows, one Query page.</summary>
    public const int CompletedReadLimit = 50;

    public static PayoutDelaySettings Default { get; } = new(24, 72, 72, 30, 3);

    public static PayoutDelaySettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>
    /// A value that is not an integer in range keeps its default: the delays from 0 to
    /// <see cref="MaxDelayHours"/>, the account age from 0 to 365 days, the completed count from 0 to
    /// <see cref="CompletedReadLimit"/> (0 skips the history read). Delays that are not ordered
    /// trusted, new, flagged all fall back, so a typo cannot give a trusted payee the longest wait.
    /// </summary>
    internal static PayoutDelaySettings From(Func<string, string?> env)
    {
        var trusted = Read(env, "PAYOUT_DELAY_TRUSTED_HOURS", Default.TrustedHours, 0, MaxDelayHours);
        var fresh = Read(env, "PAYOUT_DELAY_NEW_HOURS", Default.NewHours, 0, MaxDelayHours);
        var flagged = Read(env, "PAYOUT_DELAY_FLAGGED_HOURS", Default.FlaggedHours, 0, MaxDelayHours);
        if (!(trusted <= fresh && fresh <= flagged))
        {
            (trusted, fresh, flagged) = (Default.TrustedHours, Default.NewHours, Default.FlaggedHours);
        }

        return new PayoutDelaySettings(
            trusted,
            fresh,
            flagged,
            Read(env, "PAYOUT_TRUSTED_MIN_ACCOUNT_DAYS", Default.TrustedMinAccountDays, 0, 365),
            Read(env, "PAYOUT_TRUSTED_MIN_COMPLETED", Default.TrustedMinCompleted, 0, CompletedReadLimit));
    }

    public int HoursFor(PayoutDelayClass payee) => payee switch
    {
        PayoutDelayClass.Trusted => TrustedHours,
        PayoutDelayClass.New => NewHours,
        PayoutDelayClass.Flagged => FlaggedHours,
        _ => Math.Max(TrustedHours, Math.Max(NewHours, FlaggedHours))
    };

    private static int Read(Func<string, string?> env, string name, int fallback, int min, int max) =>
        int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>FRD-13: what a payee's data says about the wait. Server-internal, never returned to clients.</summary>
internal enum PayoutDelayClass
{
    /// <summary>Score 0 (no counted signal), an old enough account and enough completed escrows.</summary>
    Trusted,

    /// <summary>A computed score that is not trusted: a young account, too little history, or a score above 0 below HIGH.</summary>
    New,

    /// <summary>Risk tier HIGH or CRITICAL.</summary>
    Flagged,

    /// <summary>Not a computed score, or the payee's row could not be read: the longest delay.</summary>
    Unscored
}

/// <summary>FRD-13: the escrow's <c>releaseNotBefore</c> and how it was reached, for the stamp and its log line.</summary>
internal readonly record struct PayoutDelayDecision(
    DateTime ReleaseNotBefore, int DelayHours, PayoutDelayClass Farmer, PayoutDelayClass? Trucker)
{
    public string ReleaseNotBeforeIso => ReleaseNotBefore.ToString("o", CultureInfo.InvariantCulture);

    public string LogLine(string? escrowId) =>
        $"{PayoutDelayPolicy.LogToken} escrowId={MoneyAlarmLog.LogId(escrowId)} releaseNotBefore={ReleaseNotBeforeIso} "
        + $"hours={DelayHours} farmer={Name(Farmer)} trucker={(Trucker is { } trucker ? Name(trucker) : "-")}";

    private static string Name(PayoutDelayClass payee) => payee.ToString().ToLowerInvariant();
}

/// <summary>
/// FRD-13 (fraud-detection-architecture.md section 17.5, escrow-protocol.md 1.33.0): the risk-based payout
/// delay. At delivery confirm each payee (the farmer, and the trucker when the escrow has a trucker leg) is
/// classed from data the server already has: the risk score (Source before Tier, section 14.4), the account
/// age on the users row and the payee's completed escrows. One release pays both legs, so the escrow waits
/// for the later payee: <c>releaseNotBefore = max(disputeWindowEndsAt, deliveryConfirmedAt + delay)</c>, which
/// never shortens the dispute window. A read that fails gives the longer delay, never a block.
/// </summary>
internal sealed class PayoutDelayPolicy
{
    /// <summary>The escrow row attribute (ISO 8601).</summary>
    public const string ReleaseNotBeforeField = "releaseNotBefore";

    /// <summary>Log line of every stamp (not alarmed).</summary>
    public const string LogToken = "PAYOUT_DELAY_SET";

    private readonly IFraudDetectionService? _fraudService;
    private readonly IUserRepository? _users;
    private readonly IEscrowRepository? _escrows;
    private readonly PayoutDelaySettings? _settings;

    /// <param name="fraudService">Reads the risk score; without it every payee is unscored.</param>
    /// <param name="users">Reads the account age; without it every payee is unscored.</param>
    /// <param name="escrows">Counts the payee's completed escrows; without it no payee is trusted.</param>
    /// <param name="settings">Fixed settings for tests; null reads the environment on every decision.</param>
    public PayoutDelayPolicy(
        IFraudDetectionService? fraudService, IUserRepository? users, IEscrowRepository? escrows,
        PayoutDelaySettings? settings = null)
    {
        _fraudService = fraudService;
        _users = users;
        _escrows = escrows;
        _settings = settings;
    }

    /// <summary>
    /// The payees' wait for one escrow. <paramref name="deliveryTruckerId"/> names the trucker when the
    /// escrow row has none yet. Never throws: a payee whose data cannot be read is unscored.
    /// </summary>
    public async Task<PayoutDelayDecision> DecideAsync(
        Dictionary<string, object> escrow, string? deliveryTruckerId, DateTime deliveryConfirmedAt,
        DateTime disputeWindowEndsAt, DateTime nowUtc, RiskScoreMemo? scores = null)
    {
        var settings = _settings ?? PayoutDelaySettings.FromEnvironment();
        var farmer = await ClassifyAsync(Str(escrow, "farmerId"), "farmer", settings, nowUtc, scores);
        PayoutDelayClass? trucker = null;
        if (HasTruckerLeg(escrow))
        {
            var truckerId = Str(escrow, "truckerId");
            trucker = await ClassifyAsync(
                string.IsNullOrEmpty(truckerId) ? deliveryTruckerId : truckerId, "trucker", settings, nowUtc, scores);
        }

        var hours = Math.Max(settings.HoursFor(farmer), trucker is { } t ? settings.HoursFor(t) : 0);
        var delayed = deliveryConfirmedAt.ToUniversalTime().AddHours(hours);
        var windowEnd = disputeWindowEndsAt.ToUniversalTime();
        return new PayoutDelayDecision(delayed > windowEnd ? delayed : windowEnd, hours, farmer, trucker);
    }

    private async Task<PayoutDelayClass> ClassifyAsync(
        string? userId, string party, PayoutDelaySettings settings, DateTime nowUtc, RiskScoreMemo? scores)
    {
        if (string.IsNullOrEmpty(userId) || _fraudService == null || _users == null)
        {
            return PayoutDelayClass.Unscored;
        }

        try
        {
            var score = scores != null
                ? await scores.GetAsync(userId)
                : await _fraudService.GetUserRiskScoreAsync(userId);
            if (score is not { Source: RiskScoreSource.Computed })
            {
                // Unavailable carries Tier Low and TableNotConfigured was never read: neither is trusted.
                return PayoutDelayClass.Unscored;
            }

            if (score.Tier is RiskTier.High or RiskTier.Critical)
            {
                return PayoutDelayClass.Flagged;
            }

            if (score.Score > 0 || score.Tier != RiskTier.Low)
            {
                return PayoutDelayClass.New;
            }

            var user = await _users.GetUserByIdAsync(userId);
            if (user == null)
            {
                return PayoutDelayClass.Unscored;
            }

            if (!OldEnough(user.CreatedAt, settings, nowUtc))
            {
                return PayoutDelayClass.New;
            }

            return await HasHistoryAsync(userId, party, settings) ? PayoutDelayClass.Trusted : PayoutDelayClass.New;
        }
        catch (Exception)
        {
            return PayoutDelayClass.Unscored;
        }
    }

    private static bool OldEnough(string? createdAt, PayoutDelaySettings settings, DateTime nowUtc) =>
        DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)
        && nowUtc.ToUniversalTime() - created >= TimeSpan.FromDays(settings.TrustedMinAccountDays);

    /// <summary>
    /// At least <see cref="PayoutDelaySettings.TrustedMinCompleted"/> <c>completed</c> escrows as this party among
    /// its newest <see cref="PayoutDelaySettings.CompletedReadLimit"/> (one Query, the status a filter). A failed
    /// read is no history: the payee is new, not unscored, since the score was read.
    /// </summary>
    private async Task<bool> HasHistoryAsync(string userId, string party, PayoutDelaySettings settings)
    {
        if (settings.TrustedMinCompleted == 0)
        {
            return true;
        }

        if (_escrows == null)
        {
            return false;
        }

        try
        {
            var (items, _) = party == "trucker"
                ? await _escrows.QueryByTruckerAsync(userId, "completed", PayoutDelaySettings.CompletedReadLimit, null)
                : await _escrows.QueryByFarmerAsync(userId, "completed", PayoutDelaySettings.CompletedReadLimit, null);
            return items.Count >= settings.TrustedMinCompleted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The stored <c>releaseNotBefore</c> of an escrow row, or null when it has none (stamped before FRD-13:
    /// settles as before). A value that does not parse is read as <c>deliveryConfirmedAt</c> plus
    /// <see cref="PayoutDelaySettings.MaxDelayHours"/>: later, never forever.
    /// </summary>
    public static DateTime? ReleaseNotBeforeOf(Dictionary<string, object> escrow)
    {
        var stored = Str(escrow, ReleaseNotBeforeField);
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (ParseUtc(stored) is { } notBefore)
        {
            return notBefore;
        }

        return ParseUtc(Str(escrow, "deliveryConfirmedAt"))?.AddHours(PayoutDelaySettings.MaxDelayHours);
    }

    /// <summary>
    /// What a payee's escrow read shows (<c>EscrowDto.ReleaseNotBefore</c>): the stored value, or for an escrow
    /// confirmed before FRD-13 its <c>disputeWindowEndsAt</c>, the time the sweep may pay it then. Null before
    /// the delivery is confirmed.
    /// </summary>
    public static string? EffectiveReleaseNotBefore(Dictionary<string, object> escrow)
    {
        var stored = Str(escrow, ReleaseNotBeforeField);
        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        return string.IsNullOrEmpty(Str(escrow, "deliveryConfirmedAt")) ? null : Str(escrow, "disputeWindowEndsAt");
    }

    /// <summary>
    /// What a payee (the farmer or the trucker) reads: <see cref="EffectiveReleaseNotBefore"/> while the escrow is
    /// <c>escrowed</c>, else nothing. Any other status pays nothing at that date, and since FUP-61 an app without
    /// the build header reads a <c>held</c> escrow as <c>escrowed</c>, so a held escrow must not carry a date.
    /// </summary>
    public static string? PayeeReleaseNotBefore(Dictionary<string, object> escrow)
        => Str(escrow, "status") == "escrowed" ? EffectiveReleaseNotBefore(escrow) : null;

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>The escrow pays a trucker (<c>truckerPayout</c> above zero), as the release reads it.</summary>
    private static bool HasTruckerLeg(Dictionary<string, object> escrow)
        => decimal.TryParse(Str(escrow, "truckerPayout"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
           && amount > 0;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;
}

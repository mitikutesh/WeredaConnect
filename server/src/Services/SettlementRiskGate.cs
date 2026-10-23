using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-03: what the settlement risk gate decided for one escrow (fraud-detection-architecture.md
/// sections 17.2 and 18.1). <see cref="Settles"/>: no trigger, the sweep goes on as before.
/// <see cref="HoldReason"/>: a trigger, the escrow is held (or skipped while <c>FRAUD_HOLD_ENABLED</c> is off).
/// <see cref="SkipReason"/>: the gate could not decide, so nothing is settled for this escrow on this run.
/// <see cref="Party"/> names the party that decided it (<c>buyer</c>, <c>farmer</c> or <c>trucker</c>).
/// <see cref="Risk"/> is that party's risk score when the tier decided it (FRD-04, <c>risk_critical</c>),
/// for the hold's audit metadata only.
/// </summary>
public readonly record struct SettlementGateDecision(
    string? HoldReason, string? SkipReason, string? Party, RiskScoreResult? Risk = null)
{
    public static SettlementGateDecision Settle => default;

    public static SettlementGateDecision Hold(string holdReason, string party, RiskScoreResult? risk = null)
        => new(holdReason, null, party, risk);

    public static SettlementGateDecision Skip(string skipReason, string party) => new(null, skipReason, party);

    public bool Settles => HoldReason == null && SkipReason == null;
}

/// <summary>
/// FRD-03: the risk gate each settlement sweep of escrow-cron asks before the first money-related side
/// effect of an escrow (fraud-detection-architecture.md section 17.2). It looks at every party: the
/// buyer, the farmer, and the trucker when the escrow names one. Hold reasons, in precedence:
/// <c>fraud_confirmed</c> (FRD-05 defines the reject action's value), <c>account_suspended</c> (FRD-03),
/// <c>risk_critical</c> (FRD-04 reads the tier).
/// </summary>
public interface ISettlementRiskGate
{
    /// <summary>Starts a sweep run: reads are memoized per run, never across runs.</summary>
    void BeginRun();

    /// <summary>
    /// FRD-04: whether the gate reads the parties' risk tiers. Without them it holds suspended parties
    /// only, so a sweep with <c>FRAUD_HOLD_ENABLED</c> on logs <c>RISK_TABLE_UNCONFIGURED</c> (section 14.4).
    /// </summary>
    bool ReadsRiskTier { get; }

    Task<SettlementGateDecision> CheckAsync(Dictionary<string, object> escrow);
}

/// <summary>
/// FRD-03: holds the settlement of an escrow when one of its parties is suspended
/// (<c>account_suspended</c>). Each user row is read once per run. A row that cannot be read skips the
/// escrow for this run (<c>user_read_failed</c>, fail closed); a party without a row is not suspended,
/// and the payout gates decide about it as before. An escrow with a trucker leg but no <c>truckerId</c>
/// (minted before its order had a truck) takes its trucker the way the payout does, from the order when
/// the delivery names the same one; a lookup that throws fails closed too.
/// FRD-04: with a fraud service, a party at risk tier CRITICAL (a truncated read included) holds it too
/// (<c>risk_critical</c>, after the suspension). Each party's score is read once per run
/// (<see cref="RiskScoreMemo"/>), Source before Tier (section 14.4): a failed read skips the escrow for
/// this run (<c>score_unavailable</c>, fail closed), an unconfigured signals table is LOW. A CRITICAL party
/// holds the escrow even when another party could not be scored: neither moves money, and the hold is
/// certain.
/// </summary>
public sealed class PartySuspensionGate : ISettlementRiskGate
{
    /// <summary>The skip reason when a party's user row cannot be read (or no users table is wired).</summary>
    public const string SkipUserReadFailed = "user_read_failed";

    /// <summary>FRD-04: the skip reason when a party's risk score could not be read (section 14.4).</summary>
    public const string SkipScoreUnavailable = "score_unavailable";

    private static readonly (string Field, string Party)[] Parties =
    {
        ("buyerId", "buyer"), ("farmerId", "farmer"), ("truckerId", "trucker")
    };

    private readonly IUserRepository? _users;
    private readonly Func<Dictionary<string, object>, Task<string?>>? _resolveTrucker;
    private readonly IFraudDetectionService? _fraudService;
    private readonly Dictionary<string, UserDto?> _readThisRun = new(StringComparer.Ordinal);
    private RiskScoreMemo? _scoresThisRun;

    /// <param name="users">The users table; without it every check fails closed.</param>
    /// <param name="resolveTrucker">The payout's trucker lookup for an escrow without <c>truckerId</c>.</param>
    /// <param name="fraudService">FRD-04: reads the parties' risk tiers; without it only suspension holds.</param>
    public PartySuspensionGate(
        IUserRepository? users, Func<Dictionary<string, object>, Task<string?>>? resolveTrucker = null,
        IFraudDetectionService? fraudService = null)
    {
        _users = users;
        _resolveTrucker = resolveTrucker;
        _fraudService = fraudService;
    }

    public bool ReadsRiskTier => _fraudService != null;

    public void BeginRun()
    {
        _readThisRun.Clear();
        _scoresThisRun = null;
    }

    public async Task<SettlementGateDecision> CheckAsync(Dictionary<string, object> escrow)
    {
        string? suspendedParty = null;
        var parties = new List<(string UserId, string Party)>(Parties.Length);
        foreach (var (field, party) in Parties)
        {
            var userId = escrow.TryGetValue(field, out var raw) ? raw?.ToString() : null;
            if (string.IsNullOrEmpty(userId) && party == "trucker" && _resolveTrucker != null && HasTruckerLeg(escrow))
            {
                try
                {
                    userId = await _resolveTrucker(escrow);
                }
                catch (Exception)
                {
                    return SettlementGateDecision.Skip(SkipUserReadFailed, party);
                }
            }

            if (string.IsNullOrEmpty(userId))
            {
                continue;
            }

            if (!_readThisRun.TryGetValue(userId, out var user))
            {
                if (_users == null)
                {
                    return SettlementGateDecision.Skip(SkipUserReadFailed, party);
                }

                try
                {
                    user = await _users.GetUserByIdAsync(userId);
                }
                catch (Exception)
                {
                    // A failed read is not remembered: the next escrow of this party reads again.
                    return SettlementGateDecision.Skip(SkipUserReadFailed, party);
                }

                _readThisRun[userId] = user;
            }

            if (user is { Suspended: true })
            {
                suspendedParty ??= party;
            }

            parties.Add((userId, party));
        }

        if (suspendedParty != null)
        {
            return SettlementGateDecision.Hold(EscrowHoldFields.ReasonAccountSuspended, suspendedParty);
        }

        if (_fraudService == null)
        {
            return SettlementGateDecision.Settle;
        }

        _scoresThisRun ??= new RiskScoreMemo(_fraudService);
        string? unscoredParty = null;
        foreach (var (userId, party) in parties)
        {
            var score = await _scoresThisRun.GetAsync(userId);
            if (score is { Source: RiskScoreSource.TableNotConfigured })
            {
                continue;
            }

            // Anything else that is not a computed score (Unavailable carries Tier Low) must not settle.
            if (score is not { Source: RiskScoreSource.Computed })
            {
                unscoredParty ??= party;
                continue;
            }

            if (score.Tier == RiskTier.Critical)
            {
                return SettlementGateDecision.Hold(EscrowHoldFields.ReasonRiskCritical, party, score);
            }
        }

        return unscoredParty == null
            ? SettlementGateDecision.Settle
            : SettlementGateDecision.Skip(SkipScoreUnavailable, unscoredParty);
    }

    /// <summary>The escrow pays a trucker (<c>truckerPayout</c> above zero), as the release reads it.</summary>
    private static bool HasTruckerLeg(Dictionary<string, object> escrow)
        => escrow.TryGetValue("truckerPayout", out var raw)
           && decimal.TryParse(raw?.ToString(), System.Globalization.NumberStyles.Number,
               System.Globalization.CultureInfo.InvariantCulture, out var amount)
           && amount > 0;
}

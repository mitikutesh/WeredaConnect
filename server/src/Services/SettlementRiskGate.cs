using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-03: what the settlement risk gate decided for one escrow (fraud-detection-architecture.md
/// sections 17.2 and 18.1). <see cref="Settles"/>: no trigger, the sweep goes on as before.
/// <see cref="HoldReason"/>: a trigger, the escrow is held (or skipped while <c>FRAUD_HOLD_ENABLED</c> is off).
/// <see cref="SkipReason"/>: the gate could not decide, so nothing is settled for this escrow on this run.
/// <see cref="Party"/> names the party that decided it (<c>buyer</c>, <c>farmer</c> or <c>trucker</c>).
/// </summary>
public readonly record struct SettlementGateDecision(string? HoldReason, string? SkipReason, string? Party)
{
    public static SettlementGateDecision Settle => default;

    public static SettlementGateDecision Hold(string holdReason, string party) => new(holdReason, null, party);

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

    Task<SettlementGateDecision> CheckAsync(Dictionary<string, object> escrow);
}

/// <summary>
/// FRD-03: holds the settlement of an escrow when one of its parties is suspended
/// (<c>account_suspended</c>). Each user row is read once per run. A row that cannot be read skips the
/// escrow for this run (<c>user_read_failed</c>, fail closed); a party without a row is not suspended,
/// and the payout gates decide about it as before. An escrow with a trucker leg but no <c>truckerId</c>
/// (minted before its order had a truck) takes its trucker the way the payout does, from the order when
/// the delivery names the same one; a lookup that throws fails closed too.
/// </summary>
public sealed class PartySuspensionGate : ISettlementRiskGate
{
    /// <summary>The skip reason when a party's user row cannot be read (or no users table is wired).</summary>
    public const string SkipUserReadFailed = "user_read_failed";

    private static readonly (string Field, string Party)[] Parties =
    {
        ("buyerId", "buyer"), ("farmerId", "farmer"), ("truckerId", "trucker")
    };

    private readonly IUserRepository? _users;
    private readonly Func<Dictionary<string, object>, Task<string?>>? _resolveTrucker;
    private readonly Dictionary<string, UserDto?> _readThisRun = new(StringComparer.Ordinal);

    /// <param name="users">The users table; without it every check fails closed.</param>
    /// <param name="resolveTrucker">The payout's trucker lookup for an escrow without <c>truckerId</c>.</param>
    public PartySuspensionGate(IUserRepository? users, Func<Dictionary<string, object>, Task<string?>>? resolveTrucker = null)
    {
        _users = users;
        _resolveTrucker = resolveTrucker;
    }

    public void BeginRun() => _readThisRun.Clear();

    public async Task<SettlementGateDecision> CheckAsync(Dictionary<string, object> escrow)
    {
        string? suspendedParty = null;
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
        }

        return suspendedParty == null
            ? SettlementGateDecision.Settle
            : SettlementGateDecision.Hold(EscrowHoldFields.ReasonAccountSuspended, suspendedParty);
    }

    /// <summary>The escrow pays a trucker (<c>truckerPayout</c> above zero), as the release reads it.</summary>
    private static bool HasTruckerLeg(Dictionary<string, object> escrow)
        => escrow.TryGetValue("truckerPayout", out var raw)
           && decimal.TryParse(raw?.ToString(), System.Globalization.NumberStyles.Number,
               System.Globalization.CultureInfo.InvariantCulture, out var amount)
           && amount > 0;
}

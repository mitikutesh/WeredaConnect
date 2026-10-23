namespace WeredaConnect.Server.Services;

using WeredaConnect.Server.Models;

/// <summary>
/// Reads each user's risk score at most once per invocation (fraud-detection-architecture.md
/// section 14.3): create one per sync batch or settlement sweep and drop it afterwards. It is not
/// a cache on the service, because the service lives as long as the Lambda container and a
/// score must not outlive the invocation that read it. A failed read is remembered too, so one
/// batch treats a user the same way throughout. Not thread-safe: one memo serves one batch or
/// sweep, which processes its items one after another.
/// </summary>
public sealed class RiskScoreMemo
{
    private readonly IFraudDetectionService _fraudService;
    private readonly Dictionary<string, Task<RiskScoreResult>> _scores = new(StringComparer.Ordinal);

    public RiskScoreMemo(IFraudDetectionService fraudService)
    {
        _fraudService = fraudService ?? throw new ArgumentNullException(nameof(fraudService));
    }

    public Task<RiskScoreResult> GetAsync(string userId)
    {
        if (!_scores.TryGetValue(userId, out var score))
        {
            score = _fraudService.GetUserRiskScoreAsync(userId);
            _scores[userId] = score;
        }

        return score;
    }
}

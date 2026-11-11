using System.Globalization;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-29 (reputation-api.md sections 7 and 8.3): how the owner fields stored on a crop or trip
/// row reach a reader. The row stores the internal tier; a reader gets the public one
/// (<c>good</c> or <c>low</c>), and nothing at all while <c>reputationPublicTags</c> is off. A
/// buyer's browse and the match candidates leave out rows whose stored tier is <c>restricted</c>
/// while <c>reputationPenalties</c> is on: a moderation filter like the one for deleted listings,
/// never applied to the owner's own list or to a read by id.
/// </summary>
public sealed record OwnerReputationView(
    string? Tier, decimal? EffectiveRating, int? RatingCount, int? ReliabilityPct, string? At)
{
    public static OwnerReputationView None { get; } = new(null, null, null, null, null);

    /// <summary>The fields of <paramref name="row"/> as a reader gets them; all absent with the flag off or no stored copy.</summary>
    public static OwnerReputationView Of(Dictionary<string, object> row, bool publicTags)
    {
        if (!publicTags || Text(row, OwnerReputationFields.TierField) is not { } stored)
        {
            return None;
        }

        return new OwnerReputationView(
            ReputationTiers.Public(stored),
            Number(row, OwnerReputationFields.EffectiveRatingField),
            (int?)Number(row, OwnerReputationFields.RatingCountField),
            (int?)Number(row, OwnerReputationFields.ReliabilityPctField),
            Text(row, OwnerReputationFields.AtField));
    }

    /// <summary>8.3: the row's owner is restricted (the stored internal tier).</summary>
    public static bool IsRestrictedOwner(Dictionary<string, object> row)
        => Text(row, OwnerReputationFields.TierField) == ReputationTiers.Restricted;

    /// <summary>
    /// A row as a <c>/sync</c> answer carries it: the stored tier becomes the public one, or all
    /// five fields are dropped with the flag off. The row itself is changed.
    /// </summary>
    public static void ApplyTo(Dictionary<string, object> row, bool publicTags)
    {
        if (!publicTags)
        {
            foreach (var name in OwnerReputationFields.Names)
            {
                row.Remove(name);
            }
        }
        else if (Text(row, OwnerReputationFields.TierField) is { } stored)
        {
            row[OwnerReputationFields.TierField] = ReputationTiers.Public(stored);
        }
    }

    private static string? Text(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

    private static decimal? Number(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value != null
           && decimal.TryParse(
               Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}

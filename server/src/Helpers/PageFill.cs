using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// One page of a DynamoDB list read whose page token never leads to an empty last page
/// (SIM-26 for GET /crops, FUP-126 for GET /trips).
///
/// DynamoDB applies a FilterExpression after it counts <c>Limit</c>, and the handlers drop some
/// rows in memory too, so one read can come back short, or empty with a key, while matching rows
/// remain. And a read that stops at exactly <c>Limit</c> rows hands back a key even when nothing
/// follows. The page is therefore filled with further reads; once it is full, one more read of a
/// single row tells whether anything follows. At most <see cref="MaxReads"/> reads: when they run
/// out the key is returned as it stands, so a long run of filtered-out rows can make a page short
/// (never wrong).
/// </summary>
public static class PageFill
{
    /// <summary>Most reads one page makes to fill itself and look ahead.</summary>
    public const int MaxReads = 5;

    /// <summary>
    /// Reads at most <paramref name="limit"/> rows that pass <paramref name="keep"/> (every row when
    /// it is null), starting after <paramref name="startKey"/>. <paramref name="read"/> takes the
    /// most rows DynamoDB may evaluate and the key to start after, and returns what it found and
    /// DynamoDB's last evaluated key. The returned key is where the next page starts, null when no
    /// kept row follows.
    /// </summary>
    public static async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? NextKey)> ReadAsync(
        Func<int, Dictionary<string, AttributeValue>?, Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)>> read,
        int limit,
        Dictionary<string, AttributeValue>? startKey,
        Func<Dictionary<string, object>, bool>? keep = null)
    {
        var items = new List<Dictionary<string, object>>(limit);
        var key = startKey;
        for (var reads = 0; reads < MaxReads; reads++)
        {
            var lookAhead = items.Count >= limit;
            var (page, lastKey) = await read(lookAhead ? 1 : limit - items.Count, key);
            var kept = keep == null ? page : page.Where(keep).ToList();

            if (lookAhead && kept.Count > 0)
            {
                // A row follows: the next page starts where this look-ahead did.
                return (items, key);
            }

            if (!lookAhead)
            {
                items.AddRange(kept);
            }

            if (lastKey == null)
            {
                return (items, null);
            }

            key = lastKey;
        }

        return (items, key);
    }
}

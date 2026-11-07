using System.Globalization;
using System.Text.Json;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// LT-12 (live-trip.md section 10, order-lifecycle.md 2.5.0): the buyer's optional drop-off landmark
/// on an order ("Merkato, near Anwar Mosque"). The place list has towns, not neighbourhoods, so the
/// driver needs a few words more. Written by the buyer at create and changed only by the buyer
/// before pickup; read by the order's buyer, its farmer and its committed trucker. One line of at
/// most <see cref="MaxLength"/> user-visible characters, with the phone number screen of listing
/// text (a number in it would open the contact ESC-48 keeps closed).
/// </summary>
public static class OrderDropoffNote
{
    /// <summary>The order attribute and the wire name.</summary>
    public const string Field = "dropoffNote";

    /// <summary>
    /// Most user-visible characters (extended grapheme clusters, as <see cref="Length"/> counts):
    /// an Amharic syllable such as "ሰ" is one, and so is an emoji with its modifiers.
    /// </summary>
    public const int MaxLength = 80;

    /// <summary>Sync error prefix for a note that holds a phone number; permanent like every <c>ORDER_*</c> code.</summary>
    public const string ContactInTextCode = "ORDER_CONTACT_IN_TEXT";

    /// <summary>Sync error prefix for a change after pickup; permanent like every <c>ORDER_*</c> code.</summary>
    public const string LockedCode = "ORDER_DROPOFF_NOTE_LOCKED";

    public const string LockedMessage = LockedCode + ": The drop-off note can only be changed before pickup.";

    /// <summary>
    /// Order statuses (wire form) before pickup, the only ones in which the buyer may change the
    /// note. From <c>pickup_confirmed</c> on the goods have left the farm, and a cancelled or
    /// rejected order is not delivered at all.
    /// </summary>
    public static readonly IReadOnlySet<string> EditableOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "created", "pending_payment", "payment_submitted", "payment_failed",
        "funded", "awaiting_transport", "accepted"
    };

    /// <summary>
    /// Delivery statuses that mean the goods were picked up. The order follows its delivery on a
    /// best-effort write, so the edit window also asks the delivery itself.
    /// </summary>
    public static readonly IReadOnlySet<string> PickedUpDeliveryStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "pickedUp", "inTransit", "delivered", "disputed"
    };

    /// <summary>The length the limit counts: user-visible characters (extended grapheme clusters).</summary>
    public static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// Why <paramref name="raw"/> (a sync payload value) is not a valid note, or null when it is.
    /// Text or null (no note); after trimming one line (no control character, and no U+2028 line
    /// or U+2029 paragraph separator) of at most <see cref="MaxLength"/> characters. The clients
    /// turn each of those into a space before they send (order_dropoff_note.dart).
    /// </summary>
    public static string? ValidationError(object? raw)
    {
        if (!TryText(raw, out var text))
        {
            return $"{Field} must be text.";
        }

        var trimmed = text.Trim();
        if (trimmed.Any(IsLineBreakOrControl))
        {
            return $"{Field} must be one line of text.";
        }

        return Length(trimmed) > MaxLength
            ? $"{Field} must not exceed {MaxLength} characters."
            : null;
    }

    /// <summary>A control character (C0, DEL, C1) or a Unicode line or paragraph separator.</summary>
    private static bool IsLineBreakOrControl(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';

    /// <summary>
    /// The note as stored: the trimmed text, empty for none. Call after <see cref="ValidationError"/>
    /// returned null.
    /// </summary>
    public static string Normalize(object? raw) => TryText(raw, out var text) ? text.Trim() : string.Empty;

    /// <summary>
    /// Whether <paramref name="userId"/> may read the note of this order row: its buyer and its
    /// farmer always; its trucker once committed (<see cref="ContactReveal.IsTruckerCommitted"/>) on
    /// an order whose money is held (<see cref="ContactReveal.OpenOrderStatuses"/>), so a trucker who
    /// was only asked, declined, expired or dropped out, or whose order went back to the buyer,
    /// reads nothing of it; anyone else never.
    /// </summary>
    public static bool VisibleTo(IReadOnlyDictionary<string, object> order, string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (userId == Text(order, "buyerId") || userId == Text(order, "farmerId"))
        {
            return true;
        }

        return userId == Text(order, "truckerId")
               && ContactReveal.IsTruckerCommitted(order)
               && ContactReveal.OpenOrderStatuses.Contains(Text(order, "status") ?? string.Empty);
    }

    /// <summary>The stored note of the row; null when there is none.</summary>
    public static string? Of(IReadOnlyDictionary<string, object> order)
        => Text(order, Field) is { Length: > 0 } note ? note : null;

    /// <summary>The note <paramref name="userId"/> may read on this row (<see cref="VisibleTo"/>), else null.</summary>
    public static string? For(IReadOnlyDictionary<string, object> order, string? userId)
        => VisibleTo(order, userId) ? Of(order) : null;

    /// <summary>
    /// The order fields only a reader of the note receives: the note and, LT-14, the order's own
    /// drop-off point (<see cref="OrderDeliveryLocation"/>).
    /// </summary>
    private static readonly string[] ReaderOnlyFields =
    {
        Field, OrderDeliveryLocation.LatitudeField, OrderDeliveryLocation.LongitudeField
    };

    /// <summary>
    /// A sync result whose order row (<c>serverData</c>) goes to <paramref name="userId"/>: the note
    /// and the drop-off point (LT-14) left out when that caller may not read them (an uncommitted
    /// trucker's command answered with the row). Any other result is returned unchanged.
    /// </summary>
    public static SyncOperationResult ForCaller(SyncOperationResult result, string? userId)
    {
        if (result.ServerData is not { } row || !ReaderOnlyFields.Any(row.ContainsKey) || VisibleTo(row, userId))
        {
            return result;
        }

        var scrubbed = new Dictionary<string, object>(row);
        foreach (var field in ReaderOnlyFields)
        {
            scrubbed.Remove(field);
        }

        return result with { ServerData = scrubbed };
    }

    private static bool TryText(object? raw, out string text)
    {
        switch (raw)
        {
            case null:
                text = string.Empty;
                return true;
            case string s:
                text = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } je:
                text = je.GetString() ?? string.Empty;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Null }:
                text = string.Empty;
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    private static string? Text(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;
}

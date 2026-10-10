using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-06: when an accepted order that nobody picked up counts as a transport failure, so its
/// buyer may cancel it for a full refund (order-lifecycle.md 1.15.0). A trip order fails 48 hours
/// after its trip departs, counted from the farmer's acceptance when the trip left before it; a
/// self-pickup order, or one whose trip no longer exists, fails 7 days after acceptance. Server
/// clock only. The deadline is stamped on the order as <see cref="Field"/> when the farmer accepts.
/// </summary>
public static class PickupDeadline
{
    /// <summary>Server-only order field holding the deadline (ISO 8601, UTC).</summary>
    public const string Field = "pickupDeadlineAt";

    /// <summary>The cancel reason code of a buyer cancel after a transport failure (spec 4.6).</summary>
    public const string CancelReason = "transport_failed_buyer_cancelled";

    public static readonly TimeSpan TripGrace = TimeSpan.FromHours(48);

    public static readonly TimeSpan SelfPickupWindow = TimeSpan.FromDays(7);

    /// <param name="departureUtc">The trip's departure; null for self-pickup or a trip that no longer exists.</param>
    /// <param name="acceptedAtUtc">When the farmer accepted the order.</param>
    public static DateTime Compute(DateTime? departureUtc, DateTime acceptedAtUtc)
    {
        if (departureUtc is not { } departure)
        {
            return acceptedAtUtc + SelfPickupWindow;
        }

        return (departure > acceptedAtUtc ? departure : acceptedAtUtc) + TripGrace;
    }

    public static string Format(DateTime utc)
        => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Reads a stored ISO time as UTC; false when it is missing or unreadable.</summary>
    public static bool TryParse(string? raw, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    /// <summary>
    /// When the farmer accepted <paramref name="order"/>: <c>respondedAt</c>, or for a row without it
    /// the next earlier time it carries (<c>paymentVerifiedAt</c>, then <c>createdAt</c>). Null when
    /// none can be read.
    /// </summary>
    public static DateTime? AcceptedAt(Dictionary<string, object> order)
    {
        foreach (var key in new[] { "respondedAt", "paymentVerifiedAt", "createdAt" })
        {
            if (TryParse(order.TryGetValue(key, out var raw) ? raw?.ToString() : null, out var utc))
            {
                return utc;
            }
        }

        return null;
    }
}

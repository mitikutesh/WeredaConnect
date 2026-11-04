using System.Globalization;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// L-7: when a trip has left, for the browse and for a new order (trip-listing-api.md 2.2.0).
/// A trip's departureDate is the instant of Ethiopia midnight on the day the trucker picked,
/// and trucks run late, so a trip counts for its whole day in Ethiopia (Africa/Addis_Ababa,
/// UTC+3 all year, no daylight saving) and has left from the next Ethiopia midnight. The same
/// day rule the clients use (FUP-85, <c>startOfDayInEthiopia</c>).
/// </summary>
public static class TripDeparture
{
    /// <summary>
    /// The instant the Ethiopia calendar day of <paramref name="nowUtc"/> began (21:00 UTC the
    /// day before). A value that is not UTC is read as UTC.
    /// </summary>
    public static DateTime StartOfEthiopiaDayUtc(DateTime nowUtc)
    {
        var utc = nowUtc.Kind == DateTimeKind.Local
            ? nowUtc.ToUniversalTime()
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var ethiopiaDay = (utc + PushText.EthiopiaOffset).Date;
        return DateTime.SpecifyKind(ethiopiaDay - PushText.EthiopiaOffset, DateTimeKind.Utc);
    }

    /// <summary>
    /// The browse cutoff in the form departureDate is stored in since 1.3.0 (UTC with
    /// milliseconds, SIM-60), so the StatusDepartureDateIndex sort key compares as a string.
    /// </summary>
    public static string CutoffIso(DateTime nowUtc) =>
        StartOfEthiopiaDayUtc(nowUtc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a trip departing at <paramref name="departureUtc"/> (a UTC instant) has left:
    /// its Ethiopia day is over.
    /// </summary>
    public static bool HasDeparted(DateTime departureUtc, DateTime nowUtc) =>
        departureUtc < StartOfEthiopiaDayUtc(nowUtc);
}

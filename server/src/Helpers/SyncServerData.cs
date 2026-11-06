using System.Globalization;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// The typed shape of a <c>/sync</c> result's <c>serverData</c> (SIM-25, sync-protocol.md 1.37.0).
/// </summary>
/// <remarks>
/// Rows reach a sync result through <c>DynamoDbHelper.FromItem</c>, which keeps every DynamoDB
/// number as its string, and a trip's <c>matchedListings</c> is stored as JSON text. GET maps the
/// same rows to typed DTOs, so one row had two shapes (<c>"version":"2"</c> from /sync, <c>2</c>
/// from GET) and the mobile conflict resolver, which reads numbers as <c>num</c>, could not take
/// the /sync one. Before a result leaves /sync, the numeric fields of the rows become JSON numbers
/// and <c>matchedListings</c> becomes an array of ids, as GET has them. Every other value is
/// unchanged, and a value under a numeric name that is not a number stays as it is.
/// <para>
/// The fields are named, not guessed from the value: a string attribute that looks like a number
/// (a woreda code, a phone number) must stay a string. FromItem is left alone because the
/// handlers and the GET mappers read its strings.
/// </para>
/// </remarks>
public static class SyncServerData
{
    /// <summary>
    /// The numeric fields of the rows /sync answers with (crop listing, trip, order, delivery,
    /// rating, message, buyer intent, user), matched at any depth: a delivery's <c>gpsPickup</c>
    /// and <c>gpsDropoff</c> hold <c>latitude</c> and <c>longitude</c>. It covers every number of
    /// the GET read models (a test keeps them aligned) and the stored names GET renames
    /// (a crop row's <c>quantity</c> is <c>CropDto.QuantityKg</c>).
    /// </summary>
    internal static readonly HashSet<string> NumericFields = new(StringComparer.Ordinal)
    {
        "version",
        // Crop listing (CropDto); the row stores the quantity as "quantity".
        "quantity", "quantityKg", "pricePerKg", "latitude", "longitude", "farmerReliability",
        // Trip (TripDto) and its destination point (trip-listing-api 2.1.0).
        "capacity", "pricePerQuintal", "bookedQuintals", "remainingQuintals",
        "destinationLatitude", "destinationLongitude",
        // Order (OrderDto).
        "pricePerUnit", "cropCost", "transportCost", "commissionRate", "commissionAmount",
        "orderTotal",
        // Rating (RatingDto).
        "stars",
        // Buyer intent.
        "quantityNeeded", "maxPricePerKg", "deliveryLatitude", "deliveryLongitude",
    };

    /// <summary>A trip's matched listing ids, stored as JSON text and returned by GET as a list.</summary>
    internal const string MatchedListingsField = "matchedListings";

    /// <summary>The result with its <c>serverData</c> typed; a result without a row is returned as it is.</summary>
    public static SyncOperationResult Typed(SyncOperationResult result)
        => result.ServerData is { } row ? result with { ServerData = TypedRow(row) } : result;

    /// <summary>A typed copy of <paramref name="row"/>; the row itself is not changed.</summary>
    public static Dictionary<string, object> TypedRow(Dictionary<string, object> row)
    {
        var typed = new Dictionary<string, object>(row.Count, StringComparer.Ordinal);
        foreach (var (key, value) in row)
        {
            typed[key] = TypedValue(key, value);
        }

        return typed;
    }

    private static object TypedValue(string key, object value) => value switch
    {
        string text when NumericFields.Contains(key) => NumberOrText(text),
        string text when key == MatchedListingsField => IdList(text),
        Dictionary<string, object> nested => TypedRow(nested),
        List<object> list => list.ConvertAll(item => item is Dictionary<string, object> nested ? TypedRow(nested) : item),
        _ => value
    };

    /// <summary>A whole number as a long, a fraction as a decimal (or a double past its range), anything else unchanged.</summary>
    private static object NumberOrText(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
        {
            return fraction;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var large)
               && double.IsFinite(large)
            ? large
            : text;
    }

    /// <summary>
    /// The ids in a stored <c>matchedListings</c>, read as <c>TripFunction</c> reads them for GET:
    /// empty when the text is empty or not a JSON array of strings.
    /// </summary>
    private static List<object> IdList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<object>();
        }

        try
        {
            var ids = JsonSerializer.Deserialize(text, AppJsonContext.Default.ListString);
            return ids == null ? new List<object>() : ids.ConvertAll(id => (object)id);
        }
        catch (JsonException)
        {
            return new List<object>();
        }
    }
}

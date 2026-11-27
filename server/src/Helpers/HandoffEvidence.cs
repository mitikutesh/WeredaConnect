using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-440, GCR-01 and GCR-02 (delivery-verification-api.md 2.19.0, sync-protocol.md 1.71.0): what a handoff's own
/// operation says about how it was made, stamped server-only on the delivery in the transition's write. Per leg
/// (<see cref="PickupLeg"/> for the trucker's <c>pickedUp</c>, <see cref="DropoffLeg"/> for the buyer's
/// <c>delivered</c>, the self-pickup confirm included, whose point is <c>gpsDropoff</c>): why the phone had no
/// GPS fix or what kind of fix it had, the fix's own time and accuracy, how the handoff was proven (QR scanned or
/// code typed), the app it came from, and for the drop-off the phone's time of the operation. Evidence only: a
/// value that does not pass is left out, never refused, so it can never block a handoff, and nothing scores it.
/// Never returned to a client (<see cref="ServerOnlyFields"/>); read by the admin dispute evidence.
/// </summary>
public static class HandoffEvidence
{
    /// <summary>The trucker's pickup at the farm (<c>pickedUp</c>, point <c>gpsPickup</c>).</summary>
    public const string PickupLeg = "pickup";

    /// <summary>The buyer's drop-off or self-pickup confirm (<c>delivered</c>, point <c>gpsDropoff</c>).</summary>
    public const string DropoffLeg = "dropoff";

    /// <summary>Input only, in a handoff operation's <c>data</c>: why there is no fix, or what kind of fix was sent.</summary>
    public const string GpsStatusInput = "gpsStatus";

    /// <summary>Input only, inside the sent point: the fix's own time (ISO 8601 UTC). Never stored in the point.</summary>
    public const string PointCapturedAtInput = "capturedAt";

    /// <summary>Input only, inside the sent point: the fix's accuracy in metres. Never stored in the point.</summary>
    public const string PointAccuracyInput = "accuracyM";

    /// <summary>Server-only: the phone's time of the drop-off operation (its <c>timestamp</c>), when plausible.</summary>
    public const string DropoffCapturedAtField = "dropoffCapturedAt";

    /// <summary>The largest accuracy taken as one (100 km); anything larger, negative or not finite is dropped.</summary>
    public const double MaxAccuracyM = 100_000;

    /// <summary>Statuses sent with a point: a fix, or Android's approximate (coarse only) location.</summary>
    private static readonly HashSet<string> StatusesWithPoint = new(StringComparer.Ordinal) { "fix", "approximate" };

    /// <summary>Statuses sent without a point: location off, permission denied, denied for good, no fix in time.</summary>
    private static readonly HashSet<string> StatusesWithoutPoint =
        new(StringComparer.Ordinal) { "services_off", "denied", "denied_forever", "timeout" };

    /// <summary>The apps an operation may name (<c>SyncOperationRequest.Platform</c>).</summary>
    private static readonly HashSet<string> Platforms = new(StringComparer.Ordinal) { "android", "web" };

    public static string GpsStatusField(string leg) => leg + "GpsStatus";

    public static string GpsCapturedAtField(string leg) => leg + "GpsCapturedAt";

    public static string GpsAccuracyField(string leg) => leg + "GpsAccuracyM";

    public static string MethodField(string leg) => leg + "Method";

    public static string PlatformField(string leg) => leg + "Platform";

    /// <summary>Every server-only attribute this stamps on a delivery row.</summary>
    public static readonly IReadOnlyList<string> ServerOnlyFields = new[]
    {
        GpsStatusField(PickupLeg), GpsCapturedAtField(PickupLeg), GpsAccuracyField(PickupLeg),
        MethodField(PickupLeg), PlatformField(PickupLeg),
        GpsStatusField(DropoffLeg), GpsCapturedAtField(DropoffLeg), GpsAccuracyField(DropoffLeg),
        MethodField(DropoffLeg), PlatformField(DropoffLeg),
        DropoffCapturedAtField,
    };

    /// <summary>The leg a delivery status confirms: <c>pickedUp</c> the pickup, <c>delivered</c> the drop-off; else null.</summary>
    public static string? LegOf(string? status) => status switch
    {
        "pickedUp" => PickupLeg,
        "delivered" => DropoffLeg,
        _ => null
    };

    /// <summary>
    /// The attributes to stamp for <paramref name="leg"/> from the operation's <paramref name="data"/> (as the
    /// handler holds it: values are <see cref="JsonElement"/>), the method the nonce check proved
    /// (<c>qr</c> or <c>code</c>, null when none ran), the operation's <paramref name="platform"/> and
    /// <paramref name="operationTimestamp"/>, for an operation that reached the server at <paramref name="arrivedAtUtc"/>.
    /// Each value is checked on its own and left out when it does not pass:
    /// <list type="bullet">
    /// <item><c>gpsStatus</c>: one of the six values, and <c>fix</c> or <c>approximate</c> only with the leg's
    /// point, the other four only without one;</item>
    /// <item>the point's <c>capturedAt</c>: an ISO 8601 time within the plausibility rule of <c>pickupCapturedAt</c>
    /// (<see cref="TransactionPatternRules.PlausibleCaptureTime"/>);</item>
    /// <item>the point's <c>accuracyM</c>: a JSON number, finite, 0 to <see cref="MaxAccuracyM"/>;</item>
    /// <item>the platform: <c>android</c> or <c>web</c>;</item>
    /// <item>the drop-off's phone time: the operation's <c>timestamp</c> under the same plausibility rule.</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, AttributeValue> Stamps(
        string leg,
        IReadOnlyDictionary<string, object>? data,
        string? method,
        string? platform,
        string? operationTimestamp,
        DateTime arrivedAtUtc)
    {
        var stamps = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        var point = PointOf(data, leg == PickupLeg ? "gpsPickup" : "gpsDropoff");

        if (TextOf(data, GpsStatusInput) is { } status
            && (point != null ? StatusesWithPoint.Contains(status) : StatusesWithoutPoint.Contains(status)))
        {
            stamps[GpsStatusField(leg)] = new AttributeValue { S = status };
        }

        if (point is { } fix)
        {
            if (fix.TryGetProperty(PointCapturedAtInput, out var capturedAt)
                && capturedAt.ValueKind == JsonValueKind.String
                && TransactionPatternRules.PlausibleCaptureTime(capturedAt.GetString(), arrivedAtUtc) is { } fixAt)
            {
                stamps[GpsCapturedAtField(leg)] = new AttributeValue { S = fixAt.ToString("o", CultureInfo.InvariantCulture) };
            }

            if (fix.TryGetProperty(PointAccuracyInput, out var accuracy)
                && accuracy.ValueKind == JsonValueKind.Number
                && accuracy.TryGetDouble(out var metres)
                && double.IsFinite(metres) && metres is >= 0 and <= MaxAccuracyM)
            {
                stamps[GpsAccuracyField(leg)] = new AttributeValue { N = metres.ToString(CultureInfo.InvariantCulture) };
            }
        }

        if (method is "qr" or "code")
        {
            stamps[MethodField(leg)] = new AttributeValue { S = method };
        }

        if (platform != null && Platforms.Contains(platform))
        {
            stamps[PlatformField(leg)] = new AttributeValue { S = platform };
        }

        if (leg == DropoffLeg
            && TransactionPatternRules.PlausibleCaptureTime(operationTimestamp, arrivedAtUtc) is { } phoneAt)
        {
            stamps[DropoffCapturedAtField] = new AttributeValue { S = phoneAt.ToString("o", CultureInfo.InvariantCulture) };
        }

        return stamps;
    }

    /// <summary>
    /// <c>qr</c> or <c>code</c> as stamped on a delivery row (as <c>DynamoDbHelper.FromItem</c> reads it) for
    /// <paramref name="leg"/>; null on a delivery confirmed before 2.19.0, whose method is only in the audit row.
    /// </summary>
    public static string? MethodOf(Dictionary<string, object> row, string leg)
        => StoredText(row, MethodField(leg)) is { } method && method is "qr" or "code" ? method : null;

    /// <summary>A stamped text of a delivery row; null when absent or empty.</summary>
    public static string? StoredText(Dictionary<string, object> row, string field)
        => row.TryGetValue(field, out var value) && value is string text && text.Length > 0 ? text : null;

    /// <summary>The stamped accuracy of <paramref name="leg"/>'s fix in metres; null when absent or unreadable.</summary>
    public static double? AccuracyOf(Dictionary<string, object> row, string leg)
        => row.TryGetValue(GpsAccuracyField(leg), out var value) && value != null
           && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var metres)
           && double.IsFinite(metres)
            ? metres
            : null;

    private static JsonElement? PointOf(IReadOnlyDictionary<string, object>? data, string field)
        => data != null && data.TryGetValue(field, out var raw) && raw is JsonElement { ValueKind: JsonValueKind.Object } point
            ? point
            : null;

    private static string? TextOf(IReadOnlyDictionary<string, object>? data, string key)
        => data != null && data.TryGetValue(key, out var raw)
            ? raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                string text => text,
                _ => null
            }
            : null;
}

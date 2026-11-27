namespace WeredaConnect.Server.Helpers;

/// <summary>
/// The handoff GPS refusals of a <c>delivery</c> update on /sync (F12-SEC-05, FUP-439).
/// </summary>
/// <remarks>
/// Messages read <c>DELIVERY_GPS_PROXIMITY: &lt;reason&gt;: &lt;text&gt;</c>, the shape of the
/// <c>ORDER_KYC_REQUIRED</c> reason tokens. The code is unchanged for older apps; the clients
/// key on the reason, never on the text (sync-protocol.md 1.62.0,
/// delivery-verification-api.md 2.15.0). Each one is permanent: the same payload always gets
/// the same answer.
/// </remarks>
public static class GpsProximityRefusal
{
    public const string ErrorCode = "DELIVERY_GPS_PROXIMITY";

    // Reason tokens, part of the sync contract.

    /// <summary>The trucker's pickup is more than 5 km from the listing's point.</summary>
    public const string ReasonPickupFar = "pickup_far";

    /// <summary>A self-pickup buyer's confirm came without a GPS fix.</summary>
    public const string ReasonSelfPickupNoFix = "self_pickup_no_fix";

    /// <summary>The listing of a self-pickup has no point to check the buyer against.</summary>
    public const string ReasonSelfPickupNoFarmPoint = "self_pickup_no_farm_point";

    /// <summary>A self-pickup buyer's confirm is more than 5 km from the listing's point.</summary>
    public const string ReasonSelfPickupFar = "self_pickup_far";

    /// <summary>A trucker delivery's confirm is more than 5 km from both drop-off points.</summary>
    public const string ReasonDropoffFar = "dropoff_far";

    public static string Message(string reason, string text) => $"{ErrorCode}: {reason}: {text}";
}

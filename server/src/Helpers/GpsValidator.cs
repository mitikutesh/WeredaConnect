namespace WeredaConnect.Server.Helpers;

/// <summary>
/// GPS validation utilities including Haversine distance calculation
/// and proximity checking for delivery verification (F12-SEC-05).
/// </summary>
public static class GpsValidator
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Calculate Haversine distance between two GPS coordinates in kilometers.
    /// </summary>
    public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}

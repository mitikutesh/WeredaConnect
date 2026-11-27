using System.Globalization;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-123: give a cancelled order's quintals back to its trip from the escrow paths that cancel an
/// order (the auto-refund sweep's cancel and the buyer_wins or admin refund cancel before pickup), as
/// the sync cancels do. Best-effort and isolated: it never throws, so the cancel, the stock restore
/// and the refund around it go ahead whatever happens here.
/// </summary>
public static class TripBookingRelease
{
    private const double KgPerQuintal = 100.0;

    /// <summary>
    /// Release the order's booking on its trip (<see cref="ITripRepository.TryReleaseCapacityAsync"/>,
    /// idempotent per order: a trip without a booking for the order is left alone). No-op without a trip
    /// repository (the Lambda wires one only while <c>COMMITMENTS_ENABLED</c> is on), without a
    /// <c>tripId</c> or <c>orderId</c>, or with no quantity. A failure is logged with the
    /// <c>TRIP_RELEASE_FAILED</c> token; the trip then keeps the booking.
    /// </summary>
    /// <param name="order">The order row as read before the cancel (reads <c>orderId</c>, <c>tripId</c>, <c>quantity</c> in kilograms).</param>
    /// <param name="context">Lambda context for the log lines; stdout when null.</param>
    public static async Task ReleaseAsync(
        ITripRepository? trips, Dictionary<string, object> order, ILambdaContext? context)
    {
        if (trips == null)
        {
            return;
        }

        var tripId = Str(order, "tripId");
        var orderId = Str(order, "orderId");
        var quintals = Kilograms(order) / KgPerQuintal;
        if (tripId.Length == 0 || orderId.Length == 0 || quintals <= 0)
        {
            return;
        }

        try
        {
            var released = await trips.TryReleaseCapacityAsync(tripId, orderId, quintals);
            var line = released
                ? $"Trip {MoneyAlarmLog.LogId(tripId)}: {quintals} quintals released from cancelled order {MoneyAlarmLog.LogId(orderId)}."
                : $"Trip {MoneyAlarmLog.LogId(tripId)}: nothing to release for order {MoneyAlarmLog.LogId(orderId)}.";
            if (context != null)
            {
                context.Logger.LogInformation(line);
            }
            else
            {
                Console.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            var line =
                $"TRIP_RELEASE_FAILED tripId={MoneyAlarmLog.LogId(tripId)} orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]";
            if (context != null)
            {
                context.Logger.LogError(line);
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    private static string Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    /// <summary>The order's <c>quantity</c> (kilograms), read culture-free; 0 when missing or unreadable.</summary>
    private static double Kilograms(Dictionary<string, object> order)
        => order.TryGetValue("quantity", out var value)
           && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var kg)
            ? kg
            : 0.0;
}

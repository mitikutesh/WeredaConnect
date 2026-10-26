using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for trip listing persistence in DynamoDB.
/// Read-only queries for the GET /trips endpoints, plus the ESC-15 booking writes
/// (capacity reserved per order at funding, confirmed at the trucker's accept, released
/// when the order leaves the trip). Create/update/delete of the listing itself are
/// handled through the sync endpoint.
/// </summary>
public interface ITripRepository
{
    /// <summary>
    /// Query trip listings with optional filters using the StatusDepartureDateIndex GSI.
    /// </summary>
    /// <param name="fromWoreda">Filter by origin woreda.</param>
    /// <param name="toWoreda">Filter by destination woreda.</param>
    /// <param name="status">Filter by status (default: open). Used as GSI partition key.</param>
    /// <param name="limit">Maximum number of results to return (1-50).</param>
    /// <param name="exclusiveStartKey">Pagination token (DynamoDB exclusive start key).</param>
    /// <returns>List of trip listings and optional pagination token.</returns>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsAsync(
        string? fromWoreda = null,
        string? toWoreda = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query trip listings by trucker ID using the TruckerIdCreatedAtIndex GSI.
    /// </summary>
    /// <param name="truckerId">The trucker's user ID.</param>
    /// <param name="status">Optional status filter (applied as FilterExpression).</param>
    /// <param name="limit">Maximum number of results to return (1-50).</param>
    /// <param name="exclusiveStartKey">Pagination token.</param>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsByTruckerAsync(
        string truckerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Get a trip listing by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string tripId);

    /// <summary>
    /// ESC-15: the trip row as DynamoDB stores it (strongly consistent), or null when there is
    /// none. Booking callers need it for the sort key (<c>createdAt</c>) and for the string sets
    /// <c>bookedOrderIds</c> / <c>acceptedOrderIds</c>, which the plain dictionary of
    /// <see cref="GetByIdAsync"/> does not carry.
    /// </summary>
    Task<Dictionary<string, AttributeValue>?> GetRawItemAsync(string tripId);

    /// <summary>
    /// ESC-15: reserve <paramref name="quintals"/> of the trip for <paramref name="orderId"/> in one
    /// conditional write. The atomic guard is on <c>remainingQuintals</c> (DynamoDB conditions cannot
    /// do arithmetic): the write lands only while the trip still has that much room, is
    /// <c>open</c> or <c>matched</c>, is not deleted, and does not hold a booking for the order
    /// already. A trip without the booking attributes yet (written before this feature) counts as
    /// having its full <c>capacity</c> free. When the reservation leaves no room the trip is marked
    /// <c>matched</c> (fully booked). A refused write is classified from a re-read:
    /// <see cref="TripReserveOutcome.AlreadyReserved"/> (idempotent retry),
    /// <see cref="TripReserveOutcome.Full"/>, <see cref="TripReserveOutcome.Unavailable"/> (deleted,
    /// cancelled, in transit, delivered) or <see cref="TripReserveOutcome.NotFound"/>. Anything other
    /// than a failed condition throws; <paramref name="quintals"/> must be positive.
    /// </summary>
    Task<TripReserveResult> TryReserveCapacityAsync(string tripId, string orderId, double quintals);

    /// <summary>
    /// ESC-15: give the order's <paramref name="quintals"/> back to the trip and drop the order from
    /// <c>bookedOrderIds</c> (and from <c>acceptedOrderIds</c> when it is there), only while the trip
    /// holds a booking for <paramref name="orderId"/> (so a repeat release is a no-op). A <c>matched</c>
    /// trip that has room again goes back to <c>open</c>. Returns false when the trip is missing or
    /// has no booking for the order.
    /// </summary>
    Task<bool> TryReleaseCapacityAsync(string tripId, string orderId, double quintals);

    /// <summary>
    /// ESC-15: move the trip from <paramref name="fromStatus"/> to <paramref name="toStatus"/> in one
    /// write conditioned on the status and on the trip not being deleted, with <c>ADD version 1</c>
    /// and a new <c>updatedAt</c>. Returns false when the trip is missing, deleted or no longer
    /// <paramref name="fromStatus"/>.
    /// </summary>
    Task<bool> TrySetStatusAsync(string tripId, string fromStatus, string toStatus);

    /// <summary>
    /// ESC-15: the trip leg of the trucker's accept, for the order's
    /// <see cref="IOrderRepository.TryUpdateWithAsync"/> transaction: adds <paramref name="orderId"/>
    /// to <c>acceptedOrderIds</c>, only while the trip still holds the order's booking, is
    /// <c>open</c> or <c>matched</c> and is not deleted. Nothing is written here; the caller runs
    /// the transaction.
    /// </summary>
    /// <param name="tripCreatedAt">The trip's sort key, from <see cref="GetRawItemAsync"/>.</param>
    /// <param name="nowIso">The <c>updatedAt</c> the write stamps (ISO-8601 UTC).</param>
    TransactWriteItem BuildConfirmBookingItem(string tripId, string tripCreatedAt, string orderId, string nowIso);
}

/// <summary>
/// Outcome of <see cref="ITripRepository.TryReserveCapacityAsync"/>. Only <see cref="Reserved"/>
/// wrote anything.
/// </summary>
public enum TripReserveOutcome
{
    /// <summary>The capacity was taken for the order.</summary>
    Reserved,

    /// <summary>The trip already holds a booking for this order (a replayed funding).</summary>
    AlreadyReserved,

    /// <summary>The trip has less room left than the order needs.</summary>
    Full,

    /// <summary>The trip is deleted, or no longer takes bookings (cancelled, in transit, delivered).</summary>
    Unavailable,

    /// <summary>No trip row with this id.</summary>
    NotFound
}

/// <summary>
/// ESC-15: the result of a reservation. <paramref name="RemainingQuintals"/> is the trip's room
/// after the write for <see cref="TripReserveOutcome.Reserved"/>, the room it has for
/// <see cref="TripReserveOutcome.AlreadyReserved"/> and <see cref="TripReserveOutcome.Full"/>
/// (its capacity when the booking attributes are absent), and null otherwise.
/// </summary>
public sealed record TripReserveResult(TripReserveOutcome Outcome, double? RemainingQuintals);

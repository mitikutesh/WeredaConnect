using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for delivery persistence in DynamoDB.
/// Read-only queries for the GET /deliveries endpoints.
/// Create/update/delete operations are handled through the sync endpoint.
/// </summary>
public interface IDeliveryRepository
{
    /// <summary>
    /// Query deliveries with optional filters using GSIs.
    /// </summary>
    /// <param name="status">Filter by status (default: pending). Used as GSI partition key.</param>
    /// <param name="tripId">Filter by trip ID. Uses TripIdIndex GSI.</param>
    /// <param name="listingId">Filter by listing ID (post-query filter).</param>
    /// <param name="limit">Maximum number of results to return (1-50).</param>
    /// <param name="exclusiveStartKey">Pagination token (DynamoDB exclusive start key).</param>
    /// <returns>List of deliveries and optional pagination token.</returns>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryDeliveriesAsync(
        string? status = null,
        string? tripId = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query all deliveries for a participant across every status (newest first),
    /// via the ParticipantIndex GSI. The index is keyed on <c>participantId</c>,
    /// which is populated with the delivery's buyerId, so this returns the buyer's
    /// deliveries. Used by the mobile pull so a buyer's device receives
    /// trucker-driven status changes it did not author.
    /// </summary>
    /// <param name="status">Optional status filter (non-key attribute, applied as a filter).</param>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByParticipantAsync(
        string participantId,
        int limit = 50,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null,
        string? status = null);

    /// <summary>
    /// Query a trucker's deliveries across every status (newest first) via the
    /// TruckerIdCreatedAtIndex GSI, with an optional status filter. Sparse: a
    /// self-pickup delivery has no truckerId and is not indexed. SIM-16.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query a farmer's deliveries across every status (newest first) via the
    /// FarmerIdCreatedAtIndex GSI, with an optional status filter.
    /// </summary>
    /// <remarks>
    /// A farmer does not author deliveries (the trucker does) and is not the
    /// ParticipantIndex key (that is the buyer), so before this index a farmer
    /// had no way to list their own deliveries and the Deliveries tab was
    /// permanently empty.
    /// </remarks>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Get a delivery by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string deliveryId);

    /// <summary>
    /// Get a delivery by ID with a strongly consistent read when
    /// <paramref name="consistentRead"/> is true (ESC-03: the dispute verdict mirror decides
    /// on the delivery right after its own write).
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string deliveryId, bool consistentRead);

    /// <summary>
    /// SIM-12: move a <c>delivered</c> delivery to <c>disputed</c> (conditional write; the
    /// escrow dispute mirrors onto the handoff). Returns false when the delivery does not
    /// exist or is not <c>delivered</c>, so a repeat is a no-op.
    /// </summary>
    Task<bool> TryMarkDisputedAsync(string deliveryId, string disputedBy, string? reason);

    /// <summary>
    /// ESC-03: record the admin's dispute verdict on a <c>disputed</c> delivery
    /// (<c>disputeResolution</c>, <c>disputeResolvedAt</c>; the status stays <c>disputed</c>).
    /// Conditional on the delivery being <c>disputed</c> with no verdict yet, so only one
    /// writer stamps it. Returns false when the delivery is missing, not disputed, or
    /// already carries a verdict.
    /// </summary>
    Task<bool> TryRecordDisputeResolutionAsync(string deliveryId, string resolution, string resolvedAt);

    /// <summary>
    /// ESC-03: cancel a delivery that was never picked up (<c>pending</c> to <c>cancelled</c>, the
    /// same write as a party's cancel through sync: no <c>deletedAt</c> since ESC-08, so the parties'
    /// delivery lists still return it), as a system actor, when a <c>buyer_wins</c> verdict refunds
    /// the order before pickup. Conditional on <c>status = pending</c>. Returns false when the
    /// delivery is missing or no longer pending.
    /// </summary>
    Task<bool> TryCancelPendingAsync(string deliveryId);
}

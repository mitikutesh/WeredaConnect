using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>A row the user is a party to, with the user's role on it (<c>buyer</c>, <c>farmer</c> or <c>trucker</c>).</summary>
public sealed record PartyRow(string Role, Dictionary<string, object> Item);

/// <summary>The key of a crop listing or a trip (both tables are keyed id + createdAt).</summary>
public sealed record ListingKey(string Id, string CreatedAt);

/// <summary>A buyer order whose legacy deliveries may lack <c>participantId</c> (found through <c>ListingIndex</c>).</summary>
public sealed record BuyerOrderRef(string OrderId, string ListingId);

/// <summary>
/// ACC-02: the DynamoDB reads and writes of an account deletion (account-deletion.md), so the
/// deletion steps can be tested against one fake. Every lookup by user uses an existing index
/// (no Scan, except <see cref="ScanIncompleteAsync"/> for the completion sweep), and every write
/// is conditional and idempotent: running it again on a row it already changed writes nothing
/// or the same values.
/// </summary>
public interface IAccountDeletionRepository
{
    // ── users row ──

    /// <summary>The users row, read strongly consistent; null when there is none.</summary>
    Task<UserDto?> GetAccountAsync(string userId);

    /// <summary>
    /// Step 5: <c>deletionState = in_progress</c> and <c>deletionRequestedAt</c> (kept when already
    /// set), only while the row exists, has no other deletion state and is not suspended. Returns
    /// the stored <c>deletionRequestedAt</c>, or null when the condition failed.
    /// </summary>
    Task<string?> TryMarkInProgressAsync(string userId, string requestedAt);

    /// <summary>Step 9 abort: remove <c>deletionState</c> and <c>deletionRequestedAt</c> while still <c>in_progress</c>.</summary>
    Task<bool> TryClearInProgressAsync(string userId);

    /// <summary>
    /// Step 11 (and <c>reapply</c>): remove every attribute off the tombstone allowlist, set
    /// <c>deletedAt</c> (kept when set), <c>deletionState = </c><paramref name="nextState"/> and
    /// <c>version + 1</c>, conditional on the version read (re-read on a lost race). With
    /// <paramref name="requireInProgress"/> only from <c>in_progress</c>. Returns the row's
    /// <c>deletedAt</c> afterwards (also when another run already tombstoned it), or null when the
    /// row is missing or was in no state this call may move.
    /// </summary>
    Task<string?> TombstoneAsync(string userId, string deletedAt, string nextState, bool requireInProgress);

    /// <summary>Step 13: <c>cognito_pending</c> to <c>completed</c>. True when the row is <c>completed</c> afterwards.</summary>
    Task<bool> MarkCompletedAsync(string userId);

    /// <summary>
    /// The completion sweep: one page of a filtered Scan of the users table for rows in
    /// <c>in_progress</c> or <c>cognito_pending</c> requested before <paramref name="requestedBefore"/>.
    /// </summary>
    Task<(List<string> UserIds, Dictionary<string, AttributeValue>? Next)> ScanIncompleteAsync(
        string requestedBefore, Dictionary<string, AttributeValue>? startKey);

    // ── rows the user is a party to ──

    /// <summary>Orders through the buyer, farmer and trucker indexes, every page.</summary>
    Task<List<PartyRow>> QueryOrdersAsync(string userId);

    /// <summary>One order, read strongly consistent; null when missing.</summary>
    Task<Dictionary<string, object>?> GetOrderAsync(string orderId, string createdAt);

    /// <summary>Escrows through the buyer, farmer and trucker indexes, every page.</summary>
    Task<List<PartyRow>> QueryEscrowsAsync(string userId);

    /// <summary>
    /// Deliveries through the farmer, trucker and participant (buyer) indexes, plus the
    /// <c>ListingIndex</c> fallback for <paramref name="buyerOrders"/>: rows whose <c>buyerId</c> is
    /// the user or whose <c>orderId</c> is that order.
    /// </summary>
    Task<List<PartyRow>> QueryDeliveriesAsync(string userId, IReadOnlyCollection<BuyerOrderRef> buyerOrders);

    /// <summary>True when the order has an ESC-01 checkout slot (consistent read).</summary>
    Task<bool> HasCheckoutSlotAsync(string orderId);

    /// <summary>The escrow-audit trail of <c>escrow-order-{orderId}</c>, read strongly consistent.</summary>
    Task<List<Dictionary<string, object>>> GetOrderPaymentTrailAsync(string orderId);

    // ── step 7: listings ──

    Task<List<ListingKey>> QueryCropKeysAsync(string farmerId);

    Task<List<ListingKey>> QueryTripKeysAsync(string truckerId);

    /// <summary>
    /// Withdraw and scrub one crop listing by the step 7 rule for its status (re-read and reapplied
    /// on a lost condition). True when it wrote; false when the row is missing or already done.
    /// </summary>
    Task<bool> WithdrawCropAsync(ListingKey key, string now);

    /// <summary>Same as <see cref="WithdrawCropAsync"/> for a trip.</summary>
    Task<bool> WithdrawTripAsync(ListingKey key, string now);

    // ── step 8: unpaid orders ──

    /// <summary>
    /// Cancel an unpaid order as the system (<c>cancelReason = account_deleted</c>), only while it is
    /// <c>created</c>, <c>pending_payment</c> or <c>payment_failed</c> at the version read. False on a
    /// lost condition.
    /// </summary>
    Task<bool> TryCancelUnpaidOrderAsync(Dictionary<string, object> order, string now);

    // ── step 10: per-table policy ──

    /// <summary>The user's copied names on the order become the sentinel. True when it wrote.</summary>
    Task<bool> ScrubOrderAsync(Dictionary<string, object> order, IReadOnlyCollection<string> roles, string now);

    /// <summary>The user's copied names on the delivery become the sentinel, their phones are removed.</summary>
    Task<bool> ScrubDeliveryAsync(Dictionary<string, object> delivery, IReadOnlyCollection<string> roles, string now);

    /// <summary>The user's copied names on the escrow become the sentinel; nothing else changes.</summary>
    Task<bool> ScrubEscrowAsync(Dictionary<string, object> escrow, IReadOnlyCollection<string> roles);

    /// <summary>Ratings about the user (<c>RateeIdCreatedAtIndex</c>), every page.</summary>
    Task<List<Dictionary<string, object>>> QueryRatingsReceivedAsync(string rateeId);

    Task<Dictionary<string, object>?> GetRatingAsync(string ratingId);

    /// <summary>Remove the rating's comment. False when the rating or its comment is gone.</summary>
    Task<bool> RemoveRatingCommentAsync(string ratingId);

    /// <summary>Ids of the messages the user sent or received.</summary>
    Task<List<string>> QueryMessageIdsAsync(string userId);

    Task DeleteMessageAsync(string messageId);

    Task<List<string>> QueryIntentIdsAsync(string buyerId);

    Task<List<string>> QueryMatchEventIdsAsync(string intentId);

    Task DeleteMatchEventAsync(string intentId, string eventId);

    Task DeleteIntentAsync(string buyerId, string intentId);

    Task<List<string>> QueryDeviceEndpointsAsync(string userId);

    Task DeleteDeviceTokenAsync(string userId, string endpointArn);
}

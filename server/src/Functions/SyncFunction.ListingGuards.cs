using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-18: a crop listing that orders still depend on cannot be taken off the market by its
/// farmer. The stock race at funding itself is decided in <see cref="ICropRepository.TryDecrementStockAsync"/>
/// and <see cref="OrderFundingService"/>.
/// </summary>
public partial class SyncFunction
{
    /// <summary>Sync error prefix for a listing change refused because orders depend on the listing (permanent on clients).</summary>
    internal const string CropHasActiveOrdersCode = "CROP_HAS_ACTIVE_ORDERS";

    /// <summary>
    /// ESC-18: order statuses that keep a listing from being deleted: the buyer's money is on its
    /// way (<c>payment_submitted</c>, so a funding never lands on a listing deleted a moment before)
    /// or held, and the goods have not left the farm. After pickup the listing is no longer needed.
    /// </summary>
    internal static readonly IReadOnlySet<string> ListingHoldingOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "payment_submitted", "funded", OrderCommitment.AwaitingTransportStatus, "accepted"
    };

    /// <summary>
    /// ESC-18: refuse taking a listing off the market (a <c>delete</c>, or an <c>update</c> to
    /// <c>status: deleted</c>) while any of its orders is in <see cref="ListingHoldingOrderStatuses"/>:
    /// <c>CROP_HAS_ACTIVE_ORDERS</c>, nothing written. The orders come from <c>ListingIdIndex</c>
    /// (one query, every page). An index that cannot be read refuses for now with an unprefixed,
    /// retryable error. SIM-56: with or without <see cref="CommitmentsEnabled"/> (it was behind the
    /// flag, like <c>TRIP_HAS_BOOKINGS</c>): a deleted listing refuses its delivery create, so a
    /// funded order on a listing deleted with the flag off had no way to its truck. The code reaches
    /// only apps that send <c>X-App-Build</c> (all of them handle it since sync-protocol.md 1.17.0);
    /// an older app gets the sentence as <c>SYNC_VALIDATION</c> (FUP-241,
    /// <see cref="OldClientRefusal"/>), permanent on every client. Null when the change may proceed.
    /// </summary>
    private async Task<SyncOperationResult?> CheckListingRemovalAllowedAsync(
        SyncOperationRequest operation, ILambdaContext context)
    {
        List<Dictionary<string, object>> orders;
        try
        {
            orders = await _orderRepo.QueryByListingAsync(operation.EntityId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Listing delete: orders of listing {MoneyAlarmLog.LogId(operation.EntityId)} could not be read: [{ex.GetType().Name}]");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "The listing's orders could not be checked. Please try again later."
            };
        }

        var holding = orders.Count(o => ListingHoldingOrderStatuses.Contains(ItemString(o, "status")));
        if (holding == 0)
        {
            return null;
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = OldClientRefusal.ForClient(
                $"{CropHasActiveOrdersCode}: This listing has {holding} order(s) in progress. "
                + "It cannot be deleted until they are completed or cancelled.",
                _batchAppBuild, CropHasActiveOrdersCode, operation.EntityType)
        };
    }

    /// <summary>
    /// FUP-152: the message of a <c>conflict</c> that answers an <c>update</c> reaching a deleted
    /// listing.
    /// </summary>
    internal const string CropAlreadyDeletedMessage =
        CropRepository.AlreadyDeletedCode + ": The listing is already deleted.";

    /// <summary>
    /// FUP-152: an <c>update</c> that reaches a deleted listing (<see cref="CropRepository.IsDeleted"/>)
    /// writes nothing and is answered <c>conflict</c> with the stored row and its version, whatever
    /// the client sent, so the farmer's app takes the server's row (server wins) instead of retrying.
    /// Only the listing's farmer gets the row: ownership is checked first.
    /// </summary>
    private static SyncOperationResult DeletedListingConflict(
        SyncOperationRequest operation, Dictionary<string, object> stored) => new()
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = OrderVersionOf(stored),
            ServerData = stored,
            Message = CropAlreadyDeletedMessage
        };
}

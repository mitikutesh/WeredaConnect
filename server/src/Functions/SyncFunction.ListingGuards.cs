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

    /// <summary>
    /// FUP-495: the message of a <c>conflict</c> that answers an <c>update</c> reaching a sold listing.
    /// </summary>
    internal const string CropSoldMessage = CropRepository.SoldCode + ": The listing is sold.";

    /// <summary>
    /// FUP-495: an <c>update</c> that reaches a sold listing (<see cref="CropRepository.IsSold"/>)
    /// writes nothing and is answered <c>conflict</c> with the stored row and its version, whatever
    /// the client sent, as <see cref="DeletedListingConflict"/> answers a deleted one: sold is
    /// terminal, and a conflict is the one answer no shipped app resends. Only the listing's farmer
    /// gets the row: ownership is checked first. The <c>delete</c> action and the stock writes of
    /// the order flow are not touched.
    /// </summary>
    private static SyncOperationResult SoldListingConflict(
        SyncOperationRequest operation, Dictionary<string, object> stored) => new()
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = OrderVersionOf(stored),
            ServerData = stored,
            Message = CropSoldMessage
        };

    /// <summary>
    /// FUP-494: a <c>delete</c> that reaches a listing already deleted writes nothing and is
    /// answered <c>success</c> with the stored version, whatever version the client sent: what the
    /// farmer asked for already holds. Success is the one answer every shipped app takes without a
    /// resend and without touching its local copy (an app older than FUP-149 turned a conflict's
    /// deleted row into a local draft).
    /// </summary>
    private static SyncOperationResult AlreadyDeletedSuccess(
        SyncOperationRequest operation, Dictionary<string, object>? stored) => new()
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = stored != null ? OrderVersionOf(stored) : 0
        };

    /// <summary>
    /// The lot of a crop listing create for the duplicate lot check (fraud-detection-architecture.md
    /// Rule 1), read from a payload that already passed <see cref="ValidateCropFields"/>. Null when the
    /// quantity or the woreda cannot be read: the lot check is then skipped, the other checks still run.
    /// </summary>
    private static ListingLot? ListingLotOf(SyncOperationRequest operation)
    {
        var data = operation.Data;
        if (data == null
            || !decimal.TryParse(data.GetValueOrDefault("quantity")?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity)
            || data.GetValueOrDefault("woreda")?.ToString() is not { Length: > 0 } woreda)
        {
            return null;
        }

        if (TryReadOptionalDouble(data, "latitude", out var latitude, out var latitudeInvalid) && !latitudeInvalid
            && TryReadOptionalDouble(data, "longitude", out var longitude, out var longitudeInvalid) && !longitudeInvalid)
        {
            return new ListingLot(operation.EntityId, quantity, woreda, latitude, longitude);
        }

        return new ListingLot(operation.EntityId, quantity, woreda, null, null);
    }

    /// <summary>
    /// Rule 6 (fraud-detection-architecture.md, trip-listing-api.md rule 14): check a trip create that
    /// passed the field rules, its <c>departureDate</c> already normalized, against the trucker's other
    /// open and matched trips. A duplicate (same route, day and vehicle) comes back as the refusal
    /// (<c>FRAUD_DUPLICATE_LISTING</c> on the wire), with its <c>DUPLICATE_TRIP</c> signal recorded (weight 0). Another trip of the same day comes back as the
    /// flag, for the caller to record once the trip is written. A check that cannot run (no fraud
    /// service, an unreadable date, a failed read) lets the create go on: the rule never blocks a trucker
    /// because a read failed.
    /// </summary>
    private async Task<(SyncOperationResult? Refusal, FraudCheckResult? Flag)> CheckTripCreationFraudAsync(
        SyncOperationRequest operation, string truckerId, ILambdaContext context)
    {
        if (_fraudService == null || operation.Data == null
            || !TryParseDepartureDateUtc(
                operation.Data.GetValueOrDefault("departureDate")?.ToString(), requireIsoShape: false, out var departureUtc))
        {
            return (null, null);
        }

        FraudCheckResult? check;
        try
        {
            check = await _fraudService.CheckTripCreationAsync(new TripPlan(
                truckerId, operation.EntityId,
                operation.Data.GetValueOrDefault("fromWoreda")?.ToString() ?? "",
                operation.Data.GetValueOrDefault("toWoreda")?.ToString() ?? "",
                departureUtc,
                operation.Data.GetValueOrDefault("vehiclePlate")?.ToString()));
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Trip fraud check skipped for trip {MoneyAlarmLog.LogId(operation.EntityId)}: [{ex.GetType().Name}]");
            return (null, null);
        }

        if (check is { IsAllowed: false })
        {
            await TryRecordFraudSignalAsync(
                truckerId, check.FlagReason ?? check.ErrorCode ?? "FRAUD_REJECTED", operation.EntityId, "trip",
                check.Message, context);
            return (new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"{check.ErrorCode}: {check.Message}"
            }, null);
        }

        return (null, check is { IsFlagged: true } ? check : null);
    }
}

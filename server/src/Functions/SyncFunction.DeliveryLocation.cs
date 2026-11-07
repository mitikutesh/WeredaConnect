using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.8.0): the order's own drop-off point,
/// <c>deliveryLatitude</c> and <c>deliveryLongitude</c>. Set at create and changed by the buyer until
/// pickup, by the rules of the drop-off note: the same readers (<see cref="OrderDropoffNote.VisibleTo"/>)
/// and the same edit window. Who reads it is <see cref="OrderDeliveryLocation.For"/>.
/// </summary>
public partial class SyncFunction
{
    /// <summary>
    /// On create, after the field rules (<see cref="OrderDeliveryLocation.ValidationError"/>): a pair
    /// of nulls is no point, so neither field is stored.
    /// </summary>
    private static void ApplyCreateDeliveryLocation(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (OrderDeliveryLocation.Sent(data) && OrderDeliveryLocation.FromPayload(data) == null)
        {
            data.Remove(OrderDeliveryLocation.LatitudeField);
            data.Remove(OrderDeliveryLocation.LongitudeField);
        }
    }

    /// <summary>
    /// On update, right after the drop-off note and before the other order rules. In order: a party
    /// who may not read the point (<see cref="OrderDropoffNote.VisibleTo"/>, a trucker not committed)
    /// is refused with <c>ORDER_UNAUTHORIZED</c> whatever it sent, before the stored point is looked
    /// at (no equality oracle); the field rules (<c>ORDER_VALIDATION</c>); an unchanged echo of the
    /// stored point from a party who reads it is dropped; a change from anyone but the buyer is
    /// <c>ORDER_UNAUTHORIZED</c>; after pickup <c>ORDER_DELIVERY_LOCATION_LOCKED</c>. Every refusal
    /// writes nothing. A pair of nulls clears the point (the trip's destination is then the drop-off).
    /// Null when the update may go on.
    /// </summary>
    private async Task<SyncOperationResult?> CheckDeliveryLocationChangeAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> order,
        string? callerId,
        string? orderBuyerId,
        ILambdaContext context)
    {
        var data = operation.Data!;
        var isBuyer = !string.IsNullOrEmpty(callerId) && callerId == orderBuyerId;

        if (!isBuyer && !OrderDropoffNote.VisibleTo(order, callerId))
        {
            return await DeliveryLocationNotBuyerAsync(operation, callerId, context);
        }

        if (OrderDeliveryLocation.ValidationError(data) is { } error)
        {
            return OrderValidationError(operation, error);
        }

        if (OrderDeliveryLocation.Same(OrderDeliveryLocation.FromPayload(data), OrderDeliveryLocation.Of(order)))
        {
            data.Remove(OrderDeliveryLocation.LatitudeField);
            data.Remove(OrderDeliveryLocation.LongitudeField);
            return null;
        }

        if (!isBuyer)
        {
            return await DeliveryLocationNotBuyerAsync(operation, callerId, context);
        }

        return await CheckDropoffEditWindowAsync(operation, order, OrderDeliveryLocation.LockedMessage, context);
    }

    /// <summary>A drop-off point change from anyone but the buyer: refused, with the role signal.</summary>
    private Task<SyncOperationResult> DeliveryLocationNotBuyerAsync(
        SyncOperationRequest operation, string? callerId, ILambdaContext context)
        => RefuseWithSignalAsync(new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = "ORDER_UNAUTHORIZED: Only the buyer can change the drop-off place."
        }, FraudDetectionService.RoleForbiddenSignal, operation, "order", callerId, context);
}

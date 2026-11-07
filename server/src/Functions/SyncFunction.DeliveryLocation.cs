using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.9.0): the order's own drop-off point,
/// <c>deliveryLatitude</c> and <c>deliveryLongitude</c>. Set at create and changed by the buyer until
/// pickup, in the drop-off note's edit window; read by <see cref="OrderDeliveryLocation.VisibleTo"/>
/// (the trucker also while only asked). Ignored on an order without a trip (self-pickup).
/// </summary>
public partial class SyncFunction
{
    /// <summary>
    /// On create, before the field rules: an order without a trip (self-pickup, no drop-off) ignores
    /// the point, so it is dropped and never refused.
    /// </summary>
    private static void IgnoreCreateDeliveryLocationWithoutTrip(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (OrderDeliveryLocation.Sent(data) && !OrderDeliveryLocation.HasTrip(data))
        {
            OrderDeliveryLocation.Drop(data);
        }
    }

    /// <summary>
    /// On create, after the field rules (<see cref="OrderDeliveryLocation.ValidationError"/>): a pair
    /// of nulls is no point, so neither field is stored.
    /// </summary>
    private static void ApplyCreateDeliveryLocation(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (OrderDeliveryLocation.Sent(data) && OrderDeliveryLocation.FromPayload(data) == null)
        {
            OrderDeliveryLocation.Drop(data);
        }
    }

    /// <summary>
    /// On update, right after the drop-off note and before the other order rules. In order: on an
    /// order without a trip (self-pickup) the fields are ignored (dropped, never refused); a party who
    /// may not read the point (<see cref="OrderDeliveryLocation.VisibleTo"/>: a trucker of an order
    /// whose money is not held) is refused with <c>ORDER_UNAUTHORIZED</c> whatever it sent, before the
    /// stored point is looked at (no equality oracle); the field rules (<c>ORDER_VALIDATION</c>); an
    /// unchanged echo of the stored point from a party who reads it is dropped; a change from anyone
    /// but the buyer is <c>ORDER_UNAUTHORIZED</c>; after pickup <c>ORDER_DELIVERY_LOCATION_LOCKED</c>.
    /// Every refusal writes nothing. A pair of nulls clears the point (the trip's destination is then
    /// the drop-off). Null when the update may go on.
    /// </summary>
    private async Task<SyncOperationResult?> CheckDeliveryLocationChangeAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> order,
        string? callerId,
        string? orderBuyerId,
        ILambdaContext context)
    {
        var data = operation.Data!;
        if (!OrderDeliveryLocation.HasTrip(order))
        {
            OrderDeliveryLocation.Drop(data);
            return null;
        }

        var isBuyer = !string.IsNullOrEmpty(callerId) && callerId == orderBuyerId;
        if (!isBuyer && !OrderDeliveryLocation.VisibleTo(order, callerId))
        {
            return await DeliveryLocationNotBuyerAsync(operation, callerId, context);
        }

        if (OrderDeliveryLocation.ValidationError(data) is { } error)
        {
            return OrderValidationError(operation, error);
        }

        if (OrderDeliveryLocation.Same(OrderDeliveryLocation.FromPayload(data), OrderDeliveryLocation.Of(order)))
        {
            OrderDeliveryLocation.Drop(data);
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

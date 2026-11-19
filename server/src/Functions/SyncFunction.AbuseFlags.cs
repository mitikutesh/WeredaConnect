using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Persona simulation 2026-11-19 (fraud-detection-architecture.md 3.44.0, Rule 7): the review-only flags
/// of BUG-5, BUG-7 and BUG-14 and the linked-accounts check of BUG-3. Each flag is a weight 0 soft signal
/// on the caller, written after the business write it observes succeeded. None refuses anything, none
/// moves a score, and a failed read or write is logged and never changes the answer.
/// </summary>
public partial class SyncFunction
{
    /// <summary>Order statuses from the delivery on: a phone number in a chat is no longer flagged.</summary>
    private static readonly HashSet<string> DeliveredOrLaterOrderStatuses = new(StringComparer.Ordinal)
    {
        "delivered", "disputed", "dispute_resolved", "disbursing", "settled"
    };

    /// <summary>
    /// BUG-5 (FUP-448): a trucker's <c>pickedUp</c> that carried no <c>gpsPickup</c>. The pickup is
    /// accepted as before (a rural phone may have no fix); the trucker gets <c>PICKUP_NO_FIX</c> with the
    /// delivery as the entity. A delivery is picked up once, so once per delivery. A self-pickup has no
    /// trucker pickup.
    /// </summary>
    private async Task TryRecordPickupWithoutFixAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> existing, string? handoffMethod,
        string? authenticatedUserId, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(authenticatedUserId)
            || AttrString(existing, "truckerId") != authenticatedUserId
            || AttrString(existing, "deliveryType") == SelfPickupDeliveryType
            || (operation.Data != null && operation.Data.TryGetValue("gpsPickup", out var gps)
                && gps is JsonElement { ValueKind: JsonValueKind.Object }))
        {
            return;
        }

        await TryRecordFraudSignalAsync(
            authenticatedUserId, FraudDetectionService.PickupNoFixSignal, operation.EntityId, "delivery",
            $"Pickup confirmed without a GPS fix (handoff: {handoffMethod ?? "unknown"}).", context);
    }

    /// <summary>
    /// BUG-7: an order created on a trip that does not start where the listing is. Place names are free
    /// text, so nothing is refused: when the trip's <c>fromWoreda</c> is neither the listing's
    /// <c>woreda</c> nor its <c>locationName</c> (<see cref="SamePlace"/>), the buyer gets
    /// <c>ORDER_ROUTE_MISMATCH</c> with the order as the entity. An order is created once, so once per
    /// order. No trip (a self-pickup), or a name missing on either side: nothing.
    /// </summary>
    private async Task TryRecordOrderRouteMismatchAsync(
        SyncOperationRequest operation, Dictionary<string, object> listing,
        Dictionary<string, AttributeValue>? trip, string? buyerId, ILambdaContext context)
    {
        if (trip == null || string.IsNullOrEmpty(buyerId))
        {
            return;
        }

        var from = AttrString(trip, "fromWoreda");
        var woreda = ItemString(listing, "woreda");
        var place = ItemString(listing, "locationName");
        if (!IsRouteMismatch(from, woreda, place))
        {
            return;
        }

        await TryRecordFraudSignalAsync(
            buyerId, FraudDetectionService.OrderRouteMismatchSignal, operation.EntityId, "order",
            $"The trip starts in '{Short(from)}'; the listing is in '{Short(string.IsNullOrWhiteSpace(woreda) ? place : woreda)}'.",
            context);
    }

    /// <summary>
    /// Whether a trip from <paramref name="tripFrom"/> does not start at a listing in
    /// <paramref name="listingWoreda"/> (place <paramref name="listingPlace"/>): the trip's name is set, the
    /// listing has at least one name, and the trip's name matches neither.
    /// </summary>
    internal static bool IsRouteMismatch(string? tripFrom, string? listingWoreda, string? listingPlace)
    {
        if (string.IsNullOrWhiteSpace(tripFrom)
            || (string.IsNullOrWhiteSpace(listingWoreda) && string.IsNullOrWhiteSpace(listingPlace)))
        {
            return false;
        }

        return !SamePlace(tripFrom, listingWoreda) && !SamePlace(tripFrom, listingPlace);
    }

    /// <summary>Two place names as people type them: outer spaces, case and runs of inner spaces ignored.</summary>
    internal static bool SamePlace(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(Collapse(a), Collapse(b), StringComparison.OrdinalIgnoreCase);

        static string Collapse(string name)
            => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Short(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    /// <summary>
    /// BUG-14: a stored message on an order or delivery thread whose body holds a phone number
    /// (<see cref="Validation.ContactInText"/>) while the order is not yet delivered. Contact is open after
    /// the accept by design, so the message is stored as sent; the sender gets
    /// <c>CHAT_CONTACT_BEFORE_DELIVERY</c> with the order as the entity, at most once per order and
    /// sender. The signal never holds the message text. The order is read only for a body with a number.
    /// </summary>
    private async Task TryRecordChatContactAsync(
        string contextType, string contextId, string senderId, string? body, ILambdaContext context)
    {
        if (_fraudService == null || !Validation.ContactInText.ContainsPhoneNumber(body))
        {
            return;
        }

        var signalType = FraudDetectionService.ChatContactBeforeDeliverySignal;
        try
        {
            var orderId = contextType == "order" ? contextId : null;
            if (contextType == "delivery")
            {
                var delivery = await GetDeliveryItemAsync(contextId);
                orderId = delivery == null ? null : AttrString(delivery, "orderId");
            }

            if (string.IsNullOrEmpty(orderId))
            {
                return;
            }

            var order = await _orderRepo.GetByIdAsync(orderId);
            if (order == null || DeliveredOrLaterOrderStatuses.Contains(ItemString(order, "status")))
            {
                return;
            }

            // One attempt per order and sender in a batch, one row per order and sender in the table's life.
            if (!_batchSignals.Add($"{senderId}|{signalType}|{orderId}")
                || await _fraudService.HasSignalAsync(senderId, signalType, orderId))
            {
                return;
            }

            await _fraudService.RecordFraudSignalAsync(
                senderId, signalType, orderId, "order",
                $"A phone number in a message on the {contextType} thread before the order was delivered.");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to record fraud signal {signalType} for user {MoneyAlarmLog.LogId(senderId)}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// BUG-3 (rating-api.md 1.7.0): why a rating from <paramref name="raterId"/> for
    /// <paramref name="rateeId"/> stays out of the ratee's stored aggregate, or null when it counts. The two
    /// accounts are linked when they are one identity (<see cref="RelationshipPatternRules.SelfTradeReason"/>:
    /// the same phone number or Fayda ID) or have signed in on the same install
    /// (<see cref="RelationshipPatternRules.SharedDeviceCount"/> on <c>knownDevices</c>). A rater row that
    /// cannot be read counts the rating: the check never fails a rating.
    /// </summary>
    private async Task<string?> RatingLinkedAccountsReasonAsync(
        string raterId, string rateeId, UserDto? ratee, ILambdaContext context)
    {
        if (_userRepo == null || ratee == null)
        {
            return null;
        }

        try
        {
            var rater = await _userRepo.GetUserByIdAsync(raterId);
            if (RelationshipPatternRules.SelfTradeReason(raterId, rateeId, rater, ratee) is { } identity)
            {
                return identity;
            }

            return RelationshipPatternRules.SharedDeviceCount(rater, ratee) > 0 ? "shared_device" : null;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Rating linked-accounts check not made for ratee {MoneyAlarmLog.LogId(rateeId)}: [{ex.GetType().Name}]");
            return null;
        }
    }
}

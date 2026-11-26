using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GCR-03 (reputation-api.md 1.9.0 section 1.8, good-conduct-rewards.md 8 item 8): late evidence. A buyer's rating
/// that completes the order's ratings, or a pickup or drop-off photo attached, after the order's conduct stamp was
/// written re-evaluates that stamp once, within 14 days of it. Not flagged, best effort: it runs after the
/// operation's own write and never changes its answer.
/// </summary>
public partial class SyncFunction
{
    /// <summary>The conduct stamp's re-evaluation; null without the reputation-events table (tests set it).</summary>
    internal ConductStampService? ConductStamps { get; init; }

    private ConductStampService? BuildConductStamps(string? reputationEventsTable, string usersTable, bool fraudConfigured)
        => string.IsNullOrEmpty(reputationEventsTable)
            ? null
            : new ConductStampService(
                new ReputationEventRepository(_dynamoDb, reputationEventsTable, usersTable),
                _deliveryRepo, _orderRepo, _ratingRepo, _tripLocationRepo,
                fraudConfigured ? _fraudService : null);

    /// <summary>
    /// After a rating create: only the rating that completes the set the buyer's step asks for (the farmer, and the
    /// trucker of a trucked order) re-evaluates, so two ratings sent in one batch take the one re-evaluation together.
    /// </summary>
    private async Task ReevaluateConductAfterRatingAsync(
        Dictionary<string, object> order, string orderId, string rateeId, ILambdaContext context)
    {
        if (ConductStamps == null || _ratingRepo == null)
        {
            return;
        }

        try
        {
            var farmerId = order.TryGetValue("farmerId", out var f) ? f?.ToString() : null;
            var truckerId = PickupJudgement.IsSelfPickup(order) ? null
                : order.TryGetValue("truckerId", out var t) ? t?.ToString() : null;
            var buyerId = order.TryGetValue("buyerId", out var b) ? b?.ToString() : null;
            foreach (var ratee in new[] { farmerId, truckerId })
            {
                if (!string.IsNullOrEmpty(ratee) && ratee != rateeId
                    && await _ratingRepo.GetByIdAsync($"{orderId}:{ratee}", consistentRead: true) == null)
                {
                    return;
                }
            }

            // The rater first: the buyer's stamp holds the rating step.
            await ConductStamps.TryReevaluateAsync(orderId, new[] { buyerId, farmerId, truckerId }, context.Logger);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"{ConductStampService.FailedToken} order={MoneyAlarmLog.LogId(orderId)} step=rating [{ex.GetType().Name}]");
        }
    }

    /// <summary>After a pickup or drop-off photo attached: the delivery's order and parties, the photo's taker first.</summary>
    private async Task ReevaluateConductAfterPhotoAsync(Dictionary<string, AttributeValue> delivery, string type, ILambdaContext context)
    {
        var orderId = AttrString(delivery, "orderId");
        if (ConductStamps == null || orderId.Length == 0 || type is not (DeliveryPhotoKeys.Pickup or DeliveryPhotoKeys.Dropoff))
        {
            return;
        }

        var (farmerId, truckerId, buyerId) =
            (AttrString(delivery, "farmerId"), AttrString(delivery, "truckerId"), AttrString(delivery, "buyerId"));
        await ConductStamps.TryReevaluateAsync(orderId,
            type == DeliveryPhotoKeys.Pickup ? new[] { truckerId, farmerId, buyerId } : new[] { buyerId, farmerId, truckerId },
            context.Logger);
    }
}

using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-36 (spec 4.8 "Silent buyer", delivery-verification-api.md P.4, sync-protocol.md): the trucker's
/// drop-off check-in, behind <c>dropoffClassification</c>.
///
/// - The order's first move out of <c>accepted</c> after a trucker's pickup stamps it judgeable
///   (<see cref="DropoffStampAsync"/>): <c>dropoffJudgement: pending</c>, T8 and the drop-off points.
/// - <c>handoff_check_in</c> kind <c>arrived_dropoff</c> (create only, <c>{orderId}#arrived_dropoff</c>):
///   "I have arrived" at the drop-off from the order's trucker, with GPS within 5 km of the order's own
///   drop-off point or the trip's destination (as stamped at pickup), and the arrival photo already
///   attached (<c>delivery_photo</c> <c>{deliveryId}#arrival</c>). The first one is kept in the order's
///   <c>dropoffCheckIn</c> with the server's receipt time; the write moves neither the version nor
///   <c>updatedAt</c>, but it moves the next deadline from T8 to T9. One received after T8 is kept as late
///   evidence and never counted. With the flag off the kind does not exist.
/// The judgements run in the order cron (<see cref="DropoffJudgement.Decide"/>).
/// </summary>
public partial class SyncFunction
{
    /// <summary>ESC-36: <c>dropoffClassification</c> (env <c>DROPOFF_CLASSIFICATION_ENABLED</c>). Off by default.</summary>
    internal bool DropoffClassificationEnabled { get; init; } = DropoffJudgement.IsEnabled;

    /// <summary>The judgements need the commitments: they run in the order cron's commitment sweep.</summary>
    private bool DropoffJudgementOn => CommitmentsEnabled && DropoffClassificationEnabled;

    // ── the stamp at pickup ─────────────────────────────────────────────────

    /// <summary>
    /// ESC-36: the drop-off judgement's fields for a trucked order's first move out of <c>accepted</c>: T8
    /// from the delivery's <c>pickedUpAt</c> (the server's time of the pickup; now when it cannot be read; never
    /// before now plus <see cref="DropoffJudgement.LateStampGrace"/>), the order's own drop-off point and the
    /// trip's destination as they are now, so a check-in is measured against the place the parties agreed on at
    /// the handoff (FUP-204). A trip that cannot be read leaves its point out and the check-in falls back to the
    /// live trip; the advance is never refused for it.
    /// </summary>
    private async Task<Dictionary<string, object>> DropoffStampAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var pickedUpAt = now;
        TrackPoint? tripPoint = null;
        var tripRead = false;
        try
        {
            var deliveryId = ItemString(order, "activeDeliveryId");
            var delivery = deliveryId.Length == 0 ? null : await GetDeliveryItemAsync(deliveryId, consistentRead: true);
            if (delivery != null
                && OrderCommitment.TryParseUtc(AttrString(delivery, "pickedUpAt"), out var at)
                && at <= now)
            {
                pickedUpAt = at;
            }

            var tripId = ItemString(order, "tripId");
            if (tripId.Length > 0)
            {
                var trip = await GetTripItemAsync(tripId);
                tripPoint = TripTrack.PlannedDropoff(trip == null ? null : CropRepository.FromItem(trip), order: null);
                tripRead = trip != null;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Drop-off judgement: the delivery or trip of order {MoneyAlarmLog.LogId(ItemString(order, "orderId"))} "
                + $"could not be read [{ex.GetType().Name}]; stamped from now and the live trip.");
        }

        return DropoffJudgement.PickupStamp(pickedUpAt, OrderDeliveryLocation.Of(order), tripPoint, tripRead, OrderTimings.Current, now);
    }

    // ── handoff_check_in, kind arrived_dropoff ──────────────────────────────

    private async Task<SyncOperationResult> ProcessDropoffCheckInAsync(
        SyncOperationRequest operation, ParsedCheckIn checkIn, string? authenticatedUserId, ILambdaContext context)
    {
        var orderId = checkIn.OrderId;
        Dictionary<string, object>? order;
        try
        {
            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Drop-off check-in: order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The order could not be read. Please try again later.");
        }

        if (order == null)
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: The order was not found.");
        }

        var callerId = authenticatedUserId ?? string.Empty;
        if (callerId.Length == 0 || callerId != ItemString(order, "truckerId") || PickupJudgement.IsSelfPickup(order))
        {
            var refusal = CheckInResult(operation,
                $"{PickupJudgement.NotPartyCode}: Only the order's trucker checks in at the drop-off.");
            return IsPartyOfOrder(order, callerId)
                ? refusal
                : await RefuseWithSignalAsync(refusal, FraudDetectionService.RoleForbiddenSignal, operation,
                    PickupJudgement.EntityType, authenticatedUserId, context);
        }

        if (!DropoffJudgement.IsStamped(order))
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: Drop-off check-ins are not open for this order.");
        }

        // The first drop-off check-in is kept; a repeat gets the first one's answer.
        if (DropoffJudgement.CheckInOf(order) is { } kept)
        {
            return KeptDropoffAnswer(operation, order, kept);
        }

        var nowUtc = DateTime.UtcNow;
        var window = DropoffWindowOf(order, nowUtc);
        if (window == CheckInWindow.Closed)
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: This delivery is no longer on the road.");
        }

        var deliveryId = ItemString(order, "activeDeliveryId");
        Dictionary<string, AttributeValue>? delivery;
        try
        {
            delivery = deliveryId.Length == 0 ? null : await GetDeliveryItemAsync(deliveryId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Drop-off check-in: the delivery of order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The delivery could not be read. Please try again later.");
        }

        if (delivery == null
            || (window == CheckInWindow.Open && !DropoffJudgement.OnTheRoadDeliveryStatuses.Contains(AttrString(delivery, "status"))))
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: This delivery is no longer on the road.");
        }

        // Spec 4.8: the check-in needs the trucker's arrival photo, attached before it.
        if (string.IsNullOrEmpty(AttrString(delivery, DeliveryPhotoKeys.FieldOf(DeliveryPhotoKeys.Arrival))))
        {
            return CheckInResult(operation,
                $"{DropoffJudgement.PhotoRequiredCode}: Take the arrival photo first, then check in at the drop-off.");
        }

        IReadOnlyList<TrackPoint> points;
        try
        {
            points = await DropoffPointsAsync(order, delivery);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Drop-off check-in: the drop-off of order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The drop-off location could not be read. Please try again later.");
        }

        // Spec 4.8: GPS within 5 km of the drop-off. With no point to measure against (an order without its
        // own point on a trip without a destination) the check-in is kept unmeasured, and a silent buyer's
        // delivery is then never confirmed automatically (T9 disputes it instead).
        var distanceKm = DropoffJudgement.NearestKm(checkIn.Latitude!.Value, checkIn.Longitude!.Value, points);
        var late = window == CheckInWindow.Late;
        if (distanceKm is { } km && km > PickupJudgement.RadiusKm)
        {
            // FUP-204: the only point may be the buyer's own pin, which the buyer set. The first such attempt (photo
            // attached, before T8) is kept as server-only evidence: at T8 it sends the order to an admin instead
            // of the overdue dispute, so a pin far from where the buyer really is cannot cost the trucker.
            var attemptKept = !late && await KeepDropoffAttemptAsync(
                orderId, PickupJudgement.Entry(checkIn, callerId, nowUtc, distanceKm, late: false), context);
            if (attemptKept)
            {
                var attemptStatus = ItemString(order, "status");
                await EmitTxAuditAsync(context, orderId, "order", orderId, "handoff_check_in", attemptStatus, attemptStatus,
                    metadata: $"kind={PickupJudgement.Kind.ArrivedDropoff};outOfRange=true;distanceKm={DropoffJudgement.Km(km)}");
            }

            return CheckInResult(operation,
                $"{PickupJudgement.TooFarCode}: You are {DropoffJudgement.Km(km)} km from the drop-off (at most 5 km)."
                + (attemptKept ? " The attempt was kept for review." : string.Empty));
        }

        if (!await _orderRepo.TryRecordDropoffCheckInAsync(
                orderId, PickupJudgement.Entry(checkIn, callerId, nowUtc, distanceKm, late), whileOpen: !late))
        {
            // Another drop-off check-in landed first, or the judgement ran in between: read again.
            var fresh = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (fresh == null)
            {
                return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: The order was not found.");
            }

            if (DropoffJudgement.CheckInOf(fresh) is { } landed)
            {
                return KeptDropoffAnswer(operation, fresh, landed);
            }

            order = fresh;
            if (late || DropoffWindowOf(fresh, nowUtc) != CheckInWindow.Late)
            {
                return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: This delivery is no longer on the road.");
            }

            late = true;
            if (!await _orderRepo.TryRecordDropoffCheckInAsync(
                    orderId, PickupJudgement.Entry(checkIn, callerId, nowUtc, distanceKm, late: true), whileOpen: false))
            {
                return TransientOrderError(operation, "The check-in could not be saved. Please try again later.");
            }
        }

        var measured = distanceKm != null;
        var status = ItemString(order, "status");
        await EmitTxAuditAsync(context, orderId, "order", orderId, "handoff_check_in", status, status,
            metadata: $"kind={PickupJudgement.Kind.ArrivedDropoff};distanceChecked={(measured ? "true" : "false")};late={(late ? "true" : "false")}");

        if (!late)
        {
            context.Logger.LogInformation(
                $"CHECKIN_RECORDED orderId={MoneyAlarmLog.LogId(orderId)} kind={PickupJudgement.Kind.ArrivedDropoff} distanceChecked={measured}");
            return CheckInSuccess(operation, order);
        }

        // Late evidence: kept on the order, never counted. The overdue delivery is already a dispute an
        // admin decides, so it needs no queue entry of its own.
        context.Logger.LogInformation(
            $"CHECKIN_LATE orderId={MoneyAlarmLog.LogId(orderId)} kind={PickupJudgement.Kind.ArrivedDropoff} queued=false");
        return DropoffTooLateResult(operation);
    }

    private static SyncOperationResult DropoffTooLateResult(SyncOperationRequest operation)
        => CheckInResult(operation,
            $"{PickupJudgement.TooLateCode}: The delivery deadline passed before this check-in reached the server. It was kept for review.");

    /// <summary>The answer to a drop-off check-in when one is stored: the stored one's (a late one stays late).</summary>
    private static SyncOperationResult KeptDropoffAnswer(
        SyncOperationRequest operation, Dictionary<string, object> order, IReadOnlyDictionary<string, object> kept)
        => PickupJudgement.IsLateEntry(kept) ? DropoffTooLateResult(operation) : CheckInSuccess(operation, order);

    /// <summary>
    /// <see cref="CheckInWindow.Open"/> while the order is on the road, waiting for its judgement, and the
    /// server clock is not past T8; <see cref="CheckInWindow.Late"/> once T8 passed, or after the overdue
    /// judgement (or its admin queue entry); <see cref="CheckInWindow.Closed"/> otherwise (delivered,
    /// disputed by a party, or judged at T9).
    /// </summary>
    private static CheckInWindow DropoffWindowOf(Dictionary<string, object> order, DateTime nowUtc)
    {
        if (DropoffJudgement.IsPending(order)
            && DropoffJudgement.OnTheRoadOrderStatuses.Contains(ItemString(order, "status"))
            && DropoffJudgement.DeadlineOf(order) is { } deadline)
        {
            return nowUtc <= deadline ? CheckInWindow.Open : CheckInWindow.Late;
        }

        return ItemString(order, DropoffJudgement.Field) is DropoffJudgement.Outcome.DeliveryOverdue or DropoffJudgement.Outcome.Queued
            ? CheckInWindow.Late
            : CheckInWindow.Closed;
    }

    /// <summary>
    /// The points a drop-off check-in is measured against, point by point: the ones stamped at pickup; for
    /// the order's own point not stamped, the order's live one (only the buyer sets it, and a nearer extra
    /// point only helps the trucker); for the trip's destination not stamped, the live one only when the
    /// stamp could not read the trip (<see cref="DropoffJudgement.ReadsLiveTripPoint"/>: a destination the
    /// trucker adds after the handoff never counts).
    /// </summary>
    private async Task<IReadOnlyList<TrackPoint>> DropoffPointsAsync(
        Dictionary<string, object> order, Dictionary<string, AttributeValue> delivery)
    {
        var points = DropoffJudgement.StampedPoints(order).ToList();
        if (DropoffJudgement.ReadsLiveOrderPoint(order) && OrderDeliveryLocation.Of(order) is { } own)
        {
            points.Add(own);
        }

        var tripId = AttrString(delivery, "tripId");
        if (DropoffJudgement.ReadsLiveTripPoint(order)
            && tripId.Length > 0
            && TripTrack.PlannedDropoff(await GetTripItemAsync(tripId) is { } trip ? CropRepository.FromItem(trip) : null, order: null) is { } destination)
        {
            points.Add(destination);
        }

        return points;
    }

    /// <summary>FUP-204: keep the out-of-range attempt (best effort; false when it was not stored).</summary>
    private async Task<bool> KeepDropoffAttemptAsync(string orderId, Dictionary<string, object> entry, ILambdaContext context)
    {
        try
        {
            return await _orderRepo.TryRecordDropoffAttemptAsync(orderId, entry);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Drop-off check-in: the out-of-range attempt of order {MoneyAlarmLog.LogId(orderId)} could not be kept [{ex.GetType().Name}].");
            return false;
        }
    }
}

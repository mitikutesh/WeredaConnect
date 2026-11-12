using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-34 (spec 4.8, delivery-verification-api.md 2.6.0, sync-protocol.md 1.55.0): the arrival
/// check-ins of an accepted order, behind <c>pickupClassification</c>.
///
/// - The transition to <c>accepted</c> stamps the order judgeable (<see cref="StampPickupJudgementAsync"/>).
/// - <c>handoff_check_in</c> (create only, <c>{orderId}#{kind}</c>): "I have arrived" from the trucker, or
///   the buyer on a self-pickup order, and "Crops are ready" from the farmer. The first check-in of a kind
///   is kept in the order's <c>checkIns</c> map with the server's receipt time; the write moves neither the
///   version nor <c>updatedAt</c>. A check-in that reaches the server after the deadline is kept as late
///   evidence and never counted.
/// - A pickup scan that reaches a judged order is refused <c>ORDER_DEADLINE_PASSED</c>
///   (<see cref="RefuseLatePickupAsync"/>).
/// The judgement itself runs in the order cron (<see cref="PickupJudgement.Decide"/>).
/// </summary>
public partial class SyncFunction
{
    /// <summary>ESC-34: <c>pickupClassification</c> (env <c>PICKUP_CLASSIFICATION_ENABLED</c>). Off by default.</summary>
    internal bool PickupClassificationEnabled { get; init; } = PickupJudgement.IsEnabled;

    /// <summary>The judgement needs the commitments: it parks a trucker no-show in <c>awaiting_transport</c>.</summary>
    private bool PickupJudgementOn => CommitmentsEnabled && PickupClassificationEnabled;

    // ── the stamp at acceptance ─────────────────────────────────────────────

    /// <summary>
    /// ESC-34: an order that becomes <c>accepted</c> while the flag is on is judged by its check-ins: the
    /// write that accepts it also sets <c>pickupJudgement: pending</c>, an empty <c>checkIns</c> map and
    /// the listing's point as it is now, so the check-ins are measured against the farm the parties agreed
    /// on (a farmer who moves the listing later cannot push the trucker out of range, FUP-204). A listing
    /// that cannot be read leaves the point out and the check-ins fall back to the live listing; the
    /// accept is never refused for it. With the flag off nothing is added.
    /// </summary>
    private async Task StampPickupJudgementAsync(
        Dictionary<string, object> existingOrder, Dictionary<string, object> fields, ILambdaContext context)
    {
        if (!PickupJudgementOn)
        {
            return;
        }

        fields[PickupJudgement.Field] = PickupJudgement.Outcome.Pending;
        fields[PickupJudgement.CheckInsField] = new Dictionary<string, object>();

        var listingId = ItemString(existingOrder, "listingId");
        if (listingId.Length == 0)
        {
            return;
        }

        try
        {
            if (PickupJudgement.PointFor(new Dictionary<string, object>(), await _cropRepo.GetByIdAsync(listingId)) is { } point)
            {
                fields[PickupJudgement.PickupLatitudeField] = point.Latitude;
                fields[PickupJudgement.PickupLongitudeField] = point.Longitude;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Pickup judgement: listing {MoneyAlarmLog.LogId(listingId)} of order {MoneyAlarmLog.LogId(ItemString(existingOrder, "orderId"))} "
                + $"could not be read [{ex.GetType().Name}]; its check-ins use the live listing.");
        }
    }

    // ── handoff_check_in ────────────────────────────────────────────────────

    private async Task<SyncOperationResult> ProcessHandoffCheckInAsync(
        SyncOperationRequest operation, string? authenticatedUserId, ILambdaContext context)
    {
        if (operation.Action != "create")
        {
            return CheckInResult(operation, $"{PickupJudgement.ValidationCode}: A check-in is sent with action create.");
        }

        var errors = PickupJudgement.Parse(operation.EntityId, operation.Data ?? new Dictionary<string, object>(), out var checkIn);
        if (errors.Count > 0 || checkIn == null)
        {
            return CheckInResult(operation, $"{PickupJudgement.ValidationCode}: {string.Join(" ", errors)}");
        }

        var orderId = checkIn.OrderId;
        Dictionary<string, object>? order;
        try
        {
            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Check-in: order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The order could not be read. Please try again later.");
        }

        if (order == null)
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: The order was not found.");
        }

        var callerId = authenticatedUserId ?? string.Empty;
        if (callerId.Length == 0 || PickupJudgement.PartyOf(order, checkIn.Kind) != callerId)
        {
            var refusal = CheckInResult(operation, checkIn.Kind == PickupJudgement.Kind.CropsReady
                ? $"{PickupJudgement.NotPartyCode}: Only the order's farmer says the crops are ready."
                : $"{PickupJudgement.NotPartyCode}: Only the order's trucker (or the buyer collecting it) says they have arrived.");
            return IsPartyOfOrder(order, callerId)
                ? refusal
                : await RefuseWithSignalAsync(refusal, FraudDetectionService.RoleForbiddenSignal, operation,
                    PickupJudgement.EntityType, authenticatedUserId, context);
        }

        if (!PickupJudgementOn || !PickupJudgement.IsStamped(order))
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: Arrival check-ins are not open for this order.");
        }

        // The first check-in of a kind is kept; a repeat (another tap, another phone) changes nothing and
        // gets the first one's answer.
        if (PickupJudgement.EntryOf(order, checkIn.Kind) is { } kept)
        {
            return KeptCheckInAnswer(operation, order, kept);
        }

        var nowUtc = DateTime.UtcNow;
        var window = CheckInWindowOf(order, nowUtc);
        if (window == CheckInWindow.Closed)
        {
            return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: This order is no longer waiting for its pickup.");
        }

        if (window == CheckInWindow.Open && PickupJudgement.OpensAt(order) is { } opensAt && nowUtc < opensAt)
        {
            return CheckInResult(operation,
                $"{PickupJudgement.TooEarlyCode}: Check-ins open at {OrderCommitment.Iso(opensAt)}, 24 hours before the pickup day.");
        }

        TrackPoint? point;
        try
        {
            point = await CheckInPointAsync(order, checkIn);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Check-in: the listing of order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The farm's location could not be read. Please try again later.");
        }

        var distanceKm = PickupJudgement.DistanceKm(checkIn, point);
        if (distanceKm is { } km && km > PickupJudgement.RadiusKm)
        {
            return CheckInResult(operation,
                $"{PickupJudgement.TooFarCode}: You are {km.ToString("F1", CultureInfo.InvariantCulture)} km from the farm (at most 5 km).");
        }

        var late = window == CheckInWindow.Late;
        var createdAt = ItemString(order, "createdAt");
        var written = await _orderRepo.TryRecordCheckInAsync(
            orderId, createdAt, checkIn.Kind, PickupJudgement.Entry(checkIn, callerId, nowUtc, distanceKm, late), whileOpen: !late);
        if (!written)
        {
            // Another check-in of this kind landed first, or the judgement ran in between: read again.
            var fresh = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (fresh == null)
            {
                return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: The order was not found.");
            }

            if (PickupJudgement.EntryOf(fresh, checkIn.Kind) is { } landed)
            {
                return KeptCheckInAnswer(operation, fresh, landed);
            }

            order = fresh;
            if (late || CheckInWindowOf(fresh, nowUtc) != CheckInWindow.Late)
            {
                return CheckInResult(operation, $"{PickupJudgement.NotOpenCode}: This order is no longer waiting for its pickup.");
            }

            late = true;
            if (!await _orderRepo.TryRecordCheckInAsync(
                    orderId, createdAt, checkIn.Kind, PickupJudgement.Entry(checkIn, callerId, nowUtc, distanceKm, late: true), whileOpen: false))
            {
                return TransientOrderError(operation, "The check-in could not be saved. Please try again later.");
            }
        }

        var status = ItemString(order, "status");
        await EmitTxAuditAsync(context, orderId, "order", orderId, "handoff_check_in", status, status,
            metadata: $"kind={checkIn.Kind};distanceChecked={(distanceKm != null ? "true" : "false")};late={(late ? "true" : "false")}");

        if (!late)
        {
            context.Logger.LogInformation(
                $"CHECKIN_RECORDED orderId={MoneyAlarmLog.LogId(orderId)} kind={checkIn.Kind} distanceChecked={distanceKm != null}");
            return CheckInSuccess(operation, order);
        }

        // Late evidence: kept on the order, never counted. An admin looks when it contradicts a judgement.
        var queued = PickupJudgement.IsJudged(order)
                     && PickupJudgement.Contradicts(ItemString(order, PickupJudgement.Field), checkIn.Kind)
                     && await EnterLateEvidenceQueueAsync(order, context);
        context.Logger.LogInformation(
            $"CHECKIN_LATE orderId={MoneyAlarmLog.LogId(orderId)} kind={checkIn.Kind} queued={queued}");
        return TooLateResult(operation);
    }

    private static SyncOperationResult TooLateResult(SyncOperationRequest operation)
        => CheckInResult(operation,
            $"{PickupJudgement.TooLateCode}: The pickup deadline passed before this check-in reached the server. It was kept for review.");

    /// <summary>The answer to a check-in whose kind is already stored: the stored one's (a late one stays late).</summary>
    private static SyncOperationResult KeptCheckInAnswer(
        SyncOperationRequest operation, Dictionary<string, object> order, IReadOnlyDictionary<string, object> kept)
        => PickupJudgement.IsLateEntry(kept) ? TooLateResult(operation) : CheckInSuccess(operation, order);

    /// <summary>Where a check-in stands against the order: before the deadline, after it, or no longer taken at all.</summary>
    private enum CheckInWindow
    {
        Open,
        Late,
        Closed
    }

    /// <summary>
    /// <see cref="CheckInWindow.Open"/> while the order is <c>accepted</c>, waiting for its judgement and the
    /// server clock is not past the deadline; <see cref="CheckInWindow.Late"/> once that deadline passed or a
    /// no-show judgement was made; <see cref="CheckInWindow.Closed"/> otherwise (picked up, the transport or
    /// the order ended some other way, or no deadline to judge by).
    /// </summary>
    private static CheckInWindow CheckInWindowOf(Dictionary<string, object> order, DateTime nowUtc)
    {
        var deadline = PickupJudgement.DeadlineOf(order);
        if (PickupJudgement.IsPending(order) && ItemString(order, "status") == "accepted" && deadline != null)
        {
            return nowUtc <= deadline.Value ? CheckInWindow.Open : CheckInWindow.Late;
        }

        return PickupJudgement.IsJudged(order)
               && ItemString(order, PickupJudgement.Field) is not (PickupJudgement.Outcome.Scanned or PickupJudgement.Outcome.Pending)
            ? CheckInWindow.Late
            : CheckInWindow.Closed;
    }

    /// <summary>The point the check-in is measured against; the live listing is read only when the order has none stamped and the check-in has a fix.</summary>
    private async Task<TrackPoint?> CheckInPointAsync(Dictionary<string, object> order, ParsedCheckIn checkIn)
    {
        if (PickupJudgement.PointFor(order, null) is { } stamped)
        {
            return stamped;
        }

        var listingId = ItemString(order, "listingId");
        return checkIn.Latitude == null || listingId.Length == 0
            ? null
            : PickupJudgement.PointFor(order, await _cropRepo.GetByIdAsync(listingId));
    }

    private static bool IsPartyOfOrder(Dictionary<string, object> order, string callerId)
        => callerId.Length > 0
           && (callerId == ItemString(order, "buyerId")
               || callerId == ItemString(order, "farmerId")
               || callerId == ItemString(order, "truckerId"));

    private static SyncOperationResult CheckInResult(SyncOperationRequest operation, string message)
        => new() { OperationId = operation.OperationId, Status = "error", Message = message };

    private static SyncOperationResult CheckInSuccess(SyncOperationRequest operation, Dictionary<string, object> order)
        => new() { OperationId = operation.OperationId, Status = "success", ServerVersion = OrderVersionOf(order) };

    // ── late evidence ───────────────────────────────────────────────────────

    /// <summary>
    /// ESC-34: put a judged order in the admin queue as <c>late_pickup_evidence</c> (no version bump, only
    /// while it is not queued yet and still in the status read). An order already queued keeps its entry;
    /// the audit trail carries the late evidence either way. Best effort: never throws.
    /// </summary>
    private async Task<bool> EnterLateEvidenceQueueAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        var orderId = ItemString(order, "orderId");
        var status = ItemString(order, "status");
        try
        {
            var queued = await _orderRepo.TryEnterAdminQueueAsync(
                orderId, PickupJudgement.ReasonLateEvidence, $"escrow-order-{orderId}", OrderCommitment.Iso(DateTime.UtcNow), status);
            await EmitTxAuditAsync(context, orderId, "order", orderId, queued ? "admin_queue_entered" : "late_pickup_evidence",
                status, status, ctxOverride: AuditContext.ForSystem(),
                metadata: $"reason={PickupJudgement.ReasonLateEvidence};judgement={ItemString(order, PickupJudgement.Field)}");
            return queued;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Late pickup evidence: order {MoneyAlarmLog.LogId(orderId)} could not enter the admin queue [{ex.GetType().Name}].");
            return false;
        }
    }

    /// <summary>
    /// ESC-34 (spec 4.8 "Late evidence", edge case D10): the pickup scan of a delivery whose order was
    /// already judged a no-show, or sent to the admin queue, is refused with <c>ORDER_DEADLINE_PASSED</c>
    /// and nothing is written. The clients treat it as a permanent delivery refusal and put the delivery
    /// back from the server (sync-protocol.md 1.55.0). A scan whose QR nonce matches the farmer's stored
    /// nonce is real evidence of a handoff: the order enters the admin queue (<c>late_pickup_evidence</c>)
    /// and the alarmed <c>ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED</c> line names it, because the goods may
    /// have left the farm on an order whose money or transport moved on. A typed code is never taken as
    /// such evidence here: it is not counted against the code's miss limit on this path, and the queue
    /// entry is readable by the caller. Returns null to let the normal flow go on: the flag is off, this
    /// is not the pickup leg of this caller, or the order was not judged (or judged picked up).
    /// </summary>
    private async Task<SyncOperationResult?> RefuseLatePickupAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string currentStatus,
        string? authenticatedUserId, ILambdaContext context)
    {
        if (!PickupJudgementOn || operation.Data == null || currentStatus is not ("pending" or "cancelled"))
        {
            return null;
        }

        var requested = operation.Data.TryGetValue("status", out var statusObj) ? statusObj?.ToString() : null;
        var pickupParty = requested switch
        {
            "pickedUp" => AttrString(delivery, "truckerId"),
            "delivered" when AttrString(delivery, "deliveryType") == SelfPickupDeliveryType => AttrString(delivery, "buyerId"),
            _ => string.Empty
        };
        var orderId = AttrString(delivery, "orderId");
        if (pickupParty.Length == 0 || authenticatedUserId != pickupParty || orderId.Length == 0)
        {
            return null;
        }

        Dictionary<string, object>? order;
        try
        {
            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Late pickup check: order {MoneyAlarmLog.LogId(orderId)} could not be read [{ex.GetType().Name}]; the scan waits.");
            return TransientOrderError(operation, "The order could not be read. Please try again later.");
        }

        if (order == null
            || !PickupJudgement.IsJudged(order)
            || ItemString(order, PickupJudgement.Field) is PickupJudgement.Outcome.Scanned or PickupJudgement.Outcome.Pending)
        {
            return null;
        }

        var evidence = await IsRealPickupScanAsync(operation, context);
        var queued = false;
        if (evidence)
        {
            queued = await EnterLateEvidenceQueueAsync(order, context);
            context.Logger.LogError(
                $"ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED orderId={MoneyAlarmLog.LogId(orderId)} "
                + $"deliveryId={MoneyAlarmLog.LogId(operation.EntityId)} status={MoneyAlarmLog.LogId(requested)} "
                + $"reason={PickupJudgement.ReasonLateEvidence} judgement={ItemString(order, PickupJudgement.Field)}");
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = evidence
                ? $"{OrderCommitment.DeadlinePassedCode}: The pickup of this order was already judged, so the scan was not applied. "
                  + (queued ? "It was sent to support to review." : "Support has been told.")
                : $"{OrderCommitment.DeadlinePassedCode}: The pickup of this order was already judged, so the scan was not applied. "
                  + "Contact support if the crops changed hands."
        };
    }

    /// <summary>
    /// Whether the scan carries the farmer's stored QR nonce (compared in fixed time). The nonce is read,
    /// never consumed: nothing is written for a refused scan.
    /// </summary>
    private async Task<bool> IsRealPickupScanAsync(SyncOperationRequest operation, ILambdaContext context)
    {
        var scanned = operation.Data != null && operation.Data.TryGetValue("scannedQrNonce", out var nonceObj)
            ? nonceObj?.ToString()
            : null;
        if (string.IsNullOrEmpty(scanned) || _qrSecretRepo == null)
        {
            return false;
        }

        try
        {
            var secret = await _qrSecretRepo.GetAsync(operation.EntityId);
            return secret != null
                   && !string.IsNullOrEmpty(secret.Nonce)
                   && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret.Nonce), Encoding.UTF8.GetBytes(scanned));
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Late pickup check: the QR secret of delivery {MoneyAlarmLog.LogId(operation.EntityId)} could not be read [{ex.GetType().Name}].");
            return false;
        }
    }
}

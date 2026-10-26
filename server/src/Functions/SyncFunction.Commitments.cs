using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-15 / ESC-17: the sequential three-party commitments of a paid order and the trip bookings
/// behind them. The rules themselves are in <see cref="OrderCommitment"/>; this part of the sync
/// handler reads the row, runs the decision, and performs the writes and side effects it names.
/// </summary>
public partial class SyncFunction
{
    /// <summary>Order update keys a commitment command may carry besides the command itself.</summary>
    private static readonly HashSet<string> CommitmentCommandEchoFields = new(StringComparer.Ordinal)
    {
        "orderId", "updatedAt", "version", "clientVersion", "status", "respondedAt",
        OrderCommitment.DeclineReasonField, "rejectionReason"
    };

    /// <summary>
    /// The command carried under <paramref name="field"/> (<c>accept</c> or <c>decline</c>), or null
    /// when the payload has no such key. The stored response values are accepted as aliases so a
    /// client that echoes <c>accepted</c> is understood.
    /// </summary>
    private static string? CommitmentCommandOf(Dictionary<string, object> data, string field)
    {
        if (!data.TryGetValue(field, out var raw))
        {
            return null;
        }

        return raw?.ToString()?.Trim().ToLowerInvariant() switch
        {
            "accept" or "accepted" => OrderCommitment.AcceptCommand,
            "decline" or "declined" or "reject" or "rejected" => OrderCommitment.DeclineCommand,
            var other => other ?? string.Empty
        };
    }

    /// <summary>
    /// Run a farmer or trucker command against the order: validate the payload, decide with
    /// <see cref="OrderCommitment.Decide"/>, write with the client's version, then run the side
    /// effects (trip booking, delivery, refund, stock) and audit the hop.
    /// </summary>
    private async Task<SyncOperationResult> ProcessCommitmentCommandAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> existingOrder,
        CommitmentActor actor,
        string command,
        string authenticatedUserId,
        ILambdaContext context)
    {
        var data = operation.Data!;
        var commandField = actor == CommitmentActor.Farmer
            ? OrderCommitment.FarmerResponseField
            : OrderCommitment.TruckerResponseField;
        var unknown = data.Keys
            .Where(k => k != commandField && !CommitmentCommandEchoFields.Contains(k))
            .ToList();
        if (unknown.Count > 0)
        {
            return OrderValidationError(operation, $"Unknown fields: {string.Join(", ", unknown)}");
        }

        if (data.TryGetValue("orderId", out var echoedOrderId)
            && !string.Equals(echoedOrderId?.ToString(), operation.EntityId, StringComparison.Ordinal))
        {
            return OrderValidationError(operation, "orderId does not match the operation entityId.");
        }

        if (!TryGetClientVersion(operation, out var version))
        {
            return OrderValidationError(operation,
                "version is required for order updates (send clientVersion or data.version).");
        }

        var declineReason = data.TryGetValue(OrderCommitment.DeclineReasonField, out var reasonObj)
            ? reasonObj?.ToString()?.Trim().ToLowerInvariant()
            : null;
        if (string.IsNullOrEmpty(declineReason))
        {
            declineReason = null;
        }

        var orderId = operation.EntityId;
        var nowUtc = DateTime.UtcNow;
        var tripId = ItemString(existingOrder, "tripId");
        Dictionary<string, AttributeValue>? trip = null;
        DateTime? departure = null;
        if (tripId.Length > 0)
        {
            try
            {
                trip = await GetTripItemAsync(tripId);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"Commitment: trip {MoneyAlarmLog.LogId(tripId)} of order {MoneyAlarmLog.LogId(orderId)} could not be read: [{ex.GetType().Name}]");
                return TransientOrderError(operation, "The order's trip could not be read. Please try again later.");
            }

            if (trip != null && AttrString(trip, "deletedAt").Length == 0
                && TryParseDepartureDateUtc(AttrString(trip, "departureDate"), requireIsoShape: false, out var departureUtc))
            {
                departure = departureUtc;
            }
        }

        var decision = OrderCommitment.Decide(
            existingOrder, actor, command, nowUtc, departure, declineReason, IsFreshSignIn());
        if (decision.IsError)
        {
            var refusal = new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = decision.ErrorMessage
            };
            return decision.ErrorCode == "ORDER_UNAUTHORIZED"
                ? await RefuseWithSignalAsync(refusal, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context)
                : refusal;
        }

        if (decision.IsNoOp)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = OrderVersionOf(existingOrder),
                ServerData = existingOrder
            };
        }

        var fields = decision.Fields!;
        var priorStatus = ItemString(existingOrder, "status");

        // The farmer's reject keeps its optional free-text reason (same 500-character cap as the
        // legacy write, ValidateOrderFields); a decline code may ride along.
        if (actor == CommitmentActor.Farmer && decision.NewStatus == "rejected"
            && data.TryGetValue("rejectionReason", out var rejectionReason)
            && !string.IsNullOrWhiteSpace(rejectionReason?.ToString()))
        {
            var reasonText = rejectionReason.ToString()!.Trim();
            if (reasonText.Length > 500)
            {
                return OrderValidationError(operation, "rejectionReason must not exceed 500 characters.");
            }

            fields["rejectionReason"] = reasonText;
        }

        // Anything that lands the order in accepted needs the escrow check and the pickup clock
        // (the same two gates the legacy status write runs).
        EscrowRowKey? acceptFence = null;
        if (decision.NewStatus == "accepted")
        {
            var (escrowStop, fence) = await CheckAcceptEscrowAsync(operation, context);
            if (escrowStop != null)
            {
                return escrowStop;
            }

            acceptFence = fence;
            var pickupDeadline = await ComputePickupDeadlineAsync(existingOrder, nowUtc, context);
            if (pickupDeadline == null)
            {
                return TransientOrderError(operation, "The order's trip could not be read. Please try again later.");
            }

            fields[PickupDeadline.Field] = PickupDeadline.Format(pickupDeadline.Value);
        }
        else if (actor == CommitmentActor.Farmer && command == OrderCommitment.AcceptCommand)
        {
            // Accepting a trip order asks the trucker; a held escrow still blocks it.
            var (escrowStop, _) = await CheckAcceptEscrowAsync(operation, context);
            if (escrowStop != null)
            {
                return escrowStop;
            }
        }

        // The trucker dropping out of a committed order: nothing may have left the farm, and the
        // pending delivery goes first so no pickup can land on it.
        string? cancelledDeliveryId = null;
        if (decision.Effect == CommitmentEffect.ReleaseBookingAndCancelDelivery)
        {
            var activeDeliveryId = ItemString(existingOrder, "activeDeliveryId");
            List<string> pending;
            try
            {
                var deliveries = await ReadOrderDeliveriesAsync(existingOrder, activeDeliveryId);
                if (deliveries.PickedUp)
                {
                    return CancelNotAllowed(operation, "The crops were already picked up.");
                }

                pending = deliveries.Pending;
            }
            catch (Exception ex)
            {
                return TransportCancelUnavailable(operation, context, orderId, "delivery", ex);
            }

            foreach (var pendingId in pending)
            {
                try
                {
                    if (await _deliveryRepo.TryCancelPendingAsync(pendingId))
                    {
                        await EmitTxAuditAsync(context, orderId, "delivery", pendingId,
                            "delivery_cancelled", "pending", "cancelled",
                            metadata: $"reason={OrderCommitment.TransportFailure.TruckerCancelled}");
                        if (pendingId == activeDeliveryId)
                        {
                            cancelledDeliveryId = pendingId;
                        }
                    }
                    else if (!IsBeforePickup(DeliveryStatusOf(await GetDeliveryItemAsync(pendingId, consistentRead: true) ?? new())))
                    {
                        return CancelNotAllowed(operation, "The crops were already picked up.");
                    }
                }
                catch (Exception ex)
                {
                    return TransportCancelUnavailable(operation, context, orderId, "delivery", ex);
                }
            }
        }

        // The write. A trucker accept confirms the booking in the same transaction: the trip must
        // still hold this order's quintals and still be open for it.
        Dictionary<string, object>? written;
        if (decision.Effect == CommitmentEffect.ConfirmBooking)
        {
            var confirm = await WriteTruckerAcceptAsync(operation, existingOrder, trip, fields, version, nowUtc, acceptFence, context);
            if (confirm.Stop != null)
            {
                return confirm.Stop;
            }

            written = confirm.Order;
        }
        else
        {
            var updateResult = acceptFence != null
                ? await _orderRepo.UpdateUnlessEscrowHeldAsync(orderId, fields, version, acceptFence)
                : await _orderRepo.UpdateAsync(orderId, fields, version);
            if (!updateResult.IsSuccess && updateResult.Error?.Code == OrderRepository.EscrowHeldCode)
            {
                return OrderValidationError(operation, OrderUnderReviewMessage);
            }

            if (!updateResult.IsSuccess)
            {
                var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                return latest == null
                    ? TransientOrderError(operation, "Order not found.")
                    : OrderConflict(operation, latest, version);
            }

            written = updateResult.Value;
        }

        var appliedStatus = written != null ? ItemString(written, "status") : decision.NewStatus ?? priorStatus;
        await EmitTxAuditAsync(context, orderId, "order", orderId, decision.AuditAction, priorStatus, appliedStatus,
            metadata: $"{(actor == CommitmentActor.Farmer ? "farmer" : "trucker")}={command}"
                      + (declineReason != null ? $";reason={declineReason}" : string.Empty));

        // Side effects after the write, each idempotent and best-effort.
        if (appliedStatus == "rejected")
        {
            await TryScheduleOrderEscrowRefundAsync(orderId, "order_rejected", context);
            await TryRestoreListingStockAsync(existingOrder, context);
        }

        if (decision.Effect is CommitmentEffect.ReleaseBooking or CommitmentEffect.ReleaseBookingAndCancelDelivery)
        {
            await TryReleaseBookingAsync(existingOrder, context);
        }

        if (cancelledDeliveryId != null)
        {
            await ReleaseTransportCancelSlotAsync(orderId, cancelledDeliveryId, context);
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = written != null ? OrderVersionOf(written) : version + 1,
            ServerData = written
        };
    }

    /// <summary>
    /// The trucker's accept: order <c>funded → accepted</c> and the trip booking confirmed in one
    /// transaction (<see cref="ITripRepository.BuildConfirmBookingItem"/>). When the trip refuses:
    /// a booking the funding step never managed to reserve is reserved now and the write retried
    /// once; a full trip or a gone trip parks the order in <c>awaiting_transport</c> instead, with
    /// the client's version, so the trucker's screen shows what happened.
    /// </summary>
    private async Task<(SyncOperationResult? Stop, Dictionary<string, object>? Order)> WriteTruckerAcceptAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> existingOrder,
        Dictionary<string, AttributeValue>? trip,
        Dictionary<string, object> fields,
        int version,
        DateTime nowUtc,
        EscrowRowKey? acceptFence,
        ILambdaContext context)
    {
        var orderId = operation.EntityId;
        var tripId = ItemString(existingOrder, "tripId");
        var quintals = ParseDouble(existingOrder, "quantity") / KgPerQuintal;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (trip == null || AttrString(trip, "deletedAt").Length > 0 || AttrString(trip, "status") is not ("open" or "matched"))
            {
                return await ParkTransportFailedAsync(operation, existingOrder, version, nowUtc,
                    OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, context);
            }

            // FRD-03: with the hold on, the escrow is checked again inside the transaction, as the
            // farmer's accept does through UpdateUnlessEscrowHeldAsync.
            var linked = new List<TransactWriteItem>
            {
                _tripRepo.BuildConfirmBookingItem(tripId, AttrString(trip, "createdAt"), orderId, OrderCommitment.Iso(nowUtc))
            };
            if (acceptFence != null)
            {
                linked.Add(OrderRepository.EscrowNotHeldCheck(acceptFence));
            }

            OrderLinkedWriteResult write;
            try
            {
                write = await _orderRepo.TryUpdateWithAsync(orderId, fields, version, "funded", linked);
            }
            catch (Exception ex)
            {
                return (TransportCancelUnavailable(operation, context, orderId, "order", ex), null);
            }

            switch (write.Outcome)
            {
                case OrderLinkedWriteOutcome.Written:
                    return (null, write.Order);
                case OrderLinkedWriteOutcome.OrderNotFound:
                    return (TransientOrderError(operation, "Order not found."), null);
                case OrderLinkedWriteOutcome.OrderChanged:
                    {
                        var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                        return (latest == null
                            ? TransientOrderError(operation, "Order not found.")
                            : OrderConflict(operation, latest, version), null);
                    }
            }

            // A linked item refused. With the fence on, a sweep may have held the escrow since the
            // check: that is a review refusal, not a trip problem.
            if (acceptFence != null && _escrowRepo != null)
            {
                Dictionary<string, object>? escrowNow;
                try
                {
                    escrowNow = await _escrowRepo.GetByIdAsync(acceptFence.EscrowId, consistentRead: true);
                }
                catch (Exception ex)
                {
                    return (TransportCancelUnavailable(operation, context, orderId, "escrow", ex), null);
                }

                if (escrowNow != null && escrowNow.TryGetValue("status", out var escrowStatus)
                    && escrowStatus?.ToString() == EscrowHoldFields.Status)
                {
                    return (OrderValidationError(operation, OrderUnderReviewMessage), null);
                }
            }

            // The trip refused: it no longer holds this booking, is full, or is gone.
            if (attempt == 2)
            {
                break;
            }

            TripReserveResult reserve;
            try
            {
                reserve = await _tripRepo.TryReserveCapacityAsync(tripId, orderId, quintals);
                trip = await GetTripItemAsync(tripId);
            }
            catch (Exception ex)
            {
                return (TransportCancelUnavailable(operation, context, orderId, "trip", ex), null);
            }

            switch (reserve.Outcome)
            {
                case TripReserveOutcome.Reserved:
                case TripReserveOutcome.AlreadyReserved:
                    continue;
                case TripReserveOutcome.Full:
                    return await ParkTransportFailedAsync(operation, existingOrder, version, nowUtc,
                        OrderCommitment.TransportFailure.TripFull, OrderCommitment.Response.FailedCapacity, context);
                default:
                    return await ParkTransportFailedAsync(operation, existingOrder, version, nowUtc,
                        OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, context);
            }
        }

        return (TransientOrderError(operation, "The trip could not confirm the booking. Please try again later."), null);
    }

    /// <summary>
    /// The transport failed under the trucker's accept: write <c>awaiting_transport</c> with the
    /// client's version so the trucker's command ends in a success whose row says why.
    /// </summary>
    private async Task<(SyncOperationResult? Stop, Dictionary<string, object>? Order)> ParkTransportFailedAsync(
        SyncOperationRequest operation, Dictionary<string, object> existingOrder, int version, DateTime nowUtc,
        string failure, string truckerResponse, ILambdaContext context)
    {
        var orderId = operation.EntityId;
        // A full trip is a capacity race, nobody's fault; a cancelled or gone trip is the trucker's.
        var fields = OrderCommitment.TransportFailedFields(failure, truckerResponse, nowUtc,
            failure == OrderCommitment.TransportFailure.TripFull ? OrderCommitment.Party.None : OrderCommitment.Party.Trucker);
        fields[OrderCommitment.TruckerRespondedAtField] = OrderCommitment.Iso(nowUtc);
        var result = await _orderRepo.UpdateAsync(orderId, fields, version);
        if (!result.IsSuccess)
        {
            var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            return (latest == null ? TransientOrderError(operation, "Order not found.") : OrderConflict(operation, latest, version), null);
        }

        context.Logger.LogWarning(
            $"Order {MoneyAlarmLog.LogId(orderId)}: transport failed at the trucker's accept ({failure}); the buyer decides.");
        await EmitTxAuditAsync(context, orderId, "order", orderId, "order_updated",
            ItemString(existingOrder, "status"), OrderCommitment.AwaitingTransportStatus,
            ctxOverride: AuditContext.ForSystem(), metadata: $"transportFailure={failure}");
        return (null, result.Value);
    }

    /// <summary>
    /// FRD-03: a funded order whose escrow is held for review cannot be accepted (a delivery created
    /// after it could never link to the held escrow). An escrow that cannot be read refuses the
    /// accept for now (transient). With the hold on, the returned fence makes the write check the
    /// escrow again in the same transaction.
    /// </summary>
    private async Task<(SyncOperationResult? Stop, EscrowRowKey? Fence)> CheckAcceptEscrowAsync(
        SyncOperationRequest operation, ILambdaContext context)
    {
        if (_escrowRepo == null)
        {
            return (null, null);
        }

        Dictionary<string, object>? orderEscrow;
        try
        {
            orderEscrow = await _escrowRepo.GetByIdAsync($"escrow-order-{operation.EntityId}", consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"ORDER_ACCEPT_ESCROW_UNREAD orderId={MoneyAlarmLog.LogId(operation.EntityId)} error={ex.GetType().Name}");
            return (TransientOrderError(operation, "The order's payment could not be checked. Please try again later."), null);
        }

        if (orderEscrow != null && orderEscrow.TryGetValue("status", out var orderEscrowStatus)
            && orderEscrowStatus?.ToString() == EscrowHoldFields.Status)
        {
            return (OrderValidationError(operation, OrderUnderReviewMessage), null);
        }

        if (FraudHoldEnabled && orderEscrow != null && !string.IsNullOrEmpty(EscrowsTableName)
            && orderEscrow.TryGetValue("createdAt", out var escrowCreatedAt)
            && escrowCreatedAt?.ToString() is { Length: > 0 } escrowCreated)
        {
            return (null, new EscrowRowKey(EscrowsTableName, $"escrow-order-{operation.EntityId}", escrowCreated));
        }

        return (null, null);
    }

    /// <summary>
    /// ESC-15 cancel rules for a paid order (spec 4.6). Returns the answer when this cancel has its
    /// own path or is refused, or null to let the generic update continue (unpaid orders, and the
    /// buyer's post-deadline cancel of an accepted order, which the caller routes itself).
    /// </summary>
    private async Task<SyncOperationResult?> RouteCommittedCancelAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> existingOrder,
        string currentStatus,
        string? authenticatedUserId,
        string? buyerId,
        string? farmerId,
        ILambdaContext context)
    {
        var isBuyer = authenticatedUserId == buyerId;
        var isFarmer = authenticatedUserId == farmerId;

        if (currentStatus == OrderCommitment.AwaitingTransportStatus)
        {
            if (isBuyer)
            {
                return await ProcessTransportFailureCancelAsync(operation, context, CommittedCancelSpec.BuyerAwaitingTransport);
            }

            if (isFarmer)
            {
                return await ProcessTransportFailureCancelAsync(operation, context, CommittedCancelSpec.FarmerAwaitingTransport);
            }

            return null;
        }

        if (!CommitmentsEnabled)
        {
            return null;
        }

        if (currentStatus == "funded" && (isBuyer || isFarmer))
        {
            return CancelNotAllowed(operation,
                "The farmer and the trucker are deciding; you will be told as soon as they answer.");
        }

        if (currentStatus == "accepted" && isFarmer)
        {
            if (!IsFreshSignIn())
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"{StepUpPolicy.ErrorCode}: Please sign in again to cancel an order you accepted."
                };
            }

            return await ProcessTransportFailureCancelAsync(operation, context, CommittedCancelSpec.FarmerCommitted);
        }

        return null;
    }

    /// <summary>The caller's token proves a password entry within the step-up window (AUTH_040).</summary>
    private bool IsFreshSignIn() => StepUpPolicy.Check(_requestAuthTime, StepUpPolicy.ThresholdEtb) == null;

    /// <summary>
    /// ESC-15: before any money moves, the order's trip must still be able to carry it: not
    /// cancelled or gone, enough room, and not leaving within 12 hours (both parties need their
    /// window). Null when fine, or when the order has no trip or the feature is off.
    /// </summary>
    private async Task<string?> CheckTripBookableForPaymentAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        var tripId = ItemString(order, "tripId");
        if (!CommitmentsEnabled || tripId.Length == 0)
        {
            return null;
        }

        Dictionary<string, AttributeValue>? trip;
        try
        {
            trip = await GetTripItemAsync(tripId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Payment gate: trip {MoneyAlarmLog.LogId(tripId)} of order {MoneyAlarmLog.LogId(ItemString(order, "orderId"))} could not be read: [{ex.GetType().Name}]");
            return null;
        }

        const string unavailable = "This trip is no longer available; place a new order.";
        if (trip == null || AttrString(trip, "deletedAt").Length > 0 || AttrString(trip, "status") is not ("open" or "matched"))
        {
            return unavailable;
        }

        var quintals = ParseDouble(order, "quantity") / KgPerQuintal;
        var alreadyHeld = trip.TryGetValue("bookedOrderIds", out var booked) && booked.SS != null
                          && booked.SS.Contains(ItemString(order, "orderId"));
        if (!alreadyHeld && RemainingQuintalsOf(trip) < quintals - MoneyTolerance)
        {
            return unavailable;
        }

        if (TryParseDepartureDateUtc(AttrString(trip, "departureDate"), requireIsoShape: false, out var departure)
            && departure < DateTime.UtcNow + OrderCommitment.DepartureMargin)
        {
            return unavailable;
        }

        return null;
    }

    /// <summary>Quintals the trip still has free: <c>remainingQuintals</c>, or the capacity for a row without bookings.</summary>
    private static double RemainingQuintalsOf(Dictionary<string, AttributeValue> trip)
    {
        if (trip.TryGetValue("remainingQuintals", out var remaining)
            && double.TryParse(remaining.N ?? remaining.S, NumberStyles.Any, CultureInfo.InvariantCulture, out var r))
        {
            return r;
        }

        return trip.TryGetValue("capacity", out var capacity)
               && double.TryParse(capacity.N ?? capacity.S, NumberStyles.Any, CultureInfo.InvariantCulture, out var c)
            ? c
            : 0.0;
    }

    private static double BookedQuintalsOf(Dictionary<string, AttributeValue> trip)
        => trip.TryGetValue("bookedQuintals", out var booked)
           && double.TryParse(booked.N ?? booked.S, NumberStyles.Any, CultureInfo.InvariantCulture, out var b)
            ? b
            : 0.0;

    /// <summary>Trip fields frozen once an order holds capacity on it.</summary>
    private static readonly string[] FrozenBookedTripFields = { "departureDate", "pricePerQuintal", "fromWoreda", "toWoreda" };

    /// <summary>
    /// ESC-17: what a trucker may still change on a booked trip. Date, price and route are what the
    /// buyers and farmers agreed to, so they are frozen; capacity may grow but never drop below the
    /// bookings; and <c>matched</c> is the server's word for "full".
    /// </summary>
    private (string Code, string Message)? CheckBookedTripUpdate(
        Dictionary<string, object> data, Dictionary<string, AttributeValue> existing)
    {
        if (!CommitmentsEnabled)
        {
            return null;
        }

        if (data.TryGetValue("status", out var statusObj) && statusObj?.ToString() == "matched"
            && AttrString(existing, "status") != "matched")
        {
            return ("TRIP_VALIDATION", "TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.");
        }

        var booked = BookedQuintalsOf(existing);
        if (booked <= 0)
        {
            return null;
        }

        foreach (var field in FrozenBookedTripFields)
        {
            if (!data.TryGetValue(field, out var sent))
            {
                continue;
            }

            // Stored numbers are N attributes, which AttrString (S only) would read as empty.
            var stored = existing.TryGetValue(field, out var storedAttr) ? storedAttr.S ?? storedAttr.N ?? string.Empty : string.Empty;
            var sentText = sent?.ToString() ?? string.Empty;
            var unchanged = field == "pricePerQuintal"
                ? double.TryParse(sentText, NumberStyles.Any, CultureInfo.InvariantCulture, out var sentPrice)
                  && double.TryParse(stored, NumberStyles.Any, CultureInfo.InvariantCulture, out var storedPrice)
                  && Math.Abs(sentPrice - storedPrice) <= MoneyTolerance
                : field == "departureDate"
                    ? TryParseDepartureDateUtc(sentText, requireIsoShape: false, out var sentDep)
                      && TryParseDepartureDateUtc(stored, requireIsoShape: false, out var storedDep)
                      && sentDep == storedDep
                    : string.Equals(sentText, stored, StringComparison.Ordinal);
            if (!unchanged)
            {
                return ("TRIP_HAS_BOOKINGS",
                    $"TRIP_HAS_BOOKINGS: {field} cannot change while orders are booked on this trip ({booked.ToString("0.##", CultureInfo.InvariantCulture)} quintals).");
            }
        }

        if (data.TryGetValue("capacity", out var capacityObj)
            && double.TryParse(capacityObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var capacity)
            && capacity < booked - MoneyTolerance)
        {
            return ("TRIP_HAS_BOOKINGS",
                $"TRIP_HAS_BOOKINGS: capacity cannot drop below the {booked.ToString("0.##", CultureInfo.InvariantCulture)} quintals already booked.");
        }

        return null;
    }

    /// <summary>
    /// ESC-17: a trip whose crops are already moving cannot be cancelled; the admin settles those
    /// through a dispute. Null when the cancel may proceed (the orders are moved after the write).
    /// </summary>
    private async Task<SyncOperationResult?> CheckTripCancelAllowedAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> trip, ILambdaContext context)
    {
        if (!CommitmentsEnabled)
        {
            return null;
        }

        List<Dictionary<string, object>> orders;
        try
        {
            orders = await _orderRepo.QueryByTripAsync(operation.EntityId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Trip cancel: orders of trip {MoneyAlarmLog.LogId(operation.EntityId)} could not be read: [{ex.GetType().Name}]");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "The trip's orders could not be checked. Please try again later."
            };
        }

        if (orders.Any(o => ItemString(o, "status") is "pickup_confirmed" or "in_transit"))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "TRIP_HAS_BOOKINGS: Crops from this trip are already on the way; it can no longer be cancelled."
            };
        }

        return null;
    }

    /// <summary>
    /// ESC-17: after a trip was cancelled, every paid order on it loses its transport. A funded
    /// order whose farmer has not answered keeps waiting for the farmer (the accept then lands in
    /// <c>awaiting_transport</c>); one whose farmer accepted, or an accepted order before pickup,
    /// goes to <c>awaiting_transport</c> now, its pending delivery cancelled. Best effort per
    /// order with the alarmable <c>TRIP_CANCEL_CASCADE_FAILED</c> token; the commitment cron and the
    /// accept paths re-check the trip, so a missed row heals.
    /// </summary>
    private async Task CascadeTripCancelAsync(Dictionary<string, AttributeValue> trip, ILambdaContext context)
    {
        if (!CommitmentsEnabled)
        {
            return;
        }

        var tripId = AttrString(trip, "tripId");
        List<Dictionary<string, object>> orders;
        try
        {
            orders = await _orderRepo.QueryByTripAsync(tripId);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"TRIP_CANCEL_CASCADE_FAILED tripId={MoneyAlarmLog.LogId(tripId)} step=query reason=[{ex.GetType().Name}]");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var order in orders)
        {
            var orderId = ItemString(order, "orderId");
            var status = ItemString(order, "status");
            var truckerResponse = OrderCommitment.TruckerResponseOf(order);
            if (truckerResponse is OrderCommitment.Response.Cancelled or OrderCommitment.Response.Declined
                or OrderCommitment.Response.Expired or OrderCommitment.Response.None)
            {
                continue;
            }

            try
            {
                if (status == "funded" && OrderCommitment.FarmerResponseOf(order) != OrderCommitment.Response.Accepted)
                {
                    await _orderRepo.TryStampAsync(orderId,
                        new Dictionary<string, object> { { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Cancelled } },
                        new Dictionary<string, string> { { "status", "funded" } });
                    continue;
                }

                if (status is not ("funded" or "accepted"))
                {
                    continue;
                }

                if (status == "accepted")
                {
                    // Goods already on the road are never turned into a buyer refund: an accepted
                    // order whose delivery left pending is left to the parties (the trip-cancel
                    // pre-check reads order statuses, which can lag the delivery).
                    var activeDeliveryId = ItemString(order, "activeDeliveryId");
                    if (activeDeliveryId.Length > 0 && !await _deliveryRepo.TryCancelPendingAsync(activeDeliveryId))
                    {
                        var delivery = await GetDeliveryItemAsync(activeDeliveryId, consistentRead: true);
                        if (!IsBeforePickup(delivery == null ? null : DeliveryStatusOf(delivery)))
                        {
                            context.Logger.LogWarning(
                                $"Trip cancel: order {MoneyAlarmLog.LogId(orderId)} keeps its delivery {MoneyAlarmLog.LogId(activeDeliveryId)} (already picked up); left for the parties.");
                            continue;
                        }
                    }
                    else if (activeDeliveryId.Length > 0)
                    {
                        await EmitTxAuditAsync(context, orderId, "delivery", activeDeliveryId,
                            "delivery_cancelled", "pending", "cancelled",
                            ctxOverride: AuditContext.ForSystem(), metadata: $"reason={OrderCommitment.TransportFailure.TripCancelled}");
                        await _orderRepo.ReleaseDeliverySlotAsync(orderId, activeDeliveryId);
                    }
                }

                var fields = OrderCommitment.TransportFailedFields(
                    OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled, nowUtc);
                fields.Remove("status");
                var moved = await _orderRepo.TryAdvanceStatusAsync(orderId, status, OrderCommitment.AwaitingTransportStatus, fields);
                if (moved != null)
                {
                    await EmitTxAuditAsync(context, orderId, "order", orderId, "order_updated", status,
                        OrderCommitment.AwaitingTransportStatus, ctxOverride: AuditContext.ForSystem(),
                        metadata: $"transportFailure={OrderCommitment.TransportFailure.TripCancelled};tripId={tripId}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"TRIP_CANCEL_CASCADE_FAILED tripId={MoneyAlarmLog.LogId(tripId)} orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
            }
        }
    }

    /// <summary>
    /// Give an order's quintals back to its trip when the order leaves it (reject, cancel, trucker
    /// decline or drop-out). Idempotent; a failure is logged with the alarmable
    /// <c>TRIP_RELEASE_FAILED</c> token and never fails the order write it follows.
    /// </summary>
    private async Task TryReleaseBookingAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        var tripId = ItemString(order, "tripId");
        var orderId = ItemString(order, "orderId");
        var quintals = ParseDouble(order, "quantity") / KgPerQuintal;
        if (!CommitmentsEnabled || tripId.Length == 0 || orderId.Length == 0 || quintals <= 0)
        {
            return;
        }

        try
        {
            var released = await _tripRepo.TryReleaseCapacityAsync(tripId, orderId, quintals);
            context.Logger.LogInformation(released
                ? $"Trip {MoneyAlarmLog.LogId(tripId)}: {quintals} quintals released from order {MoneyAlarmLog.LogId(orderId)}."
                : $"Trip {MoneyAlarmLog.LogId(tripId)}: nothing to release for order {MoneyAlarmLog.LogId(orderId)}.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"TRIP_RELEASE_FAILED tripId={MoneyAlarmLog.LogId(tripId)} orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
        }
    }
}

/// <summary>
/// ESC-15: how one of the committed-order cancels runs through
/// <c>ProcessTransportFailureCancelAsync</c>: the status the order must be in, the reason codes
/// written on the order and the escrow, who cancelled and who is at fault, and whether the pickup
/// deadline gates it (only the buyer's cancel of an accepted order waits for it).
/// </summary>
internal sealed record CommittedCancelSpec(
    string ExpectedStatus,
    string CancelReason,
    string RefundReason,
    string CancelledBy,
    string? FaultParty,
    bool CheckPickupDeadline)
{
    /// <summary>ESC-06: the buyer cancels an accepted order nobody picked up by its deadline.</summary>
    public static CommittedCancelSpec BuyerAfterPickupDeadline { get; } = new(
        "accepted", PickupDeadline.CancelReason, "order_transport_failed",
        OrderCommitment.Party.Buyer, OrderCommitment.Party.Trucker, CheckPickupDeadline: true);

    /// <summary>
    /// The buyer cancels in awaiting_transport for a full refund. The fault was recorded when the
    /// transport failed (trucker, or none for a full trip), so the cancel keeps the row's value.
    /// </summary>
    public static CommittedCancelSpec BuyerAwaitingTransport { get; } = new(
        OrderCommitment.AwaitingTransportStatus, PickupDeadline.CancelReason, "order_transport_failed",
        OrderCommitment.Party.Buyer, FaultParty: null, CheckPickupDeadline: false);

    /// <summary>The farmer cancels in awaiting_transport: the disruption is not theirs, so the recorded fault stands.</summary>
    public static CommittedCancelSpec FarmerAwaitingTransport { get; } = new(
        OrderCommitment.AwaitingTransportStatus, "farmer_cancelled", "order_transport_failed",
        OrderCommitment.Party.Farmer, FaultParty: null, CheckPickupDeadline: false);

    /// <summary>The farmer backs out of an accepted order before pickup (fresh sign-in): the farmer is at fault.</summary>
    public static CommittedCancelSpec FarmerCommitted { get; } = new(
        "accepted", "farmer_cancelled", "order_cancelled",
        OrderCommitment.Party.Farmer, OrderCommitment.Party.Farmer, CheckPickupDeadline: false);
}

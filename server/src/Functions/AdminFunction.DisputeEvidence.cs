using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FRD-14: GET /admin/disputes/{escrowId}/evidence, the evidence package of one dispute
/// (escrow-protocol.md 1.40.0): the escrow, its order and delivery (with the FRD-12 handoff photos),
/// the stitched timeline of the transaction view, the handoffs (QR scan or typed code, who, GPS and
/// the distance to the planned point) and, only with <c>include=messages</c>, the order's and the
/// delivery's message threads, each such read audited before it is answered. Key reads and bounded
/// Queries only, never a Scan. The trip trail stays on its own route (LT-13).
/// </summary>
public partial class AdminFunction
{
    /// <summary>The messages table (<c>MESSAGES_TABLE</c>); empty answers a message read with 503.</summary>
    internal string MessagesTable { get; init; } = Environment.GetEnvironmentVariable("MESSAGES_TABLE") ?? "";

    /// <summary>The most messages read from one thread: one Query page, newest first.</summary>
    internal const int MaxMessagesPerThread = 100;

    /// <summary>The transaction-audit action of an admin's read of a dispute's messages.</summary>
    internal const string MessagesReadAction = "admin_dispute_messages_read";

    private const string SelfPickupType = "self_pickup";

    /// <summary>
    /// The evidence of one dispute for the admin dispute page. Admin group only, like the trail and the
    /// verdict (<c>403 ADMIN_001</c>). An unknown escrow is <c>404 ADMIN_003</c>, an escrow that never had
    /// a dispute <c>404 ADMIN_019</c>, so the messages of an order nobody disputed are never read here.
    /// A missing order, delivery or audit leg is left out, never an error.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDisputeEvidenceAsync(
        string escrowId,
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_escrowsTable))
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrows table not configured.");
        }

        var withMessages = WantsMessages(request);
        if (withMessages && (string.IsNullOrEmpty(MessagesTable) || TxAuditRepo() == null))
        {
            // A read that cannot be audited is never made.
            context.Logger.LogError("Dispute messages are not configured (MESSAGES_TABLE or TRANSACTION_AUDIT_TABLE missing).");
            return LambdaResponse.Error(503, "ADMIN_004", "Messages not configured.");
        }

        var escrowItem = await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId);
        if (escrowItem == null)
        {
            return LambdaResponse.Error(404, "ADMIN_003", "Escrow not found.");
        }

        var escrow = MapToEscrowDto(DynamoDbHelper.FromItem(escrowItem));
        if (escrow.Dispute == null)
        {
            return LambdaResponse.Error(404, "ADMIN_019", "This escrow has no dispute.");
        }

        await EnrichEscrowNamesAsync(new List<EscrowDto> { escrow });

        var orderId = string.IsNullOrEmpty(escrow.OrderId) ? null : escrow.OrderId;
        OrderDto? order = null;
        if (orderId != null && !string.IsNullOrEmpty(_ordersTable))
        {
            var orderItem = await QuerySingleItemAsync(_ordersTable, "orderId", orderId);
            if (orderItem != null)
            {
                order = OrderFunction.MapToOrderDto(DynamoDbHelper.FromItem(orderItem));
            }
        }

        // The verdict and the other escrow rows are anchored on the order, or the escrow without one.
        var transactionId = orderId ?? escrowId;
        var txRepo = TxAuditRepo();
        var unifiedEvents = txRepo != null
            ? await txRepo.GetByTransactionAsync(transactionId)
            : new List<Dictionary<string, object>>();

        var deliveryId = string.IsNullOrEmpty(escrow.DeliveryId)
            ? unifiedEvents
                .Where(e => GetString(e, "entityType") == "delivery")
                .Select(e => GetStringOrNull(e, "entityId"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id))
            : escrow.DeliveryId;

        Dictionary<string, object>? deliveryRow = null;
        DeliveryDto? delivery = null;
        if (!string.IsNullOrEmpty(deliveryId) && !string.IsNullOrEmpty(_deliveriesTable))
        {
            var deliveryItem = await QuerySingleItemAsync(_deliveriesTable, "deliveryId", deliveryId);
            if (deliveryItem != null)
            {
                deliveryRow = DynamoDbHelper.FromItem(deliveryItem);
                // The parties are shown by name: no phone and no QR token on the dispute page.
                delivery = WithDeliveryPhotoUrls(DeliveryFunction.MapToDeliveryDto(
                    deliveryRow, includePhone: false, includeQrToken: false), deliveryRow, context);
            }
        }

        if (txRepo != null && !string.IsNullOrEmpty(deliveryId))
        {
            var seen = new HashSet<string>(unifiedEvents.Select(e => GetString(e, "auditId")));
            var byDelivery = await txRepo.GetByEntityAsync(deliveryId);
            unifiedEvents.AddRange(byDelivery.Where(e => seen.Add(GetString(e, "auditId"))));
        }

        var auditEntries = new List<EscrowAuditEntryDto>();
        if (!string.IsNullOrEmpty(_escrowAuditTable))
        {
            var auditItems = await new EscrowAuditRepository(_dynamoDb, _escrowAuditTable).GetAuditTrailAsync(escrow.EscrowId);
            auditEntries = auditItems.Select(MapToAuditEntry).ToList();
        }

        List<MessageDto>? messages = null;
        bool? messagesTruncated = null;
        if (withMessages)
        {
            var (read, truncated) = await ReadDisputeMessagesAsync(orderId, order, delivery);
            var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
            try
            {
                // Audited before anything is answered: no row, no messages.
                await TransactionAuditWriter.EmitRequiredAsync(
                    txRepo!, transactionId, "escrow", escrow.EscrowId, MessagesReadAction,
                    AuditContext.From(request, adminUserId, "admin"),
                    metadata: $"messages={read.Count};threads={read.Select(m => m.ConversationId).Distinct().Count()}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Dispute messages read not audited, none answered: [{ex.GetType().Name}]");
                return LambdaResponse.Error(503, "ADMIN_020", "The read could not be audited, so no message is shown.");
            }

            AuditLogger.LogAudit("ADMIN", "READ_DISPUTE_MESSAGES",
                userId: adminUserId, correlationId: AuditLogger.GetCorrelationId(request),
                entityType: "escrow", entityId: escrow.EscrowId, detail: $"messages={read.Count}", outcome: "success");
            messages = read;
            messagesTruncated = truncated;
        }

        var response = new AdminDisputeEvidenceResponse
        {
            Escrow = escrow,
            Order = order,
            Delivery = delivery,
            Timeline = BuildTimeline(order, delivery, escrow, auditEntries, unifiedEvents),
            Handoffs = await HandoffsAsync(deliveryRow, delivery, unifiedEvents, context),
            Messages = messages,
            MessagesTruncated = messagesTruncated
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDisputeEvidenceResponse);
    }

    /// <summary>True for <c>include=messages</c>: the admin asked to read the parties' messages.</summary>
    private static bool WantsMessages(APIGatewayProxyRequest request)
        => request.QueryStringParameters != null
           && request.QueryStringParameters.TryGetValue("include", out var include)
           && string.Equals(include, "messages", StringComparison.Ordinal);

    /// <summary>
    /// The message threads of the disputed order and its delivery: for each context, the thread of every
    /// pair of its parties (messaging.md: <c>{contextType}:{contextId}:{low}:{high}</c>), so at most six
    /// Queries of <see cref="MaxMessagesPerThread"/> each. Oldest first.
    /// </summary>
    private async Task<(List<MessageDto> Messages, bool Truncated)> ReadDisputeMessagesAsync(
        string? orderId, OrderDto? order, DeliveryDto? delivery)
    {
        var threads = new List<string>();
        if (orderId != null && order != null)
        {
            threads.AddRange(ConversationIds("order", orderId, order.BuyerId, order.FarmerId, order.TruckerId));
        }

        if (delivery != null)
        {
            threads.AddRange(ConversationIds(
                "delivery", delivery.DeliveryId, delivery.BuyerId, delivery.FarmerId, delivery.TruckerId));
        }

        var repo = new MessageRepository(_dynamoDb, MessagesTable);
        var messages = new List<MessageDto>();
        var truncated = false;
        foreach (var conversationId in threads.Distinct(StringComparer.Ordinal))
        {
            var (items, lastKey) = await repo.QueryByConversationAsync(conversationId, MaxMessagesPerThread);
            truncated |= lastKey != null;
            messages.AddRange(items
                .Select(MessageFunction.MapToMessageDto)
                .Where(m => m.ConversationId == conversationId));
        }

        return (messages
            .OrderBy(m => ParseTimestamp(m.CreatedAt))
            .ThenBy(m => m.MessageId, StringComparer.Ordinal)
            .ToList(), truncated);
    }

    /// <summary>The conversation id of every pair of distinct parties of one context, as messaging.md sets it.</summary>
    internal static IEnumerable<string> ConversationIds(string contextType, string contextId, params string?[] parties)
    {
        var ids = parties
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                yield return $"{contextType}:{contextId}:{ids[i]}:{ids[j]}";
            }
        }
    }

    /// <summary>
    /// The handoffs the delivery went through: the trucker's pickup and the buyer's drop-off, or the
    /// buyer's self-pickup at the farm. Who and how come from the transition's audit row (the latest
    /// one of its action); the GPS from the delivery. The planned point is read only for a handoff with
    /// a GPS fix, best effort: a failed read leaves the distance out.
    /// </summary>
    private async Task<List<AdminHandoffEvidenceDto>> HandoffsAsync(
        Dictionary<string, object>? row,
        DeliveryDto? delivery,
        List<Dictionary<string, object>> unifiedEvents,
        ILambdaContext context)
    {
        var handoffs = new List<AdminHandoffEvidenceDto>();
        if (row == null || delivery == null)
        {
            return handoffs;
        }

        var selfPickup = delivery.DeliveryType == SelfPickupType;
        var pickupEvent = LatestDeliveryEvent(unifiedEvents, delivery.DeliveryId, "picked_up");
        var deliveredEvent = LatestDeliveryEvent(unifiedEvents, delivery.DeliveryId, "delivered");

        if (!selfPickup && (delivery.PickedUpAt != null || pickupEvent != null))
        {
            var planned = delivery.GpsPickup != null ? await PlannedPickupAsync(delivery, context) : null;
            handoffs.Add(Handoff("pickup", delivery.PickedUpAt, GetStringOrNull(row, "pickupCapturedAt"),
                pickupEvent, delivery.GpsPickup, planned, delivery));
        }

        if (delivery.ConfirmedAt != null || deliveredEvent != null)
        {
            LatLngDto? planned = null;
            if (delivery.GpsDropoff != null)
            {
                planned = selfPickup
                    ? await PlannedPickupAsync(delivery, context)
                    : await PlannedDropoffAsync(row, context);
            }

            handoffs.Add(Handoff(selfPickup ? SelfPickupType : "dropoff", delivery.ConfirmedAt, null,
                deliveredEvent, delivery.GpsDropoff, planned, delivery));
        }

        return handoffs;
    }

    private static AdminHandoffEvidenceDto Handoff(
        string leg,
        string? at,
        string? capturedAt,
        Dictionary<string, object>? auditEvent,
        LatLngDto? gps,
        LatLngDto? planned,
        DeliveryDto delivery)
    {
        var actorId = auditEvent != null ? GetStringOrNull(auditEvent, "actor") : null;
        return new AdminHandoffEvidenceDto
        {
            Leg = leg,
            At = at ?? (auditEvent != null ? GetStringOrNull(auditEvent, "timestamp") : null),
            CapturedAt = capturedAt,
            ActorId = actorId,
            ActorRole = actorId == null ? null
                : actorId == delivery.TruckerId ? "trucker"
                : actorId == delivery.BuyerId ? "buyer"
                : actorId == delivery.FarmerId ? "farmer"
                : null,
            Method = auditEvent != null ? HandoffMethodOf(GetStringOrNull(auditEvent, "metadata")) : null,
            Gps = gps,
            Planned = planned,
            DistanceKm = DistanceKm(gps, planned)
        };
    }

    /// <summary>The latest audit row of one delivery transition (rows arrive oldest first per query).</summary>
    private static Dictionary<string, object>? LatestDeliveryEvent(
        List<Dictionary<string, object>> unifiedEvents, string deliveryId, string action)
        => unifiedEvents
            .Where(e => GetString(e, "entityType") == "delivery"
                        && GetString(e, "entityId") == deliveryId
                        && GetString(e, "action") == action)
            .OrderBy(e => ParseTimestamp(GetString(e, "timestamp")))
            .LastOrDefault();

    /// <summary><c>qr</c> or <c>code</c> from the transition's audit metadata (<c>handoff=qr</c>); null otherwise.</summary>
    internal static string? HandoffMethodOf(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
        {
            return null;
        }

        foreach (var part in metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("handoff=", StringComparison.Ordinal))
            {
                var method = part["handoff=".Length..];
                return method is "qr" or "code" ? method : null;
            }
        }

        return null;
    }

    /// <summary>The great-circle km between the two points, 2 decimals; null without both, or at (0, 0) (no fix).</summary>
    internal static double? DistanceKm(LatLngDto? gps, LatLngDto? planned)
    {
        if (gps == null || planned == null || (gps.Latitude == 0 && gps.Longitude == 0))
        {
            return null;
        }

        return TripTrack.Round(
            GpsValidator.HaversineDistance(gps.Latitude, gps.Longitude, planned.Latitude, planned.Longitude), 2);
    }

    /// <summary>The listing's location, where the goods were to be picked up; null when unknown or unreadable.</summary>
    private async Task<LatLngDto?> PlannedPickupAsync(DeliveryDto delivery, ILambdaContext context)
    {
        if (string.IsNullOrEmpty(_cropsTable) || string.IsNullOrEmpty(delivery.ListingId))
        {
            return null;
        }

        try
        {
            var crop = await new CropRepository(_dynamoDb, _cropsTable).GetByIdAsync(delivery.ListingId);
            return LatLngOf(TripTrack.PointOf(crop, "latitude", "longitude"));
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"evidence: planned pickup unavailable: [{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>The trip's destination, the planned drop-off the delivered GPS check uses; null when unknown.</summary>
    private async Task<LatLngDto?> PlannedDropoffAsync(Dictionary<string, object> row, ILambdaContext context)
    {
        var tripId = AdminTrail.TripIdOf(row);
        if (string.IsNullOrEmpty(_tripsTable) || tripId == null)
        {
            return null;
        }

        try
        {
            var trip = await new TripRepository(_dynamoDb, _tripsTable).GetByIdAsync(tripId);
            return LatLngOf(TripTrack.PlannedDropoff(trip, row));
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"evidence: planned drop-off unavailable: [{ex.GetType().Name}]");
            return null;
        }
    }

    private static LatLngDto? LatLngOf(TrackPoint? point)
        => point == null ? null : new LatLngDto { Latitude = point.Latitude, Longitude = point.Longitude };
}

using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-01: the single funding path for orders, shared by the mock auto-fund in
/// <c>SyncFunction</c> and the live Telebirr webhook (order-lifecycle.md, escrow-protocol.md
/// §Order-Funded Escrow).
///
/// Funding has two parts:
/// 1. The status change. Live payments use <see cref="TryFundFromPaymentAsync"/>, a write that
///    only succeeds while the order is still <c>payment_submitted</c> and that records the
///    Telebirr references in server-only fields. The mock path keeps its version-checked write.
/// 2. The side effects every funded order needs (<see cref="EnsureFundedSideEffectsAsync"/>):
///    the listing gives up the ordered stock and the order escrow is created. Both are
///    idempotent per order, so a payment redelivery can repeat them to repair a partial attempt.
///    ESC-18: when the listing no longer holds the ordered stock, the funding lost the race and
///    the order is cancelled with a full refund instead.
/// </summary>
public sealed class OrderFundingService
{
    /// <summary>Server-only order field: the merch_order_id of the Telebirr checkout that paid.</summary>
    public const string MerchOrderIdField = "telebirrMerchOrderId";

    /// <summary>Server-only order field: Telebirr's payment_order_id for the payment.</summary>
    public const string PaymentOrderIdField = "telebirrPaymentOrderId";

    /// <summary>Server-only order field: Telebirr's trans_id for the payment.</summary>
    public const string TransIdField = "telebirrTransId";

    private const double KgPerQuintal = 100.0;

    private readonly IOrderRepository _orderRepo;
    private readonly ICropRepository _cropRepo;
    private readonly EscrowService? _escrowService;
    private readonly ITripRepository? _tripRepo;
    private readonly bool _commitmentsEnabled;
    private readonly ITransactionAuditRepository? _txAuditRepo;

    /// <param name="commitmentsEnabled">ESC-15 on or off; null reads <see cref="_commitmentsEnabled"/> (the env flag).</param>
    /// <param name="txAuditRepo">ESC-18: where the out-of-stock end of an order is audited; null writes no row.</param>
    public OrderFundingService(
        IOrderRepository orderRepo, ICropRepository cropRepo, EscrowService? escrowService,
        ITripRepository? tripRepo = null, bool? commitmentsEnabled = null,
        ITransactionAuditRepository? txAuditRepo = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _escrowService = escrowService;
        _tripRepo = tripRepo;
        _commitmentsEnabled = commitmentsEnabled ?? OrderCommitment.IsEnabled;
        _txAuditRepo = txAuditRepo;
    }

    /// <summary>
    /// Fund a <c>payment_submitted</c> order from a verified live payment. Returns the funded
    /// order, or null when the order is missing or no longer <c>payment_submitted</c> (the
    /// caller re-reads it and classifies the payment). ESC-15: the write also carries the
    /// commitment stamps (<see cref="FundingStampsAsync"/>), so the farmer's request and its
    /// deadline exist the moment the money is held.
    /// </summary>
    public async Task<Dictionary<string, object>?> TryFundFromPaymentAsync(
        string orderId, string merchOrderId, string paymentOrderId, string? transId)
    {
        var nowUtc = DateTime.UtcNow;
        var fields = new Dictionary<string, object>
        {
            { "paymentVerifiedAt", nowUtc.ToString("o") },
            { MerchOrderIdField, merchOrderId },
            { PaymentOrderIdField, paymentOrderId }
        };
        if (!string.IsNullOrEmpty(transId))
        {
            fields[TransIdField] = transId;
        }

        if (_commitmentsEnabled)
        {
            var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (order != null)
            {
                foreach (var (key, value) in await FundingStampsAsync(order, nowUtc))
                {
                    fields[key] = value;
                }
            }
        }

        return await _orderRepo.TryMarkFundedAsync(orderId, fields);
    }

    /// <summary>
    /// ESC-15: the commitment fields a funding write carries (<see cref="OrderCommitment.FundingStamps"/>):
    /// who is asked first and until when. The trip's departure bounds the farmer's window; a trip
    /// that cannot be read (or no trip repository) leaves the plain 24-hour window. Empty when the
    /// feature is off, so the old funding write is unchanged.
    /// </summary>
    public async Task<Dictionary<string, object>> FundingStampsAsync(
        IReadOnlyDictionary<string, object> order, DateTime nowUtc)
    {
        if (!_commitmentsEnabled)
        {
            return new Dictionary<string, object>();
        }

        DateTime? departure = null;
        var tripId = GetString(order, "tripId");
        if (tripId.Length > 0 && _tripRepo != null)
        {
            try
            {
                var trip = await _tripRepo.GetByIdAsync(tripId);
                if (trip != null && OrderCommitment.TryParseUtc(GetString(trip, "departureDate"), out var departureUtc))
                {
                    departure = departureUtc;
                }
            }
            catch (Exception)
            {
                // The deadline falls back to the plain window; the trip is read again at the
                // farmer's accept and at the capacity reservation.
            }
        }

        return OrderCommitment.FundingStamps(order, nowUtc, departure);
    }

    /// <summary>
    /// Apply what every funded order needs: take the ordered stock off the listing, create the
    /// order escrow and (ESC-15) reserve the trip capacity. Every step is idempotent per order.
    /// Failures are logged with an alarmable token and reported in the result; they never undo
    /// the funding. ESC-18: a funding that finds too little stock on the listing lost the race
    /// (<see cref="StockDecrementResult.InsufficientStock"/>): the escrow is still created, so the
    /// money has its record, then the order ends with a full refund (<see cref="EndOutOfStockAsync"/>)
    /// and no trip capacity is reserved for it.
    /// </summary>
    public async Task<FundingSideEffectsResult> EnsureFundedSideEffectsAsync(
        Dictionary<string, object> fundedOrder, string actor, AuditContext? auditContext, ILambdaContext context)
    {
        var stock = await DecrementStockAsync(fundedOrder, context);
        var escrowCreated = await EnsureEscrowAsync(fundedOrder, actor, auditContext, context);
        if (stock == StockDecrementResult.InsufficientStock)
        {
            var ended = await EndOutOfStockAsync(fundedOrder, escrowCreated, actor, auditContext, context);
            return new FundingSideEffectsResult(stock, escrowCreated, Trip: null, OutOfStockOrder: ended);
        }

        var trip = await ReserveTripCapacityAsync(fundedOrder, context);
        return new FundingSideEffectsResult(stock, escrowCreated, trip);
    }

    /// <summary>ESC-18: the status a funded order ends in when its funding lost the listing's stock.</summary>
    public const string OutOfStockStatus = "cancelled";

    /// <summary>
    /// ESC-18: the <c>cancelReason</c> (and escrow <c>refundReason</c>) of that end, one of the
    /// server-only codes (<see cref="OrderCommitment.ServerCancelReasons"/>).
    /// </summary>
    public const string OutOfStockReason = "listing_out_of_stock";

    /// <summary>
    /// The statuses an order whose funding lost the stock can be ended from: the money is held and
    /// no goods have changed hands. A farmer who already accepted is checked separately.
    /// </summary>
    private static readonly string[] OutOfStockEndableStatuses = { "funded", OrderCommitment.AwaitingTransportStatus };

    /// <summary>
    /// ESC-18 (spec A2): end an order whose funding lost the listing's stock. The order moves to
    /// <see cref="OutOfStockStatus"/> with <c>cancelReason: listing_out_of_stock</c>,
    /// <c>cancelledBy: system</c> and <c>faultParty: none</c> (a race the system lost is nobody's
    /// fault, so the farmer gets no penalty), in one write conditioned on its status. Then, like
    /// every cancel of a funded order, the escrow refund is scheduled
    /// (<see cref="EscrowService.ScheduleRefundForOrderAsync"/>; the hourly refund sweep pays the
    /// whole amount back, commission included, once per escrow) and a trip booking an earlier
    /// attempt may have made is released. No stock is restored: none was taken.
    /// Returns the ended order, or null when it could not be ended: an order that has moved on
    /// or whose farmer accepted (checked on the row read and again in the write's condition, so an
    /// accept that lands in between is never cancelled), or a failed write, is logged with the
    /// alarmable <c>ORDER_STOCK_DECREMENT_FAILED</c> token, since it now needs stock the listing
    /// no longer has.
    /// </summary>
    private async Task<Dictionary<string, object>?> EndOutOfStockAsync(
        Dictionary<string, object> fundedOrder, bool escrowExists, string actor, AuditContext? auditContext,
        ILambdaContext context)
    {
        var orderId = GetString(fundedOrder, "orderId");
        var listingId = GetString(fundedOrder, "listingId");
        if (GetString(fundedOrder, OrderCommitment.FarmerResponseField) == OrderCommitment.Response.Accepted)
        {
            // Only a repair can get here (an earlier attempt's decrement failed and the farmer has
            // answered since). The farmer committed to the goods, so a person decides, not the server.
            context.Logger.LogError(
                $"ORDER_STOCK_DECREMENT_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} "
                + "reason=insufficient_stock status=farmer_accepted");
            return null;
        }

        var fields = new Dictionary<string, object>
        {
            { "cancelledAt", DateTime.UtcNow.ToString("o") },
            { "cancelReason", OutOfStockReason },
            { OrderCommitment.CancelledByField, OrderCommitment.Party.System },
            { OrderCommitment.FaultPartyField, OrderCommitment.Party.None }
        };

        Dictionary<string, object>? ended = null;
        string? endedFrom = null;
        try
        {
            foreach (var from in OutOfStockEndableStatuses)
            {
                ended = await _orderRepo.TryAdvanceStatusUnlessAsync(orderId, from, OutOfStockStatus, fields,
                    OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted);
                if (ended != null)
                {
                    endedFrom = from;
                    break;
                }
            }

            if (ended == null)
            {
                // Two runs of the same funding (a payment redelivery racing the first delivery) can
                // both lose the stock. The one that ended the order audited it; the refund and the
                // release below are idempotent, so this run arms them again in case that one stopped.
                var current = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                if (current == null
                    || GetString(current, "status") != OutOfStockStatus
                    || GetString(current, "cancelReason") != OutOfStockReason)
                {
                    // Moved on, or the farmer accepted after the read (the write's condition refused it).
                    var state = current == null ? "missing"
                        : GetString(current, OrderCommitment.FarmerResponseField) == OrderCommitment.Response.Accepted
                          && OutOfStockEndableStatuses.Contains(GetString(current, "status"))
                            ? "farmer_accepted"
                            : MoneyAlarmLog.LogId(GetString(current, "status"));
                    context.Logger.LogError(
                        $"ORDER_STOCK_DECREMENT_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} "
                        + $"reason=insufficient_stock status={state}");
                    return null;
                }

                context.Logger.LogInformation(
                    $"Order {MoneyAlarmLog.LogId(orderId)}: already ended for lack of stock on listing {MoneyAlarmLog.LogId(listingId)}.");
                ended = current;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_STOCK_DECREMENT_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} "
                + $"reason=insufficient_stock step=end [{ex.GetType().Name}]");
            return null;
        }

        if (endedFrom != null)
        {
            context.Logger.LogWarning(
                $"Order {MoneyAlarmLog.LogId(orderId)}: listing {MoneyAlarmLog.LogId(listingId)} ran out of stock at funding; "
                + $"{endedFrom} -> {OutOfStockStatus} ({OutOfStockReason}), full refund.");
            await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, orderId, "order", orderId,
                "order_cancelled", endedFrom, OutOfStockStatus, AuditContext.ForSystem(),
                metadata: $"cancelReason={OutOfStockReason};listingId={listingId}");
        }

        await ScheduleOutOfStockRefundAsync(fundedOrder, escrowExists, actor, auditContext, context);

        // FUP-123: no capacity is reserved after a lost stock race, but a funding attempt that
        // crashed after its reservation left one; give it back. Never throws.
        await TripBookingRelease.ReleaseAsync(_tripRepo, fundedOrder, context);
        return ended;
    }

    /// <summary>Pause before looking for an escrow written a moment ago once more (its reads are eventually consistent).</summary>
    private static readonly TimeSpan RefundScheduleRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// ESC-18: expire the ended order's escrow now (<c>refundReason: listing_out_of_stock</c>) so
    /// the hourly refund sweep pays the buyer back through the gateway path every refund takes.
    /// The escrow was usually written a moment before, and the lookup reads are eventually
    /// consistent, so an escrow known to exist that is not found is looked for once more. An
    /// escrow whose create failed is created once more first: the order is cancelled now, so no
    /// redelivery or delivery link will create it later. The same alarm token as the other cancel
    /// paths when the refund still cannot be armed: <c>reason=no_escrow</c> means the money is held
    /// with no escrow record (a person refunds it by hand); any other reason means the 7 day sweep
    /// refunds the terminal order.
    /// </summary>
    private async Task ScheduleOutOfStockRefundAsync(
        Dictionary<string, object> fundedOrder, bool escrowExists, string actor, AuditContext? auditContext,
        ILambdaContext context)
    {
        var orderId = GetString(fundedOrder, "orderId");
        if (!escrowExists && _escrowService != null)
        {
            escrowExists = await EnsureEscrowAsync(fundedOrder, actor, auditContext, context);
        }

        if (!escrowExists || _escrowService == null)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=no_escrow");
            return;
        }

        try
        {
            var scheduled = await _escrowService.ScheduleRefundForOrderAsync(
                orderId, OutOfStockReason, "system", AuditContext.ForSystem());
            if (!scheduled)
            {
                await Task.Delay(RefundScheduleRetryDelay);
                scheduled = await _escrowService.ScheduleRefundForOrderAsync(
                    orderId, OutOfStockReason, "system", AuditContext.ForSystem());
            }

            if (scheduled)
            {
                context.Logger.LogInformation(
                    $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason={OutOfStockReason}");
            }
            else
            {
                context.Logger.LogError(
                    $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=escrow_not_armable");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// ESC-15: hold the order's quintals on its trip, once per order (<see cref="ITripRepository.TryReserveCapacityAsync"/>).
    /// A trip that is full, cancelled or gone cannot carry the order: it moves straight to
    /// <c>awaiting_transport</c> (<see cref="OrderCommitment.TransportFailure.TripFull"/> or
    /// <see cref="OrderCommitment.TransportFailure.TripCancelled"/>) so the buyer is told at once
    /// and the farmer is never asked for a trade that cannot travel. A reservation that threw
    /// leaves the order <c>funded</c>; the trucker's accept verifies the booking and reserves it
    /// then. Null when the order has no trip, the feature is off, or no trip repository is wired.
    /// </summary>
    public async Task<TripReserveOutcome?> ReserveTripCapacityAsync(
        Dictionary<string, object> fundedOrder, ILambdaContext context)
    {
        var orderId = GetString(fundedOrder, "orderId");
        var tripId = GetString(fundedOrder, "tripId");
        var quintals = ParseDouble(fundedOrder, "quantity") / KgPerQuintal;
        if (!_commitmentsEnabled || _tripRepo == null || tripId.Length == 0 || orderId.Length == 0 || quintals <= 0)
        {
            return null;
        }

        TripReserveResult result;
        try
        {
            result = await _tripRepo.TryReserveCapacityAsync(tripId, orderId, quintals);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_TRIP_RESERVE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} tripId={MoneyAlarmLog.LogId(tripId)} reason=[{ex.GetType().Name}]");
            return null;
        }

        if (result.Outcome is TripReserveOutcome.Reserved or TripReserveOutcome.AlreadyReserved)
        {
            context.Logger.LogInformation(
                $"Trip {MoneyAlarmLog.LogId(tripId)}: {quintals} quintals {result.Outcome} for order {MoneyAlarmLog.LogId(orderId)} (remaining {result.RemainingQuintals}).");
            return result.Outcome;
        }

        // A full trip is a capacity race, nobody's fault; a cancelled or gone trip is the trucker's.
        var full = result.Outcome == TripReserveOutcome.Full;
        var failure = full ? OrderCommitment.TransportFailure.TripFull : OrderCommitment.TransportFailure.TripCancelled;
        var fields = OrderCommitment.TransportFailedFields(
            failure,
            full ? OrderCommitment.Response.FailedCapacity : OrderCommitment.Response.Cancelled,
            DateTime.UtcNow,
            faultParty: full ? OrderCommitment.Party.None : OrderCommitment.Party.Trucker);
        fields.Remove("status");
        try
        {
            var moved = await _orderRepo.TryAdvanceStatusAsync(orderId, "funded", OrderCommitment.AwaitingTransportStatus, fields);
            context.Logger.LogWarning(moved != null
                ? $"Order {MoneyAlarmLog.LogId(orderId)}: transport failed at funding ({failure}); the buyer decides."
                : $"Order {MoneyAlarmLog.LogId(orderId)}: transport failed at funding ({failure}) but the order was no longer funded.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_TRIP_RESERVE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} tripId={MoneyAlarmLog.LogId(tripId)} reason=[{ex.GetType().Name}] step=awaiting_transport");
        }

        return result.Outcome;
    }

    /// <summary>
    /// Take a funded order's stock off its listing, once per order. Returns null when the order
    /// carries no listing or quantity (nothing to take).
    /// </summary>
    public async Task<StockDecrementResult?> DecrementStockAsync(
        Dictionary<string, object> order, ILambdaContext context)
    {
        var orderId = GetString(order, "orderId");
        var listingId = GetString(order, "listingId");
        // SIM-07: order.quantity is kilograms on the wire; listing stock is quintals.
        var quintals = ParseDouble(order, "quantity") / KgPerQuintal;
        if (orderId.Length == 0 || listingId.Length == 0 || quintals <= 0)
        {
            return null;
        }

        StockDecrementResult result;
        try
        {
            result = await _cropRepo.TryDecrementStockAsync(listingId, orderId, quintals);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_STOCK_DECREMENT_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} reason=[{ex.GetType().Name}]");
            return StockDecrementResult.Failed;
        }

        if (result is StockDecrementResult.Failed or StockDecrementResult.ListingNotFound)
        {
            context.Logger.LogError(
                $"ORDER_STOCK_DECREMENT_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} reason={result}");
        }
        else if (result == StockDecrementResult.InsufficientStock)
        {
            // ESC-18: a lost race, not a fault. The caller ends the order with a full refund.
            context.Logger.LogWarning(
                $"Listing {MoneyAlarmLog.LogId(listingId)}: not enough stock left for order {MoneyAlarmLog.LogId(orderId)} ({quintals} quintals); nothing taken.");
        }
        else
        {
            context.Logger.LogInformation(
                $"Listing {MoneyAlarmLog.LogId(listingId)}: stock for order {MoneyAlarmLog.LogId(orderId)} {result} ({quintals} quintals).");
        }

        return result;
    }

    private async Task<bool> EnsureEscrowAsync(
        Dictionary<string, object> fundedOrder, string actor, AuditContext? auditContext, ILambdaContext context)
    {
        if (_escrowService == null)
        {
            return false;
        }

        var orderId = GetString(fundedOrder, "orderId");
        try
        {
            var created = await _escrowService.CreateFromFundedOrderAsync(fundedOrder, actor, auditContext);
            if (!created)
            {
                context.Logger.LogError($"ORDER_ESCROW_CREATE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=validation");
            }

            return created;
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"ORDER_ESCROW_CREATE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
            return false;
        }
    }

    private static string GetString(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static double ParseDouble(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0.0;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0.0,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            _ => 0.0
        };
    }
}

/// <summary>What <see cref="OrderFundingService.EnsureFundedSideEffectsAsync"/> did.</summary>
/// <param name="Stock">The stock step, or null when the order carries no listing or quantity.</param>
/// <param name="EscrowCreated">True when the order escrow exists after the call.</param>
/// <param name="Trip">The trip reservation (ESC-15), or null when the order has no trip or the step did not run.</param>
/// <param name="OutOfStockOrder">
/// ESC-18: the order as ended after its funding lost the listing's stock, or null when the stock
/// was taken (or the end could not be written; then it was logged with an alarm token).
/// </param>
public sealed record FundingSideEffectsResult(
    StockDecrementResult? Stock, bool EscrowCreated, TripReserveOutcome? Trip = null,
    Dictionary<string, object>? OutOfStockOrder = null);

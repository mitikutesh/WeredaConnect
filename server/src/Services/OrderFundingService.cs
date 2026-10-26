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

    /// <param name="commitmentsEnabled">ESC-15 on or off; null reads <see cref="_commitmentsEnabled"/> (the env flag).</param>
    public OrderFundingService(
        IOrderRepository orderRepo, ICropRepository cropRepo, EscrowService? escrowService,
        ITripRepository? tripRepo = null, bool? commitmentsEnabled = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _escrowService = escrowService;
        _tripRepo = tripRepo;
        _commitmentsEnabled = commitmentsEnabled ?? OrderCommitment.IsEnabled;
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
    /// the funding.
    /// </summary>
    public async Task<FundingSideEffectsResult> EnsureFundedSideEffectsAsync(
        Dictionary<string, object> fundedOrder, string actor, AuditContext? auditContext, ILambdaContext context)
    {
        var stock = await DecrementStockAsync(fundedOrder, context);
        var escrowCreated = await EnsureEscrowAsync(fundedOrder, actor, auditContext, context);
        var trip = await ReserveTripCapacityAsync(fundedOrder, context);
        return new FundingSideEffectsResult(stock, escrowCreated, trip);
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
public sealed record FundingSideEffectsResult(StockDecrementResult? Stock, bool EscrowCreated, TripReserveOutcome? Trip = null);

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

    public OrderFundingService(IOrderRepository orderRepo, ICropRepository cropRepo, EscrowService? escrowService)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _escrowService = escrowService;
    }

    /// <summary>
    /// Fund a <c>payment_submitted</c> order from a verified live payment. Returns the funded
    /// order, or null when the order is missing or no longer <c>payment_submitted</c> (the
    /// caller re-reads it and classifies the payment).
    /// </summary>
    public Task<Dictionary<string, object>?> TryFundFromPaymentAsync(
        string orderId, string merchOrderId, string paymentOrderId, string? transId)
    {
        var fields = new Dictionary<string, object>
        {
            { "paymentVerifiedAt", DateTime.UtcNow.ToString("o") },
            { MerchOrderIdField, merchOrderId },
            { PaymentOrderIdField, paymentOrderId }
        };
        if (!string.IsNullOrEmpty(transId))
        {
            fields[TransIdField] = transId;
        }

        return _orderRepo.TryMarkFundedAsync(orderId, fields);
    }

    /// <summary>
    /// Apply what every funded order needs: take the ordered stock off the listing and create
    /// the order escrow. Both steps are idempotent per order. Failures are logged with an
    /// alarmable token and reported in the result; they never undo the funding.
    /// </summary>
    public async Task<FundingSideEffectsResult> EnsureFundedSideEffectsAsync(
        Dictionary<string, object> fundedOrder, string actor, AuditContext? auditContext, ILambdaContext context)
    {
        var stock = await DecrementStockAsync(fundedOrder, context);
        var escrowCreated = await EnsureEscrowAsync(fundedOrder, actor, auditContext, context);
        return new FundingSideEffectsResult(stock, escrowCreated);
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
                $"ORDER_STOCK_DECREMENT_FAILED orderId={orderId} listingId={listingId} reason=[{ex.GetType().Name}]");
            return StockDecrementResult.Failed;
        }

        if (result is StockDecrementResult.Failed or StockDecrementResult.ListingNotFound)
        {
            context.Logger.LogError(
                $"ORDER_STOCK_DECREMENT_FAILED orderId={orderId} listingId={listingId} reason={result}");
        }
        else
        {
            context.Logger.LogInformation(
                $"Listing {listingId}: stock for order {orderId} {result} ({quintals} quintals).");
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
                context.Logger.LogError($"ORDER_ESCROW_CREATE_FAILED orderId={orderId} reason=validation");
            }

            return created;
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"ORDER_ESCROW_CREATE_FAILED orderId={orderId} reason=[{ex.GetType().Name}]");
            return false;
        }
    }

    private static string GetString(Dictionary<string, object> item, string key)
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
public sealed record FundingSideEffectsResult(StockDecrementResult? Stock, bool EscrowCreated);

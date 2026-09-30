using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-01: the shared funding path for mock and live order payments.
/// </summary>
public class OrderFundingServiceTests
{
    private const string OrderId = "00000000-0000-0000-0000-00000000000b";

    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _cropRepo = Substitute.For<ICropRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly OrderFundingService _sut;

    public OrderFundingServiceTests()
    {
        _sut = new OrderFundingService(_orderRepo, _cropRepo, escrowService: null);
    }

    private static Dictionary<string, object> FundedOrder(object quantityKg) => new()
    {
        { "orderId", OrderId }, { "listingId", "listing-1" }, { "quantity", quantityKg }, { "status", "funded" }
    };

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    [Fact]
    public async Task DecrementStock_TakesTheOrderQuantityInQuintals()
    {
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0).Returns(StockDecrementResult.Decremented);

        var actual = await _sut.DecrementStockAsync(FundedOrder("500"), _context);

        Assert.Equal(StockDecrementResult.Decremented, actual);
        await _cropRepo.Received(1).TryDecrementStockAsync("listing-1", OrderId, 5.0);
    }

    [Theory]
    [InlineData(StockDecrementResult.Failed)]
    [InlineData(StockDecrementResult.ListingNotFound)]
    public async Task DecrementStock_Failure_LogsTheAlarmToken(StockDecrementResult result)
    {
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0).Returns(result);

        var actual = await _sut.DecrementStockAsync(FundedOrder(500.0), _context);

        Assert.Equal(result, actual);
        Assert.Contains("ORDER_STOCK_DECREMENT_FAILED", Logs);
    }

    [Fact]
    public async Task DecrementStock_RepositoryThrows_ReportsFailedWithoutThrowing()
    {
        _cropRepo.TryDecrementStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns<StockDecrementResult>(_ => throw new InvalidOperationException("dynamo down"));

        var actual = await _sut.DecrementStockAsync(FundedOrder("500"), _context);

        Assert.Equal(StockDecrementResult.Failed, actual);
        Assert.Contains("ORDER_STOCK_DECREMENT_FAILED", Logs);
    }

    [Fact]
    public async Task DecrementStock_OrderWithoutListingOrQuantity_DoesNothing()
    {
        var actual = await _sut.DecrementStockAsync(
            new Dictionary<string, object> { { "orderId", OrderId }, { "quantity", "0" } }, _context);

        Assert.Null(actual);
        await _cropRepo.DidNotReceive().TryDecrementStockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task TryFundFromPayment_RecordsTheTelebirrReferences()
    {
        await _sut.TryFundFromPaymentAsync(OrderId, "WCO1", "TB-1", "TXN-1");

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f[OrderFundingService.MerchOrderIdField] == "WCO1"
            && (string)f[OrderFundingService.PaymentOrderIdField] == "TB-1"
            && (string)f[OrderFundingService.TransIdField] == "TXN-1"
            && f.ContainsKey("paymentVerifiedAt")
            && !f.ContainsKey("status")));
    }

    [Fact]
    public async Task TryFundFromPayment_WithoutTransId_OmitsIt()
    {
        await _sut.TryFundFromPaymentAsync(OrderId, "WCO1", "TB-1", null);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            !f.ContainsKey(OrderFundingService.TransIdField)));
    }

    [Fact]
    public async Task EnsureFundedSideEffects_WithoutEscrowService_StillTakesTheStock()
    {
        _cropRepo.TryDecrementStockAsync("listing-1", OrderId, 5.0).Returns(StockDecrementResult.Decremented);

        var actual = await _sut.EnsureFundedSideEffectsAsync(FundedOrder("500"), "system", null, _context);

        Assert.Equal(StockDecrementResult.Decremented, actual.Stock);
        Assert.False(actual.EscrowCreated);
    }
}

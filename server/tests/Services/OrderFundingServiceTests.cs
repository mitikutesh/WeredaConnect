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
        Assert.Null(actual.Trip);
    }

    // ===== ESC-15: commitment stamps and the trip booking =====

    private readonly ITripRepository _tripRepo = Substitute.For<ITripRepository>();

    private OrderFundingService Committing(bool enabled = true)
        => new(_orderRepo, _cropRepo, escrowService: null, tripRepo: _tripRepo, commitmentsEnabled: enabled);

    private static Dictionary<string, object> TripOrder(string status = "funded") => new()
    {
        { "orderId", OrderId }, { "listingId", "listing-1" }, { "quantity", "500" }, { "status", status },
        { "tripId", "trip-1" }, { "truckerId", "trucker-1" }
    };

    [Fact]
    public async Task FundingStamps_AskTheFarmer_BoundedByTheTripDeparture()
    {
        var departure = DateTime.UtcNow.AddHours(20);
        _tripRepo.GetByIdAsync("trip-1").Returns(new Dictionary<string, object> { { "departureDate", departure.ToString("o") } });

        var stamps = await Committing().FundingStampsAsync(TripOrder(), DateTime.UtcNow);

        Assert.Equal(OrderCommitment.Response.Pending, stamps[OrderCommitment.FarmerResponseField]);
        Assert.Equal(OrderCommitment.Response.NotAsked, stamps[OrderCommitment.TruckerResponseField]);
        var deadline = DateTime.Parse((string)stamps[OrderCommitment.FarmerDeadlineAtField], null,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.True(Math.Abs((deadline - departure.AddHours(-12)).TotalSeconds) < 5);
    }

    [Fact]
    public async Task FundingStamps_FlagOff_AreEmpty()
    {
        var stamps = await Committing(enabled: false).FundingStampsAsync(TripOrder(), DateTime.UtcNow);

        Assert.Empty(stamps);
        await _tripRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task NoFlagGiven_FollowsTheEnvironmentFlag()
    {
        // The webhook Lambda builds the service without a flag: it must follow COMMITMENTS_ENABLED
        // (off in this test process), never a hard-coded value, or mock and live funding drift apart.
        var service = new OrderFundingService(_orderRepo, _cropRepo, escrowService: null, tripRepo: _tripRepo);

        var stamps = await service.FundingStampsAsync(TripOrder(), DateTime.UtcNow);

        Assert.Equal(OrderCommitment.IsEnabled, stamps.Count > 0);
    }

    [Fact]
    public async Task TryFundFromPayment_CarriesTheStamps()
    {
        _orderRepo.GetByIdAsync(OrderId, true).Returns(TripOrder("payment_submitted"));

        await Committing().TryFundFromPaymentAsync(OrderId, "WCO1", "TB-1", null);

        await _orderRepo.Received(1).TryMarkFundedAsync(OrderId, Arg.Is<Dictionary<string, object>>(f =>
            (string)f[OrderCommitment.FarmerResponseField] == OrderCommitment.Response.Pending
            && (string)f[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.NotAsked
            && (string)f[OrderCommitment.FulfillmentField] == OrderCommitment.Fulfillment.Trucker));
    }

    [Fact]
    public async Task EnsureFundedSideEffects_ReservesTheTripCapacity()
    {
        _tripRepo.TryReserveCapacityAsync("trip-1", OrderId, 5.0).Returns(new TripReserveResult(TripReserveOutcome.Reserved, 95));

        var actual = await Committing().EnsureFundedSideEffectsAsync(TripOrder(), "system", null, _context);

        Assert.Equal(TripReserveOutcome.Reserved, actual.Trip);
        await _orderRepo.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Theory]
    [InlineData(TripReserveOutcome.Full, OrderCommitment.TransportFailure.TripFull, OrderCommitment.Response.FailedCapacity)]
    [InlineData(TripReserveOutcome.Unavailable, OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled)]
    [InlineData(TripReserveOutcome.NotFound, OrderCommitment.TransportFailure.TripCancelled, OrderCommitment.Response.Cancelled)]
    public async Task EnsureFundedSideEffects_TripCannotCarryTheOrder_ParksItWithTheBuyer(
        TripReserveOutcome outcome, string failure, string truckerResponse)
    {
        _tripRepo.TryReserveCapacityAsync("trip-1", OrderId, 5.0).Returns(new TripReserveResult(outcome, 0));

        await Committing().EnsureFundedSideEffectsAsync(TripOrder(), "system", null, _context);

        await _orderRepo.Received(1).TryAdvanceStatusAsync(OrderId, "funded", OrderCommitment.AwaitingTransportStatus,
            Arg.Is<Dictionary<string, object>>(f =>
                (string)f[OrderCommitment.TransportFailureField] == failure
                && (string)f[OrderCommitment.TruckerResponseField] == truckerResponse
                && f.ContainsKey(OrderCommitment.BuyerChoiceDeadlineAtField)
                && !f.ContainsKey("status")));
    }

    [Fact]
    public async Task EnsureFundedSideEffects_ReservationThrows_LeavesTheOrderFunded_AndAlarms()
    {
        _tripRepo.TryReserveCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns<TripReserveResult>(_ => throw new InvalidOperationException("dynamo down"));

        var actual = await Committing().EnsureFundedSideEffectsAsync(TripOrder(), "system", null, _context);

        Assert.Null(actual.Trip);
        Assert.Contains("ORDER_TRIP_RESERVE_FAILED", Logs);
        await _orderRepo.DidNotReceive().TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task EnsureFundedSideEffects_SelfPickup_ReservesNothing()
    {
        var actual = await Committing().EnsureFundedSideEffectsAsync(FundedOrder("500"), "system", null, _context);

        Assert.Null(actual.Trip);
        await _tripRepo.DidNotReceive().TryReserveCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }
}

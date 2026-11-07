using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-17 (design 4.10, edge case G4): the <c>commitment_deadlines</c> run closes the open trips that
/// left with no booking and no live order (<c>cancelled</c>, <c>cancelReason: expired</c>). The trips
/// table is an in-memory fake whose close honours the same condition as the repository write
/// (still open, version unchanged, no booking), so a re-run and a funding racing the close behave
/// as they would against DynamoDB.
/// </summary>
public class OrderCronDepartedTripTests
{
    private const string TripId = "trip-1";
    private const string CreatedAt = "2026-10-20T08:00:00.000Z";

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly TestLambdaContext _context = new();

    /// <summary>The trips table: every row, whatever its status.</summary>
    private readonly List<Dictionary<string, AttributeValue>> _rows = new();
    private readonly List<string> _cutoffsRead = new();

    /// <summary>Runs between the cron's read and its close (a race), once.</summary>
    private Action? _beforeClose;

    private bool _alwaysAnotherPage;

    public OrderCronDepartedTripTests()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new List<Dictionary<string, object>>());
        _orders.QueryByTripAsync(Arg.Any<string>()).Returns(new List<Dictionary<string, object>>());

        _trips.QueryDepartedOpenAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(ci =>
            {
                var cutoff = ci.ArgAt<string>(0);
                _cutoffsRead.Add(cutoff);
                var page = _rows
                    .Where(r => r["status"].S == "open" && string.CompareOrdinal(r["departureDate"].S, cutoff) < 0)
                    .Select(r => new Dictionary<string, AttributeValue>(r))
                    .ToList();
                Dictionary<string, AttributeValue>? next = _alwaysAnotherPage
                    ? new Dictionary<string, AttributeValue> { { "tripId", new AttributeValue { S = "more" } } }
                    : null;
                return (page, next);
            });

        _trips.TryExpireUnbookedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var race = _beforeClose;
                _beforeClose = null;
                race?.Invoke();

                var row = _rows.Single(r => r["tripId"].S == ci.ArgAt<string>(0));
                var holds = row["status"].S == "open"
                    && row["version"].N == ci.ArgAt<int>(2).ToString(CultureInfo.InvariantCulture)
                    && string.CompareOrdinal(row["departureDate"].S, ci.ArgAt<string>(3)) < 0
                    && !row.ContainsKey("bookedOrderIds")
                    && !row.ContainsKey("deletedAt");
                if (!holds)
                {
                    return false;
                }

                row["status"] = S("cancelled");
                row["cancelReason"] = S(TripLifecycle.ExpiredCancelReason);
                row["version"] = N(ci.ArgAt<int>(2) + 1);
                return true;
            });
    }

    // ===== fixture =====

    private OrderCronFunction Sut(bool enabled = true)
        => new(_orders, transactionAuditRepo: null, inboxRepo: null,
            escrowService: new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
                orderRepo: _orders),
            cropRepo: _crops,
            tripRepo: _trips)
        {
            CommitmentsEnabled = enabled
        };

    private async Task<Dictionary<string, string>> RunAsync(bool enabled = true)
    {
        var response = await Sut(enabled).FunctionHandler(
            new APIGatewayProxyRequest { HttpMethod = "POST", Body = "{\"action\":\"commitment_deadlines\"}" }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Ethiopia midnight <paramref name="days"/> days from today's, in the stored form.</summary>
    private static string EthiopiaDay(int days)
        => TripDeparture.StartOfEthiopiaDayUtc(DateTime.UtcNow).AddDays(days)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private Dictionary<string, AttributeValue> AddTrip(
        string tripId = TripId, int departureDay = -2, int version = 3, string[]? bookedIds = null, double booked = 0)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "tripId", S(tripId) },
            { "createdAt", S(CreatedAt) },
            { "truckerId", S("trucker-1") },
            { "status", S("open") },
            { "departureDate", S(EthiopiaDay(departureDay)) },
            { "capacity", new AttributeValue { N = "100" } },
            { "version", N(version) }
        };
        if (bookedIds != null)
        {
            row["bookedOrderIds"] = new AttributeValue { SS = bookedIds.ToList() };
        }

        if (booked > 0)
        {
            row["bookedQuintals"] = new AttributeValue { N = booked.ToString(CultureInfo.InvariantCulture) };
        }

        _rows.Add(row);
        return row;
    }

    private void OrdersOnTrip(params string[] statuses)
        => _orders.QueryByTripAsync(TripId).Returns(statuses
            .Select((status, i) => new Dictionary<string, object>
            {
                { "orderId", $"order-{i}" }, { "tripId", TripId }, { "status", status }
            })
            .ToList());

    // ===== closing =====

    [Fact]
    public async Task UnbookedTripThatLeft_NoOrders_IsClosedAsExpired()
    {
        var trip = AddTrip();
        var cutoffBefore = TripDeparture.CutoffIso(DateTime.UtcNow);

        var summary = await RunAsync();

        var cutoffAfter = TripDeparture.CutoffIso(DateTime.UtcNow);
        summary["departedTripsClosed"].Should().Be("1");
        summary["departedTripsKept"].Should().Be("0");
        trip["status"].S.Should().Be("cancelled");
        trip["cancelReason"].S.Should().Be("expired");
        trip["version"].N.Should().Be("4");
        // The browse cutoff (start of today in Ethiopia), read 100 rows a page.
        _cutoffsRead.Should().ContainSingle().Which.Should().BeOneOf(cutoffBefore, cutoffAfter);
        await _trips.Received(1).QueryDepartedOpenAsync(Arg.Any<string>(), OrderCronFunction.DepartedTripPageSize, null);
        await _trips.Received(1).TryExpireUnbookedAsync(TripId, CreatedAt, 3, _cutoffsRead[0], Arg.Any<string>());
        Logs.Should().Contain("open -> cancelled (expired: left unbooked)");
    }

    [Fact]
    public async Task ReRun_ClosesNothingMore()
    {
        AddTrip();

        var first = await RunAsync();
        var second = await RunAsync();

        first["departedTripsClosed"].Should().Be("1");
        second["departedTripsClosed"].Should().Be("0");
        second["conflicts"].Should().Be("0");
        await _trips.Received(1).TryExpireUnbookedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FundingLandsBetweenTheReadAndTheClose_TheTripStaysOpen()
    {
        // A buyer's money reserves the trip after the cron read it: the booking write does not bump the
        // version, so only the no-booking condition of the close stops it.
        var trip = AddTrip();
        _beforeClose = () => trip["bookedOrderIds"] = new AttributeValue { SS = ["order-late"] };

        var summary = await RunAsync();

        summary["departedTripsClosed"].Should().Be("0");
        summary["conflicts"].Should().Be("1");
        trip["status"].S.Should().Be("open");
        Logs.Should().Contain("changed since it was read; not closed this run");
    }

    [Fact]
    public async Task TruckerEditsTheTripBetweenTheReadAndTheClose_TheEditWins()
    {
        // The trucker moves the date ahead: a new version, and the version condition refuses the close.
        var trip = AddTrip();
        _beforeClose = () =>
        {
            trip["version"] = N(4);
            trip["departureDate"] = S(EthiopiaDay(3));
        };

        var summary = await RunAsync();

        summary["conflicts"].Should().Be("1");
        trip["status"].S.Should().Be("open");
    }

    [Fact]
    public async Task OnlyClosedOrTransportFailedOrders_TheTripCloses()
    {
        AddTrip();
        OrdersOnTrip("cancelled", "rejected", "payment_failed", OrderCommitment.AwaitingTransportStatus);

        var summary = await RunAsync();

        summary["departedTripsClosed"].Should().Be("1");
    }

    // ===== keeping =====

    [Fact]
    public async Task BookedTripThatLeft_IsKept_ItsOrdersNotEvenRead()
    {
        var trip = AddTrip(bookedIds: ["order-1"], booked: 5);

        var summary = await RunAsync();

        summary["departedTripsKept"].Should().Be("1");
        summary["departedTripsClosed"].Should().Be("0");
        trip["status"].S.Should().Be("open");
        await _orders.DidNotReceive().QueryByTripAsync(Arg.Any<string>());
        await _trips.DidNotReceiveWithAnyArgs().TryExpireUnbookedAsync(default!, default!, default, default!, default!);
    }

    [Fact]
    public async Task BookedQuintalsWithoutTheSet_StillCountsAsBooked()
    {
        AddTrip(booked: 2.5);

        var summary = await RunAsync();

        summary["departedTripsKept"].Should().Be("1");
    }

    [Theory]
    [InlineData("created")]
    [InlineData("payment_submitted")]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("settled")]
    public async Task AnOrderStillOnTheTrip_KeepsIt_EvenWithoutABooking(string orderStatus)
    {
        // Bookings exist only since ESC-15 with the flag on: an order accepted before it, one still
        // being paid, or one the truck already carried keeps the trip as it is.
        var trip = AddTrip();
        OrdersOnTrip("cancelled", orderStatus);

        var summary = await RunAsync();

        summary["departedTripsKept"].Should().Be("1");
        trip["status"].S.Should().Be("open");
        await _trips.DidNotReceiveWithAnyArgs().TryExpireUnbookedAsync(default!, default!, default, default!, default!);
    }

    [Fact]
    public async Task TripLeavingToday_IsNeverClosed()
    {
        // Trucks run late: a trip stays open all of its day (L-7). Only a row the index should not
        // have returned would reach here; the date is checked again.
        var trip = AddTrip(departureDay: 0);
        _trips.QueryDepartedOpenAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, AttributeValue>> { trip }, (Dictionary<string, AttributeValue>?)null));

        var summary = await RunAsync();

        summary["departedTripsClosed"].Should().Be("0");
        await _trips.DidNotReceiveWithAnyArgs().TryExpireUnbookedAsync(default!, default!, default, default!, default!);
    }

    [Fact]
    public async Task UnreadableOrders_TheTripWaitsARun()
    {
        AddTrip();
        _orders.QueryByTripAsync(TripId).ThrowsAsync(new InvalidOperationException("throttled"));

        var summary = await RunAsync();

        summary["outcome"].Should().Be("ok");
        summary["departedTripsClosed"].Should().Be("0");
        Logs.Should().Contain("could not be read [InvalidOperationException]; it waits a run");
    }

    [Fact]
    public async Task UnreadableIndex_TheRunStillEndsOk()
    {
        _trips.QueryDepartedOpenAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new InvalidOperationException("throttled"));

        var summary = await RunAsync();

        summary["outcome"].Should().Be("ok");
        Logs.Should().Contain("departed trips could not be read [InvalidOperationException]");
    }

    // ===== bounds =====

    [Fact]
    public async Task PagesAreBoundedPerRun()
    {
        _alwaysAnotherPage = true;

        await RunAsync();

        _cutoffsRead.Should().HaveCount(OrderCronFunction.MaxDepartedTripPages);
        Logs.Should().Contain($"{OrderCronFunction.MaxDepartedTripPages} pages of departed trips read; the rest wait a run");
    }

    [Fact]
    public async Task ClosesAreBoundedPerRun_TheRestNextRun()
    {
        for (var i = 0; i <= OrderCronFunction.MaxDepartedTripCloses; i++)
        {
            AddTrip(tripId: $"trip-{i}");
        }

        var first = await RunAsync();
        var second = await RunAsync();

        first["departedTripsClosed"].Should().Be(OrderCronFunction.MaxDepartedTripCloses.ToString(CultureInfo.InvariantCulture));
        second["departedTripsClosed"].Should().Be("1");
        _rows.Should().OnlyContain(r => r["status"].S == "cancelled");
    }

    [Fact]
    public async Task FlagOff_ReadsNoTrip()
    {
        AddTrip();

        var summary = await RunAsync(enabled: false);

        summary["outcome"].Should().Be("disabled");
        await _trips.DidNotReceiveWithAnyArgs().QueryDepartedOpenAsync(default!, default, default);
        _rows.Single()["status"].S.Should().Be("open");
    }
}
